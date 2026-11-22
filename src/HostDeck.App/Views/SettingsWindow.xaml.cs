using System.Windows;
using HostDeck.Presentation.ViewModels;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(SettingsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 40;
    }

    private void OnBrowseTeamLibrary(object sender, RoutedEventArgs e)
    {
        var vm = (SettingsViewModel)DataContext;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose team library (shared confCons.xml)",
            Filter = "mRemoteNG (confCons.xml)|*.xml|All files|*.*",
        };
        try
        {
            var current = vm.TeamLibraryPath.Trim();
            if (current.Length > 0) dialog.InitialDirectory = System.IO.Directory.Exists(current) ? current : System.IO.Path.GetDirectoryName(current);
        }
        catch (ArgumentException) { }
        if (dialog.ShowDialog(this) == true) vm.TeamLibraryPath = dialog.FileName;
    }

    private void OnCheckForUpdates(object sender, RoutedEventArgs e) => ((SettingsViewModel)DataContext).CheckForUpdates?.Invoke();

    private void OnBrowseBackupFolder(object sender, RoutedEventArgs e)
    {
        var vm = (SettingsViewModel)DataContext;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for automatic backups" };
        var current = vm.BackupFolder.Trim().Length > 0 ? vm.BackupFolder.Trim() : vm.BackupFolderHint;
        if (System.IO.Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) == true) vm.BackupFolder = dialog.FolderName;
    }

    private void OnOpenBackupFolder(object sender, RoutedEventArgs e)
    {
        var vm = (SettingsViewModel)DataContext;
        var folder = vm.Backups?.Folder;
        if (folder is null) return;
        System.IO.Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
    }

    private void OnBackupNow(object sender, RoutedEventArgs e)
    {
        var vm = (SettingsViewModel)DataContext;
        if (vm.Backups is null) return;
        try
        {
            var file = vm.Backups.BackupNow();
            System.Windows.MessageBox.Show(this, $"Backup created:\n{file}\n\n{vm.Backups.Summary()}", "Backups");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(this, $"Backup failed: {ex.Message}", "Backups", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ((SettingsViewModel)DataContext).UpdateToken = TokenBox.Password ?? "";
        DialogResult = true;
    }
}
