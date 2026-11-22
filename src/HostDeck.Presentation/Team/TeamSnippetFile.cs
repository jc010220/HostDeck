using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostDeck.Presentation.Snippets;

namespace HostDeck.Presentation.Team;

/// <summary>Snippet in der Team-Datei, inkl. Löschmarke (damit Löschungen bei allen ankommen).</summary>
public sealed class TeamSnippetEntry
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Execute { get; set; } = true;
    public string Category { get; set; } = "";
    public DateTime ModifiedUtc { get; set; }
    public string? ModifiedBy { get; set; }
    /// <summary>Gesetzt = gelöscht (bleibt als Marke erhalten, damit ältere Kopien es nicht wiederbeleben).</summary>
    public DateTime? DeletedUtc { get; set; }

    public DateTime ChangedUtc => DeletedUtc is { } d && d > ModifiedUtc ? d : ModifiedUtc;

    public Snippet ToSnippet() => new() { Id = Id, Name = Name, Command = Command, Execute = Execute, Category = Category, IsTeam = true };

    public static TeamSnippetEntry From(Snippet s) => new()
    {
        Id = s.Id, Name = s.Name, Command = s.Command, Execute = s.Execute, Category = s.Category,
        ModifiedUtc = DateTime.UtcNow, ModifiedBy = Environment.UserName,
    };
}

/// <summary>Das Team-Passwort fehlt oder ist falsch.</summary>
public sealed class TeamSnippetPasswordException(string message) : Exception(message);

/// <summary>
/// Team-Snippets als eigene Datei neben der Team-Datei (z. B. <c>confCons.hostdeck-snippets.json</c>) – die
/// confCons.xml bleibt so unverändert mRemoteNG-kompatibel. Ist die Team-Datei passwortgeschützt, werden die
/// Snippets mit demselben Team-Passwort verschlüsselt (AES-256-GCM, PBKDF2-SHA256). Zusammengeführt wird je
/// Snippet: die jüngere Änderung gewinnt, Löschungen bleiben als Marke erhalten.
/// </summary>
public static class TeamSnippetFile
{
    private const int Iterations = 210_000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathFor(string teamFile) =>
        Path.Combine(Path.GetDirectoryName(teamFile) ?? ".", Path.GetFileNameWithoutExtension(teamFile) + ".hostdeck-snippets.json");

    private sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public string? Salt { get; set; }
        public string? Nonce { get; set; }
        public string? Tag { get; set; }
        /// <summary>Verschlüsselt (Base64) – oder bei ungeschützter Team-Datei leer und stattdessen <see cref="Items"/>.</summary>
        public string? Data { get; set; }
        public List<TeamSnippetEntry>? Items { get; set; }
    }

    /// <summary>Liest die Datei; fehlt sie, gibt es eine leere Liste.</summary>
    public static List<TeamSnippetEntry> Read(string path, string? password)
    {
        if (!File.Exists(path)) return [];
        var bytes = TeamLibraryFile.ReadShared(path);
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes) ?? new Envelope();
        if (envelope.Data is null)
        {
            // Mit Passwort geschützt, Datei aber unverschlüsselt ⇒ von jemandem ohne Passwort ersetzt. Nie übernehmen
            // (sonst würde der nächste Schreibvorgang den fremden Inhalt verschlüsselt „beglaubigen“).
            if (!string.IsNullOrEmpty(password) && envelope.Items is { Count: > 0 })
                throw new TeamSnippetPasswordException("The shared snippets file is not encrypted although the library has a password – it may have been replaced. It is ignored.");
            return Clamp(envelope.Items ?? []);
        }
        if (string.IsNullOrEmpty(password))
            throw new TeamSnippetPasswordException("The team snippets are protected with the team password.");
        try
        {
            var salt = Convert.FromBase64String(envelope.Salt!);
            var nonce = Convert.FromBase64String(envelope.Nonce!);
            var tag = Convert.FromBase64String(envelope.Tag!);
            var cipher = Convert.FromBase64String(envelope.Data);
            var plain = new byte[cipher.Length];
            var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(nonce, cipher, tag, plain, "HostDeck.TeamSnippets.v1"u8);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            return Clamp(JsonSerializer.Deserialize<List<TeamSnippetEntry>>(plain) ?? []);
        }
        catch (Exception ex) when (ex is AuthenticationTagMismatchException or FormatException or ArgumentNullException)
        {
            throw new TeamSnippetPasswordException("The team snippets could not be decrypted (team password changed?).");
        }
    }

    /// <summary>Zeitstempel aus der Datei höchstens wenig in der Zukunft – sonst könnte ein Eintrag mit „2099“ nie mehr geändert werden.</summary>
    private static List<TeamSnippetEntry> Clamp(List<TeamSnippetEntry> items)
    {
        var limit = DateTime.UtcNow.AddMinutes(10);
        foreach (var e in items)
        {
            if (e.ModifiedUtc > limit) e.ModifiedUtc = limit;
            if (e.DeletedUtc > limit) e.DeletedUtc = limit;
        }
        return items;
    }

    /// <summary>Änderung einbringen: neu einlesen, zusammenführen, atomar schreiben. Liefert den neuen Gesamtstand.</summary>
    public static List<TeamSnippetEntry> Apply(string path, string? password, IEnumerable<TeamSnippetEntry> changes)
    {
        using var _ = TeamLibraryFile.AcquireLock(path);
        var merged = Merge(Read(path, password), changes);
        Write(path, password, merged);
        return merged;
    }

    /// <summary>Kompletten Stand neu schreiben (z. B. mit neuem Team-Passwort) – atomar und unter Sperre.</summary>
    public static void Rewrite(string path, string? password, List<TeamSnippetEntry> items)
    {
        using var _ = TeamLibraryFile.AcquireLock(path);
        Write(path, password, items);
    }

    /// <summary>Je Id gewinnt die jüngere Änderung (Bearbeiten oder Löschen).</summary>
    public static List<TeamSnippetEntry> Merge(IEnumerable<TeamSnippetEntry> current, IEnumerable<TeamSnippetEntry> incoming)
    {
        var byId = current.ToDictionary(e => e.Id);
        foreach (var e in incoming)
            if (!byId.TryGetValue(e.Id, out var existing) || e.ChangedUtc >= existing.ChangedUtc)
                byId[e.Id] = e;
        // Löschmarken nach 180 Tagen aufräumen
        var cutoff = DateTime.UtcNow.AddDays(-180);
        return byId.Values.Where(e => e.DeletedUtc is null || e.DeletedUtc > cutoff)
                          .OrderBy(e => e.Category).ThenBy(e => e.Name).ToList();
    }

    private static void Write(string path, string? password, List<TeamSnippetEntry> items)
    {
        var envelope = new Envelope();
        if (string.IsNullOrEmpty(password))
        {
            envelope.Items = items;
        }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var plain = JsonSerializer.SerializeToUtf8Bytes(items);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Encrypt(nonce, plain, cipher, tag, "HostDeck.TeamSnippets.v1"u8);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            envelope.Salt = Convert.ToBase64String(salt);
            envelope.Nonce = Convert.ToBase64String(nonce);
            envelope.Tag = Convert.ToBase64String(tag);
            envelope.Data = Convert.ToBase64String(cipher);
        }
        var temp = path + ".hostdeck-tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(envelope, Json));
        File.Move(temp, path, overwrite: true);
    }
}
