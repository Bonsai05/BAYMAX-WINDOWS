using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baymax.Contracts;

namespace Baymax.Core.Planning;

public sealed class FileSystemActionExecutor
{
    private const int MaximumScannedEntries = 10_000;
    private const long MaximumFileBytes = 5 * 1024 * 1024;
    private readonly string _workspaceRoot;

    public FileSystemActionExecutor(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);
    }

    public string WorkspaceRoot => _workspaceRoot;

    public async Task<PlanExecutionResponse> ExecuteAsync(
        TaskPlanResponse plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var results = new List<ActionExecutionResult>(plan.Actions.Count);
        var failed = false;
        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failed)
            {
                results.Add(new ActionExecutionResult(
                    action.ActionId,
                    action.Ordinal,
                    action.Name,
                    "SKIPPED",
                    "PREVIOUS_ACTION_FAILED",
                    "The plan stopped after an earlier action failed.",
                    null));
                continue;
            }

            try
            {
                var result = await ExecuteActionAsync(action, cancellationToken);
                results.Add(new ActionExecutionResult(
                    action.ActionId,
                    action.Ordinal,
                    action.Name,
                    "COMPLETED",
                    null,
                    null,
                    JsonSerializer.SerializeToElement(result)));
            }
            catch (UnauthorizedAccessException exception)
            {
                results.Add(Failed(action, "ACCESS_DENIED", exception.Message));
                failed = true;
            }
            catch (DirectoryNotFoundException exception)
            {
                results.Add(Failed(action, "NOT_FOUND", exception.Message));
                failed = true;
            }
            catch (FileNotFoundException exception)
            {
                results.Add(Failed(action, "NOT_FOUND", exception.Message));
                failed = true;
            }
            catch (DecoderFallbackException exception)
            {
                results.Add(Failed(action, "INVALID_ENCODING", exception.Message));
                failed = true;
            }
            catch (InvalidDataException exception)
            {
                results.Add(Failed(action, "LIMIT_EXCEEDED", exception.Message));
                failed = true;
            }
            catch (IOException exception)
            {
                results.Add(Failed(action, "IO_ERROR", exception.Message));
                failed = true;
            }
        }

        var status = results.All(result => result.Status == "COMPLETED")
            ? "COMPLETED"
            : results.All(result => result.Status is "FAILED" or "SKIPPED") ? "FAILED" : "PARTIAL";
        return new PlanExecutionResponse(status, results, DateTimeOffset.UtcNow);
    }

    private async Task<object> ExecuteActionAsync(PlannedAction action, CancellationToken cancellationToken) =>
        action.Name switch
        {
            "LIST_DIRECTORY" => ListDirectory(action.Arguments, cancellationToken),
            "SEARCH_FILES" => SearchFiles(action.Arguments, cancellationToken),
            "GET_FILE_METADATA" => GetFileMetadata(action.Arguments),
            "READ_FILE" => await ReadFileAsync(action.Arguments, cancellationToken),
            "WRITE_FILE" => await WriteFileAsync(action.Arguments, cancellationToken),
            "COPY_FILES" => await CopyFilesAsync(action.Arguments, cancellationToken),
            "MOVE_FILE" => MoveFile(action.Arguments),
            "RENAME_PATH" => RenamePath(action.Arguments),
            "CREATE_DIRECTORY" => CreateDirectory(action.Arguments),
            _ => throw new InvalidDataException($"Unsupported action '{action.Name}'.")
        };

    private object ListDirectory(JsonElement arguments, CancellationToken cancellationToken)
    {
        var root = ResolvePath(arguments.GetProperty("path").GetString()!, mustExist: true);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Directory '{root}' does not exist.");
        }

        var entries = new List<object>();
        AddDirectoryEntries(
            root,
            1,
            arguments.GetProperty("recursive").GetBoolean(),
            arguments.GetProperty("maxDepth").GetInt32(),
            arguments.GetProperty("maxEntries").GetInt32(),
            entries,
            cancellationToken);
        return new { path = root, entries };
    }

    private object SearchFiles(JsonElement arguments, CancellationToken cancellationToken)
    {
        var root = ResolvePath(arguments.GetProperty("rootPath").GetString()!, mustExist: true);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Directory '{root}' does not exist.");
        }

        var query = arguments.GetProperty("query").GetString()!;
        var maxDepth = arguments.GetProperty("maxDepth").GetInt32();
        var maxResults = arguments.GetProperty("maxResults").GetInt32();
        var filePatterns = arguments.TryGetProperty("filePatterns", out var patternsElement)
            ? patternsElement.EnumerateArray().Select(pattern => pattern.GetString()!).ToArray()
            : [];
        var matches = new List<object>();
        var scannedEntries = 0;
        SearchDirectory(
            root,
            query,
            filePatterns,
            1,
            maxDepth,
            maxResults,
            matches,
            ref scannedEntries,
            cancellationToken);
        return new { rootPath = root, matches };
    }

    private object GetFileMetadata(JsonElement arguments)
    {
        var path = ResolvePath(arguments.GetProperty("path").GetString()!, mustExist: true);
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        info.Refresh();
        return new
        {
            path,
            type = isDirectory ? "directory" : "file",
            sizeBytes = isDirectory ? (long?)null : ((FileInfo)info).Length,
            lastModifiedUtc = info.LastWriteTimeUtc
        };
    }

    private async Task<object> ReadFileAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = ResolvePath(arguments.GetProperty("path").GetString()!, mustExist: true);
        if (Directory.Exists(path))
        {
            throw new InvalidDataException("READ_FILE only accepts files.");
        }

        var maxBytes = arguments.GetProperty("maxBytes").GetInt32();
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        if (stream.Length > maxBytes)
        {
            throw new InvalidDataException($"The file exceeds the requested {maxBytes}-byte read limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        var content = new UTF8Encoding(false, true).GetString(bytes);
        return new { path, content, sizeBytes = bytes.Length };
    }

    private async Task<object> WriteFileAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = ResolvePath(arguments.GetProperty("path").GetString()!, mustExist: false);
        var parent = Path.GetDirectoryName(path)
            ?? throw new UnauthorizedAccessException("A file must be created below the workspace root.");
        ResolvePath(parent, mustExist: true);

        var content = arguments.GetProperty("content").GetString()!;
        var bytes = new UTF8Encoding(false, true).GetBytes(content);
        if (bytes.LongLength > MaximumFileBytes)
        {
            throw new InvalidDataException($"WRITE_FILE content cannot exceed {MaximumFileBytes} bytes.");
        }

        var mode = arguments.GetProperty("mode").GetString()!;
        var exists = File.Exists(path) || Directory.Exists(path);
        switch (mode)
        {
            case "create" when exists:
                throw new IOException($"Destination '{path}' already exists.");
            case "create":
                await WriteNewFileAsync(path, bytes, cancellationToken);
                break;
            case "replace":
                if (!File.Exists(path) || Directory.Exists(path))
                {
                    throw new FileNotFoundException($"File '{path}' must exist before it can be replaced.", path);
                }

                await ReplaceFileAsync(path, bytes, cancellationToken);
                break;
            case "append":
                if (!File.Exists(path) || Directory.Exists(path))
                {
                    throw new FileNotFoundException($"File '{path}' must exist before content can be appended.", path);
                }

                var currentLength = new FileInfo(path).Length;
                if (currentLength + bytes.LongLength > MaximumFileBytes)
                {
                    throw new InvalidDataException($"The resulting file cannot exceed {MaximumFileBytes} bytes.");
                }

                ResolvePath(path, mustExist: true);
                await using (var stream = new FileStream(
                                 path,
                                 FileMode.Append,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 64 * 1024,
                                 useAsync: true))
                {
                    if (stream.Length + bytes.LongLength > MaximumFileBytes)
                    {
                        throw new InvalidDataException(
                            $"The resulting file cannot exceed {MaximumFileBytes} bytes.");
                    }

                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                break;
            default:
                throw new InvalidDataException($"Unsupported write mode '{mode}'.");
        }

        return new { path, mode, bytesWritten = bytes.LongLength };
    }

    private async Task<object> CopyFilesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var destinationDirectory = ResolvePath(
            arguments.GetProperty("destinationDirectory").GetString()!,
            mustExist: true);
        if (!Directory.Exists(destinationDirectory))
        {
            throw new DirectoryNotFoundException($"Destination '{destinationDirectory}' is not a directory.");
        }

        var sourcePaths = arguments.GetProperty("sourcePaths")
            .EnumerateArray()
            .Select(source => ResolvePath(source.GetString()!, mustExist: true))
            .ToArray();
        if (sourcePaths.Length != sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new InvalidDataException("COPY_FILES cannot contain duplicate source paths.");
        }

        var maxTotalBytes = arguments.GetProperty("maxTotalBytes").GetInt64();
        long totalBytes = 0;
        foreach (var sourcePath in sourcePaths)
        {
            if (Directory.Exists(sourcePath))
            {
                throw new InvalidDataException("COPY_FILES accepts files only; directory copying is disabled.");
            }

            totalBytes = checked(totalBytes + new FileInfo(sourcePath).Length);
            if (totalBytes > maxTotalBytes)
            {
                throw new InvalidDataException($"The files exceed the requested {maxTotalBytes}-byte copy limit.");
            }
        }

        var collisionPolicy = arguments.GetProperty("collisionPolicy").GetString()!;
        var destinations = new List<(string Source, string? Destination)>(sourcePaths.Length);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in sourcePaths)
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath) || reserved.Contains(destinationPath))
            {
                destinationPath = collisionPolicy switch
                {
                    "fail" => throw new IOException($"Destination '{destinationPath}' already exists."),
                    "skip" => null,
                    "rename" => GetAvailableCopyName(destinationPath, reserved),
                    _ => throw new InvalidDataException($"Unsupported collision policy '{collisionPolicy}'.")
                };
            }

            if (destinationPath is not null)
            {
                reserved.Add(destinationPath);
            }

            destinations.Add((sourcePath, destinationPath));
        }

        var results = new List<object>(destinations.Count);
        foreach (var (sourcePath, destinationPath) in destinations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (destinationPath is null)
            {
                results.Add(new { sourcePath, status = "SKIPPED", destinationPath = (string?)null });
                continue;
            }

            await CopyOneFileAsync(sourcePath, destinationPath, cancellationToken);
            results.Add(new { sourcePath, status = "COPIED", destinationPath });
        }

        return new { totalBytes, files = results };
    }

    private object MoveFile(JsonElement arguments)
    {
        var sourcePath = ResolvePath(arguments.GetProperty("sourcePath").GetString()!, mustExist: true);
        var destinationPath = ResolvePath(arguments.GetProperty("destinationPath").GetString()!, mustExist: false);
        var replaceExisting = arguments.GetProperty("replaceExisting").GetBoolean();
        if (Directory.Exists(sourcePath))
        {
            throw new InvalidDataException("MOVE_FILE accepts files only; moving directories is disabled.");
        }

        var destinationParent = Path.GetDirectoryName(destinationPath)!;
        ResolvePath(destinationParent, mustExist: true);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The source and destination paths must be different.");
        }

        EnsureCollisionPolicy(destinationPath, replaceExisting, allowReplaceFile: true);
        ResolvePath(sourcePath, mustExist: true);
        ResolvePath(destinationPath, mustExist: false);
        File.Move(sourcePath, destinationPath, replaceExisting);
        return new { sourcePath, destinationPath, moved = true, replacedExisting = replaceExisting };
    }

    private object RenamePath(JsonElement arguments)
    {
        var sourcePath = ResolvePath(arguments.GetProperty("sourcePath").GetString()!, mustExist: true);
        var destinationPath = ResolvePath(arguments.GetProperty("destinationPath").GetString()!, mustExist: false);
        var replaceExisting = arguments.GetProperty("replaceExisting").GetBoolean();
        var sourceParent = Path.GetDirectoryName(sourcePath);
        var destinationParent = Path.GetDirectoryName(destinationPath);
        if (!string.Equals(sourceParent, destinationParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("RENAME_PATH must keep the item in its existing parent directory.");
        }

        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The source and destination paths must be different.");
        }

        var isDirectory = Directory.Exists(sourcePath);
        EnsureCollisionPolicy(destinationPath, replaceExisting, allowReplaceFile: !isDirectory);
        ResolvePath(sourcePath, mustExist: true);
        ResolvePath(destinationPath, mustExist: false);
        if (isDirectory)
        {
            Directory.Move(sourcePath, destinationPath);
        }
        else
        {
            File.Move(sourcePath, destinationPath, replaceExisting);
        }

        return new { sourcePath, destinationPath, renamed = true, replacedExisting = replaceExisting };
    }

    private object CreateDirectory(JsonElement arguments)
    {
        var path = ResolvePath(arguments.GetProperty("path").GetString()!, mustExist: false);
        if (File.Exists(path))
        {
            throw new IOException($"A file already exists at '{path}'.");
        }

        var alreadyExisted = Directory.Exists(path);
        ResolvePath(path, mustExist: alreadyExisted);
        Directory.CreateDirectory(path);
        ResolvePath(path, mustExist: true);
        return new { path, created = !alreadyExisted };
    }

    private async Task WriteNewFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        ResolvePath(path, mustExist: false);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private async Task ReplaceFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path)!;
        var temporaryPath = Path.Combine(parent, $".baymax-{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteNewFileAsync(temporaryPath, bytes, cancellationToken);
            ResolvePath(path, mustExist: true);
            ResolvePath(temporaryPath, mustExist: true);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task CopyOneFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".baymax-{Guid.NewGuid():N}.tmp");
        try
        {
            ResolvePath(sourcePath, mustExist: true);
            ResolvePath(temporaryPath, mustExist: false);
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 128 * 1024,
                             useAsync: true))
            await using (var target = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }

            ResolvePath(sourcePath, mustExist: true);
            ResolvePath(temporaryPath, mustExist: true);
            ResolvePath(destinationPath, mustExist: false);
            var sourceHash = await HashFileAsync(sourcePath, cancellationToken);
            var copiedHash = await HashFileAsync(temporaryPath, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, copiedHash))
            {
                throw new IOException($"Copy verification failed for '{sourcePath}'.");
            }

            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureCollisionPolicy(string destinationPath, bool replaceExisting, bool allowReplaceFile)
    {
        var destinationExists = File.Exists(destinationPath) || Directory.Exists(destinationPath);
        if (!destinationExists)
        {
            return;
        }

        if (!replaceExisting)
        {
            throw new IOException($"Destination '{destinationPath}' already exists.");
        }

        if (!allowReplaceFile || !File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException("Only an existing file can be replaced; directory replacement is never allowed.");
        }
    }

    private static string GetAvailableCopyName(string path, HashSet<string> reserved)
    {
        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; index <= 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && !reserved.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"No available collision-free name could be found for '{path}'.");
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    private void AddDirectoryEntries(
        string directory,
        int depth,
        bool recursive,
        int maxDepth,
        int maxEntries,
        List<object> entries,
        CancellationToken cancellationToken)
    {
        ResolvePath(directory, mustExist: true);
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvePath(entryPath, mustExist: true);
            var attributes = File.GetAttributes(entryPath);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(entryPath) : new FileInfo(entryPath);
            info.Refresh();
            entries.Add(new
            {
                name = Path.GetFileName(entryPath),
                path = entryPath,
                type = isDirectory ? "directory" : "file",
                sizeBytes = isDirectory ? (long?)null : ((FileInfo)info).Length,
                lastModifiedUtc = info.LastWriteTimeUtc
            });
            if (entries.Count > maxEntries)
            {
                throw new InvalidDataException($"Directory listing exceeded the {maxEntries} entry limit.");
            }

            if (recursive && isDirectory && depth < maxDepth)
            {
                AddDirectoryEntries(
                    entryPath,
                    depth + 1,
                    recursive,
                    maxDepth,
                    maxEntries,
                    entries,
                    cancellationToken);
            }
        }
    }

    private void SearchDirectory(
        string directory,
        string query,
        IReadOnlyList<string> filePatterns,
        int depth,
        int maxDepth,
        int maxResults,
        List<object> matches,
        ref int scannedEntries,
        CancellationToken cancellationToken)
    {
        ResolvePath(directory, mustExist: true);
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedEntries++;
            if (scannedEntries > MaximumScannedEntries)
            {
                throw new InvalidDataException(
                    $"File search exceeded the {MaximumScannedEntries} scanned-entry limit.");
            }

            ResolvePath(entryPath, mustExist: true);
            var attributes = File.GetAttributes(entryPath);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var name = Path.GetFileName(entryPath);
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase)
                && (filePatterns.Count == 0
                    || filePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, true))))
            {
                var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(entryPath) : new FileInfo(entryPath);
                info.Refresh();
                matches.Add(new
                {
                    path = entryPath,
                    type = isDirectory ? "directory" : "file",
                    sizeBytes = isDirectory ? (long?)null : ((FileInfo)info).Length,
                    lastModifiedUtc = info.LastWriteTimeUtc
                });
                if (matches.Count > maxResults)
                {
                    throw new InvalidDataException($"File search exceeded the {maxResults} result limit.");
                }
            }

            if (isDirectory && depth < maxDepth)
            {
                SearchDirectory(
                    entryPath,
                    query,
                    filePatterns,
                    depth + 1,
                    maxDepth,
                    maxResults,
                    matches,
                    ref scannedEntries,
                    cancellationToken);
            }
        }
    }

    private string ResolvePath(string requestedPath, bool mustExist)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new UnauthorizedAccessException("The requested path is invalid.", exception);
        }

        if (!IsWithinRoot(_workspaceRoot, fullPath))
        {
            throw new UnauthorizedAccessException("The requested path is outside the Baymax workspace.");
        }

        var relativePath = Path.GetRelativePath(_workspaceRoot, fullPath);
        var currentPath = _workspaceRoot;
        CheckNotReparsePoint(currentPath);
        if (relativePath == ".")
        {
            return fullPath;
        }

        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            var attributes = TryGetAttributes(currentPath);
            if (attributes is null)
            {
                if (mustExist)
                {
                    throw new FileNotFoundException($"Path '{currentPath}' does not exist.", currentPath);
                }

                continue;
            }

            if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    $"Reparse points are not allowed in the workspace: '{currentPath}'.");
            }
        }

        return fullPath;
    }

    private static string NormalizeWorkspaceRoot(string workspaceRoot)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException(
                $"The Baymax workspace '{fullPath}' does not exist.");
        }

        string? currentPath = fullPath;
        var volumeRoot = Path.GetPathRoot(fullPath);
        while (currentPath is not null)
        {
            CheckNotReparsePoint(currentPath);
            if (string.Equals(currentPath, volumeRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            currentPath = Directory.GetParent(currentPath)?.FullName;
        }

        return fullPath;
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        return relativePath == "."
            || (!Path.IsPathRooted(relativePath)
                && relativePath != ".."
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static void CheckNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException($"Reparse points are not allowed in the workspace: '{path}'.");
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static ActionExecutionResult Failed(PlannedAction action, string failureCode, string error) =>
        new(action.ActionId, action.Ordinal, action.Name, "FAILED", failureCode, error, null);
}
