using System.IO;
using System.Windows;
using HostDeck.Presentation.Services;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

/// <summary>Auswahl der wiederherzustellenden Sicherung (Liste aus dem Sicherungsordner oder beliebige Datei).</summary>
public partial class RestoreBackupWindow : FluentWindow
{
    public RestoreBackupWindow(IReadOnlyList<BackupInfo> backups)
    {
        InitializeComponent();
        BackupList.ItemsSource = backups;
        EmptyText.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (backups.Count > 0) BackupList.SelectedIndex = 0;
    }

    /// <summary>Gewählte Sicherung (*.hdk; die *.hdk.key liegt daneben).</summary>
    public string? SelectedFile { get; private set; }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        RestoreButton.IsEnabled = BackupList.SelectedItem is BackupInfo;

    private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OnRestore(sender, e);

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupInfo backup) return;
        SelectedFile = backup.Path;
        DialogResult = true;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose personal HostDeck backup", Filter = "HostDeck personal backup (*.hdk)|*.hdk" };
        if (dialog.ShowDialog(this) != true) return;
        if (!File.Exists(dialog.FileName + ".key"))
        {
            System.Windows.MessageBox.Show(this, $"The matching key file is missing:\n{dialog.FileName}.key\n\nBoth files belong together.", "Restore backup",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        SelectedFile = dialog.FileName;
        DialogResult = true;
    }
}
