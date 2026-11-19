using LlmGateway.Domain;

namespace LlmGateway.Application.Abstractions;

public interface IIdempotencyStore
{
    Task<IdempotencyStartResult> BeginAsync(
        string tenantId,
        string idempotencyKey,
        string requestFingerprint,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        string tenantId,
        string idempotencyKey,
        string requestFingerprint,
        int statusCode,
        string contentType,
        string responseBody,
        CancellationToken cancellationToken);

    Task ReleaseAsync(
        string tenantId,
        string idempotencyKey,
        string requestFingerprint,
        CancellationToken cancellationToken);
}
