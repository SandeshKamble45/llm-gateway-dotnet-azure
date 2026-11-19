using System.Text.Json;
using LlmGateway.Application.Abstractions;
using LlmGateway.Application.Configuration;
using LlmGateway.Application.Services;
using LlmGateway.Domain;
using LlmGateway.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LlmGateway.Api.Controllers;

[ApiController]
[Route("v1/gateway")]
public sealed class GatewayController(
    GatewayService gateway,
    IGatewayStateStore stateStore,
    IIdempotencyStore idempotencyStore,
    ProviderResiliencePipeline providerResilience,
    IOptions<AuthenticationOptions> authenticationOptions,
    IOptions<JsonOptions> jsonOptions) : ControllerBase
{
    private const string JsonContentType = "application/json; charset=utf-8";

    [HttpPost("chat")]
    [ProducesResponseType(typeof(GatewayResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(RateLimitResult), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Chat(
        [FromBody] ChatRequest? request,
        [FromHeader(Name = "X-Demo-Access-Code")] string? demoAccessCode,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest("Request body is required");
        if (!IsTenantAuthorized(request.TenantId))
            return Forbid();

        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (key is not null && (key.Length > 128 || key.Any(char.IsControl)))
            return BadRequest("Idempotency-Key must contain 1–128 visible characters.");

        string tenantId;
        try
        {
            // Preflight happens before a key is acquired and before any quota state is changed.
            tenantId = gateway.ValidateRequest(request, demoAccessCode);
        }
        catch (GatewayException exception)
        {
            return StatusCode(exception.StatusCode, exception.Payload ?? exception.Message);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }

        if (key is null)
        {
            try
            {
                var response = await gateway.ProcessAsync(request, demoAccessCode, null, cancellationToken);
                return Ok(response);
            }
            catch (GatewayException exception)
            {
                return StatusCode(exception.StatusCode, exception.Payload ?? exception.Message);
            }
            catch (ArgumentException exception)
            {
                return BadRequest(exception.Message);
            }
        }

        var fingerprint = IdempotencyRequestFingerprint.Create(request, demoAccessCode);
        var start = await idempotencyStore.BeginAsync(tenantId, key, fingerprint, cancellationToken);

        switch (start.State)
        {
            case IdempotencyRequestState.PayloadMismatch:
                return Conflict("Idempotency-Key was already used with a different request payload.");
            case IdempotencyRequestState.InProgress:
                return Conflict("A request with this Idempotency-Key is still in progress. Do not submit it with a new key.");
            case IdempotencyRequestState.Completed:
                Response.Headers["Idempotency-Replayed"] = "true";
                return new ContentResult
                {
                    Content = start.ResponseBody,
                    ContentType = start.ContentType,
                    StatusCode = start.StatusCode
                };
            case IdempotencyRequestState.Acquired:
                break;
            default:
                throw new InvalidOperationException($"Unsupported idempotency state: {start.State}.");
        }

        try
        {
            var response = await gateway.ProcessAsync(request, demoAccessCode, key, cancellationToken);
            return await CompleteAndReturnAsync(
                tenantId, key, fingerprint, StatusCodes.Status200OK, response);
        }
        catch (GatewayException exception)
        {
            // Cache deterministic admission/provider outcomes too: they may follow a consumed
            // sliding-window or token-bucket admission, or a provider operation that already ran.
            return await CompleteAndReturnAsync(
                tenantId,
                key,
                fingerprint,
                exception.StatusCode,
                exception.Payload ?? exception.Message);
        }
        catch (ArgumentException exception)
        {
            return await CompleteAndReturnAsync(
                tenantId, key, fingerprint, StatusCodes.Status400BadRequest, exception.Message);
        }
        // Do not release on cancellation or unexpected errors after acquisition. The provider may
        // have received the request; leaving the record in_progress is safer than duplicating a charge.
    }

    private async Task<IActionResult> CompleteAndReturnAsync(
        string tenantId,
        string key,
        string fingerprint,
        int statusCode,
        object payload)
    {
        var body = JsonSerializer.Serialize(payload, jsonOptions.Value.JsonSerializerOptions);
        await idempotencyStore.CompleteAsync(
            tenantId, key, fingerprint, statusCode, JsonContentType, body, CancellationToken.None);

        return new ContentResult
        {
            Content = body,
            ContentType = JsonContentType,
            StatusCode = statusCode
        };
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(GatewayStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Status([FromQuery] string? tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest("tenantId is required");
        if (!IsTenantAuthorized(tenantId))
            return Forbid();

        try
        {
            var response = await gateway.GetStatusAsync(
                tenantId, providerResilience.GetStatus(), cancellationToken);
            return Ok(response);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    [HttpPost("check/token-bucket")]
    public async Task<IActionResult> CheckTokenBucket(
        [FromQuery] string? tenantId,
        [FromQuery] long cost = 1,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest("tenantId is required");
        if (!IsTenantAuthorized(tenantId))
            return Forbid();
        try
        {
            var normalized = TenantId.Normalize(tenantId);
            var result = await stateStore.ConsumeTokenBucketAsync(
                normalized, "primary-model", cost, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException exception) { return BadRequest(exception.Message); }
    }

    private bool IsTenantAuthorized(string? tenantId)
    {
        var settings = authenticationOptions.Value;
        if (!settings.Enabled)
            return true;

        if (string.IsNullOrWhiteSpace(tenantId))
            return false;

        var claim = User.FindFirst(settings.TenantClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(claim))
            return false;

        try
        {
            return string.Equals(
                TenantId.Normalize(claim),
                TenantId.Normalize(tenantId),
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [HttpPost("check/sliding-window")]
    public async Task<IActionResult> CheckSlidingWindow(
        [FromQuery] string? tenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest("tenantId is required");
        if (!IsTenantAuthorized(tenantId))
            return Forbid();
        try
        {
            var normalized = TenantId.Normalize(tenantId);
            var result = await stateStore.ConsumeSlidingWindowAsync(normalized, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException exception) { return BadRequest(exception.Message); }
    }
}
