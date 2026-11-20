using System.Xml.Linq;
using HostDeck.Import.MRemoteNG.Crypto;

namespace HostDeck.Tests.Crypto;

public class CipherTests
{
    private static string ProtectedAttribute(string fixture) =>
        (string)XDocument.Parse(Fixture.Read(fixture)).Root!.Attribute("Protected")!;

    [Fact]
    public void Aead_decrypts_real_mRemoteNG_value_with_default_key()
    {
        var cipher = new AeadConfConsCipher();
        Assert.Equal("ThisIsNotProtected", cipher.Decrypt(ProtectedAttribute("confCons_v2_6.xml"), "mR3m"));
    }

    [Fact]
    public void Aead_decrypts_real_mRemoteNG_value_with_custom_master_password()
    {
        var cipher = new AeadConfConsCipher();
        Assert.Equal("ThisIsProtected", cipher.Decrypt(ProtectedAttribute("confCons_v2_6_passwordis_Password.xml"), "Password"));
    }

    [Fact]
    public void Aead_respects_kdf_iterations()
    {
        var value = ProtectedAttribute("confCons_v2_6_5k-iterations.xml");
        Assert.Equal("ThisIsNotProtected", new AeadConfConsCipher(kdfIterations: 5000).Decrypt(value, "mR3m"));
        Assert.Throws<ConfConsDecryptionException>(() => new AeadConfConsCipher(kdfIterations: 1000).Decrypt(value, "mR3m"));
    }

    [Fact]
    public void Aead_wrong_password_throws()
    {
        Assert.Throws<ConfConsDecryptionException>(() =>
            new AeadConfConsCipher().Decrypt(ProtectedAttribute("confCons_v2_6_passwordis_Password.xml"), "mR3m"));
    }

    [Fact]
    public void Legacy_decrypts_real_mRemoteNG_value()
    {
        Assert.Equal("ThisIsNotProtected", new LegacyRijndaelConfConsCipher().Decrypt(ProtectedAttribute("confCons_v2_5.xml"), "mR3m"));
    }

    [Fact]
    public void Legacy_wrong_password_never_yields_marker()
    {
        var value = ProtectedAttribute("confCons_v2_5.xml");
        try { Assert.NotEqual("ThisIsNotProtected", new LegacyRijndaelConfConsCipher().Decrypt(value, "falsch")); }
        catch (ConfConsDecryptionException) { /* erwartet in ~99,6 % der Fälle (Padding-Fehler) */ }
    }

    public static TheoryData<ConfConsCipherEngine, ConfConsCipherMode> AllCombinations()
    {
        var data = new TheoryData<ConfConsCipherEngine, ConfConsCipherMode>();
        foreach (var e in Enum.GetValues<ConfConsCipherEngine>())
            foreach (var m in Enum.GetValues<ConfConsCipherMode>())
                data.Add(e, m);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Aead_roundtrip_all_engines_and_modes(ConfConsCipherEngine engine, ConfConsCipherMode mode)
    {
        var cipher = new AeadConfConsCipher(engine, mode);
        const string secret = "S3cr3t! äöü € 🔑";
        var encrypted = cipher.Encrypt(secret, "mypassword111111");
        Assert.NotEqual(encrypted, cipher.Encrypt(secret, "mypassword111111")); // zufälliges Salt + Nonce
        Assert.Equal(secret, cipher.Decrypt(encrypted, "mypassword111111"));
    }

    [Fact]
    public void Aead_layout_is_salt16_nonce16_ciphertext_tag16()
    {
        var bytes = Convert.FromBase64String(new AeadConfConsCipher().Encrypt("abc", "pw"));
        Assert.Equal(16 + 16 + 3 + 16, bytes.Length);
    }

    [Fact]
    public void Legacy_roundtrip()
    {
        var cipher = new LegacyRijndaelConfConsCipher();
        Assert.Equal("Hällo", cipher.Decrypt(cipher.Encrypt("Hällo", "mR3m"), "mR3m"));
    }

    [Fact]
    public void Empty_values_stay_empty()
    {
        Assert.Equal("", new AeadConfConsCipher().Decrypt("", "mR3m"));
        Assert.Equal("", new AeadConfConsCipher().Encrypt("", "mR3m"));
        Assert.Equal("", new LegacyRijndaelConfConsCipher().Decrypt("", "mR3m"));
    }

    [Fact]
    public void Garbage_input_throws_decryption_exception()
    {
        Assert.Throws<ConfConsDecryptionException>(() => new AeadConfConsCipher().Decrypt("not base64 !!", "mR3m"));
        Assert.Throws<ConfConsDecryptionException>(() => new AeadConfConsCipher().Decrypt("AAAA", "mR3m"));
        Assert.Throws<ConfConsDecryptionException>(() => new LegacyRijndaelConfConsCipher().Decrypt("AAAA", "mR3m"));
    }

    [Fact]
    public void Pbkdf2_matches_rfc6070_sha1_vector()
    {
        // RFC 6070: P="password", S="salt", c=4096, dkLen=20 → 4b007901b765489abead49d926f721d065a429c1
        var key = AeadConfConsCipher.DeriveKey("password", "salt"u8.ToArray(), 4096);
        Assert.Equal("4b007901b765489abead49d926f721d065a429c1", Convert.ToHexStringLower(key)[..40]);
    }

    [Theory]
    [InlineData("mR3m")]
    [InlineData("Password")]
    [InlineData("Pässwörd§ÿ")]           // Latin-1 jenseits ASCII: 1 Byte pro Zeichen (nicht UTF-8!)
    [InlineData("x")]
    public void Native_kdf_is_bit_identical_to_bouncycastle(string password)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        Assert.Equal(AeadConfConsCipher.DeriveKeyBouncyCastle(password, salt, 1000), AeadConfConsCipher.DeriveKey(password, salt, 1000));
    }

    [Fact]
    public void Non_latin1_master_password_behaves_like_mRemoteNG()
    {
        Assert.Throws<OverflowException>(() => AeadConfConsCipher.DeriveKeyBouncyCastle("€uro", new byte[16], 1000)); // mRemoteNG-Verhalten
        Assert.Throws<ArgumentException>(() => new AeadConfConsCipher().Encrypt("x", "€uro"));
        var encrypted = new AeadConfConsCipher().Encrypt("x", "mR3m");
        Assert.Throws<ConfConsDecryptionException>(() => new AeadConfConsCipher().Decrypt(encrypted, "€uro"));
    }

    [Fact]
    public void Kdf_rejects_too_few_iterations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AeadConfConsCipher(kdfIterations: 999));
    }
}
