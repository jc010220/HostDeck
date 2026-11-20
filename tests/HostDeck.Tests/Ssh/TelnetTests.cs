using System.Net;
using System.Net.Sockets;
using System.Text;
using HostDeck.Protocols.Ssh;

namespace HostDeck.Tests.Ssh;

public class TelnetTests
{
    private const byte Iac = 255, Do = 253, Will = 251, Wont = 252, Dont = 254, Sb = 250, Se = 240;

    private static async Task<byte[]> ReadAtLeast(NetworkStream s, int count, CancellationToken ct)
    {
        var buf = new List<byte>();
        var tmp = new byte[1024];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(5000);
        while (buf.Count < count)
        {
            var n = await s.ReadAsync(tmp, cts.Token);
            if (n == 0) break;
            buf.AddRange(tmp.AsSpan(0, n).ToArray());
        }
        return buf.ToArray();
    }

    [Fact]
    public async Task Negotiates_options_auto_logs_in_and_escapes_input()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var output = new StringBuilder();
        await using var session = new TelnetSession("127.0.0.1", port, raw: false, username: "admin", password: "geheim");
        session.Output += s => { lock (output) output.Append(s); };
        await session.ConnectAsync(100, 40, ct: ct);

        using var server = await listener.AcceptTcpClientAsync(ct);
        var s = server.GetStream();

        // Client bietet von sich aus NAWS + TTYPE an
        var hello = await ReadAtLeast(s, 6, ct);
        Assert.Equal(new byte[] { Iac, Will, 31, Iac, Will, 24 }, hello[..6]);

        // Server: DO NAWS, DO TTYPE, WILL ECHO, DO 99 (unbekannt), SB TTYPE SEND, dann Login-Prompt
        await s.WriteAsync(new byte[] { Iac, Do, 31, Iac, Do, 24, Iac, Will, 1, Iac, Do, 99, Iac, Sb, 24, 1, Iac, Se }, ct);
        await s.WriteAsync("Router login: "u8.ToArray(), ct);

        var replies = await ReadAtLeast(s, 9 + 3 + 3 + 20 + 7, ct); // NAWS, DO ECHO, WONT 99, TTYPE IS, Login
        var text = Encoding.Latin1.GetString(replies);
        Assert.Contains(Encoding.Latin1.GetString([Iac, Sb, 31, 0, 100, 0, 40, Iac, Se]), text);      // Fenstergröße 100x40
        Assert.Contains(Encoding.Latin1.GetString([Iac, 253, 1]), text);                              // DO ECHO
        Assert.Contains(Encoding.Latin1.GetString([Iac, Wont, 99]), text);                            // unbekannt abgelehnt
        Assert.Contains("XTERM-256COLOR", text);
        Assert.EndsWith("admin\r\0", text);                                                            // Auto-Login, CR NUL

        await s.WriteAsync("\r\nPassword: "u8.ToArray(), ct);
        var pw = Encoding.Latin1.GetString(await ReadAtLeast(s, 8, ct));
        Assert.Equal("geheim\r\0", pw);

        await s.WriteAsync("\r\nWelcome ÿ"u8.ToArray(), ct); // UTF-8 ÿ = C3 BF (kein IAC)
        await s.WriteAsync(new byte[] { Iac, Iac }, ct);             // escaptes 0xFF im Datenstrom
        await Task.Delay(300, ct);
        lock (output)
        {
            Assert.Contains("Router login:", output.ToString());
            Assert.Contains("Welcome ÿ", output.ToString());
            Assert.False(output.ToString().Contains((char)0x1f), "Ausgabe: " + string.Join(" ", output.ToString().Select(c => ((int)c).ToString("x2"))));
        }

        // Eingabe mit 0xFF wird verdoppelt; Resize schickt neues NAWS
        session.SendInput("ÿ");  // UTF-8: C3 BF – kein IAC, unverändert
        session.Resize(132, 50);
        var tail = await ReadAtLeast(s, 2 + 9, ct);
        Assert.Equal(new byte[] { 0xC3, 0xBF, Iac, Sb, 31, 0, 132, 0, 50, Iac, Se }, tail[..11]);
    }

    [Fact]
    public async Task Raw_mode_passes_bytes_unchanged_and_reports_close()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var output = new StringBuilder();
        var closed = new TaskCompletionSource<string?>();

        await using var session = new TelnetSession("127.0.0.1", port, raw: true);
        session.Output += s => { lock (output) output.Append(s); };
        session.Closed += r => closed.TrySetResult(r);
        await session.ConnectAsync(80, 24, ct: ct);
        using (var server = await listener.AcceptTcpClientAsync(ct))
        {
            var s = server.GetStream();
            session.SendInput("ping\r");
            Assert.Equal("ping\r", Encoding.ASCII.GetString(await ReadAtLeast(s, 5, ct)));
            await s.WriteAsync("pong"u8.ToArray(), ct);
            await Task.Delay(200, ct);
        }
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        lock (output) Assert.Equal("pong", output.ToString());
    }

    [Fact]
    public async Task Unreachable_port_fails_fast()
    {
        await using var session = new TelnetSession("127.0.0.1", 1, raw: true);
        await Assert.ThrowsAnyAsync<Exception>(() => session.ConnectAsync(80, 24, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
    }
}
