using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace HostDeck.Protocols.Vnc;

public sealed class VncAuthenticationException(string message) : Exception(message);

public readonly record struct DirtyRect(int X, int Y, int Width, int Height);

/// <summary>
/// VNC-Client nach RFC 6143 (RFB 3.3/3.7/3.8). Sicherheit: None, VNC-Auth (DES).
/// Kodierungen: ZRLE, Hextile, CopyRect, Raw, DesktopSize. Framebuffer: 32 bpp BGRA (für WPF Bgr32).
/// Hinweis: Klassisches VNC ist unverschlüsselt – über unsichere Netze per SSH-Tunnel/VPN nutzen.
/// </summary>
public sealed class RfbClient : IAsyncDisposable
{
    private const int EncRaw = 0, EncCopyRect = 1, EncHextile = 5, EncZrle = 16, EncDesktopSize = -223;

    private TcpClient? _tcp;
    private Stream? _stream;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _fbLock = new();
    private ZlibChunkStream? _zrleInput;
    private ZLibStream? _zrle;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string DesktopName { get; private set; } = "";
    public string ServerVersion { get; private set; } = "";
    public string SecurityType { get; private set; } = "";
    public bool ViewOnly { get; init; }

    /// <summary>Nur für Tests: Kodierungen in dieser Reihenfolge anfordern.</summary>
    internal int[]? PreferredEncodings { get; init; }

    /// <summary>BGRA, Zeilenlänge Width*4. Zugriff nur unter <see cref="LockFramebuffer"/>.</summary>
    public byte[] Framebuffer { get; private set; } = [];

    public event Action<DirtyRect>? FramebufferUpdated;
    public event Action<int, int>? DesktopResized;
    public event Action<string>? ClipboardReceived;
    public event Action<string?>? Closed;

    public bool IsConnected => _tcp?.Connected == true;

    /// <summary>Gibt exklusiven Zugriff auf den Framebuffer (z. B. zum Kopieren in eine Bitmap).</summary>
    public void LockFramebuffer(Action<byte[], int, int> access)
    {
        lock (_fbLock) access(Framebuffer, Width, Height);
    }

    // ---- Verbindungsaufbau ---------------------------------------------------------------------------

    /// <param name="passwordProvider">Wird nur aufgerufen, wenn der Server ein Passwort verlangt. null = Abbruch.</param>
    public async Task ConnectAsync(string host, int port, Func<string?> passwordProvider, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            await tcp.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
            _tcp = tcp;
            _stream = new BufferedStream(tcp.GetStream(), 64 * 1024);
            await HandshakeAsync(passwordProvider, connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"Keine Antwort von {host}:{port}.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        await RequestUpdateAsync(incremental: false).ConfigureAwait(false);
    }

    private async Task HandshakeAsync(Func<string?> passwordProvider, CancellationToken ct)
    {
        var versionBytes = await ReadExactAsync(12, ct).ConfigureAwait(false);
        ServerVersion = Encoding.ASCII.GetString(versionBytes).Trim();
        if (!ServerVersion.StartsWith("RFB ", StringComparison.Ordinal)) throw new IOException("Kein VNC-Server (ungültige Protokollkennung).");
        var minor = int.Parse(ServerVersion.Split('.')[1]);
        var useMinor = minor >= 8 ? 8 : minor >= 7 ? 7 : 3;
        await WriteAsync(Encoding.ASCII.GetBytes($"RFB 003.00{useMinor}\n"), ct).ConfigureAwait(false);

        int securityType;
        if (useMinor == 3)
        {
            securityType = (int)await ReadU32Async(ct).ConfigureAwait(false);
            if (securityType == 0) throw new IOException(await ReadReasonAsync(ct).ConfigureAwait(false));
        }
        else
        {
            var count = (await ReadExactAsync(1, ct).ConfigureAwait(false))[0];
            if (count == 0) throw new IOException(await ReadReasonAsync(ct).ConfigureAwait(false));
            var types = await ReadExactAsync(count, ct).ConfigureAwait(false);
            securityType = types.Contains((byte)1) ? 1 : types.Contains((byte)2) ? 2
                : throw new IOException($"Kein unterstütztes Sicherheitsverfahren angeboten ({string.Join(",", types)}). Unterstützt: None, VNC-Passwort.");
            await WriteAsync([(byte)securityType], ct).ConfigureAwait(false);
        }

        SecurityType = securityType == 1 ? "Keine" : "VNC-Passwort";
        if (securityType == 2)
        {
            var challenge = await ReadExactAsync(16, ct).ConfigureAwait(false);
            var password = passwordProvider() ?? throw new VncAuthenticationException("Passworteingabe abgebrochen.");
            await WriteAsync(EncryptChallenge(challenge, password), ct).ConfigureAwait(false);
        }

        if (securityType == 2 || useMinor == 8)
        {
            var result = await ReadU32Async(ct).ConfigureAwait(false);
            if (result != 0)
            {
                var reason = useMinor == 8 ? await ReadReasonAsync(ct).ConfigureAwait(false) : "Anmeldung fehlgeschlagen.";
                throw new VncAuthenticationException(string.IsNullOrWhiteSpace(reason) ? "Falsches VNC-Passwort." : reason);
            }
        }

        await WriteAsync([1], ct).ConfigureAwait(false); // ClientInit: shared
        var init = await ReadExactAsync(24, ct).ConfigureAwait(false);
        var width = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(0));
        var height = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2));
        var nameLength = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
        DesktopName = Encoding.UTF8.GetString(await ReadExactAsync((int)Math.Min(nameLength, 4096), ct).ConfigureAwait(false));
        Resize(width, height);

        // Eigenes Pixelformat: 32 bpp, Tiefe 24, Little Endian, True Color, R<<16 G<<8 B ⇒ Speicher BGRA
        var pf = new byte[20];
        pf[0] = 0; // SetPixelFormat
        pf[4] = 32; pf[5] = 24; pf[6] = 0; pf[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(pf.AsSpan(8), 255);
        BinaryPrimitives.WriteUInt16BigEndian(pf.AsSpan(10), 255);
        BinaryPrimitives.WriteUInt16BigEndian(pf.AsSpan(12), 255);
        pf[14] = 16; pf[15] = 8; pf[16] = 0;
        await WriteAsync(pf, ct).ConfigureAwait(false);

        int[] encodings = PreferredEncodings ?? [EncZrle, EncHextile, EncCopyRect, EncRaw, EncDesktopSize];
        var enc = new byte[4 + encodings.Length * 4];
        enc[0] = 2; // SetEncodings
        BinaryPrimitives.WriteUInt16BigEndian(enc.AsSpan(2), (ushort)encodings.Length);
        for (var i = 0; i < encodings.Length; i++) BinaryPrimitives.WriteInt32BigEndian(enc.AsSpan(4 + i * 4), encodings[i]);
        await WriteAsync(enc, ct).ConfigureAwait(false);
    }

    /// <summary>VNC-Auth: DES-ECB mit dem Passwort (max. 8 Zeichen) als Schlüssel, Bits je Byte gespiegelt.</summary>
    internal static byte[] EncryptChallenge(byte[] challenge, string password)
    {
        var key = new byte[8];
        var pw = Encoding.Latin1.GetBytes(password);
        for (var i = 0; i < 8 && i < pw.Length; i++) key[i] = ReverseBits(pw[i]);
        using var des = DES.Create();
        des.Key = key;
        return des.EncryptEcb(challenge, PaddingMode.None);
    }

    private static byte ReverseBits(byte b)
    {
        byte r = 0;
        for (var i = 0; i < 8; i++) { r = (byte)((r << 1) | (b & 1)); b >>= 1; }
        return r;
    }

    private void Resize(int width, int height)
    {
        lock (_fbLock)
        {
            Width = width;
            Height = height;
            Framebuffer = new byte[width * height * 4];
        }
    }

    // ---- Empfang ------------------------------------------------------------------------------------

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        string? reason = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var type = (await ReadExactAsync(1, ct).ConfigureAwait(false))[0];
                switch (type)
                {
                    case 0: await ReadFramebufferUpdateAsync(ct).ConfigureAwait(false); await RequestUpdateAsync(incremental: true).ConfigureAwait(false); break;
                    case 1: // SetColourMapEntries (nicht genutzt bei True Color) – überspringen
                        var hdr = await ReadExactAsync(5, ct).ConfigureAwait(false);
                        await ReadExactAsync(BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(3)) * 6, ct).ConfigureAwait(false);
                        break;
                    case 2: break; // Bell
                    case 3:
                        var cut = await ReadExactAsync(7, ct).ConfigureAwait(false);
                        var len = BinaryPrimitives.ReadUInt32BigEndian(cut.AsSpan(3));
                        var text = Encoding.Latin1.GetString(await ReadExactAsync((int)Math.Min(len, 10_000_000), ct).ConfigureAwait(false));
                        ClipboardReceived?.Invoke(text);
                        break;
                    default: throw new IOException($"Unbekannte Servernachricht {type}.");
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException) { reason = ex.Message; }
        Closed?.Invoke(reason);
    }

    private async Task ReadFramebufferUpdateAsync(CancellationToken ct)
    {
        var hdr = await ReadExactAsync(3, ct).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(1));
        for (var r = 0; r < count; r++)
        {
            var rh = await ReadExactAsync(12, ct).ConfigureAwait(false);
            int x = BinaryPrimitives.ReadUInt16BigEndian(rh.AsSpan(0)), y = BinaryPrimitives.ReadUInt16BigEndian(rh.AsSpan(2));
            int w = BinaryPrimitives.ReadUInt16BigEndian(rh.AsSpan(4)), h = BinaryPrimitives.ReadUInt16BigEndian(rh.AsSpan(6));
            var encoding = BinaryPrimitives.ReadInt32BigEndian(rh.AsSpan(8));

            if (encoding == EncDesktopSize)
            {
                Resize(w, h);
                DesktopResized?.Invoke(w, h);
                continue;
            }
            if (x + w > Width || y + h > Height) throw new InvalidDataException("Rechteck außerhalb des Framebuffers.");

            switch (encoding)
            {
                case EncRaw:
                    var raw = await ReadExactAsync(w * h * 4, ct).ConfigureAwait(false);
                    lock (_fbLock) Blit(raw, 0, w * 4, x, y, w, h);
                    break;
                case EncCopyRect:
                    var src = await ReadExactAsync(4, ct).ConfigureAwait(false);
                    CopyRect(BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(0)), BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(2)), x, y, w, h);
                    break;
                case EncHextile:
                    await DecodeHextileAsync(x, y, w, h, ct).ConfigureAwait(false);
                    break;
                case EncZrle:
                    var length = (int)await ReadU32Async(ct).ConfigureAwait(false);
                    var data = await ReadExactAsync(length, ct).ConfigureAwait(false);
                    DecodeZrle(data, x, y, w, h);
                    break;
                default:
                    throw new InvalidDataException($"Nicht angeforderte Kodierung {encoding}.");
            }
            FramebufferUpdated?.Invoke(new DirtyRect(x, y, w, h));
        }
    }

    private void Blit(byte[] src, int srcOffset, int srcStride, int x, int y, int w, int h)
    {
        var stride = Width * 4;
        for (var row = 0; row < h; row++)
        {
            var dst = (y + row) * stride + x * 4;
            Buffer.BlockCopy(src, srcOffset + row * srcStride, Framebuffer, dst, w * 4);
            for (var a = dst + 3; a < dst + w * 4; a += 4) Framebuffer[a] = 0xFF; // Füllbyte des Servers ⇒ deckend
        }
    }

    private void Fill(uint bgra, int x, int y, int w, int h)
    {
        var stride = Width * 4;
        Span<byte> px = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(px, bgra | 0xFF000000);
        for (var row = 0; row < h; row++)
        {
            var span = Framebuffer.AsSpan((y + row) * stride + x * 4, w * 4);
            for (var c = 0; c < w; c++) px.CopyTo(span[(c * 4)..]);
        }
    }

    private void CopyRect(int sx, int sy, int x, int y, int w, int h)
    {
        lock (_fbLock)
        {
            var stride = Width * 4;
            var temp = new byte[w * h * 4]; // Überlappung sicher behandeln
            for (var row = 0; row < h; row++)
                Buffer.BlockCopy(Framebuffer, (sy + row) * stride + sx * 4, temp, row * w * 4, w * 4);
            Blit(temp, 0, w * 4, x, y, w, h);
        }
    }

    // ---- Hextile (RFC 6143 7.7.4) ------------------------------------------------------------------------

    private async Task DecodeHextileAsync(int rx, int ry, int rw, int rh, CancellationToken ct)
    {
        uint background = 0, foreground = 0;
        for (var ty = ry; ty < ry + rh; ty += 16)
        {
            var th = Math.Min(16, ry + rh - ty);
            for (var tx = rx; tx < rx + rw; tx += 16)
            {
                var tw = Math.Min(16, rx + rw - tx);
                var sub = (await ReadExactAsync(1, ct).ConfigureAwait(false))[0];
                if ((sub & 1) != 0)
                {
                    var raw = await ReadExactAsync(tw * th * 4, ct).ConfigureAwait(false);
                    lock (_fbLock) Blit(raw, 0, tw * 4, tx, ty, tw, th);
                    continue;
                }
                if ((sub & 2) != 0) background = await ReadPixelAsync(ct).ConfigureAwait(false);
                if ((sub & 4) != 0) foreground = await ReadPixelAsync(ct).ConfigureAwait(false);
                lock (_fbLock) Fill(background, tx, ty, tw, th);
                if ((sub & 8) == 0) continue;

                var n = (await ReadExactAsync(1, ct).ConfigureAwait(false))[0];
                var coloured = (sub & 16) != 0;
                var rects = await ReadExactAsync(n * (coloured ? 6 : 2), ct).ConfigureAwait(false);
                lock (_fbLock)
                {
                    for (int i = 0, p = 0; i < n; i++)
                    {
                        var color = foreground;
                        if (coloured) { color = BinaryPrimitives.ReadUInt32LittleEndian(rects.AsSpan(p)); p += 4; }
                        int xy = rects[p++], wh = rects[p++];
                        int sx = xy >> 4, sy = xy & 15, sw = (wh >> 4) + 1, sh = (wh & 15) + 1;
                        if (sx + sw <= tw && sy + sh <= th) Fill(color, tx + sx, ty + sy, sw, sh);
                    }
                }
            }
        }
    }

    private async Task<uint> ReadPixelAsync(CancellationToken ct) =>
        BinaryPrimitives.ReadUInt32LittleEndian(await ReadExactAsync(4, ct).ConfigureAwait(false));

    // ---- ZRLE (RFC 6143 7.7.6) ----------------------------------------------------------------------------

    private void DecodeZrle(byte[] compressed, int rx, int ry, int rw, int rh)
    {
        // Ein zlib-Strom über die gesamte Verbindung; der Server spült (Z_SYNC_FLUSH) je Rechteck.
        _zrleInput ??= new ZlibChunkStream();
        _zrle ??= new ZLibStream(_zrleInput, CompressionMode.Decompress);
        _zrleInput.Append(compressed);
        var z = _zrle;

        Span<uint> palette = stackalloc uint[128];
        var tile = new uint[64 * 64];
        for (var ty = ry; ty < ry + rh; ty += 64)
        {
            var th = Math.Min(64, ry + rh - ty);
            for (var tx = rx; tx < rx + rw; tx += 64)
            {
                var tw = Math.Min(64, rx + rw - tx);
                var count = tw * th;
                var sub = ReadByte(z);
                if (sub == 0)
                {
                    for (var i = 0; i < count; i++) tile[i] = ReadCPixel(z);
                }
                else if (sub == 1)
                {
                    tile.AsSpan(0, count).Fill(ReadCPixel(z));
                }
                else if (sub <= 16)
                {
                    for (var i = 0; i < sub; i++) palette[i] = ReadCPixel(z);
                    var bits = sub == 2 ? 1 : sub <= 4 ? 2 : 4;
                    for (var row = 0; row < th; row++)
                    {
                        int value = 0, available = 0;
                        for (var col = 0; col < tw; col++)
                        {
                            if (available < bits) { value = (value << 8) | ReadByte(z); available += 8; }
                            available -= bits;
                            tile[row * tw + col] = palette[(value >> available) & ((1 << bits) - 1)];
                        }
                    }
                }
                else if (sub == 128)
                {
                    for (var i = 0; i < count;)
                    {
                        var color = ReadCPixel(z);
                        var run = ReadRunLength(z);
                        tile.AsSpan(i, Math.Min(run, count - i)).Fill(color);
                        i += run;
                    }
                }
                else if (sub >= 130)
                {
                    var size = sub - 128;
                    for (var i = 0; i < size; i++) palette[i] = ReadCPixel(z);
                    for (var i = 0; i < count;)
                    {
                        var index = ReadByte(z);
                        var run = (index & 128) != 0 ? ReadRunLength(z) : 1;
                        tile.AsSpan(i, Math.Min(run, count - i)).Fill(palette[index & 127]);
                        i += run;
                    }
                }
                else
                {
                    throw new InvalidDataException($"Ungültige ZRLE-Unterkodierung {sub}.");
                }

                lock (_fbLock)
                {
                    var stride = Width * 4;
                    for (var row = 0; row < th; row++)
                    {
                        var dst = Framebuffer.AsSpan((ty + row) * stride + tx * 4, tw * 4);
                        for (var col = 0; col < tw; col++)
                            BinaryPrimitives.WriteUInt32LittleEndian(dst[(col * 4)..], tile[row * tw + col] | 0xFF000000);
                    }
                }
            }
        }
    }

    private static int ReadByte(Stream s)
    {
        var b = s.ReadByte();
        return b >= 0 ? b : throw new InvalidDataException("ZRLE-Daten unvollständig.");
    }

    /// <summary>CPIXEL bei 32 bpp/Tiefe 24 Little Endian: 3 Bytes B, G, R.</summary>
    private static uint ReadCPixel(Stream s) => (uint)(ReadByte(s) | (ReadByte(s) << 8) | (ReadByte(s) << 16));

    private static int ReadRunLength(Stream s)
    {
        var length = 1;
        int b;
        do { b = ReadByte(s); length += b; } while (b == 255);
        return length;
    }

    // ---- Senden -------------------------------------------------------------------------------------

    public Task RequestUpdateAsync(bool incremental)
    {
        var msg = new byte[10];
        msg[0] = 3;
        msg[1] = (byte)(incremental ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6), (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8), (ushort)Height);
        return WriteAsync(msg, CancellationToken.None);
    }

    /// <summary>X11-Keysym senden (z. B. 0xff0d = Return).</summary>
    public Task SendKeyAsync(uint keysym, bool down)
    {
        if (ViewOnly) return Task.CompletedTask;
        var msg = new byte[8];
        msg[0] = 4;
        msg[1] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), keysym);
        return WriteAsync(msg, CancellationToken.None);
    }

    /// <param name="buttons">Bit 0 links, 1 Mitte, 2 rechts, 3/4 Rad hoch/runter.</param>
    public Task SendPointerAsync(int x, int y, byte buttons)
    {
        if (ViewOnly) return Task.CompletedTask;
        var msg = new byte[6];
        msg[0] = 5;
        msg[1] = buttons;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)Math.Clamp(x, 0, Math.Max(Width - 1, 0)));
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4), (ushort)Math.Clamp(y, 0, Math.Max(Height - 1, 0)));
        return WriteAsync(msg, CancellationToken.None);
    }

    public Task SendClipboardAsync(string text)
    {
        if (ViewOnly) return Task.CompletedTask;
        var data = Encoding.Latin1.GetBytes(text.Replace("\r\n", "\n"));
        var msg = new byte[8 + data.Length];
        msg[0] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), (uint)data.Length);
        data.CopyTo(msg, 8);
        return WriteAsync(msg, CancellationToken.None);
    }

    /// <summary>Strg+Alt+Entf an den Server.</summary>
    public async Task SendCtrlAltDelAsync()
    {
        foreach (var k in new uint[] { 0xffe3, 0xffe9, 0xffff }) await SendKeyAsync(k, true).ConfigureAwait(false);
        foreach (var k in new uint[] { 0xffff, 0xffe9, 0xffe3 }) await SendKeyAsync(k, false).ConfigureAwait(false);
    }

    // ---- I/O ---------------------------------------------------------------------------------------

    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        if (_stream is null) return;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally { _writeLock.Release(); }
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        if (count > 0) await _stream!.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    private async Task<uint> ReadU32Async(CancellationToken ct) => BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, ct).ConfigureAwait(false));

    private async Task<string> ReadReasonAsync(CancellationToken ct)
    {
        var len = await ReadU32Async(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(await ReadExactAsync((int)Math.Min(len, 4096), ct).ConfigureAwait(false));
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _stream?.Dispose();
        _tcp?.Dispose();
        _zrle?.Dispose();
        _stream = null;
        _tcp = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Eingabestrom für den verbindungsweiten zlib-Kontext: liefert nur bereits empfangene Daten.</summary>
    private sealed class ZlibChunkStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private byte[]? _current;
        private int _offset;

        public void Append(byte[] chunk) { if (chunk.Length > 0) _chunks.Enqueue(chunk); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (_chunks.Count == 0) return 0;
                _current = _chunks.Dequeue();
                _offset = 0;
            }
            var n = Math.Min(count, _current.Length - _offset);
            Buffer.BlockCopy(_current, _offset, buffer, offset, n);
            _offset += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
