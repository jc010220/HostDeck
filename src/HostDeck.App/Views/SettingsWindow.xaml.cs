using System.Windows;
using HostDeck.Presentation.ViewModels;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(SettingsViewModel vm)
    {
        DataContext = vm;
        vm.PickLibraryFile = PickLibraryFile;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        Nav.SelectedIndex = 0; // erst jetzt – alle Bereiche sind geladen
    }

    // ---- Navigation ----

    private void OnNavChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Während InitializeComponent existieren die Bereiche evtl. noch nicht
        if (SecAppearance is null || Nav.SelectedItem is not System.Windows.Controls.ListBoxItem { Tag: string target }) return;
        foreach (var section in new[] { SecAppearance, SecBehavior, SecTeam, SecBackups, SecUpdates })
            section.Visibility = section.Name == target ? Visibility.Visible : Visibility.Hidden;
    }

    private bool _navCollapsed;

    /// <summary>Hamburger: Menü auf Symbole zusammenklappen (Beschriftung dann als Tooltip) bzw. wieder aufklappen.</summary>
    private void OnToggleNav(object sender, RoutedEventArgs e)
    {
        _navCollapsed = !_navCollapsed;
        NavColumn.Width = new GridLength(_navCollapsed ? 64 : 210);
        foreach (var item in Nav.Items.OfType<System.Windows.Controls.ListBoxItem>())
        {
            if (item.Content is not System.Windows.Controls.StackPanel { Children: [FrameworkElement icon, System.Windows.Controls.TextBlock label] }) continue;
            label.Visibility = _navCollapsed ? Visibility.Collapsed : Visibility.Visible;
            icon.Margin = _navCollapsed ? new Thickness(0) : new Thickness(0, 0, 12, 0);
            item.HorizontalContentAlignment = _navCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            item.ToolTip = _navCollapsed ? label.Text : null;
        }
    }

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
            System.Windows.MessageBox.Show(this, $"Library backed up.\n\n{vm.TeamBackupSummary}", "Library backups");
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

    private string? PickLibraryFile(string current)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose library file (.xml) – or type the name of a new file",
            Filter = "mRemoteNG connection file (*.xml)|*.xml|All files|*.*",
            CheckFileExists = false,
            FileName = "",
        };
        try
        {
            current = current.Trim();
            if (current.Length > 0) dialog.InitialDirectory = System.IO.Directory.Exists(current) ? current : System.IO.Path.GetDirectoryName(current);
        }
        catch (ArgumentException) { }
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
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
