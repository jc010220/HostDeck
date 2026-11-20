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
    public const int SchemaVersion = 1;
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
            throw new IOException($"Es existiert bereits eine Datenbank unter {path}.");
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
        try
        {
            var store = new EncryptedConnectionStore(path, keyFile, dek);
            store.Migrate();
            return store;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26) // SQLITE_NOTADB
        {
            throw new InvalidDataException("Datenbank und Schlüsseldatei passen nicht zusammen oder die Datenbank ist beschädigt.", ex);
        }
    }

    // ---- Schlüsselverwaltung -----------------------------------------------------------------------

    public void ChangeMasterPassword(string currentPassword, string newPassword)
    {
        KeyFile.ValidatePassword(newPassword);
        _keyFile.UnlockWithPassword(currentPassword); // prüft das alte Passwort
        var replacement = KeyFile.CreateForDek(_dek, newPassword, _keyFile.KdfParams);
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
            throw new InvalidDataException($"Datenbank-Schema {version} ist neuer als diese HostDeck-Version ({SchemaVersion}).");

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
    }

    // ---- Infrastruktur -----------------------------------------------------------------------------

    private SqliteConnection Connection => _connection ?? throw new ObjectDisposedException(nameof(EncryptedConnectionStore));

    private static SqliteConnection OpenConnection(string path, byte[] dek)
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
            var hex = Convert.ToHexString(dek);
            // SQLCipher-v4-Format; Raw-Key ⇒ keine zusätzliche interne PBKDF2 (Argon2id ist bereits erfolgt)
            Execute(connection, null, $"PRAGMA cipher = 'sqlcipher'; PRAGMA legacy = 4; PRAGMA key = \"x'{hex}'\";");
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
