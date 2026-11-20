using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;

namespace HostDeck.Tests.Core;

public class InheritanceTests
{
    [Fact]
    public void Nested_fields_are_resolved_per_field()
    {
        var tree = new ConnectionTree();
        var folder = tree.Root.Add(new FolderNode("RDP-Farm"));
        folder.Settings.Rdp.Gateway.Hostname = "gw.corp.local";
        folder.Settings.Rdp.ColorDepth = RdpColorDepth.Colors32Bit;
        var conn = folder.Add(new ConnectionNode("ts01"));
        conn.Settings.Rdp.ColorDepth = RdpColorDepth.Colors24Bit;

        var resolved = SettingsResolver.Resolve(conn);
        Assert.Equal("gw.corp.local", resolved.Rdp.Gateway.Hostname);   // geerbt
        Assert.Equal(RdpColorDepth.Colors24Bit, resolved.Rdp.ColorDepth); // überschrieben
        Assert.Equal(RdGatewayUsage.Never, resolved.Rdp.Gateway.Usage);   // Default
        Assert.Equal(folder, SettingsResolver.FindOrigin(conn, s => s.Rdp.Gateway.Hostname));
        Assert.Null(SettingsResolver.FindOrigin(conn, s => s.Rdp.Gateway.Usage));
    }

    [Fact]
    public void Port_defaults_to_protocol_port()
    {
        var tree = new ConnectionTree();
        var c = tree.Root.Add(new ConnectionNode("x"));
        c.Settings.Protocol = ProtocolKind.Ssh2;
        Assert.Equal(22, SettingsResolver.Resolve(c).Port);
        c.Settings.Port = 2222;
        Assert.Equal(2222, SettingsResolver.Resolve(c).Port);
    }

    [Fact]
    public void Root_settings_act_as_user_defaults()
    {
        var tree = new ConnectionTree();
        tree.Root.Settings.Protocol = ProtocolKind.Ssh2;
        var c = tree.Root.Add(new FolderNode("a")).Add(new ConnectionNode("b"));
        Assert.Equal(ProtocolKind.Ssh2, SettingsResolver.Resolve(c).Protocol);
    }

    [Fact]
    public void InheritEverything_clears_all_fields()
    {
        var s = ConnectionSettings.CreateDefaults();
        SettingsResolver.InheritEverything(s);
        Assert.Null(s.Protocol);
        Assert.Null(s.Rdp.Gateway.Hostname);
        Assert.Null(s.Vnc.ViewOnly);
    }

    [Fact]
    public void Vault_credential_on_folder_is_inherited_and_can_be_overridden()
    {
        var tree = new ConnectionTree();
        var admin = tree.Vault.Add(new Credential { Name = "Domain Admin", Username = "admin", Domain = "CORP", Password = "pw1" });
        var folder = tree.Root.Add(new FolderNode("DCs"));
        folder.Settings.CredentialId = admin.Id;
        var dc1 = folder.Add(new ConnectionNode("dc1"));
        var dc2 = folder.Add(new ConnectionNode("dc2"));
        dc2.Settings.Password = "anderes";

        var r1 = CredentialResolver.Resolve(dc1, tree.Vault);
        Assert.Equal(("admin", "CORP", "pw1"), (r1.Username, r1.Domain, r1.Password));
        Assert.Same(admin, r1.VaultEntry);

        var r2 = CredentialResolver.Resolve(dc2, tree.Vault);
        Assert.Equal(("admin", "anderes"), (r2.Username, r2.Password)); // nächster Eintrag gewinnt
    }

    [Fact]
    public void Nearer_inline_value_beats_vault_further_up()
    {
        var tree = new ConnectionTree();
        var cred = tree.Vault.Add(new Credential { Name = "c", Username = "vaultuser", Password = "vaultpw" });
        var outer = tree.Root.Add(new FolderNode("outer"));
        outer.Settings.CredentialId = cred.Id;
        var inner = outer.Add(new FolderNode("inner"));
        inner.Settings.Username = "inlineuser";
        var c = inner.Add(new ConnectionNode("c"));

        var r = CredentialResolver.Resolve(c, tree.Vault);
        Assert.Equal("inlineuser", r.Username);
        Assert.Equal("vaultpw", r.Password);
        Assert.Equal(inner, r.UsernameOrigin);
    }

    [Fact]
    public void Dangling_credential_reference_is_ignored()
    {
        var tree = new ConnectionTree();
        var f = tree.Root.Add(new FolderNode("f"));
        f.Settings.Username = "fallback";
        var c = f.Add(new ConnectionNode("c"));
        c.Settings.CredentialId = Guid.NewGuid();
        Assert.Equal("fallback", CredentialResolver.Resolve(c, tree.Vault).Username);
    }

    [Fact]
    public void Moving_nodes_updates_parent_and_prevents_cycles()
    {
        var tree = new ConnectionTree();
        var a = tree.Root.Add(new FolderNode("a"));
        var b = a.Add(new FolderNode("b"));
        var c = b.Add(new ConnectionNode("c"));
        Assert.Equal("a/b/c", c.Path);

        tree.Root.Add(c);
        Assert.Equal("c", c.Path);
        Assert.Empty(b.Children);
        Assert.Throws<InvalidOperationException>(() => b.Add(a));
    }
}
