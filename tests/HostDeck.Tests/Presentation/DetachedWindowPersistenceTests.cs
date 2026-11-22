using HostDeck.Core.Model;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.ViewModels;
using static HostDeck.Tests.Presentation.MainViewModelTests;

namespace HostDeck.Tests.Presentation;

public class DetachedWindowPersistenceTests
{
    [Fact]
    public void Detached_windows_with_their_tabs_and_position_are_restored()
    {
        var (vm, _, settings, store) = Create();
        foreach (var name in new[] { "web01", "web02", "db01" })
            vm.SessionTree.Add(new ConnectionNode(name) { Hostname = "10.0.0.1", Settings = { Protocol = ProtocolKind.Rdp } });
        foreach (var item in vm.SessionTree.Items) vm.OpenCommand.Execute(item);
        var db = vm.Workspace.AllTabs.Single(t => t.Title == "db01");
        var detached = vm.DetachTab(db);
        detached.Placement = new WindowPlacement { Left = 2000, Top = 100, Width = 1200, Height = 800 };

        vm.CaptureWorkspaceForShutdown();
        vm.CloseDetachedWorkspace(detached);                  // so schließt das Hauptfenster die Zusatzfenster
        vm.Shutdown();
        var saved = settings.Stored.LastWorkspace!;
        Assert.Equal(3, saved.ConnectionCount);
        var window = Assert.Single(saved.Windows);
        Assert.Equal(2000, window.Placement!.Left);

        var restoredWindows = new List<WorkspaceViewModel>();
        var again = new MainViewModel(new FakeDialogs(), settings, new NoTheme(), store);
        again.DetachedWindowRequested += restoredWindows.Add;
        again.Initialize();
        Assert.Equal(2, again.Workspace.TabCount);
        var restored = Assert.Single(restoredWindows);
        Assert.Equal("db01", Assert.Single(restored.AllTabs).Title);
        Assert.Equal(1200, restored.Placement!.Width);
    }
}
