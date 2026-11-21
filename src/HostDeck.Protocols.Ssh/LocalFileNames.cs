namespace HostDeck.Protocols.Ssh;

/// <summary>
/// Dateinamen vom Server sind nicht vertrauenswürdig: Ein manipulierter Server könnte z. B.
/// <c>..\..\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\x.bat</c> liefern
/// (unter Linux ist „\“ ein gültiges Namenszeichen) und so Dateien außerhalb des Zielordners schreiben.
/// </summary>
public static class LocalFileNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Windows-Regeln unabhängig vom Betriebssystem, auf dem HostDeck läuft
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>Nur der letzte Namensteil, ohne Pfadtrenner, Steuerzeichen, reservierte Gerätenamen.</summary>
    public static string Sanitize(string remoteName)
    {
        var name = remoteName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var chars = name.Select(c => c < 32 || Invalid.Contains(c) ? '_' : c).ToArray();
        name = new string(chars).Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name is "." or "..") name = "download";
        var stem = name.Split('.')[0];
        if (Reserved.Contains(stem)) name = "_" + name;
        if (name.Length > 200) name = name[..200];
        return name;
    }

    /// <summary>Vorhandene Dateien nie still überschreiben: „datei (1).txt“, „datei (2).txt“ …</summary>
    public static string UniqueTarget(string directory, string fileName)
    {
        var target = Path.Combine(directory, fileName);
        var full = Path.GetFullPath(target);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Invalid file name from server: {fileName}");
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; File.Exists(target) || File.Exists(target + ".hostdeck-part"); i++)
            target = Path.Combine(directory, $"{stem} ({i}){ext}");
        return target;
    }

    /// <summary>
    /// „Aus dem Internet“-Markierung (Mark of the Web) setzen, damit Windows SmartScreen/Office beim Öffnen
    /// heruntergeladener Programme und Makros warnt – wie bei Browser-Downloads.
    /// </summary>
    public static void MarkAsDownloaded(string path, string source)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={source.Replace("\r", "").Replace("\n", "")}\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // z. B. FAT32-Stick ohne alternative Datenströme – Download bleibt gültig
        }
    }
}
