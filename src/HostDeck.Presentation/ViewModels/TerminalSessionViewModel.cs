using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HostDeck.Core.Model;
using HostDeck.Presentation.Services;
using HostDeck.Protocols.Ssh;
using Renci.SshNet.Common;

namespace HostDeck.Presentation.ViewModels;

/// <summary>
/// Terminal-Session eines Tabs (SSH). Ausgabe-Events kommen aus Hintergrund-Threads und werden
/// von der View gepuffert; Zustandsänderungen werden auf den UI-Thread gebracht.
/// </summary>
public sealed partial class TerminalSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SessionTabViewModel _tab;
    private readonly IConnectionStore _knownHosts;
    private readonly IDialogService _dialogs;
    private readonly AppSettings _settings;
    private readonly Action<TerminalSessionViewModel, string> _routeInput;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private SshTerminalSession? _session;
    private uint _columns = 120, _rows = 32;

    public TerminalSessionViewModel(SessionTabViewModel tab, IConnectionStore knownHosts, IDialogService dialogs,
        AppSettings settings, Action<TerminalSessionViewModel, string> routeInput)
    {
        _tab = tab;
        _knownHosts = knownHosts;
        _dialogs = dialogs;
        _settings = settings;
        _routeInput = routeInput;
    }

    public SessionTabViewModel Tab => _tab;

    private const int MaxHistoryChars = 512 * 1024;
    private readonly System.Text.StringBuilder _history = new();
    private readonly List<Action<string>> _sinks = [];
    private readonly object _outputLock = new();

    /// <summary>
    /// Terminal-Control anmelden: erhält zuerst den bisherigen Verlauf (z. B. Ausgabe vor dem Laden
    /// von WebView2), danach live. Aufruf kommt ggf. aus Hintergrund-Threads.
    /// </summary>
    public IDisposable Subscribe(Action<string> sink)
    {
        lock (_outputLock)
        {
            if (_history.Length > 0) sink(_history.ToString());
            _sinks.Add(sink);
        }
        return new Unsubscriber(() => { lock (_outputLock) _sinks.Remove(sink); });
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private void Publish(string text)
    {
        lock (_outputLock)
        {
            _history.Append(text);
            if (_history.Length > MaxHistoryChars)
            {
                // an einem Zeilenumbruch kürzen, um keine Escape-Sequenz zu zerteilen
                var cut = _history.ToString().IndexOf('\n', _history.Length - MaxHistoryChars / 2);
                _history.Remove(0, cut > 0 ? cut + 1 : _history.Length - MaxHistoryChars / 2);
            }
            foreach (var sink in _sinks) sink(text);
        }
    }

    [ObservableProperty] private string? _workingDirectory;
    [ObservableProperty] private SftpBrowserViewModel? _sftp;

    public bool IsConnected => _session?.IsConnected == true;

    [RelayCommand]
    public async Task ConnectAsync()
    {
        if (_tab.State is SessionState.Connecting or SessionState.Connected) return;
        await DisposeSessionAsync();

        var endpoint = BuildEndpoint();
        SetState(SessionState.Connecting);
        WriteStatus($"Verbinde mit {endpoint.Username}@{endpoint.Host}:{endpoint.Port} …");

        var session = new SshTerminalSession(endpoint, _knownHosts, new SshSessionOptions
        {
            Columns = _columns,
            Rows = _rows,
            Highlighting = _settings.TerminalHighlighting,
        });
        session.Output += Publish;
        session.WorkingDirectoryChanged += dir => OnUi(() =>
        {
            WorkingDirectory = dir;
            if (_settings.SftpFollowsTerminal) Sftp?.OnTerminalDirectoryChanged(dir);
        });
        session.Closed += reason => OnUi(() =>
        {
            if (_tab.State != SessionState.Connected) return;
            SetState(SessionState.Disconnected);
            WriteStatus("Verbindung beendet" + (reason is null ? "." : $": {reason}") + " – Enter oder Doppelklick auf den Tab verbindet neu.");
        });

        try
        {
            await session.ConnectAsync(
                info => OnUiAndWait(() => _dialogs.AskHostKey(info)),
                (prompt, echo) => OnUiAndWait(() => _dialogs.Prompt($"{endpoint.Host}", prompt, secret: !echo)));
            _session = session;
            SetState(SessionState.Connected);
            Sftp = new SftpBrowserViewModel(session, _dialogs, _ui);
            _ = Sftp.ConnectAsync();
        }
        catch (Exception ex) when (ex is SshException or HostKeyRejectedException or System.Net.Sockets.SocketException
                                       or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            await session.DisposeAsync();
            SetState(SessionState.Failed);
            WriteStatus($"Verbindung fehlgeschlagen: {Describe(ex)}");
        }
    }

    private SshEndpoint BuildEndpoint()
    {
        var c = _tab.Credentials;
        var username = c.Username;
        if (string.IsNullOrEmpty(username))
            username = _dialogs.Prompt(_tab.Connection.Hostname, "Benutzername:") ?? "";
        return new SshEndpoint(_tab.Connection.Hostname, _tab.Settings.Port ?? 22, username)
        {
            Password = string.IsNullOrEmpty(c.Password) ? null : c.Password,
            PrivateKey = c.VaultEntry?.PrivateKey,
            PrivateKeyPassphrase = c.VaultEntry?.PrivateKeyPassphrase,
        };
    }

    private static string Describe(Exception ex) => ex switch
    {
        SshAuthenticationException => "Anmeldung abgelehnt (Benutzername/Passwort/Schlüssel prüfen).",
        HostKeyRejectedException h => h.Message,
        System.Net.Sockets.SocketException s => $"Netzwerkfehler ({s.SocketErrorCode}).",
        SshOperationTimeoutException or TimeoutException => "Zeitüberschreitung.",
        _ => ex.Message,
    };

    /// <summary>Tastatureingabe aus dem Terminal-Control. Bei getrennter Session: Enter verbindet neu.</summary>
    public void OnUserInput(string data)
    {
        if (!IsConnected)
        {
            if (data == "\r" && _tab.State is SessionState.Disconnected or SessionState.Failed) _ = ConnectAsync();
            return;
        }
        _routeInput(this, data); // Multi-Execution-Entscheidung trifft das MainViewModel
    }

    /// <summary>Direkt an diese Session senden (auch von Multi-Exec und Makros genutzt).</summary>
    public void Send(string data) => _session?.SendInput(data);

    public void Resize(uint columns, uint rows)
    {
        _columns = columns;
        _rows = rows;
        _session?.Resize(columns, rows);
    }

    public bool HighlightingEnabled
    {
        get => _session?.HighlightingEnabled ?? _settings.TerminalHighlighting;
        set { if (_session is not null) _session.HighlightingEnabled = value; }
    }

    /// <summary>Statuszeile im Terminal (grau), z. B. „Verbinde …“.</summary>
    private void WriteStatus(string text) => Publish($"\r\n\u001b[90m{text}\u001b[0m\r\n");

    private void SetState(SessionState state) => OnUi(() =>
    {
        _tab.State = state;
        OnPropertyChanged(nameof(IsConnected));
    });

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }

    private T OnUiAndWait<T>(Func<T> func)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) return func();
        T result = default!;
        _ui.Send(_ => result = func(), null);
        return result;
    }

    private async Task DisposeSessionAsync()
    {
        if (Sftp is not null) await Sftp.DisposeAsync();
        Sftp = null;
        if (_session is not null) await _session.DisposeAsync();
        _session = null;
    }

    public async ValueTask DisposeAsync() => await DisposeSessionAsync();
}

/// <summary>SFTP-Browser in der Seitenleiste (folgt optional dem Terminal-Verzeichnis).</summary>
public sealed partial class SftpBrowserViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SshTerminalSession _terminal;
    private readonly IDialogService _dialogs;
    private readonly SynchronizationContext? _ui;
    private SftpSession? _sftp;
    private CancellationTokenSource? _navigation;

    internal SftpBrowserViewModel(SshTerminalSession terminal, IDialogService dialogs, SynchronizationContext? ui)
    {
        _terminal = terminal;
        _dialogs = dialogs;
        _ui = ui;
    }

    public ObservableCollection<RemoteEntry> Entries { get; } = [];

    [ObservableProperty] private string _path = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _followTerminal = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _transferPercent;
    [ObservableProperty] private bool _isTransferring;

    public async Task ConnectAsync()
    {
        try
        {
            IsBusy = true;
            _sftp = _terminal.CreateSftp();
            await _sftp.ConnectAsync();
            await NavigateAsync(_terminal.WorkingDirectory ?? _sftp.HomeDirectory ?? "/");
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            Status = $"SFTP nicht verfügbar: {ex.Message}";
            _sftp = null;
        }
        finally { IsBusy = false; }
    }

    internal void OnTerminalDirectoryChanged(string directory)
    {
        if (FollowTerminal && _sftp is not null && directory != Path) _ = NavigateAsync(directory);
    }

    [RelayCommand]
    public async Task NavigateAsync(string? path)
    {
        if (_sftp is null || string.IsNullOrWhiteSpace(path)) return;
        _navigation?.Cancel();
        var cts = _navigation = new CancellationTokenSource();
        try
        {
            IsBusy = true;
            var entries = await _sftp.ListAsync(path, cts.Token);
            if (cts.IsCancellationRequested) return;
            Post(() =>
            {
                Path = path;
                Entries.Clear();
                foreach (var e in entries) Entries.Add(e);
                Status = $"{entries.Count} Einträge";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is SshException or InvalidOperationException)
        {
            Post(() => Status = ex is SftpPermissionDeniedException ? $"Kein Zugriff auf {path}" : ex.Message);
        }
        finally { Post(() => IsBusy = false); }
    }

    [RelayCommand] private Task Up() => NavigateAsync(SftpSession.Parent(Path));
    [RelayCommand] private Task Refresh() => NavigateAsync(Path);

    [RelayCommand]
    private Task Open(RemoteEntry? entry) => entry is { IsDirectory: true } ? NavigateAsync(entry.FullPath) : DownloadToAsync([entry!], DownloadsFolder);

    private static string DownloadsFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    [RelayCommand]
    private Task Download(IList<object>? selection)
    {
        var entries = selection?.OfType<RemoteEntry>().Where(e => !e.IsDirectory).ToList() ?? [];
        if (entries.Count == 0) return Task.CompletedTask;
        var folder = _dialogs.PickFolder("Zielordner für den Download");
        return folder is null ? Task.CompletedTask : DownloadToAsync(entries, folder);
    }

    private async Task DownloadToAsync(IReadOnlyList<RemoteEntry?> entries, string folder)
    {
        if (_sftp is null) return;
        Directory.CreateDirectory(folder);
        await TransferAsync(entries.Count, async (i, progress) => await _sftp.DownloadAsync(entries[i]!, folder, progress));
        Status = $"{entries.Count} Datei(en) heruntergeladen nach {folder}";
    }

    /// <summary>Upload, z. B. per Drag &amp; Drop aus dem Explorer.</summary>
    public async Task UploadAsync(IReadOnlyList<string> localFiles)
    {
        if (_sftp is null || localFiles.Count == 0) return;
        var files = localFiles.Where(File.Exists).ToList();
        if (files.Count < localFiles.Count) Status = "Ordner-Upload wird noch nicht unterstützt – nur Dateien.";
        await TransferAsync(files.Count, async (i, progress) => await _sftp.UploadAsync(files[i], Path, progress));
        await NavigateAsync(Path);
    }

    private async Task TransferAsync(int count, Func<int, IProgress<TransferProgress>, Task> transfer)
    {
        IsTransferring = true;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var index = i;
                var progress = new Progress<TransferProgress>(p =>
                {
                    TransferPercent = p.Total > 0 ? (index + (double)p.Transferred / p.Total) / count * 100 : 0;
                    Status = $"{p.Name}: {p.Transferred / 1024:N0} / {p.Total / 1024:N0} KB";
                });
                await transfer(i, progress);
            }
        }
        catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Übertragung fehlgeschlagen", ex.Message);
        }
        finally
        {
            IsTransferring = false;
            TransferPercent = 0;
        }
    }

    [RelayCommand]
    private async Task NewFolder()
    {
        if (_sftp is null || _dialogs.Prompt("Neuer Ordner", "Name:") is not { Length: > 0 } name) return;
        await Run(() => _sftp.CreateDirectoryAsync(SftpSession.Combine(Path, name)));
    }

    [RelayCommand]
    private async Task Rename(RemoteEntry? entry)
    {
        if (_sftp is null || entry is null || _dialogs.Prompt("Umbenennen", "Neuer Name:", entry.Name) is not { Length: > 0 } name || name == entry.Name) return;
        await Run(() => _sftp.RenameAsync(entry.FullPath, SftpSession.Combine(SftpSession.Parent(entry.FullPath), name)));
    }

    [RelayCommand]
    private async Task Delete(IList<object>? selection)
    {
        var entries = selection?.OfType<RemoteEntry>().ToList() ?? [];
        if (_sftp is null || entries.Count == 0) return;
        var what = entries.Count == 1 ? $"„{entries[0].Name}“" : $"{entries.Count} Einträge";
        if (!_dialogs.Confirm("Löschen", $"{what} auf dem Server löschen? (Ordner nur, wenn leer)")) return;
        foreach (var e in entries) await Run(() => _sftp.DeleteAsync(e), refresh: false);
        await NavigateAsync(Path);
    }

    private async Task Run(Func<Task> action, bool refresh = true)
    {
        try
        {
            await action();
            if (refresh) await NavigateAsync(Path);
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException)
        {
            _dialogs.ShowError("SFTP", ex.Message);
        }
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }

    public async ValueTask DisposeAsync()
    {
        _navigation?.Cancel();
        if (_sftp is not null) await _sftp.DisposeAsync();
        _sftp = null;
    }
}
