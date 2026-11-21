using Renci.SshNet;
using Renci.SshNet.Common;

namespace HostDeck.Protocols.Ssh;

public sealed record RemoteEntry(string Name, string FullPath, bool IsDirectory, bool IsSymbolicLink, long Size, DateTime LastWriteTime, string Permissions);

public sealed record TransferProgress(string Name, long Transferred, long Total);

/// <summary>SFTP-Dateizugriff für den Seitenleisten-Browser (eigene Verbindung, Host-Key bereits bestätigt).</summary>
public sealed class SftpSession : IAsyncDisposable
{
    private readonly SftpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1); // SFTP-Kanal nicht parallel nutzen
    private readonly string _endpointHost;

    internal SftpSession(SshEndpoint endpoint, string expectedFingerprint, TimeSpan timeout)
    {
        _endpointHost = endpoint.Host;
        _client = new SftpClient(SshConnectionFactory.Create(endpoint, timeout, interactivePrompt: null));
        _client.HostKeyReceived += (_, e) => e.CanTrust = "SHA256:" + e.FingerPrintSHA256 == expectedFingerprint;
    }

    public string? HomeDirectory { get; private set; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _client.ConnectAsync(ct).ConfigureAwait(false);
        HomeDirectory = _client.WorkingDirectory;
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = new List<RemoteEntry>();
            await foreach (var f in _client.ListDirectoryAsync(path, ct).ConfigureAwait(false))
            {
                if (f.Name is "." or "..") continue;
                var isDir = f.IsDirectory;
                if (f.IsSymbolicLink)
                {
                    try { isDir = (await _client.GetAttributesAsync(f.FullName, ct).ConfigureAwait(false)).IsDirectory; }
                    catch (SftpPathNotFoundException) { /* toter Link */ }
                    catch (SftpPermissionDeniedException) { }
                }
                list.Add(new RemoteEntry(f.Name, f.FullName, isDir, f.IsSymbolicLink, f.Length, f.LastWriteTime, Permissions(f.Attributes)));
            }
            return list.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task UploadAsync(string localFile, string remoteDirectory, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var name = Path.GetFileName(localFile);
        var target = Combine(remoteDirectory, name);
        await using var stream = File.OpenRead(localFile);
        var total = stream.Length;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _client.UploadFileAsync(stream, target, true,
                new Progress<UploadFileProgressReport>(r => progress?.Report(new TransferProgress(name, (long)r.TotalBytesUploaded, total))), ct)
                .ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task DownloadAsync(RemoteEntry entry, string localDirectory, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var target = LocalFileNames.UniqueTarget(localDirectory, LocalFileNames.Sanitize(entry.Name));
        var temp = target + ".hostdeck-part";
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var stream = File.Create(temp))
            {
                await _client.DownloadFileAsync(entry.FullPath, stream,
                    new Progress<DownloadFileProgressReport>(r => progress?.Report(new TransferProgress(entry.Name, (long)r.TotalBytesDownloaded, entry.Size))), ct)
                    .ConfigureAwait(false);
            }
            File.Move(temp, target, overwrite: false); // abgebrochene Downloads hinterlassen keine halbe Zieldatei
            LocalFileNames.MarkAsDownloaded(target, $"sftp://{_endpointHost}{entry.FullPath}");
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        finally { _gate.Release(); }
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Locked(() => _client.CreateDirectoryAsync(path, ct), ct);
    public Task RenameAsync(string from, string to, CancellationToken ct = default) => Locked(() => _client.RenameFileAsync(from, to, ct), ct);

    public Task DeleteAsync(RemoteEntry entry, CancellationToken ct = default) => Locked(() => entry.IsDirectory && !entry.IsSymbolicLink
        ? _client.DeleteDirectoryAsync(entry.FullPath, ct)
        : _client.DeleteFileAsync(entry.FullPath, ct), ct);

    public static string Combine(string directory, string name) => directory.EndsWith('/') ? directory + name : directory + "/" + name;

    public static string Parent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i <= 0 ? "/" : trimmed[..i];
    }

    private async Task Locked(Func<Task> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private static string Permissions(Renci.SshNet.Sftp.SftpFileAttributes a)
    {
        char P(bool b, char c) => b ? c : '-';
        return string.Concat(
            a.IsDirectory ? 'd' : a.IsSymbolicLink ? 'l' : '-',
            P(a.OwnerCanRead, 'r'), P(a.OwnerCanWrite, 'w'), P(a.OwnerCanExecute, 'x'),
            P(a.GroupCanRead, 'r'), P(a.GroupCanWrite, 'w'), P(a.GroupCanExecute, 'x'),
            P(a.OthersCanRead, 'r'), P(a.OthersCanWrite, 'w'), P(a.OthersCanExecute, 'x'));
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Run(() =>
        {
            if (_client.IsConnected) _client.Disconnect();
            _client.Dispose();
        }).ConfigureAwait(false);
        _gate.Dispose();
    }
}
