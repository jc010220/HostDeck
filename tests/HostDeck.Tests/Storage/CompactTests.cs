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
}
