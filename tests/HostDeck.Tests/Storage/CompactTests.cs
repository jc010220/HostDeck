using HostDeck.Storage;

namespace HostDeck.Tests.Storage;

public sealed class CompactTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("hostdeck-compact-");
    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void Large_freed_space_is_given_back()
    {
        var db = Path.Combine(_dir.FullName, "c.hdk");
        using var store = EncryptedConnectionStore.Create(db, "Korrekt-Pferd-Batterie", KdfParameters.FastForTests);
        var big = new string('x', 6 * 1024 * 1024);
        store.WriteDocument("big", big);
        store.WriteDocument("big", "{}");
        Assert.True(store.CompactIfFragmented());
        Assert.False(store.CompactIfFragmented());
        Assert.Equal("{}", store.ReadDocument("big"));
    }

    [Fact]
    public void Compacted_database_stays_encrypted()
    {
        var db = Path.Combine(_dir.FullName, "e.hdk");
        using (var store = EncryptedConnectionStore.Create(db, "Korrekt-Pferd-Batterie", KdfParameters.FastForTests))
        {
            store.WriteDocument("big", new string('y', 4 * 1024 * 1024));
            store.WriteDocument("marker", "\"SECRET-MARKER-4711\"");
            store.WriteDocument("big", "{}");
            Assert.True(store.CompactIfFragmented());
        }
        foreach (var file in Directory.EnumerateFiles(_dir.FullName))
            Assert.DoesNotContain("SECRET-MARKER-4711", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(file)));
        using (var reopened = EncryptedConnectionStore.Open(db, "Korrekt-Pferd-Batterie"))
            Assert.Contains("SECRET-MARKER-4711", reopened.ReadDocument("marker"));
        Assert.Throws<WrongMasterPasswordException>(() => EncryptedConnectionStore.Open(db, "Falsches-Passwort-123"));
    }
}
