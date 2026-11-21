using HostDeck.Core.Model;
using HostDeck.Core.Sync;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.Team;
using HostDeck.Presentation.ViewModels;
using static HostDeck.Tests.Presentation.MainViewModelTests;

namespace HostDeck.Tests.Presentation;

/// <summary>Zwei Kollegen arbeiten auf derselben Team-Datei (wie KeePass „Synchronisieren“).</summary>
public sealed class TeamLibraryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hostdeck-team-").FullName;
    private string TeamFile => Path.Combine(_dir, "confCons.xml");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void WriteTeamFile(params TreeNode[] topLevel)
    {
        var tree = new ConnectionTree();
        foreach (var n in topLevel) tree.Root.Add(n);
        new ConfConsExporter().ExportFile(tree, TeamFile);
    }

    private (MainViewModel Vm, FakeDialogs Dialogs, MemoryStore Store) User(string name, bool readOnly = false)
    {
        var (vm, dialogs, _, store) = Create();
        store.Location = Path.Combine(_dir, name, "hostdeck.db");
        Assert.True(vm.ConnectTeamLibraryAsync(TeamFile, readOnly).GetAwaiter().GetResult());
        return (vm, dialogs, store);
    }

    private ConnectionTree ReadFile() => new ConfConsImporter().ImportFile(TeamFile).Tree;

    private static ConnectionNode Conn(MainViewModel vm, string name) =>
        vm.TeamContainer!.Descendants().OfType<ConnectionNode>().Single(c => c.Name == name);

    private static void Edit(MainViewModel vm, Action change)
    {
        change();
        vm.Persist();
    }

    private static TreeNode[] Sample()
    {
        var net = new FolderNode("Netzwerk");
        net.Add(new ConnectionNode("sw01") { Hostname = "10.0.0.1", Settings = { Protocol = ProtocolKind.Ssh2 } });
        net.Add(new ConnectionNode("sw02") { Hostname = "10.0.0.2", Settings = { Protocol = ProtocolKind.Ssh2 } });
        var srv = new FolderNode("Server");
        srv.Add(new ConnectionNode("dc01") { Hostname = "10.1.0.1", Settings = { Protocol = ProtocolKind.Rdp, Username = "admin", Password = "Geheim!1" } });
        return [net, srv];
    }

    [Fact]
    public void Changes_of_two_users_are_merged_and_nobody_loses_anything()
    {
        WriteTeamFile(Sample());
        var (alice, _, _) = User("alice");
        var (bob, _, _) = User("bob");

        Edit(alice, () => Conn(alice, "sw01").Hostname = "10.0.0.11");
        Edit(bob, () => Conn(bob, "dc01").Settings.Port = 3390);
        Edit(bob, () => ((FolderNode)bob.TeamContainer!.Children[0]).Add(new ConnectionNode("sw03") { Hostname = "10.0.0.3" }));
        Assert.Equal(1, alice.TeamPendingChanges);
        Assert.Equal(2, bob.TeamPendingChanges);

        Assert.True(alice.SyncTeam(write: true));
        Assert.True(bob.SyncTeam(write: true));   // liest Alice' Stand, führt zusammen, schreibt
        Assert.True(alice.SyncTeam(write: false)); // Alice übernimmt Bobs Änderungen

        var file = ReadFile();
        Assert.Equal("10.0.0.11", file.Connections.Single(c => c.Name == "sw01").Hostname);
        Assert.Equal(3390, file.Connections.Single(c => c.Name == "dc01").Settings.Port);
        Assert.Contains(file.Connections, c => c.Name == "sw03");
        Assert.Equal(3390, Conn(alice, "dc01").Settings.Port);
        Assert.Equal("10.0.0.3", Conn(alice, "sw03").Hostname);
        Assert.Equal("10.0.0.11", Conn(bob, "sw01").Hostname);
        Assert.Equal(0, alice.TeamPendingChanges);
        Assert.Equal(0, bob.TeamPendingChanges);
        Assert.Equal("Geheim!1", Conn(alice, "dc01").Settings.Password); // gemeinsame Passwörter bleiben erhalten
    }

    [Fact]
    public void Personal_credentials_stay_local_and_are_never_written_to_the_team_file()
    {
        WriteTeamFile(Sample());
        var (alice, _, aliceStore) = User("alice");
        var radius = new Credential { Name = "RADIUS Alice", Username = "alice", Password = "MeinRadius!" };
        alice.SessionTree.Tree.Vault.Add(radius);
        var net = alice.TeamContainer!.Children.OfType<FolderNode>().Single(f => f.Name == "Netzwerk");
        Edit(alice, () => net.Settings.CredentialId = radius.Id);
        Assert.Equal(0, alice.TeamPendingChanges); // persönliche Zuordnung ist keine Team-Änderung

        Edit(alice, () => Conn(alice, "sw02").Hostname = "10.0.0.22");
        Assert.True(alice.SyncTeam(write: true));

        var raw = File.ReadAllText(TeamFile);
        Assert.DoesNotContain("alice", raw, StringComparison.OrdinalIgnoreCase);
        var file = ReadFile();
        Assert.All(file.Root.Descendants(), n => Assert.Null(n.Settings.CredentialId));
        Assert.DoesNotContain(file.Root.Descendants(), n => n.Settings.Password == "MeinRadius!");
        // lokal bleibt die Zuordnung auch nach dem Synchronisieren
        Assert.Equal(radius.Id, alice.TeamContainer!.Children.OfType<FolderNode>().Single(f => f.Name == "Netzwerk").Settings.CredentialId);
        Assert.Equal(radius.Id, aliceStore.Saved.Root.Descendants().Single(n => n.Name == "Netzwerk").Settings.CredentialId);
    }

    [Fact]
    public void Conflicting_edit_asks_and_keeps_mine_or_theirs()
    {
        WriteTeamFile(Sample());
        var (alice, _, _) = User("alice");
        var (bob, bobDialogs, _) = User("bob");

        Edit(alice, () => Conn(alice, "sw01").Hostname = "10.0.0.100");
        Assert.True(alice.SyncTeam(write: true));

        Edit(bob, () => Conn(bob, "sw01").Hostname = "10.0.0.200");
        bobDialogs.ConfirmAnswer = false; // „Version aus der Datei übernehmen“
        Assert.True(bob.SyncTeam(write: true));
        Assert.Equal("10.0.0.100", Conn(bob, "sw01").Hostname);
        Assert.Equal("10.0.0.100", ReadFile().Connections.Single(c => c.Name == "sw01").Hostname);

        Edit(bob, () => Conn(bob, "sw01").Hostname = "10.0.0.201");
        Edit(alice, () => Conn(alice, "sw01").Hostname = "10.0.0.101");
        Assert.True(alice.SyncTeam(write: true));
        bobDialogs.ConfirmAnswer = true; // „meine Version speichern“
        Assert.True(bob.SyncTeam(write: true));
        Assert.Equal("10.0.0.201", ReadFile().Connections.Single(c => c.Name == "sw01").Hostname);
    }

    [Fact]
    public void Deletion_is_synced_but_deleted_and_changed_elsewhere_is_kept()
    {
        WriteTeamFile(Sample());
        var (alice, _, _) = User("alice");
        var (bob, _, _) = User("bob");

        Edit(alice, () => Conn(alice, "sw02").Parent!.Remove(Conn(alice, "sw02")));
        Assert.True(alice.SyncTeam(write: true));
        Assert.True(bob.SyncTeam(write: false));
        Assert.DoesNotContain(bob.TeamContainer!.Descendants(), n => n.Name == "sw02");

        // Bob löscht dc01, Alice ändert es gleichzeitig ⇒ bleibt erhalten
        Edit(bob, () => Conn(bob, "dc01").Parent!.Remove(Conn(bob, "dc01")));
        Edit(alice, () => Conn(alice, "dc01").Hostname = "10.1.0.99");
        Assert.True(alice.SyncTeam(write: true));
        Assert.True(bob.SyncTeam(write: true));
        Assert.Equal("10.1.0.99", ReadFile().Connections.Single(c => c.Name == "dc01").Hostname);
    }

    [Fact]
    public void Read_only_copy_on_jumphost_follows_the_file_and_never_writes()
    {
        WriteTeamFile(Sample());
        var (jump, _, _) = User("jump", readOnly: true);
        var before = File.ReadAllBytes(TeamFile);

        Edit(jump, () => Conn(jump, "sw01").Hostname = "1.2.3.4");
        Assert.Equal(0, jump.TeamPendingChanges);
        Assert.False(jump.SyncTeam(write: true) && File.ReadAllBytes(TeamFile).SequenceEqual(before) is false);
        Assert.Equal(before, File.ReadAllBytes(TeamFile));

        // neue Kopie wird „hingelegt“ (RDP-Kopie) ⇒ übernommen, lokale Abweichung verworfen
        var updated = Sample();
        ((FolderNode)updated[0]).Add(new ConnectionNode("sw09") { Hostname = "10.0.0.9" });
        var original = ReadFile();
        var tree = new ConnectionTree();
        foreach (var n in original.Root.Children.ToList()) tree.Root.Add(n);
        ((FolderNode)tree.Root.Children[0]).Add(new ConnectionNode("sw09") { Hostname = "10.0.0.9" });
        new ConfConsExporter().ExportFile(tree, TeamFile);

        Assert.True(jump.SyncTeam(write: false));
        Assert.Equal("10.0.0.1", Conn(jump, "sw01").Hostname);
        Assert.Equal("10.0.0.9", Conn(jump, "sw09").Hostname);
    }

    [Fact]
    public void Deleting_the_team_folder_disconnects_and_never_empties_the_file()
    {
        WriteTeamFile(Sample());
        var (alice, _, _) = User("alice");
        var before = File.ReadAllBytes(TeamFile);
        Edit(alice, () => alice.SessionTree.Tree.Root.Remove(alice.TeamContainer!));

        Assert.False(alice.SyncTeam(write: true));
        Assert.False(alice.HasTeamLibrary);
        Assert.Equal(before, File.ReadAllBytes(TeamFile));
    }

    [Fact]
    public async Task Password_protected_team_file_keeps_its_protection_and_password_is_remembered()
    {
        var tree = new ConnectionTree();
        foreach (var n in Sample()) tree.Root.Add(n);
        new ConfConsExporter(new ConfConsExportOptions { MasterPassword = "TeamPass#2026" }).ExportFile(tree, TeamFile);

        var (vm, dialogs, _, store) = Create();
        store.Location = Path.Combine(_dir, "alice", "hostdeck.db");
        dialogs.Passwords.Enqueue("TeamPass#2026");
        Assert.True(await vm.ConnectTeamLibraryAsync(TeamFile, false));

        Edit(vm, () => Conn(vm, "sw01").Hostname = "10.0.0.50");
        Assert.True(vm.SyncTeam(write: true)); // kein weiterer Dialog nötig
        Assert.Empty(dialogs.Passwords);

        Assert.Throws<ConfConsAuthenticationException>(() => new ConfConsImporter().ImportFile(TeamFile));
        var reread = new ConfConsImporter(new ConfConsImportOptions { MasterPassword = "TeamPass#2026" }).ImportFile(TeamFile);
        Assert.True(reread.File.CustomMasterPassword);
        Assert.Equal("10.0.0.50", reread.Tree.Connections.Single(c => c.Name == "sw01").Hostname);
        Assert.Equal("Geheim!1", reread.Tree.Connections.Single(c => c.Name == "dc01").Settings.Password);
    }

    [Fact]
    public async Task Earlier_manual_import_is_replaced_without_duplicate_ids()
    {
        WriteTeamFile(Sample());
        var (vm, _, _, store) = Create();
        store.Location = Path.Combine(_dir, "alice", "hostdeck.db");
        Assert.True(vm.TryImport(TeamFile));
        var count = vm.SessionTree.Tree.Connections.Count();

        Assert.True(await vm.ConnectTeamLibraryAsync(TeamFile, false));
        var ids = vm.SessionTree.Tree.Root.Descendants().Select(n => n.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(count, vm.SessionTree.Tree.Connections.Count());
        Assert.Equal(count, vm.TeamContainer!.Descendants().OfType<ConnectionNode>().Count());
    }

    [Fact]
    public void Concurrent_write_is_detected_and_lock_is_respected()
    {
        WriteTeamFile(Sample());
        var snapshot = TeamLibraryFile.Read(TeamFile, null, null);
        File.AppendAllText(TeamFile, "\n"); // jemand anderes hat gespeichert
        Assert.Throws<TeamFileChangedException>(() =>
            TeamLibraryFile.Write(TeamFile, snapshot.Children, snapshot.Info, null, snapshot.Hash, Path.Combine(_dir, "bk")));

        var fresh = TeamLibraryFile.Read(TeamFile, null, null);
        File.WriteAllText(TeamFile + ".hostdeck-lock", "bob@PC42 now");
        var ex = Assert.Throws<TeamFileLockedException>(() =>
            TeamLibraryFile.Write(TeamFile, fresh.Children, fresh.Info, null, fresh.Hash, Path.Combine(_dir, "bk")));
        Assert.Equal("bob@PC42", ex.Holder);
    }

    [Fact]
    public void Roundtrip_through_the_file_is_stable()
    {
        foreach (var (fixture, password) in new[] { ("confCons_v2_6.xml", (string?)null), ("confCons_v2_6_passwordis_Password.xml", "Password") })
        {
            File.Copy(Fixture.PathOf(fixture), TeamFile, overwrite: true);
            var original = TeamLibraryFile.Read(TeamFile, password, null);
            var (_, written) = TeamLibraryFile.Write(TeamFile, original.Children, original.Info, password ?? original.Password, original.Hash, Path.Combine(_dir, "bk"));
            var second = TeamLibraryFile.Read(TeamFile, password, null);
            // Was Write als „geschrieben“ meldet, ist exakt das, was andere lesen (keine Scheinänderungen)
            var a = TreeMerge.Snapshot(written);
            var b = TreeMerge.Snapshot(second.Children);
            Assert.Equal(a.Count, b.Count);
            foreach (var (id, entry) in a) Assert.Equal(entry, b[id]);
        }
    }
}
