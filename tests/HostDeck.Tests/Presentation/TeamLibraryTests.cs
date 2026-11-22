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
        _dialogsByVm[vm] = dialogs;
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
        Assert.NotEmpty(alice.TeamBackups());  // vorheriger Stand verschlüsselt gesichert
        Assert.False(Directory.Exists(Path.Combine(_dir, "alice", "backups", "team")));
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
            TeamLibraryFile.Write(TeamFile, snapshot.Children, snapshot.Info, null, snapshot.Hash, null));

        var fresh = TeamLibraryFile.Read(TeamFile, null, null);
        File.WriteAllText(TeamFile + ".hostdeck-lock", "bob@PC42 now");
        var ex = Assert.Throws<TeamFileLockedException>(() =>
            TeamLibraryFile.Write(TeamFile, fresh.Children, fresh.Info, null, fresh.Hash, null));
        Assert.Equal("bob@PC42", ex.Holder);
    }

    [Fact]
    public void Roundtrip_through_the_file_is_stable()
    {
        foreach (var (fixture, password) in new[] { ("confCons_v2_6.xml", (string?)null), ("confCons_v2_6_passwordis_Password.xml", "Password") })
        {
            File.Copy(Fixture.PathOf(fixture), TeamFile, overwrite: true);
            var original = TeamLibraryFile.Read(TeamFile, password, null);
            var (_, written) = TeamLibraryFile.Write(TeamFile, original.Children, original.Info, password ?? original.Password, original.Hash, null);
            var second = TeamLibraryFile.Read(TeamFile, password, null);
            // Was Write als „geschrieben“ meldet, ist exakt das, was andere lesen (keine Scheinänderungen)
            var a = TreeMerge.Snapshot(written);
            var b = TreeMerge.Snapshot(second.Children);
            Assert.Equal(a.Count, b.Count);
            foreach (var (id, entry) in a) Assert.Equal(entry, b[id]);
        }
    }

    [Fact]
    public async Task Team_password_can_be_set_from_HostDeck_and_colleagues_are_asked_once()
    {
        WriteTeamFile(Sample());
        var (alice, aliceDialogs, _) = User("alice");
        var (bob, bobDialogs, _) = User("bob");

        aliceDialogs.PromptAnswer = _ => "short";
        await alice.SetTeamPasswordCommand.ExecuteAsync(null);
        Assert.Contains(aliceDialogs.Errors, e => e.Contains("10 characters"));
        Assert.False(new ConfConsImporter().ImportFile(TeamFile).File.CustomMasterPassword);

        aliceDialogs.PromptAnswer = _ => "Team-Secret#2026";
        await alice.SetTeamPasswordCommand.ExecuteAsync(null);

        // Datei ist jetzt geschützt (wie mRemoteNG „Password protect“), mit mehr KDF-Iterationen, Inhalt unverändert
        Assert.Throws<ConfConsAuthenticationException>(() => new ConfConsImporter().ImportFile(TeamFile));
        var reread = new ConfConsImporter(new ConfConsImportOptions { MasterPassword = "Team-Secret#2026" }).ImportFile(TeamFile);
        Assert.True(reread.File.CustomMasterPassword);
        Assert.True(reread.File.KdfIterations >= 10_000);
        Assert.Equal("Geheim!1", reread.Tree.Connections.Single(c => c.Name == "dc01").Settings.Password);

        // Alice synchronisiert weiter ohne Rückfrage
        Edit(alice, () => Conn(alice, "sw01").Hostname = "10.0.0.77");
        Assert.True(alice.SyncTeam(write: true));

        // Bob wird einmal gefragt, danach nicht mehr
        bobDialogs.Passwords.Enqueue("Team-Secret#2026");
        Assert.True(bob.SyncTeam(write: false));
        Assert.Equal("10.0.0.77", Conn(bob, "sw01").Hostname);
        Assert.True(bob.SyncTeam(write: false));
        Assert.Empty(bobDialogs.Passwords);
    }

    [Theory]
    [InlineData("kurz", false)]
    [InlineData("Lang-genug-123", true)]
    [InlineData("Passwort-mit-€-Zeichen", false)] // € liegt außerhalb von Latin-1
    [InlineData("Grüße-aus-Zürich", true)]        // Umlaute sind Latin-1
    public void Team_password_rules_match_mRemoteNG(string password, bool ok)
        => Assert.Equal(ok, MainViewModel.TeamPasswordProblem(password) is null);

    [Fact]
    public async Task Connecting_via_command_is_read_write_by_default()
    {
        WriteTeamFile(Sample());
        var (vm, dialogs, _, store) = Create();
        store.Location = Path.Combine(_dir, "carol", "hostdeck.db");
        dialogs.FileToOpen = TeamFile;
        dialogs.ConfirmAnswer = true; // eine evtl. Rückfrage dürfte NICHT „nur lesen“ bedeuten
        await vm.ConnectTeamLibraryCommand.ExecuteAsync(null);
        Assert.True(vm.HasTeamLibrary);
        Assert.False(vm.Settings.TeamReadOnly);
        Assert.DoesNotContain("read-only", vm.TeamStatus);
    }

    [Fact]
    public void Read_only_copy_blocks_changes_instead_of_silently_losing_them()
    {
        WriteTeamFile(Sample());
        var (jump, dialogs, _) = User("jump", readOnly: true);
        var sw01 = jump.SessionTree.AllItems.Single(i => i.Node.Name == "sw01");
        var net = jump.SessionTree.AllItems.Single(i => i.Node.Name == "Netzwerk");
        var before = File.ReadAllBytes(TeamFile);

        jump.DeleteCommand.Execute(sw01);
        jump.DuplicateCommand.Execute(sw01);
        dialogs.PromptAnswer = _ => "renamed";
        jump.RenameCommand.Execute(sw01);
        jump.SessionTree.SelectedItem = net;
        jump.NewConnectionCommand.Execute(null);
        jump.NewFolderCommand.Execute(null);
        Assert.False(jump.SessionTree.CanMove(sw01, null, DropPosition.After));

        Assert.Equal(["sw01", "sw02"], ((FolderNode)net.Node).Children.Select(c => c.Name));
        Assert.Equal(before, File.ReadAllBytes(TeamFile));

        // Eigenschaften: Team-Werte werden zurückgesetzt, persönliche Login-Zuordnung bleibt
        var radius = jump.SessionTree.Tree.Vault.Add(new Credential { Name = "RADIUS", Username = "me", Password = "x" });
        dialogs.EditAnswer = e =>
        {
            e.Hostname = "6.6.6.6";
            e.Credential = e.CredentialOptions.Single(o => o.Id == radius.Id);
            return e.Apply() is null;
        };
        jump.EditItemCommand.Execute(jump.SessionTree.AllItems.Single(i => i.Node.Name == "sw01"));
        var reverted = Conn(jump, "sw01");
        Assert.Equal("10.0.0.1", reverted.Hostname);
        Assert.Equal(radius.Id, reverted.Settings.CredentialId);

        // Personal-Bereich bleibt normal bearbeitbar
        jump.SessionTree.SelectedItem = null;
        dialogs.EditAnswer = e => e.Apply() is null;
        jump.NewFolderCommand.Execute(null);
        Assert.Contains(jump.SessionTree.Items, i => i.Node.Name == "New folder");
    }

    [Fact]
    public async Task Password_prompts_for_the_team_file_run_on_the_UI_thread()
    {
        var tree = new ConnectionTree();
        foreach (var n in Sample()) tree.Root.Add(n);
        new ConfConsExporter(new ConfConsExportOptions { MasterPassword = "TeamPass#2026" }).ExportFile(tree, TeamFile);

        using var ui = new UiThread();
        var (vm, dialogs, _, store) = await ui.Run(() => Create());
        store.Location = Path.Combine(_dir, "ui", "hostdeck.db");
        dialogs.Passwords.Enqueue("TeamPass#2026");

        Assert.True(await ui.Run(() => vm.ConnectTeamLibraryAsync(TeamFile, false)));
        Assert.NotEmpty(dialogs.PasswordPromptThreads);
        Assert.All(dialogs.PasswordPromptThreads, id => Assert.Equal(ui.ThreadId, id));

        // gespeichertes Passwort vergessen ⇒ beim Synchronisieren erneut fragen – ebenfalls im UI-Thread
        store.Documents[MainViewModel.TeamPasswordDocument] = "";
        dialogs.Passwords.Enqueue("TeamPass#2026");
        Assert.True(await ui.Run(() => vm.SyncTeamAsync(write: false, interactive: true)));
        Assert.Equal(2, dialogs.PasswordPromptThreads.Count);
        Assert.All(dialogs.PasswordPromptThreads, id => Assert.Equal(ui.ThreadId, id));
    }

    [Fact]
    public void Disconnect_removes_the_team_folder_but_keeps_the_file()
    {
        WriteTeamFile(Sample());
        var (alice, dialogs, _) = User("alice");
        var before = File.ReadAllBytes(TeamFile);

        dialogs.ConfirmAnswer = false; // abbrechen
        alice.DisconnectTeamLibraryCommand.Execute(null);
        Assert.True(alice.HasTeamLibrary);

        dialogs.ConfirmAnswer = true;
        alice.DisconnectTeamLibraryCommand.Execute(null);
        Assert.False(alice.HasTeamLibrary);
        Assert.Null(alice.TeamContainer);
        Assert.DoesNotContain(alice.SessionTree.Tree.Root.Descendants(), n => n.Name is "sw01" or "Team");
        Assert.Equal(before, File.ReadAllBytes(TeamFile));
    }

    [Fact]
    public async Task Team_snippets_are_shared_encrypted_and_deletions_propagate()
    {
        WriteTeamFile(Sample());
        var (alice, aliceDialogs, _) = User("alice");
        var (bob, _, _) = User("bob");

        alice.Snippets.Add("Restart app", "sudo systemctl restart app", "Team", team: true);
        await alice.SyncTeamSnippetsAsync();
        await bob.SyncTeamSnippetsAsync();
        var shared = Assert.Single(bob.Snippets.Items, s => s.Name == "Restart app");
        Assert.True(shared.IsTeam);
        Assert.DoesNotContain(bob.Snippets.Items, s => s.Name == "Restart app" && !s.IsTeam);

        // Persönliche Snippets bleiben persönlich
        alice.Snippets.Add("Only mine", "echo mine");
        await bob.SyncTeamSnippetsAsync();
        Assert.DoesNotContain(bob.Snippets.Items, s => s.Name == "Only mine");

        // Team-Passwort setzen ⇒ Snippet-Datei wird verschlüsselt
        aliceDialogs.PromptAnswer = _ => "Team-Secret#2026";
        await alice.SetTeamPasswordCommand.ExecuteAsync(null);
        var raw = File.ReadAllText(HostDeck.Presentation.Team.TeamSnippetFile.PathFor(TeamFile));
        Assert.DoesNotContain("systemctl", raw);

        // Bob löscht (kennt das Passwort nach einmaliger Abfrage) ⇒ bei Alice weg
        var bobDialogs = GetDialogs(bob);
        bobDialogs.Passwords.Enqueue("Team-Secret#2026");
        Assert.True(bob.SyncTeam(write: false));
        bob.Snippets.Remove(bob.Snippets.Items.Single(s => s.Name == "Restart app"));
        await bob.SyncTeamSnippetsAsync();
        await alice.SyncTeamSnippetsAsync();
        Assert.DoesNotContain(alice.Snippets.Items, s => s.Name == "Restart app");
    }

    private readonly Dictionary<MainViewModel, FakeDialogs> _dialogsByVm = [];
    private FakeDialogs GetDialogs(MainViewModel vm) => _dialogsByVm[vm];

    [Fact]
    public async Task Read_only_copy_shows_team_snippets_but_cannot_change_them()
    {
        WriteTeamFile(Sample());
        var (alice, _, _) = User("alice");
        alice.Snippets.Add("Shared", "uptime", team: true);
        await alice.SyncTeamSnippetsAsync();

        var (jump, _, _) = User("jump", readOnly: true);
        await jump.SyncTeamSnippetsAsync();
        var s = Assert.Single(jump.Snippets.Items, x => x.Name == "Shared");
        Assert.False(jump.Snippets.CanEdit(s));
    }

    [Fact]
    public void Orphaned_team_folder_from_a_restored_backup_is_removed_and_disconnect_always_works()
    {
        WriteTeamFile(Sample());
        var (alice, dialogs, store) = User("alice");
        var snapshotWithTeam = store.Saved;                 // „Sicherung“ mit Team-Ordner

        // Team getrennt (Einstellung leer), danach wird die alte Sicherung wiederhergestellt
        alice.Settings.TeamLibraryPath = null;
        var restored = new MainViewModel(new FakeDialogs(), new MemorySettings { Stored = alice.Settings }, new NoTheme(), new MemoryStore { Saved = snapshotWithTeam });
        restored.Initialize();
        Assert.Null(restored.TeamContainer);
        Assert.DoesNotContain(restored.SessionTree.Tree.Root.Descendants(), n => n.Name == "sw01");

        // auch direkt: Trennen wirkt, selbst wenn die Einstellung schon leer ist
        var (bob, bobDialogs, _) = User("bob");
        bob.Settings.TeamLibraryPath = null;
        Assert.NotNull(bob.TeamContainer);
        bobDialogs.ConfirmAnswer = true;
        bob.DisconnectTeamLibraryCommand.Execute(null);
        Assert.Null(bob.TeamContainer);
    }

    [Fact]
    public void Restoring_a_backup_without_team_folder_reconnects_instead_of_disconnecting()
    {
        WriteTeamFile(Sample());
        var (alice, _, _) = User("alice");
        var settings = new MemorySettings { Stored = alice.Settings };   // Team ist verbunden
        var restored = new MainViewModel(new FakeDialogs(), settings, new NoTheme(), new MemoryStore { Saved = new ConnectionTree() });
        restored.Initialize();
        Assert.True(restored.HasTeamLibrary);
        Assert.NotNull(restored.TeamContainer);
        Assert.Contains(restored.TeamContainer!.Descendants(), n => n.Name == "sw01");
    }

    [Fact]
    public async Task Team_password_can_be_removed_again()
    {
        WriteTeamFile(Sample());
        var (alice, dialogs, _) = User("alice");
        alice.Snippets.Add("Shared", "uptime", team: true);
        await alice.SyncTeamSnippetsAsync();
        dialogs.PromptAnswer = _ => "Team-Secret#2026";
        await alice.SetTeamPasswordCommand.ExecuteAsync(null);
        Assert.True(new ConfConsImporter(new ConfConsImportOptions { MasterPassword = "Team-Secret#2026" }).ImportFile(TeamFile).File.CustomMasterPassword);

        dialogs.ConfirmAnswer = false;                       // Warnung abgelehnt ⇒ bleibt geschützt
        await alice.RemoveTeamPasswordCommand.ExecuteAsync(null);
        Assert.Throws<ConfConsAuthenticationException>(() => new ConfConsImporter().ImportFile(TeamFile));

        dialogs.ConfirmAnswer = true;
        await alice.RemoveTeamPasswordCommand.ExecuteAsync(null);
        var plain = new ConfConsImporter().ImportFile(TeamFile);   // ohne Passwort lesbar
        Assert.False(plain.File.CustomMasterPassword);
        Assert.Equal("Geheim!1", plain.Tree.Connections.Single(c => c.Name == "dc01").Settings.Password);
        Assert.Contains("uptime", File.ReadAllText(HostDeck.Presentation.Team.TeamSnippetFile.PathFor(TeamFile))); // Snippets unverschlüsselt

        // Kollege ohne Passwort kann direkt lesen
        var (bob, bobDialogs, _) = User("bob");
        Assert.Empty(bobDialogs.PasswordPromptThreads);
        Assert.NotNull(bob.TeamContainer);
    }
}
