using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Konscious.Security.Cryptography;

namespace HostDeck.Storage;

/// <summary>Argon2id-Parameter. Standard: 64 MiB, 3 Durchläufe, 4 Lanes (≈ 0,3 s) – über OWASP-Minimum.</summary>
public sealed record KdfParameters(int MemoryKiB = 65536, int Iterations = 3, int Parallelism = 4)
{
    public static KdfParameters Default { get; } = new();

    /// <summary>Nur für Tests – absichtlich schwach.</summary>
    public static KdfParameters FastForTests { get; } = new(MemoryKiB: 1024, Iterations: 1, Parallelism: 1);
}

/// <summary>Schützt den Datenbankschlüssel gerätegebunden (z. B. Windows DPAPI) für „auf diesem PC merken“.</summary>
public interface IDeviceKeyProtector
{
    string Id { get; }
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>DPAPI, gebunden an Windows-Benutzer + Rechner.</summary>
public sealed class DpapiDeviceKeyProtector : IDeviceKeyProtector
{
    private static readonly byte[] Entropy = "HostDeck.DeviceSlot.v1"u8.ToArray();

    public string Id => "dpapi-currentuser";

    public byte[] Protect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser)
        : throw new PlatformNotSupportedException("DPAPI ist nur unter Windows verfügbar.");

    public byte[] Unprotect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser)
        : throw new PlatformNotSupportedException("DPAPI ist nur unter Windows verfügbar.");
}

public sealed class WrongMasterPasswordException() : Exception("Das Master-Passwort ist falsch.");

/// <summary>
/// Schlüsseldatei neben der Datenbank (<c>*.key</c>).
///
/// <code>
/// Master-Passwort ──Argon2id(salt)──► KEK ──AES-256-GCM──► DEK (zufällig, 256 bit) ──► SQLCipher-Raw-Key
///                                       Geräteschlüssel (DPAPI) ──► DEK  (optional, „auf diesem PC merken“)
/// </code>
/// Passwortwechsel = DEK neu verpacken, die Datenbank bleibt unverändert. Weitere „Slots“
/// (z. B. Team-Schlüssel) lassen sich später ergänzen.
/// </summary>
internal sealed class KeyFile
{
    private const string FormatName = "HostDeck.KeyFile";
    private const int CurrentVersion = 1;
    private static readonly byte[] PasswordSlotAad = "HostDeck.KeyFile.v1.password"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;
    public string Cipher { get; set; } = "sqlcipher4";
    public required KdfSection Kdf { get; set; }
    public required WrappedKey PasswordSlot { get; set; }
    public DeviceSlot? Device { get; set; }

    public sealed class KdfSection
    {
        public string Algorithm { get; set; } = "argon2id";
        public int MemoryKiB { get; set; }
        public int Iterations { get; set; }
        public int Parallelism { get; set; }
        public required string Salt { get; set; }
    }

    public sealed class WrappedKey
    {
        public required string Nonce { get; set; }
        public required string Ciphertext { get; set; }
        public required string Tag { get; set; }
    }

    public sealed class DeviceSlot
    {
        public required string Protector { get; set; }
        public required string Data { get; set; }
    }

    // ---- Erzeugen / Entsperren ---------------------------------------------------------------------

    public static (KeyFile File, byte[] Dek) Create(string masterPassword, KdfParameters kdf)
    {
        ValidatePassword(masterPassword);
        var dek = RandomNumberGenerator.GetBytes(32);
        return (CreateForDek(dek, masterPassword, kdf), dek);
    }

    public static KeyFile CreateForDek(byte[] dek, string masterPassword, KdfParameters kdf)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = DeriveKek(masterPassword, salt, kdf);
        try
        {
            return new KeyFile
            {
                Kdf = new KdfSection
                {
                    MemoryKiB = kdf.MemoryKiB,
                    Iterations = kdf.Iterations,
                    Parallelism = kdf.Parallelism,
                    Salt = Convert.ToBase64String(salt),
                },
                PasswordSlot = Wrap(dek, kek),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public byte[] UnlockWithPassword(string masterPassword)
    {
        var kek = DeriveKek(masterPassword, Convert.FromBase64String(Kdf.Salt), KdfParams);
        try { return Unwrap(PasswordSlot, kek); }
        catch (AuthenticationTagMismatchException) { throw new WrongMasterPasswordException(); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public byte[]? TryUnlockWithDevice(IDeviceKeyProtector protector)
    {
        if (Device is null || Device.Protector != protector.Id) return null;
        try { return protector.Unprotect(Convert.FromBase64String(Device.Data)); }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or FormatException) { return null; }
    }

    public KdfParameters KdfParams => new(Kdf.MemoryKiB, Kdf.Iterations, Kdf.Parallelism);

    // ---- Datei -------------------------------------------------------------------------------------

    public static KeyFile Load(string path)
    {
        var file = JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
                   ?? throw new InvalidDataException("Schlüsseldatei ist leer.");
        if (file.Format != FormatName) throw new InvalidDataException("Keine HostDeck-Schlüsseldatei.");
        if (file.Version > CurrentVersion) throw new InvalidDataException($"Schlüsseldatei-Version {file.Version} wird nicht unterstützt (bitte HostDeck aktualisieren).");
        if (file.Kdf.Algorithm != "argon2id") throw new InvalidDataException($"Unbekannte KDF '{file.Kdf.Algorithm}'.");
        return file;
    }

    /// <summary>Atomar schreiben (temp + Replace), damit ein Absturz nie eine halbe Schlüsseldatei hinterlässt.</summary>
    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
        else File.Move(temp, path);
    }

    // ---- Krypto ------------------------------------------------------------------------------------

    internal static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw new ArgumentException("Das Master-Passwort muss mindestens 8 Zeichen lang sein.", nameof(password));
    }

    internal static byte[] DeriveKek(string password, byte[] salt, KdfParameters kdf)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        try
        {
            using var argon = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = kdf.MemoryKiB,
                Iterations = kdf.Iterations,
                DegreeOfParallelism = kdf.Parallelism,
            };
            return argon.GetBytes(32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static WrappedKey Wrap(byte[] dek, byte[] kek)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[dek.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(kek, tag.Length);
        aes.Encrypt(nonce, dek, cipher, tag, PasswordSlotAad);
        return new WrappedKey
        {
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(cipher),
            Tag = Convert.ToBase64String(tag),
        };
    }

    private static byte[] Unwrap(WrappedKey wrapped, byte[] kek)
    {
        var cipher = Convert.FromBase64String(wrapped.Ciphertext);
        var tag = Convert.FromBase64String(wrapped.Tag);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(kek, tag.Length);
        aes.Decrypt(Convert.FromBase64String(wrapped.Nonce), cipher, tag, plain, PasswordSlotAad);
        return plain;
    }

    public void SetDeviceSlot(IDeviceKeyProtector protector, byte[] dek) =>
        Device = new DeviceSlot { Protector = protector.Id, Data = Convert.ToBase64String(protector.Protect(dek)) };
}
