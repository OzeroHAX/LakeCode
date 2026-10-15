using System.Text.Encodings.Web;
using System.Text.Json;
using LakeCode.Core.Attachments;

namespace LakeCode.Core.Sessions;

/// <summary>Application-owned snapshots, separate from the agent's workspace file tools.</summary>
internal sealed class SessionStorage
{
    private const long MaxSnapshotBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
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
        var path = Path.Combine(directory, "session.json");
        await WriteAtomicAsync(path, snapshot, cancellationToken);
        await SaveSummaryAsync(directory, snapshot, new FileInfo(path), cancellationToken);
    }

    public async Task SavePendingAsync(string id, PendingMessage[] messages)
    {
        ValidateDirectory(_root);
        var directory = Path.Combine(_root, ValidateId(id));
        ValidateDirectory(directory);
        // Versioned envelope prevents older clients from silently restoring Plan requests as Build.
        await WriteAtomicAsync(Path.Combine(directory, "pending.json"),
            new { SchemaVersion = 4, Messages = messages }, CancellationToken.None);
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
        if (snapshot.SchemaVersion is not (1 or 2 or 3 or 4) || snapshot.FrameworkVersion != "1.24.0")
            throw new InvalidDataException("Версия сохранённой сессии несовместима с этой версией LakeCode.");
        if (snapshot.Id != id || snapshot.Turns is null || string.IsNullOrWhiteSpace(snapshot.WorkspaceRoot) ||
            string.IsNullOrWhiteSpace(snapshot.Title) || string.IsNullOrWhiteSpace(snapshot.ModelId) ||
            string.IsNullOrWhiteSpace(snapshot.ReasoningEffort) || !Guid.TryParseExact(snapshot.ProviderId, "D", out _))
            throw new InvalidDataException("Некорректные данные сессии.");
        foreach (var turn in snapshot.Turns)
        {
            if (turn is null || !Enum.IsDefined(turn.AgentMode) || turn.Text is null || turn.Images is null || turn.Events is null ||
                turn.Status is not ("in_progress" or "completed" or "failed" or "interrupted") ||
                turn.Images.Any(image => image is null || image.Data is null || image.Name is null))
                throw new InvalidDataException("Некорректные данные сообщения в сессии.");
            if (turn.Plan is { } plan && (plan.Id == Guid.Empty || plan.Revision < 1 ||
                string.IsNullOrWhiteSpace(plan.Markdown) || turn.AgentMode != Agent.AgentMode.Plan))
                throw new InvalidDataException("Некорректный план в сессии.");
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
            using var pendingDocument = await JsonDocument.ParseAsync(pendingStream, cancellationToken: cancellationToken);
            var pendingRoot = pendingDocument.RootElement;
            if (pendingRoot.ValueKind == JsonValueKind.Object)
            {
                if (!pendingRoot.TryGetProperty("SchemaVersion", out var version) || !version.TryGetInt32(out var number) || number != 4 ||
                    !pendingRoot.TryGetProperty("Messages", out pendingRoot))
                    throw new InvalidDataException("Неподдерживаемая версия очереди сообщений.");
            }
            snapshot.PendingMessages = pendingRoot.Deserialize<PendingMessage[]>(JsonOptions)
                ?? throw new InvalidDataException("Очередь сообщений пуста или повреждена.");
        }
        if (snapshot.PendingMessages is null || snapshot.PendingMessages.Length > 32 ||
            snapshot.PendingMessages.Any(item => item is null || item.Text is null || item.Images is null ||
                !Guid.TryParseExact(item.ProviderId, "D", out _) || string.IsNullOrWhiteSpace(item.ModelId) || string.IsNullOrWhiteSpace(item.ReasoningEffort) ||
                !Enum.IsDefined(item.Mode) || !Enum.IsDefined(item.AgentMode) || item.Images.Length > ImageAttachments.MaxAttachments ||
                item.Images.Any(image => image is null || image.Data is null || image.Name is null || image.Data.Length > ImageAttachments.MaxImageBytes) ||
                item.Images.Sum(image => (long)image.Data.Length) > ImageAttachments.MaxTotalBytes) ||
            snapshot.PendingMessages.Sum(item => item.Images.Sum(image => (long)image.Data.Length)) > 96L * 1024 * 1024)
            throw new InvalidDataException("Некорректная очередь сообщений.");
        foreach (var item in snapshot.PendingMessages)
        {
            item.SubagentModel?.Validate();
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
                var summary = await LoadSummaryAsync(directory, id, errors, cancellationToken);
                if (summary.WorkspaceRoot == workspaceRoot)
                    sessions.Add(new SessionSummary(id, summary.Title, summary.UpdatedAt, summary.Turns));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                errors.Add($"{id}: {error.Message}");
            }
        }
        return (sessions.OrderByDescending(session => session.UpdatedAt).ToList(), errors);
    }

    // Recoverable metadata only: the full snapshot remains the source of truth.
    private sealed record StoredSummary(int Version, int SchemaVersion, string FrameworkVersion,
        string Id, string WorkspaceRoot, string Title, DateTimeOffset UpdatedAt, int Turns,
        long SnapshotLength, long SnapshotWriteTicks);

    private static Task SaveSummaryAsync(string directory, SessionSnapshot snapshot, FileInfo file,
        CancellationToken cancellationToken) => WriteAtomicAsync(Path.Combine(directory, "summary.json"),
        new StoredSummary(1, snapshot.SchemaVersion, snapshot.FrameworkVersion, snapshot.Id,
            snapshot.WorkspaceRoot, snapshot.Title, snapshot.UpdatedAt, snapshot.Turns.Count,
            file.Length, file.LastWriteTimeUtc.Ticks), cancellationToken);

    private async Task<StoredSummary> LoadSummaryAsync(string directory, string id, List<string> errors,
        CancellationToken cancellationToken)
    {
        ValidatePath(directory);
        var snapshotPath = Path.Combine(directory, "session.json");
        RejectLink(snapshotPath);
        var file = new FileInfo(snapshotPath);
        var length = file.Length;
        var writeTicks = file.LastWriteTimeUtc.Ticks;
        if (length > MaxSnapshotBytes)
            throw new InvalidDataException("Сессия превышает лимит хранения 256 MiB.");
        var summaryPath = Path.Combine(directory, "summary.json");
        RejectLink(summaryPath);
        try
        {
            await using var stream = new FileStream(summaryPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 64 * 1024)
            {
                var summary = await JsonSerializer.DeserializeAsync<StoredSummary>(stream, JsonOptions, cancellationToken);
                if (summary is { Version: 1, SchemaVersion: 1 or 2 or 3 or 4, FrameworkVersion: "1.24.0" } &&
                    summary.Id == id && !string.IsNullOrWhiteSpace(summary.WorkspaceRoot) &&
                    !string.IsNullOrWhiteSpace(summary.Title) && summary.Turns >= 0 &&
                    summary.SnapshotLength == length && summary.SnapshotWriteTicks == writeTicks)
                    return summary;
            }
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            // Missing, stale or damaged metadata is rebuilt once from the existing snapshot.
        }

        var snapshot = await LoadAsync(id, cancellationToken);
        file.Refresh();
        if (file.Length != length || file.LastWriteTimeUtc.Ticks != writeTicks)
            throw new IOException("Сессия изменилась во время чтения; обновите список.");
        var rebuilt = new StoredSummary(1, snapshot.SchemaVersion, snapshot.FrameworkVersion, id,
            snapshot.WorkspaceRoot, snapshot.Title, snapshot.UpdatedAt, snapshot.Turns.Count, length, writeTicks);
        try
        {
            await WriteAtomicAsync(summaryPath, rebuilt, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{id}: не удалось сохранить метаданные списка: {error.Message}");
        }
        return rebuilt;
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
