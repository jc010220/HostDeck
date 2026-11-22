namespace HostDeck.Tests.Storage;

public class BackupNamingTests
{
    [Fact]
    public void Automatic_and_manual_backup_names_never_collide()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "HostDeck-Backup_20261122.hdk"), "manual");
            File.WriteAllText(Path.Combine(dir, "HostDeck-Backup_20261121-090000.hdk"), "auto 1.0.23");
            File.WriteAllText(Path.Combine(dir, $"{HostDeck.Storage.EncryptedConnectionStore.BackupPrefix}20261122-090000.hdk"), "auto");
            string[] patterns = [$"{HostDeck.Storage.EncryptedConnectionStore.BackupPrefix}*.hdk", "HostDeck-Backup_????????-??????.hdk", "HostDeck-Sicherung-*.hdk"];
            var automatic = patterns.SelectMany(p => Directory.EnumerateFiles(dir, p)).Select(Path.GetFileName).ToList();
            Assert.Equal(2, automatic.Count);
            Assert.DoesNotContain("HostDeck-Backup_20261122.hdk", automatic);
        }
        finally { Directory.Delete(dir, true); }
    }
}
