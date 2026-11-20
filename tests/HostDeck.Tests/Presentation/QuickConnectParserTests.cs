using HostDeck.Core.Model;
using HostDeck.Presentation.QuickConnect;

namespace HostDeck.Tests.Presentation;

public class QuickConnectParserTests
{
    [Theory]
    [InlineData("ssh admin@10.0.0.1", ProtocolKind.Ssh2, "10.0.0.1", null, "admin", null)]
    [InlineData("ssh admin@web01:2222", ProtocolKind.Ssh2, "web01", 2222, "admin", null)]
    [InlineData("ssh -p 2222 -l root db01", ProtocolKind.Ssh2, "db01", 2222, "root", null)]
    [InlineData("admin@host", ProtocolKind.Ssh2, "host", null, "admin", null)]
    [InlineData("rdp server01", ProtocolKind.Rdp, "server01", null, null, null)]
    [InlineData("mstsc /v:server01:3390", ProtocolKind.Rdp, "server01", 3390, null, null)]
    [InlineData(@"rdp://CORP\admin@ts01", ProtocolKind.Rdp, "ts01", null, "admin", "CORP")]
    [InlineData("rdp://CORP;admin@ts01:3390", ProtocolKind.Rdp, "ts01", 3390, "admin", "CORP")]
    [InlineData("telnet switch01 2323", ProtocolKind.Telnet, "switch01", 2323, null, null)]
    [InlineData("vnc kiosk:5901", ProtocolKind.Vnc, "kiosk", 5901, null, null)]
    [InlineData("https://portal.corp.local", ProtocolKind.Https, "portal.corp.local", null, null, null)]
    [InlineData("ssh root@[fe80::1]:22", ProtocolKind.Ssh2, "fe80::1", 22, "root", null)]
    [InlineData("  10.1.2.3  ", ProtocolKind.Ssh2, "10.1.2.3", null, null, null)]
    public void Parses(string input, ProtocolKind protocol, string host, int? port, string? user, string? domain)
    {
        Assert.True(QuickConnectParser.TryParse(input, ProtocolKind.Ssh2, out var t));
        Assert.Equal(new QuickConnectTarget(protocol, host, port, user, domain), t);
    }

    [Fact]
    public void Plain_host_uses_configured_default_protocol()
    {
        Assert.True(QuickConnectParser.TryParse("server01", ProtocolKind.Rdp, out var t));
        Assert.Equal(ProtocolKind.Rdp, t!.Protocol);
        Assert.Equal(3389, t.EffectivePort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ssh")]
    [InlineData("ssh host:99999")]
    [InlineData("ssh a b c")]
    public void Rejects(string input) => Assert.False(QuickConnectParser.TryParse(input, ProtocolKind.Ssh2, out _));

    [Fact]
    public void Creates_transient_connection()
    {
        QuickConnectParser.TryParse(@"rdp://CORP\admin@ts01", ProtocolKind.Ssh2, out var t);
        var node = t!.ToConnection();
        Assert.Equal("ts01", node.Hostname);
        Assert.Equal("admin", node.Settings.Username);
        Assert.Equal("CORP", node.Settings.Domain);
        Assert.Equal(ProtocolKind.Rdp, node.Settings.Protocol);
    }
}
