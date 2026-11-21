using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace HostDeck.Protocols.Ssh;

public sealed record SshEndpoint(string Host, int Port, string Username)
{
    public string? Password { get; init; }
    /// <summary>Privater Schlüssel (OpenSSH/PEM/PuTTY-PPK-Inhalt, nicht Dateipfad).</summary>
    public string? PrivateKey { get; init; }
    public string? PrivateKeyPassphrase { get; init; }
}

public sealed record HostKeyInfo(string Host, int Port, string KeyType, string Fingerprint, string? PreviousFingerprint)
{
    public bool Changed => PreviousFingerprint is not null;
}

public enum HostKeyDecision { Reject, AcceptOnce, AcceptAndSave }

/// <summary>Bekannte Host-Keys (in HostDeck: Tabelle in der verschlüsselten Datenbank).</summary>
public interface IKnownHosts
{
    string? Find(string host, int port, string keyType);
    void Remember(string host, int port, string keyType, string fingerprint);
}

public sealed class HostKeyRejectedException(HostKeyInfo info)
    : Exception(info.Changed ? $"Host-Key von {info.Host} hat sich geändert – Verbindung abgelehnt." : $"Host-Key von {info.Host} nicht vertraut.")
{
    public HostKeyInfo Info { get; } = info;
}

public sealed class SshSessionOptions
{
    public string TerminalType { get; init; } = "xterm-256color";
    public uint Columns { get; init; } = 120;
    public uint Rows { get; init; } = 32;
    /// <summary>Prompt-Hook für OSC 7 setzen (nur bei erkannter bash/zsh).</summary>
    public bool TrackWorkingDirectory { get; init; } = true;
    public bool Highlighting { get; init; } = true;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Gemeinsame Verbindungs- und Host-Key-Logik für Terminal und SFTP.</summary>
internal static class SshConnectionFactory
{
    public static ConnectionInfo Create(SshEndpoint endpoint, TimeSpan timeout, Func<string, bool, string?>? interactivePrompt)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(endpoint.PrivateKey))
        {
            var keyFile = new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(endpoint.PrivateKey)), endpoint.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(endpoint.Username, keyFile));
        }
        if (!string.IsNullOrEmpty(endpoint.Password))
            methods.Add(new PasswordAuthenticationMethod(endpoint.Username, endpoint.Password));

        // Keyboard-Interactive: gespeichertes Passwort beantworten, sonst (z. B. OTP) den Benutzer fragen
        var kbd = new KeyboardInteractiveAuthenticationMethod(endpoint.Username);
        kbd.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                var isPasswordPrompt = prompt.Request.Contains("assword", StringComparison.OrdinalIgnoreCase);
                prompt.Response = isPasswordPrompt && !string.IsNullOrEmpty(endpoint.Password)
                    ? endpoint.Password
                    : interactivePrompt?.Invoke(prompt.Request, prompt.IsEchoed) ?? "";
            }
        };
        methods.Add(kbd);

        return new ConnectionInfo(endpoint.Host, endpoint.Port, endpoint.Username, methods.ToArray()) { Timeout = timeout };
    }

    /// <summary>
    /// Host-Key prüfen: bekannt ⇒ ok; sonst Callback (blockierend, Verbindungsaufbau läuft nicht im UI-Thread).
    /// </summary>
    public static void AttachHostKeyCheck(BaseClient client, SshEndpoint endpoint, IKnownHosts knownHosts,
        Func<HostKeyInfo, HostKeyDecision> decide, Action<HostKeyInfo> onRejected, Action<string> onAccepted)
    {
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = "SHA256:" + e.FingerPrintSHA256;
            var known = knownHosts.Find(endpoint.Host, endpoint.Port, e.HostKeyName);
            if (known == fingerprint)
            {
                e.CanTrust = true;
                onAccepted(fingerprint);
                return;
            }

            var info = new HostKeyInfo(endpoint.Host, endpoint.Port, e.HostKeyName, fingerprint, known);
            var decision = decide(info);
            e.CanTrust = decision != HostKeyDecision.Reject;
            if (decision == HostKeyDecision.AcceptAndSave)
                knownHosts.Remember(endpoint.Host, endpoint.Port, e.HostKeyName, fingerprint);
            if (e.CanTrust) onAccepted(fingerprint);
            else onRejected(info);
        };
    }
}

/// <summary>
/// Interaktive SSH-Terminal-Session (PTY). Ausgabe als UTF-8-String (für xterm.js), Eingabe als String.
/// </summary>
public sealed class SshTerminalSession : ITerminalBackend
{
    // Setzt einen Prompt-Hook, der das Verzeichnis per OSC 7 meldet (bash: PROMPT_COMMAND, zsh: precmd).
    // Führendes Leerzeichen ⇒ bei HISTCONTROL=ignorespace nicht in der History.
    internal const string CwdHook =
        " __hd_osc7(){ printf '\\033]7;file://%s%s\\033\\\\' \"${HOSTNAME:-$HOST}\" \"$PWD\"; }; " +
        "if [ -n \"$ZSH_VERSION\" ]; then precmd_functions+=(__hd_osc7); " +
        "else PROMPT_COMMAND=\"__hd_osc7${PROMPT_COMMAND:+;$PROMPT_COMMAND}\"; fi\n";

    private readonly SshEndpoint _endpoint;
    private readonly IKnownHosts _knownHosts;
    private readonly SshSessionOptions _options;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly OscWorkingDirectoryParser _cwdParser = new();
    private readonly TerminalHighlighter _highlighter = new();
    private readonly object _outputLock = new();

    private SshClient? _client;
    private ShellStream? _shell;
    private StringBuilder? _suppressed;   // Ausgabe während der Hook-Installation (wird verworfen)
    private Timer? _suppressTimeout;
    private HostKeyInfo? _rejectedHostKey;
    private string? _interactivePassword; // interaktiv eingegebenes Passwort, nur für die SFTP-Verbindung dieser Session

    public SshTerminalSession(SshEndpoint endpoint, IKnownHosts knownHosts, SshSessionOptions? options = null)
    {
        _endpoint = endpoint;
        _knownHosts = knownHosts;
        _options = options ?? new SshSessionOptions();
        _highlighter.Enabled = _options.Highlighting;
    }

    /// <summary>Terminal-Ausgabe (bereits UTF-8-dekodiert und ggf. eingefärbt). Achtung: Hintergrund-Thread.</summary>
    public event Action<string>? Output;
    public event Action<string>? WorkingDirectoryChanged;
    public event Action<string?>? Closed;

    public bool IsConnected => _client?.IsConnected == true && _shell is not null;
    public string? WorkingDirectory { get; private set; }
    /// <summary>Beim Verbinden akzeptierter Host-Key – SFTP nutzt ihn ohne erneute Nachfrage.</summary>
    public string? AcceptedFingerprint { get; private set; }
    public bool CwdTrackingActive { get; private set; }

    public bool HighlightingEnabled
    {
        get => _highlighter.Enabled;
        set => _highlighter.Enabled = value;
    }

    /// <param name="decideHostKey">Wird bei unbekanntem/geändertem Host-Key aufgerufen (Hintergrund-Thread, darf blockieren).</param>
    /// <param name="interactivePrompt">Für Keyboard-Interactive-Abfragen ohne gespeichertes Passwort (z. B. OTP).</param>
    public async Task ConnectAsync(Func<HostKeyInfo, HostKeyDecision> decideHostKey,
        Func<string, bool, string?>? interactivePrompt = null, CancellationToken cancellationToken = default)
    {
        Func<string, bool, string?>? prompt = interactivePrompt is null ? null : (request, echo) =>
        {
            var answer = interactivePrompt(request, echo);
            if (!echo && request.Contains("assword", StringComparison.OrdinalIgnoreCase)) _interactivePassword = answer;
            return answer;
        };
        var info = SshConnectionFactory.Create(_endpoint, _options.ConnectTimeout, prompt);
        var client = new SshClient(info) { KeepAliveInterval = _options.KeepAlive };
        SshConnectionFactory.AttachHostKeyCheck(client, _endpoint, _knownHosts, decideHostKey,
            rejected => _rejectedHostKey = rejected, fp => AcceptedFingerprint = fp);

        try
        {
            await Task.Run(() => client.ConnectAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (SshConnectionException) when (_rejectedHostKey is not null)
        {
            client.Dispose();
            throw new HostKeyRejectedException(_rejectedHostKey);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;

        var shellIsPosix = _options.TrackWorkingDirectory && await DetectPosixShellAsync(client).ConfigureAwait(false);

        var modes = new Dictionary<TerminalModes, uint> { [TerminalModes.ECHO] = 1, [TerminalModes.IUTF8] = 1 };
        _shell = client.CreateShellStream(_options.TerminalType, _options.Columns, _options.Rows, 0, 0, 64 * 1024, modes);
        _shell.DataReceived += (_, e) => OnData(e.Data);
        _shell.Closed += (_, _) => Closed?.Invoke(null);
        _shell.ErrorOccurred += (_, e) => Closed?.Invoke(e.Exception.Message);

        if (shellIsPosix) InstallCwdHook();
    }

    /// <summary>Exec-Kanal: nur bei bash/zsh wird später etwas in die Shell geschrieben (nie auf Switch-CLIs).</summary>
    private static async Task<bool> DetectPosixShellAsync(SshClient client)
    {
        try
        {
            using var cmd = client.CreateCommand("printf '%s' \"${BASH_VERSION:+bash}${ZSH_VERSION:+zsh}:$SHELL\"");
            cmd.CommandTimeout = TimeSpan.FromSeconds(3);
            var result = await Task.Run(cmd.Execute).ConfigureAwait(false);
            return cmd.ExitStatus == 0 && (result.EndsWith("/bash", StringComparison.Ordinal) || result.EndsWith("/zsh", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is SshException or SshOperationTimeoutException or InvalidOperationException)
        {
            return false;
        }
    }

    private void InstallCwdHook()
    {
        lock (_outputLock)
        {
            _suppressed = new StringBuilder();
            // Fallback: kommt kein OSC 7 (z. B. eigene Shell-Konfiguration), Ausgabe unverändert zeigen
            // Timer-Rückrufe dürfen nie werfen – eine Ausnahme dort beendet den gesamten Prozess
            _suppressTimeout = new Timer(_ =>
            {
                try { EndSuppression(found: false); }
                catch (Exception) { /* Session wird gerade beendet */ }
            }, null, TimeSpan.FromSeconds(4), Timeout.InfiniteTimeSpan);
        }
        _shell!.Write(CwdHook);
    }

    private void EndSuppression(bool found, string remainder = "")
    {
        string? flush;
        lock (_outputLock)
        {
            if (_suppressed is null) return;
            flush = found ? "\r\u001b[2K" + remainder : _suppressed.ToString();
            _suppressed = null;
            _suppressTimeout?.Dispose();
            _suppressTimeout = null;
            CwdTrackingActive = found;
        }
        Emit(flush);
    }

    private void OnData(byte[] data)
    {
        var chars = new char[_decoder.GetCharCount(data, 0, data.Length)];
        _decoder.GetChars(data, 0, data.Length, chars, 0);
        var text = new string(chars);

        foreach (var dir in _cwdParser.Feed(text))
        {
            WorkingDirectory = dir;
            WorkingDirectoryChanged?.Invoke(dir);
        }

        string? remainderAfterHook = null;
        lock (_outputLock)
        {
            if (_suppressed is not null)
            {
                _suppressed.Append(text);
                var all = _suppressed.ToString();
                var osc = all.IndexOf("\u001b]7;", StringComparison.Ordinal);
                var end = osc >= 0 ? IndexOfOscEnd(all, osc) : -1;
                if (end < 0) return; // weiter puffern
                remainderAfterHook = all[(end + 1)..];
            }
        }

        if (remainderAfterHook is not null)
            EndSuppression(found: true, remainderAfterHook); // Echo des Hook-Befehls + alten Prompt verwerfen
        else
            Emit(text);
    }

    private static int IndexOfOscEnd(string s, int start)
    {
        for (var j = start; j < s.Length; j++)
        {
            if (s[j] == '\a') return j;
            if (s[j] == '\u001b' && j + 1 < s.Length && s[j + 1] == '\\' && j > start) return j + 1;
        }
        return -1;
    }

    private void Emit(string text)
    {
        if (text.Length == 0) return;
        Output?.Invoke(_highlighter.Process(text));
    }

    public void SendInput(string text)
    {
        if (_shell is null || text.Length == 0) return;
        _shell.Write(text);
        _shell.Flush();
    }

    public void Resize(uint columns, uint rows)
    {
        if (_shell is null || columns == 0 || rows == 0) return;
        _shell.ChangeWindowSize(columns, rows, 0, 0);
    }

    /// <summary>Eigene SFTP-Verbindung mit demselben Endpunkt; der Host-Key muss dem akzeptierten entsprechen.</summary>
    public SftpSession CreateSftp() => new(
        _endpoint with { Password = _endpoint.Password ?? _interactivePassword },
        AcceptedFingerprint ?? throw new InvalidOperationException("Erst verbinden."),
        _options.ConnectTimeout);

    public async ValueTask DisposeAsync()
    {
        _interactivePassword = null;
        _suppressTimeout?.Dispose();
        var shell = _shell;
        var client = _client;
        _shell = null;
        _client = null;
        await Task.Run(() =>
        {
            shell?.Dispose();
            if (client?.IsConnected == true) client.Disconnect();
            client?.Dispose();
        }).ConfigureAwait(false);
    }
}
