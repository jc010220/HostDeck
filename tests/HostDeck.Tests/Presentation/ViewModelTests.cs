using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.ViewModels;
using HostDeck.Protocols.Ssh;
using System.Collections.Concurrent;

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
    internal sealed class FakeDialogs : IDialogService
    {
        public string? FileToOpen;
        public Queue<string?> Passwords = new();
        public List<string> Errors = [];
        public bool ConfirmAnswer = true;
        public int ConfirmCalls;
        public (string, string)? PasswordChange;
        public string? OpenFile(string title, string filter) => FileToOpen;
        public string? SaveFileAnswer;
        public string? LastSuggestedFileName;
        public string? SaveFile(string title, string filter, string defaultFileName) { LastSuggestedFileName = defaultFileName; return SaveFileAnswer; }
        public List<int> PasswordPromptThreads = [];
        public string? AskMasterPassword(string? s, bool failed, int attempt, int max)
        {
            PasswordPromptThreads.Add(Environment.CurrentManagedThreadId);
            return Passwords.Count > 0 ? Passwords.Dequeue() : null;
        }
        public void ShowInfo(string title, string message) { }
        public void ShowError(string title, string message) => Errors.Add(message);
        public bool Confirm(string title, string message) { ConfirmCalls++; return ConfirmAnswer; }
        public (string Current, string New)? AskChangeMasterPassword() => PasswordChange;
        public HostKeyDecision HostKeyAnswer = HostKeyDecision.AcceptAndSave;
        public ConcurrentQueue<HostKeyInfo> HostKeyPrompts = new();
        public HostKeyDecision AskHostKey(HostKeyInfo info) { HostKeyPrompts.Enqueue(info); return HostKeyAnswer; }
        public Func<string, string?> PromptAnswer = _ => null;
        public string? Prompt(string title, string message, string defaultValue = "", bool secret = false) => PromptAnswer(message);
        public string? PickFolder(string title) => null;
        public Func<ConnectionEditorViewModel, bool> EditAnswer = e => e.Apply() is null;
        public bool EditConnection(ConnectionEditorViewModel editor) => EditAnswer(editor);
        public Func<SettingsViewModel, bool> SettingsAnswer = _ => true;
        public bool EditSettings(SettingsViewModel settings) => SettingsAnswer(settings);
        public Action<CredentialVaultViewModel> VaultAction = _ => { };
        public void ManageVault(CredentialVaultViewModel vault) => VaultAction(vault);
    }

    internal sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Stored = new();
        public AppSettings Load() => Stored;
        public void Save(AppSettings settings) => Stored = settings;
    }

    internal sealed class NoTheme : IThemeService { public AppTheme? Applied; public void Apply(AppTheme theme) => Applied = theme; }

    /// <summary>Speichert über den echten mRemoteNG-Exporter – prüft so nebenbei die Vollständigkeit.</summary>
    internal sealed class MemoryStore : IConnectionStore
    {
        public ConnectionTree Saved = new();
        public int SaveCount;
        public string CurrentPassword = "altes-passwort";
        public string Location { get; set; } = "memory";
        public ConnectionTree Load() => LoadFailure is null ? Saved : throw LoadFailure;
        public int LastSavedCount;
        public void Save(ConnectionTree tree) { Saved = tree; SaveCount++; LastSavedCount = tree.Connections.Count(); }
        public bool HasDeviceUnlock { get; private set; }
        public bool DeviceUnlockSupported => true;
        public void SetDeviceUnlock(bool enabled) => HasDeviceUnlock = enabled;
        public void ChangeMasterPassword(string currentPassword, string newPassword)
        {
            if (currentPassword != CurrentPassword) throw new UnauthorizedAccessException();
            CurrentPassword = newPassword;
        }
        public readonly Dictionary<string, string> Documents = new();
        public string? ReadDocument(string name) => Documents.GetValueOrDefault(name);
        public List<string> Backups = [];
        public string Backup(string targetDirectory) { Backups.Add(targetDirectory); return Path.Combine(targetDirectory, "backup.hdk"); }
        public string BackupTo(string targetFile) { Backups.Add(targetFile); return targetFile; }
        public int CountConnections() => LastSavedCount; // wie die echte DB: Stand des letzten Speicherns
        public Exception? LoadFailure;
        public void WriteDocument(string name, string json) => Documents[name] = json;
        public readonly ConcurrentDictionary<string, string> HostKeys = new();
        public string? Find(string host, int port, string keyType) => HostKeys.GetValueOrDefault($"{host}:{port}:{keyType}");
        public void Remember(string host, int port, string keyType, string fingerprint) => HostKeys[$"{host}:{port}:{keyType}"] = fingerprint;
    }

    internal static (MainViewModel Vm, FakeDialogs Dialogs, MemorySettings Settings, MemoryStore Store) Create()
    {
        var d = new FakeDialogs();
        var s = new MemorySettings();
        var store = new MemoryStore();
        var vm = new MainViewModel(d, s, new NoTheme(), store);
        vm.Initialize();
        return (vm, d, s, store);
    }

    private static void ImportFixture(MainViewModel vm, FakeDialogs dialogs, string file = "confCons_v2_6.xml")
    {
        dialogs.FileToOpen = Fixture.PathOf(file);
        vm.ImportMRemoteNGCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    }

    [Fact]
    public void Import_with_master_password_prompt_is_persisted()
    {
        var (vm, dialogs, settings, store) = Create();
        dialogs.Passwords.Enqueue("falsch");
        dialogs.Passwords.Enqueue("Password");
        ImportFixture(vm, dialogs, "confCons_v2_6_passwordis_Password.xml");

        Assert.Empty(dialogs.Errors);
        Assert.Equal(12, vm.SessionTree.VisibleConnectionCount);
        Assert.Equal(12, store.Saved.Connections.Count());
        Assert.True(store.Saved.Vault.Count > 0);          // gemeinsame Logins ⇒ Vault
        Assert.EndsWith("confCons_v2_6_passwordis_Password.xml", settings.Stored.LastImportPath);
    }

    [Fact]
    public void Second_import_goes_into_own_folder_with_new_ids()
    {
        var (vm, dialogs, _, store) = Create();
        ImportFixture(vm, dialogs);
        ImportFixture(vm, dialogs);

        Assert.Empty(dialogs.Errors);
        Assert.Equal(24, store.Saved.Connections.Count());
        var importFolder = store.Saved.Root.Children.OfType<FolderNode>().Single(f => f.Name.StartsWith("Import confCons_v2_6"));
        Assert.Equal(3, importFolder.Children.Count);
        var ids = store.Saved.Root.Descendants().Select(n => n.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(@"domainFolder1\userFolder1:folder1",
            Fixture.EffectiveCredentials(store.Saved, importFolder.Name + "/Folder1/Connection1.1"));
    }

    [Fact]
    public void Structural_changes_are_saved_immediately()
    {
        var (vm, dialogs, _, store) = Create();
        ImportFixture(vm, dialogs);
        var before = store.SaveCount;

        var item = vm.SessionTree.AllItems.Single(i => i.Node.Path == "Folder1/Connection1.1");
        vm.SessionTree.Move(item, vm.SessionTree.AllItems.Single(i => i.Node.Path == "Folder2"), DropPosition.Into);
        Assert.Equal(before + 1, store.SaveCount);
        Assert.NotNull(store.Saved.FindByPath("Folder2/Connection1.1"));

        vm.NewFolderCommand.Execute(null);
        vm.DeleteCommand.Execute(item);
        Assert.Equal(before + 3, store.SaveCount);
        Assert.Null(store.Saved.FindByPath("Folder2/Connection1.1"));
    }

    [Fact]
    public void Shutdown_saves_expansion_state()
    {
        var (vm, dialogs, _, store) = Create();
        ImportFixture(vm, dialogs);
        var folder = vm.SessionTree.AllItems.Single(i => i.Node.Path == "Folder1");
        folder.IsExpanded = !folder.IsExpanded;
        vm.Shutdown();
        Assert.Equal(folder.IsExpanded, ((FolderNode)store.Saved.FindByPath("Folder1")!).IsExpanded);
    }

    [Fact]
    public void First_start_offers_mRemoteNG_import_once()
    {
        var d = new FakeDialogs();
        var s = new MemorySettings();
        var store = new MemoryStore();
        var vm = new MainViewModel(d, s, new NoTheme(), store);
        vm.Initialize(Fixture.PathOf("confCons_v2_6.xml"));
        var callsAfterFirstStart = d.ConfirmCalls;           // Übernahme-Angebot (+ Sicherheitsprüfung)
        Assert.True(callsAfterFirstStart >= 1);
        Assert.Equal(12, store.Saved.Connections.Count());

        var vm2 = new MainViewModel(d, s, new NoTheme(), new MemoryStore());
        vm2.Initialize(Fixture.PathOf("confCons_v2_6.xml"));
        Assert.Equal(callsAfterFirstStart, d.ConfirmCalls); // nicht erneut fragen
    }

    [Fact]
    public void Change_master_password_and_device_unlock()
    {
        var (vm, dialogs, _, store) = Create();
        dialogs.PasswordChange = ("falsch", "neues-passwort");
        vm.ChangeMasterPasswordCommand.Execute(null);
        Assert.Single(dialogs.Errors);
        Assert.Equal("altes-passwort", store.CurrentPassword);

        dialogs.PasswordChange = ("altes-passwort", "neues-passwort");
        vm.ChangeMasterPasswordCommand.Execute(null);
        Assert.Equal("neues-passwort", store.CurrentPassword);

        vm.IsDeviceUnlockEnabled = true;
        Assert.True(store.HasDeviceUnlock);
        vm.IsDeviceUnlockEnabled = false;
        Assert.False(store.HasDeviceUnlock);
    }

    [Fact]
    public void Quick_connect_prefers_existing_connection_then_parses()
    {
        var (vm, dialogs, _, _) = Create();
        ImportFixture(vm, dialogs);

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
        var (vm, dialogs, _, _) = Create();
        ImportFixture(vm, dialogs);
        vm.OpenAllTiledCommand.Execute(vm.SessionTree.AllItems.Single(i => i.Node.Path == "Folder1"));
        Assert.Equal(WorkspaceLayout.Grid, vm.Workspace.Layout);
        Assert.Equal(3, vm.Workspace.TabCount);
    }

    [Fact]
    public void Theme_toggle_is_persisted()
    {
        var (vm, _, settings, _) = Create();
        vm.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppTheme.Dark, settings.Stored.Theme);
        vm.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppTheme.Light, settings.Stored.Theme);
        vm.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppTheme.System, settings.Stored.Theme);
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
