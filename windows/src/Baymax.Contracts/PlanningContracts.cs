using System.Text.Json;

namespace Baymax.Contracts;

public sealed record CreatePlanRequest(
    string Instruction,
    IReadOnlyList<ConversationMessage>? History = null);

public sealed record ConversationMessage(string Role, string Content);

public sealed record PlannedAction(
    Guid ActionId,
    int Ordinal,
    string Name,
    JsonElement Arguments);

public sealed record TaskPlanResponse(
    string Summary,
    bool NeedsClarification,
    string ClarifyingQuestion,
    string Model,
    IReadOnlyList<PlannedAction> Actions,
    long? PromptTokens,
    long? CompletionTokens,
    double DurationMilliseconds,
    double? TokensPerSecond);

public sealed record ActionDefinitionResponse(
    string Name,
    string Description,
    JsonElement ArgumentsSchema,
    string ExpectedResult,
    IReadOnlyList<string> FailureCodes);

public sealed record CreatePlanApprovalRequest(
    TaskPlanResponse Plan,
    bool ConfirmApproval);

public sealed record PlanApprovalResponse(
    Guid ApprovalId,
    string PlanHash,
    DateTimeOffset ExpiresAt,
    string ApprovedBy);

public sealed record ExecutePlanRequest(
    TaskPlanResponse Plan,
    Guid ApprovalId);

public sealed record ActionExecutionResult(
    Guid ActionId,
    int Ordinal,
    string Name,
    string Status,
    string? FailureCode,
    string? Error,
    JsonElement? Result);

public sealed record PlanExecutionResponse(
    string Status,
    IReadOnlyList<ActionExecutionResult> Actions,
    DateTimeOffset CompletedAt);
