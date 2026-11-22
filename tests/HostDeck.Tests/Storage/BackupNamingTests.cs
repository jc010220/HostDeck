namespace HostDeck.Tests.Storage;

public class BackupNamingTests
{
    [Fact]
    public void Automatic_and_manual_backup_names_never_collide()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "HostDeck-Backup_20261122.hdk"), "manual (1.0.23)");
            File.WriteAllText(Path.Combine(dir, "HostDeck-Personal-Backup_20261122.hdk"), "manual");
            File.WriteAllText(Path.Combine(dir, "HostDeck-Personal-BeforeRestore_20261122-100000.hdk"), "before restore");
            File.WriteAllText(Path.Combine(dir, "HostDeck-AutoBackup_20261120-090000.hdk"), "auto 1.0.24");
            File.WriteAllText(Path.Combine(dir, "HostDeck-Backup_20261121-090000.hdk"), "auto 1.0.23");
            File.WriteAllText(Path.Combine(dir, $"{HostDeck.Storage.EncryptedConnectionStore.BackupPrefix}20261122-090000.hdk"), "auto");
            string[] patterns = [$"{HostDeck.Storage.EncryptedConnectionStore.BackupPrefix}*.hdk", "HostDeck-AutoBackup_*.hdk", "HostDeck-Backup_????????-??????.hdk", "HostDeck-Sicherung-*.hdk"];
            var automatic = patterns.SelectMany(p => Directory.EnumerateFiles(dir, p)).Select(Path.GetFileName).ToList();
            Assert.Equal(3, automatic.Count);
            Assert.DoesNotContain("HostDeck-Backup_20261122.hdk", automatic);
            Assert.DoesNotContain("HostDeck-Personal-Backup_20261122.hdk", automatic);
            Assert.DoesNotContain("HostDeck-Personal-BeforeRestore_20261122-100000.hdk", automatic);
        }
        finally { Directory.Delete(dir, true); }
    }
}
