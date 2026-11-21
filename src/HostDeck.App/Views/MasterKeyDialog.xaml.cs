using System.Windows;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

public enum MasterKeyDialogMode { Unlock, Create, Change }

/// <summary>Entsperren, Ersteinrichtung und Ändern des Master-Passworts.</summary>
public partial class MasterKeyDialog : FluentWindow
{
    private const int MinLength = 8;
    private readonly MasterKeyDialogMode _mode;

    public MasterKeyDialog(MasterKeyDialogMode mode, string? error = null, bool deviceUnlockSupported = true)
    {
        _mode = mode;
        InitializeComponent();

        switch (mode)
        {
            case MasterKeyDialogMode.Unlock:
                Heading.Text = "Unlock HostDeck";
                Info.Text = "Your connections and credentials are stored encrypted.";
                OkButton.Content = "Unlock";
                CurrentPanel.Visibility = ConfirmPanel.Visibility = Visibility.Collapsed;
                break;
            case MasterKeyDialogMode.Create:
                Heading.Text = "Set master password";
                Info.Text = "It protects all connections and passwords (AES-256). Without this password the data cannot be recovered.";
                OkButton.Content = "Create";
                CurrentPanel.Visibility = ForgotButton.Visibility = Visibility.Collapsed;
                CancelButton.Content = "Exit";
                break;
            case MasterKeyDialogMode.Change:
                Heading.Text = "Change master password";
                Info.Text = "The database is not re-encrypted – only its key is re-wrapped.";
                NewLabel.Text = "New master password";
                OkButton.Content = "Change";
                ForgotButton.Visibility = RememberBox.Visibility = Visibility.Collapsed;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ShowInTaskbar = false;
                break;
        }

        if (!deviceUnlockSupported) RememberBox.Visibility = Visibility.Collapsed;
        if (error is not null) ShowError(error);
        Loaded += (_, _) => (mode == MasterKeyDialogMode.Change ? CurrentInput : NewInput).Focus();
    }

    public string CurrentPassword => CurrentInput.Password;
    public string Password => NewInput.Password;
    public bool Remember => RememberBox.IsChecked == true;
    /// <summary>Benutzer hat „Passwort vergessen → neu beginnen“ bestätigt.</summary>
    public bool ResetRequested { get; private set; }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var pw = NewInput.Password;
        var classes = new[] { pw.Any(char.IsLower), pw.Any(char.IsUpper), pw.Any(char.IsDigit), pw.Any(c => !char.IsLetterOrDigit(c)) }.Count(x => x);
        StrengthText.Text = pw.Length switch
        {
            0 => "",
            < MinLength => $"Too short (at least {MinLength} characters)",
            _ when pw.Length >= 16 || (pw.Length >= 12 && classes >= 3) => "Strong",
            _ when classes >= 3 => "Medium – longer is better (passphrase of several words)",
            _ => "Weak – use a longer password or mixed characters",
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_mode == MasterKeyDialogMode.Unlock)
        {
            if (Password.Length == 0) return;
            DialogResult = true;
            return;
        }

        if (_mode == MasterKeyDialogMode.Change && CurrentPassword.Length == 0) { ShowError("Please enter the current password."); return; }
        if (Password.Length < MinLength) { ShowError($"The password must be at least {MinLength} characters long."); return; }
        if (Password != ConfirmInput.Password) { ShowError("The passwords do not match."); return; }
        DialogResult = true;
    }

    private void OnForgot(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(this,
            "The master password cannot be recovered – by design.\n\n" +
            "You can start over: the existing encrypted database is NOT deleted but " +
            "renamed (backup), and you set a new master password.\n\nStart over?",
            "Forgot password", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        ResetRequested = true;
        DialogResult = false;
    }
}
