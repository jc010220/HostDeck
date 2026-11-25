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

    private static readonly Dictionary<string, int> Repeats = [];

    /// <summary>
    /// Identische Fehler (gleicher Kontext, Typ, Meldung, Ort) werden nach 10 Einträgen nur noch zusammengefasst
    /// protokolliert – eine Fehlerschleife darf weder die Platte füllen noch die App ausbremsen.
    /// </summary>
    public static void Error(string context, Exception ex)
    {
        var key = $"{context}|{ex.GetType().FullName}|{ex.Message}|{ex.StackTrace?.Split('\n').FirstOrDefault()}";
        int count;
        lock (Gate)
        {
            Repeats.TryGetValue(key, out count);
            Repeats[key] = ++count;
        }
        if (count <= 10) Write("FEHLER", $"{context}: {ex}");
        else if (count % 1000 == 0) Write("FEHLER", $"{context}: {ex.GetType().Name}: {ex.Message} (bisher {count}× aufgetreten)");
    }
    /// <summary>Info-Zeilen enthalten auch Texte von Servern bzw. aus Bibliotheken ⇒ Steuerzeichen entschärfen (keine gefälschten Zeilen).</summary>
    public static void Info(string message) => Write("INFO", Sanitize(message));

    internal static string Sanitize(string message)
    {
        var clean = new string(message.Select(c => c is '\r' or '\n' ? ' ' : char.IsControl(c) ? '?' : c).ToArray());
        return clean.Length > 2000 ? clean[..2000] + " …" : clean;
    }

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
