using System.Security.Cryptography;
using HostDeck.Storage;
using Microsoft.Data.Sqlite;

namespace HostDeck.Tests.Storage;

public class RawKeyCompatibilityTests
{
    [Fact]
    public void Databases_keyed_with_the_former_pragma_string_still_open()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(EncryptedConnectionStore).TypeHandle);
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "old.hdk");
        var dek = RandomNumberGenerator.GetBytes(32);

        // so wurde bis 1.0.32 verschlüsselt
        using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = $"PRAGMA cipher = 'sqlcipher'; PRAGMA legacy = 4; PRAGMA key = \"x'{Convert.ToHexString(dek)}'\"; " +
                              "CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('hello');";
            cmd.ExecuteNonQuery();
        }

        using var connection = EncryptedConnectionStore.OpenConnection(path, dek);
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT v FROM t";
        Assert.Equal("hello", read.ExecuteScalar());

        var wrong = RandomNumberGenerator.GetBytes(32);
        Assert.ThrowsAny<Exception>(() => EncryptedConnectionStore.OpenConnection(path, wrong).Dispose());
    }
}
