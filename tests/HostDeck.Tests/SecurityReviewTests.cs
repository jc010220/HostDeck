using System.Net;
using System.Net.Sockets;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.QuickConnect;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.ViewModels;
using HostDeck.Protocols.Vnc;
using static HostDeck.Tests.Presentation.MainViewModelTests;

namespace HostDeck.Tests;

/// <summary>Regressionstests zu den Befunden des externen Sicherheits-Reviews (Version 1.0.14).</summary>
public sealed class SecurityReviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hostdeck-sec-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static ConnectionNode AddConnection(MainViewModel vm, string password)
    {
        var c = new ConnectionNode("srv") { Hostname = "10.0.0.5", Settings = { Protocol = ProtocolKind.Ssh2, Username = "root", Password = password } };
        vm.SessionTree.Add(c);
        var cred = vm.SessionTree.Tree.Vault.Add(new Credential { Name = "vault", Username = "adm", Password = "VaultSecret!9" });
        var c2 = new ConnectionNode("srv2") { Hostname = "10.0.0.6", Settings = { CredentialId = cred.Id } };
        vm.SessionTree.Add(c2);
        return c;
    }

    // ---- 1. Export ---------------------------------------------------------------------------------

    [Fact]
    public void Export_without_password_contains_no_secrets()
    {
        var (vm, dialogs, _, _) = Create();
        AddConnection(vm, "InlineSecret!7");
        var path = Path.Combine(_dir, "export.xml");
        dialogs.PromptAnswer = _ => "";                       // leer = ohne Passwörter
        dialogs.SaveFileAnswer = path;
        vm.ExportMRemoteNGCommand.Execute(null);

        var result = new ConfConsImporter().ImportFile(path);
        Assert.False(result.File.CustomMasterPassword);
        Assert.All(result.Tree.Connections, c => Assert.True(string.IsNullOrEmpty(c.Settings.Password)));
        Assert.Contains(result.Tree.Connections, c => c.Settings.Username == "adm"); // Benutzernamen bleiben
    }

    [Fact]
    public void Export_with_password_protects_the_whole_file()
    {
        var (vm, dialogs, _, _) = Create();
        AddConnection(vm, "InlineSecret!7");
        var path = Path.Combine(_dir, "export.xml");
        dialogs.PromptAnswer = _ => "Export-Pass#123";
        dialogs.SaveFileAnswer = path;
        vm.ExportMRemoteNGCommand.Execute(null);

        Assert.Throws<ConfConsAuthenticationException>(() => new ConfConsImporter().ImportFile(path));
        var result = new ConfConsImporter(new ConfConsImportOptions { MasterPassword = "Export-Pass#123" }).ImportFile(path);
        Assert.True(result.File.FullFileEncryption);
        Assert.True(result.File.KdfIterations >= 10_000);
        Assert.Contains(result.Tree.Connections, c => c.Settings.Password == "VaultSecret!9");
        Assert.DoesNotContain("srv2", File.ReadAllText(path));  // Vollverschlüsselung: nicht einmal Namen lesbar
    }

    // ---- 2. Persönliche Logins an geändertes Ziel ----------------------------------------------------

    [Fact]
    public async Task Personal_login_is_not_sent_to_a_target_changed_by_the_team_file()
    {
        var team = Path.Combine(_dir, "confCons.xml");
        var t = new ConnectionTree();
        var net = new FolderNode("Netzwerk");
        net.Add(new ConnectionNode("sw01") { Hostname = "10.0.0.1", Settings = { Protocol = ProtocolKind.Rdp } });
        t.Root.Add(net);
        new ConfConsExporter().ExportFile(t, team);

        var (me, dialogs, _, store) = Create();
        store.Location = Path.Combine(_dir, "me", "db");
        Assert.True(await me.ConnectTeamLibraryAsync(team, false));
        var radius = me.SessionTree.Tree.Vault.Add(new Credential { Name = "RADIUS", Username = "me", Password = "MyRadius!1" });
        me.TeamContainer!.Children.OfType<FolderNode>().Single().Settings.CredentialId = radius.Id; // am Ordner, geerbt
        me.Persist();

        var sw01 = () => me.SessionTree.AllItems.Single(i => i.Node.Name == "sw01");
        dialogs.ConfirmAnswer = false;                       // erste Verwendung muss ausdrücklich bestätigt werden
        var before = dialogs.ConfirmCalls;
        me.OpenCommand.Execute(sw01());
        Assert.Equal(before + 1, dialogs.ConfirmCalls);
        Assert.NotEqual("MyRadius!1", me.Workspace.ActiveTab!.Credentials.Password);
        me.Workspace.Close(me.Workspace.ActiveTab);
        dialogs.ConfirmAnswer = true;
        me.OpenCommand.Execute(sw01());
        Assert.Equal("MyRadius!1", me.Workspace.ActiveTab!.Credentials.Password);
        me.Workspace.Close(me.Workspace.ActiveTab);

        // Kollege biegt das Ziel um
        var (evil, _, _, evilStore) = Create();
        evilStore.Location = Path.Combine(_dir, "evil", "db");
        Assert.True(await evil.ConnectTeamLibraryAsync(team, false));
        ((ConnectionNode)evil.TeamContainer!.Descendants().Single(n => n.Name == "sw01")).Hostname = "203.0.113.66";
        evil.Persist();
        Assert.True(evil.SyncTeam(write: true));

        Assert.True(me.SyncTeam(write: false));
        dialogs.ConfirmAnswer = false;                       // „Login an neues Ziel senden?“ ⇒ Nein
        var asked = dialogs.ConfirmCalls;
        me.OpenCommand.Execute(sw01());
        Assert.Equal(asked + 1, dialogs.ConfirmCalls);
        Assert.NotEqual("MyRadius!1", me.Workspace.ActiveTab!.Credentials.Password);
        me.Workspace.Close(me.Workspace.ActiveTab);

        dialogs.ConfirmAnswer = true;                        // bewusst bestätigt ⇒ ab jetzt ohne Rückfrage
        me.OpenCommand.Execute(sw01());
        Assert.Equal("MyRadius!1", me.Workspace.ActiveTab!.Credentials.Password);
        me.Workspace.Close(me.Workspace.ActiveTab);
        asked = dialogs.ConfirmCalls;
        me.OpenCommand.Execute(sw01());
        Assert.Equal(asked, dialogs.ConfirmCalls);
    }

    [Fact]
    public void Target_key_includes_the_rdp_gateway_and_accepts_old_keys_without_gateway()
    {
        var c = new ConnectionNode("dc01") { Hostname = "10.1.0.1", Settings = { Protocol = ProtocolKind.Rdp } };
        var t = new ConnectionTree();
        t.Root.Add(c);
        var plain = MainViewModel.TargetKey(c);
        Assert.EndsWith("|-", plain);
        c.Settings.Rdp.Gateway.Usage = RdGatewayUsage.Always;
        c.Settings.Rdp.Gateway.Hostname = "rdgw.corp.example";
        var withGateway = MainViewModel.TargetKey(c);
        Assert.NotEqual(plain, withGateway);
        Assert.EndsWith("|rdgw.corp.example", withGateway);
        c.Settings.Rdp.Gateway.Hostname = "evil-gw.example";
        Assert.NotEqual(withGateway, MainViewModel.TargetKey(c)); // reine Gateway-Änderung ⇒ anderes Ziel
    }

    // ---- 3. Klartext-Protokolle --------------------------------------------------------------------

    [Fact]
    public void Plaintext_password_consent_is_asked_once_per_entry_and_host()
    {
        var (_, dialogs, _, store) = Create();
        var id = Guid.NewGuid();
        dialogs.ConfirmAnswer = false;
        Assert.False(PlaintextConsent.Ask(store, dialogs, id, "sw01", 23, "Telnet", f => f()));
        Assert.False(PlaintextConsent.Ask(store, dialogs, id, "sw01", 23, "Telnet", f => f())); // „Nein“ wird nicht gemerkt
        dialogs.ConfirmAnswer = true;
        Assert.True(PlaintextConsent.Ask(store, dialogs, id, "sw01", 23, "Telnet", f => f()));
        var calls = dialogs.ConfirmCalls;
        Assert.True(PlaintextConsent.Ask(store, dialogs, id, "SW01", 23, "Telnet", f => f()));
        Assert.Equal(calls, dialogs.ConfirmCalls);
        dialogs.ConfirmAnswer = false;
        Assert.False(PlaintextConsent.Ask(store, dialogs, id, "other-host", 23, "Telnet", f => f())); // anderer Host ⇒ neu fragen
        Assert.False(PlaintextConsent.Ask(store, dialogs, id, "sw01", 2323, "Telnet", f => f()));     // anderer Port ⇒ neu fragen
    }

    // ---- Schnellverbindung -------------------------------------------------------------------------

    [Theory]
    [InlineData("ssh admin:Geheim1@10.0.0.1")]
    [InlineData("ssh://admin:Geheim1@10.0.0.1:22")]
    [InlineData("admin:Geheim1@host")]
    public void Quick_connect_never_stores_inline_passwords(string input)
    {
        var (vm, _, settings, _) = Create();
        vm.QuickConnectText = input;
        vm.QuickConnectCommand.Execute(null);
        Assert.Equal(0, vm.Workspace.TabCount);
        Assert.DoesNotContain(settings.Stored.QuickConnectHistory, h => h.Contains("Geheim1"));
        Assert.False(QuickConnectParser.TryParse(input, ProtocolKind.Ssh2, out _));
    }

    [Fact]
    public void Quick_connect_history_contains_only_normalized_fields()
    {
        var (vm, _, settings, _) = Create();
        vm.QuickConnectText = "ssh -p 2222 admin@10.0.0.1";
        vm.QuickConnectCommand.Execute(null);
        Assert.Equal("ssh admin@10.0.0.1:2222", settings.Stored.QuickConnectHistory[0]);
        Assert.True(QuickConnectParser.TryParse(settings.Stored.QuickConnectHistory[0], ProtocolKind.Rdp, out var again));
        Assert.Equal(("10.0.0.1", 2222, "admin"), (again!.Host, again.Port, again.Username));
    }

    [Fact]
    public void Old_history_entries_with_passwords_are_purged_on_start()
    {
        var settings = new MemorySettings();
        settings.Stored.QuickConnectHistory = ["ssh root:hunter2@db01", "rdp server01"];
        _ = new MainViewModel(new FakeDialogs(), settings, new NoTheme(), new MemoryStore());
        Assert.Equal(["rdp server01"], settings.Stored.QuickConnectHistory);
    }

    // ---- Ressourcengrenzen -------------------------------------------------------------------------

    [Fact]
    public async Task Vnc_rejects_absurd_desktop_sizes_instead_of_allocating_gigabytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync(ct);
            var s = c.GetStream();
            await s.WriteAsync("RFB 003.008\n"u8.ToArray(), ct);
            var buf = new byte[12]; await s.ReadExactlyAsync(buf, ct);
            await s.WriteAsync(new byte[] { 1, 1 }, ct);             // ein Verfahren: None
            await s.ReadExactlyAsync(new byte[1], ct);
            await s.WriteAsync(new byte[4], ct);                     // SecurityResult OK
            await s.ReadExactlyAsync(new byte[1], ct);               // ClientInit
            var init = new byte[24];
            init[0] = 0xFF; init[1] = 0xFF; init[2] = 0xFF; init[3] = 0xFF; // 65535 × 65535
            await s.WriteAsync(init, ct);
            await Task.Delay(500, ct);
        }, ct);

        await using var client = new RfbClient();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ConnectAsync("127.0.0.1", port, () => null, ct: TestContext.Current.CancellationToken));
        await server;
    }

    [Fact]
    public void Oversized_files_are_rejected_before_being_read_into_memory()
    {
        var path = Path.Combine(_dir, "huge.xml");
        using (var f = File.Create(path)) f.SetLength(ConfConsImporter.MaxFileBytes + 1);   // sparse, kein echter Speicherbedarf
        Assert.Throws<ConfConsFormatException>(() => new ConfConsImporter().ImportFile(path));
        Assert.Throws<ConfConsFormatException>(() => HostDeck.Presentation.Team.TeamLibraryFile.Read(path, null, null));
    }

    [Fact]
    public void Import_rejects_absurd_kdf_iterations()
    {
        var xml = Fixture.Read("confCons_v2_6.xml");
        xml = System.Text.RegularExpressions.Regex.Replace(xml, "KdfIterations=\"\\d+\"", "KdfIterations=\"2000000000\"");
        Assert.Throws<ConfConsFormatException>(() => new ConfConsImporter().Import(xml));
    }
}

public class SqliteVersionTests
{
    [Fact]
    public void Bundled_sqlite_is_not_affected_by_CVE_2025_6965()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(HostDeck.Storage.EncryptedConnectionStore).TypeHandle);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        var version = Version.Parse((string)cmd.ExecuteScalar()!);
        Assert.True(version >= new Version(3, 50, 2), $"SQLite {version} is older than 3.50.2");
    }
}
