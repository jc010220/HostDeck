using System.Collections.Concurrent;
using System.Text;
using HostDeck.Protocols.Ssh;

namespace HostDeck.Tests.Ssh;

/// <summary>
/// Integrationstests gegen einen echten SSH-Server. Aktiv nur, wenn konfiguriert:
/// HOSTDECK_TEST_SSH=host:port, HOSTDECK_TEST_SSH_USER, HOSTDECK_TEST_SSH_PASSWORD, optional HOSTDECK_TEST_SSH_KEYFILE.
/// </summary>
public class SshIntegrationTests
{
    private static readonly string? Target = Environment.GetEnvironmentVariable("HOSTDECK_TEST_SSH");

    private sealed class MemoryKnownHosts : IKnownHosts
    {
        public readonly ConcurrentDictionary<string, string> Entries = new();
        public string? Find(string host, int port, string keyType) => Entries.GetValueOrDefault($"{host}:{port}:{keyType}");
        public void Remember(string host, int port, string keyType, string fingerprint) => Entries[$"{host}:{port}:{keyType}"] = fingerprint;
    }

    private static SshEndpoint Endpoint(bool useKey = false)
    {
        Assert.SkipWhen(Target is null, "Kein Test-SSH-Server konfiguriert (HOSTDECK_TEST_SSH).");
        var parts = Target!.Split(':');
        var keyFile = Environment.GetEnvironmentVariable("HOSTDECK_TEST_SSH_KEYFILE");
        if (useKey) Assert.SkipWhen(keyFile is null, "Kein Test-Schlüssel konfiguriert.");
        return new SshEndpoint(parts[0], int.Parse(parts[1]), Environment.GetEnvironmentVariable("HOSTDECK_TEST_SSH_USER")!)
        {
            Password = useKey ? null : Environment.GetEnvironmentVariable("HOSTDECK_TEST_SSH_PASSWORD"),
            PrivateKey = useKey ? File.ReadAllText(keyFile!) : null,
        };
    }

    /// <summary>Sammelt Terminal-Ausgabe und wartet auf einen Text.</summary>
    private sealed class OutputCollector
    {
        private readonly StringBuilder _sb = new();
        private readonly object _lock = new();
        public void Add(string s) { lock (_lock) _sb.Append(s); }
        public string Text { get { lock (_lock) return _sb.ToString(); } }

        public async Task<bool> WaitForAsync(string text, int timeoutMs = 8000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                if (Text.Contains(text, StringComparison.Ordinal)) return true;
                await Task.Delay(50);
            }
            return false;
        }
    }

    [Fact]
    public async Task Password_login_terminal_io_and_trust_on_first_use()
    {
        var known = new MemoryKnownHosts();
        var prompts = new List<HostKeyInfo>();
        var output = new OutputCollector();

        await using (var session = new SshTerminalSession(Endpoint(), known, new SshSessionOptions { TrackWorkingDirectory = false }))
        {
            session.Output += output.Add;
            await session.ConnectAsync(info => { prompts.Add(info); return HostKeyDecision.AcceptAndSave; }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(session.IsConnected);
            session.SendInput("echo hallo-$((40+2))\n");
            Assert.True(await output.WaitForAsync("hallo-42"), output.Text);
        }

        Assert.Single(prompts);
        Assert.False(prompts[0].Changed);
        Assert.StartsWith("SHA256:", prompts[0].Fingerprint);
        Assert.Single(known.Entries);

        // Zweite Verbindung: Key bekannt ⇒ keine Nachfrage
        await using var again = new SshTerminalSession(Endpoint(), known, new SshSessionOptions { TrackWorkingDirectory = false });
        await again.ConnectAsync(_ => throw new InvalidOperationException("darf nicht fragen"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(again.IsConnected);
    }

    [Fact]
    public async Task Changed_host_key_is_reported_and_can_be_rejected()
    {
        var known = new MemoryKnownHosts();
        var ep = Endpoint();
        known.Remember(ep.Host, ep.Port, "ssh-ed25519", "SHA256:alterSchluessel");
        HostKeyInfo? seen = null;

        await using var session = new SshTerminalSession(ep, known);
        var ex = await Assert.ThrowsAsync<HostKeyRejectedException>(() =>
            session.ConnectAsync(info => { seen = info; return HostKeyDecision.Reject; }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(seen!.Changed);
        Assert.Equal("SHA256:alterSchluessel", seen.PreviousFingerprint);
        Assert.True(ex.Info.Changed);
        Assert.Equal("SHA256:alterSchluessel", known.Find(ep.Host, ep.Port, "ssh-ed25519")); // nicht überschrieben
    }

    [Fact]
    public async Task Working_directory_tracking_is_invisible_and_follows_cd()
    {
        var output = new OutputCollector();
        var dirs = new ConcurrentQueue<string>();
        await using var session = new SshTerminalSession(Endpoint(), new MemoryKnownHosts());
        session.Output += output.Add;
        session.WorkingDirectoryChanged += dirs.Enqueue;
        await session.ConnectAsync(_ => HostKeyDecision.AcceptOnce, cancellationToken: TestContext.Current.CancellationToken);

        var until = DateTime.UtcNow.AddSeconds(8);
        while (!session.CwdTrackingActive && DateTime.UtcNow < until) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(session.CwdTrackingActive, "Hook wurde nicht aktiv: " + output.Text);

        session.SendInput("cd /tmp\n");
        until = DateTime.UtcNow.AddSeconds(8);
        while (session.WorkingDirectory != "/tmp" && DateTime.UtcNow < until) await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal("/tmp", session.WorkingDirectory);
        Assert.Contains(dirs, d => d.StartsWith("/home/", StringComparison.Ordinal)); // Startverzeichnis
        Assert.DoesNotContain("__hd_osc7", output.Text);                              // Hook-Befehl unsichtbar
    }

    [Fact]
    public async Task Public_key_login()
    {
        await using var session = new SshTerminalSession(Endpoint(useKey: true), new MemoryKnownHosts(), new SshSessionOptions { TrackWorkingDirectory = false });
        await session.ConnectAsync(_ => HostKeyDecision.AcceptOnce, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(session.IsConnected);
    }

    [Fact]
    public async Task Sftp_list_upload_download_rename_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var terminal = new SshTerminalSession(Endpoint(), new MemoryKnownHosts(), new SshSessionOptions { TrackWorkingDirectory = false });
        await terminal.ConnectAsync(_ => HostKeyDecision.AcceptOnce, cancellationToken: ct);
        await using var sftp = terminal.CreateSftp();
        await sftp.ConnectAsync(ct);

        var home = sftp.HomeDirectory!;
        var dir = SftpSession.Combine(home, "hd-test-" + Guid.NewGuid().ToString("N")[..8]);
        await sftp.CreateDirectoryAsync(dir, ct);

        var local = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(local.FullName, "bericht äöü.txt");
            await File.WriteAllTextAsync(file, "Inhalt 123", ct);
            var reports = new List<TransferProgress>();
            await sftp.UploadAsync(file, dir, new SyncProgress<TransferProgress>(reports.Add), ct);

            var entries = await sftp.ListAsync(dir, ct);
            var entry = Assert.Single(entries);
            Assert.Equal("bericht äöü.txt", entry.Name);
            Assert.Equal(10, entry.Size);
            Assert.StartsWith("-rw", entry.Permissions);

            var download = Directory.CreateDirectory(Path.Combine(local.FullName, "dl"));
            await sftp.DownloadAsync(entry, download.FullName, null, ct);
            Assert.Equal("Inhalt 123", await File.ReadAllTextAsync(Path.Combine(download.FullName, entry.Name), ct));

            await sftp.RenameAsync(entry.FullPath, SftpSession.Combine(dir, "neu.txt"), ct);
            var renamed = Assert.Single(await sftp.ListAsync(dir, ct));
            Assert.Equal("neu.txt", renamed.Name);

            await sftp.DeleteAsync(renamed, ct);
            Assert.Empty(await sftp.ListAsync(dir, ct));
            Assert.Contains(await sftp.ListAsync(home, ct), e => e.FullPath == dir && e.IsDirectory);
        }
        finally
        {
            await sftp.DeleteAsync(new RemoteEntry("", dir, true, false, 0, default, ""), ct);
            local.Delete(true);
        }
    }

    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
