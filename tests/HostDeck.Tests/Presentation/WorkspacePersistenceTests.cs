using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Presentation;

public class WorkspacePersistenceTests
{
    private static void Import(MainViewModel vm, MainViewModelTests.FakeDialogs d)
    {
        d.FileToOpen = Fixture.PathOf("confCons_v2_6.xml");
        vm.ImportMRemoteNGCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    }

    private static TreeItemViewModel Item(MainViewModel vm, string path) => vm.SessionTree.AllItems.Single(i => i.Node.Path == path);

    [Fact]
    public void Last_workspace_is_restored_on_next_start()
    {
        var (vm, d, settings, store) = MainViewModelTests.Create();
        Import(vm, d);
        vm.Workspace.SetLayout(WorkspaceLayout.TwoColumns);
        vm.Workspace.Open(new SessionTabViewModel((ConnectionNode)Item(vm, "Folder1/Connection1.1").Node, store.Saved.Vault, false), vm.Workspace.Panes[0]);
        vm.OpenInPane(Item(vm, "Folder2/Connection2.1"), vm.Workspace.Panes[1]);
        vm.QuickConnectText = "rdp 10.9.9.9";
        vm.QuickConnectCommand.Execute(null);  // transient ⇒ wird nicht gespeichert
        vm.Shutdown();

        Assert.Equal(WorkspaceLayout.TwoColumns, settings.Stored.LastWorkspace!.Layout);
        Assert.Equal(2, settings.Stored.LastWorkspace.ConnectionCount);

        // Neustart
        var vm2 = new MainViewModel(d, settings, new MainViewModelTests.NoTheme(), store);
        vm2.Initialize();
        Assert.Equal(WorkspaceLayout.TwoColumns, vm2.Workspace.Layout);
        Assert.Equal(["Connection1.1"], vm2.Workspace.Panes[0].Tabs.Select(t => t.Title));
        Assert.Equal(["Connection2.1"], vm2.Workspace.Panes[1].Tabs.Select(t => t.Title));
    }

    [Fact]
    public void Named_workspaces_via_palette_and_deleted_connections_are_skipped()
    {
        var (vm, d, settings, _) = MainViewModelTests.Create();
        Import(vm, d);
        vm.OpenAllTiledCommand.Execute(Item(vm, "Folder1"));
        d.PromptAnswer = _ => "ACME Web";
        vm.SaveWorkspaceCommand.Execute(null);
        Assert.Equal(3, settings.Stored.SavedWorkspaces["ACME Web"].ConnectionCount);

        foreach (var t in vm.Workspace.AllTabs.ToList()) vm.Workspace.Close(t);
        vm.DeleteCommand.Execute(Item(vm, "Folder1/Connection1.2"));

        vm.Palette.Query = ">acme web";
        var open = vm.Palette.Results.First(r => r.Title.StartsWith("Open workspace", StringComparison.Ordinal));
        vm.ExecutePaletteItem(open);
        Assert.Equal(WorkspaceLayout.Grid, vm.Workspace.Layout);
        Assert.Equal(2, vm.Workspace.TabCount); // gelöschte Verbindung übersprungen
    }

    [Fact]
    public void Import_offers_to_fix_unchecked_rdp_certificates()
    {
        var (vm, d, _, store) = MainViewModelTests.Create();
        d.ConfirmAnswer = true;
        Import(vm, d);
        Assert.DoesNotContain(store.Saved.Connections, c =>
            SettingsResolver.Resolve(c) is { Protocol: ProtocolKind.Rdp, Rdp.AuthenticationLevel: RdpAuthenticationLevel.NoAuth });

        var (vm2, d2, _, store2) = MainViewModelTests.Create();
        d2.ConfirmAnswer = false;   // abgelehnt ⇒ Werte bleiben wie in mRemoteNG
        Import(vm2, d2);
        Assert.Contains(store2.Saved.Connections, c =>
            SettingsResolver.Resolve(c) is { Protocol: ProtocolKind.Rdp, Rdp.AuthenticationLevel: RdpAuthenticationLevel.NoAuth });
    }

    [Fact]
    public void Palette_connection_opens_tab_and_records_recent()
    {
        var (vm, d, settings, _) = MainViewModelTests.Create();
        Import(vm, d);
        vm.Palette.Query = "c211";
        var hit = vm.Palette.Results[0];
        Assert.Equal("Connection2.1.1", hit.Title);
        vm.ExecutePaletteItem(hit, split: true);
        Assert.Equal(WorkspaceLayout.TwoColumns, vm.Workspace.Layout);
        Assert.Equal(hit.Connection!.Id, settings.Stored.RecentConnections[0]);
        Assert.True(Item(vm, "Folder2/Folder2.1/Connection2.1.1").IsSelected);
    }
}
