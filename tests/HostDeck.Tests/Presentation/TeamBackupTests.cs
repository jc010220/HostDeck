using HostDeck.Presentation.Team;

namespace HostDeck.Tests.Presentation;

public sealed class TeamBackupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hostdeck-teambk-").FullName;
    private string TeamFile => Path.Combine(_dir, "confCons.xml");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Sub(string sub) => Path.Combine(_dir, TeamBackup.FolderName, sub);

    [Fact]
    public void Disabled_by_default_and_policy_is_shared_next_to_the_team_file()
    {
        File.WriteAllText(TeamFile, "<xml/>");
        Assert.False(TeamBackup.RunIfDue(TeamFile, DateTime.Now));
        Assert.False(Directory.Exists(Path.Combine(_dir, TeamBackup.FolderName, "daily")));

        TeamBackup.WritePolicy(TeamFile, new TeamBackupPolicy { Enabled = true, KeepDaily = 3 });
        Assert.True(File.Exists(Path.Combine(_dir, TeamBackup.FolderName, "policy.json")));
        var read = TeamBackup.ReadPolicy(TeamFile);
        Assert.True(read.Enabled);
        Assert.Equal(3, read.KeepDaily);
        Assert.Equal(Environment.UserName, read.ChangedBy);
    }

    [Fact]
    public void Daily_weekly_monthly_subfolders_with_retention_per_folder()
    {
        File.WriteAllText(TeamFile, "<xml/>");
        File.WriteAllText(TeamSnippetFile.PathFor(TeamFile), "{}");
        TeamBackup.WritePolicy(TeamFile, new TeamBackupPolicy { Enabled = true, KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 3 });

        var start = new DateTime(2026, 1, 1, 9, 0, 0);
        for (var day = 0; day < 200; day++)
            Assert.True(TeamBackup.RunIfDue(TeamFile, start.AddDays(day)));
        Assert.False(TeamBackup.RunIfDue(TeamFile, start.AddDays(199).AddHours(5))); // heute schon gesichert (z. B. Kollege)

        Assert.Equal(7, Directory.GetFiles(Sub("daily"), "confCons_*.xml").Length);
        Assert.Equal(4, Directory.GetFiles(Sub("weekly"), "confCons_*.xml").Length);
        Assert.Equal(3, Directory.GetFiles(Sub("monthly"), "confCons_*.xml").Length);
        Assert.Equal(7, Directory.GetFiles(Sub("daily"), "confCons.hostdeck-snippets_*.json").Length); // Snippets mitgesichert
        Assert.True(File.Exists(Path.Combine(Sub("daily"), "confCons_2026-07-19.xml")));               // neueste bleibt
        Assert.True(File.Exists(Path.Combine(Sub("monthly"), "confCons_2026-07.xml")));
    }

    [Fact]
    public void Foreign_files_in_the_backup_folders_are_never_deleted()
    {
        File.WriteAllText(TeamFile, "<xml/>");
        TeamBackup.WritePolicy(TeamFile, new TeamBackupPolicy { Enabled = true, KeepDaily = 1, KeepWeekly = 1, KeepMonthly = 1 });
        Directory.CreateDirectory(Sub("daily"));
        File.WriteAllText(Path.Combine(Sub("daily"), "notes.txt"), "keep me");
        File.WriteAllText(Path.Combine(Sub("daily"), "other-team_2026-01-01.xml"), "keep me");
        for (var day = 0; day < 5; day++) TeamBackup.RunIfDue(TeamFile, new DateTime(2026, 3, 1).AddDays(day));
        Assert.True(File.Exists(Path.Combine(Sub("daily"), "notes.txt")));
        Assert.True(File.Exists(Path.Combine(Sub("daily"), "other-team_2026-01-01.xml")));
    }
}
