using HostDeck.Core.Model;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Presentation;

public sealed class EditorPortProtocolTests
{
    private static ConnectionEditorViewModel Editor(int? folderPort = null)
    {
        var folder = new FolderNode("f") { Settings = { Protocol = ProtocolKind.Rdp, Port = folderPort } };
        var c = new ConnectionNode("srv") { Hostname = "10.0.0.1" };
        folder.Add(c);
        new ConnectionTree().Root.Add(folder);
        return new ConnectionEditorViewModel(c, new CredentialVault());
    }

    [Fact]
    public void Choosing_a_protocol_fills_its_default_port()
    {
        var e = Editor(folderPort: 3389);
        e.Protocol = e.ProtocolOptions.Single(o => o.Value == ProtocolKind.Ssh2);
        Assert.Equal("22", e.Port);
        Assert.DoesNotContain("3389", e.PortPlaceholder);
        e.Protocol = e.ProtocolOptions.Single(o => o.Value == ProtocolKind.Vnc);
        Assert.Equal("5900", e.Port);
    }

    [Fact]
    public void Custom_port_is_kept_when_changing_protocol()
    {
        var e = Editor();
        e.Protocol = e.ProtocolOptions.Single(o => o.Value == ProtocolKind.Ssh2);
        e.Port = "2222";
        e.Protocol = e.ProtocolOptions.Single(o => o.Value == ProtocolKind.Telnet);
        Assert.Equal("2222", e.Port);
    }

    [Fact]
    public void Typing_a_well_known_port_switches_the_protocol_and_typing_on_reverts_it()
    {
        var e = Editor();
        e.Port = "22";
        Assert.Equal(ProtocolKind.Ssh2, e.Protocol.Value);
        e.Port = "2200";                       // war nur der Anfang
        Assert.Null(e.Protocol.Value);         // wieder „geerbt“ (RDP)
        e.Port = "3389";
        Assert.Null(e.Protocol.Value);         // RDP ist bereits das wirksame Protokoll
        e.Port = "5900";
        Assert.Equal(ProtocolKind.Vnc, e.Protocol.Value);
    }
}

public sealed class EditorRdpTabTests
{
    [Fact]
    public void Rdp_tab_only_for_rdp_connections_and_folders()
    {
        var folder = new FolderNode("f");
        var c = new ConnectionNode("srv") { Hostname = "10.0.0.1", Settings = { Protocol = ProtocolKind.Ssh2 } };
        folder.Add(c);
        new ConnectionTree().Root.Add(folder);
        var e = new ConnectionEditorViewModel(c, new CredentialVault());
        Assert.False(e.ShowRdpTab);
        e.Protocol = e.ProtocolOptions.Single(o => o.Value == ProtocolKind.Rdp);
        Assert.True(e.ShowRdpTab);
        Assert.True(new ConnectionEditorViewModel(folder, new CredentialVault()).ShowRdpTab);
    }
}

public sealed class EditorClearPasswordTests
{
    [Fact]
    public void Stored_password_can_be_deleted()
    {
        var folder = new FolderNode("f") { Settings = { Password = "FolderPw!" } };
        var c = new ConnectionNode("srv") { Hostname = "10.0.0.1", Settings = { Password = "Secret!1" } };
        folder.Add(c);
        new ConnectionTree().Root.Add(folder);
        var e = new ConnectionEditorViewModel(c, new CredentialVault());
        Assert.True(e.CanClearPassword);
        e.ClearPassword = true;
        Assert.Null(e.Apply());
        Assert.Equal("", c.Settings.Password); // gelöscht und nicht vom Ordner geerbt
    }
}

public sealed class EditorProtocolListTests
{
    [Fact]
    public void Only_supported_protocols_are_offered()
    {
        var c = new ConnectionNode("srv") { Hostname = "10.0.0.1" };
        new ConnectionTree().Root.Add(c);
        var e = new ConnectionEditorViewModel(c, new CredentialVault());
        Assert.DoesNotContain(e.ProtocolOptions, o => o.Value is ProtocolKind.Ssh1 or ProtocolKind.Rlogin or ProtocolKind.Ica);
        Assert.Contains(e.ProtocolOptions, o => o.Value == ProtocolKind.Ssh2);

        var old = new ConnectionNode("old") { Hostname = "10.0.0.2", Settings = { Protocol = ProtocolKind.Ssh1 } };
        new ConnectionTree().Root.Add(old);
        var e2 = new ConnectionEditorViewModel(old, new CredentialVault());
        Assert.Equal(ProtocolKind.Ssh1, e2.Protocol.Value);    // bestehender Eintrag bleibt auswählbar
    }
}
