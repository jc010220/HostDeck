using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.Team;
using HostDeck.Presentation.ViewModels;
using static HostDeck.Tests.Presentation.MainViewModelTests;

namespace HostDeck.Tests.Presentation;

/// <summary>Mehrere Bibliotheken: z. B. Team-Datei auf dem Share und eigene Datei auf OneDrive.</summary>
public sealed class MultiLibraryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hostdeck-libs-").FullName;
    private string TeamFile => Path.Combine(_dir, "share", "confCons.xml");
    private string OneDriveFile => Path.Combine(_dir, "OneDrive", "HostDeck", "confCons.xml");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static void Write(string file, params TreeNode[] topLevel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tree = new ConnectionTree();
        foreach (var n in topLevel) tree.Root.Add(n);
        new ConfConsExporter().ExportFile(tree, file);
    }

    private static ConnectionTree Read(string file) => new ConfConsImporter().ImportFile(file).Tree;

    private (MainViewModel Vm, FakeDialogs Dialogs, MemorySettings Settings, MemoryStore Store) TwoLibraries()
    {
        Write(TeamFile, new ConnectionNode("sw01") { Hostname = "10.0.0.1", Settings = { Protocol = ProtocolKind.Ssh2 } });
        Write(OneDriveFile, new ConnectionNode("lab01") { Hostname = "192.168.1.10", Settings = { Protocol = ProtocolKind.Ssh2 } });
        var (vm, dialogs, settings, store) = Create();
        Assert.True(vm.ConnectTeamLibraryAsync(TeamFile, false).GetAwaiter().GetResult());
        Assert.True(vm.ConnectTeamLibraryAsync(OneDriveFile, false, name: "STFW").GetAwaiter().GetResult());
        return (vm, dialogs, settings, store);
    }

    private static FolderNode Folder(MainViewModel vm, string name) =>
        vm.SessionTree.Tree.Root.Children.OfType<FolderNode>().Single(f => f.Name == name);

    [Fact]
    public void Each_library_gets_its_own_folder_and_changes_go_to_the_right_file()
    {
        var (vm, _, settings, _) = TwoLibraries();
        Assert.Equal(2, vm.Libraries.Count);
        Assert.Contains(Folder(vm, "Team").Children, n => n.Name == "sw01");
        Assert.Contains(Folder(vm, "STFW").Children, n => n.Name == "lab01");
        Assert.Single(settings.Stored.Libraries);
        Assert.Equal(TeamFile, settings.Stored.TeamLibraryPath); // erste Bibliothek wie bisher

        Folder(vm, "STFW").Add(new ConnectionNode("lab02") { Hostname = "192.168.1.11" });
        ((ConnectionNode)Folder(vm, "Team").Children[0]).Hostname = "10.0.0.11";
        vm.Persist();
        Assert.Equal(2, vm.TeamPendingChanges);

        Assert.True(vm.SyncTeam(write: true));
        Assert.Equal(0, vm.TeamPendingChanges);
        Assert.Contains(Read(OneDriveFile).Connections, c => c.Name == "lab02");
        Assert.DoesNotContain(Read(TeamFile).Connections, c => c.Name == "lab02");
        Assert.Equal("10.0.0.11", Read(TeamFile).Connections.Single().Hostname);
        Assert.Contains("STFW", vm.TeamStatus);
    }

    [Fact]
    public void Moving_an_entry_from_one_library_to_another_moves_it_between_the_files()
    {
        var (vm, _, _, _) = TwoLibraries();
        var sw01 = Folder(vm, "Team").Children.Single();
        Folder(vm, "Team").Remove(sw01);
        Folder(vm, "STFW").Add(sw01);
        vm.Persist();
        Assert.True(vm.SyncTeam(write: true));
        Assert.DoesNotContain(Read(TeamFile).Connections, c => c.Name == "sw01");
        Assert.Contains(Read(OneDriveFile).Connections, c => c.Name == "sw01");
    }

    [Fact]
    public void Libraries_are_restored_after_restart_and_disconnecting_one_keeps_the_other()
    {
        var (vm, _, settings, store) = TwoLibraries();
        var restartedStore = new MemoryStore { Saved = store.Saved };
        foreach (var (k, v) in store.Documents) restartedStore.Documents[k] = v;
        var restarted = new MainViewModel(new FakeDialogs(), new MemorySettings { Stored = settings.Stored }, new NoTheme(), restartedStore);
        restarted.Initialize();
        Assert.Equal(2, restarted.Libraries.Count);
        Assert.Contains(Folder(restarted, "STFW").Children, n => n.Name == "lab01");

        var second = restarted.Libraries.Single(l => !l.IsPrimary);
        Assert.True(restarted.DisconnectLibrary(second, ask: false));
        Assert.Single(restarted.Libraries);
        Assert.DoesNotContain(restarted.SessionTree.Tree.Root.Children, n => n.Name == "STFW");
        Assert.Contains(Folder(restarted, "Team").Children, n => n.Name == "sw01");
        Assert.True(File.Exists(OneDriveFile)); // Datei selbst bleibt
    }

    [Fact]
    public async Task The_same_file_or_a_copy_of_it_cannot_be_connected_twice()
    {
        var (vm, dialogs, _, _) = TwoLibraries();
        // gleiche Datei ⇒ wird nur neu eingelesen, keine dritte Bibliothek
        Assert.True(await vm.ConnectTeamLibraryAsync(OneDriveFile, false));
        Assert.Equal(2, vm.Libraries.Count);

        // Kopie mit denselben IDs ⇒ abgelehnt (doppelte IDs im Baum)
        var copy = Path.Combine(_dir, "copy", "confCons.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(TeamFile, copy);
        Assert.False(await vm.ConnectTeamLibraryAsync(copy, false, name: "Copy"));
        Assert.Contains(dialogs.Errors, e => e.Contains("copy"));
        Assert.Equal(2, vm.Libraries.Count);
    }

    [Fact]
    public void Read_only_applies_per_library()
    {
        var (vm, _, _, _) = TwoLibraries();
        var team = vm.Libraries.Single(l => l.IsPrimary);
        var settingsVm = new SettingsViewModel(vm.Settings) { LibraryRows = [] };
        vm.Settings.TeamReadOnly = true;
        Assert.True(vm.IsReadOnlyTeamNode(Folder(vm, "Team").Children[0]));
        Assert.False(vm.IsReadOnlyTeamNode(Folder(vm, "STFW").Children[0]));
        Assert.True(vm.IsReadOnlyTeamDestination(Folder(vm, "Team")));
        Assert.False(vm.IsReadOnlyTeamDestination(Folder(vm, "STFW")));
        Assert.True(team.ReadOnly);
        Assert.Empty(settingsVm.Libraries);
    }

    [Fact]
    public async Task A_missing_file_is_created_as_a_new_protected_library()
    {
        var (vm, dialogs, _, _) = Create();
        var file = Path.Combine(_dir, "OneDrive", "new", "confCons.xml");
        dialogs.ConfirmAnswer = true;
        dialogs.PromptAnswer = _ => "Personal#Library1";
        Assert.True(await vm.ConnectTeamLibraryAsync(file, false));
        Assert.True(File.Exists(file));
        Assert.Empty(dialogs.Errors);

        vm.TeamContainer!.Add(new ConnectionNode("db01") { Hostname = "10.9.0.1", Settings = { Password = "Db-Secret!" } });
        vm.Persist();
        Assert.True(vm.SyncTeam(write: true));
        var raw = File.ReadAllText(file);
        Assert.DoesNotContain("Db-Secret!", raw);
        // ohne Passwort nicht lesbar, mit Passwort schon
        Assert.ThrowsAny<ConfConsAuthenticationException>(() => new ConfConsImporter().ImportFile(file));
        var withPassword = new ConfConsImporter(new ConfConsImportOptions { MasterPassword = "Personal#Library1" }).ImportFile(file).Tree;
        Assert.Equal("Db-Secret!", withPassword.Connections.Single().Settings.Password);
    }

    [Fact]
    public async Task Shared_snippets_are_saved_to_the_library_they_belong_to()
    {
        var (vm, _, _, _) = TwoLibraries();
        await vm.SyncTeamSnippetsAsync();
        var added = vm.Snippets.Add("Team only", "uptime", team: true); // neu ⇒ erste beschreibbare Bibliothek
        await vm.SyncTeamSnippetsAsync();
        var stfw = vm.Libraries.Single(l => !l.IsPrimary);
        Assert.Contains(TeamSnippetFile.Read(TeamSnippetFile.PathFor(TeamFile), null), e => e.Name == "Team only");
        Assert.False(File.Exists(TeamSnippetFile.PathFor(OneDriveFile)));

        // Snippet einer anderen Bibliothek bleibt dort
        var lab = vm.Snippets.Items.Single(s => s.Name == "Team only");
        Assert.Equal(vm.Libraries.Single(l => l.IsPrimary).Id, lab.LibraryId);
        lab.LibraryId = stfw.Id;
        lab.Command = "uptime -p";
        vm.Snippets.Save(lab);
        await vm.SyncTeamSnippetsAsync();
        Assert.Contains(TeamSnippetFile.Read(TeamSnippetFile.PathFor(OneDriveFile), null), e => e.Command == "uptime -p");
        Assert.NotNull(added);
    }

    [Fact]
    public void Settings_rows_rename_and_add_libraries()
    {
        var (vm, dialogs, _, _) = TwoLibraries();
        var third = Path.Combine(_dir, "team2", "confCons.xml");
        Write(third, new ConnectionNode("fw01") { Hostname = "10.5.0.1" });
        dialogs.SettingsAnswer = s =>
        {
            s.Libraries.Single(r => r.Name == "STFW").Name = "Lab";
            s.Libraries.Add(new SettingsViewModel.LibraryRow { Name = "Network team", Path = third });
            return true;
        };
        vm.OpenSettingsCommand.Execute(null);
        SpinWait.SpinUntil(() => vm.Libraries.Count == 3, TimeSpan.FromSeconds(10));
        Assert.Equal(3, vm.Libraries.Count);
        Assert.Contains(Folder(vm, "Lab").Children, n => n.Name == "lab01");
        Assert.Contains(Folder(vm, "Network team").Children, n => n.Name == "fw01");
        Assert.Equal("Lab", vm.Settings.Libraries.Single(l => l.Path == OneDriveFile).Name);
    }

    [Fact]
    public void Suggested_names_use_the_folder_for_confCons()
    {
        Assert.Equal("HostDeck", MainViewModel.SuggestLibraryName(Path.Combine(_dir, "OneDrive", "HostDeck", "confCons.xml")));
        Assert.Equal("Lab", MainViewModel.SuggestLibraryName(Path.Combine(_dir, "Lab.xml")));
    }
}
