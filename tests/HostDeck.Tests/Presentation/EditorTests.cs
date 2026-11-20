using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Presentation;

public class ConnectionEditorTests
{
    private static (ConnectionTree Tree, FolderNode Folder, ConnectionNode Conn) Tree()
    {
        var tree = new ConnectionTree();
        var folder = tree.Root.Add(new FolderNode("Kunde ACME"));
        folder.Settings.Username = "acme-admin";
        folder.Settings.Password = "ordnerpw";
        folder.Settings.Protocol = ProtocolKind.Ssh2;
        var conn = folder.Add(new ConnectionNode("web01") { Hostname = "10.0.0.1" });
        return (tree, folder, conn);
    }

    [Fact]
    public void Shows_inherited_values_as_placeholders()
    {
        var (tree, _, conn) = Tree();
        var e = new ConnectionEditorViewModel(conn, tree.Vault);
        Assert.Equal("geerbt von Kunde ACME: acme-admin", e.UsernamePlaceholder);
        Assert.Null(e.Protocol.Value);
        Assert.Equal("(geerbt: SSH)", e.Protocol.Label);
        Assert.Equal("Standard: 22", e.PortPlaceholder);
        Assert.True(e.InheritPassword);
        Assert.Contains("geerbt", e.PasswordHint);
    }

    [Fact]
    public void Apply_writes_explicit_values_and_keeps_inheritance_for_empty_fields()
    {
        var (tree, _, conn) = Tree();
        var e = new ConnectionEditorViewModel(conn, tree.Vault)
        {
            Hostname = " web01.acme.local ",
            Port = "2222",
            Description = "Webserver",
        };
        e.Protocol = e.ProtocolOptions.Single(o => o.Value == ProtocolKind.Rdp);
        e.RestrictedAdmin = true;
        Assert.Null(e.Apply());

        Assert.Equal("web01.acme.local", conn.Hostname);
        Assert.Equal(2222, conn.Settings.Port);
        Assert.Equal(ProtocolKind.Rdp, conn.Settings.Protocol);
        Assert.True(conn.Settings.Rdp.UseRestrictedAdmin);
        Assert.Null(conn.Settings.Username);                       // leer ⇒ weiter geerbt
        Assert.Equal("ordnerpw", CredentialResolver.Resolve(conn, tree.Vault).Password);
    }

    [Fact]
    public void Own_password_and_validation()
    {
        var (tree, _, conn) = Tree();
        var e = new ConnectionEditorViewModel(conn, tree.Vault) { InheritPassword = false, NewPassword = "eigenes" };
        Assert.Null(e.Apply());
        Assert.Equal("eigenes", conn.Settings.Password);

        var reopened = new ConnectionEditorViewModel(conn, tree.Vault);
        Assert.True(reopened.HasOwnPassword);
        Assert.Null(reopened.Apply());                                // leer lassen ⇒ Passwort bleibt
        Assert.Equal("eigenes", conn.Settings.Password);

        Assert.NotNull(new ConnectionEditorViewModel(conn, tree.Vault) { Port = "70000" }.Apply());
        Assert.NotNull(new ConnectionEditorViewModel(conn, tree.Vault) { Name = " " }.Apply());
    }

    [Fact]
    public void Warns_about_insecure_rdp_settings()
    {
        var (tree, _, conn) = Tree();
        var e = new ConnectionEditorViewModel(conn, tree.Vault);
        Assert.Null(e.SecurityWarning);                                // sicherer Standard
        e.AuthLevel = e.AuthLevelOptions.Single(o => o.Value == RdpAuthenticationLevel.NoAuth);
        Assert.Contains("Zertifikat", e.SecurityWarning);
    }

    [Fact]
    public void Vault_assignment_and_removal_releases_references()
    {
        var (tree, folder, conn) = Tree();
        var vault = new CredentialVaultViewModel(tree);
        vault.Add();
        vault.Username = "svc";
        vault.NewPassword = "vaultpw";
        vault.Save();
        var cred = vault.Selected!;

        var e = new ConnectionEditorViewModel(folder, tree.Vault);
        e.Credential = e.CredentialOptions.Single(o => o.Id == cred.Id);
        Assert.Null(e.Apply());
        Assert.Equal(("svc", "vaultpw"), (CredentialResolver.Resolve(conn, tree.Vault).Username, CredentialResolver.Resolve(conn, tree.Vault).Password));
        Assert.Equal(1, vault.UsageCount(cred));

        Assert.Equal(1, vault.Remove());
        Assert.Null(folder.Settings.CredentialId);
        Assert.Equal(0, tree.Vault.Count);
    }

    [Fact]
    public void New_connection_opens_editor_and_is_removed_when_cancelled()
    {
        var (vm, dialogs, _, store) = MainViewModelTests.Create();
        dialogs.EditAnswer = _ => false;
        vm.NewConnectionCommand.Execute(null);
        Assert.Empty(store.Saved.Connections);

        dialogs.EditAnswer = ed => { ed.Name = "neu"; ed.Hostname = "h"; return ed.Apply() is null; };
        vm.NewConnectionCommand.Execute(null);
        Assert.Equal("neu", Assert.Single(store.Saved.Connections).Name);
    }
}
