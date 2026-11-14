using System.Text.Json;
using Baymax.Contracts;
using Baymax.Core.Models;

namespace Baymax.Core.Planning;

public sealed class TaskPlanner(LlamaCppClient modelClient, ExecutionPolicy executionPolicy)
{
    private const int MaximumActions = 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, ActionDefinition> Actions = CreateActions();

    public IReadOnlyList<ActionDefinitionResponse> GetActionDefinitions() =>
        GetEnabledActions()
            .Select(action => new ActionDefinitionResponse(
                action.Name,
                action.Description,
                action.Schema.RootElement.Clone(),
                action.ExpectedResult,
                action.FailureCodes))
            .ToArray();

    public void ValidatePlanForExecution(TaskPlanResponse plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Actions is null
            || plan.NeedsClarification
            || plan.Actions.Count is < 1 or > MaximumActions)
        {
            throw new InvalidDataException("Only complete plans containing 1 to 20 actions can be executed.");
        }

        if (string.IsNullOrWhiteSpace(plan.Summary)
            || plan.Summary.Length > 500
            || !string.IsNullOrEmpty(plan.ClarifyingQuestion))
        {
            throw new InvalidDataException("The plan summary or clarification fields are invalid.");
        }

        var actionIds = new HashSet<Guid>();
        for (var index = 0; index < plan.Actions.Count; index++)
        {
            var action = plan.Actions[index];
            if (action.ActionId == Guid.Empty
                || !actionIds.Add(action.ActionId)
                || action.Ordinal != index + 1)
            {
                throw new InvalidDataException("Plan action IDs and ordinals must be unique and sequential.");
            }

            ValidateAction(action);
        }
    }

    public void ValidateMobilePlanForExecution(TaskPlanResponse plan)
    {
        ValidatePlanForExecution(plan);
        executionPolicy.ValidateMobile(plan);
    }

    public void ValidateAction(PlannedAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (string.IsNullOrWhiteSpace(action.Name) || action.Arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Each plan action must have a supported name and object arguments.");
        }

        if (!Actions.TryGetValue(action.Name, out var definition))
        {
            throw new InvalidDataException($"The plan contains unsupported action '{action.Name}'.");
        }

        if (!executionPolicy.IsActionEnabled(action.Name))
        {
            throw new InvalidDataException($"The plan contains an action disabled by workspace permissions: '{action.Name}'.");
        }

        JsonSchemaValidator.Validate(definition.Schema.RootElement, action.Arguments);
        ValidatePaths(definition, action.Arguments);
    }

    public Task<TaskPlanResponse> CreatePlanAsync(
        CreatePlanRequest request,
        CancellationToken cancellationToken = default) =>
        CreatePlanAsync(request, mobileExecution: false, cancellationToken);

    public Task<TaskPlanResponse> CreateMobilePlanAsync(
        CreatePlanRequest request,
        CancellationToken cancellationToken = default) =>
        CreatePlanAsync(request, mobileExecution: true, cancellationToken);

    private async Task<TaskPlanResponse> CreatePlanAsync(
        CreatePlanRequest request,
        bool mobileExecution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Instruction) || request.Instruction.Length > 4000)
        {
            throw new ArgumentException("Instruction must contain 1 to 4000 non-whitespace characters.", nameof(request));
        }

        var history = request.History ?? [];
        if (history.Count > 40
            || history.Any(message =>
                message is null
                || message.Role is not ("user" or "assistant")
                || string.IsNullOrWhiteSpace(message.Content)
                || message.Content.Length > 4000)
            || history.Sum(message => (long)message.Content.Length) > 16000)
        {
            throw new ArgumentException(
                "Conversation history must contain at most 40 user/assistant messages and 16,000 total characters.",
                nameof(request));
        }

        var enabledActions = GetEnabledActions(mobileExecution);
        if (enabledActions.Count == 0)
        {
            throw new ArgumentException(
                mobileExecution
                    ? "Enable the required workspace permissions in Settings to allow direct Android actions."
                    : "Enable Read or Write in Settings to allow workspace actions.",
                nameof(request));
        }

        using var planFormat = JsonDocument.Parse(CreatePlanFormat(enabledActions));
        var messages = new List<ModelMessage>(history.Count + 2)
        {
            new("system", CreateSystemPrompt(enabledActions, mobileExecution))
        };
        messages.AddRange(history.Select(message => new ModelMessage(message.Role, message.Content)));
        messages.Add(new ModelMessage("user", request.Instruction.Trim()));

        var response = await modelClient.CompleteAsync(
            new ModelCompletionRequest(
                messages,
                planFormat.RootElement.Clone(),
                MaxTokens: 2048),
            cancellationToken);

        if (response.Content is null)
        {
            throw new InvalidDataException("The model returned no task plan.");
        }

        using var document = JsonDocument.Parse(response.Content);
        JsonSchemaValidator.Validate(
            planFormat.RootElement.GetProperty("json_schema").GetProperty("schema"),
            document.RootElement);

        var plan = document.RootElement;
        var summary = plan.GetProperty("summary").GetString()!;
        var needsClarification = plan.GetProperty("needsClarification").GetBoolean();
        var clarifyingQuestion = plan.GetProperty("clarifyingQuestion").GetString()!;
        var proposedActions = plan.GetProperty("actions");
        if (needsClarification)
        {
            if (proposedActions.GetArrayLength() != 0 || string.IsNullOrWhiteSpace(clarifyingQuestion))
            {
                throw new InvalidDataException(
                    "A plan that needs clarification must contain a question and no proposed actions.");
            }
        }
        else if (proposedActions.GetArrayLength() == 0 || !string.IsNullOrWhiteSpace(clarifyingQuestion))
        {
            throw new InvalidDataException(
                "A plan without a clarification request must contain at least one action and no clarification question.");
        }

        if (proposedActions.GetArrayLength() > MaximumActions)
        {
            throw new InvalidDataException($"A task plan cannot contain more than {MaximumActions} actions.");
        }

        var validatedActions = new List<PlannedAction>(proposedActions.GetArrayLength());
        var ordinal = 1;
        foreach (var proposedAction in proposedActions.EnumerateArray())
        {
            var name = proposedAction.GetProperty("name").GetString()!;
            if (!Actions.TryGetValue(name, out var definition)
                || !(mobileExecution
                    ? executionPolicy.IsMobileActionEnabled(name)
                    : executionPolicy.IsActionEnabled(name)))
            {
                throw new InvalidDataException($"The model proposed an unsupported or disabled action '{name}'.");
            }

            var arguments = proposedAction.GetProperty("arguments");
            JsonSchemaValidator.Validate(definition.Schema.RootElement, arguments);
            ValidatePaths(definition, arguments);
            validatedActions.Add(new PlannedAction(
                Guid.NewGuid(),
                ordinal++,
                definition.Name,
                arguments.Clone()));
        }

        return new TaskPlanResponse(
            summary,
            needsClarification,
            clarifyingQuestion,
            response.Model,
            validatedActions,
            response.PromptTokens,
            response.CompletionTokens,
            response.DurationMilliseconds,
            response.TokensPerSecond);
    }

    private IReadOnlyList<ActionDefinition> GetEnabledActions(bool mobileExecution = false) =>
        Actions.Values
            .Where(action => mobileExecution
                ? executionPolicy.IsMobileActionEnabled(action.Name)
                : executionPolicy.IsActionEnabled(action.Name))
            .ToArray();

    private string CreateSystemPrompt(
        IReadOnlyList<ActionDefinition> enabledActions,
        bool mobileExecution)
    {
        var catalog = string.Join(
            Environment.NewLine,
            enabledActions.Select(action =>
                $"{action.Name}: {action.Description}{Environment.NewLine}Arguments schema: {action.Schema.RootElement.GetRawText()}"));
        return $"""
            Convert the user's request into a safe, ordered JSON plan using only the allowed actions below.
            Return only the required JSON object. Do not execute actions, claim they were completed, or invent paths.
            All paths must be fully qualified Windows paths inside this workspace: {executionPolicy.WorkspaceRoot}
            Never propose a path outside that workspace. If a path, source, destination, or intent is missing or ambiguous, return needsClarification=true, ask one concise question, and return no actions.
            For a clear request, set needsClarification=false, clarifyingQuestion to an empty string, and provide one or more actions in execution order.
            {(mobileExecution ? "Never propose permanent file deletion, running commands, changing the registry or system settings, or any action not listed." : "Never propose permanent file deletion, running commands, changing the registry or system settings, or any action not listed. Only propose MOVE_FILE for a clear user request to move/cut-and-paste a file; it removes the source path but never permanently deletes the file.")}
            Copy collisionPolicy may only be fail, skip, or rename; COPY_FILES never overwrites.
            {(mobileExecution ? "WRITE_FILE must use create mode. Never move, rename, replace, append, or delete files." : "WRITE_FILE mode must be create unless the user clearly requests changing an existing file; replace and append require a plan-bound Windows-authenticated approval.")}
            {(mobileExecution ? string.Empty : "MOVE_FILE is permitted only for an explicit move/cut-and-paste request; it removes the source path but never permanently deletes the file. RENAME_PATH must stay in the same directory.")}
            {(mobileExecution ? "This request will execute directly after submission from a paired Android device." : string.Empty)}
            Maximum 20 actions. Keep the summary concise.

            Allowed actions and their strict arguments schemas:
            {catalog}
            """;
    }

    private static string CreatePlanFormat(IReadOnlyList<ActionDefinition> enabledActions)
    {
        var actionNames = JsonSerializer.Serialize(enabledActions.Select(action => action.Name), JsonOptions);
        return $$"""
            {
              "type": "json_schema",
              "json_schema": {
                "name": "baymax_task_plan",
                "strict": true,
                "schema": {
                  "type": "object",
                  "properties": {
                    "summary": { "type": "string", "minLength": 1, "maxLength": 500 },
                    "needsClarification": { "type": "boolean" },
                    "clarifyingQuestion": { "type": "string", "maxLength": 1000 },
                    "actions": {
                      "type": "array",
                      "minItems": 0,
                      "maxItems": 20,
                      "items": {
                        "type": "object",
                        "properties": {
                          "name": { "type": "string", "enum": {{actionNames}} },
                          "arguments": { "type": "object" }
                        },
                        "required": ["name", "arguments"],
                        "additionalProperties": false
                      }
                    }
                  },
                  "required": ["summary", "needsClarification", "clarifyingQuestion", "actions"],
                  "additionalProperties": false
                }
              }
            }
            """;
    }

    private static IReadOnlyDictionary<string, ActionDefinition> CreateActions()
    {
        var actions = new[]
        {
            new ActionDefinition(
                "LIST_DIRECTORY",
                "List entries in a directory.",
                """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "minLength": 1, "maxLength": 1024 },
                    "recursive": { "type": "boolean" },
                    "maxDepth": { "type": "integer", "minimum": 1, "maximum": 32 },
                    "maxEntries": { "type": "integer", "minimum": 1, "maximum": 1000 }
                  },
                  "required": ["path", "recursive", "maxDepth", "maxEntries"],
                  "additionalProperties": false
                }
                """,
                "A list of entries with name, full path, type, size, and modification time; maxEntries is 1-1000 and maxDepth is 1-32.",
                ["INVALID_PATH", "ACCESS_DENIED", "NOT_FOUND", "LIMIT_EXCEEDED"]),
            new ActionDefinition(
                "SEARCH_FILES",
                "Search file and directory names under a root directory.",
                """
                {
                  "type": "object",
                  "properties": {
                    "rootPath": { "type": "string", "minLength": 1, "maxLength": 1024 },
                    "query": { "type": "string", "minLength": 1, "maxLength": 256 },
                    "maxDepth": { "type": "integer", "minimum": 1, "maximum": 32 },
                    "filePatterns": {
                      "type": "array",
                      "minItems": 1,
                      "maxItems": 20,
                      "items": { "type": "string", "minLength": 1, "maxLength": 128 }
                    },
                    "maxResults": { "type": "integer", "minimum": 1, "maximum": 1000 }
                  },
                  "required": ["rootPath", "query", "maxDepth", "maxResults"],
                  "additionalProperties": false
                }
                """,
                "A bounded list of matching paths and basic metadata; maxResults is 1-1000, maxDepth is 1-32, and file contents are not returned.",
                ["INVALID_PATH", "ACCESS_DENIED", "NOT_FOUND", "LIMIT_EXCEEDED"]),
            new ActionDefinition(
                "GET_FILE_METADATA",
                "Get basic metadata for one file or directory.",
                """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "minLength": 1, "maxLength": 1024 }
                  },
                  "required": ["path"],
                  "additionalProperties": false
                }
                """,
                "The path, entry type, size when applicable, and modification time.",
                ["INVALID_PATH", "ACCESS_DENIED", "NOT_FOUND"]),
            new ActionDefinition(
                "READ_FILE",
                "Read a bounded UTF-8 text file.",
                """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "minLength": 1, "maxLength": 1024 },
                    "maxBytes": { "type": "integer", "minimum": 1, "maximum": 1048576 }
                  },
                  "required": ["path", "maxBytes"],
                  "additionalProperties": false
                }
                """,
                "The UTF-8 text content of a file up to 1 MiB; binary or oversized files are rejected.",
                ["INVALID_PATH", "ACCESS_DENIED", "NOT_FOUND", "LIMIT_EXCEEDED", "INVALID_ENCODING"]),
            new ActionDefinition(
                "WRITE_FILE",
                "Create, replace, or append UTF-8 text to a file.",
                """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "minLength": 1, "maxLength": 1024 },
                    "content": { "type": "string", "maxLength": 5242880 },
                    "mode": { "type": "string", "enum": ["create", "replace", "append"] }
                  },
                  "required": ["path", "content", "mode"],
                  "additionalProperties": false
                }
                """,
                "Writes the exact UTF-8 content in the selected mode. Replace and append require approval bound to this exact plan.",
                ["INVALID_PATH", "ACCESS_DENIED", "NOT_FOUND", "COLLISION", "LIMIT_EXCEEDED"]),
            new ActionDefinition(
                "COPY_FILES",
                "Copy an explicit list of files into a destination directory.",
                """
                {
                  "type": "object",
                  "properties": {
                    "sourcePaths": {
                      "type": "array",
                      "minItems": 1,
                      "maxItems": 100,
                      "items": { "type": "string", "minLength": 1, "maxLength": 1024 }
                    },
                    "destinationDirectory": { "type": "string", "minLength": 1, "maxLength": 1024 },
                    "collisionPolicy": { "type": "string", "enum": ["fail", "skip", "rename"] },
                    "maxTotalBytes": { "type": "integer", "minimum": 1, "maximum": 5368709120 }
                  },
                  "required": ["sourcePaths", "destinationDirectory", "collisionPolicy", "maxTotalBytes"],
                  "additionalProperties": false
                }
                """,
                "Per-source copy results with destination paths and verification metadata; at most 100 sources and maxTotalBytes is 1 byte to 5 GiB.",
                ["INVALID_PATH", "ACCESS_DENIED", "NOT_FOUND", "COLLISION", "LIMIT_EXCEEDED"]),
            new ActionDefinition(
                "MOVE_FILE",
                "Move one file to a new path inside the workspace. Directories cannot be moved.",
                """
                {
                  "type": "object",
                  "properties": {
                    "sourcePath": { "type": "string", "minLength": 1, "maxLength": 1024 },
                    "destinationPath": { "type": "string", "minLength": 1, "maxLength": 1024 },
                    "replaceExisting": { "type": "boolean" }
                  },
                  "required": ["sourcePath", "destinationPath", "replaceExisting"],
                  "additionalProperties": false
                }
                """,
                "Moves one file. Existing destinations are replaced only when explicitly requested and the exact plan is approved.",
                ["INVALID_PATH", "ACCESS_DENIED", "NOT_FOUND", "COLLISION"]),
            new ActionDefinition(
                "RENAME_PATH",
                "Rename one file or directory within its current parent directory.",
                """
                {
                  "type": "object",
                  "properties": {
                    "sourcePath": { "type": "string", "minLength": 1, "maxLength": 1024 },
                    "destinationPath": { "type": "string", "minLength": 1, "maxLength": 1024 },
                    "replaceExisting": { "type": "boolean" }
                  },
                  "required": ["sourcePath", "destinationPath", "replaceExisting"],
                  "additionalProperties": false
                }
                """,
                "Renames one file or directory in the same parent. Existing destinations are replaced only when explicitly requested and the exact plan is approved.",
                ["INVALID_PATH", "ACCESS_DENIED", "NOT_FOUND", "COLLISION"]),
            new ActionDefinition(
                "CREATE_DIRECTORY",
                "Create one directory at an explicit path.",
                """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "minLength": 1, "maxLength": 1024 }
                  },
                  "required": ["path"],
                  "additionalProperties": false
                }
                """,
                "The created directory path and whether it already existed.",
                ["INVALID_PATH", "ACCESS_DENIED", "ALREADY_EXISTS"])
        };

        return actions.ToDictionary(action => action.Name, StringComparer.Ordinal);
    }

    private static void ValidatePaths(ActionDefinition definition, JsonElement arguments)
    {
        string[] propertyNames = definition.Name switch
        {
            "LIST_DIRECTORY" or "GET_FILE_METADATA" or "CREATE_DIRECTORY" or "READ_FILE" or "WRITE_FILE" => ["path"],
            "SEARCH_FILES" => ["rootPath"],
            "COPY_FILES" => ["destinationDirectory"],
            "MOVE_FILE" or "RENAME_PATH" => ["sourcePath", "destinationPath"],
            _ => []
        };
        foreach (var propertyName in propertyNames)
        {
            ValidatePath(arguments.GetProperty(propertyName).GetString()!, propertyName);
        }

        if (definition.Name == "COPY_FILES")
        {
            foreach (var sourcePath in arguments.GetProperty("sourcePaths").EnumerateArray())
            {
                ValidatePath(sourcePath.GetString()!, "sourcePaths");
            }
        }

        if (definition.Name == "WRITE_FILE")
        {
            var content = arguments.GetProperty("content").GetString()!;
            if (System.Text.Encoding.UTF8.GetByteCount(content) > 5 * 1024 * 1024)
            {
                throw new InvalidDataException("WRITE_FILE content cannot exceed 5 MiB when encoded as UTF-8.");
            }
        }
    }

    private static void ValidatePath(string path, string fieldName)
    {
        if (!Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || path.Contains('*')
            || path.Contains('?')
            || path.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"Planned path '{fieldName}' must be an absolute Windows path without wildcards or device namespaces.");
        }

        var rootLength = Path.GetPathRoot(path)!.Length;
        var segments = path[rootLength..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Planned path '{fieldName}' cannot contain '.' or '..' segments.");
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        if (segments.Any(segment =>
                segment.IndexOfAny(invalidFileNameChars) >= 0
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || IsReservedWindowsName(segment)))
        {
            throw new InvalidDataException($"Planned path '{fieldName}' contains an invalid Windows path component.");
        }
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var name = segment.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (name.Length == 4
                && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && name[3] is >= '1' and <= '9');
    }

    private sealed class ActionDefinition(
        string name,
        string description,
        string schema,
        string expectedResult,
        IReadOnlyList<string> failureCodes)
    {
        public string Name { get; } = name;
        public string Description { get; } = description;
        public JsonDocument Schema { get; } = JsonDocument.Parse(schema);
        public string ExpectedResult { get; } = expectedResult;
        public IReadOnlyList<string> FailureCodes { get; } = failureCodes;
    }
}
