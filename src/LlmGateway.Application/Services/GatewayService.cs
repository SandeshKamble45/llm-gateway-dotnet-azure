using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LlmGateway.Application.Abstractions;
using LlmGateway.Application.Configuration;
using LlmGateway.Domain;
using Microsoft.Extensions.Options;

namespace LlmGateway.Application.Services;

public sealed class GatewayService
{
    private readonly IGatewayStateStore _state;
    private readonly ILlmProvider _provider;
    private readonly ITokenCounter _tokenCounter;
    private readonly IModelPricingService _pricing;
    private readonly GatewayOptions _options;
    private readonly string? _demoAccessCode;
    private readonly GatewayMetrics _metrics;

    public GatewayService(
        IGatewayStateStore state,
        ILlmProvider provider,
        ITokenCounter tokenCounter,
        IModelPricingService pricing,
        IOptions<GatewayOptions> options,
        GatewayMetrics metrics)
    {
        _state = state;
        _provider = provider;
        _tokenCounter = tokenCounter;
        _pricing = pricing;
        _options = options.Value;
        _demoAccessCode = _options.DemoAccessCode;
        _metrics = metrics;
    }

    /// <summary>
    /// Runs all deterministic request validation before any limiter or budget state is mutated.
    /// The controller calls this before acquiring an idempotency key so invalid requests do not
    /// leave a key locked or consume quota.
    /// </summary>
    public string ValidateRequest(ChatRequest request, string? demoAccessCode)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TenantId))
            throw new GatewayException(400, "tenantId is required");
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new GatewayException(400, "prompt is required");
        if (request.Prompt.Length > _options.MaxPromptLength)
            throw new GatewayException(400, $"prompt cannot exceed {_options.MaxPromptLength} characters");

        string tenantId;
        try { tenantId = TenantId.Normalize(request.TenantId); }
        catch (ArgumentException ex) { throw new GatewayException(400, ex.Message); }

        var model = ResolveModel(request);
        if (!string.Equals(model, _provider.PrimaryModel, StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(_demoAccessCode) ||
             !FixedTimeEquals(_demoAccessCode, demoAccessCode)))
            throw new GatewayException(403, "Invalid or missing demo access code");

        var estimatedInputTokens = Math.Max(1, _tokenCounter.CountTokens(request.Prompt));
        if (estimatedInputTokens > _options.TokenBucketCapacity)
            throw new GatewayException(400, "Estimated prompt tokens exceed the configured token-bucket capacity.");

        try { _ = _pricing.GetPricing(model); }
        catch (ArgumentException ex) { throw new GatewayException(400, ex.Message); }

        return tenantId;
    }

    public async Task<GatewayResponse> ProcessAsync(
        ChatRequest request,
        string? demoAccessCode,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var tenantId = ValidateRequest(request, demoAccessCode);
        var model = ResolveModel(request);
        var estimatedInputTokens = Math.Max(1, _tokenCounter.CountTokens(request.Prompt));

        _metrics.RecordRequest();

        // Request frequency, token capacity and monetary budget remain independent admission controls.
        var requestLimit = await _state.ConsumeSlidingWindowAsync(tenantId, cancellationToken);
        if (!requestLimit.Allowed)
        {
            _metrics.RecordRejectedRequest("sliding-window");
            throw new GatewayException(429, "Request rate limit exceeded", requestLimit);
        }

        var tokenLimit = await _state.ConsumeTokenBucketAsync(
            tenantId, model, estimatedInputTokens, cancellationToken);
        if (!tokenLimit.Allowed)
        {
            _metrics.RecordRejectedRequest("token-bucket");
            throw new GatewayException(429, "Token capacity exhausted", tokenLimit);
        }

        var pricing = _pricing.GetPricing(model);
        var estimatedReservation = checked(
            pricing.CalculateInputCost(estimatedInputTokens) +
            pricing.CalculateOutputCost(_options.MaxOutputTokens));

        var reservationId = BuildReservationId(idempotencyKey);
        var reservation = await _state.ReserveBudgetAsync(
            tenantId, reservationId, estimatedReservation, cancellationToken);
        if (!reservation.Allowed)
        {
            if (reservation.InProgress || reservation.AlreadySettled)
                throw new GatewayException(409, "Budget reservation already exists for this operation.", reservation);
            throw new GatewayException(402, "Daily tenant budget exceeded", reservation);
        }

        var timer = Stopwatch.StartNew();
        LlmResponse response;
        _metrics.RecordProviderCall();
        try
        {
            response = await _provider.CompleteAsync(model, request.Prompt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A cancelled HTTP exchange can still have reached the provider. Keep both the budget
            // reservation and idempotency record locked; a retry must not create a second billable call.
            throw;
        }
        catch (Exception)
        {
            timer.Stop();
            _metrics.RecordProviderFailure();
            _metrics.RecordProviderLatency(timer.Elapsed.TotalMilliseconds);
            var settlement = await _state.SettleBudgetAsync(
                tenantId, reservationId, 0, CancellationToken.None);
            throw new GatewayException(
                502,
                "LLM provider unavailable",
                new GatewayErrorResponse("LLM provider unavailable", estimatedReservation, settlement));
        }
        finally
        {
            timer.Stop();
        }

        _metrics.RecordProviderLatency(timer.Elapsed.TotalMilliseconds);

        ModelPricing actualPricing;
        try { actualPricing = _pricing.GetPricing(response.ModelUsed); }
        catch (ArgumentException)
        {
            // Do not silently account usage at an unknown price. The reservation remains held.
            throw new GatewayException(502, $"Provider returned an unsupported model: {response.ModelUsed}");
        }

        var actualCost = checked(
            actualPricing.CalculateInputCost(response.Usage.InputTokens) +
            actualPricing.CalculateOutputCost(response.Usage.OutputTokens));

        _metrics.RecordTokens(response.Usage.TotalTokens);
        _metrics.RecordActualCost(actualCost);

        var finalSettlement = await _state.SettleBudgetAsync(
            tenantId, reservationId, actualCost, cancellationToken);

        return new GatewayResponse(
            response,
            estimatedReservation,
            actualCost,
            finalSettlement,
            timer.ElapsedMilliseconds);
    }

    public async Task<GatewayStatusResponse> GetStatusAsync(string tenantId, CircuitBreakerStatus circuitBreaker, CancellationToken cancellationToken)
    {
        var normalized = TenantId.Normalize(tenantId);
        var primaryModel = _provider.PrimaryModel;
        var tokenStatus = await _state.GetTokenBucketStatusAsync(normalized, primaryModel, cancellationToken);
        var windowStatus = await _state.GetSlidingWindowStatusAsync(normalized, cancellationToken);
        var budgetStatus = await _state.GetBudgetStatusAsync(normalized, cancellationToken);
        return new GatewayStatusResponse(normalized, tokenStatus, windowStatus, budgetStatus, circuitBreaker);
    }

    private string ResolveModel(ChatRequest request) =>
        string.IsNullOrWhiteSpace(request.Model) ? _provider.PrimaryModel : request.Model.Trim();

    private static string BuildReservationId(string? idempotencyKey)
    {
        var date = DateTime.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey.Trim())));
        return $"{date}-{suffix}";
    }

    private static bool FixedTimeEquals(string expected, string? supplied)
    {
        if (string.IsNullOrEmpty(supplied)) return false;
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(supplied);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
