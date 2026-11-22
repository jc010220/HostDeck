using System.IO;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.ViewModels;
using HostDeck.Storage;

namespace HostDeck.App.Services;

/// <summary>
/// Automatische Sicherungen: einmal täglich (beim Start und stündlich geprüft, solange HostDeck läuft) eine
/// verschlüsselte Kopie von Datenbank + Schlüsseldatei; danach Aufräumen nach Großvater-Vater-Sohn-Regel.
/// Gelöscht werden ausschließlich von HostDeck erzeugte Sicherungen (fester Dateinamen-Präfix) – nie fremde Dateien.
/// </summary>
public sealed class BackupService(MainViewModel vm, IConnectionStore store, string defaultFolder) : IBackupManager, IDisposable
{
    // aktuelle + frühere Namen automatischer Sicherungen (1.0.23: „HostDeck-Backup_yyyyMMdd-HHmmss“, davor deutsch).
    // Manuelle Sicherungen („HostDeck-Backup_yyyyMMdd.hdk“, ohne Uhrzeit) passen auf keines dieser Muster.
    private static readonly string[] Patterns =
    [
        $"{EncryptedConnectionStore.BackupPrefix}*.hdk",
        "HostDeck-Backup_????????-??????.hdk",
        "HostDeck-Sicherung-*.hdk",
    ];
    private Timer? _timer;

    public string Folder => string.IsNullOrWhiteSpace(vm.Settings.BackupFolder) ? defaultFolder : vm.Settings.BackupFolder!.Trim();

    public void Start()
    {
        RunIfDue();
        _timer = new Timer(_ => RunIfDue(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    private void RunIfDue()
    {
        try
        {
            if (!vm.Settings.AutoBackup || store.CountConnections() == 0) return; // keine leeren Sicherungen anhäufen
            var latest = List().Select(b => b.Time).DefaultIfEmpty(DateTime.MinValue).Max();
            if (DateTime.Now - latest < TimeSpan.FromHours(23)) return;
            BackupNow();
        }
        catch (Exception ex)
        {
            Log.Error("Automatic backup", ex);
        }
    }

    /// <summary>Sofort sichern und aufräumen; liefert den Pfad der neuen Sicherung.</summary>
    public string BackupNow()
    {
        var file = store.Backup(Folder);
        Log.Info($"Backup: {file}");
        ApplyRetention();
        return file;
    }

    public void ApplyRetention()
    {
        var backups = List();
        var keep = BackupRetention.SelectToKeep(backups, Policy);
        foreach (var (path, _) in backups.Where(b => !keep.Contains(b.Path)))
        {
            try
            {
                File.Delete(path);
                File.Delete(path + ".key");
                Log.Info($"Backup removed by retention policy: {path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Remove old backup", ex);
            }
        }
    }

    public BackupRetentionPolicy Policy => new(vm.Settings.BackupKeepDaily, vm.Settings.BackupKeepWeekly, vm.Settings.BackupKeepMonthly);

    public List<(string Path, DateTime Time)> List()
    {
        if (!Directory.Exists(Folder)) return [];
        return Patterns.SelectMany(p => Directory.EnumerateFiles(Folder, p))
                       .Select(p => (p, File.GetLastWriteTime(p))).ToList();
    }

    /// <summary>Kurzbeschreibung für die Einstellungen („12 backups, newest 22.11.2026 09:15“).</summary>
    public string Summary()
    {
        var list = List();
        return list.Count == 0 ? "No automatic backups yet."
            : $"{list.Count} backup(s), newest {list.Max(b => b.Time):g}, oldest {list.Min(b => b.Time):d}.";
    }

    public void Dispose() => _timer?.Dispose();
}
