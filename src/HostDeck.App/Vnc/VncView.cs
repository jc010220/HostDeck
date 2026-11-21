using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HostDeck.Presentation.ViewModels;
using HostDeck.Protocols.Vnc;

namespace HostDeck.App.Vnc;

/// <summary>VNC-Anzeige: WriteableBitmap (gebündelte Updates), Maus, Tastatur (X11-Keysyms), Zwischenablage.</summary>
public sealed class VncView : Grid
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform, Focusable = false };
    private readonly TextBlock _status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
    private readonly Border _statusPanel;
    private readonly object _dirtyLock = new();
    private readonly Wpf.Ui.Controls.Button _actionButton;
    private VncSessionViewModel? _vm;
    private RfbClient? _client;
    private WriteableBitmap? _bitmap;
    private Int32Rect? _dirty;
    private bool _renderScheduled;
    private byte _buttons;
    private string? _lastClipboard;
    private bool _everConnected;

    public VncView()
    {
        Focusable = true;
        Background = Brushes.Black;
        FocusVisualStyle = null;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        var reconnect = _actionButton = new Wpf.Ui.Controls.Button { Content = "Connect", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        reconnect.Click += (_, _) =>
        {
            if (_vm?.Tab.State == SessionState.Connecting) _vm.CancelConnect();
            else _ = _vm?.ConnectAsync();
        };
        _statusPanel = new Border
        {
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _status, reconnect } },
        };
        _statusPanel.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6), Opacity = 0.85 };
        bar.Children.Add(BarButton("Ctrl+Alt+Del", () => _ = _vm?.CtrlAltDelCommand.ExecuteAsync(null)));
        // Zwischenablage standardmäßig AUS: sonst ginge z. B. ein gerade kopiertes Passwort beim Klick in die Ansicht
        // an den Server, und der Server könnte die lokale Zwischenablage überschreiben. Je Richtung einzeln freigeben.
        bar.Children.Add(BarButton("Paste clipboard", SendClipboardOnce));
        bar.Children.Add(ToggleButton("Receive clipboard", on => _receiveClipboard = on,
            "Let the remote computer write into your Windows clipboard (off by default)"));
        bar.Children.Add(BarButton("1:1 / fit", () =>
        {
            _image.Stretch = _image.Stretch == Stretch.Uniform ? Stretch.None : Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(_image, _image.Stretch == Stretch.None ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        }));

        Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _image, Focusable = false });
        Children.Add(bar);
        Children.Add(_statusPanel);

        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_vm is not null && !_everConnected && IsLoaded && _vm.Client is null) { _everConnected = true; _ = _vm.ConnectAsync(); }
        });
        IsVisibleChanged += (_, _) => { if (IsVisible) Focus(); };
        // Ansicht verworfen (Tab in anderes Fenster verschoben): nur abmelden, die Verbindung lebt im ViewModel weiter
        Unloaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!IsLoaded) BindClient(null); });

        MouseDown += (_, e) => { Focus(); SetButton(e.ChangedButton, true); SendPointer(e); e.Handled = true; };
        MouseUp += (_, e) => { SetButton(e.ChangedButton, false); SendPointer(e); e.Handled = true; };
        MouseMove += (_, e) => SendPointer(e);
        MouseWheel += (_, e) =>
        {
            var bit = (byte)(e.Delta > 0 ? 8 : 16);
            if (MapPoint(e) is not { } p || _client is null) return;
            _ = _client.SendPointerAsync(p.X, p.Y, (byte)(_buttons | bit));
            _ = _client.SendPointerAsync(p.X, p.Y, _buttons);
        };
        PreviewKeyDown += (_, e) => OnKey(e, true);
        PreviewKeyUp += (_, e) => OnKey(e, false);
        PreviewTextInput += OnText;
    }

    private bool _receiveClipboard;

    private static System.Windows.Controls.Primitives.ToggleButton ToggleButton(string text, Action<bool> changed, string tip)
    {
        var t = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = text, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3), FontSize = 11, Focusable = false, ToolTip = tip,
        };
        t.Checked += (_, _) => changed(true);
        t.Unchecked += (_, _) => changed(false);
        return t;
    }

    /// <summary>Text der Windows-Zwischenablage einmalig an den Server senden (bewusste Aktion).</summary>
    private void SendClipboardOnce()
    {
        _lastClipboard = null; // bewusst senden, auch wenn derselbe Text schon einmal übertragen wurde
        SyncClipboardToServer();
        Focus();
    }

    private static Wpf.Ui.Controls.Button BarButton(string text, Action click)
    {
        var b = new Wpf.Ui.Controls.Button { Content = text, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3), FontSize = 11, Focusable = false };
        b.Click += (_, _) => click();
        return b;
    }

    private void Attach()
    {
        if (_vm is not null || DataContext is not SessionTabViewModel { Vnc: { } vm } tab) return;
        _vm = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VncSessionViewModel.Client)) BindClient(vm.Client);
            if (e.PropertyName == nameof(VncSessionViewModel.Message)) UpdateStatus();
        };
        tab.Closed += (_, _) => BindClient(null);
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SessionTabViewModel.State)) UpdateStatus(); };
        if (vm.Client is not null) { _everConnected = true; BindClient(vm.Client); } // Tab wurde verschoben: laufende Verbindung übernehmen
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var connected = _client is not null;
        _statusPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        _status.Text = _vm?.Message ?? "Not connected.";
        _actionButton.Content = _vm?.Tab.State == SessionState.Connecting ? "Cancel" : "Connect";
        _actionButton.Appearance = _vm?.Tab.State == SessionState.Connecting ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
    }

    private void BindClient(RfbClient? client)
    {
        if (_client is not null)
        {
            _client.FramebufferUpdated -= OnFramebufferUpdated;
            _client.DesktopResized -= OnDesktopResized;
            _client.ClipboardReceived -= OnClipboardReceived;
        }
        _client = client;
        if (client is not null)
        {
            client.FramebufferUpdated += OnFramebufferUpdated;
            client.DesktopResized += OnDesktopResized;
            client.ClipboardReceived += OnClipboardReceived;
            CreateBitmap();
            Focus();
        }
        UpdateStatus();
    }

    private void CreateBitmap()
    {
        if (_client is not { Width: > 0, Height: > 0 } c) return;
        _bitmap = new WriteableBitmap(c.Width, c.Height, 96, 96, PixelFormats.Bgr32, null);
        _image.Source = _bitmap;
        lock (_dirtyLock) _dirty = new Int32Rect(0, 0, c.Width, c.Height);
        ScheduleRender();
    }

    private void OnDesktopResized(int w, int h) => Dispatcher.BeginInvoke(CreateBitmap);

    private void OnFramebufferUpdated(DirtyRect r)
    {
        lock (_dirtyLock)
        {
            var rect = new Int32Rect(r.X, r.Y, r.Width, r.Height);
            _dirty = _dirty is { } d ? Union(d, rect) : rect;
        }
        ScheduleRender();
    }

    private static Int32Rect Union(Int32Rect a, Int32Rect b)
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Int32Rect(x, y, Math.Max(a.X + a.Width, b.X + b.Width) - x, Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
    }

    private void ScheduleRender()
    {
        lock (_dirtyLock)
        {
            if (_renderScheduled) return;
            _renderScheduled = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, Render);
    }

    private void Render()
    {
        Int32Rect? dirty;
        lock (_dirtyLock)
        {
            dirty = _dirty;
            _dirty = null;
            _renderScheduled = false;
        }
        if (dirty is not { } d || _bitmap is null || _client is null) return;
        _client.LockFramebuffer((fb, w, h) =>
        {
            if (w != _bitmap.PixelWidth || h != _bitmap.PixelHeight) return; // Größenwechsel folgt
            var rect = new Int32Rect(Math.Clamp(d.X, 0, w), Math.Clamp(d.Y, 0, h), 0, 0);
            rect.Width = Math.Clamp(d.Width, 0, w - rect.X);
            rect.Height = Math.Clamp(d.Height, 0, h - rect.Y);
            if (rect.Width > 0 && rect.Height > 0)
                _bitmap.WritePixels(rect, fb, w * 4, (rect.Y * w + rect.X) * 4);
        });
    }

    // ---- Eingabe --------------------------------------------------------------------------------------

    private (int X, int Y)? MapPoint(MouseEventArgs e)
    {
        if (_client is null || _image.ActualWidth <= 0 || _image.ActualHeight <= 0) return null;
        var p = e.GetPosition(_image);
        return ((int)(p.X * _client.Width / _image.ActualWidth), (int)(p.Y * _client.Height / _image.ActualHeight));
    }

    private void SetButton(MouseButton button, bool down)
    {
        var bit = button switch { MouseButton.Left => 1, MouseButton.Middle => 2, MouseButton.Right => 4, _ => 0 };
        _buttons = (byte)(down ? _buttons | bit : _buttons & ~bit);
    }

    private void SendPointer(MouseEventArgs e)
    {
        if (MapPoint(e) is { } p && _client is not null) _ = _client.SendPointerAsync(p.X, p.Y, _buttons);
    }

    private void OnKey(KeyEventArgs e, bool down)
    {
        if (_client is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var keysym = SpecialKeysym(key);
        var ctrlOrAlt = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;
        if (keysym == 0 && ctrlOrAlt && key is >= Key.A and <= Key.Z) keysym = (uint)('a' + (key - Key.A));
        if (keysym == 0 && ctrlOrAlt && key is >= Key.D0 and <= Key.D9) keysym = (uint)('0' + (key - Key.D0));
        if (keysym == 0) return; // druckbare Zeichen kommen über TextInput (Tastaturlayout!)
        _ = _client.SendKeyAsync(keysym, down);
        e.Handled = true;
    }

    private void OnText(object sender, TextCompositionEventArgs e)
    {
        if (_client is null) return;
        foreach (var ch in e.Text)
        {
            if (char.IsControl(ch)) continue;
            var keysym = ch <= 0xFF ? ch : 0x01000000u | ch;
            _ = _client.SendKeyAsync(keysym, true);
            _ = _client.SendKeyAsync(keysym, false);
        }
        e.Handled = true;
    }

    internal static uint SpecialKeysym(Key key) => key switch
    {
        Key.Back => 0xff08, Key.Tab => 0xff09, Key.Enter => 0xff0d, Key.Escape => 0xff1b, Key.Delete => 0xffff,
        Key.Home => 0xff50, Key.Left => 0xff51, Key.Up => 0xff52, Key.Right => 0xff53, Key.Down => 0xff54,
        Key.PageUp => 0xff55, Key.PageDown => 0xff56, Key.End => 0xff57, Key.Insert => 0xff63,
        Key.LeftShift => 0xffe1, Key.RightShift => 0xffe2, Key.LeftCtrl => 0xffe3, Key.RightCtrl => 0xffe4,
        Key.LeftAlt => 0xffe9, Key.RightAlt => 0xffea, Key.LWin => 0xffeb, Key.RWin => 0xffec, Key.CapsLock => 0xffe5,
        >= Key.F1 and <= Key.F12 => 0xffbe + (uint)(key - Key.F1),
        _ => 0,
    };

    // ---- Zwischenablage -----------------------------------------------------------------------------

    private void OnClipboardReceived(string text) => Dispatcher.BeginInvoke(() =>
    {
        if (!_receiveClipboard) return;
        _lastClipboard = text;
        try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { }
    });

    private void SyncClipboardToServer()
    {
        if (_client is null) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (text == _lastClipboard) return;
            _lastClipboard = text;
            _ = _client.SendClipboardAsync(text);
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}
