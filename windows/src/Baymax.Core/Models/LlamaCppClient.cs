using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baymax.Contracts;

namespace Baymax.Core.Models;

public sealed class ModelUnavailableException(string message) : Exception(message);

public sealed class LlamaCppClient
{
    private readonly LlamaCppOptions _options;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

    public LlamaCppClient(LlamaCppOptions options, HttpClient httpClient)
    {
        _options = options;
        _httpClient = httpClient;
    }

    public async Task<ModelCompletionResponse> CompleteAsync(
        ModelCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            throw new ModelUnavailableException("The local model runtime is not configured.");
        }

        ValidateRequest(request);
        await _inferenceLock.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                new Uri(_options.Endpoint, "v1/chat/completions"),
                new LlamaChatRequest(
                    "baymax-local",
                    request.Messages,
                    request.MaxTokens,
                    Stream: false,
                    ChatTemplateKwargs: new Dictionary<string, bool> { ["enable_thinking"] = false },
                    request.ResponseFormat,
                    request.Tools,
                    request.ToolChoice),
                cancellationToken);

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = responseBody.Length <= 512 ? responseBody : responseBody[..512];
                throw new HttpRequestException(
                    $"llama.cpp returned HTTP {(int)response.StatusCode}: {detail}",
                    inner: null,
                    response.StatusCode);
            }

            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("llama.cpp returned a completion that is not a JSON object.");
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw new InvalidDataException("llama.cpp returned no chat completion choices.");
            }

            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("llama.cpp returned a completion choice that is not an object.");
            }

            var message = GetRequiredObject(choice, "message", "llama.cpp choice");
            string? content = null;
            if (message.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind != JsonValueKind.Null)
            {
                if (contentElement.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("llama.cpp returned non-text message content.");
                }

                content = contentElement.GetString();
            }

            var toolCalls = ParseToolCalls(message);

            if (request.ResponseFormat is { } responseFormat)
            {
                if (content is null)
                {
                    throw new InvalidDataException("llama.cpp returned no content for a structured response.");
                }

                using var structuredContent = JsonDocument.Parse(content);
                var schema = JsonSchemaValidator.GetSchema(responseFormat);
                if (schema.ValueKind == JsonValueKind.Object)
                {
                    JsonSchemaValidator.Validate(schema, structuredContent.RootElement);
                }
            }

            stopwatch.Stop();
            var completionTokens = ReadUsage(root, "completion_tokens");
            var durationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return new ModelCompletionResponse(
                ReadRequiredString(root, "model", "llama.cpp completion"),
                content,
                ReadRequiredString(choice, "finish_reason", "llama.cpp choice"),
                toolCalls,
                ReadUsage(root, "prompt_tokens"),
                completionTokens,
                durationMilliseconds,
                completionTokens is > 0 && durationMilliseconds > 0
                    ? completionTokens.Value * 1000d / durationMilliseconds
                    : null);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private static IReadOnlyList<ModelToolCall> ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var toolCallsElement)
            || toolCallsElement.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("llama.cpp returned tool_calls in an invalid format.");
        }

        var toolCalls = new List<ModelToolCall>();
        foreach (var toolCall in toolCallsElement.EnumerateArray())
        {
            if (toolCall.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("llama.cpp returned a tool call that is not an object.");
            }

            var function = GetRequiredObject(toolCall, "function", "llama.cpp tool call");
            if (!function.TryGetProperty("arguments", out var arguments))
            {
                throw new InvalidDataException("A llama.cpp tool call has no arguments.");
            }

            if (arguments.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("A llama.cpp tool call has non-string arguments.");
            }

            using var parsedArguments = JsonDocument.Parse(arguments.GetString()!);
            if (parsedArguments.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("A llama.cpp tool call's arguments must be a JSON object.");
            }

            var callType = ReadRequiredString(toolCall, "type", "llama.cpp tool call");
            if (callType != "function")
            {
                throw new InvalidDataException($"llama.cpp returned unsupported tool-call type '{callType}'.");
            }

            toolCalls.Add(new ModelToolCall(
                ReadRequiredString(toolCall, "id", "llama.cpp tool call"),
                callType,
                ReadRequiredString(function, "name", "llama.cpp tool call function"),
                arguments.GetString()!));
        }

        if (toolCalls.Count > 16)
        {
            throw new InvalidDataException("llama.cpp returned more than 16 tool calls.");
        }

        return toolCalls;
    }

    private static long? ReadUsage(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return null;
        }

        if (usage.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("llama.cpp returned an invalid usage object.");
        }

        if (!usage.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result) || result < 0)
        {
            throw new InvalidDataException($"llama.cpp returned an invalid usage value for '{propertyName}'.");
        }

        return result;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, string context)
    {
        if (element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        throw new InvalidDataException($"llama.cpp returned no valid '{propertyName}' in its {context}.");
    }

    private static JsonElement GetRequiredObject(JsonElement element, string propertyName, string context)
    {
        if (element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        throw new InvalidDataException($"llama.cpp returned no valid '{propertyName}' object in its {context}.");
    }

    private static void ValidateRequest(ModelCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages is null || request.Messages.Count is < 1 or > 64)
        {
            throw new ArgumentException("A completion must contain between 1 and 64 messages.", nameof(request));
        }

        var totalCharacters = 0;
        foreach (var message in request.Messages)
        {
            if (message.Role is not ("system" or "user" or "assistant"))
            {
                throw new ArgumentException(
                    "Message roles must be system, user, or assistant.",
                    nameof(request));
            }

            if (string.IsNullOrWhiteSpace(message.Content) || message.Content.Length > 16_000)
            {
                throw new ArgumentException("Message content must contain 1 to 16000 characters.", nameof(request));
            }

            totalCharacters += message.Content.Length;
        }

        if (totalCharacters > 64_000)
        {
            throw new ArgumentException("The total message content must not exceed 64000 characters.", nameof(request));
        }

        if (request.MaxTokens is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "MaxTokens must be from 1 to 4096.");
        }

        if (request.ResponseFormat is { ValueKind: not JsonValueKind.Object })
        {
            throw new ArgumentException("ResponseFormat must be a JSON object.", nameof(request));
        }

        if (request.ResponseFormat is { } responseFormat)
        {
            if (responseFormat.GetRawText().Length > 16_000)
            {
                throw new ArgumentException("ResponseFormat must not exceed 16000 characters.", nameof(request));
            }

            _ = JsonSchemaValidator.GetSchema(responseFormat);
        }

        if (request.Tools is { ValueKind: not JsonValueKind.Array })
        {
            throw new ArgumentException("Tools must be a JSON array.", nameof(request));
        }

        if (request.Tools is { } tools && tools.GetArrayLength() > 16)
        {
            throw new ArgumentException("No more than 16 tools may be provided.", nameof(request));
        }

        if (request.Tools is { } toolDefinitions && toolDefinitions.GetRawText().Length > 32_000)
        {
            throw new ArgumentException("Tools must not exceed 32000 characters.", nameof(request));
        }

        if (request.ToolChoice is { ValueKind: not JsonValueKind.String and not JsonValueKind.Object })
        {
            throw new ArgumentException("ToolChoice must be a string or JSON object.", nameof(request));
        }

        if (request.ToolChoice is not null && request.Tools is null)
        {
            throw new ArgumentException("ToolChoice cannot be set without tool definitions.", nameof(request));
        }
    }

    private sealed record LlamaChatRequest(
        [property: JsonPropertyName("model")]
        string Model,
        [property: JsonPropertyName("messages")]
        IReadOnlyList<ModelMessage> Messages,
        [property: JsonPropertyName("max_tokens")]
        int MaxTokens,
        [property: JsonPropertyName("stream")]
        bool Stream,
        [property: JsonPropertyName("chat_template_kwargs")]
        IReadOnlyDictionary<string, bool> ChatTemplateKwargs,
        [property: JsonPropertyName("response_format")]
        JsonElement? ResponseFormat,
        [property: JsonPropertyName("tools")]
        JsonElement? Tools,
        [property: JsonPropertyName("tool_choice")]
        JsonElement? ToolChoice);
}
