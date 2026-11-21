using System.Net;
using System.Net.Sockets;
using HostDeck.Core.Model;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Presentation;

public class ReachabilityTests
{
    [Fact]
    public async Task Checks_only_visible_connections_and_reports_status()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var openPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var tree = new ConnectionTree();
        var open = tree.Root.Add(new ConnectionNode("offen") { Hostname = "127.0.0.1" });
        open.Settings.Port = openPort;
        var closed = tree.Root.Add(new ConnectionNode("zu") { Hostname = "127.0.0.1" });
        closed.Settings.Port = 1;
        var folder = tree.Root.Add(new FolderNode("zugeklappt") { IsExpanded = false });
        var hidden = folder.Add(new ConnectionNode("versteckt") { Hostname = "127.0.0.1" });
        hidden.Settings.Port = openPort;

        var vm = new SessionTreeViewModel();
        vm.Load(tree);
        using var monitor = new ReachabilityMonitor(vm, () => true, null) { Timeout = TimeSpan.FromSeconds(2) };
        await monitor.CheckDueAsync(TestContext.Current.CancellationToken);

        TreeItemViewModel Item(string name) => vm.AllItems.Single(i => i.Name == name);
        Assert.Equal(Reachability.Online, Item("offen").Reachability);
        Assert.Equal(Reachability.Offline, Item("zu").Reachability);
        Assert.Equal(Reachability.Unknown, Item("versteckt").Reachability);   // Ordner zu ⇒ nicht geprüft

        Item("zugeklappt").IsExpanded = true;
        await monitor.CheckDueAsync(TestContext.Current.CancellationToken);    // Intervall: bereits geprüfte nicht erneut
        Assert.Equal(Reachability.Online, Item("versteckt").Reachability);
    }

    [Fact]
    public async Task Probe_handles_invalid_hosts()
    {
        Assert.False(await ReachabilityMonitor.ProbeAsync("host.invalid", 22, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }
}
