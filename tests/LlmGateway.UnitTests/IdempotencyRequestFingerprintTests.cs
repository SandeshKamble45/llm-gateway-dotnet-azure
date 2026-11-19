using LlmGateway.Application.Services;
using LlmGateway.Domain;
using Xunit;

namespace LlmGateway.UnitTests;

public sealed class IdempotencyRequestFingerprintTests
{
    [Fact]
    public void FingerprintIsStableForEquivalentRequest()
    {
        var request = new ChatRequest("tenant-a", "hello", "primary-model");
        var sameRequest = new ChatRequest(" tenant-a ", "hello", " primary-model ");

        Assert.Equal(
            IdempotencyRequestFingerprint.Create(request, null),
            IdempotencyRequestFingerprint.Create(sameRequest, null));
    }

    [Fact]
    public void FingerprintChangesWhenPromptChanges()
    {
        var original = new ChatRequest("tenant-a", "hello", "primary-model");
        var changed = new ChatRequest("tenant-a", "different prompt", "primary-model");

        Assert.NotEqual(
            IdempotencyRequestFingerprint.Create(original, null),
            IdempotencyRequestFingerprint.Create(changed, null));
    }

    [Fact]
    public void FingerprintIncludesModelAndDemoAccessCode()
    {
        var request = new ChatRequest("tenant-a", "hello", "gpt-5-mini");

        var withCode = IdempotencyRequestFingerprint.Create(request, "code-a");
        var withDifferentCode = IdempotencyRequestFingerprint.Create(request, "code-b");

        Assert.NotEqual(withCode, withDifferentCode);
    }

    [Fact]
    public void FingerprintIsTenantScoped()
    {
        var tenantA = new ChatRequest("tenant-a", "hello", "primary-model");
        var tenantB = new ChatRequest("tenant-b", "hello", "primary-model");

        Assert.NotEqual(
            IdempotencyRequestFingerprint.Create(tenantA, null),
            IdempotencyRequestFingerprint.Create(tenantB, null));
    }
}
