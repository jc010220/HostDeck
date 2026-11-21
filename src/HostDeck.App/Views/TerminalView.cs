using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HostDeck.Presentation.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HostDeck.App.Views;

/// <summary>
/// Terminal-Ansicht eines SSH-Tabs: xterm.js in WebView2. Ausgabe wird gebündelt (max. ~60 Nachrichten/s),
/// damit auch große Datenmengen (cat, find /) flüssig bleiben.
/// </summary>
public sealed class TerminalView : Border
{
    private const string Origin = "https://hostdeck.terminal/";
    private static Task<CoreWebView2Environment>? _environment;

    private readonly StringBuilder _pending = new();
    private readonly object _lock = new();
    private bool _flushScheduled;
    private WebView2? _webView;
    private IDisposable? _subscription;
    private TerminalSessionViewModel? _terminal;
    private SessionTabViewModel? _tab;

    public TerminalView()
    {
        DataContextChanged += (_, _) => _ = InitializeAsync();
        // Tab in anderes Fenster/anderen Bereich verschoben ⇒ diese Ansicht wird nicht mehr gebraucht.
        // (Die Session läuft weiter; die neue Ansicht bekommt den Verlauf per Subscribe nachgeliefert.)
        Unloaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!IsLoaded) ReleaseView(); });
        IsVisibleChanged += (_, _) => { if (IsVisible) Post(new { t = "focus" }); };
    }

    /// <summary>Wird von der App gesetzt: Tastenkürzel aus dem Terminal (Fokus liegt im WebView2-HWND).</summary>
    public static Action<string>? ShortcutHandler { get; set; }
    public static Func<string>? CurrentTheme { get; set; }
    public static Action<int>? FontSizeChanged { get; set; }
    public static int FontSize { get; set; } = 14;

    private static event Action<string>? ThemeChanged;
    public static void BroadcastTheme(string theme) => ThemeChanged?.Invoke(theme);

    private static Task<CoreWebView2Environment> Environment => _environment ??= CoreWebView2Environment.CreateAsync(
        null, Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "HostDeck", "WebView2"));

    private async Task InitializeAsync()
    {
        if (_webView is not null || DataContext is not SessionTabViewModel { Terminal: { } terminal } tab) return;
        _terminal = terminal;
        _tab = tab;
        _tab.Closed += OnTabClosed;
        ThemeChanged += ApplyTheme;

        _webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1e, 0x1e, 0x1e) };
        Child = _webView;
        try
        {
            await _webView.EnsureCoreWebView2Async(await Environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            Child = new TextBlock
            {
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
                Text = "The terminal requires the Microsoft Edge WebView2 Runtime (normally preinstalled on Windows 10/11).\n\n" + ex.Message,
            };
            return;
        }

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; // kein Strg+R/F5 = Neuladen, Strg+P = Drucken …
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached;
        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        // Das Terminal darf seine eigene Seite nie verlassen (Links öffnet der Standardbrowser)
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin, StringComparison.Ordinal)) e.Cancel = true; };
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.WebMessageReceived += OnMessage;
        core.Navigate(Origin + "terminal.html");
    }

    private static readonly System.Reflection.Assembly Assembly = typeof(TerminalView).Assembly;

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var name = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        var stream = Assembly.GetManifestResourceStream("terminal/" + name);
        if (stream is null)
        {
            e.Response = _webView!.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        var type = Path.GetExtension(name) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", _ => "application/octet-stream" };
        e.Response = _webView!.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {type}; charset=utf-8");
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin, StringComparison.Ordinal)) return;
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("t").GetString())
        {
            case "ready":
                Post(new { t = "theme", theme = CurrentTheme?.Invoke() ?? "dark" });
                Post(new { t = "font", size = FontSize });
                _terminal!.Resize((uint)m.GetProperty("cols").GetInt32(), (uint)m.GetProperty("rows").GetInt32());
                _subscription ??= _terminal.Subscribe(Enqueue); // liefert den bisherigen Verlauf nach
                break;
            case "i":
                _terminal?.OnUserInput(m.GetProperty("d").GetString() ?? "");
                break;
            case "r":
                _terminal?.Resize((uint)m.GetProperty("cols").GetInt32(), (uint)m.GetProperty("rows").GetInt32());
                break;
            case "copy":
                TrySetClipboard(m.GetProperty("d").GetString());
                break;
            case "paste-request":
                if (Clipboard.ContainsText() && ConfirmPaste(Clipboard.GetText()) is { } text) Post(new { t = "paste", d = text.Replace("\r\n", "\r") });
                break;
            case "link":
                var uri = m.GetProperty("uri").GetString();
                if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.ToString()) { UseShellExecute = true });
                break;
            case "shortcut":
                ShortcutHandler?.Invoke(m.GetProperty("key").GetString() ?? "");
                break;
            case "font":
                FontSize = m.GetProperty("size").GetInt32();
                FontSizeChanged?.Invoke(FontSize);
                break;
        }
    }

    /// <summary>
    /// Mehrzeiliges Einfügen führt jede Zeile sofort aus (auch versteckte Befehle aus kopierten Webseiten).
    /// Daher wie Windows Terminal nachfragen – bei Multi-Exec sogar für alle Server gleichzeitig.
    /// </summary>
    private string? ConfirmPaste(string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (lines.Length < 2) return text;
        var preview = string.Join("\n", lines.Take(8).Select(l => l.Length > 100 ? l[..100] + "…" : l)) + (lines.Length > 8 ? $"\n… ({lines.Length - 8} more lines)" : "");
        var owner = Window.GetWindow(this);
        var message = $"You are pasting {lines.Length} lines. Each line is executed immediately:\n\n{preview}\n\nPaste?";
        var result = owner is null
            ? MessageBox.Show(message, "Multi-line paste", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, message, "Multi-line paste", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes ? text : null;
    }

    private static void TrySetClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (COMException) { /* Zwischenablage kurz von anderer App gesperrt */ }
    }

    /// <summary>Ausgabe aus dem Hintergrund-Thread sammeln und gebündelt im UI-Thread senden.</summary>
    private void Enqueue(string text)
    {
        lock (_lock)
        {
            _pending.Append(text);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Flush);
    }

    private void Flush()
    {
        string data;
        lock (_lock)
        {
            data = _pending.ToString();
            _pending.Clear();
            _flushScheduled = false;
        }
        if (data.Length > 0) Post(new { t = "o", d = data });
    }

    public void ApplyTheme(string theme) => Post(new { t = "theme", theme });

    private void Post(object message)
    {
        if (_webView?.CoreWebView2 is { } core)
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void OnTabClosed(object? sender, EventArgs e) => ReleaseView();

    private void ReleaseView()
    {
        _subscription?.Dispose();
        _subscription = null;
        if (_tab is not null) _tab.Closed -= OnTabClosed;
        ThemeChanged -= ApplyTheme;
        _webView?.Dispose();
        _webView = null;
        Child = null;
    }
}
