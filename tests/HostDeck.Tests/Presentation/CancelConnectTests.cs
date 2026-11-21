using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HostDeck.Core.Model;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Presentation;

/// <summary>Verbindungsaufbau, der „ewig lädt“, muss sich sofort abbrechen lassen.</summary>
public sealed class CancelConnectTests : IDisposable
{
    // Server nimmt TCP-Verbindungen an, sendet aber nie etwas (SSH-Banner/RFB-Version bleiben aus)
    private readonly TcpListener _silent = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _accepted = [];
    private int Port => ((IPEndPoint)_silent.LocalEndpoint).Port;

    public CancelConnectTests()
    {
        _silent.Start();
        _ = Task.Run(async () =>
        {
            try { while (true) _accepted.Add(await _silent.AcceptTcpClientAsync()); }
            catch (Exception) { /* Listener beendet */ }
        });
    }

    public void Dispose()
    {
        _silent.Stop();
        foreach (var c in _accepted) c.Dispose();
    }

    private static async Task WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20, TestContext.Current.CancellationToken);
    }

    private (MainViewModel Vm, SessionTabViewModel Tab) Open(ProtocolKind protocol)
    {
        var (vm, _, _, _) = MainViewModelTests.Create();
        var conn = new ConnectionNode("haengt") { Hostname = "127.0.0.1" };
        conn.Settings.Protocol = protocol;
        conn.Settings.Port = Port;
        conn.Settings.Username = "admin";
        conn.Settings.Password = "pw";
        vm.SessionTree.Add(conn);
        vm.OpenCommand.Execute(vm.SessionTree.FindByNode(conn));
        return (vm, vm.Workspace.ActiveTab!);
    }

    [Fact]
    public async Task Ssh_connect_can_be_cancelled_via_command_and_escape()
    {
        var (_, tab) = Open(ProtocolKind.Ssh2);
        await WaitFor(() => tab.IsConnecting);
        Assert.True(tab.IsConnecting);
        await Task.Delay(300, TestContext.Current.CancellationToken);   // „lädt lange“

        var sw = Stopwatch.StartNew();
        tab.CancelConnectCommand.Execute(null);
        await WaitFor(() => tab.State == SessionState.Disconnected);
        Assert.Equal(SessionState.Disconnected, tab.State);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Abbruch dauerte {sw.ElapsedMilliseconds} ms");

        // Enter verbindet erneut, Esc bricht wieder ab
        tab.Terminal!.OnUserInput("\r");
        await WaitFor(() => tab.IsConnecting);
        tab.Terminal.OnUserInput("\u001b");
        await WaitFor(() => tab.State == SessionState.Disconnected);
        Assert.Equal(SessionState.Disconnected, tab.State);
    }

    [Fact]
    public async Task Vnc_connect_can_be_cancelled()
    {
        var (_, tab) = Open(ProtocolKind.Vnc);
        var connect = tab.Vnc!.ConnectAsync();
        await WaitFor(() => tab.IsConnecting);
        tab.CancelConnectCommand.Execute(null);
        await connect.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(SessionState.Disconnected, tab.State);
        Assert.Equal("Connecting cancelled.", tab.Vnc.Message);
    }

    [Fact]
    public async Task Closing_tab_while_connecting_stops_the_attempt()
    {
        var (vm, tab) = Open(ProtocolKind.Ssh2);
        await WaitFor(() => tab.IsConnecting);
        vm.Workspace.Close(tab);
        await WaitFor(() => tab.State != SessionState.Connecting);
        Assert.NotEqual(SessionState.Connected, tab.State);
        Assert.NotEqual(SessionState.Connecting, tab.State);
    }

    [Fact]
    public void Palette_offers_cancel_while_connecting()
    {
        var (vm, tab) = Open(ProtocolKind.Ssh2);
        tab.State = SessionState.Connecting;
        vm.Palette.Query = ">abbrechen";
        Assert.Contains(vm.Palette.Results, r => r.Title.StartsWith("Cancel connecting", StringComparison.Ordinal));
    }
}
