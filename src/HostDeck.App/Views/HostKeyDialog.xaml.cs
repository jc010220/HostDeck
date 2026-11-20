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
            Intro.Text = "Der Server meldet einen anderen Schlüssel als bei der letzten Verbindung.";
            PreviousText.Text = info.PreviousFingerprint;
            SaveButton.Appearance = ControlAppearance.Danger;
            SaveButton.Content = "Neuen Key speichern";
        }
        else
        {
            Intro.Text = "Erste Verbindung zu diesem Server. Vergleiche den Fingerprint (z. B. mit „ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub“ auf dem Server).";
            PreviousLabel.Visibility = PreviousText.Visibility = Visibility.Collapsed;
            SaveButton.IsDefault = true;
        }
    }

    public HostKeyDecision Decision { get; private set; } = HostKeyDecision.Reject;

    private void OnOnce(object sender, RoutedEventArgs e) { Decision = HostKeyDecision.AcceptOnce; DialogResult = true; }
    private void OnSave(object sender, RoutedEventArgs e) { Decision = HostKeyDecision.AcceptAndSave; DialogResult = true; }
}
