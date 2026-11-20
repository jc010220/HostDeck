using Wpf.Ui.Controls;

namespace HostDeck.App;

/// <summary>
/// Alle verwendeten Fluent-Icons an einer Stelle. In XAML per <c>{x:Static app:Icons.Folder}</c> –
/// so fallen falsche Icon-Namen schon beim Kompilieren auf statt erst zur Laufzeit.
/// </summary>
public static class Icons
{
    public static SymbolRegular Folder => SymbolRegular.Folder24;
    public static SymbolRegular FolderOpen => SymbolRegular.FolderOpen24;
    public static SymbolRegular FolderAdd => SymbolRegular.FolderAdd24;
    public static SymbolRegular Add => SymbolRegular.Add24;
    public static SymbolRegular Delete => SymbolRegular.Delete24;
    public static SymbolRegular Search => SymbolRegular.Search24;
    public static SymbolRegular Flash => SymbolRegular.Flash24;
    public static SymbolRegular Dismiss => SymbolRegular.Dismiss16;
    public static SymbolRegular Import => SymbolRegular.ArrowImport24;
    public static SymbolRegular Export => SymbolRegular.ArrowExportLtr24;
    public static SymbolRegular Save => SymbolRegular.Save24;
    public static SymbolRegular Moon => SymbolRegular.WeatherMoon24;
    public static SymbolRegular Keyboard => SymbolRegular.KeyboardShift24;
    public static SymbolRegular Plug => SymbolRegular.PlugConnected24;
    public static SymbolRegular Lock => SymbolRegular.LockClosed24;
    public static SymbolRegular ArrowUp => SymbolRegular.ArrowUp24;
    public static SymbolRegular Refresh => SymbolRegular.ArrowClockwise24;
    public static SymbolRegular Document => SymbolRegular.Document24;
    public static SymbolRegular Edit => SymbolRegular.Edit24;

    public static SymbolRegular LayoutSingle => SymbolRegular.Square24;
    public static SymbolRegular LayoutColumns => SymbolRegular.LayoutColumnTwo24;
    public static SymbolRegular LayoutRows => SymbolRegular.LayoutRowTwo24;
    public static SymbolRegular LayoutGrid => SymbolRegular.Grid24;

    public static SymbolRegular Desktop => SymbolRegular.Desktop24;
    public static SymbolRegular Console => SymbolRegular.WindowConsole20;
    public static SymbolRegular Globe => SymbolRegular.Globe24;
    public static SymbolRegular Screen => SymbolRegular.ScreenSearch24;
    public static SymbolRegular App => SymbolRegular.Apps24;
    public static SymbolRegular Server => SymbolRegular.Server24;

    public static SymbolRegular ForProtocol(Core.Model.ProtocolKind protocol) => protocol switch
    {
        Core.Model.ProtocolKind.Rdp or Core.Model.ProtocolKind.Ica => Desktop,
        Core.Model.ProtocolKind.Ssh1 or Core.Model.ProtocolKind.Ssh2 or Core.Model.ProtocolKind.Telnet
            or Core.Model.ProtocolKind.Rlogin or Core.Model.ProtocolKind.Raw
            or Core.Model.ProtocolKind.PowerShell or Core.Model.ProtocolKind.Wsl or Core.Model.ProtocolKind.Terminal => Console,
        Core.Model.ProtocolKind.Http or Core.Model.ProtocolKind.Https => Globe,
        Core.Model.ProtocolKind.Vnc or Core.Model.ProtocolKind.Ard or Core.Model.ProtocolKind.AnyDesk => Screen,
        _ => App,
    };
}
