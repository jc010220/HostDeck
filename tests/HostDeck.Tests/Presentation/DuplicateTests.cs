using HostDeck.Core.Model;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Presentation;

public class DuplicateTests
{
    [Fact]
    public void Duplicate_inserts_independent_copy_after_original_and_opens_editor()
    {
        var (vm, dialogs, _, store) = MainViewModelTests.Create();
        var original = vm.SessionTree.Add(new ConnectionNode("web01") { Hostname = "10.0.0.1" });
        var source = (ConnectionNode)original.Node;
        source.Settings.Protocol = ProtocolKind.Ssh2;
        source.Settings.Port = 2222;
        source.Settings.Username = "admin";
        source.Settings.Ssh.OpeningCommand = "sudo -i";
        vm.SessionTree.Add(new ConnectionNode("web99") { Hostname = "10.0.0.99" }); // danach

        dialogs.EditAnswer = e =>
        {
            Assert.Equal("web01 (copy)", e.Name);
            Assert.Equal("10.0.0.1", e.Hostname);
            e.Name = "web02";
            e.Hostname = "10.0.0.2";
            return e.Apply() is null;
        };
        vm.DuplicateCommand.Execute(original);

        var siblings = vm.SessionTree.Items.Select(i => i.Node.Name).ToList();
        Assert.Equal(["web01", "web02", "web99"], siblings);
        var copy = (ConnectionNode)vm.SessionTree.Items[1].Node;
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("10.0.0.2", copy.Hostname);
        Assert.Equal(2222, copy.Settings.Port);
        Assert.Equal("admin", copy.Settings.Username);
        Assert.Equal("sudo -i", copy.Settings.Ssh.OpeningCommand);
        Assert.Equal("10.0.0.1", source.Hostname);

        // unabhängige Kopie: Änderungen wirken nicht aufs Original
        copy.Settings.Port = 22;
        copy.Settings.Ssh.OpeningCommand = null;
        Assert.Equal(2222, source.Settings.Port);
        Assert.Equal("sudo -i", source.Settings.Ssh.OpeningCommand);
        Assert.Equal(3, store.Saved.Connections.Count());
    }

    [Fact]
    public void Cancelling_the_editor_discards_the_copy()
    {
        var (vm, dialogs, _, _) = MainViewModelTests.Create();
        var original = vm.SessionTree.Add(new ConnectionNode("web01") { Hostname = "10.0.0.1" });
        dialogs.EditAnswer = _ => false;
        vm.DuplicateCommand.Execute(original);
        Assert.Single(vm.SessionTree.Items);
    }
}
