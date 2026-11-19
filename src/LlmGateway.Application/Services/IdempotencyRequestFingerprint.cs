using System.Security.Cryptography;
using System.Text.Json;
using LlmGateway.Domain;

namespace LlmGateway.Application.Services;

public static class IdempotencyRequestFingerprint
{
    public static string Create(ChatRequest request, string? demoAccessCode)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new FingerprintPayload(
            TenantId.Normalize(request.TenantId),
            request.Prompt,
            string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
            demoAccessCode));

        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private sealed record FingerprintPayload(
        string TenantId,
        string? Prompt,
        string? Model,
        string? DemoAccessCode);
}
