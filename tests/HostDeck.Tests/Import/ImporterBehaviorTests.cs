using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Import.MRemoteNG.Crypto;

namespace HostDeck.Tests.Import;

public class ImporterBehaviorTests
{
    private static readonly AeadConfConsCipher Cipher = new();

    private static string Doc(string nodes, string password = "mR3m", string version = "2.6") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" Export="false" EncryptionEngine="AES"
            BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false"
            Protected="{Cipher.Encrypt(password == "mR3m" ? "ThisIsNotProtected" : "ThisIsProtected", password)}" ConfVersion="{version}">
          {nodes}
        </mrng:Connections>
        """;

    // ---- Master-Passwort -------------------------------------------------------------------------

    [Fact]
    public void Prompts_for_master_password_until_correct()
    {
        var xml = Doc("""<Node Name="a" Type="Connection" Hostname="h" />""", password: "geheim");
        var requests = new List<MasterPasswordRequest>();
        var answers = new Queue<string>(["falsch", "geheim"]);

        var result = new ConfConsImporter(new ConfConsImportOptions
        {
            MasterPasswordProvider = r => { requests.Add(r); return answers.Dequeue(); },
        }).Import(xml, "test.xml");

        Assert.True(result.File.CustomMasterPassword);
        Assert.Equal(2, requests.Count);
        Assert.False(requests[0].PreviousAttemptFailed);
        Assert.True(requests[1].PreviousAttemptFailed);
        Assert.Equal("test.xml", requests[0].SourceName);
    }

    [Fact]
    public void Cancelled_prompt_throws()
    {
        var xml = Doc("", password: "geheim");
        var ex = Assert.Throws<ConfConsAuthenticationException>(() =>
            new ConfConsImporter(new ConfConsImportOptions { MasterPasswordProvider = _ => null }).Import(xml));
        Assert.True(ex.Cancelled);
    }

    [Fact]
    public void Too_many_wrong_passwords_throws()
    {
        var calls = 0;
        var ex = Assert.Throws<ConfConsAuthenticationException>(() =>
            new ConfConsImporter(new ConfConsImportOptions { MasterPasswordProvider = _ => { calls++; return "nope"; } })
                .Import(Doc("", password: "geheim")));
        Assert.False(ex.Cancelled);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Protected_file_without_provider_throws()
    {
        Assert.Throws<ConfConsAuthenticationException>(() => new ConfConsImporter().Import(Doc("", password: "geheim")));
    }

    [Fact]
    public void Default_key_needs_no_prompt()
    {
        var result = new ConfConsImporter(new ConfConsImportOptions
        {
            MasterPasswordProvider = _ => throw new InvalidOperationException("darf nicht fragen"),
        }).Import(Doc(""));
        Assert.False(result.File.CustomMasterPassword);
    }

    // ---- Vererbungssemantik ----------------------------------------------------------------------

    [Fact]
    public void Top_level_nodes_ignore_inherit_flags_like_mRemoteNG()
    {
        // mRemoteNG: ConnectionInfoInheritance.InheritanceActive == false für Kinder der Wurzel.
        var xml = Doc("""
            <Node Name="Top" Type="Container" Username="topuser" InheritUsername="True" Port="2222" InheritPort="True" Protocol="SSH2">
              <Node Name="Child" Type="Connection" Hostname="srv" Username="ignored" InheritUsername="True" InheritPort="True" Protocol="SSH2" />
            </Node>
            """);
        var tree = new ConfConsImporter().Import(xml).Tree;

        var top = tree.FindByPath("Top")!;
        var child = tree.FindByPath("Top/Child")!;
        Assert.Equal("topuser", top.Settings.Username);    // explizit trotz Inherit-Flag
        Assert.Null(child.Settings.Username);              // geerbt
        Assert.Equal("topuser", CredentialResolver.Resolve(child, tree.Vault).Username);
        Assert.Equal(2222, SettingsResolver.Resolve(child).Port);
    }

    [Fact]
    public void Legacy_Inherit_attribute_turns_on_everything()
    {
        var xml = Doc("""
            <Node Name="F" Type="Container" Protocol="SSH2" Port="22">
              <Node Name="C" Type="Connection" Hostname="x" Inherit="True" Protocol="RDP" Port="3389" />
            </Node>
            """);
        var child = new ConfConsImporter().Import(xml).Tree.FindByPath("F/C")!;
        Assert.Equal(ProtocolKind.Ssh2, SettingsResolver.Resolve(child).Protocol);
    }

    // ---- Spezialfälle Mapping --------------------------------------------------------------------

    [Theory]
    [InlineData("True", RdpDiskDrives.Local)]
    [InlineData("False", RdpDiskDrives.None)]
    [InlineData("All", RdpDiskDrives.All)]
    [InlineData("Custom", RdpDiskDrives.Custom)]
    public void Redirect_disk_drives_accepts_bool_and_enum(string raw, RdpDiskDrives expected)
    {
        var node = new ConfConsImporter().Import(Doc($"""<Node Name="c" Type="Connection" RedirectDiskDrives="{raw}" />""")).Tree.FindByPath("c")!;
        Assert.Equal(expected, node.Settings.Rdp.RedirectDiskDrives);
    }

    [Fact]
    public void Old_fixed_resolution_is_parsed()
    {
        var node = new ConfConsImporter().Import(Doc("""<Node Name="c" Type="Connection" Resolution="Res1920x1080" />""")).Tree.FindByPath("c")!;
        Assert.Equal(RdpResolutionMode.Fixed, node.Settings.Rdp.ResolutionMode);
        Assert.Equal(1920, node.Settings.Rdp.DesktopWidth);
        Assert.Equal(1080, node.Settings.Rdp.DesktopHeight);
    }

    [Fact]
    public void Broken_single_password_yields_warning_not_failure()
    {
        var other = new AeadConfConsCipher().Encrypt("x", "anderesPasswort");
        var result = new ConfConsImporter().Import(Doc($"""
            <Node Name="ok" Type="Connection" Password="{Cipher.Encrypt("gut", "mR3m")}" />
            <Node Name="kaputt" Type="Connection" Password="{other}" />
            """));

        Assert.Equal("gut", result.Tree.FindByPath("ok")!.Settings.Password);
        Assert.Equal("", result.Tree.FindByPath("kaputt")!.Settings.Password); // explizit leer, nicht geerbt
        Assert.Equal(1, result.Statistics.SecretsFailed);
        Assert.Contains(result.Warnings, w => w.Contains("kaputt") && w.Contains("Password"));
    }

    [Fact]
    public void Duplicate_and_invalid_ids_get_fresh_guids()
    {
        var id = Guid.NewGuid();
        var result = new ConfConsImporter().Import(Doc($"""
            <Node Name="a" Type="Connection" Id="{id}" />
            <Node Name="b" Type="Connection" Id="{id}" />
            <Node Name="c" Type="Connection" Id="kein-guid" />
            """));
        var ids = result.Tree.Connections.Select(c => c.Id).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.Equal(id, ids[0]);
        Assert.Equal("kein-guid", result.Tree.FindByPath("c")!.Extensions["mrng:Id"]);
    }

    [Fact]
    public void Unknown_protocol_and_newer_version_warn()
    {
        var result = new ConfConsImporter().Import(Doc("""<Node Name="c" Type="Connection" Protocol="Gopher" />""", version: "3.1"));
        Assert.Contains(result.Warnings, w => w.Contains("Gopher"));
        Assert.Contains(result.Warnings, w => w.Contains("3.1"));
    }

    // ---- Sicherheit / Format ---------------------------------------------------------------------

    [Fact]
    public void Rejects_dtd_xxe()
    {
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE c [<!ENTITY x SYSTEM "file:///etc/passwd">]>
            <Connections ConfVersion="2.6"><Node Name="&x;" /></Connections>
            """;
        Assert.Throws<ConfConsFormatException>(() => new ConfConsImporter().Import(xxe));
    }

    [Fact]
    public void Rejects_foreign_xml()
    {
        Assert.Throws<ConfConsFormatException>(() => new ConfConsImporter().Import("<foo/>"));
    }

    // ---- Credential-Vault ------------------------------------------------------------------------

    [Fact]
    public void Shared_credentials_are_extracted_into_vault()
    {
        var pw = Cipher.Encrypt("Adm1n!", "mR3m");
        var xml = Doc($"""
            <Node Name="a" Type="Connection" Username="admin" Domain="CORP" Password="{pw}" />
            <Node Name="b" Type="Connection" Username="admin" Domain="CORP" Password="{pw}" />
            <Node Name="c" Type="Connection" Username="solo" Domain="" Password="{Cipher.Encrypt("x", "mR3m")}" />
            """);

        var result = new ConfConsImporter(new ConfConsImportOptions { CredentialExtraction = CredentialExtractionMode.SharedOnly }).Import(xml);
        var tree = result.Tree;

        Assert.Equal(1, result.Statistics.CredentialsExtracted);
        var credential = Assert.Single(tree.Vault.Items);
        Assert.Equal(@"CORP\admin", credential.Name);
        Assert.Equal(credential.Id, tree.FindByPath("a")!.Settings.CredentialId);
        Assert.Null(tree.FindByPath("a")!.Settings.Password);
        Assert.Equal(@"CORP\admin:Adm1n!", Fixture.EffectiveCredentials(tree, "b"));
        Assert.Equal("solo", tree.FindByPath("c")!.Settings.Username);     // nur einmal vorhanden ⇒ bleibt inline
    }

    [Fact]
    public void Extraction_on_fixture_keeps_effective_credentials()
    {
        var result = new ConfConsImporter(new ConfConsImportOptions { CredentialExtraction = CredentialExtractionMode.All })
            .ImportFile(Fixture.PathOf("confCons_v2_6.xml"));
        Assert.True(result.Tree.Vault.Count > 0);
        foreach (var (path, expected) in Fixture.ExpectedCredentials)
            Assert.Equal(expected, Fixture.EffectiveCredentials(result.Tree, path));
    }
}
