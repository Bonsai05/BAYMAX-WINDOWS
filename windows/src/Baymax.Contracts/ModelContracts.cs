using System.Text.Json;

namespace Baymax.Contracts;

public sealed record ModelMessage(string Role, string Content);

public sealed record ModelCompletionRequest(
    IReadOnlyList<ModelMessage> Messages,
    JsonElement? ResponseFormat = null,
    JsonElement? Tools = null,
    JsonElement? ToolChoice = null,
    int MaxTokens = 1024);

public sealed record ModelToolCall(
    string Id,
    string Type,
    string FunctionName,
    string ArgumentsJson);

public sealed record ModelCompletionResponse(
    string Model,
    string? Content,
    string FinishReason,
    IReadOnlyList<ModelToolCall> ToolCalls,
    long? PromptTokens,
    long? CompletionTokens,
    double DurationMilliseconds,
    double? TokensPerSecond);

public sealed record ModelRuntimeStatus(
    string Status,
    string Message,
    int? ProcessId,
    int RestartCount,
    string Endpoint);
