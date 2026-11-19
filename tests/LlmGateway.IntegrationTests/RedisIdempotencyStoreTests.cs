using LlmGateway.Application.Configuration;
using LlmGateway.Domain;
using LlmGateway.Infrastructure;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace LlmGateway.IntegrationTests;

/// <summary>
/// Verifies idempotency state transitions against a real Redis instance.
/// </summary>
public sealed class RedisIdempotencyStoreTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();
    private IConnectionMultiplexer? _connection;
    private RedisIdempotencyStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await _redis.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        _store = new RedisIdempotencyStore(_connection, Options.Create(new GatewayOptions
        {
            IdempotencyKeyTtlSeconds = 3_600
        }));
    }

    public async ValueTask DisposeAsync()
    {
        _connection?.Dispose();
        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task FirstRequestAcquiresKeyAndConcurrentDuplicateIsInProgress()
    {
        var first = await _store.BeginAsync("tenant-a", "request-1", "fingerprint-a", CancellationToken.None);
        var duplicate = await _store.BeginAsync("tenant-a", "request-1", "fingerprint-a", CancellationToken.None);

        Assert.Equal(IdempotencyRequestState.Acquired, first.State);
        Assert.Equal(IdempotencyRequestState.InProgress, duplicate.State);
    }

    [Fact]
    public async Task SameKeyWithDifferentRequestFingerprintIsRejected()
    {
        await _store.BeginAsync("tenant-a", "request-2", "fingerprint-a", CancellationToken.None);

        var mismatch = await _store.BeginAsync("tenant-a", "request-2", "fingerprint-b", CancellationToken.None);

        Assert.Equal(IdempotencyRequestState.PayloadMismatch, mismatch.State);
    }

    [Fact]
    public async Task CompletedRequestReplaysOriginalStatusContentTypeAndBody()
    {
        const string body = "{\"response\":{\"completion\":\"persisted result\"}}";
        await _store.BeginAsync("tenant-a", "request-3", "fingerprint-a", CancellationToken.None);

        await _store.CompleteAsync(
            "tenant-a",
            "request-3",
            "fingerprint-a",
            200,
            "application/json; charset=utf-8",
            body,
            CancellationToken.None);

        var replay = await _store.BeginAsync("tenant-a", "request-3", "fingerprint-a", CancellationToken.None);

        Assert.Equal(IdempotencyRequestState.Completed, replay.State);
        Assert.Equal(200, replay.StatusCode);
        Assert.Equal("application/json; charset=utf-8", replay.ContentType);
        Assert.Equal(body, replay.ResponseBody);
    }

    [Fact]
    public async Task ReleasingOwnedInProgressKeyAllowsSafeRetry()
    {
        await _store.BeginAsync("tenant-a", "request-4", "fingerprint-a", CancellationToken.None);

        await _store.ReleaseAsync("tenant-a", "request-4", "fingerprint-a", CancellationToken.None);

        var retry = await _store.BeginAsync("tenant-a", "request-4", "fingerprint-a", CancellationToken.None);
        Assert.Equal(IdempotencyRequestState.Acquired, retry.State);
    }

    [Fact]
    public async Task ConcurrentBeginsHaveExactlyOneOwner()
    {
        var starts = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => _store.BeginAsync("tenant-a", "request-5", "fingerprint-a", CancellationToken.None)));

        Assert.Single(starts, x => x.State == IdempotencyRequestState.Acquired);
        Assert.Equal(19, starts.Count(x => x.State == IdempotencyRequestState.InProgress));
    }

    [Fact]
    public async Task IdempotencyKeysAreIsolatedByTenant()
    {
        var tenantA = await _store.BeginAsync("tenant-a", "same-key", "same-fingerprint", CancellationToken.None);
        var tenantB = await _store.BeginAsync("tenant-b", "same-key", "same-fingerprint", CancellationToken.None);

        Assert.Equal(IdempotencyRequestState.Acquired, tenantA.State);
        Assert.Equal(IdempotencyRequestState.Acquired, tenantB.State);
    }

    [Fact]
    public async Task OnlyOwnerCanCompleteRecord()
    {
        await _store.BeginAsync("tenant-a", "request-6", "fingerprint-a", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CompleteAsync(
            "tenant-a",
            "request-6",
            "different-fingerprint",
            200,
            "application/json",
            "{}",
            CancellationToken.None));
    }
}
