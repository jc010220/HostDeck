namespace HostDeck.Core.Model;

/// <summary>Unterstützte Protokolle. Namen bewusst eigenständig, das Mapping zu mRemoteNG liegt im Importer.</summary>
public enum ProtocolKind
{
    Rdp,
    Ssh2,
    Ssh1,
    Telnet,
    Rlogin,
    Raw,
    Vnc,
    Http,
    Https,
    PowerShell,
    Wsl,
    Ard,
    Terminal,
    AnyDesk,
    Ica,
    ExternalApp,
}

public static class ProtocolDefaults
{
    public static int DefaultPort(ProtocolKind protocol) => protocol switch
    {
        ProtocolKind.Rdp => 3389,
        ProtocolKind.Ssh1 or ProtocolKind.Ssh2 => 22,
        ProtocolKind.Telnet => 23,
        ProtocolKind.Rlogin => 513,
        ProtocolKind.Vnc => 5900,
        ProtocolKind.Http => 80,
        ProtocolKind.Https => 443,
        ProtocolKind.PowerShell => 5985,
        ProtocolKind.Ard => 5900,
        ProtocolKind.Ica => 1494,
        _ => 0,
    };
}

// ---- RDP ------------------------------------------------------------------
// Werte-Namen identisch zu mRemoteNG, damit Import/Export verlustfrei bleibt.

public enum RdpColorDepth { Colors256 = 8, Colors15Bit = 15, Colors16Bit = 16, Colors24Bit = 24, Colors32Bit = 32 }

public enum RdpResolutionMode { FitToWindow, SmartSize, Fullscreen, Fixed }

public enum RdpSoundRedirection { BringToThisComputer = 0, LeaveAtRemoteComputer = 1, DoNotPlay = 2 }

public enum RdpSoundQuality { Dynamic = 0, Medium = 1, High = 2 }

public enum RdpAuthenticationLevel { NoAuth = 0, AuthRequired = 1, WarnOnFailedAuth = 2 }

public enum RdpDiskDrives { None, Local, All, Custom }

public enum RdpVersion { Rdc6, Rdc7, Rdc8, Rdc9, Rdc10, Rdc11, Highest = 1000 }

public enum RdGatewayUsage { Never = 0, Always = 1, Detect = 2 }

public enum RdGatewayCredentialMode { No = 0, Yes = 1, SmartCard = 2, ExternalCredentialProvider = 3, AccessToken = 4 }

// ---- VNC ------------------------------------------------------------------

public enum VncCompression { CompNone = 99, Comp0 = 0, Comp1, Comp2, Comp3, Comp4, Comp5, Comp6, Comp7, Comp8, Comp9 }

public enum VncEncoding { EncRaw, EncRRE, EncCorre, EncHextile, EncZlib, EncTight, EncZLibHex, EncZRLE }

public enum VncAuthMode { AuthVNC, AuthWin }

public enum VncProxyType { ProxyNone, ProxyHTTP, ProxySocks5, ProxyUltra }

public enum VncColors { ColNormal, Col8Bit }

public enum VncSmartSizeMode { SmartSNo, SmartSFree, SmartSAspect }
