using System.IO;
using System.Text;

namespace HostDeck.App.Services;

/// <summary>
/// Schlankes Fehlerprotokoll unter %LOCALAPPDATA%\HostDeck\logs (14 Tage). Enthält nur Fehler/Ausnahmen –
/// niemals Passwörter oder Terminal-Inhalte.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HostDeck", "logs");

    public static void Error(string context, Exception ex) => Write("FEHLER", $"{context}: {ex}");
    public static void Info(string message) => Write("INFO", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var file = Path.Combine(Directory, $"hostdeck-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Logging darf nie selbst abstürzen */ }
    }

    public static void Cleanup()
    {
        try
        {
            foreach (var f in new DirectoryInfo(Directory).EnumerateFiles("hostdeck-*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-14)))
                f.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
