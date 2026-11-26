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
        Assert.Equal(ProtocolKind.Ssh2, e2.Protocol.Value);    // wird als „SSH“ angezeigt …
        Assert.DoesNotContain(e2.ProtocolOptions, o => o.Value == ProtocolKind.Ssh1);
        Assert.Null(e2.Apply());
        Assert.Equal(ProtocolKind.Ssh1, old.Settings.Protocol); // … bleibt aber gespeichert SSH1 (mRemoteNG-Kollegen)
    }
}

public sealed class FolderSimplificationTests
{
    [Fact]
    public void Folder_shows_and_clears_values_passed_on_from_mremoteng()
    {
        var folder = new FolderNode("f") { Settings = { Protocol = ProtocolKind.Rdp, Port = 3389, Username = "admin" } };
        new ConnectionTree().Root.Add(folder);
        var e = new ConnectionEditorViewModel(folder, new CredentialVault());
        Assert.Contains("port 3389", e.HiddenFolderValues);
        e.ClearHiddenFolderValuesCommand.Execute(null);
        Assert.Null(e.HiddenFolderValues);
        Assert.Null(e.Apply());
        Assert.Null(folder.Settings.Port);
        Assert.Null(folder.Settings.Protocol);
        Assert.Null(folder.Settings.Username);
    }

    [Fact]
    public void Several_credentials_can_be_deleted_at_once()
    {
        var tree = new ConnectionTree();
        var a = tree.Vault.Add(new Credential { Name = "a", Username = "a" });
        var b = tree.Vault.Add(new Credential { Name = "b", Username = "b" });
        tree.Vault.Add(new Credential { Name = "c", Username = "c" });
        var conn = new ConnectionNode("x") { Hostname = "h", Settings = { CredentialId = a.Id } };
        tree.Root.Add(conn);
        var vm = new CredentialVaultViewModel(tree);
        Assert.Equal(1, vm.RemoveMany([a, b]));
        Assert.Single(tree.Vault.Items);
        Assert.Null(conn.Settings.CredentialId);
    }
}

public sealed class VaultCleanupTests
{
    [Fact]
    public void Duplicates_are_merged_and_references_follow()
    {
        var tree = new ConnectionTree();
        var a = tree.Vault.Add(new Credential { Name = "admin (import 1)", Username = "admin", Password = "pw" });
        var b = tree.Vault.Add(new Credential { Name = "admin", Username = "admin", Password = "pw" });
        tree.Vault.Add(new Credential { Name = "upper", Username = "ADMIN", Password = "pw" }); // andere Schreibweise ⇒ bleibt (SSH unterscheidet)
        var other = tree.Vault.Add(new Credential { Name = "admin other pw", Username = "admin", Password = "different" });
        var c1 = new ConnectionNode("x") { Hostname = "h", Settings = { CredentialId = a.Id } };
        var c2 = new ConnectionNode("y") { Hostname = "h", Settings = { CredentialId = b.Id } };
        tree.Root.Add(c1); tree.Root.Add(c2);
        var vm = new CredentialVaultViewModel(tree);
        Assert.Equal(1, vm.MergeDuplicates());
        Assert.Equal(3, tree.Vault.Items.Count);
        Assert.Equal(c1.Settings.CredentialId, c2.Settings.CredentialId);
        Assert.Contains(tree.Vault.Items, c => c.Id == other.Id);
    }
}

public sealed class EditorTwoStateTests
{
    [Fact]
    public void Checkboxes_show_effective_value_and_store_only_deviations()
    {
        var folder = new FolderNode("f") { Settings = { Favorite = true } };
        var c = new ConnectionNode("srv") { Hostname = "h" };
        folder.Add(c);
        new ConnectionTree().Root.Add(folder);
        var e = new ConnectionEditorViewModel(c, new CredentialVault());
        Assert.True(e.IsFavorite);                 // vom Ordner geerbt
        e.IsFavorite = false;
        Assert.Null(e.Apply());
        Assert.False(c.Settings.Favorite);         // Abweichung gespeichert
        var e2 = new ConnectionEditorViewModel(c, new CredentialVault());
        e2.IsFavorite = true;                      // wieder wie der Ordner ⇒ erben
        Assert.Null(e2.Apply());
        Assert.Null(c.Settings.Favorite);
        Assert.True(e2.IsCredSsp);                 // Standard: NLA an
    }
}
