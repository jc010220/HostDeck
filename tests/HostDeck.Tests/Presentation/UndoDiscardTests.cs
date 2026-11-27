using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.ViewModels;
using static HostDeck.Tests.Presentation.MainViewModelTests;

namespace HostDeck.Tests.Presentation;

public sealed class UndoDiscardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hostdeck-undo-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Undo_restores_deleted_and_renamed_entries_step_by_step()
    {
        var (vm, dialogs, _, _) = Create();
        var a = new ConnectionNode("srv-a") { Hostname = "10.0.0.1" };
        vm.SessionTree.Add(a);
        vm.Persist();
        Assert.True(vm.CanUndo);                       // Hinzufügen ist rückgängig machbar

        a.Name = "renamed";
        vm.Persist();
        dialogs.ConfirmAnswer = true;
        vm.SessionTree.Tree.Root.Remove(a);
        vm.SessionTree.Load(vm.SessionTree.Tree);
        vm.Persist();
        Assert.DoesNotContain(vm.SessionTree.Tree.Connections, c => c.Id == a.Id);

        vm.UndoCommand.Execute(null);                  // Löschen zurück
        Assert.Equal("renamed", vm.SessionTree.Tree.Connections.Single(c => c.Id == a.Id).Name);
        vm.UndoCommand.Execute(null);                  // Umbenennen zurück
        Assert.Equal("srv-a", vm.SessionTree.Tree.Connections.Single(c => c.Id == a.Id).Name);
        vm.UndoCommand.Execute(null);                  // Hinzufügen zurück
        Assert.Empty(vm.SessionTree.Tree.Connections);
        Assert.False(vm.CanUndo);

        vm.RedoCommand.Execute(null);                  // Hinzufügen wieder da
        Assert.Equal("srv-a", vm.SessionTree.Tree.Connections.Single().Name);
        vm.RedoCommand.Execute(null);
        Assert.Equal("renamed", vm.SessionTree.Tree.Connections.Single().Name);
        vm.SessionTree.Tree.Connections.Single().Name = "new change";
        vm.Persist();
        Assert.False(vm.CanRedo);                      // neue Änderung ⇒ Wiederholen verfällt
    }

    [Fact]
    public async Task Unsaved_library_changes_can_be_discarded()
    {
        var file = Path.Combine(_dir, "team.xml");
        var t = new ConnectionTree();
        t.Root.Add(new ConnectionNode("sw01") { Hostname = "10.0.0.1" });
        new ConfConsExporter().ExportFile(t, file);
        var (vm, _, _, store) = Create();
        store.Location = Path.Combine(_dir, "me", "db");
        Assert.True(await vm.ConnectTeamLibraryAsync(file, false));
        var radius = vm.SessionTree.Tree.Vault.Add(new Credential { Name = "RADIUS", Username = "me", Password = "x" });
        var sw01 = vm.TeamContainer!.Children.OfType<ConnectionNode>().Single();
        sw01.Settings.CredentialId = radius.Id;        // persönlich – bleibt
        sw01.Hostname = "10.9.9.9";                   // Team-Änderung – wird verworfen
        vm.TeamContainer.Add(new ConnectionNode("new") { Hostname = "1.2.3.4" });
        vm.Persist();
        Assert.Equal(2, vm.TeamPendingChanges);

        Assert.True(await vm.DiscardLibraryChangesAsync(vm.Libraries.Single(), ask: false));
        Assert.Equal(0, vm.TeamPendingChanges);
        var restored = vm.TeamContainer!.Children.OfType<ConnectionNode>().Single();
        Assert.Equal("10.0.0.1", restored.Hostname);
        Assert.Equal(radius.Id, restored.Settings.CredentialId);
    }
}

public sealed class SplitViewCycleTests
{
    [Fact]
    public void Ctrl_tab_cycles_visible_sessions_in_split_view()
    {
        var ws = new WorkspaceViewModel();
        ws.SetLayout(WorkspaceLayout.TwoColumns);
        var left = ws.Open(new SessionTabViewModel(new ConnectionNode("left") { Hostname = "l" }, new CredentialVault(), isTransient: false), ws.Panes[0]);
        var right = ws.Open(new SessionTabViewModel(new ConnectionNode("right") { Hostname = "r" }, new CredentialVault(), isTransient: false), ws.Panes[1]);
        Assert.Same(right, ws.ActiveTab);
        ws.SelectNextTab();
        Assert.Same(left, ws.ActiveTab);
        ws.SelectNextTab(backwards: true);
        Assert.Same(right, ws.ActiveTab);
    }
}

public sealed class SftpFilterTests
{
    [Theory]
    [InlineData("syslog.1", "*.1", true)]
    [InlineData("syslog.1", "*.log", false)]
    [InlineData("app.log", "APP", true)]
    [InlineData("a.txt", "", true)]
    public void Name_filter_supports_text_and_wildcards(string name, string filter, bool expected) =>
        Assert.Equal(expected, SftpBrowserViewModel.NameMatches(name, filter));
}
