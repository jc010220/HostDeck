using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG.Crypto;
using HostDeck.Import.MRemoteNG.Xml;

namespace HostDeck.Import.MRemoteNG;

/// <summary>
/// Importiert mRemoteNG-Verbindungsdateien (<c>confCons.xml</c>, ConfVersion 0.x – 2.9) inkl.
/// Passwort-Entschlüsselung, Full-File-Encryption, Legacy-Formaten und Vererbung.
/// </summary>
public sealed class ConfConsImporter(ConfConsImportOptions? options = null)
{
    /// <summary>Höchste getestete ConfVersion (mRemoteNG 1.8-Entwicklungszweig, Stand Nov. 2026).</summary>
    public const double MaxKnownConfVersion = 2.9;

    /// <summary>Höchste akzeptierte PBKDF2-Iterationszahl (mRemoteNG bietet bis 50.000 an).</summary>
    public const int MaxKdfIterations = 100_000;

    /// <summary>Höchste akzeptierte Dateigröße (50.000 Verbindungen sind etwa 50 MB).</summary>
    public const long MaxFileBytes = 128L * 1024 * 1024;

    private const string ExtensionPrefix = "mrng:";
    private static readonly XNamespace MrngNamespace = "http://mremoteng.org";

    private readonly ConfConsImportOptions _options = options ?? new ConfConsImportOptions();

    public ConfConsImportResult ImportFile(string path)
    {
        // Übergroße (manipulierte) Dateien gar nicht erst vollständig in den Speicher laden
        var length = new FileInfo(path).Length;
        if (length > MaxFileBytes)
            throw new ConfConsFormatException($"The file is too large ({length / 1024 / 1024} MB, maximum {MaxFileBytes / 1024 / 1024} MB).");
        return Import(File.ReadAllText(path), Path.GetFileName(path));
    }

    public ConfConsImportResult Import(string content, string? sourceName = null)
    {
        if (content.Length > MaxFileBytes) throw new ConfConsFormatException("The file is too large.");
        ArgumentNullException.ThrowIfNull(content);
        var session = new ImportSession(_options, sourceName);
        return session.Run(content);
    }

    /// <summary>Zustand eines einzelnen Importvorgangs.</summary>
    private sealed class ImportSession(ConfConsImportOptions options, string? sourceName)
    {
        private readonly List<string> _warnings = [];
        private readonly HashSet<Guid> _usedIds = [];
        private readonly List<PendingSecret> _pendingSecrets = [];
        private IConfConsCipher _cipher = new LegacyRijndaelConfConsCipher();
        private string _password = ConfConsDefaults.DefaultPassword;
        private double _confVersion;
        private int _folders, _connections, _secretsOk, _secretsFailed;

        public ConfConsImportResult Run(string content)
        {
            content = content.TrimStart('﻿', ' ', '\t', '\r', '\n');

            // 1) Uralt-Format: komplette Datei mit Legacy-Rijndael verschlüsselt (kein XML erkennbar).
            var legacyFullFile = false;
            if (!content.StartsWith('<'))
            {
                legacyFullFile = true;
                content = Authenticate(_cipher, content, plain => plain.TrimStart('﻿', ' ', '\r', '\n').StartsWith('<'));
            }

            // 2) XML laden (ohne DTD/externe Entities).
            var doc = ParseXml(content);
            var root = doc.Root!;
            if (root.Name.LocalName != "Connections")
                throw new ConfConsFormatException($"Not an mRemoteNG connection file: root element is <{root.Name.LocalName}>.");

            _confVersion = ParseConfVersion(root);
            if (_confVersion > MaxKnownConfVersion)
                _warnings.Add($"ConfVersion {_confVersion.ToString(CultureInfo.InvariantCulture)} is newer than the highest tested version {MaxKnownConfVersion.ToString(CultureInfo.InvariantCulture)} – importing on a best-effort basis.");

            // 3) Verschlüsselungsverfahren bestimmen.
            var dpapi = string.Equals((string?)root.Attribute("KeyProtection"), "Dpapi", StringComparison.OrdinalIgnoreCase);
            ConfConsCipherEngine? engine = null;
            ConfConsCipherMode? mode = null;
            int? iterations = null;
            if (dpapi)
            {
                _cipher = new DpapiConfConsCipher();
            }
            else if (_confVersion >= 2.6)
            {
                engine = ParseEnum((string?)root.Attribute("EncryptionEngine"), ConfConsCipherEngine.AES, "EncryptionEngine");
                mode = ParseEnum((string?)root.Attribute("BlockCipherMode"), ConfConsCipherMode.GCM, "BlockCipherMode");
                iterations = ParseIterations((string?)root.Attribute("KdfIterations"));
                _cipher = new AeadConfConsCipher(engine.Value, mode.Value, iterations.Value);
            }
            else if (!legacyFullFile)
            {
                _cipher = new LegacyRijndaelConfConsCipher();
            }

            // 4) Master-Passwort prüfen (Root-Attribut "Protected").
            var protectedValue = (string?)root.Attribute("Protected");
            if (dpapi)
            {
                if (TryDecrypt(_cipher, protectedValue ?? "", "", out var marker) is false || marker != ConfConsDefaults.NotProtectedMarker)
                    throw new ConfConsAuthenticationException("DPAPI-protected file cannot be decrypted with this Windows account.", cancelled: false);
            }
            else if (_confVersion > 1.3 && !string.IsNullOrEmpty(protectedValue))
            {
                Authenticate(_cipher, protectedValue, plain => plain is ConfConsDefaults.NotProtectedMarker or ConfConsDefaults.ProtectedMarker);
            }
            else if (!legacyFullFile && options.MasterPassword is { Length: > 0 } given)
            {
                _password = given;
            }

            // 5) Full-File-Encryption (ab 2.6): Inhalt des Root-Elements ist ein einziger Chiffretext.
            var fullFile = _confVersion >= 2.6 && FieldMaps.TryParseBool((string?)root.Attribute("FullFileEncryption"), out var ff) && ff;
            if (fullFile)
                DecryptFullFile(root);

            // 6) Baum aufbauen.
            var tree = new ConnectionTree(((string?)root.Attribute("Name"))?.Trim() is { Length: > 0 } n ? n : "Connections");
            ReadChildren(root, tree.Root, parentIsRoot: true);
            DecryptPendingSecrets();

            // 7) Optional: Zugangsdaten in den Vault überführen.
            var extracted = CredentialExtractor.Extract(tree, options.CredentialExtraction);

            return new ConfConsImportResult
            {
                Tree = tree,
                Warnings = _warnings,
                Statistics = new ImportStatistics(_folders, _connections, _secretsOk, _secretsFailed, extracted),
                File = new ConfConsFileInfo
                {
                    ConfVersion = _confVersion,
                    CipherDescription = _cipher.Description,
                    Engine = engine,
                    Mode = mode,
                    KdfIterations = iterations,
                    FullFileEncryption = fullFile,
                    LegacyFullFileEncryption = legacyFullFile,
                    DpapiProtected = dpapi,
                    CustomMasterPassword = !dpapi && _password != ConfConsDefaults.DefaultPassword,
                },
            };
        }

        // ---- Authentifizierung ------------------------------------------------------------------

        /// <summary>
        /// Probiert Standardschlüssel → <see cref="ConfConsImportOptions.MasterPassword"/> → Benutzerabfrage.
        /// Setzt bei Erfolg <see cref="_password"/> und liefert den Klartext.
        /// </summary>
        private string Authenticate(IConfConsCipher cipher, string cipherText, Func<string, bool> isValid)
        {
            // Bereits bestätigtes Passwort zuerst (Legacy-Full-File: Datei und "Protected" nutzen dasselbe).
            var candidates = new List<string> { _password, ConfConsDefaults.DefaultPassword };
            if (options.MasterPassword is { Length: > 0 } given)
                candidates.Add(given);
            candidates = candidates.Distinct(StringComparer.Ordinal).ToList();

            foreach (var candidate in candidates)
            {
                if (TryDecrypt(cipher, cipherText, candidate, out var plain) && isValid(plain))
                {
                    _password = candidate;
                    return plain;
                }
            }

            if (options.MasterPasswordProvider is null)
                throw new ConfConsAuthenticationException(
                    options.MasterPassword is null
                        ? "The file is protected with a master password."
                        : "The given master password is wrong.",
                    cancelled: false);

            var previousFailed = options.MasterPassword is not null;
            for (var attempt = 1; attempt <= options.MaxPasswordAttempts; attempt++)
            {
                var entered = options.MasterPasswordProvider(new MasterPasswordRequest(sourceName, attempt, options.MaxPasswordAttempts, previousFailed));
                if (string.IsNullOrEmpty(entered))
                    throw new ConfConsAuthenticationException("Password entry cancelled.", cancelled: true);

                if (TryDecrypt(cipher, cipherText, entered, out var plain) && isValid(plain))
                {
                    _password = entered;
                    return plain;
                }
                previousFailed = true;
            }

            throw new ConfConsAuthenticationException($"Master password entered incorrectly {options.MaxPasswordAttempts}×.", cancelled: false);
        }

        private static bool TryDecrypt(IConfConsCipher cipher, string cipherText, string password, out string plain)
        {
            try
            {
                plain = cipher.Decrypt(cipherText, password);
                return true;
            }
            catch (ConfConsDecryptionException)
            {
                plain = "";
                return false;
            }
        }

        private void DecryptFullFile(XElement root)
        {
            string inner;
            try { inner = _cipher.Decrypt(root.Value.Trim(), _password); }
            catch (ConfConsDecryptionException ex) { throw new ConfConsFormatException("Full-file encryption could not be decrypted.", ex); }

            var wrapper = ParseXml($"<r xmlns:mrng=\"{MrngNamespace.NamespaceName}\">{inner}</r>").Root!;
            root.ReplaceNodes(wrapper.Nodes());
        }

        private sealed record PendingSecret(string Attribute, string CipherText, string NodePath, Action<string?> Apply);

        /// <summary>
        /// PBKDF2 pro Wert (eigenes Salt) ist der teuerste Teil des Imports ⇒ parallel über alle Kerne.
        /// Ergebnisse und Warnungen werden danach in Dokumentreihenfolge angewendet.
        /// </summary>
        private void DecryptPendingSecrets()
        {
            var plain = new string?[_pendingSecrets.Count];
            var errors = new string?[_pendingSecrets.Count];
            Parallel.For(0, _pendingSecrets.Count, i =>
            {
                try { plain[i] = _cipher.Decrypt(_pendingSecrets[i].CipherText, _password); }
                catch (ConfConsDecryptionException ex) { errors[i] = ex.Message; }
            });

            for (var i = 0; i < _pendingSecrets.Count; i++)
            {
                var secret = _pendingSecrets[i];
                if (errors[i] is { } error)
                {
                    _secretsFailed++;
                    _warnings.Add($"{secret.NodePath}: {secret.Attribute} could not be decrypted ({error}) – field left empty.");
                    secret.Apply(null);
                }
                else
                {
                    _secretsOk++;
                    secret.Apply(plain[i]);
                }
            }
            _pendingSecrets.Clear();
        }

        // ---- Knoten -----------------------------------------------------------------------------

        private void ReadChildren(XElement parentElement, FolderNode parent, bool parentIsRoot)
        {
            foreach (var element in parentElement.Elements().Where(e => e.Name.LocalName == "Node"))
            {
                var name = (string?)element.Attribute("Name") ?? "";
                var type = (string?)element.Attribute("Type") ?? "Connection";
                var id = TakeId(element, out var originalId);

                TreeNode node;
                if (type.Equals("Container", StringComparison.OrdinalIgnoreCase))
                {
                    node = new FolderNode(name, id)
                    {
                        IsExpanded = FieldMaps.TryParseBool((string?)element.Attribute("Expanded"), out var exp) && exp,
                    };
                    _folders++;
                }
                else
                {
                    if (!type.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                        _warnings.Add($"{name}: unknown node type '{type}' – imported as a connection.");
                    node = new ConnectionNode(name, id) { Hostname = (string?)element.Attribute("Hostname") ?? "" };
                    _connections++;
                }

                parent.Add(node);
                if (originalId is not null)
                    node.Extensions[ExtensionPrefix + "Id"] = originalId;

                ReadSettings(element, node, inheritanceActive: !parentIsRoot);

                if (node is FolderNode folder)
                    ReadChildren(element, folder, parentIsRoot: false);
            }
        }

        private void ReadSettings(XElement element, TreeNode node, bool inheritanceActive)
        {
            var path = node.Path;
            var ctx = new MapContext(_confVersion, path,
                (attr, value, apply) => _pendingSecrets.Add(new PendingSecret(attr, value, path, apply)),
                w => _warnings.Add(w));

            // mRemoteNG (ConnectionInfoInheritance.InheritanceActive): Knoten direkt unter der Wurzel
            // erben NIE – deren Inherit-Flags werden ignoriert und der eigene Wert gilt.
            var inheritAll = inheritanceActive && FieldMaps.TryParseBool((string?)element.Attribute("Inherit"), out var all) && all;

            foreach (var map in FieldMaps.All)
            {
                var inherited = inheritanceActive && (inheritAll ||
                    (map.InheritAttribute is { } ia && FieldMaps.TryParseBool((string?)element.Attribute(ia), out var flag) && flag));
                if (inherited) continue; // null = erben

                if (element.Attribute(map.Attribute)?.Value is { } value)
                    map.Read(node.Settings, value, ctx);
            }

            if (!options.PreserveUnknownAttributes) return;

            foreach (var attribute in element.Attributes())
            {
                var attrName = attribute.Name.LocalName;
                if (attribute.IsNamespaceDeclaration || FieldMaps.MappedAttributes.Contains(attrName)) continue;
                if (FieldMaps.StructuralAttributes.Contains(attrName) && !(attrName == "Hostname" && node is FolderNode && attribute.Value.Length > 0))
                    continue;
                node.Extensions[ExtensionPrefix + attrName] = attribute.Value;
            }
        }

        /// <summary>Übernimmt die mRemoteNG-GUID; fehlend/ungültig/doppelt ⇒ neue GUID (wie mRemoteNG).</summary>
        private Guid TakeId(XElement element, out string? originalId)
        {
            originalId = null;
            var raw = (string?)element.Attribute("Id");
            if (Guid.TryParse(raw, out var id) && _usedIds.Add(id))
                return id;

            if (!string.IsNullOrWhiteSpace(raw))
            {
                originalId = raw;
                _warnings.Add($"{(string?)element.Attribute("Name")}: Id '{raw}' invalid or duplicate – new Id assigned.");
            }
            var fresh = Guid.NewGuid();
            _usedIds.Add(fresh);
            return fresh;
        }

        // ---- Helfer -----------------------------------------------------------------------------

        private double ParseConfVersion(XElement root)
        {
            var raw = (string?)root.Attribute("ConfVersion");
            if (raw is null)
            {
                _warnings.Add("No ConfVersion attribute – very old file, importing on a best-effort basis.");
                return 0;
            }
            if (double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return v;
            throw new ConfConsFormatException($"Invalid ConfVersion '{raw}'.");
        }

        private TEnum ParseEnum<TEnum>(string? raw, TEnum fallback, string attribute) where TEnum : struct, Enum
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (Enum.TryParse<TEnum>(raw, true, out var value) && Enum.IsDefined(value)) return value;
            throw new ConfConsFormatException($"Unsupported value {attribute}='{raw}'.");
        }

        private int ParseIterations(string? raw)
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var it) && it >= 1000)
            {
                // Obergrenze: eine manipulierte Datei könnte sonst mit riesigen Werten die App minutenlang blockieren
                if (it > MaxKdfIterations)
                    throw new ConfConsFormatException($"KdfIterations {it} exceeds the supported maximum of {MaxKdfIterations}.");
                return it;
            }
            _warnings.Add($"KdfIterations '{raw}' invalid – using {ConfConsDefaults.DefaultKdfIterations}.");
            return ConfConsDefaults.DefaultKdfIterations;
        }

        private static XDocument ParseXml(string xml)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            try
            {
                using var reader = XmlReader.Create(new StringReader(xml), settings);
                return XDocument.Load(reader, LoadOptions.None);
            }
            catch (XmlException ex)
            {
                throw new ConfConsFormatException($"Invalid XML: {ex.Message}", ex);
            }
        }
    }
}
