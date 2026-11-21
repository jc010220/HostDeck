using System.Windows;
using HostDeck.Protocols.Ssh;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

public partial class HostKeyDialog : FluentWindow
{
    public HostKeyDialog(HostKeyInfo info)
    {
        InitializeComponent();
        HostText.Text = $"{info.Host}:{info.Port}";
        TypeText.Text = info.KeyType;
        FingerprintText.Text = info.Fingerprint;
        if (info.Changed)
        {
            WarningBox.Visibility = Visibility.Visible;
            Intro.Text = "The server presents a different key than on the last connection.";
            PreviousText.Text = info.PreviousFingerprint;
            SaveButton.Appearance = ControlAppearance.Danger;
            SaveButton.Content = "Save new key";
        }
        else
        {
            Intro.Text = "First connection to this server. Compare the fingerprint (e.g. with “ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub” on the server).";
            PreviousLabel.Visibility = PreviousText.Visibility = Visibility.Collapsed;
            SaveButton.IsDefault = true;
        }
    }

    public HostKeyDecision Decision { get; private set; } = HostKeyDecision.Reject;

    private void OnOnce(object sender, RoutedEventArgs e) { Decision = HostKeyDecision.AcceptOnce; DialogResult = true; }
    private void OnSave(object sender, RoutedEventArgs e) { Decision = HostKeyDecision.AcceptAndSave; DialogResult = true; }
}
