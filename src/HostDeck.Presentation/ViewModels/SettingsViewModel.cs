using CommunityToolkit.Mvvm.ComponentModel;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.Services;

namespace HostDeck.Presentation.ViewModels;

/// <summary>Einstellungen-Dialog: arbeitet auf einer Kopie, übernimmt erst mit <see cref="Apply"/>.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        _theme = settings.Theme;
        _defaultProtocol = settings.QuickConnectDefaultProtocol;
        _credentialExtraction = settings.CredentialExtraction;
        _highlighting = settings.TerminalHighlighting;
        _sftpFollows = settings.SftpFollowsTerminal;
        _restoreWorkspace = settings.RestoreWorkspaceOnStart;
        _showReachability = settings.ShowReachability;
        _fontSize = FontSizes.Contains(settings.TerminalFontSize) ? settings.TerminalFontSize : 14;
        _updateSource = settings.UpdateSource ?? "";
    }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();
    public IReadOnlyList<int> FontSizes { get; } = [10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24];
    public IReadOnlyList<ProtocolKind> Protocols { get; } = [ProtocolKind.Ssh2, ProtocolKind.Rdp, ProtocolKind.Telnet, ProtocolKind.Vnc, ProtocolKind.Https];
    public IReadOnlyList<CredentialExtractionMode> ExtractionModes { get; } = Enum.GetValues<CredentialExtractionMode>();

    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private ProtocolKind _defaultProtocol;
    [ObservableProperty] private CredentialExtractionMode _credentialExtraction;
    [ObservableProperty] private bool _highlighting;
    [ObservableProperty] private bool _sftpFollows;
    [ObservableProperty] private bool _restoreWorkspace;
    [ObservableProperty] private bool _showReachability;
    [ObservableProperty] private int _fontSize;
    [ObservableProperty] private string _updateSource;

    public bool ThemeChanged => Theme != _settings.Theme;

    public void Apply()
    {
        _settings.Theme = Theme;
        _settings.QuickConnectDefaultProtocol = DefaultProtocol;
        _settings.CredentialExtraction = CredentialExtraction;
        _settings.TerminalHighlighting = Highlighting;
        _settings.SftpFollowsTerminal = SftpFollows;
        _settings.RestoreWorkspaceOnStart = RestoreWorkspace;
        _settings.ShowReachability = ShowReachability;
        _settings.TerminalFontSize = Math.Clamp(FontSize, 8, 32);
        var source = UpdateSource.Trim();
        if (source != (_settings.UpdateSource ?? ""))
        {
            _settings.UpdateSource = source.Length == 0 ? null : source;
            _settings.LastUpdateCheckUtc = null;
        }
    }
}
