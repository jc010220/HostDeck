using System.Windows;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

public partial class MasterPasswordDialog : FluentWindow
{
    public MasterPasswordDialog(string? sourceName, bool previousAttemptFailed, int attempt, int maxAttempts)
    {
        InitializeComponent();
        Message.Text = $"“{sourceName ?? "Connection file"}” is protected with a master password.";
        if (previousAttemptFailed)
        {
            ErrorText.Text = $"Wrong password – attempt {attempt} of {maxAttempts}.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    public string Password => PasswordInput.Password;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (PasswordInput.Password.Length > 0) DialogResult = true;
    }
}
