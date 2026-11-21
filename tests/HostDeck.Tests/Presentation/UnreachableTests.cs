using HostDeck.Core.Model;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Presentation;

/// <summary>Gemeldeter Fehler: Absturz beim Öffnen eines nicht erreichbaren Geräts.</summary>
public class UnreachableTests
{
    public static TheoryData<ProtocolKind, string, int> Targets => new()
    {
        { ProtocolKind.Ssh2, "127.0.0.1", 1 },          // Port geschlossen
        { ProtocolKind.Ssh2, "host.invalid", 22 },      // Name existiert nicht
        { ProtocolKind.Telnet, "127.0.0.1", 1 },
        { ProtocolKind.Telnet, "host.invalid", 23 },
        { ProtocolKind.Raw, "127.0.0.1", 1 },
        { ProtocolKind.Vnc, "127.0.0.1", 1 },
        { ProtocolKind.Vnc, "host.invalid", 5900 },
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task Unreachable_target_fails_gracefully(ProtocolKind protocol, string host, int port)
    {
        var (vm, dialogs, _, _) = MainViewModelTests.Create();
        dialogs.PromptAnswer = _ => "egal";
        var conn = new ConnectionNode("offline") { Hostname = host };
        conn.Settings.Protocol = protocol;
        conn.Settings.Port = port;
        conn.Settings.Username = "admin";
        conn.Settings.Password = "pw";
        vm.SessionTree.Add(conn);

        var errors = new List<(string, Exception)>();
        HostDeck.Presentation.Services.AppLog.Sink = (c, e) => errors.Add((c, e));
        try
        {
            vm.OpenCommand.Execute(vm.SessionTree.FindByNode(conn));
            var tab = vm.Workspace.ActiveTab!;
            if (tab.Vnc is { } vnc) await vnc.ConnectAsync(); // VNC verbindet sonst erst beim Anzeigen

            var until = DateTime.UtcNow.AddSeconds(20);
            while (tab.State is SessionState.Connecting or SessionState.Disconnected && DateTime.UtcNow < until)
                await Task.Delay(50, TestContext.Current.CancellationToken);

            Assert.Equal(SessionState.Failed, tab.State);
            // nur erwartete Netzwerkfehler – nichts Unerwartetes (andere, parallel laufende Tests ignorieren)
            Assert.DoesNotContain(errors, e => e.Item1.Contains($"{host}:{port}", StringComparison.Ordinal));
            vm.Workspace.Close(tab);                       // Schließen nach Fehler darf nicht werfen
            Assert.Equal(0, vm.Workspace.TabCount);
        }
        finally { HostDeck.Presentation.Services.AppLog.Sink = null; }
    }
}
