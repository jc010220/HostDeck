using static HostDeck.Tests.Presentation.MainViewModelTests;

namespace HostDeck.Tests.Presentation;

public class BackupNameTests
{
    [Fact]
    public void Backup_suggests_name_with_date_and_uses_the_chosen_file()
    {
        var (vm, dialogs, _, store) = Create();
        dialogs.SaveFileAnswer = @"C:\Backups\HostDeck-Personal-Backup_20261122.hdk";
        vm.BackupCommand.Execute(null);
        Assert.Equal($"HostDeck-Personal-Backup_{DateTime.Now:yyyyMMdd}.hdk", dialogs.LastSuggestedFileName);
        Assert.Contains(@"C:\Backups\HostDeck-Personal-Backup_20261122.hdk", store.Backups);
    }
}
