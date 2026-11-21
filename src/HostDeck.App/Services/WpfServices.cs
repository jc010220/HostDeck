using System.Windows;
using HostDeck.App.Views;
using HostDeck.Presentation.Services;
using HostDeck.Protocols.Ssh;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace HostDeck.App.Services;

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        var mRemoteNgFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mRemoteNG");
        if (System.IO.Directory.Exists(mRemoteNgFolder)) dialog.InitialDirectory = mRemoteNgFolder;
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? AskMasterPassword(string? sourceName, bool previousAttemptFailed, int attempt, int maxAttempts)
    {
        var dialog = new MasterPasswordDialog(sourceName, previousAttemptFailed, attempt, maxAttempts) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    public bool Confirm(string title, string message) =>
        System.Windows.MessageBox.Show(Owner ?? Application.Current.MainWindow!, message, title, MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;

    public (string Current, string New)? AskChangeMasterPassword()
    {
        var dialog = new MasterKeyDialog(MasterKeyDialogMode.Change) { Owner = Owner };
        return dialog.ShowDialog() == true ? (dialog.CurrentPassword, dialog.Password) : null;
    }

    public HostKeyDecision AskHostKey(HostKeyInfo info)
    {
        var dialog = new HostKeyDialog(info) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Decision : HostKeyDecision.Reject;
    }

    public string? Prompt(string title, string message, string defaultValue = "", bool secret = false)
    {
        var dialog = new InputDialog(title, message, defaultValue, secret) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public bool EditConnection(HostDeck.Presentation.ViewModels.ConnectionEditorViewModel editor) =>
        new ConnectionEditorWindow(editor) { Owner = Owner }.ShowDialog() == true;

    public bool EditSettings(HostDeck.Presentation.ViewModels.SettingsViewModel settings) =>
        new SettingsWindow(settings) { Owner = Owner }.ShowDialog() == true;

    public void ManageVault(HostDeck.Presentation.ViewModels.CredentialVaultViewModel vault) =>
        new VaultWindow(vault) { Owner = Owner }.ShowDialog();

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public void ShowInfo(string title, string message) => new MessageDialog(title, message, isError: false) { Owner = Owner }.ShowDialog();
    public void ShowError(string title, string message) => new MessageDialog(title, message, isError: true) { Owner = Owner }.ShowDialog();
}

public sealed class ThemeService : IThemeService
{
    private Window? _watched;

    public void Apply(AppTheme theme)
    {
        var window = Application.Current?.MainWindow;
        if (theme == AppTheme.System)
        {
            ApplicationThemeManager.ApplySystemTheme(updateAccent: true);
            if (window is not null && _watched != window)
            {
                SystemThemeWatcher.Watch(window);
                _watched = window;
            }
        }
        else
        {
            if (window is not null && _watched == window)
            {
                SystemThemeWatcher.UnWatch(window);
                _watched = null;
            }
            ApplicationThemeManager.Apply(theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
        }
    }
}
