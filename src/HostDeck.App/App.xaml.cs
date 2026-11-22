using System.IO;
using System.Windows;
using System.Windows.Threading;
using HostDeck.App.Services;
using HostDeck.App.Views;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.ViewModels;
using HostDeck.Storage;

namespace HostDeck.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private StoreAdapter? _store;
    private UpdateService? _updates;
    private BackupService? _backups;
    private DpapiDeviceKeyProtector? _device;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Behaviors.GentleWheelScroll.Register();
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", (Exception)args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Background task", args.Exception); args.SetObserved(); };
        Log.Info($"Startup, version {typeof(App).Assembly.GetName().Version}");
        Log.Cleanup();
        HostDeck.Presentation.Services.AppLog.Sink = Log.Error;
        HostDeck.Presentation.Services.AppLog.InfoSink = Log.Info;
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // Dialoge vor dem Hauptfenster dürfen die App nicht beenden

        // Zwei Instanzen würden sich gegenseitig die Datenbank überschreiben
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\HostDeck.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("HostDeck is already running.", "HostDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var settingsStore = new JsonSettingsStore(JsonSettingsStore.DefaultPath);
        var theme = new ThemeService();
        theme.Apply(settingsStore.Load().Theme); // Entsperr-Dialog bereits im richtigen Theme

        var device = _device = new DpapiDeviceKeyProtector();
        var store = UnlockOrCreate(EncryptedConnectionStore.DefaultPath, device);
        Log.Info(store is null ? "Unlock cancelled" : $"Database opened: {store.DatabasePath} ({new FileInfo(store.DatabasePath).Length / 1024} KB)");
        if (store is null)
        {
            Shutdown();
            return;
        }
        _store = new StoreAdapter(store, device);

        var dialogs = new DialogService();
        var viewModel = new MainViewModel(dialogs, settingsStore, theme, _store);
        _updates = new UpdateService(viewModel, dialogs, _store);
        _updates.Register();
        viewModel.AppVersion = _updates.CurrentVersion;
        viewModel.CheckForUpdatesAction = () => _ = _updates.CheckAsync(interactive: true);
        viewModel.RestoreBackupAction = RestoreBackup;
        _viewModel = viewModel;
        var window = new MainWindow(viewModel);
        Views.MainWindow.RegisterGlobalShortcuts(window);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Closing += (_, _) => viewModel.Shutdown();
        window.Show();

        _ = StartAsync(viewModel, store, e.Args);
    }

    /// <summary>Laden, Arbeitsbereich wiederherstellen, ggf. importieren – ohne den UI-Thread zu blockieren.</summary>
    private async Task StartAsync(MainViewModel viewModel, EncryptedConnectionStore store, string[] args)
    {
        try
        {
            _backups = new BackupService(viewModel, _store!, Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "backups"));
            viewModel.BackupService = _backups;
            await Task.Run(_backups.Start); // Kopieren einer großen DB nicht im UI-Thread
            var mRemoteNgFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mRemoteNG", "confCons.xml");
            await viewModel.InitializeAsync(mRemoteNgFile);
            Log.Info("Startup complete");
            _ = _updates!.CheckOnStartupAsync();

            // Datei per Kommandozeile / "Öffnen mit" (z. B. HostDeck.exe confCons.xml)
            if (args is [var path, ..] && File.Exists(path))
                await viewModel.TryImportAsync(path);
        }
        catch (Exception ex)
        {
            Log.Error("Start", ex);
        }
    }

    /// <summary>Einmal täglich beim Start eine verschlüsselte Sicherung (die letzten 10 bleiben erhalten).</summary>

    /// <summary>Gerätschlüssel → Passwortdialog (mit Wiederholung) → bzw. Ersteinrichtung.</summary>
    private static EncryptedConnectionStore? UnlockOrCreate(string path, IDeviceKeyProtector device)
    {
        if (!EncryptedConnectionStore.Exists(path))
            return CreateNew(path, device);

        try
        {
            if (OperatingSystem.IsWindows() && EncryptedConnectionStore.TryOpenWithDevice(path, device) is { } viaDevice)
                return viaDevice;
        }
        catch (InvalidDataException) { /* Geräteschlüssel passt nicht mehr ⇒ Passwort abfragen */ }

        string? error = null;
        while (true)
        {
            var dialog = new MasterKeyDialog(MasterKeyDialogMode.Unlock, error, OperatingSystem.IsWindows());
            if (dialog.ShowDialog() != true)
            {
                if (!dialog.ResetRequested) return null;
                MoveAside(path);
                return CreateNew(path, device);
            }

            try
            {
                var store = EncryptedConnectionStore.Open(path, dialog.Password);
                if (dialog.Remember) store.EnableDeviceUnlock(device);
                return store;
            }
            catch (WrongMasterPasswordException)
            {
                error = "Wrong master password.";
            }
            catch (InvalidDataException ex)
            {
                MessageBox.Show(ex.Message, "Database cannot be opened", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }
    }

    private static EncryptedConnectionStore? CreateNew(string path, IDeviceKeyProtector device)
    {
        var dialog = new MasterKeyDialog(MasterKeyDialogMode.Create, deviceUnlockSupported: OperatingSystem.IsWindows());
        if (dialog.ShowDialog() != true) return null;
        var store = EncryptedConnectionStore.Create(path, dialog.Password);
        if (dialog.Remember) store.EnableDeviceUnlock(device);
        return store;
    }

    /// <summary>„Passwort vergessen“: alte Dateien nicht löschen, sondern mit Zeitstempel sichern.</summary>
    private static void MoveAside(string path)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var file in new[] { path, path + ".key", path + "-wal", path + "-shm" })
            if (File.Exists(file)) File.Move(file, $"{file}.{stamp}.bak");
    }

    // ---- Persönliche Sicherung wiederherstellen ---------------------------------------------------

    /// <summary>
    /// 1) Sicherung prüfen (in einer Kopie öffnen, Inhalt anzeigen), 2) bestätigen lassen, 3) aktuellen Stand als
    /// „HostDeck-Personal-BeforeRestore_…“ sichern, 4) Datenbank schließen und ersetzen, 5) HostDeck neu starten.
    /// </summary>
    private void RestoreBackup(string backupFile)
    {
        var owner = MainWindow;
        if (_viewModel is null || _store is null) return;
        if (!File.Exists(backupFile) || !File.Exists(backupFile + ".key"))
        {
            MessageBox.Show(owner, "The backup or its key file (.hdk.key) is missing.", "Restore personal backup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 1) Prüfen – in einem Temp-Ordner, damit die Sicherung selbst unverändert bleibt
        string summary;
        var temp = Directory.CreateTempSubdirectory("hostdeck-restore-").FullName;
        try
        {
            var probe = Path.Combine(temp, "probe.hdk");
            File.Copy(backupFile, probe);
            File.Copy(backupFile + ".key", probe + ".key");
            EncryptedConnectionStore? opened = null;
            try { opened = EncryptedConnectionStore.TryOpenWithDevice(probe, _device!); }
            catch (InvalidDataException) { }
            for (var attempt = 1; opened is null && attempt <= 3; attempt++)
            {
                var dialog = new MasterPasswordDialog($"Backup {Path.GetFileName(backupFile)}", attempt > 1, attempt, 3) { Owner = owner };
                if (dialog.ShowDialog() != true) return;
                try { opened = EncryptedConnectionStore.Open(probe, dialog.Password); }
                catch (WrongMasterPasswordException) { }
            }
            if (opened is null)
            {
                MessageBox.Show(owner, "The master password of this backup was entered incorrectly three times. Nothing was changed.", "Restore personal backup");
                return;
            }
            using (opened)
            {
                var tree = opened.Load();
                summary = $"{tree.Connections.Count()} connections in {tree.Folders.Count()} folders, {tree.Vault.Count} vault entries";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Error("Check backup", ex);
            MessageBox.Show(owner, $"This backup cannot be opened: {ex.Message}\n\nNothing was changed.", "Restore personal backup", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // 2) Bestätigen
        if (MessageBox.Show(owner,
                $"Restore this personal backup?\n\n{Path.GetFileName(backupFile)}  ({File.GetLastWriteTime(backupFile):g})\nContains {summary}.\n\n" +
                "The current state is saved as a backup first. HostDeck then restarts – unlock it with the master password that was valid at backup time. " +
                "The team library is not affected; it is synchronized again after the restart.",
                "Restore personal backup", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // 3) Aktuellen Stand sichern (nie von der Aufbewahrungsregel gelöscht)
        string safety;
        try
        {
            _viewModel.Shutdown();
            var folder = _backups?.Folder ?? Path.Combine(Path.GetDirectoryName(EncryptedConnectionStore.DefaultPath)!, "backups");
            safety = _store.BackupTo(Path.Combine(folder, $"HostDeck-Personal-BeforeRestore_{DateTime.Now:yyyyMMdd-HHmmss}.hdk"));
            Log.Info($"Restore: current state saved as {safety}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Backup before restore", ex);
            MessageBox.Show(owner, $"The current state could not be backed up ({ex.Message}). Nothing was changed.", "Restore personal backup", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 4) Schließen und ersetzen
        var target = EncryptedConnectionStore.DefaultPath;
        try
        {
            _backups?.Dispose();
            _store.Dispose();
            _store = null;
            foreach (var leftover in new[] { target + "-wal", target + "-shm" })
                if (File.Exists(leftover)) File.Delete(leftover);
            File.Copy(backupFile, target, overwrite: true);
            File.Copy(backupFile + ".key", target + ".key", overwrite: true);
            Log.Info($"Restore: {backupFile} restored");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Restore backup", ex);
            MessageBox.Show(owner, $"Restoring failed: {ex.Message}\n\nYour previous state is saved here:\n{safety}\n\nHostDeck will now close.",
                "Restore personal backup", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // 5) Neu starten (Einzelinstanz-Sperre vorher freigeben, sonst meldet die neue Instanz „läuft bereits“)
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        _singleInstance = null;
        if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updates?.ApplyOnExit();
        _backups?.Dispose();
        _store?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static bool _showingError;

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI", e.Exception);
        e.Handled = true; // Sessions in anderen Tabs nicht wegen eines UI-Fehlers verlieren
        if (_showingError) return;
        _showingError = true;
        // Dialog nicht innerhalb des fehlerhaften Aufrufs öffnen (Layout/COM-Rückruf) – sonst Folgeabsturz
        Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                new MessageDialog("Unexpected error",
                    $"{e.Exception.Message}\n\nHostDeck keeps running. Details in the error log:\n{Log.Directory}", isError: true).ShowDialog();
            }
            catch (Exception ex) { Log.Error("Error dialog", ex); }
            finally { _showingError = false; }
        });
    }
}
