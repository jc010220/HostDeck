using System.Security.Cryptography;
using HostDeck.Protocols.Vnc;

namespace HostDeck.Tests.Vnc;

public class VncAuthTests
{
    [Fact]
    public void Des_challenge_uses_bit_reversed_key()
    {
        // Gegenprobe: Schlüssel "password" bitgespiegelt = 0E 86 CE CE EE F6 4E 26
        var challenge = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        using var des = DES.Create();
        des.Key = [0x0E, 0x86, 0xCE, 0xCE, 0xEE, 0xF6, 0x4E, 0x26];
        Assert.Equal(des.EncryptEcb(challenge, PaddingMode.None), RfbClient.EncryptChallenge(challenge, "password"));
        // nur die ersten 8 Zeichen zählen
        Assert.Equal(RfbClient.EncryptChallenge(challenge, "password"), RfbClient.EncryptChallenge(challenge, "password-lang"));
    }
}

/// <summary>
/// Gegen echte VNC-Server (TigerVNC). Konfiguration:
/// HOSTDECK_TEST_VNC_AUTH=host:port (mit Passwort HOSTDECK_TEST_VNC_PASSWORD, Hintergrund #336699),
/// HOSTDECK_TEST_VNC_OPEN=host:port (ohne Passwort, beliebiges Muster).
/// </summary>
public class VncIntegrationTests
{
    private static (string Host, int Port) Target(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        Assert.SkipWhen(value is null, $"Kein VNC-Testserver konfiguriert ({variable}).");
        var parts = value!.Split(':');
        return (parts[0], int.Parse(parts[1]));
    }

    private static async Task<byte[]> CaptureAsync(RfbClient client, string host, int port, string? password)
    {
        var full = new TaskCompletionSource();
        long covered = 0;
        client.FramebufferUpdated += r => { if (Interlocked.Add(ref covered, (long)r.Width * r.Height) >= (long)client.Width * client.Height) full.TrySetResult(); };
        await client.ConnectAsync(host, port, () => password, ct: TestContext.Current.CancellationToken);
        await full.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        byte[] copy = [];
        client.LockFramebuffer((fb, _, _) => copy = (byte[])fb.Clone());
        return copy;
    }

    [Fact]
    public async Task Password_server_delivers_expected_background()
    {
        var (host, port) = Target("HOSTDECK_TEST_VNC_AUTH");
        await using var client = new RfbClient();
        var fb = await CaptureAsync(client, host, port, Environment.GetEnvironmentVariable("HOSTDECK_TEST_VNC_PASSWORD"));
        Assert.Equal("VNC-Passwort", client.SecurityType);
        Assert.Equal((800, 600), (client.Width, client.Height));
        var center = (300 * client.Width + 400) * 4;
        Assert.Equal(new byte[] { 0x99, 0x66, 0x33, 0xFF }, fb[center..(center + 4)]); // BGRA von #336699
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var (host, port) = Target("HOSTDECK_TEST_VNC_AUTH");
        await using var client = new RfbClient();
        await Assert.ThrowsAsync<VncAuthenticationException>(() =>
            client.ConnectAsync(host, port, () => "falsch", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Zrle_hextile_and_raw_decode_identically()
    {
        var (host, port) = Target("HOSTDECK_TEST_VNC_OPEN");
        byte[] reference;
        await using (var raw = new RfbClient { PreferredEncodings = [0] })
            reference = await CaptureAsync(raw, host, port, null);
        Assert.True(reference.Distinct().Count() > 3, "Testmuster erwartet (xsetroot -mod)");

        foreach (var encoding in new[] { 16, 5 }) // ZRLE, Hextile
        {
            await using var client = new RfbClient { PreferredEncodings = [encoding, 0] };
            var fb = await CaptureAsync(client, host, port, null);
            Assert.Equal("Keine", client.SecurityType);
            var firstDiff = Enumerable.Range(0, fb.Length).FirstOrDefault(i => fb[i] != reference[i], -1);
            Assert.True(firstDiff < 0, $"Kodierung {encoding}: Abweichung bei Byte {firstDiff} (Pixel {firstDiff / 4 % client.Width},{firstDiff / 4 / client.Width})");
        }
    }

    [Fact]
    public async Task Pointer_and_key_events_are_accepted()
    {
        var (host, port) = Target("HOSTDECK_TEST_VNC_OPEN");
        await using var client = new RfbClient();
        await CaptureAsync(client, host, port, null);
        await client.SendPointerAsync(10, 10, 1);
        await client.SendPointerAsync(10, 10, 0);
        await client.SendKeyAsync(0xff0d, true);
        await client.SendKeyAsync(0xff0d, false);
        await client.SendClipboardAsync("hallo");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.True(client.IsConnected);
    }
}
