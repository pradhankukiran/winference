using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Winference.Core.Tools;

/// <summary>Read-only tools for local models. None of them change, delete or send anything.</summary>
public static class BuiltInTools
{
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxDirectoryEntries = 100;

    public static LocalTool GetCurrentDateTime() => new(
        Name: "get_current_datetime",
        Description: "Returns the current local date, time, day of the week and time zone.",
        Parameters: [],
        Handler: (_, _) =>
        {
            var now = DateTimeOffset.Now;
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                local = now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                dayOfWeek = now.DayOfWeek.ToString(),
                timeZone = TimeZoneInfo.Local.DisplayName
            }));
        },
        AsksFirst: false);

    public static LocalTool GetSystemStatus() => new(
        Name: "get_system_status",
        Description: "Returns the Windows version, processor architecture, processor count and memory in use.",
        Parameters: [],
        Handler: (_, _) =>
        {
            var memory = GC.GetGCMemoryInfo();
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                logicalProcessors = Environment.ProcessorCount,
                totalMemoryGb = Math.Round(memory.TotalAvailableMemoryBytes / 1073741824.0, 1),
                memoryInUseGb = Math.Round(memory.MemoryLoadBytes / 1073741824.0, 1),
                uptimeHours = Math.Round(Environment.TickCount64 / 3600000.0, 1)
            }));
        },
        AsksFirst: false);

    public static LocalTool ReadLocalFile() => new(
        Name: "read_local_file",
        Description: $"Reads a text file on this PC (up to {MaxFileBytes / 1024} KB).",
        Parameters: [new("path", "string", "Full path of the file, for example C:\\Users\\me\\notes.txt.", IsRequired: true)],
        Handler: async (args, token) =>
        {
            if (ResolvePath(args) is not { } path) return Error("Give the full path of the file.");
            if (!File.Exists(path)) return Error("File not found.", path);
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes) return Error($"The file is larger than {MaxFileBytes / 1024} KB.", path);
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (Array.IndexOf(bytes, (byte)0) >= 0) return Error("This is a binary file, not text.", path);
            return JsonSerializer.Serialize(new { path, content = new UTF8Encoding(false).GetString(bytes) });
        });

    public static LocalTool ListDirectory() => new(
        Name: "list_directory",
        Description: $"Lists the files and folders in a folder on this PC (up to {MaxDirectoryEntries} entries).",
        Parameters: [new("path", "string", "Full path of the folder, for example C:\\Users\\me\\Documents.", IsRequired: true)],
        Handler: (args, _) =>
        {
            if (ResolvePath(args) is not { } path) return Task.FromResult(Error("Give the full path of the folder."));
            if (!Directory.Exists(path)) return Task.FromResult(Error("Folder not found.", path));
            var directory = new DirectoryInfo(path);
            var entries = directory.EnumerateFileSystemInfos()
                .Where(entry => !entry.Attributes.HasFlag(FileAttributes.Hidden) && !entry.Attributes.HasFlag(FileAttributes.System))
                .Take(MaxDirectoryEntries + 1)
                .Select(entry => new
                {
                    name = entry.Name,
                    type = entry is DirectoryInfo ? "folder" : "file",
                    sizeBytes = entry is FileInfo file ? file.Length : (long?)null,
                    modified = entry.LastWriteTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                })
                .ToArray();
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                folder = path,
                entries = entries.Take(MaxDirectoryEntries),
                truncated = entries.Length > MaxDirectoryEntries
            }));
        });

    public static IReadOnlyList<LocalTool> GetAll() => [GetCurrentDateTime(), GetSystemStatus(), ReadLocalFile(), ListDirectory()];

    /// <summary>The path argument, with environment variables expanded. Relative paths are rejected.</summary>
    public static string? ResolvePath(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("path", out var value) ||
            value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } raw) return null;
        var expanded = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        if (expanded.StartsWith('~'))
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..];
        return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : null;
    }

    private static string Error(string message, string? path = null) =>
        JsonSerializer.Serialize(new { error = message, path });
}
