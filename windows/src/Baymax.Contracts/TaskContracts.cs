namespace Baymax.Contracts;

public enum TaskState
{
    Received,
    Understanding,
    Planning,
    Validating,
    WaitingApproval,
    Queued,
    Running,
    Verifying,
    Completed,
    Partial,
    Failed,
    Cancelled,
    Denied
}

public sealed record SubmitTaskRequest(
    string UserInstruction,
    string IdempotencyKey,
    string? OriginDeviceId = null);

public sealed record TaskStepResponse(
    Guid StepId,
    int Ordinal,
    string Name,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record TaskEventResponse(
    Guid EventId,
    int Sequence,
    string EventType,
    string PayloadJson,
    DateTimeOffset OccurredAt);

public sealed record TaskResponse(
    Guid TaskId,
    string UserInstruction,
    string? OriginDeviceId,
    TaskState Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int CurrentStep,
    IReadOnlyList<TaskStepResponse> Steps,
    IReadOnlyList<TaskEventResponse> Events);
