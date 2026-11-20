using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;

namespace HostDeck.Tests;

/// <summary>
/// Echte confCons.xml-Testdateien aus dem mRemoteNG-Repository (mRemoteNGTests/Resources, GPL-2.0),
/// gespeichert mit mRemoteNG selbst – damit sind sie unabhängige Testvektoren für Krypto und Format.
/// </summary>
internal static class Fixture
{
    public static string PathOf(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    public static string Read(string name) => File.ReadAllText(PathOf(name));

    /// <summary>(Datei, Master-Passwort) – alle Varianten enthalten denselben logischen Inhalt.</summary>
    public static TheoryData<string, string?> AllVariants => new()
    {
        { "confCons_v2_5.xml", null },
        { "confCons_v2_5_fullencryption.xml", null },
        { "confCons_v2_5_passwordis_Password_fullencryption.xml", "Password" },
        { "confCons_v2_6.xml", null },
        { "confCons_v2_6_5k-iterations.xml", null },
        { "confCons_v2_6_fullencryption.xml", null },
        { "confCons_v2_6_passwordis_Password.xml", "Password" },
        { "confCons_v2_6_passwordis_Password_fullencryption.xml", "Password" },
    };

    /// <summary>Pfad → "DOMAIN\user:passwort" (effektiv, nach Vererbung) – vom Original-Inhalt abgeleitet.</summary>
    public static readonly IReadOnlyDictionary<string, string> ExpectedCredentials = new Dictionary<string, string>
    {
        ["Folder1/Connection1.1"] = @"domainFolder1\userFolder1:folder1",
        ["Folder1/Connection1.3"] = @"domainFolder1\userFolder1:folder1",
        ["Folder2"] = @"folder2domain\userFolder2:folder2",
        ["Folder2/Connection2.1"] = @"domainC21\usernameC21:C21",
        ["Folder2/Folder2.1/Connection2.1.1"] = @"domainC2111\usernameC211:C211",
        ["Folder2/Folder2.1/Folder2.1.1/Connection2.1.1.1"] = @"domainC2111\usernameforC2111:C2111",
        ["Folder2/Folder2.2/InheritalAllConnection"] = @"folder2domain\userFolder2:folder2",
        ["Folder2/Folder2.2/Folder2.2.1 InheritAll/Connection2.2.1.1 InheritAll"] = @"folder2domain\userFolder2:folder2",
        ["Folder2/Folder2.2/InheritUsernameConnection"] = @"inheritUsernameDomain\userFolder2:inheritUsernamePassword",
        ["Folder2/Folder2.2/InheritDomainConnection"] = @"folder2domain\inheritDomainUsername:inheritUsernamePassword",
        ["Folder2/Folder2.2/InheritPasswordConnection"] = @"inheritUsernameDomain\inheritDomainUsername:folder2",
        ["rootConnection"] = @"rootdomain\root:rootpassword",
    };

    public static string EffectiveCredentials(ConnectionTree tree, string path)
    {
        var node = tree.FindByPath(path) ?? throw new KeyNotFoundException(path);
        var c = CredentialResolver.Resolve(node, tree.Vault);
        return $@"{c.Domain}\{c.Username}:{c.Password}";
    }
}
