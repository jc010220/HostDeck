using System.Globalization;

namespace HostDeck.Presentation.Services;

/// <summary>Aufbewahrung nach dem Großvater-Vater-Sohn-Prinzip (wie restic/Borg „keep-daily/weekly/monthly“).</summary>
public sealed record BackupRetentionPolicy(int KeepDaily = 7, int KeepWeekly = 4, int KeepMonthly = 12)
{
    public static BackupRetentionPolicy Default { get; } = new();
}

/// <summary>Automatische Sicherungen (in der App: BackupService).</summary>
public interface IBackupManager
{
    string Folder { get; }
    string BackupNow();
    void ApplyRetention();
    string Summary();
}

public static class BackupRetention
{
    /// <summary>
    /// Welche Sicherungen bleiben? Je Regel wird pro Zeitraum (Tag, ISO-Kalenderwoche, Monat) die jeweils
    /// neueste Sicherung behalten, für die letzten N Zeiträume, in denen es Sicherungen gibt. Eine Sicherung kann
    /// mehrere Regeln erfüllen. Die allerneueste bleibt immer – egal wie die Regeln eingestellt sind.
    /// </summary>
    public static HashSet<string> SelectToKeep(IEnumerable<(string Path, DateTime Time)> backups, BackupRetentionPolicy policy)
    {
        var ordered = backups.OrderByDescending(b => b.Time).ToList();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ordered.Count == 0) return keep;
        keep.Add(ordered[0].Path);

        void Apply(int count, Func<DateTime, string> period)
        {
            if (count <= 0) return;
            var seen = new HashSet<string>();
            foreach (var (path, time) in ordered)
            {
                if (seen.Count >= count) break;
                if (seen.Add(period(time))) keep.Add(path); // erste (= neueste) Sicherung des Zeitraums
            }
        }

        Apply(policy.KeepDaily, t => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Apply(policy.KeepWeekly, t => $"{ISOWeek.GetYear(t)}-W{ISOWeek.GetWeekOfYear(t):00}");
        Apply(policy.KeepMonthly, t => t.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        return keep;
    }
}
