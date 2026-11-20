using System.Reflection;
using System.Runtime.InteropServices;

namespace HostDeck.App.Rdp;

/// <summary>
/// Minimal-Interop für das Windows-RDP-ActiveX (mstscax.dll). Alle IDs und die Methodenreihenfolge
/// der IUnknown-Schnittstellen stammen aus der Typbibliothek (per tlbimp erzeugte Metadaten geprüft).
/// Die dualen Settings-Objekte werden per IDispatch (Name) angesprochen – robust über Windows-Versionen.
/// </summary>
internal static class RdpClsid
{
    /// <summary>Neueste zuerst; die erste registrierte Klasse wird verwendet.</summary>
    public static readonly (int Version, string Clsid)[] Candidates =
    [
        (11, "1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8"),
        (10, "A0C63C30-F08D-4AB4-907C-34905D770C7D"),
        (9, "8B918B82-7985-4C24-89DF-C33AD2BBFBCD"),
        (8, "A3BC03A0-041D-42E3-AD22-882B7865C9C5"),
        (7, "54D38BF7-B1EF-4479-9674-1BD6EA465258"),
    ];
}

/// <summary>IMsTscNonScriptable – nur der erste Eintrag (ClearTextPassword) wird benutzt.</summary>
[ComImport, Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscNonScriptable
{
    void SetClearTextPassword([MarshalAs(UnmanagedType.BStr)] string value);
}

/// <summary>IMsRdpClientNonScriptable5 – vollständige vtable-Reihenfolge bis WarnAboutClipboardRedirection.</summary>
[ComImport, Guid("4F6996D5-D7B1-412C-B0FF-063718566907"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpClientNonScriptable5
{
    // IMsTscNonScriptable
    void SetClearTextPassword([MarshalAs(UnmanagedType.BStr)] string value);
    void SetPortablePassword([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetPortablePassword();
    void SetPortableSalt([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetPortableSalt();
    void SetBinaryPassword([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetBinaryPassword();
    void SetBinarySalt([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetBinarySalt();
    void ResetPassword();
    // IMsRdpClientNonScriptable
    void NotifyRedirectDeviceChange(nuint wParam, nint lParam);
    void SendKeys(int numKeys, ref short pbArrayKeyUp, ref int plKeyData);
    // IMsRdpClientNonScriptable2
    void SetUIParentWindowHandle(nint hwnd);
    nint GetUIParentWindowHandle();
    // IMsRdpClientNonScriptable3
    void SetShowRedirectionWarningDialog([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool GetShowRedirectionWarningDialog();
    void SetPromptForCredentials([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool GetPromptForCredentials();
    void SetNegotiateSecurityLayer([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool GetNegotiateSecurityLayer();
    void SetEnableCredSspSupport([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool GetEnableCredSspSupport();
    void SetRedirectDynamicDrives([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool GetRedirectDynamicDrives();
    void SetRedirectDynamicDevices([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool GetRedirectDynamicDevices();
    [return: MarshalAs(UnmanagedType.IDispatch)] object GetDeviceCollection();
    [return: MarshalAs(UnmanagedType.IDispatch)] object GetDriveCollection();
    // IMsRdpClientNonScriptable4
    void SetWarnAboutSendingCredentials([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool GetWarnAboutSendingCredentials();
    void SetWarnAboutClipboardRedirection([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool GetWarnAboutClipboardRedirection();
}

[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpExtendedSettings
{
    void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);
    object GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
}

/// <summary>Event-Dispinterface; nur die DISPIDs zählen (aus der Typbibliothek).</summary>
[ComImport, Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(5)] void OnEnterFullScreenMode();
    [DispId(6)] void OnLeaveFullScreenMode();
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
    [DispId(17)] void OnAutoReconnecting(int disconnectReason, int attemptCount, ref int pArcContinueStatus);
    [DispId(22)] void OnLogonError(int lError);
    [DispId(33)] void OnAutoReconnected();
}

/// <summary>Late-Binding über IDispatch für die dualen Schnittstellen (Settings, Methoden).</summary>
internal static class Dispatch
{
    public static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    public static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);

    public static object? Call(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    /// <summary>Setzt eine Eigenschaft, ignoriert aber „nicht vorhanden“ (ältere Control-Versionen).</summary>
    public static bool TrySet(object target, string name, object value)
    {
        try { Set(target, name, value); return true; }
        catch (Exception ex) when (ex is MissingMethodException or COMException or TargetInvocationException) { return false; }
    }
}
