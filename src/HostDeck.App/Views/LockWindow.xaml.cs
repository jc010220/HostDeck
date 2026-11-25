using System.Windows;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

/// <summary>Sperrbildschirm (automatische Sperre / „Lock now“). Schließen geht nur durch Entsperren oder Beenden.</summary>
public partial class LockWindow : FluentWindow
{
    private readonly Func<string, bool> _verify;
    private int _failures;

    public LockWindow(Func<string, bool> verify, string reason)
    {
        _verify = verify;
        InitializeComponent();
        Reason.Text = reason;
        Closing += (_, e) => { if (DialogResult is null) e.Cancel = true; }; // kein Schließen per Alt+F4
    }

    private async void OnUnlock(object sender, RoutedEventArgs e)
    {
        var password = PasswordInput.Password;
        PasswordInput.Clear();
        // Argon2id (64 MiB) nicht im UI-Thread: sonst stockt die ganze Oberfläche – und der Tastatur-Hook
        IsEnabled = false;
        var ok = password.Length > 0 && await Task.Run(() => _verify(password));
        IsEnabled = true;
        if (ok)
        {
            DialogResult = true;
            return;
        }
        PasswordInput.Focus();
        _failures++;
        ErrorText.Visibility = Visibility.Visible;
        // nach mehreren Fehlversuchen kurz bremsen (Argon2id bremst zusätzlich)
        if (_failures >= 3)
        {
            IsEnabled = false;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * _failures)));
            IsEnabled = true;
            PasswordInput.Focus();
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => DialogResult = false;
}
