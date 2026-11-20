using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;

namespace HostDeck.Tests.Import;

/// <summary>Import der Original-mRemoteNG-Testdateien (alle Format-/Verschlüsselungsvarianten).</summary>
public class ImporterFixtureTests
{
    private static ConfConsImportResult Import(string file, string? password) =>
        new ConfConsImporter(new ConfConsImportOptions { MasterPassword = password }).ImportFile(Fixture.PathOf(file));

    [Theory]
    [MemberData(nameof(Fixture.AllVariants), MemberType = typeof(Fixture))]
    public void Imports_structure(string file, string? password)
    {
        var result = Import(file, password);
        var tree = result.Tree;

        Assert.Empty(result.Warnings);
        Assert.Equal(3, tree.Root.Children.Count);                          // Folder1, Folder2, rootConnection
        Assert.Equal(6, result.Statistics.Folders);
        Assert.Equal(12, result.Statistics.Connections);

        var folder1 = Assert.IsType<FolderNode>(tree.FindByPath("Folder1"));
        Assert.True(folder1.IsExpanded);
        Assert.Equal(3, folder1.Children.OfType<ConnectionNode>().Count());
        Assert.Equal(2, Assert.IsType<FolderNode>(tree.FindByPath("Folder2/Folder2.1")).Children.Count);
    }

    [Theory]
    [MemberData(nameof(Fixture.AllVariants), MemberType = typeof(Fixture))]
    public void Decrypts_all_passwords_and_resolves_inheritance(string file, string? password)
    {
        var result = Import(file, password);

        Assert.Equal(0, result.Statistics.SecretsFailed);
        Assert.Equal(10, result.Statistics.SecretsDecrypted);
        foreach (var (path, expected) in Fixture.ExpectedCredentials)
            Assert.Equal(expected, Fixture.EffectiveCredentials(result.Tree, path));
    }

    [Theory]
    [MemberData(nameof(Fixture.AllVariants), MemberType = typeof(Fixture))]
    public void Reports_file_metadata(string file, string? password)
    {
        var info = Import(file, password).File;

        Assert.Equal(password is not null, info.CustomMasterPassword);
        Assert.Equal(file.Contains("v2_6") && file.Contains("fullencryption"), info.FullFileEncryption);
        Assert.Equal(file.Contains("v2_5") && file.Contains("fullencryption"), info.LegacyFullFileEncryption);
        Assert.Equal(file.Contains("5k") ? 5000 : file.Contains("v2_6") ? 1000 : null, info.KdfIterations);
    }

    [Fact]
    public void Maps_inheritance_flags_to_null()
    {
        var tree = Import("confCons_v2_6.xml", null).Tree;

        var folder22 = tree.FindByPath("Folder2/Folder2.2")!;
        Assert.Null(folder22.Settings.Username);                               // InheritUsername="True"
        Assert.Equal(folder22.Parent, SettingsResolver.FindOrigin(folder22, s => s.Username));

        var inheritAll = tree.FindByPath("Folder2/Folder2.2/InheritalAllConnection")!;
        Assert.Null(inheritAll.Settings.Protocol);
        Assert.Null(inheritAll.Settings.Rdp.ColorDepth);
    }

    [Fact]
    public void Maps_protocols_ports_and_hostnames()
    {
        var tree = Import("confCons_v2_6.xml", null).Tree;
        var ssh = (ConnectionNode)tree.FindByPath("Folder1/Connection1.3")!;
        var resolved = SettingsResolver.Resolve(ssh);
        Assert.Equal(ProtocolKind.Ssh2, resolved.Protocol);
        Assert.Equal(22, resolved.Port);

        Assert.Equal(ProtocolKind.Ica, SettingsResolver.Resolve(tree.FindByPath("Folder1")!).Protocol);
    }

    [Fact]
    public void Files_without_ids_get_unique_guids()
    {
        // Die v2.5/2.6-Testdateien enthalten keine Id-Attribute (vor ConfConsEnsureConnectionsHaveIds).
        var ids = Import("confCons_v2_6.xml", null).Tree.Root.Descendants().Select(n => n.Id).ToList();
        Assert.Equal(18, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, ids);
    }

    [Fact]
    public void Preserves_unmapped_attributes_as_extensions()
    {
        var node = Import("confCons_v2_6.xml", null).Tree.FindByPath("Folder1/Connection1.1")!;
        Assert.True(node.Extensions.ContainsKey("mrng:RenderingEngine"));
        Assert.True(node.Extensions.ContainsKey("mrng:ICAEncryptionStrength"));
        Assert.False(node.Extensions.ContainsKey("mrng:Password"));
    }
}
