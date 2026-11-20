using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Import.MRemoteNG.Crypto;

namespace HostDeck.Tests.Import;

public class ExporterTests
{
    public static TheoryData<bool, string?, double> Variants => new()
    {
        { false, null, 2.6 },
        { false, "Geheim123", 2.8 },
        { true, null, 2.7 },
        { true, "Geheim123", 2.9 },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void Roundtrip_preserves_tree_settings_and_credentials(bool fullFile, string? masterPassword, double version)
    {
        var original = new ConfConsImporter().ImportFile(Fixture.PathOf("confCons_v2_6.xml")).Tree;

        var xml = new ConfConsExporter(new ConfConsExportOptions
        {
            FullFileEncryption = fullFile,
            MasterPassword = masterPassword,
            ConfVersion = version,
        }).Export(original);

        var reimported = new ConfConsImporter(new ConfConsImportOptions { MasterPassword = masterPassword }).Import(xml);
        Assert.Empty(reimported.Warnings);
        Assert.Equal(fullFile, reimported.File.FullFileEncryption);
        Assert.Equal(masterPassword is not null, reimported.File.CustomMasterPassword);

        var before = original.Root.Descendants().ToList();
        var after = reimported.Tree.Root.Descendants().ToList();
        Assert.Equal(before.Select(n => (n.Path, n.Id, n.GetType())), after.Select(n => (n.Path, n.Id, n.GetType())));

        foreach (var (a, b) in before.Zip(after))
        {
            Assert.Equal(Snapshot(SettingsResolver.Resolve(a)), Snapshot(SettingsResolver.Resolve(b)));
            Assert.Equal(CredentialResolver.Resolve(a, original.Vault) with { UsernameOrigin = null },
                         CredentialResolver.Resolve(b, reimported.Tree.Vault) with { UsernameOrigin = null });
            Assert.Equal(a.Extensions.OrderBy(x => x.Key), b.Extensions.OrderBy(x => x.Key));
            if (a is ConnectionNode ca) Assert.Equal(ca.Hostname, ((ConnectionNode)b).Hostname);
        }

        // Vererbung bleibt Vererbung
        Assert.Null(reimported.Tree.FindByPath("Folder2/Folder2.2")!.Settings.Username);
    }

    [Fact]
    public void Vault_credentials_are_written_inline()
    {
        var tree = new ConnectionTree();
        var cred = tree.Vault.Add(new Credential { Name = "svc", Username = "svc_backup", Domain = "CORP", Password = "Pa$$w0rd" });
        var folder = tree.Root.Add(new FolderNode("Server"));
        folder.Settings.CredentialId = cred.Id;
        var conn = folder.Add(new ConnectionNode("db01") { Hostname = "10.0.0.5" });
        conn.Settings.Protocol = ProtocolKind.Ssh2;

        var xml = new ConfConsExporter().Export(tree);
        Assert.DoesNotContain("Pa$$w0rd", xml);

        var back = new ConfConsImporter().Import(xml).Tree;
        Assert.Equal(@"CORP\svc_backup:Pa$$w0rd", Fixture.EffectiveCredentials(back, "Server/db01"));
        Assert.Null(back.FindByPath("Server/db01")!.Settings.Password); // weiterhin geerbt
        Assert.Equal(22, SettingsResolver.Resolve(back.FindByPath("Server/db01")!).Port);
    }

    [Fact]
    public void Exported_file_is_readable_with_mRemoteNG_crypto_parameters()
    {
        var tree = new ConnectionTree();
        var c = tree.Root.Add(new ConnectionNode("x") { Hostname = "h" });
        c.Settings.Password = "geheim";

        var xml = new ConfConsExporter(new ConfConsExportOptions { MasterPassword = "Master" }).Export(tree);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.Contains("ConfVersion=\"2.8\"", xml);

        var protectedValue = System.Xml.Linq.XDocument.Parse(xml).Root!.Attribute("Protected")!.Value;
        Assert.Equal("ThisIsProtected", new AeadConfConsCipher().Decrypt(protectedValue, "Master"));
    }

    [Fact]
    public void Disk_drives_written_as_bool_below_2_8()
    {
        var tree = new ConnectionTree();
        tree.Root.Add(new ConnectionNode("x")).Settings.Rdp.RedirectDiskDrives = RdpDiskDrives.All;
        Assert.Contains("RedirectDiskDrives=\"true\"", new ConfConsExporter(new() { ConfVersion = 2.7 }).Export(tree));
        Assert.Contains("RedirectDiskDrives=\"All\"", new ConfConsExporter(new() { ConfVersion = 2.8 }).Export(tree));
    }

    /// <summary>Vergleichbare Darstellung aller (verschachtelten) Settings-Werte.</summary>
    private static string Snapshot(object settings) => string.Join("|",
        settings.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).OrderBy(p => p.Name).Select(p =>
        {
            var v = p.GetValue(settings);
            return v is not null && p.PropertyType.Namespace == typeof(ConnectionSettings).Namespace && p.PropertyType.IsClass
                ? $"{p.Name}{{{Snapshot(v)}}}"
                : $"{p.Name}={v}";
        }));
}
