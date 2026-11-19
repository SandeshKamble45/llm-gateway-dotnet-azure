namespace LlmGateway.Domain;

public enum IdempotencyRequestState
{
    Acquired = 1,
    PayloadMismatch = 2,
    InProgress = 3,
    Completed = 4
}

public sealed record IdempotencyStartResult(
    IdempotencyRequestState State,
    int StatusCode = 200,
    string ContentType = "application/json; charset=utf-8",
    string ResponseBody = "");
