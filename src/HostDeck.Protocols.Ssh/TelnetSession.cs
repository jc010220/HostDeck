using System.Net.Sockets;
using System.Text;

namespace HostDeck.Protocols.Ssh;

/// <summary>Gemeinsame Schnittstelle der Terminal-Protokolle (SSH, Telnet, Raw-TCP).</summary>
public interface ITerminalBackend : IAsyncDisposable
{
    event Action<string>? Output;
    event Action<string>? WorkingDirectoryChanged;
    event Action<string?>? Closed;
    bool IsConnected { get; }
    bool HighlightingEnabled { get; set; }
    void SendInput(string text);
    void Resize(uint columns, uint rows);
}

/// <summary>
/// Telnet (RFC 854) mit Optionsverhandlung für ECHO, SGA, TTYPE und NAWS – oder Raw-TCP ohne Protokoll.
/// Optionaler Auto-Login: beantwortet „login:/Username:“ und „Password:“ mit gespeicherten Zugangsdaten.
/// </summary>
public sealed class TelnetSession : ITerminalBackend
{
    private const byte Iac = 255, Dont = 254, Do = 253, Wont = 252, Will = 251, Sb = 250, Se = 240;
    private const byte OptBinary = 0, OptEcho = 1, OptSga = 3, OptTtype = 24, OptNaws = 31;
    private const byte TtypeIs = 0, TtypeSend = 1;

    private readonly string _host;
    private readonly int _port;
    private readonly bool _raw;
    private readonly string? _username;
    private readonly string? _password;
    private readonly TerminalHighlighter _highlighter = new();
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly HashSet<byte> _weWill = [];
    private readonly HashSet<byte> _theyWill = [];
    private readonly StringBuilder _loginTail = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private uint _columns = 120, _rows = 32;
    private bool _sentUser, _sentPassword;
    private DateTime _loginDeadline;

    public TelnetSession(string host, int port, bool raw = false, string? username = null, string? password = null)
    {
        _host = host;
        _port = port;
        _raw = raw;
        _username = string.IsNullOrEmpty(username) ? null : username;
        _password = string.IsNullOrEmpty(password) ? null : password;
    }

    public event Action<string>? Output;
    public event Action<string>? WorkingDirectoryChanged { add { } remove { } }
    public event Action<string?>? Closed;

    public bool IsConnected => _client?.Connected == true;
    public bool HighlightingEnabled { get => _highlighter.Enabled; set => _highlighter.Enabled = value; }

    public async Task ConnectAsync(uint columns, uint rows, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        _columns = columns;
        _rows = rows;
        var client = new TcpClient { NoDelay = true };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            await client.ConnectAsync(_host, _port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Keine Antwort von {_host}:{_port}.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _stream = client.GetStream();
        _cts = new CancellationTokenSource();
        _loginDeadline = DateTime.UtcNow.AddSeconds(30);
        _ = Task.Run(() => ReadLoopAsync(_cts.Token));

        if (!_raw)
        {
            // Wir bieten Fenstergröße und Terminaltyp an; der Server entscheidet.
            await SendCommandAsync(Will, OptNaws).ConfigureAwait(false);
            await SendCommandAsync(Will, OptTtype).ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        string? reason = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await _stream!.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break;
                var data = _raw ? buffer.AsSpan(0, n).ToArray() : await ProcessTelnetAsync(buffer, n).ConfigureAwait(false);
                if (data.Length == 0) continue;

                var chars = new char[_decoder.GetCharCount(data, 0, data.Length)];
                _decoder.GetChars(data, 0, data.Length, chars, 0);
                var text = new string(chars);
                Output?.Invoke(_highlighter.Process(text));
                await AutoLoginAsync(text).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { reason = ex.Message; }
        Closed?.Invoke(reason);
    }

    // ---- Telnet-Protokoll ------------------------------------------------------------------------------

    private enum ParseState { Data, Iac, Command, Sub, SubIac }
    private ParseState _state = ParseState.Data;
    private byte _command;
    private readonly List<byte> _sub = [];

    /// <summary>Filtert IAC-Sequenzen aus dem Datenstrom und beantwortet Verhandlungen.</summary>
    internal async Task<byte[]> ProcessTelnetAsync(byte[] buffer, int count)
    {
        var output = new List<byte>(count);
        var replies = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            var b = buffer[i];
            switch (_state)
            {
                case ParseState.Data:
                    if (b == Iac) _state = ParseState.Iac;
                    else output.Add(b);
                    break;
                case ParseState.Iac:
                    if (b == Iac) { output.Add(Iac); _state = ParseState.Data; }
                    else if (b is Do or Dont or Will or Wont) { _command = b; _state = ParseState.Command; }
                    else if (b == Sb) { _sub.Clear(); _state = ParseState.Sub; }
                    else _state = ParseState.Data; // NOP, GA, AYT … ignorieren
                    break;
                case ParseState.Command:
                    replies.AddRange(Negotiate(_command, b));
                    _state = ParseState.Data;
                    break;
                case ParseState.Sub:
                    if (b == Iac) _state = ParseState.SubIac;
                    else _sub.Add(b);
                    break;
                case ParseState.SubIac:
                    if (b == Se)
                    {
                        if (_sub is [OptTtype, TtypeSend, ..])
                            replies.Add([Iac, Sb, OptTtype, TtypeIs, .. "XTERM-256COLOR"u8, Iac, Se]);
                        _state = ParseState.Data;
                    }
                    else { _sub.Add(b); _state = ParseState.Sub; }
                    break;
            }
        }
        foreach (var reply in replies) await WriteAsync(reply).ConfigureAwait(false);
        return output.ToArray();
    }

    private IEnumerable<byte[]> Negotiate(byte command, byte option)
    {
        switch (command)
        {
            case Do when option is OptNaws or OptTtype or OptSga or OptBinary:
                if (_weWill.Add(option) && option != OptNaws && option != OptTtype) yield return [Iac, Will, option];
                if (option == OptNaws) yield return NawsPacket();
                break;
            case Do: // alles andere ablehnen
                yield return [Iac, Wont, option];
                break;
            case Dont:
                _weWill.Remove(option);
                break;
            case Will when option is OptEcho or OptSga or OptBinary:
                if (_theyWill.Add(option)) yield return [Iac, Do, option];
                break;
            case Will:
                yield return [Iac, Dont, option];
                break;
            case Wont:
                _theyWill.Remove(option);
                break;
        }
    }

    private byte[] NawsPacket()
    {
        var w = (ushort)Math.Min(_columns, ushort.MaxValue);
        var h = (ushort)Math.Min(_rows, ushort.MaxValue);
        var bytes = new List<byte> { Iac, Sb, OptNaws };
        foreach (var v in new[] { (byte)(w >> 8), (byte)w, (byte)(h >> 8), (byte)h })
        {
            bytes.Add(v);
            if (v == Iac) bytes.Add(Iac); // 255 verdoppeln
        }
        bytes.AddRange([Iac, Se]);
        return bytes.ToArray();
    }

    private Task SendCommandAsync(byte command, byte option)
    {
        if (command == Will) _weWill.Add(option);
        return WriteAsync([Iac, command, option]);
    }

    // ---- Auto-Login -----------------------------------------------------------------------------------

    private async Task AutoLoginAsync(string text)
    {
        if (_username is null && _password is null) return;
        if (DateTime.UtcNow > _loginDeadline || (_sentPassword && (_sentUser || _username is null))) return;

        _loginTail.Append(text);
        if (_loginTail.Length > 200) _loginTail.Remove(0, _loginTail.Length - 200);
        var tail = _loginTail.ToString().TrimEnd().ToLowerInvariant();

        if (!_sentUser && _username is not null && (tail.EndsWith("login:") || tail.EndsWith("username:") || tail.EndsWith("user name:")))
        {
            _sentUser = true;
            _loginTail.Clear();
            SendInput(_username + "\r");
        }
        else if (!_sentPassword && _password is not null && (tail.EndsWith("password:") || tail.EndsWith("passwort:")))
        {
            _sentPassword = true;
            _loginTail.Clear();
            SendInput(_password + "\r");
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ---- Senden ---------------------------------------------------------------------------------------

    public void SendInput(string text)
    {
        if (_stream is null || text.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!_raw)
        {
            var escaped = new List<byte>(bytes.Length + 4);
            for (var i = 0; i < bytes.Length; i++)
            {
                escaped.Add(bytes[i]);
                if (bytes[i] == Iac) escaped.Add(Iac);
                // NVT: ein alleinstehendes CR wird als CR NUL gesendet (außer im Binärmodus)
                else if (bytes[i] == '\r' && !_weWill.Contains(OptBinary) && (i + 1 >= bytes.Length || bytes[i + 1] != '\n')) escaped.Add(0);
            }
            bytes = escaped.ToArray();
        }
        _ = WriteAsync(bytes);
    }

    public void Resize(uint columns, uint rows)
    {
        _columns = columns;
        _rows = rows;
        if (!_raw && _weWill.Contains(OptNaws) && _stream is not null) _ = WriteAsync(NawsPacket());
    }

    private async Task WriteAsync(byte[] data)
    {
        if (_stream is null) return;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await _stream.WriteAsync(data).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally { _writeLock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return ValueTask.CompletedTask;
    }
}
