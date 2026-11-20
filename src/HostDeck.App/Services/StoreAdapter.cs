using HostDeck.Core.Model;
using HostDeck.Presentation.Services;
using HostDeck.Storage;

namespace HostDeck.App.Services;

/// <summary>Verbindet den verschlüsselten Store mit dem ViewModel (Gerätschlüssel = Windows DPAPI).</summary>
public sealed class StoreAdapter(EncryptedConnectionStore store, IDeviceKeyProtector deviceProtector) : IConnectionStore, IDisposable
{
    public string Location => store.DatabasePath;
    public ConnectionTree Load() => store.Load();
    public void Save(ConnectionTree tree) => store.Save(tree);

    public bool HasDeviceUnlock => store.HasDeviceUnlock;
    public bool DeviceUnlockSupported => OperatingSystem.IsWindows();

    public void SetDeviceUnlock(bool enabled)
    {
        if (enabled) store.EnableDeviceUnlock(deviceProtector);
        else store.DisableDeviceUnlock();
    }

    public void ChangeMasterPassword(string currentPassword, string newPassword)
    {
        try { store.ChangeMasterPassword(currentPassword, newPassword); }
        catch (WrongMasterPasswordException) { throw new UnauthorizedAccessException(); }
    }

    public void Dispose() => store.Dispose();
}
