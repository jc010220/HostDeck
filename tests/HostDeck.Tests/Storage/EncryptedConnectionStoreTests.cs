using System.Text;
using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Storage;
using Konscious.Security.Cryptography;

namespace HostDeck.Tests.Storage;

public sealed class EncryptedConnectionStoreTests : IDisposable
{
    private const string Password = "Korrekt-Pferd-Batterie";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("hostdeck-store-");
    private string DbPath => Path.Combine(_dir.FullName, "connections.hdk");

    public void Dispose() => _dir.Delete(recursive: true);

    private EncryptedConnectionStore Create() => EncryptedConnectionStore.Create(DbPath, Password, KdfParameters.FastForTests);

    private static ConnectionTree FixtureTree() =>
        new ConfConsImporter(new ConfConsImportOptions { CredentialExtraction = CredentialExtractionMode.SharedOnly })
            .ImportFile(Fixture.PathOf("confCons_v2_6.xml")).Tree;

    [Fact]
    public void Roundtrip_preserves_tree_vault_inheritance_and_extensions()
    {
        var original = FixtureTree();
        original.Root.Settings.Protocol = ProtocolKind.Ssh2; // Benutzer-Defaults an der Wurzel
        using (var store = Create()) store.Save(original);

        using var reopened = EncryptedConnectionStore.Open(DbPath, Password);
        var loaded = reopened.Load();

        Assert.Equal(original.Root.Descendants().Select(n => (n.Path, n.Id, n.GetType(), n.Name)),
                     loaded.Root.Descendants().Select(n => (n.Path, n.Id, n.GetType(), n.Name)));
        Assert.Equal(ProtocolKind.Ssh2, loaded.Root.Settings.Protocol);
        Assert.Equal(original.Vault.Items.Select(c => (c.Id, c.Name, c.Password)).OrderBy(x => x.Id),
                     loaded.Vault.Items.Select(c => (c.Id, c.Name, c.Password)).OrderBy(x => x.Id));

        foreach (var (path, expected) in Fixture.ExpectedCredentials)
            Assert.Equal(expected, Fixture.EffectiveCredentials(loaded, path));

        var folder22 = loaded.FindByPath("Folder2/Folder2.2")!;
        Assert.Null(folder22.Settings.Username);   // "erben" bleibt "erben"
        foreach (var (a, b) in original.Root.Descendants().Zip(loaded.Root.Descendants()))
        {
            Assert.Equal(SettingsResolver.Resolve(a).Rdp.ColorDepth, SettingsResolver.Resolve(b).Rdp.ColorDepth);
            Assert.Equal(a.Extensions.OrderBy(x => x.Key), b.Extensions.OrderBy(x => x.Key));
            if (a is FolderNode fa) Assert.Equal(fa.IsExpanded, ((FolderNode)b).IsExpanded);
            if (a is ConnectionNode ca) Assert.Equal(ca.Hostname, ((ConnectionNode)b).Hostname);
        }
    }

    [Fact]
    public void Database_and_keyfile_contain_no_plaintext()
    {
        var tree = new ConnectionTree();
        var c = tree.Root.Add(new ConnectionNode("geheimer-server") { Hostname = "dc01.intern.example" });
        c.Settings.Password = "SuperGeheim123!";
        tree.Vault.Add(new Credential { Name = "Domain Admin", Username = "da_admin", Password = "VaultPasswort!" });
        using (var store = Create()) store.Save(tree);

        foreach (var file in _dir.GetFiles())
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(file.FullName));
            foreach (var secret in new[] { "geheimer-server", "dc01.intern", "SuperGeheim", "VaultPasswort", "da_admin", "SQLite format 3" })
                Assert.DoesNotContain(secret, text);
        }
    }

    [Fact]
    public void Wrong_password_is_rejected()
    {
        using (Create()) { }
        Assert.Throws<WrongMasterPasswordException>(() => EncryptedConnectionStore.Open(DbPath, "falsches-passwort"));
    }

    [Fact]
    public void Change_master_password_keeps_data()
    {
        using (var store = Create())
        {
            var tree = new ConnectionTree();
            tree.Root.Add(new ConnectionNode("a") { Hostname = "h" });
            store.Save(tree);
            Assert.Throws<WrongMasterPasswordException>(() => store.ChangeMasterPassword("falsch-falsch", "Neues-Passwort-42"));
            store.ChangeMasterPassword(Password, "Neues-Passwort-42");
        }

        Assert.Throws<WrongMasterPasswordException>(() => EncryptedConnectionStore.Open(DbPath, Password));
        using var reopened = EncryptedConnectionStore.Open(DbPath, "Neues-Passwort-42");
        Assert.Equal("a", Assert.Single(reopened.Load().Connections).Name);
    }

    private sealed class FakeDeviceProtector(byte mask) : IDeviceKeyProtector
    {
        public string Id => "fake";
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ mask)).ToArray();
        public byte[] Unprotect(byte[] data) => Protect(data);
    }

    [Fact]
    public void Device_unlock_can_be_enabled_and_disabled()
    {
        var device = new FakeDeviceProtector(0x5A);
        using (var store = Create())
        {
            Assert.Null(EncryptedConnectionStore.TryOpenWithDevice(DbPath, device));
            store.EnableDeviceUnlock(device);
            Assert.True(store.HasDeviceUnlock);
        }

        using (var viaDevice = EncryptedConnectionStore.TryOpenWithDevice(DbPath, device))
        {
            Assert.NotNull(viaDevice);
            // anderes "Gerät" ⇒ falscher Schlüssel ⇒ Datenbank lässt sich nicht öffnen
            Assert.Throws<InvalidDataException>(() => EncryptedConnectionStore.TryOpenWithDevice(DbPath, new FakeDeviceProtector(0x11)));
            viaDevice!.DisableDeviceUnlock();
        }
        Assert.Null(EncryptedConnectionStore.TryOpenWithDevice(DbPath, device));
    }

    [Fact]
    public void Create_refuses_to_overwrite_and_validates_password()
    {
        using (Create()) { }
        Assert.Throws<IOException>(() => EncryptedConnectionStore.Create(DbPath, Password, KdfParameters.FastForTests));
        Assert.Throws<ArgumentException>(() =>
            EncryptedConnectionStore.Create(Path.Combine(_dir.FullName, "x.hdk"), "kurz", KdfParameters.FastForTests));
        Assert.False(File.Exists(Path.Combine(_dir.FullName, "x.hdk")));
    }

    [Fact]
    public void Save_replaces_previous_state()
    {
        using var store = Create();
        var tree = new ConnectionTree();
        var folder = tree.Root.Add(new FolderNode("f"));
        folder.Add(new ConnectionNode("a"));
        store.Save(tree);

        tree.Root.Remove(folder);
        tree.Root.Add(new ConnectionNode("b"));
        store.Save(tree);

        Assert.Equal(["b"], store.Load().Root.Descendants().Select(n => n.Name));
    }

    [Fact]
    public void Large_tree_saves_and_loads_quickly()
    {
        var tree = new ConnectionTree();
        for (var f = 0; f < 50; f++)
        {
            var folder = tree.Root.Add(new FolderNode($"F{f}"));
            for (var i = 0; i < 100; i++)
                folder.Add(new ConnectionNode($"s{i}") { Hostname = $"10.{f}.0.{i}" }).Settings.Password = "pw";
        }
        using var store = Create();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        store.Save(tree);
        var loaded = store.Load();
        Assert.Equal(5000, loaded.Connections.Count());
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Speichern+Laden dauerte {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Known_host_keys_survive_reopen_and_full_saves()
    {
        using (var store = Create())
        {
            Assert.Null(store.FindHostKey("Web01", 22, "ssh-ed25519"));
            store.RememberHostKey("Web01", 22, "ssh-ed25519", "SHA256:alt");
            store.RememberHostKey("web01", 22, "ssh-ed25519", "SHA256:neu"); // Groß-/Kleinschreibung egal, überschreibt
            store.Save(new ConnectionTree());                                   // Gesamtspeichern löscht Known Hosts nicht
        }
        using var reopened = EncryptedConnectionStore.Open(DbPath, Password);
        Assert.Equal("SHA256:neu", reopened.FindHostKey("WEB01", 22, "ssh-ed25519"));
        Assert.Null(reopened.FindHostKey("web01", 2222, "ssh-ed25519"));
    }

    [Fact]
    public void Backup_can_be_opened_with_same_password()
    {
        string backup;
        using (var store = Create())
        {
            var tree = new ConnectionTree();
            tree.Root.Add(new ConnectionNode("gesichert") { Hostname = "h" });
            store.Save(tree);
            backup = store.Backup(Path.Combine(_dir.FullName, "sicherung"));
            Assert.True(EncryptedConnectionStore.Exists(backup));
        }
        using var restored = EncryptedConnectionStore.Open(backup, Password);
        Assert.Equal("gesichert", Assert.Single(restored.Load().Connections).Name);
    }

    [Fact]
    public void Argon2id_matches_rfc9106_test_vector()
    {
        // RFC 9106, Abschnitt 5.3 (Argon2id, Version 0x13)
        var argon = new Argon2id(Enumerable.Repeat((byte)0x01, 32).ToArray())
        {
            Salt = Enumerable.Repeat((byte)0x02, 16).ToArray(),
            KnownSecret = Enumerable.Repeat((byte)0x03, 8).ToArray(),
            AssociatedData = Enumerable.Repeat((byte)0x04, 12).ToArray(),
            MemorySize = 32,
            Iterations = 3,
            DegreeOfParallelism = 4,
        };
        Assert.Equal("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659", Convert.ToHexStringLower(argon.GetBytes(32)));
    }
}
