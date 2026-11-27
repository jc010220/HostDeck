using HostDeck.Presentation.Team;

namespace HostDeck.Tests.Presentation;

public sealed class TeamVersionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hostdeck-versions-").FullName;
    private string TeamFile => Path.Combine(_dir, "team.xml");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Every_save_creates_a_version_when_team_backups_are_on()
    {
        File.WriteAllText(TeamFile, "<v1/>");
        Assert.Null(TeamBackup.SaveVersion(TeamFile, null, DateTime.Now));            // Regel aus ⇒ nichts
        TeamBackup.WritePolicy(TeamFile, new TeamBackupPolicy { Enabled = true, KeepVersionsDays = 30 });

        var t = new DateTime(2026, 11, 27, 10, 0, 0);
        File.WriteAllText(TeamFile, "<v2/>");
        Assert.NotNull(TeamBackup.SaveVersion(TeamFile, "<v1/>"u8.ToArray(), t));
        File.WriteAllText(TeamFile, "<v3/>");
        Assert.NotNull(TeamBackup.SaveVersion(TeamFile, "<v2/>"u8.ToArray(), t.AddMinutes(5)));
        var files = Directory.GetFiles(Path.Combine(_dir, TeamBackup.FolderName, "versions")).Select(File.ReadAllText).ToList();
        Assert.Equal(3, files.Count);                                                  // Stand davor + 2 Speicherungen
        Assert.Contains("<v1/>", files);
        Assert.Contains("<v3/>", files);
    }

    [Fact]
    public void Old_versions_are_removed_but_the_last_20_always_stay()
    {
        var dir = Path.Combine(_dir, "versions");
        Directory.CreateDirectory(dir);
        var now = new DateTime(2026, 11, 27, 12, 0, 0);
        for (var i = 0; i < 30; i++) File.WriteAllText(Path.Combine(dir, $"team_{now.AddDays(-100 - i):yyyyMMdd-HHmmss}_u.xml"), "x");
        for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(dir, $"team_{now.AddHours(-i):yyyyMMdd-HHmmss}_u.xml"), "x");
        TeamBackup.ApplyVersionRetention(dir, "team", keepDays: 30, now);
        Assert.Equal(TeamBackup.MinVersions, Directory.GetFiles(dir).Length);           // 5 neue + 15 der alten
    }
}
