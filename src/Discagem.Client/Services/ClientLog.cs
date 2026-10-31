using System.Globalization;

namespace Discagem.Client.Services;

/// <summary>
/// A small log of what went wrong (and how media was set up), kept next to the settings as
/// <c>discagem.log</c> so that problems on someone else's computer can be diagnosed. It is capped at
/// about 1 MB; the previous log is kept as <c>discagem.old.log</c>.
/// </summary>
public static class ClientLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Sync = new();

    public static string FilePath => Path.Combine(ClientSettings.Directory, "discagem.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warning(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(ClientSettings.Directory);
                var path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, Path.Combine(ClientSettings.Directory, "discagem.old.log"), overwrite: true);
                }

                var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
                File.AppendAllText(path, $"{timestamp} {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never break the app.
        }
    }
}
