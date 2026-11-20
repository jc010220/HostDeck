using System.Windows;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

public partial class InputDialog : FluentWindow
{
    private readonly bool _secret;

    public InputDialog(string title, string message, string defaultValue, bool secret)
    {
        InitializeComponent();
        _secret = secret;
        Title = TitleBar.Title = title;
        Message.Text = message;
        ValueInput.Text = defaultValue;
        if (secret)
        {
            ValueInput.Visibility = Visibility.Collapsed;
            SecretInput.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) =>
        {
            if (secret) SecretInput.Focus();
            else { ValueInput.Focus(); ValueInput.SelectAll(); }
        };
    }

    public string Value => _secret ? SecretInput.Password : ValueInput.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
