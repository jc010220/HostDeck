using System.Security.Cryptography;
using System.Text;
using HostDeck.Core.Model;
using HostDeck.Core.Sync;
using HostDeck.Import.MRemoteNG;

namespace HostDeck.Presentation.Team;

/// <summary>Gelesener Stand der Team-Datei.</summary>
public sealed record TeamSnapshot(
    IReadOnlyList<TreeNode> Children,
    ConfConsFileInfo Info,
    string Hash,
    string? Password,
    bool PasswordWasEntered,
    IReadOnlyList<string> Warnings);

/// <summary>Die Datei wurde zwischen Lesen und Schreiben von jemand anderem geändert ⇒ neu zusammenführen.</summary>
public sealed class TeamFileChangedException() : Exception("Die Team-Datei wurde inzwischen geändert.");

/// <summary>Jemand anderes schreibt gerade.</summary>
public sealed class TeamFileLockedException(string holder) : Exception($"Die Team-Datei wird gerade von {holder} gespeichert. Bitte gleich noch einmal versuchen.")
{
    public string Holder { get; } = holder;
}

/// <summary>
/// Zugriff auf die gemeinsame <c>confCons.xml</c> (Netzlaufwerk oder lokale Kopie auf dem Jumphost).
/// Lesen ohne Sperre; Schreiben mit Sperrdatei, Prüfung auf zwischenzeitliche Änderung, atomarem Ersetzen
/// und lokaler Sicherung des vorherigen Stands. Format und Verschlüsselungsparameter der Datei bleiben erhalten,
/// damit Kollegen mit mRemoteNG sie weiter öffnen können.
/// </summary>
public static class TeamLibraryFile
{
    private static readonly TimeSpan StaleLock = TimeSpan.FromMinutes(2);

    /// <summary>Pfad kann eine Datei oder ein Ordner sein (Ordner ⇒ neueste *.xml darin, z. B. auf dem Jumphost).</summary>
    public static string? Resolve(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        var path = configured.Trim();
        if (Directory.Exists(path))
        {
            return new DirectoryInfo(path).EnumerateFiles("*.xml")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        return path;
    }

    /// <summary>Günstige Änderungsprüfung ohne Lesen des Inhalts.</summary>
    public static (DateTime WriteTimeUtc, long Length)? Probe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static TeamSnapshot Read(string path, string? knownPassword, Func<MasterPasswordRequest, string?>? askPassword)
    {
        var bytes = ReadShared(path);
        var content = Encoding.UTF8.GetString(bytes);
        string? entered = null;
        var importer = new ConfConsImporter(new ConfConsImportOptions
        {
            MasterPassword = knownPassword,
            CredentialExtraction = CredentialExtractionMode.Disabled, // Zugangsdaten bleiben 1:1 an den Knoten
            MasterPasswordProvider = askPassword is null ? null : r => entered = askPassword(r),
        });
        var result = importer.Import(content, Path.GetFileName(path));
        var password = entered ?? (result.File.CustomMasterPassword ? knownPassword : null);
        return new TeamSnapshot(result.Tree.Root.Children.ToList(), result.File, HashOf(bytes), password, entered is not null, result.Warnings);
    }

    /// <summary>
    /// Schreibt den zusammengeführten Stand. Wirft <see cref="TeamFileChangedException"/>, wenn die Datei
    /// seit dem Lesen (<paramref name="expectedHash"/>) geändert wurde, und <see cref="TeamFileLockedException"/>,
    /// wenn gerade jemand anderes schreibt. Liefert Hash und Inhalt des Geschriebenen (so wie ihn die anderen lesen –
    /// z. B. ergänzt mRemoteNG-Standardwerte für Felder, die eine ältere Datei noch nicht kannte).
    /// </summary>
    public static (string Hash, IReadOnlyList<TreeNode> Written) Write(string path, IEnumerable<TreeNode> children, ConfConsFileInfo format, string? password,
        string expectedHash, Action<byte[]>? backupPrevious)
    {
        var tree = new ConnectionTree();
        foreach (var child in children) tree.Root.Add(TreeMerge.CopyWithIds(child, includePersonal: false));

        var exporter = new ConfConsExporter(new ConfConsExportOptions
        {
            MasterPassword = password,
            ConfVersion = Math.Clamp(format.ConfVersion, 2.6, ConfConsImporter.MaxKnownConfVersion),
            Engine = format.Engine ?? Import.MRemoteNG.Crypto.ConfConsCipherEngine.AES,
            Mode = format.Mode ?? Import.MRemoteNG.Crypto.ConfConsCipherMode.GCM,
            KdfIterations = format.KdfIterations ?? Import.MRemoteNG.Crypto.ConfConsDefaults.DefaultKdfIterations,
            FullFileEncryption = format.FullFileEncryption,
        });
        var xml = exporter.Export(tree);
        var bytes = new UTF8Encoding(false).GetBytes(xml);
        var written = new ConfConsImporter(new ConfConsImportOptions
        {
            MasterPassword = password,
            CredentialExtraction = CredentialExtractionMode.Disabled,
        }).Import(xml).Tree.Root.Children.ToList();

        using var _ = AcquireLock(path);
        var current = File.Exists(path) ? ReadShared(path) : [];
        if (HashOf(current) != expectedHash) throw new TeamFileChangedException();

        if (current.Length > 0)
        {
            try { backupPrevious?.Invoke(current); }
            catch (Exception ex) { Services.AppLog.Error("Team-Sicherung", ex); } // Sicherung darf das Speichern nicht verhindern
        }

        var temp = path + ".hostdeck-tmp";
        File.WriteAllBytes(temp, bytes);
        try
        {
            File.Move(temp, path, overwrite: true); // atomar auf demselben Laufwerk/derselben Freigabe
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return (HashOf(bytes), written);
    }

    public static string HashOf(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static byte[] ReadShared(string path)
    {
        // mRemoteNG/andere HostDeck-Instanzen dürfen die Datei gleichzeitig offen haben
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
            catch (IOException) when (attempt < 4 && File.Exists(path))
            {
                Thread.Sleep(200 * (attempt + 1)); // kurz gesperrt (gerade ersetzt)
            }
        }
    }

    private static IDisposable AcquireLock(string path)
    {
        var lockPath = path + ".hostdeck-lock";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose);
                var info = Encoding.UTF8.GetBytes($"{Environment.UserName}@{Environment.MachineName} {DateTime.UtcNow:O}");
                stream.Write(info);
                stream.Flush();
                return stream;
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath);
                if (age > StaleLock && attempt == 0)
                {
                    TryDelete(lockPath); // verwaist (Absturz/Netzabbruch)
                    continue;
                }
                string holder;
                try { holder = File.ReadAllText(lockPath).Split(' ')[0]; }
                catch (IOException) { holder = "einem Kollegen"; }
                throw new TeamFileLockedException(string.IsNullOrWhiteSpace(holder) ? "einem Kollegen" : holder);
            }
        }
        throw new TeamFileLockedException("einem Kollegen");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
