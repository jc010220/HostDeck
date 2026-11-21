using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace HostDeck.Import.MRemoteNG.Crypto;

/// <summary>Namen identisch zu mRemoteNG <c>BlockCipherEngines</c> (Attribut <c>EncryptionEngine</c>).</summary>
public enum ConfConsCipherEngine { AES, Twofish, Serpent }

/// <summary>Namen identisch zu mRemoteNG <c>BlockCipherModes</c> (Attribut <c>BlockCipherMode</c>).</summary>
public enum ConfConsCipherMode { GCM, CCM, EAX }

public sealed class ConfConsDecryptionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Ein von mRemoteNG verwendetes Verschlüsselungsverfahren für Felder bzw. die ganze Datei.</summary>
public interface IConfConsCipher
{
    string Description { get; }

    /// <summary>Entschlüsselt einen Base64-Wert. Leere Eingabe ⇒ "". Fehler ⇒ <see cref="ConfConsDecryptionException"/>.</summary>
    string Decrypt(string cipherTextBase64, string password);

    /// <summary>Verschlüsselt (für Export/Rundreise). Leere Eingabe ⇒ "".</summary>
    string Encrypt(string plainText, string password);
}

public static class ConfConsDefaults
{
    /// <summary>Hartkodierter Standardschlüssel von mRemoteNG, wenn kein Master-Passwort gesetzt ist.</summary>
    public const string DefaultPassword = "mR3m";

    /// <summary>Klartext des Root-Attributs <c>Protected</c> ohne bzw. mit eigenem Master-Passwort.</summary>
    public const string NotProtectedMarker = "ThisIsNotProtected";
    public const string ProtectedMarker = "ThisIsProtected";

    public const int DefaultKdfIterations = 1000;
}

/// <summary>
/// mRemoteNG ≥ 1.76 / ConfVersion ≥ 2.6: <c>AeadCryptographyProvider</c>.
///
/// <para>Format (Base64): <c>salt[16] ‖ nonce[16] ‖ ciphertext ‖ tag[16]</c></para>
/// <list type="bullet">
/// <item>Key = PBKDF2-HMAC-<b>SHA1</b>(Pkcs5PasswordToBytes(pw), salt, KdfIterations) → 256 bit</item>
/// <item>Associated Data = salt</item>
/// <item>Nonce = <b>128 bit</b> (CCM: 88 bit) – deshalb BouncyCastle statt <c>System.Security.Cryptography.AesGcm</c>,
///       das ausschließlich 96-bit-Nonces akzeptiert.</item>
/// <item>Klartext UTF-8.</item>
/// </list>
/// </summary>
public sealed class AeadConfConsCipher : IConfConsCipher
{
    private const int SaltBytes = 16;
    private const int MacBits = 128;
    private const int KeyBits = 256;

    public AeadConfConsCipher(
        ConfConsCipherEngine engine = ConfConsCipherEngine.AES,
        ConfConsCipherMode mode = ConfConsCipherMode.GCM,
        int kdfIterations = ConfConsDefaults.DefaultKdfIterations)
    {
        if (kdfIterations < 1000)
            throw new ArgumentOutOfRangeException(nameof(kdfIterations), "mRemoteNG requires at least 1000 KDF iterations.");
        Engine = engine;
        Mode = mode;
        KdfIterations = kdfIterations;
    }

    public ConfConsCipherEngine Engine { get; }
    public ConfConsCipherMode Mode { get; }
    public int KdfIterations { get; }
    public string Description => $"{Engine}-{KeyBits}/{Mode}, PBKDF2-SHA1 × {KdfIterations}";

    private int NonceBytes => Mode == ConfConsCipherMode.CCM ? 11 : 16;

    public string Decrypt(string cipherTextBase64, string password)
    {
        if (string.IsNullOrWhiteSpace(cipherTextBase64)) return "";
        RequirePassword(password);
        if (!IsLatin1(password))
            throw new ConfConsDecryptionException("Password contains characters outside Latin-1 – mRemoteNG cannot use such passwords.");

        byte[] data;
        try { data = Convert.FromBase64String(cipherTextBase64.Trim()); }
        catch (FormatException ex) { throw new ConfConsDecryptionException("Value is not valid Base64.", ex); }

        if (data.Length < SaltBytes + NonceBytes + MacBits / 8)
            throw new ConfConsDecryptionException("Ciphertext is too short.");

        var salt = data.AsSpan(0, SaltBytes).ToArray();
        var nonce = data.AsSpan(SaltBytes, NonceBytes).ToArray();
        var cipherText = data.AsSpan(SaltBytes + NonceBytes).ToArray();
        var key = DeriveKey(password, salt, KdfIterations);

        var cipher = CreateCipher();
        cipher.Init(false, new AeadParameters(new KeyParameter(key), MacBits, nonce, salt));
        var plain = new byte[cipher.GetOutputSize(cipherText.Length)];
        try
        {
            var len = cipher.ProcessBytes(cipherText, 0, cipherText.Length, plain, 0);
            len += cipher.DoFinal(plain, len);
            return Encoding.UTF8.GetString(plain, 0, len);
        }
        catch (InvalidCipherTextException ex)
        {
            throw new ConfConsDecryptionException("Decryption failed (wrong password or damaged data).", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string Encrypt(string plainText, string password)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        RequirePassword(password);
        if (!IsLatin1(password))
            throw new ArgumentException("The master password may only contain Latin-1 characters (mRemoteNG/BouncyCastle limitation).", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = DeriveKey(password, salt, KdfIterations);
        try
        {
            var plain = Encoding.UTF8.GetBytes(plainText);
            var cipher = CreateCipher();
            cipher.Init(true, new AeadParameters(new KeyParameter(key), MacBits, nonce, salt));
            var output = new byte[cipher.GetOutputSize(plain.Length)];
            var len = cipher.ProcessBytes(plain, 0, plain.Length, output, 0);
            len += cipher.DoFinal(output, len);

            var result = new byte[SaltBytes + NonceBytes + len];
            salt.CopyTo(result, 0);
            nonce.CopyTo(result, SaltBytes);
            Array.Copy(output, 0, result, SaltBytes + NonceBytes, len);
            return Convert.ToBase64String(result);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Identisch zu mRemoteNG <c>Pkcs5S2KeyGenerator</c>: BouncyCastle-PBKDF2 mit SHA1 und
    /// <c>Pkcs5PasswordToBytes</c> (nimmt nur das niederwertige Byte jedes UTF-16-Zeichens!).
    /// </summary>
    internal static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        // Native PBKDF2 (≈10× schneller als BouncyCastle-managed), bitgleich zu Pkcs5S2ParametersGenerator,
        // solange die Passwort-Bytes identisch gebildet werden – siehe Pkcs5PasswordBytes.
        var passwordBytes = Pkcs5PasswordBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA1, KeyBits / 8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>Nachbau von BouncyCastle <c>PbeParametersGenerator.Pkcs5PasswordToBytes</c>: (byte)char.</summary>
    internal static byte[] Pkcs5PasswordBytes(string password)
    {
        var bytes = new byte[password.Length];
        for (var i = 0; i < password.Length; i++)
            bytes[i] = checked((byte)password[i]); // wie BouncyCastle: > U+00FF ⇒ OverflowException
        return bytes;
    }

    /// <summary>Referenzimplementierung exakt wie mRemoteNG (nur für Tests / Gegenprobe).</summary>
    internal static byte[] DeriveKeyBouncyCastle(string password, byte[] salt, int iterations)
    {
        var generator = new Pkcs5S2ParametersGenerator();
        generator.Init(PbeParametersGenerator.Pkcs5PasswordToBytes(password.ToCharArray()), salt, iterations);
        return ((KeyParameter)generator.GenerateDerivedMacParameters(KeyBits)).GetKey();
    }

    private IAeadBlockCipher CreateCipher()
    {
        IBlockCipher engine = Engine switch
        {
            ConfConsCipherEngine.AES => new AesEngine(),
            ConfConsCipherEngine.Twofish => new TwofishEngine(),
            ConfConsCipherEngine.Serpent => new SerpentEngine(),
            _ => throw new ArgumentOutOfRangeException(nameof(Engine), Engine, null),
        };
        return Mode switch
        {
            ConfConsCipherMode.GCM => new GcmBlockCipher(engine),
            ConfConsCipherMode.CCM => new CcmBlockCipher(engine),
            ConfConsCipherMode.EAX => new EaxBlockCipher(engine),
            _ => throw new ArgumentOutOfRangeException(nameof(Mode), Mode, null),
        };
    }

    /// <summary>BouncyCastle (und damit mRemoteNG) wirft bei Zeichen &gt; U+00FF eine OverflowException.</summary>
    public static bool IsLatin1(string password) => password.All(c => c <= '\u00FF');

    private static void RequirePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password required.", nameof(password));
    }
}

/// <summary>
/// Altes Format (ConfVersion ≤ 2.5, mRemoteNG ≤ 1.75): <c>LegacyRijndaelCryptographyProvider</c>.
/// <para>Format (Base64): <c>iv[16] ‖ AES-128-CBC-PKCS7(ciphertext)</c>, Key = MD5(UTF-8(pw)).</para>
/// </summary>
public sealed class LegacyRijndaelConfConsCipher : IConfConsCipher
{
    private const int BlockBytes = 16;

    public string Description => "AES-128/CBC (Legacy Rijndael, MD5-Key)";

    public string Decrypt(string cipherTextBase64, string password)
    {
        if (string.IsNullOrEmpty(cipherTextBase64) || string.IsNullOrEmpty(password)) return cipherTextBase64 ?? "";

        var key = MD5.HashData(Encoding.UTF8.GetBytes(password));
        try
        {
            var data = Convert.FromBase64String(cipherTextBase64.Trim());
            if (data.Length < 2 * BlockBytes || data.Length % BlockBytes != 0)
                throw new ConfConsDecryptionException("Ciphertext has an invalid length.");

            using var aes = Aes.Create();
            aes.Key = key;
            var plain = aes.DecryptCbc(data.AsSpan(BlockBytes), data.AsSpan(0, BlockBytes), PaddingMode.PKCS7);

            // mRemoteNG liest per StreamReader(UTF8, detectEncodingFromByteOrderMarks: true)
            using var reader = new StreamReader(new MemoryStream(plain), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new ConfConsDecryptionException("Decryption failed (wrong password or damaged data).", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string Encrypt(string plainText, string password)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        var key = MD5.HashData(Encoding.UTF8.GetBytes(password));
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            var iv = RandomNumberGenerator.GetBytes(BlockBytes);
            var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(plainText), iv, PaddingMode.PKCS7);
            return Convert.ToBase64String([.. iv, .. cipher]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

/// <summary>
/// Neuere mRemoteNG-Builds: <c>KeyProtection="Dpapi"</c> – an Windows-Benutzer + Rechner gebunden.
/// Funktioniert nur unter Windows mit demselben Benutzerkonto, das die Datei gespeichert hat.
/// </summary>
public sealed class DpapiConfConsCipher : IConfConsCipher
{
    public string Description => "Windows DPAPI (CurrentUser)";

    public string Decrypt(string cipherTextBase64, string password)
    {
        if (string.IsNullOrWhiteSpace(cipherTextBase64)) return "";
        if (!OperatingSystem.IsWindows())
            throw new ConfConsDecryptionException("DPAPI-protected files can only be decrypted on Windows.");
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(cipherTextBase64.Trim()), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new ConfConsDecryptionException(
                "DPAPI decryption failed – the file was saved under a different Windows account or computer.", ex);
        }
    }

    public string Encrypt(string plainText, string password)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), null, DataProtectionScope.CurrentUser));
    }
}
