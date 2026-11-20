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
                Heading.Text = "HostDeck entsperren";
                Info.Text = "Deine Verbindungen und Zugangsdaten sind verschlüsselt gespeichert.";
                OkButton.Content = "Entsperren";
                CurrentPanel.Visibility = ConfirmPanel.Visibility = Visibility.Collapsed;
                break;
            case MasterKeyDialogMode.Create:
                Heading.Text = "Master-Passwort festlegen";
                Info.Text = "Es schützt alle Verbindungen und Passwörter (AES-256). Ohne dieses Passwort sind die Daten nicht wiederherstellbar.";
                OkButton.Content = "Erstellen";
                CurrentPanel.Visibility = ForgotButton.Visibility = Visibility.Collapsed;
                CancelButton.Content = "Beenden";
                break;
            case MasterKeyDialogMode.Change:
                Heading.Text = "Master-Passwort ändern";
                Info.Text = "Die Datenbank wird nicht neu verschlüsselt – nur ihr Schlüssel neu verpackt.";
                NewLabel.Text = "Neues Master-Passwort";
                OkButton.Content = "Ändern";
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
            < MinLength => $"Zu kurz (mindestens {MinLength} Zeichen)",
            _ when pw.Length >= 16 || (pw.Length >= 12 && classes >= 3) => "Stark",
            _ when classes >= 3 => "Mittel – länger ist besser (Passphrase aus mehreren Wörtern)",
            _ => "Schwach – länger oder gemischte Zeichen verwenden",
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

        if (_mode == MasterKeyDialogMode.Change && CurrentPassword.Length == 0) { ShowError("Bitte das aktuelle Passwort eingeben."); return; }
        if (Password.Length < MinLength) { ShowError($"Das Passwort muss mindestens {MinLength} Zeichen lang sein."); return; }
        if (Password != ConfirmInput.Password) { ShowError("Die Passwörter stimmen nicht überein."); return; }
        DialogResult = true;
    }

    private void OnForgot(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(this,
            "Das Master-Passwort kann nicht wiederhergestellt werden – das ist Absicht.\n\n" +
            "Du kannst neu beginnen: Die bisherige verschlüsselte Datenbank wird NICHT gelöscht, sondern " +
            "umbenannt (Sicherung), und du legst ein neues Master-Passwort fest.\n\nNeu beginnen?",
            "Passwort vergessen", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        ResetRequested = true;
        DialogResult = false;
    }
}
