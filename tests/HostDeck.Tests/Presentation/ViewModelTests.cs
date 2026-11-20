using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Presentation;

public class SessionTreeViewModelTests
{
    private static SessionTreeViewModel LoadFixture()
    {
        var vm = new SessionTreeViewModel();
        vm.Load(new ConfConsImporter().ImportFile(Fixture.PathOf("confCons_v2_6.xml")).Tree);
        return vm;
    }

    private static TreeItemViewModel Item(SessionTreeViewModel vm, string path) => vm.AllItems.Single(i => i.Node.Path == path);

    [Fact]
    public void Mirrors_tree()
    {
        var vm = LoadFixture();
        Assert.Equal(3, vm.Items.Count);
        Assert.Equal(18, vm.AllItems.Count());
        Assert.Equal(12, vm.VisibleConnectionCount);
        Assert.Equal(ProtocolKind.Ssh2, Item(vm, "Folder1/Connection1.3").Protocol);
    }

    [Fact]
    public void Filter_shows_matches_and_their_ancestors()
    {
        var vm = LoadFixture();
        vm.FilterText = "inherit connection";

        var visible = vm.AllItems.Where(i => i.IsVisible).Select(i => i.Node.Path).ToList();
        Assert.Contains("Folder2/Folder2.2/InheritUsernameConnection", visible);
        Assert.Contains("Folder2/Folder2.2", visible);              // Vorfahr bleibt sichtbar
        Assert.DoesNotContain("Folder1/Connection1.1", visible);
        Assert.True(Item(vm, "Folder2/Folder2.2").IsExpanded);       // aufgeklappt

        vm.FilterText = "";
        Assert.All(vm.AllItems, i => Assert.True(i.IsVisible));
        Assert.Equal(12, vm.VisibleConnectionCount);
    }

    [Fact]
    public void Filter_on_folder_name_shows_whole_folder()
    {
        var vm = LoadFixture();
        vm.FilterText = "folder1";
        Assert.True(Item(vm, "Folder1/Connection1.2").IsVisible);
        Assert.False(Item(vm, "Folder2/Connection2.1").IsVisible);
    }

    [Fact]
    public void Filter_does_not_overwrite_saved_expansion_state()
    {
        var vm = LoadFixture();
        var folder = (FolderNode)Item(vm, "Folder2/Folder2.2").Node;
        var saved = folder.IsExpanded;
        vm.FilterText = "InheritPassword";
        vm.FilterText = "";
        Assert.Equal(saved, folder.IsExpanded);
    }

    [Fact]
    public void Drag_and_drop_moves_in_model_and_updates_inheritance()
    {
        var vm = LoadFixture();
        var conn = Item(vm, "Folder2/Folder2.2/InheritalAllConnection");
        var folder1 = Item(vm, "Folder1");

        Assert.True(vm.Move(conn, folder1, DropPosition.Into));

        Assert.Equal("Folder1/InheritalAllConnection", conn.Node.Path);
        Assert.Same(folder1, conn.Parent);
        Assert.Contains(conn, folder1.Children);
        // erbt jetzt das Protokoll von Folder1 (ICA) statt von Folder2.2 (RDP)
        Assert.Equal(ProtocolKind.Ica, conn.Protocol);
        Assert.Equal(ProtocolKind.Ica, SettingsResolver.Resolve(conn.Node).Protocol);
    }

    [Fact]
    public void Drop_before_and_after_reorders_siblings()
    {
        var vm = LoadFixture();
        var c1 = Item(vm, "Folder1/Connection1.1");
        var c3 = Item(vm, "Folder1/Connection1.3");

        Assert.True(vm.Move(c1, c3, DropPosition.After));
        var folder = (FolderNode)c1.Node.Parent!;
        Assert.Equal(["Connection1.2", "Connection1.3", "Connection1.1"], folder.Children.Select(c => c.Name));
        Assert.Equal(folder.Children.Select(c => c.Name), c1.Parent!.Children.Select(c => c.Name));

        Assert.True(vm.Move(c1, Item(vm, "Folder1/Connection1.2"), DropPosition.Before));
        Assert.Equal(["Connection1.1", "Connection1.2", "Connection1.3"], folder.Children.Select(c => c.Name));
    }

    [Fact]
    public void Cannot_drop_folder_into_itself_or_into_connection()
    {
        var vm = LoadFixture();
        var folder2 = Item(vm, "Folder2");
        Assert.False(vm.Move(folder2, Item(vm, "Folder2/Folder2.1"), DropPosition.Into));
        Assert.False(vm.Move(Item(vm, "Folder1/Connection1.1"), Item(vm, "Folder1/Connection1.2"), DropPosition.Into));
        Assert.Equal("Folder2", folder2.Node.Path);
    }

    [Fact]
    public void Drop_on_empty_space_moves_to_top_level()
    {
        var vm = LoadFixture();
        var c = Item(vm, "Folder1/Connection1.1");
        Assert.True(vm.Move(c, null, DropPosition.Into));
        Assert.Equal("Connection1.1", c.Node.Path);
        Assert.Same(c, vm.Items[^1]);
    }

    [Fact]
    public void Add_and_delete()
    {
        var vm = LoadFixture();
        vm.SelectedItem = Item(vm, "Folder1/Connection1.1");
        var added = vm.Add(new ConnectionNode("neu") { Hostname = "h" });
        Assert.Equal("Folder1/neu", added.Node.Path);

        vm.Delete(Item(vm, "Folder1"));
        Assert.Null(vm.Tree.FindByPath("Folder1"));
        Assert.Equal(2, vm.Items.Count);
        Assert.Null(vm.SelectedItem);
    }
}

public class WorkspaceViewModelTests
{
    private static SessionTabViewModel Tab(string name) =>
        new(new ConnectionNode(name) { Hostname = name }, new CredentialVault(), isTransient: false);

    [Fact]
    public void Layout_changes_create_panes_and_never_close_sessions()
    {
        var ws = new WorkspaceViewModel();
        ws.SetLayout(WorkspaceLayout.Grid);
        Assert.Equal(4, ws.Panes.Count);
        Assert.Equal((2, 2), (ws.Rows, ws.Columns));

        for (var i = 0; i < 4; i++) ws.Open(Tab($"s{i}"), ws.Panes[i]);
        ws.SetLayout(WorkspaceLayout.TwoRows);
        Assert.Equal(2, ws.Panes.Count);
        Assert.Equal((2, 1), (ws.Rows, ws.Columns));
        Assert.Equal(4, ws.TabCount);
        Assert.Equal(["s1", "s2", "s3"], ws.Panes[1].Tabs.Select(t => t.Title));

        ws.SetLayout(WorkspaceLayout.Single);
        Assert.Single(ws.Panes);
        Assert.Equal(4, ws.Panes[0].Tabs.Count);
        Assert.True(ws.Panes[0].IsActive);
    }

    [Fact]
    public void Open_targets_active_pane_and_selects_tab()
    {
        var ws = new WorkspaceViewModel();
        ws.SetLayout(WorkspaceLayout.TwoColumns);
        ws.ActivePane = ws.Panes[1];
        var t = ws.Open(Tab("a"));
        Assert.Same(t, ws.Panes[1].SelectedTab);
        Assert.Same(t, ws.ActiveTab);
        Assert.False(ws.Panes[0].IsActive);
    }

    [Fact]
    public void Close_selects_neighbour()
    {
        var ws = new WorkspaceViewModel();
        var a = ws.Open(Tab("a"));
        var b = ws.Open(Tab("b"));
        var c = ws.Open(Tab("c"));
        ws.Panes[0].SelectedTab = b;
        ws.Close(b);
        Assert.Same(c, ws.Panes[0].SelectedTab);
        ws.Close(c);
        ws.Close(a);
        Assert.Null(ws.Panes[0].SelectedTab);
        Assert.True(ws.Panes[0].IsEmpty);
    }

    [Fact]
    public void Move_tab_between_panes()
    {
        var ws = new WorkspaceViewModel();
        ws.SetLayout(WorkspaceLayout.TwoColumns);
        var a = ws.Open(Tab("a"), ws.Panes[0]);
        var b = ws.Open(Tab("b"), ws.Panes[0]);

        ws.MoveTab(a, ws.Panes[1]);
        Assert.Equal([b], ws.Panes[0].Tabs);
        Assert.Equal([a], ws.Panes[1].Tabs);
        Assert.Same(ws.Panes[1], ws.ActivePane);
        Assert.Same(b, ws.Panes[0].SelectedTab);
    }

    [Theory]
    [InlineData(2, WorkspaceLayout.TwoColumns)]
    [InlineData(3, WorkspaceLayout.Grid)]
    [InlineData(6, WorkspaceLayout.Grid)]
    public void Open_tiled_chooses_layout(int count, WorkspaceLayout expected)
    {
        var ws = new WorkspaceViewModel();
        ws.OpenTiled(Enumerable.Range(0, count).Select(i => Tab($"t{i}")).ToList());
        Assert.Equal(expected, ws.Layout);
        Assert.Equal(count, ws.TabCount);
        Assert.All(ws.Panes.Take(Math.Min(count, ws.Panes.Count)), p => Assert.NotEmpty(p.Tabs));
    }

    [Fact]
    public void Next_tab_wraps()
    {
        var ws = new WorkspaceViewModel();
        var a = ws.Open(Tab("a"));
        ws.Open(Tab("b"));
        ws.SelectNextTab();
        Assert.Same(a, ws.ActiveTab);
        ws.SelectNextTab(backwards: true);
        Assert.Equal("b", ws.ActiveTab!.Title);
    }
}

public class MainViewModelTests
{
    private sealed class FakeDialogs : IDialogService
    {
        public string? FileToOpen;
        public Queue<string?> Passwords = new();
        public List<string> Errors = [];
        public string? OpenFile(string title, string filter) => FileToOpen;
        public string? SaveFile(string title, string filter, string defaultFileName) => null;
        public string? AskMasterPassword(string? s, bool failed, int attempt, int max) => Passwords.Count > 0 ? Passwords.Dequeue() : null;
        public void ShowInfo(string title, string message) { }
        public void ShowError(string title, string message) => Errors.Add(message);
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Stored = new();
        public AppSettings Load() => Stored;
        public void Save(AppSettings settings) => Stored = settings;
    }

    private sealed class NoTheme : IThemeService { public AppTheme? Applied; public void Apply(AppTheme theme) => Applied = theme; }

    private static (MainViewModel Vm, FakeDialogs Dialogs, MemorySettings Settings) Create()
    {
        var d = new FakeDialogs();
        var s = new MemorySettings();
        return (new MainViewModel(d, s, new NoTheme()), d, s);
    }

    [Fact]
    public void Import_with_master_password_prompt()
    {
        var (vm, dialogs, settings) = Create();
        dialogs.FileToOpen = Fixture.PathOf("confCons_v2_6_passwordis_Password.xml");
        dialogs.Passwords.Enqueue("falsch");
        dialogs.Passwords.Enqueue("Password");

        vm.ImportMRemoteNGCommand.Execute(null);

        Assert.Empty(dialogs.Errors);
        Assert.Equal(12, vm.SessionTree.VisibleConnectionCount);
        Assert.Equal(dialogs.FileToOpen, settings.Stored.LastImportPath);
        // Standard: gemeinsame Zugangsdaten ⇒ Vault
        Assert.True(vm.SessionTree.Tree.Vault.Count > 0);
    }

    [Fact]
    public void Cancelled_password_on_startup_is_silent()
    {
        var (vm, dialogs, settings) = Create();
        settings.Stored.LastImportPath = Fixture.PathOf("confCons_v2_6_passwordis_Password.xml");
        vm.Initialize();
        Assert.Empty(dialogs.Errors);
        Assert.Equal("Laden abgebrochen", vm.StatusText);
    }

    [Fact]
    public void Quick_connect_prefers_existing_connection_then_parses()
    {
        var (vm, dialogs, _) = Create();
        dialogs.FileToOpen = Fixture.PathOf("confCons_v2_6.xml");
        vm.ImportMRemoteNGCommand.Execute(null);

        vm.QuickConnectText = "connection1.3";
        vm.QuickConnectCommand.Execute(null);
        var tab = vm.Workspace.ActiveTab!;
        Assert.False(tab.IsTransient);
        Assert.Equal(@"domainFolder1\userFolder1", tab.UserDisplay);
        Assert.Equal("", vm.QuickConnectText);

        vm.QuickConnectText = "ssh root@10.9.9.9:2200";
        vm.QuickConnectCommand.Execute(null);
        tab = vm.Workspace.ActiveTab!;
        Assert.True(tab.IsTransient);
        Assert.Equal("10.9.9.9:2200", tab.Endpoint);
        Assert.Equal(["ssh root@10.9.9.9:2200", "connection1.3"], vm.QuickConnectHistory);

        vm.SaveTransientSessionCommand.Execute(tab);
        Assert.NotNull(vm.SessionTree.FindByNode(tab.Connection));
    }

    [Fact]
    public void Open_folder_tiled()
    {
        var (vm, dialogs, _) = Create();
        dialogs.FileToOpen = Fixture.PathOf("confCons_v2_6.xml");
        vm.ImportMRemoteNGCommand.Execute(null);

        vm.OpenAllTiledCommand.Execute(vm.SessionTree.AllItems.Single(i => i.Node.Path == "Folder1"));
        Assert.Equal(WorkspaceLayout.Grid, vm.Workspace.Layout);
        Assert.Equal(3, vm.Workspace.TabCount);
    }

    [Fact]
    public void Theme_toggle_is_persisted()
    {
        var (vm, _, settings) = Create();
        vm.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppTheme.Dark, settings.Stored.Theme);
    }
}

public class JsonSettingsStoreTests
{
    [Fact]
    public void Roundtrip_and_corrupt_file_fallback()
    {
        var dir = Directory.CreateTempSubdirectory();
        var path = Path.Combine(dir.FullName, "sub", "settings.json");
        var store = new JsonSettingsStore(path);

        store.Save(new AppSettings { Theme = AppTheme.Dark, QuickConnectHistory = ["ssh a@b"], QuickConnectDefaultProtocol = ProtocolKind.Rdp });
        var loaded = store.Load();
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(ProtocolKind.Rdp, loaded.QuickConnectDefaultProtocol);
        Assert.Equal(["ssh a@b"], loaded.QuickConnectHistory);

        File.WriteAllText(path, "{ kaputt");
        Assert.Equal(AppTheme.System, store.Load().Theme);
        dir.Delete(recursive: true);
    }
}

public class TabSelectionTests
{
    [Fact]
    public void Exactly_one_selected_tab_per_pane()
    {
        var ws = new WorkspaceViewModel();
        ws.SetLayout(WorkspaceLayout.TwoColumns);
        var a = ws.Open(new SessionTabViewModel(new ConnectionNode("a"), new CredentialVault(), false), ws.Panes[0]);
        var b = ws.Open(new SessionTabViewModel(new ConnectionNode("b"), new CredentialVault(), false), ws.Panes[0]);
        Assert.False(a.IsSelected);
        Assert.True(b.IsSelected);

        ws.MoveTab(b, ws.Panes[1]);
        Assert.True(a.IsSelected);
        Assert.True(b.IsSelected); // jeweils ausgewählt in ihrem Bereich

        ws.Close(a);
        Assert.False(a.IsSelected);
    }
}
