using System.Globalization;
using System.Text.Json;

namespace HostDeck.Presentation.Team;

/// <summary>Team-weite Sicherungsregel – liegt als <c>policy.json</c> im Sicherungsordner neben der Team-Datei.</summary>
public sealed class TeamBackupPolicy
{
    public bool Enabled { get; set; }
    public int KeepDaily { get; set; } = 14;
    public int KeepWeekly { get; set; } = 8;
    public int KeepMonthly { get; set; } = 12;
    /// <summary>Jede gespeicherte Version so viele Tage aufheben (0 = aus). Danach greifen die Tages-/Wochen-/Monatsstände.</summary>
    public int KeepVersionsDays { get; set; } = 30;
    public DateTime? ChangedUtc { get; set; }
    public string? ChangedBy { get; set; }
}

/// <summary>
/// Sicherungen der Team-Bibliothek im selben Ordner wie die Team-Datei:
/// <code>
/// HostDeck-Backups\
///   policy.json                       (gilt für das ganze Team)
///   daily\   confCons_2026-11-22.xml   (+ confCons.hostdeck-snippets_2026-11-22.json)
///   weekly\  confCons_2026-W47.xml
///   monthly\ confCons_2026-11.xml
/// </code>
/// Großvater-Vater-Sohn über Unterordner: die erste Sicherung eines Tages/einer Woche/eines Monats landet im jeweiligen
/// Ordner, je Ordner bleiben die neuesten N. Wer im Team zuerst HostDeck öffnet, erstellt die Tagessicherung – die anderen
/// sehen, dass sie existiert. Die Dateien sind 1:1-Kopien (mit Team-Passwort verschlüsselt, falls gesetzt).
/// </summary>
public static class TeamBackup
{
    public const string FolderName = "HostDeck-Backups";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FolderFor(string teamFile) => Path.Combine(Path.GetDirectoryName(teamFile) ?? ".", FolderName);

    public static TeamBackupPolicy ReadPolicy(string teamFile)
    {
        var path = Path.Combine(FolderFor(teamFile), "policy.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<TeamBackupPolicy>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    public static void WritePolicy(string teamFile, TeamBackupPolicy policy)
    {
        var folder = FolderFor(teamFile);
        Directory.CreateDirectory(folder);
        policy.ChangedUtc = DateTime.UtcNow;
        policy.ChangedBy = Environment.UserName;
        var path = Path.Combine(folder, "policy.json");
        File.WriteAllText(path + ".hostdeck-tmp", JsonSerializer.Serialize(policy, Json));
        File.Move(path + ".hostdeck-tmp", path, overwrite: true);
    }

    /// <summary>Tages-/Wochen-/Monatssicherung anlegen, falls für heute noch keine existiert; danach aufräumen.</summary>
    /// <returns>true, wenn eine neue Sicherung erstellt wurde.</returns>
    public static bool RunIfDue(string teamFile, DateTime now, bool force = false)
    {
        var policy = ReadPolicy(teamFile);
        if (!force && !policy.Enabled) return false;
        if (!File.Exists(teamFile)) return false;

        var folder = FolderFor(teamFile);
        var stem = Path.GetFileNameWithoutExtension(teamFile);
        var periods = new (string Sub, string Stamp)[]
        {
            ("daily", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("weekly", $"{ISOWeek.GetYear(now)}-W{ISOWeek.GetWeekOfYear(now):00}"),
            ("monthly", now.ToString("yyyy-MM", CultureInfo.InvariantCulture)),
        };
        if (!force && File.Exists(Path.Combine(folder, "daily", $"{stem}_{periods[0].Stamp}.xml"))) return false;

        Directory.CreateDirectory(folder);
        using (TeamLibraryFile.AcquireLock(Path.Combine(folder, "backup"))) // nur ein Teammitglied sichert gleichzeitig
        {
            var snippets = TeamSnippetFile.PathFor(teamFile);
            var created = false;
            foreach (var (sub, stamp) in periods)
            {
                var target = Path.Combine(folder, sub, $"{stem}_{stamp}.xml");
                if (File.Exists(target) && !(force && sub == "daily")) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, TeamLibraryFile.ReadShared(teamFile));
                if (File.Exists(snippets))
                    File.WriteAllBytes(Path.Combine(folder, sub, $"{Path.GetFileNameWithoutExtension(snippets)}_{stamp}.json"), TeamLibraryFile.ReadShared(snippets));
                created = true;
            }
            ApplyRetention(folder, stem, policy);
            return created;
        }
    }

    // ---- Versionsverlauf: jede Speicherung ----------------------------------------------------------
    // Wie der Versionsverlauf von SharePoint/OneDrive: Nach jedem Speichern (Strg+S) liegt der neue Stand als eigene Datei
    // in „versions“ – mit Zeitpunkt und wer gespeichert hat. Aufgehoben werden alle Versionen der letzten N Tage (mindestens
    // die letzten 20, höchstens 1000); ältere Stände decken die Tages-/Wochen-/Monatssicherungen ab.

    public const int MinVersions = 20, MaxVersions = 1000;

    /// <summary>
    /// Nach einem erfolgreichen Speichern: aktuellen Stand der Datei als Version ablegen (beim ersten Mal zusätzlich den
    /// Stand davor). Liefert den Pfad der neuen Version oder null, wenn Versionen aus sind.
    /// </summary>
    public static string? SaveVersion(string teamFile, byte[]? previous, DateTime now)
    {
        var policy = ReadPolicy(teamFile);
        if (!policy.Enabled || policy.KeepVersionsDays <= 0 || !File.Exists(teamFile)) return null;
        var dir = Path.Combine(FolderFor(teamFile), "versions");
        Directory.CreateDirectory(dir);
        var stem = Path.GetFileNameWithoutExtension(teamFile);
        var user = new string(Environment.UserName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').Take(32).ToArray());
        if (user.Length == 0) user = "user";
        if (previous is { Length: > 0 } && !Directory.EnumerateFiles(dir, $"{stem}_*.xml").Any())
            File.WriteAllBytes(Path.Combine(dir, $"{stem}_{now.AddSeconds(-1):yyyyMMdd-HHmmss}_before.xml"), previous);
        var target = Path.Combine(dir, $"{stem}_{now:yyyyMMdd-HHmmss}_{user}.xml");
        for (var i = 2; File.Exists(target); i++) target = Path.Combine(dir, $"{stem}_{now:yyyyMMdd-HHmmss}_{user}_{i}.xml");
        File.WriteAllBytes(target, TeamLibraryFile.ReadShared(teamFile));
        ApplyVersionRetention(dir, stem, policy.KeepVersionsDays, now);
        return target;
    }

    public static void ApplyVersionRetention(string dir, string stem, int keepDays, DateTime now)
    {
        // Dateinamen beginnen mit yyyyMMdd-HHmmss ⇒ lexikografisch = zeitlich sortiert
        var files = Directory.EnumerateFiles(dir, $"{stem}_*.xml").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        var cutoff = now.AddDays(-keepDays).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (var i = 0; i < files.Count; i++)
        {
            var stamp = Path.GetFileName(files[i])[(stem.Length + 1)..];
            var tooOld = string.CompareOrdinal(stamp, cutoff) < 0;
            if (i >= MaxVersions || (i >= MinVersions && tooOld)) TryDelete(files[i]);
        }
    }

    public static int VersionCount(string teamFile)
    {
        var dir = Path.Combine(FolderFor(teamFile), "versions");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, $"{Path.GetFileNameWithoutExtension(teamFile)}_*.xml").Count() : 0;
    }

    /// <summary>Je Unterordner die neuesten N behalten (nur von HostDeck angelegte Dateien dieser Team-Datei).</summary>
    public static void ApplyRetention(string folder, string stem, TeamBackupPolicy policy)
    {
        foreach (var (sub, keep) in new[] { ("daily", policy.KeepDaily), ("weekly", policy.KeepWeekly), ("monthly", policy.KeepMonthly) })
        {
            var dir = Path.Combine(folder, sub);
            if (!Directory.Exists(dir)) continue;
            // Datei-Stamps sortieren lexikografisch korrekt (yyyy-MM-dd, yyyy-Www, yyyy-MM)
            var stamps = Directory.EnumerateFiles(dir, $"{stem}_*.xml")
                .Select(f => Path.GetFileNameWithoutExtension(f)[(stem.Length + 1)..])
                .OrderByDescending(s => s, StringComparer.Ordinal).ToList();
            foreach (var stamp in stamps.Skip(Math.Max(keep, 1))) // die neueste bleibt immer
            {
                TryDelete(Path.Combine(dir, $"{stem}_{stamp}.xml"));
                TryDelete(Path.Combine(dir, $"{stem}.hostdeck-snippets_{stamp}.json"));
            }
        }
    }

    public static string Summary(string teamFile)
    {
        var folder = FolderFor(teamFile);
        var stem = Path.GetFileNameWithoutExtension(teamFile);
        int Count(string sub) => Directory.Exists(Path.Combine(folder, sub)) ? Directory.EnumerateFiles(Path.Combine(folder, sub), $"{stem}_*.xml").Count() : 0;
        var policy = ReadPolicy(teamFile);
        var by = policy.ChangedBy is { } who ? $" Policy last changed by {who} ({policy.ChangedUtc?.ToLocalTime():g})." : "";
        return $"{VersionCount(teamFile)} saved version(s), {Count("daily")} daily, {Count("weekly")} weekly, {Count("monthly")} monthly backup(s) in {folder}.{by}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
