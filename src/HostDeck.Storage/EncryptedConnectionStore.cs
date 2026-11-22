using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HostDeck.Core.Model;
using Microsoft.Data.Sqlite;

namespace HostDeck.Storage;

/// <summary>
/// Verbindungsbestand + Credential-Vault in einer vollständig verschlüsselten SQLite-Datenbank
/// (SQLCipher v4: AES-256-CBC, HMAC-SHA512 pro Seite). Dateien:
/// <list type="bullet">
/// <item><c>connections.hdk</c> – Datenbank (ohne Schlüssel nicht von Zufall unterscheidbar)</item>
/// <item><c>connections.hdk.key</c> – Schlüsseldatei (siehe <see cref="KeyFile"/>), muss mitgesichert werden</item>
/// </list>
/// </summary>
public sealed class EncryptedConnectionStore : IDisposable
{
    public const int SchemaVersion = 2;
    private const string KeyFileSuffix = ".key";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // null = erben ⇒ wird gar nicht gespeichert
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly KeyFile _keyFile;
    private byte[] _dek;
    private SqliteConnection? _connection;

    static EncryptedConnectionStore() => SQLitePCL.Batteries_V2.Init();

    private EncryptedConnectionStore(string path, KeyFile keyFile, byte[] dek)
    {
        _path = Path.GetFullPath(path);
        _keyFile = keyFile;
        _dek = dek;
        _connection = OpenConnection(_path, dek);
    }

    public string DatabasePath => _path;
    public string KeyFilePath => _path + KeyFileSuffix;
    public bool HasDeviceUnlock => _keyFile.Device is not null;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HostDeck", "connections.hdk");

    public static bool Exists(string path) => File.Exists(path) && File.Exists(path + KeyFileSuffix);

    // ---- Anlegen / Öffnen --------------------------------------------------------------------------

    public static EncryptedConnectionStore Create(string path, string masterPassword, KdfParameters? kdf = null)
    {
        if (File.Exists(path) || File.Exists(path + KeyFileSuffix))
            throw new IOException($"A database already exists at {path}.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var (keyFile, dek) = KeyFile.Create(masterPassword, kdf ?? KdfParameters.Default);
        var store = new EncryptedConnectionStore(path, keyFile, dek);
        try
        {
            store.Migrate();
            keyFile.Save(store.KeyFilePath); // erst wenn die DB steht
            return store;
        }
        catch
        {
            store.Dispose();
            File.Delete(path);
            throw;
        }
    }

    /// <exception cref="WrongMasterPasswordException">Falsches Passwort.</exception>
    public static EncryptedConnectionStore Open(string path, string masterPassword)
    {
        var keyFile = KeyFile.Load(path + KeyFileSuffix);
        var dek = keyFile.UnlockWithPassword(masterPassword);
        return OpenWithDek(path, keyFile, dek);
    }

    /// <summary>Entsperren ohne Passwort über den Geräteschlüssel; <c>null</c>, wenn nicht eingerichtet/ungültig.</summary>
    public static EncryptedConnectionStore? TryOpenWithDevice(string path, IDeviceKeyProtector protector)
    {
        if (!Exists(path)) return null;
        var keyFile = KeyFile.Load(path + KeyFileSuffix);
        var dek = keyFile.TryUnlockWithDevice(protector);
        return dek is null ? null : OpenWithDek(path, keyFile, dek);
    }

    private static EncryptedConnectionStore OpenWithDek(string path, KeyFile keyFile, byte[] dek)
    {
        EncryptedConnectionStore? store = null;
        try
        {
            store = new EncryptedConnectionStore(path, keyFile, dek);
            store.Migrate();
            return store;
        }
        catch (Exception ex)
        {
            // Fehlgeschlagen ⇒ Verbindung schließen und Schlüssel nicht im Speicher liegen lassen
            if (store is not null) store.Dispose();
            else CryptographicOperations.ZeroMemory(dek);
            if (ex is SqliteException { SqliteErrorCode: 26 }) // SQLITE_NOTADB
                throw new InvalidDataException("Database and key file do not match or the database is damaged.", ex);
            throw;
        }
    }

    // ---- Schlüsselverwaltung -----------------------------------------------------------------------

    /// <summary>Master-Passwort prüfen (z. B. zum Entsperren nach automatischer Sperre), ohne etwas zu ändern.</summary>
    public bool VerifyMasterPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        try
        {
            var dek = _keyFile.UnlockWithPassword(password);
            try { return CryptographicOperations.FixedTimeEquals(dek, _dek); }
            finally { CryptographicOperations.ZeroMemory(dek); }
        }
        catch (WrongMasterPasswordException) { return false; }
    }

    public void ChangeMasterPassword(string currentPassword, string newPassword)
    {
        KeyFile.ValidatePassword(newPassword);
        CryptographicOperations.ZeroMemory(_keyFile.UnlockWithPassword(currentPassword)); // prüft das alte Passwort
        // Nie schwächer als der aktuelle Standard – auch wenn die Schlüsseldatei manipuliert/alt ist
        var current = _keyFile.KdfParams;
        var floor = KdfParameters.Default;
        var kdf = current.MemoryKiB < floor.MemoryKiB || current.Iterations < floor.Iterations || current.Parallelism < floor.Parallelism ? floor : current;
        var replacement = KeyFile.CreateForDek(_dek, newPassword, kdf);
        _keyFile.Kdf = replacement.Kdf;
        _keyFile.PasswordSlot = replacement.PasswordSlot;
        _keyFile.Save(KeyFilePath);
    }

    public void EnableDeviceUnlock(IDeviceKeyProtector protector)
    {
        _keyFile.SetDeviceSlot(protector, _dek);
        _keyFile.Save(KeyFilePath);
    }

    public void DisableDeviceUnlock()
    {
        _keyFile.Device = null;
        _keyFile.Save(KeyFilePath);
    }

    // ---- Laden / Speichern -------------------------------------------------------------------------

    public ConnectionTree Load()
    {
        var db = Connection;
        var rootName = ReadMeta(db, "root_name") ?? "Connections";
        var tree = new ConnectionTree(rootName);
        if (ReadMeta(db, "root_settings") is { } rootSettings)
            tree.Root.Settings = Deserialize<ConnectionSettings>(rootSettings) ?? new();

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, username, domain, password, private_key, private_key_passphrase, notes FROM credentials ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                tree.Vault.Add(new Credential
                {
                    Id = Guid.Parse(r.GetString(0)),
                    Name = r.GetString(1),
                    Username = r.GetString(2),
                    Domain = r.GetString(3),
                    Password = r.GetString(4),
                    PrivateKey = r.IsDBNull(5) ? null : r.GetString(5),
                    PrivateKeyPassphrase = r.IsDBNull(6) ? null : r.GetString(6),
                    Notes = r.GetString(7),
                });
            }
        }

        // Knoten: erst alle lesen, dann in sort_order unter ihre Eltern hängen
        var rows = new List<(Guid Id, Guid? Parent, TreeNode Node)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT id, parent_id, kind, name, hostname, is_expanded, settings, extensions FROM nodes ORDER BY sort_order";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = Guid.Parse(r.GetString(0));
                Guid? parent = r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1));
                TreeNode node = r.GetInt32(2) == 0
                    ? new FolderNode(r.GetString(3), id) { IsExpanded = r.GetInt32(5) != 0 }
                    : new ConnectionNode(r.GetString(3), id) { Hostname = r.GetString(4) };
                node.Settings = Deserialize<ConnectionSettings>(r.GetString(6)) ?? new();
                foreach (var (k, v) in Deserialize<Dictionary<string, string>>(r.GetString(7)) ?? [])
                    node.Extensions[k] = v;
                rows.Add((id, parent, node));
            }
        }

        var folders = rows.Where(x => x.Node is FolderNode).ToDictionary(x => x.Id, x => (FolderNode)x.Node);
        foreach (var (_, parent, node) in rows)
        {
            var target = parent is { } p && folders.TryGetValue(p, out var f) ? f : tree.Root; // verwaiste Knoten ⇒ Wurzel
            target.Add(node);
        }
        return tree;
    }

    /// <summary>Speichert den kompletten Stand in einer Transaktion (alles oder nichts).</summary>
    public void Save(ConnectionTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var db = Connection;
        using var tx = db.BeginTransaction();

        Execute(db, tx, "DELETE FROM nodes; DELETE FROM credentials;");
        WriteMeta(db, tx, "root_name", tree.Root.Name);
        WriteMeta(db, tx, "root_settings", Serialize(tree.Root.Settings));
        WriteMeta(db, tx, "saved_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO credentials (id, name, username, domain, password, private_key, private_key_passphrase, notes)
                VALUES ($id, $name, $user, $domain, $pw, $key, $pass, $notes)
                """;
            var p = AddParameters(cmd, "$id", "$name", "$user", "$domain", "$pw", "$key", "$pass", "$notes");
            foreach (var c in tree.Vault.Items)
            {
                Set(p, c.Id.ToString(), c.Name, c.Username, c.Domain, c.Password, c.PrivateKey, c.PrivateKeyPassphrase, c.Notes);
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO nodes (id, parent_id, sort_order, kind, name, hostname, is_expanded, settings, extensions)
                VALUES ($id, $parent, $order, $kind, $name, $host, $expanded, $settings, $ext)
                """;
            var p = AddParameters(cmd, "$id", "$parent", "$order", "$kind", "$name", "$host", "$expanded", "$settings", "$ext");
            var order = 0;
            foreach (var node in tree.Root.Descendants()) // Dokumentreihenfolge ⇒ Eltern vor Kindern
            {
                Set(p,
                    node.Id.ToString(),
                    node.Parent is { } parent && parent != tree.Root ? parent.Id.ToString() : null,
                    order++,
                    node is FolderNode ? 0 : 1,
                    node.Name,
                    (node as ConnectionNode)?.Hostname ?? "",
                    node is FolderNode { IsExpanded: true } ? 1 : 0,
                    Serialize(node.Settings),
                    Serialize(node.Extensions));
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    // ---- Schema ------------------------------------------------------------------------------------

    private void Migrate()
    {
        var db = Connection;
        Execute(db, null, "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        var version = int.TryParse(ReadMeta(db, "schema_version"), out var v) ? v : 0;
        if (version > SchemaVersion)
            throw new InvalidDataException($"Database schema {version} is newer than this HostDeck version ({SchemaVersion}).");

        if (version < 1)
        {
            using var tx = db.BeginTransaction();
            Execute(db, tx, """
                CREATE TABLE nodes (
                    id          TEXT PRIMARY KEY,
                    parent_id   TEXT NULL,
                    sort_order  INTEGER NOT NULL,
                    kind        INTEGER NOT NULL,          -- 0 = Ordner, 1 = Verbindung
                    name        TEXT NOT NULL,
                    hostname    TEXT NOT NULL,
                    is_expanded INTEGER NOT NULL,
                    settings    TEXT NOT NULL,             -- JSON, null-Felder (= erben) entfallen
                    extensions  TEXT NOT NULL              -- JSON (z. B. mRemoteNG-Fremdattribute)
                );
                CREATE INDEX ix_nodes_parent ON nodes(parent_id);
                CREATE TABLE credentials (
                    id                     TEXT PRIMARY KEY,
                    name                   TEXT NOT NULL,
                    username               TEXT NOT NULL,
                    domain                 TEXT NOT NULL,
                    password               TEXT NOT NULL,
                    private_key            TEXT NULL,
                    private_key_passphrase TEXT NULL,
                    notes                  TEXT NOT NULL
                );
                """);
            WriteMeta(db, tx, "schema_version", "1");
            tx.Commit();
        }

        if (version < 2)
        {
            using var tx = db.BeginTransaction();
            Execute(db, tx, """
                CREATE TABLE known_hosts (
                    host        TEXT NOT NULL,
                    port        INTEGER NOT NULL,
                    key_type    TEXT NOT NULL,
                    fingerprint TEXT NOT NULL,              -- "SHA256:…"
                    added_utc   TEXT NOT NULL,
                    PRIMARY KEY (host, port, key_type)
                );
                """);
            WriteMeta(db, tx, "schema_version", "2");
            tx.Commit();
        }
    }

    /// <summary>Anzahl gespeicherter Verbindungen (für die Plausibilitätsprüfung vor dem Speichern).</summary>
    public int CountConnections()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM nodes WHERE kind = 1";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ---- Sicherung ---------------------------------------------------------------------------------------

    /// <summary>
    /// Konsistente Sicherung: WAL in die Datenbank zurückschreiben, dann Datenbank + Schlüsseldatei kopieren.
    /// Die Sicherung bleibt verschlüsselt und wird mit demselben Master-Passwort geöffnet.
    /// </summary>
    /// <summary>
    /// Automatische Sicherungen der persönlichen Datenbank heißen <c>HostDeck-Personal-AutoBackup_yyyyMMdd-HHmmss.hdk</c> –
    /// bewusst anders als manuelle („HostDeck-Personal-Backup_yyyyMMdd.hdk“), damit die Aufbewahrungsregel nie eine manuelle Sicherung löscht.
    /// </summary>
    public const string BackupPrefix = "HostDeck-Personal-AutoBackup_";

    /// <summary>Sicherung in einen Ordner (Name mit Zeitstempel) – Datenbank + Schlüsseldatei.</summary>
    public string Backup(string targetDirectory)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return BackupTo(Path.Combine(targetDirectory, $"{BackupPrefix}{stamp}.hdk"), overwrite: false);
    }

    /// <summary>Sicherung unter einem gewählten Dateinamen; die Schlüsseldatei liegt daneben als <c>*.hdk.key</c>.</summary>
    public string BackupTo(string targetFile, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetFile))!);
        Execute(Connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
        File.Copy(_path, targetFile, overwrite);
        // Schlüsseldatei ohne Geräte-Slot (DPAPI): Eine Sicherung lässt sich nur mit dem Master-Passwort öffnen –
        // auch für jemanden, der an die DPAPI-Schlüssel des Windows-Kontos kommt.
        var keyTarget = targetFile + KeyFileSuffix;
        if (!overwrite && File.Exists(keyTarget)) throw new IOException($"The file '{keyTarget}' already exists.");
        var copy = KeyFile.Load(KeyFilePath);
        copy.Device = null;
        copy.Save(keyTarget);
        return targetFile;
    }

    // ---- Beliebige verschlüsselte Dokumente (z. B. Snippets) ---------------------------------------------

    /// <summary>JSON-Dokument im verschlüsselten Speicher lesen (Tabelle meta, Schlüssel "doc:…").</summary>
    public string? ReadDocument(string name) => ReadMeta(Connection, "doc:" + name);

    public void WriteDocument(string name, string json)
    {
        using var tx = Connection.BeginTransaction();
        WriteMeta(Connection, tx, "doc:" + name, json);
        tx.Commit();
    }

    // ---- Bekannte SSH-Host-Keys --------------------------------------------------------------------

    public string? FindHostKey(string host, int port, string keyType)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT fingerprint FROM known_hosts WHERE host = $h AND port = $p AND key_type = $k";
        cmd.Parameters.AddWithValue("$h", host.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$p", port);
        cmd.Parameters.AddWithValue("$k", keyType);
        return cmd.ExecuteScalar() as string;
    }

    public IReadOnlyDictionary<string, string> FindAllHostKeys(string host, int port)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT key_type, fingerprint FROM known_hosts WHERE host = $h AND port = $p ORDER BY added_utc DESC";
        cmd.Parameters.AddWithValue("$h", host.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$p", port);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.TryAdd(reader.GetString(0), reader.GetString(1));
        return result;
    }

    public void RememberHostKey(string host, int port, string keyType, string fingerprint)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO known_hosts (host, port, key_type, fingerprint, added_utc) VALUES ($h, $p, $k, $f, $t)
            ON CONFLICT(host, port, key_type) DO UPDATE SET fingerprint = excluded.fingerprint, added_utc = excluded.added_utc
            """;
        cmd.Parameters.AddWithValue("$h", host.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$p", port);
        cmd.Parameters.AddWithValue("$k", keyType);
        cmd.Parameters.AddWithValue("$f", fingerprint);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    // ---- Infrastruktur -----------------------------------------------------------------------------

    private SqliteConnection Connection => _connection ?? throw new ObjectDisposedException(nameof(EncryptedConnectionStore));

    /// <summary>
    /// Schlüssel direkt als Bytes an SQLite übergeben (<c>x'…'</c>-Raw-Key) – ohne ihn als .NET-String in einen
    /// SQL-Befehl einzubauen. Strings sind unveränderlich und blieben bis zur Speicherbereinigung (oder in einem
    /// Crash-Dump) im Arbeitsspeicher; das Byte-Array wird sofort danach überschrieben.
    /// </summary>
    private static void ApplyRawKey(SqliteConnection connection, byte[] dek)
    {
        const string HexDigits = "0123456789ABCDEF";
        var key = new byte[dek.Length * 2 + 3];
        try
        {
            key[0] = (byte)'x';
            key[1] = (byte)'\'';
            for (var i = 0; i < dek.Length; i++)
            {
                key[2 + i * 2] = (byte)HexDigits[dek[i] >> 4];
                key[3 + i * 2] = (byte)HexDigits[dek[i] & 0xF];
            }
            key[^1] = (byte)'\'';
            var rc = SQLitePCL.raw.sqlite3_key(connection.Handle, key);
            if (rc != SQLitePCL.raw.SQLITE_OK) throw new InvalidDataException($"Setting the database key failed (SQLite error {rc}).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static SqliteConnection OpenConnection(string path, byte[] dek)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // Schlüssel nicht in einem Pool weiterleben lassen
        }.ToString());
        connection.Open();
        try
        {
            // SQLCipher-v4-Format; Raw-Key ⇒ keine zusätzliche interne PBKDF2 (Argon2id ist bereits erfolgt)
            Execute(connection, null, "PRAGMA cipher = 'sqlcipher'; PRAGMA legacy = 4;");
            ApplyRawKey(connection, dek);
            // Schlüsselprüfung: wirft SQLITE_NOTADB bei falschem Schlüssel
            Execute(connection, null, "SELECT count(*) FROM sqlite_master; PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? ReadMeta(SqliteConnection db, string key)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void WriteMeta(SqliteConnection db, SqliteTransaction tx, string key, string value)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static SqliteParameter[] AddParameters(SqliteCommand cmd, params string[] names)
        => names.Select(n => cmd.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();

    private static void Set(SqliteParameter[] parameters, params object?[] values)
    {
        for (var i = 0; i < parameters.Length; i++)
            parameters[i].Value = values[i] ?? DBNull.Value;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);

    public void Dispose()
    {
        if (_connection is null) return;
        _connection.Dispose();
        _connection = null;
        CryptographicOperations.ZeroMemory(_dek);
        _dek = [];
    }
}
