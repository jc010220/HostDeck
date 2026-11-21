using HostDeck.Core.Model;
using HostDeck.Presentation.QuickConnect;
using static HostDeck.Tests.Presentation.MainViewModelTests;

namespace HostDeck.Tests.Presentation;

public class QuickConnectPortTests
{
    [Theory]
    [InlineData("server01:3389", ProtocolKind.Rdp)]
    [InlineData("10.0.0.1:22", ProtocolKind.Ssh2)]
    [InlineData("switch01:23", ProtocolKind.Telnet)]
    [InlineData("kvm:5901", ProtocolKind.Vnc)]
    [InlineData("fw01:443", ProtocolKind.Https)]
    [InlineData("printer:80", ProtocolKind.Http)]
    [InlineData("admin@db01:2222", ProtocolKind.Rdp)]   // unbekannter Port ⇒ Standard
    [InlineData("ssh server01:3389", ProtocolKind.Ssh2)] // ausdrückliches Protokoll gewinnt
    [InlineData("server01", ProtocolKind.Rdp)]
    public void Known_ports_select_the_protocol(string input, ProtocolKind expected)
    {
        Assert.True(QuickConnectParser.TryParse(input, ProtocolKind.Rdp, out var target));
        Assert.Equal(expected, target!.Protocol);
    }

    [Fact]
    public void Protocol_choice_next_to_the_box_overrides_default_and_port()
    {
        var (vm, _, _, _) = Create();
        vm.QuickConnectProtocol = vm.QuickConnectProtocols.Single(o => o.Label == "RDP");
        vm.QuickConnectText = "10.0.0.5:22";
        vm.QuickConnectCommand.Execute(null);
        Assert.Equal(ProtocolKind.Rdp, vm.Workspace.ActiveTab!.Protocol);

        vm.QuickConnectProtocol = vm.QuickConnectProtocols.Single(o => o.Label == "Auto");
        vm.QuickConnectText = "10.0.0.6:3389";
        vm.QuickConnectCommand.Execute(null);
        Assert.Equal(ProtocolKind.Rdp, vm.Workspace.ActiveTab!.Protocol);

        vm.QuickConnectProtocol = vm.QuickConnectProtocols.Single(o => o.Label == "SSH");
        vm.QuickConnectText = "telnet switch01";                 // ausdrücklich im Text ⇒ gewinnt weiterhin
        vm.QuickConnectCommand.Execute(null);
        Assert.Equal(ProtocolKind.Telnet, vm.Workspace.ActiveTab!.Protocol);
    }
}
