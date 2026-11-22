using HostDeck.Presentation.Services;

namespace HostDeck.Tests.Presentation;

public class BackupRetentionTests
{
    private static List<(string, DateTime)> Daily(DateTime newest, int days) =>
        Enumerable.Range(0, days).Select(i => ($"b{i:000}", newest.AddDays(-i))).ToList();

    [Fact]
    public void Default_policy_keeps_7_daily_4_weekly_12_monthly()
    {
        var backups = Daily(new DateTime(2026, 11, 22, 9, 0, 0), 400); // ein Jahr+ tägliche Sicherungen
        var keep = BackupRetention.SelectToKeep(backups, BackupRetentionPolicy.Default);

        Assert.All(Enumerable.Range(0, 7), i => Assert.Contains($"b{i:000}", keep)); // letzte 7 Tage
        Assert.InRange(keep.Count, 12, 7 + 4 + 12);                                   // Überschneidungen zählen einmal
        var kept = backups.Where(b => keep.Contains(b.Item1)).Select(b => b.Item2).ToList();
        Assert.True(kept.Min() > new DateTime(2026, 11, 22).AddMonths(-12).AddDays(-31)); // nichts älter als ~12 Monate
        Assert.True(kept.Min() < new DateTime(2026, 11, 22).AddMonths(-10));              // aber Monatsstände reichen weit zurück
    }

    [Fact]
    public void Newest_backup_is_always_kept_even_with_zero_retention()
    {
        var backups = Daily(DateTime.Now, 5);
        var keep = BackupRetention.SelectToKeep(backups, new BackupRetentionPolicy(0, 0, 0));
        Assert.Equal(["b000"], keep);
    }

    [Fact]
    public void Gaps_do_not_shrink_retention_periods_count_only_existing_backups()
    {
        // nur alle 10 Tage eine Sicherung (z. B. Laptop selten an) ⇒ trotzdem 7 Tages-Stände
        var backups = Enumerable.Range(0, 20).Select(i => ($"b{i}", new DateTime(2026, 11, 22).AddDays(-10 * i))).ToList();
        var keep = BackupRetention.SelectToKeep(backups, new BackupRetentionPolicy(7, 0, 0));
        Assert.Equal(7, keep.Count);
    }

    [Fact]
    public void Several_backups_per_day_keep_only_the_newest_of_that_day()
    {
        var day = new DateTime(2026, 11, 22);
        var backups = new List<(string, DateTime)> { ("morning", day.AddHours(8)), ("evening", day.AddHours(20)), ("yesterday", day.AddDays(-1)) };
        var keep = BackupRetention.SelectToKeep(backups, new BackupRetentionPolicy(7, 0, 0));
        Assert.Equal(["evening", "yesterday"], keep.Order());
    }
}
