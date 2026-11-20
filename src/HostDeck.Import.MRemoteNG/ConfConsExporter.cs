using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG.Crypto;
using HostDeck.Import.MRemoteNG.Xml;

namespace HostDeck.Import.MRemoteNG;

public sealed class ConfConsExportOptions
{
    /// <summary>Master-Passwort; <c>null</c> ⇒ mRemoteNG-Standardschlüssel ("mR3m", d.h. nicht geschützt).</summary>
    public string? MasterPassword { get; init; }

    /// <summary>
    /// Ziel-ConfVersion (2.6 – 2.9). mRemoteNG lehnt Dateien mit höherer Version als es kennt ab,
    /// daher Standard 2.8. Für mRemoteNG 1.76/1.77-Releases ggf. 2.6/2.7 wählen.
    /// </summary>
    public double ConfVersion { get; init; } = 2.8;

    public bool FullFileEncryption { get; init; }
    public ConfConsCipherEngine Engine { get; init; } = ConfConsCipherEngine.AES;
    public ConfConsCipherMode Mode { get; init; } = ConfConsCipherMode.GCM;
    public int KdfIterations { get; init; } = ConfConsDefaults.DefaultKdfIterations;

    /// <summary>Beim Import aufbewahrte Fremdattribute ("mrng:…") zurückschreiben.</summary>
    public bool WriteExtensions { get; init; } = true;
}

/// <summary>
/// Exportiert einen <see cref="ConnectionTree"/> als mRemoteNG-<c>confCons.xml</c>.
/// Vault-Zugangsdaten werden dabei inline an die Knoten geschrieben (mRemoteNG kennt keinen Vault).
/// </summary>
public sealed class ConfConsExporter(ConfConsExportOptions? options = null)
{
    private static readonly XNamespace Mrng = "http://mremoteng.org";
    private static readonly HashSet<string> CredentialAttributes = ["Username", "Domain", "Password"];

    private readonly ConfConsExportOptions _options = options ?? new ConfConsExportOptions();

    public void ExportFile(ConnectionTree tree, string path)
    {
        File.WriteAllText(path, Export(tree), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public string Export(ConnectionTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (_options.ConfVersion is < 2.6 or > ConfConsImporter.MaxKnownConfVersion)
            throw new ArgumentOutOfRangeException(nameof(ConfConsExportOptions.ConfVersion), "Unterstützt: 2.6 bis 2.9.");

        var cipher = new AeadConfConsCipher(_options.Engine, _options.Mode, _options.KdfIterations);
        var password = string.IsNullOrEmpty(_options.MasterPassword) ? ConfConsDefaults.DefaultPassword : _options.MasterPassword;
        var ctx = new WriteContext(_options.ConfVersion, plain => cipher.Encrypt(plain, password));

        var root = new XElement(Mrng + "Connections",
            new XAttribute(XNamespace.Xmlns + "mrng", Mrng),
            new XAttribute("Name", tree.Root.Name),
            new XAttribute("Export", "false"),
            new XAttribute("EncryptionEngine", _options.Engine),
            new XAttribute("BlockCipherMode", _options.Mode),
            new XAttribute("KdfIterations", _options.KdfIterations),
            new XAttribute("FullFileEncryption", _options.FullFileEncryption ? "true" : "false"),
            new XAttribute("Protected", cipher.Encrypt(
                password == ConfConsDefaults.DefaultPassword ? ConfConsDefaults.NotProtectedMarker : ConfConsDefaults.ProtectedMarker, password)),
            new XAttribute("ConfVersion", _options.ConfVersion.ToString("0.0", CultureInfo.InvariantCulture)));

        var nodes = tree.Root.Children.Select(c => WriteNode(c, tree, ctx)).ToList();
        if (_options.FullFileEncryption)
        {
            var inner = string.Concat(nodes.Select(n => n.ToString(SaveOptions.DisableFormatting)));
            root.Add(cipher.Encrypt(inner, password));
        }
        else
        {
            root.Add(nodes);
        }

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(new StringWriter(sb, CultureInfo.InvariantCulture), new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(writer);
        // StringWriter meldet UTF-16 – mRemoteNG erwartet utf-8 im Header.
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
    }

    private XElement WriteNode(TreeNode node, ConnectionTree tree, WriteContext ctx)
    {
        var isFolder = node is FolderNode;
        // mRemoteNG ignoriert Vererbung direkt unter der Wurzel ⇒ dort immer explizit schreiben.
        var inheritanceActive = node.Parent is { Parent: not null };
        var resolved = SettingsResolver.Resolve(node);
        var vaultCredential = tree.Vault.Get(node.Settings.CredentialId);
        var effectiveCredentials = CredentialResolver.Resolve(node, tree.Vault);

        var element = new XElement("Node",
            new XAttribute("Name", node.Name),
            new XAttribute("Id", node.Id.ToString()),
            new XAttribute("Type", isFolder ? "Container" : "Connection"));
        if (node is FolderNode folder)
            element.Add(new XAttribute("Expanded", folder.IsExpanded ? "true" : "false"));
        element.Add(new XAttribute("Hostname", node is ConnectionNode c ? c.Hostname : ""));

        foreach (var map in FieldMaps.All)
        {
            string? own;
            if (CredentialAttributes.Contains(map.Attribute) && vaultCredential is not null)
            {
                // Vault-Eintrag am Knoten ⇒ explizit inline schreiben.
                own = map.Attribute switch
                {
                    "Username" => vaultCredential.Username,
                    "Domain" => vaultCredential.Domain,
                    _ => ctx.Encrypt(vaultCredential.Password),
                };
            }
            else
            {
                own = map.Write(node.Settings, ctx);
            }

            var inherit = own is null && inheritanceActive && map.InheritAttribute is not null;

            // HostDeck: Port ohne Wert in der ganzen Kette = Standardport des (eigenen) Protokolls.
            // mRemoteNG würde bei InheritPort den Port des Elternknotens nehmen ⇒ explizit schreiben.
            if (inherit && map.Attribute == "Port" && SettingsResolver.FindOrigin(node.Parent!, s => s.Port) is null)
                inherit = false;
            string value;
            if (own is not null)
                value = own;
            else if (CredentialAttributes.Contains(map.Attribute))
                value = inherit ? (map.Encrypted ? "" : (map.Attribute == "Username" ? effectiveCredentials.Username : effectiveCredentials.Domain))
                                : map.Attribute switch
                                {
                                    "Username" => effectiveCredentials.Username,
                                    "Domain" => effectiveCredentials.Domain,
                                    _ => ctx.Encrypt(effectiveCredentials.Password),
                                };
            else if (map.Encrypted)
                value = inherit ? "" : map.Write(resolved, ctx) ?? "";   // Geheimnisse nicht doppelt ablegen
            else
                value = map.Write(resolved, ctx) ?? "";

            element.SetAttributeValue(map.Attribute, value);
            if (map.InheritAttribute is not null)
                element.SetAttributeValue(map.InheritAttribute, inherit ? "true" : "false");
        }

        if (_options.WriteExtensions)
        {
            foreach (var (key, value) in node.Extensions)
            {
                if (!key.StartsWith("mrng:", StringComparison.Ordinal)) continue;
                var attr = key["mrng:".Length..];
                if (attr is "Id" || element.Attribute(attr) is not null) continue;
                element.SetAttributeValue(attr, value);
            }
        }

        if (node is FolderNode f)
            element.Add(f.Children.Select(child => WriteNode(child, tree, ctx)));
        return element;
    }
}
