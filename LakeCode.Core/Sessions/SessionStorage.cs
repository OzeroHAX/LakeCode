using System.Text.Json;
using LakeCode.Core.Attachments;

namespace LakeCode.Core.Sessions;

/// <summary>Application-owned snapshots, separate from the agent's workspace file tools.</summary>
internal sealed class SessionStorage
{
    private const long MaxSnapshotBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _root;

    public SessionStorage()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidOperationException("Не удалось определить домашний каталог для сессий.");
        _root = Path.Combine(home, ".lakecode", "sessions");
    }

    public async Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ValidateDirectory(_root);
        var directory = Path.Combine(_root, ValidateId(snapshot.Id));
        ValidateDirectory(directory);
        await WriteAtomicAsync(Path.Combine(directory, "session.json"), snapshot, cancellationToken);
    }

    public async Task SavePendingAsync(string id, PendingMessage[] messages)
    {
        ValidateDirectory(_root);
        var directory = Path.Combine(_root, ValidateId(id));
        ValidateDirectory(directory);
        await WriteAtomicAsync(Path.Combine(directory, "pending.json"), messages, CancellationToken.None);
    }

    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        RejectLink(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                if (stream.Length > MaxSnapshotBytes)
                    throw new InvalidDataException("Сессия превышает лимит хранения 256 MiB.");
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<SessionSnapshot> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_root, ValidateId(id));
        ValidatePath(directory);
        var path = Path.Combine(directory, "session.json");
        RejectLink(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxSnapshotBytes)
            throw new InvalidDataException("Сессия превышает лимит хранения 256 MiB.");
        var snapshot = await JsonSerializer.DeserializeAsync<SessionSnapshot>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Файл сессии пуст.");
        if (snapshot.SchemaVersion is not (1 or 2 or 3) || snapshot.FrameworkVersion != "1.24.0")
            throw new InvalidDataException("Версия сохранённой сессии несовместима с этой версией LakeCode.");
        if (snapshot.Id != id || snapshot.Turns is null || string.IsNullOrWhiteSpace(snapshot.WorkspaceRoot) ||
            string.IsNullOrWhiteSpace(snapshot.Title) || string.IsNullOrWhiteSpace(snapshot.ModelId) ||
            string.IsNullOrWhiteSpace(snapshot.ReasoningEffort) || !Guid.TryParseExact(snapshot.ProviderId, "D", out _))
            throw new InvalidDataException("Некорректные данные сессии.");
        foreach (var turn in snapshot.Turns)
        {
            if (turn is null || turn.Text is null || turn.Images is null || turn.Events is null ||
                turn.Status is not ("in_progress" or "completed" or "failed" or "interrupted") ||
                turn.Images.Any(image => image is null || image.Data is null || image.Name is null))
                throw new InvalidDataException("Некорректные данные сообщения в сессии.");
            foreach (var item in turn.Events)
            {
                if (item is null || item.Kind is not ("text" or "thinking" or "tool") ||
                    (item.Kind != "tool" && item.Text is null))
                    throw new InvalidDataException("Некорректное событие ответа в сессии.");
                if (item.Kind != "tool") continue;
                if (item.ToolCall is not { } call || string.IsNullOrEmpty(call.CallId) ||
                    string.IsNullOrEmpty(call.Name) || call.Arguments is null || !Enum.IsDefined(call.Status))
                    throw new InvalidDataException("Некорректный вызов инструмента в сессии.");
                using var arguments = JsonDocument.Parse(call.Arguments);
                if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Аргументы инструмента должны быть объектом.");
            }
        }
        if (snapshot.DraftFiles is null) throw new InvalidDataException("Некорректные файловые вложения черновика.");
        FileAttachments.Validate(snapshot.DraftFiles);
        if (snapshot.DraftMentions is null) throw new InvalidDataException("Некорректные инлайн-вложения черновика.");
        FileMention.Validate(snapshot.DraftText ?? "", snapshot.DraftMentions, snapshot.DraftFiles);
        foreach (var turn in snapshot.Turns)
        {
            if (turn.Files is null) throw new InvalidDataException("Некорректные файловые вложения сообщения.");
            FileAttachments.Validate(turn.Files);
            if (turn.Mentions is null) throw new InvalidDataException("Некорректные инлайн-вложения сообщения.");
            FileMention.Validate(turn.Text, turn.Mentions, turn.Files);
        }
        var pendingPath = Path.Combine(directory, "pending.json");
        RejectLink(pendingPath);
        if (File.Exists(pendingPath))
        {
            await using var pendingStream = new FileStream(pendingPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (pendingStream.Length > MaxSnapshotBytes)
                throw new InvalidDataException("Очередь превышает лимит хранения.");
            snapshot.PendingMessages = await JsonSerializer.DeserializeAsync<PendingMessage[]>(pendingStream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Очередь сообщений пуста или повреждена.");
        }
        if (snapshot.PendingMessages is null || snapshot.PendingMessages.Length > 32 ||
            snapshot.PendingMessages.Any(item => item is null || item.Text is null || item.Images is null ||
                !Guid.TryParseExact(item.ProviderId, "D", out _) || string.IsNullOrWhiteSpace(item.ModelId) || string.IsNullOrWhiteSpace(item.ReasoningEffort) ||
                !Enum.IsDefined(item.Mode) || item.Images.Length > ImageAttachments.MaxAttachments ||
                item.Images.Any(image => image is null || image.Data is null || image.Name is null || image.Data.Length > ImageAttachments.MaxImageBytes) ||
                item.Images.Sum(image => (long)image.Data.Length) > ImageAttachments.MaxTotalBytes) ||
            snapshot.PendingMessages.Sum(item => item.Images.Sum(image => (long)image.Data.Length)) > 96L * 1024 * 1024)
            throw new InvalidDataException("Некорректная очередь сообщений.");
        foreach (var item in snapshot.PendingMessages)
        {
            if (item.Files is null) throw new InvalidDataException("Некорректные файловые вложения очереди.");
            FileAttachments.Validate(item.Files);
            if (item.Mentions is null) throw new InvalidDataException("Некорректные инлайн-вложения очереди.");
            FileMention.Validate(item.Text, item.Mentions, item.Files);
        }
        return snapshot;
    }

    public async Task<(List<SessionSummary> Sessions, List<string> Errors)> ListAsync(
        string workspaceRoot, CancellationToken cancellationToken)
    {
        ValidatePath(_root);
        var sessions = new List<SessionSummary>();
        var errors = new List<string>();
        if (!Directory.Exists(_root)) return (sessions, errors);
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "D", out _)) continue;
            try
            {
                var snapshot = await LoadAsync(id, cancellationToken);
                if (snapshot.WorkspaceRoot == workspaceRoot)
                    sessions.Add(new SessionSummary(id, snapshot.Title, snapshot.UpdatedAt, snapshot.Turns.Count));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                errors.Add($"{id}: {error.Message}");
            }
        }
        return (sessions.OrderByDescending(session => session.UpdatedAt).ToList(), errors);
    }

    private static string ValidateId(string id) => Guid.TryParseExact(id, "D", out _)
        ? id : throw new InvalidDataException("Некорректный идентификатор сессии.");

    private static void ValidateDirectory(string path)
    {
        ValidatePath(path);
        if (Directory.Exists(path)) return;
        // Create private application directories, without changing permissions of existing parents.
        var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) ValidateDirectory(parent);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void ValidatePath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            RejectLink(current);
    }

    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException($"Символические ссылки в пути хранения сессий запрещены: {path}");
    }
}
