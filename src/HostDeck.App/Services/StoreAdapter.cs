using HostDeck.Core.Model;
using HostDeck.Presentation.Services;
using HostDeck.Storage;

namespace HostDeck.App.Services;

/// <summary>Verbindet den verschlüsselten Store mit dem ViewModel (Gerätschlüssel = Windows DPAPI).</summary>
/// <remarks>
/// Thread-sicher: Host-Key-Abfragen kommen aus dem SSH-Verbindungsthread, Speichern aus dem UI-Thread –
/// eine SQLite-Verbindung darf aber nicht parallel benutzt werden.
/// </remarks>
public sealed class StoreAdapter(EncryptedConnectionStore store, IDeviceKeyProtector deviceProtector) : IConnectionStore, IDisposable
{
    private readonly Lock _lock = new();

    public string Location => store.DatabasePath;
    public ConnectionTree Load() { lock (_lock) return store.Load(); }
    public void Save(ConnectionTree tree) { lock (_lock) store.Save(tree); }

    public int CountConnections() { lock (_lock) return store.CountConnections(); }
    public string Backup(string targetDirectory) { lock (_lock) return store.Backup(targetDirectory); }
    public string BackupTo(string targetFile) { lock (_lock) return store.BackupTo(targetFile, overwrite: true); } // Dialog hat Überschreiben bestätigt
    public string? ReadDocument(string name) { lock (_lock) return store.ReadDocument(name); }
    public void WriteDocument(string name, string json) { lock (_lock) store.WriteDocument(name, json); }

    public string? Find(string host, int port, string keyType) { lock (_lock) return store.FindHostKey(host, port, keyType); }
    public void Remember(string host, int port, string keyType, string fingerprint) { lock (_lock) store.RememberHostKey(host, port, keyType, fingerprint); }

    public bool HasDeviceUnlock => store.HasDeviceUnlock;
    public bool DeviceUnlockSupported => OperatingSystem.IsWindows();

    public void SetDeviceUnlock(bool enabled)
    {
        lock (_lock)
        {
            if (enabled) store.EnableDeviceUnlock(deviceProtector);
            else store.DisableDeviceUnlock();
        }
    }

    public void ChangeMasterPassword(string currentPassword, string newPassword)
    {
        try { lock (_lock) store.ChangeMasterPassword(currentPassword, newPassword); }
        catch (WrongMasterPasswordException) { throw new UnauthorizedAccessException(); }
    }

    public void Dispose() { lock (_lock) store.Dispose(); }
}
