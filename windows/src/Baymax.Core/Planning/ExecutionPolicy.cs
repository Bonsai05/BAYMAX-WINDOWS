using Baymax.Contracts;

namespace Baymax.Core.Planning;

public sealed class ExecutionPolicy
{
    public static readonly string[] Capabilities =
    [
        "LIST_DIRECTORY",
        "SEARCH_FILES",
        "GET_FILE_METADATA",
        "READ_FILE",
        "WRITE_FILE",
        "COPY_FILES",
        "MOVE_FILE",
        "RENAME_PATH",
        "CREATE_DIRECTORY"
    ];

    private readonly string _workspaceRoot;

    public ExecutionPolicy(
        string workspaceRoot,
        bool readEnabled = true,
        bool writeEnabled = true,
        bool executeEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!Path.IsPathFullyQualified(workspaceRoot))
        {
            throw new InvalidOperationException("The Baymax workspace path must be absolute.");
        }

        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var volumeRoot = Path.GetPathRoot(_workspaceRoot);
        if (volumeRoot is not null
            && string.Equals(
                Path.TrimEndingDirectorySeparator(_workspaceRoot),
                Path.TrimEndingDirectorySeparator(volumeRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Baymax workspace must be a folder below a drive root.");
        }

        ReadEnabled = readEnabled;
        WriteEnabled = writeEnabled;
        ExecuteEnabled = executeEnabled;
        if (!Directory.Exists(_workspaceRoot))
        {
            throw new InvalidOperationException(
                $"The Baymax workspace '{_workspaceRoot}' does not exist.");
        }
    }

    public string WorkspaceRoot => _workspaceRoot;
    public bool ReadEnabled { get; }
    public bool WriteEnabled { get; }
    public bool ExecuteEnabled { get; }

    public bool IsActionEnabled(string actionName) =>
        actionName switch
        {
            "LIST_DIRECTORY" or "SEARCH_FILES" or "GET_FILE_METADATA" or "READ_FILE" => ReadEnabled,
            "COPY_FILES" => ReadEnabled && WriteEnabled,
            "WRITE_FILE" or "MOVE_FILE" or "RENAME_PATH" or "CREATE_DIRECTORY" => WriteEnabled,
            _ => false
        };

    public bool IsMobileActionEnabled(string actionName) =>
        ExecuteEnabled
        && (actionName switch
        {
            "LIST_DIRECTORY" or "SEARCH_FILES" or "GET_FILE_METADATA" or "READ_FILE" => ReadEnabled,
            "COPY_FILES" => ReadEnabled && WriteEnabled,
            "WRITE_FILE" or "CREATE_DIRECTORY" => WriteEnabled,
            _ => false
        });

    public void ValidateMobile(TaskPlanResponse plan)
    {
        Validate(plan);
        foreach (var action in plan.Actions)
        {
            if (!IsMobileActionEnabled(action.Name))
            {
                throw new UnauthorizedAccessException(
                    $"Action '{action.Name}' is not allowed for direct Android execution.");
            }

            if (action.Name == "WRITE_FILE"
                && !string.Equals(
                    action.Arguments.GetProperty("mode").GetString(),
                    "create",
                    StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    "Direct Android execution may create files but cannot replace or append to existing files.");
            }
        }
    }

    public void Validate(TaskPlanResponse plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ExecuteEnabled)
        {
            throw new UnauthorizedAccessException("Plan execution is disabled by the workspace permissions.");
        }

        if (plan.Actions.Count == 0)
        {
            throw new UnauthorizedAccessException("Execution is denied for plans without actions.");
        }

        foreach (var action in plan.Actions)
        {
            if (!Capabilities.Contains(action.Name, StringComparer.Ordinal)
                || !IsActionEnabled(action.Name))
            {
                throw new UnauthorizedAccessException(
                    $"Action '{action.Name}' is not granted by the workspace permissions.");
            }

            foreach (var path in GetPaths(action))
            {
                string canonicalPath;
                try
                {
                    canonicalPath = Path.GetFullPath(path);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    throw new UnauthorizedAccessException("An action contains an invalid path.", exception);
                }

                if (!IsWithinRoot(_workspaceRoot, canonicalPath))
                {
                    throw new UnauthorizedAccessException(
                        $"Action '{action.Name}' references a path outside the Baymax workspace.");
                }
            }
        }
    }

    private static IEnumerable<string> GetPaths(PlannedAction action)
    {
        switch (action.Name)
        {
            case "LIST_DIRECTORY":
            case "GET_FILE_METADATA":
            case "READ_FILE":
            case "WRITE_FILE":
            case "CREATE_DIRECTORY":
                yield return action.Arguments.GetProperty("path").GetString()!;
                break;
            case "SEARCH_FILES":
                yield return action.Arguments.GetProperty("rootPath").GetString()!;
                break;
            case "COPY_FILES":
                yield return action.Arguments.GetProperty("destinationDirectory").GetString()!;
                foreach (var source in action.Arguments.GetProperty("sourcePaths").EnumerateArray())
                {
                    yield return source.GetString()!;
                }
                break;
            case "MOVE_FILE":
            case "RENAME_PATH":
                yield return action.Arguments.GetProperty("sourcePath").GetString()!;
                yield return action.Arguments.GetProperty("destinationPath").GetString()!;
                break;
            default:
                throw new UnauthorizedAccessException($"Action '{action.Name}' has no workspace capability.");
        }
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        return relativePath == "."
            || (!Path.IsPathRooted(relativePath)
                && relativePath != ".."
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
