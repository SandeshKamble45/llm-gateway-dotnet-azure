using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LlmGateway.Application.Abstractions;
using LlmGateway.Application.Configuration;
using LlmGateway.Domain;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace LlmGateway.Infrastructure;

public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly IDatabase _database;
    private readonly GatewayOptions _options;
    private readonly IReadOnlyDictionary<string, string> _scripts;

    public RedisIdempotencyStore(
        IConnectionMultiplexer connection,
        IOptions<GatewayOptions> options)
    {
        _database = connection.GetDatabase();
        _options = options.Value;
        _scripts = LoadScripts();
    }

    public async Task<IdempotencyStartResult> BeginAsync(
        string tenantId,
        string idempotencyKey,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        ValidateArguments(tenantId, idempotencyKey, requestFingerprint);

        var result = await _database.ScriptEvaluateAsync(
            _scripts["idempotency_begin.lua"],
            [BuildKey(tenantId, idempotencyKey)],
            [requestFingerprint, _options.IdempotencyKeyTtlSeconds])
            .WaitAsync(cancellationToken);

        var values = (RedisResult[])result!;
        var state = (long)values[0];

        return state switch
        {
            1 => new IdempotencyStartResult(IdempotencyRequestState.Acquired),
            2 => new IdempotencyStartResult(IdempotencyRequestState.PayloadMismatch),
            3 => new IdempotencyStartResult(IdempotencyRequestState.InProgress),
            4 => new IdempotencyStartResult(
                IdempotencyRequestState.Completed,
                (int)(long)values[1],
                values[2].ToString(),
                values[3].ToString()),
            _ => throw new InvalidOperationException($"Unknown idempotency state returned by Redis: {state}.")
        };
    }

    public async Task CompleteAsync(
        string tenantId,
        string idempotencyKey,
        string requestFingerprint,
        int statusCode,
        string contentType,
        string responseBody,
        CancellationToken cancellationToken)
    {
        ValidateArguments(tenantId, idempotencyKey, requestFingerprint);
        if (statusCode is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(responseBody);

        var result = await _database.ScriptEvaluateAsync(
            _scripts["idempotency_complete.lua"],
            [BuildKey(tenantId, idempotencyKey)],
            [
                requestFingerprint,
                statusCode,
                contentType,
                responseBody,
                _options.IdempotencyKeyTtlSeconds
            ]).WaitAsync(cancellationToken);

        if ((long)result != 1)
            throw new InvalidOperationException("The idempotency record could not be completed because it is no longer owned by this request.");
    }

    public async Task ReleaseAsync(
        string tenantId,
        string idempotencyKey,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        ValidateArguments(tenantId, idempotencyKey, requestFingerprint);

        await _database.ScriptEvaluateAsync(
            _scripts["idempotency_release.lua"],
            [BuildKey(tenantId, idempotencyKey)],
            [requestFingerprint])
            .WaitAsync(cancellationToken);
    }

    private static RedisKey BuildKey(string tenantId, string idempotencyKey)
    {
        var tenant = TenantId.ToKeySegment(TenantId.Normalize(tenantId));
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)));
        return $"idempotency:chat:{{{tenant}}}:{keyHash}";
    }

    private static void ValidateArguments(string tenantId, string idempotencyKey, string requestFingerprint)
    {
        _ = TenantId.Normalize(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);
    }

    private static IReadOnlyDictionary<string, string> LoadScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in new[]
        {
            "idempotency_begin.lua",
            "idempotency_complete.lua",
            "idempotency_release.lua"
        })
        {
            var resource = assembly.GetManifestResourceNames()
                .SingleOrDefault(x => x.EndsWith($".{name}", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Embedded Redis script '{name}' was not found.");

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Unable to read Redis script '{name}'.");
            using var reader = new StreamReader(stream);
            map.Add(name, reader.ReadToEnd());
        }

        return map;
    }
}
