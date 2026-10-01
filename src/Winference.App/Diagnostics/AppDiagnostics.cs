using System.Text.Json;

namespace Winference.App.Diagnostics;

internal static class AppDiagnostics
{
    private static readonly object WriteLock = new();

    public static string LogsDirectory { get; } = Path.Combine(
        Winference.Infrastructure.Setup.SetupPaths.CreateDefault().DataRoot, "Logs");

    public static bool Write(string eventName, object? details = null)
    {
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(LogsDirectory);
                var path = Path.Combine(LogsDirectory, "shell.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 1_048_576)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    time = DateTimeOffset.UtcNow,
                    eventName,
                    details
                }) + Environment.NewLine);
                return true;
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool WriteException(string eventName, Exception exception) =>
        Write(eventName, new
        {
            type = exception.GetType().FullName,
            exception.Message,
            hresult = $"0x{exception.HResult:X8}",
            exception.StackTrace,
            innerType = exception.InnerException?.GetType().FullName,
            innerMessage = exception.InnerException?.Message,
            details = exception.Data.Keys.Cast<object>()
                .Where(key => exception.Data[key] is string)
                .ToDictionary(key => key.ToString() ?? "detail", key => (string)exception.Data[key]!)
        });
}
