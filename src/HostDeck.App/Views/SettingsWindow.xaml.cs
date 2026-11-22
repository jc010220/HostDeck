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

    // ---- Navigation ----

    private void OnNavChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not System.Windows.Controls.ListBoxItem { Tag: string target }) return;
        foreach (var section in new[] { SecAppearance, SecBehavior, SecTeam, SecBackups, SecUpdates })
            section.Visibility = section.Name == target ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Hamburger: Menü auf Symbole zusammenklappen bzw. wieder aufklappen.</summary>
    private void OnToggleNav(object sender, RoutedEventArgs e) =>
        NavColumn.Width = new GridLength(NavColumn.Width.Value > 100 ? 56 : 210);

    /// <summary>Direkt einen Bereich öffnen (z. B. aus dem Menü „Team backups“).</summary>
    public void ShowSection(string name)
    {
        foreach (var item in Nav.Items.OfType<System.Windows.Controls.ListBoxItem>())
            if (item.Tag as string == name) { Nav.SelectedItem = item; break; }
    }

    // ---- Team-Sicherungen ----

    private void OnTeamBackupNow(object sender, RoutedEventArgs e)
    {
        var vm = (SettingsViewModel)DataContext;
        if (vm.TeamFile is not { } file) return;
        try
        {
            HostDeck.Presentation.Team.TeamBackup.RunIfDue(file, DateTime.Now, force: true);
            vm.RefreshTeamBackupSummary();
            System.Windows.MessageBox.Show(this, $"Team library backed up.\n\n{vm.TeamBackupSummary}", "Team backups");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or HostDeck.Presentation.Team.TeamFileLockedException)
        {
            System.Windows.MessageBox.Show(this, $"Team backup failed: {ex.Message}", "Team backups", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnOpenTeamBackupFolder(object sender, RoutedEventArgs e)
    {
        var vm = (SettingsViewModel)DataContext;
        if (vm.TeamFile is not { } file) return;
        var folder = HostDeck.Presentation.Team.TeamBackup.FolderFor(file);
        System.IO.Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
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

    private void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        var vm = (SettingsViewModel)DataContext;
        if (vm.Backups is null || vm.RestoreBackup is null) return;
        var picker = new RestoreBackupWindow(vm.Backups.AllBackups()) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedFile is not { } file) return;
        DialogResult = false; // Einstellungen schließen, dann wiederherstellen (startet HostDeck neu)
        Dispatcher.BeginInvoke(() => vm.RestoreBackup(file));
    }

    private void OnBackupNow(object sender, RoutedEventArgs e)
    {
        var vm = (SettingsViewModel)DataContext;
        if (vm.Backups is null) return;
        try
        {
            var file = vm.Backups.BackupNow();
            System.Windows.MessageBox.Show(this, $"Backup created:\n{file}\n\n{vm.Backups.Summary()}", "Personal backups");
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
