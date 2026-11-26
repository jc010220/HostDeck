using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;

namespace HostDeck.Presentation.ViewModels;

/// <summary>Split-Screen-Layouts (MobaXterm: "Single / 2 vertikal / 2 horizontal / 4er-Grid").</summary>
public enum WorkspaceLayout { Single, TwoColumns, TwoRows, Grid }

public enum SessionState { Disconnected, Connecting, Connected, Failed }

/// <summary>Eine geöffnete Session (Tab). Die Protokoll-Engine wird in Phase 4 angehängt.</summary>
public sealed partial class SessionTabViewModel : ObservableObject
{
    public SessionTabViewModel(ConnectionNode connection, CredentialVault vault, bool isTransient)
    {
        Connection = connection;
        IsTransient = isTransient;
        Settings = SettingsResolver.Resolve(connection);
        Credentials = CredentialResolver.Resolve(connection, vault);
        _title = connection.Name;
    }

    public Guid TabId { get; } = Guid.NewGuid();
    public ConnectionNode Connection { get; }
    /// <summary>Quick-Connect-Session, die (noch) nicht im Baum gespeichert ist.</summary>
    public bool IsTransient { get; }
    /// <summary>Beim Öffnen aufgelöste effektive Einstellungen (Vererbung + Defaults).</summary>
    public ConnectionSettings Settings { get; }
    public ResolvedCredentials Credentials { get; }

    public ProtocolKind Protocol => Settings.Protocol ?? ProtocolKind.Rdp;
    public string Endpoint => $"{Connection.Hostname}:{Settings.Port}";
    public string UserDisplay => string.IsNullOrEmpty(Credentials.Domain) ? Credentials.Username : $@"{Credentials.Domain}\{Credentials.Username}";
    public string ToolTip => $"{Connection.Path}\n{Protocol.DisplayName()} → {Endpoint}" + (UserDisplay.Length > 0 ? $"\nUser: {UserDisplay}" : "");

    [ObservableProperty] private string _title;
    [ObservableProperty] private SessionState _state = SessionState.Disconnected;

    public bool IsConnecting => State == SessionState.Connecting;
    partial void OnStateChanged(SessionState value)
    {
        OnPropertyChanged(nameof(IsConnecting));
        if (value == SessionState.Connected) ConnectedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Zeitpunkt der letzten erfolgreichen Verbindung.</summary>
    public DateTime? ConnectedAtUtc { get; private set; }

    /// <summary>Aktionen, die das Fenster/der Workspace ausführen muss (Schließen, Ablösen, Speichern).</summary>
    public event Action<SessionTabViewModel, TabAction>? ActionRequested;

    [RelayCommand] private void RequestClose() => ActionRequested?.Invoke(this, TabAction.Close);
    [RelayCommand] private void RequestDetach() => ActionRequested?.Invoke(this, TabAction.Detach);
    [RelayCommand] private void RequestSave() => ActionRequested?.Invoke(this, TabAction.Save);
    [RelayCommand] private void RequestDuplicate() => ActionRequested?.Invoke(this, TabAction.Duplicate);
    [RelayCommand] private void RequestDuplicateSplit() => ActionRequested?.Invoke(this, TabAction.DuplicateSplit);

    [RelayCommand]
    private void Reconnect()
    {
        if (Terminal is { } terminal) _ = terminal.ConnectAsync();
        Rdp?.ConnectCommand.Execute(null);
        if (Vnc is { } vnc) _ = vnc.ConnectAsync();
    }

    /// <summary>Sitzung wurde vom Benutzer regulär beendet (exit/logout, Abmelden in RDP) – nicht bei Verbindungsfehlern.</summary>
    public event Action<SessionTabViewModel>? EndedNormally;
    public void ReportEndedNormally() => EndedNormally?.Invoke(this);

    /// <summary>Doppelklick auf den Tab: nur wenn die Verbindung getrennt bzw. fehlgeschlagen ist, neu verbinden.</summary>
    [RelayCommand]
    private void ReconnectIfClosed()
    {
        if (State is SessionState.Disconnected or SessionState.Failed) Reconnect();
    }

    [RelayCommand] private void FullScreen() => Rdp?.FullScreenCommand.Execute(null);

    /// <summary>Tab soll die Tastatur bekommen (neu geöffnet bzw. angeklickt) – die Ansicht fokussiert sich, sobald sie bereit ist.</summary>
    public event EventHandler? FocusRequested;
    /// <summary>Fokuswunsch, der noch nicht erfüllt werden konnte (z. B. Terminal lädt noch).</summary>
    public bool FocusPending { get; set; }

    public void RequestFocus()
    {
        FocusPending = true;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Laufenden Verbindungsaufbau abbrechen – unabhängig vom Protokoll.</summary>
    [RelayCommand]
    public void CancelConnect()
    {
        Terminal?.CancelConnect();
        Rdp?.CancelConnect();
        Vnc?.CancelConnect();
        Web?.CancelConnect();
    }
    /// <summary>Sichtbar im eigenen Bereich. Inhalte nicht ausgewählter Tabs bleiben erhalten (RDP-Verbindung!).</summary>
    [ObservableProperty] private bool _isSelected;
    /// <summary>Empfängt Eingaben im Multi-Execution-Modus (nur Terminal-Sessions, Phase 4).</summary>
    [ObservableProperty] private bool _isMultiExecutionTarget = true;

    public bool IsTerminal => Protocol is ProtocolKind.Ssh1 or ProtocolKind.Ssh2 or ProtocolKind.Telnet
        or ProtocolKind.Rlogin or ProtocolKind.Raw;

    /// <summary>SSH-Engine (Phase 4a); bei anderen Protokollen null.</summary>
    [ObservableProperty] private TerminalSessionViewModel? _terminal;

    /// <summary>Tab wurde geschlossen (View gibt WebView2/RDP-Control frei).</summary>
    public event EventHandler? Closed;
    internal void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>RDP-Engine (Phase 4b); bei anderen Protokollen null.</summary>
    [ObservableProperty] private RdpSessionViewModel? _rdp;

    /// <summary>HTTP/HTTPS-Engine; bei anderen Protokollen null.</summary>
    [ObservableProperty] private WebSessionViewModel? _web;
    public bool HasWeb => Web is not null;
    partial void OnWebChanged(WebSessionViewModel? value)
    {
        OnPropertyChanged(nameof(HasEngine));
        OnPropertyChanged(nameof(HasWeb));
    }

    /// <summary>VNC-Engine; bei anderen Protokollen null.</summary>
    [ObservableProperty] private VncSessionViewModel? _vnc;
    public bool HasVnc => Vnc is not null;
    partial void OnVncChanged(VncSessionViewModel? value)
    {
        OnPropertyChanged(nameof(HasEngine));
        OnPropertyChanged(nameof(HasVnc));
    }

    /// <summary>Für die View: Platzhalter nur, solange keine Engine existiert.</summary>
    public bool HasEngine => Terminal is not null || Rdp is not null || Web is not null || Vnc is not null;
    public bool HasTerminal => Terminal is not null;
    public bool HasRdp => Rdp is not null;

    partial void OnTerminalChanged(TerminalSessionViewModel? value)
    {
        OnPropertyChanged(nameof(HasEngine));
        OnPropertyChanged(nameof(HasTerminal));
    }

    partial void OnRdpChanged(RdpSessionViewModel? value)
    {
        OnPropertyChanged(nameof(HasEngine));
        OnPropertyChanged(nameof(HasRdp));
    }
}

public enum TabAction { Close, Detach, Save, Duplicate, DuplicateSplit }

/// <summary>Ein Bereich im Split-Screen mit eigener Tab-Leiste.</summary>
public sealed partial class PaneViewModel(int index) : ObservableObject
{
    public int Index { get; } = index;
    public ObservableCollection<SessionTabViewModel> Tabs { get; } = [];

    [ObservableProperty] private SessionTabViewModel? _selectedTab;
    [ObservableProperty] private bool _isActive;

    public bool IsEmpty => Tabs.Count == 0;

    partial void OnSelectedTabChanged(SessionTabViewModel? oldValue, SessionTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        // Tab gewechselt (Klick, Strg+Tab, Schließen) ⇒ gleich weitertippen können
        if (oldValue is not null && newValue is not null) newValue.RequestFocus();
    }
    internal void NotifyEmptyChanged() => OnPropertyChanged(nameof(IsEmpty));
}

/// <summary>Arbeitsbereich: Bereiche (1/2/4), Tabs, aktiver Bereich, Tab-Verschiebung.</summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    /// <summary>Fensterposition (nur bei abgelösten Fenstern; vom Fenster gepflegt, beim Beenden gemerkt).</summary>
    public Services.WindowPlacement? Placement { get; set; }

    public WorkspaceViewModel()
    {
        Panes.Add(new PaneViewModel(0) { IsActive = true });
        _activePane = Panes[0];
    }

    public ObservableCollection<PaneViewModel> Panes { get; } = [];

    [ObservableProperty] private WorkspaceLayout _layout = WorkspaceLayout.Single;
    [ObservableProperty] private PaneViewModel _activePane;

    public int Rows => Layout is WorkspaceLayout.TwoRows or WorkspaceLayout.Grid ? 2 : 1;
    public int Columns => Layout is WorkspaceLayout.TwoColumns or WorkspaceLayout.Grid ? 2 : 1;

    public IEnumerable<SessionTabViewModel> AllTabs => Panes.SelectMany(p => p.Tabs);
    public SessionTabViewModel? ActiveTab => ActivePane.SelectedTab;
    public int TabCount => Panes.Sum(p => p.Tabs.Count);

    public static int PaneCount(WorkspaceLayout layout) => layout switch
    {
        WorkspaceLayout.Single => 1,
        WorkspaceLayout.Grid => 4,
        _ => 2,
    };

    partial void OnActivePaneChanged(PaneViewModel? oldValue, PaneViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        newValue.IsActive = true;
        OnPropertyChanged(nameof(ActiveTab));
    }

    /// <summary>
    /// Wechselt das Layout. Fallen Bereiche weg, wandern deren Tabs in den letzten verbleibenden
    /// Bereich – es wird nie eine Session geschlossen.
    /// </summary>
    [RelayCommand]
    public void SetLayout(WorkspaceLayout layout)
    {
        var target = PaneCount(layout);
        while (Panes.Count < target)
            Panes.Add(new PaneViewModel(Panes.Count));

        if (Panes.Count > target)
        {
            var keep = Panes[target - 1];
            var keepSelection = keep.SelectedTab;
            var removedPanes = Panes.Skip(target).ToList(); // in Reihenfolge, damit Tabs geordnet bleiben
            var wasActiveRemoved = removedPanes.Contains(ActivePane);
            foreach (var removed in removedPanes)
                foreach (var tab in removed.Tabs.ToList())
                    MoveTab(tab, keep);
            foreach (var removed in removedPanes)
                Panes.Remove(removed);
            if (!wasActiveRemoved && keepSelection is not null) keep.SelectedTab = keepSelection;
            ActivePane = wasActiveRemoved || !Panes.Contains(ActivePane) ? keep : ActivePane;
        }

        Layout = layout;
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Columns));
    }

    /// <summary>Öffnet einen Tab im angegebenen bzw. aktiven Bereich und aktiviert ihn.</summary>
    public SessionTabViewModel Open(SessionTabViewModel tab, PaneViewModel? pane = null)
    {
        pane ??= ActivePane;
        pane.Tabs.Add(tab);
        pane.SelectedTab = tab;
        pane.NotifyEmptyChanged();
        ActivePane = pane;
        NotifyTabsChanged();
        tab.RequestFocus(); // neue Shell: sofort lostippen, ohne erst hineinzuklicken
        return tab;
    }

    /// <summary>
    /// Öffnet mehrere Sessions verteilt: 2 ⇒ nebeneinander, 3–4 ⇒ 4er-Grid, mehr ⇒ reihum auf 4 Bereiche.
    /// (MobaXterm: "Alle Sessions des Ordners im Split-Screen öffnen")
    /// </summary>
    public void OpenTiled(IReadOnlyList<SessionTabViewModel> tabs)
    {
        if (tabs.Count == 0) return;
        var layout = tabs.Count switch { 1 => Layout, 2 => WorkspaceLayout.TwoColumns, _ => WorkspaceLayout.Grid };
        if (PaneCount(layout) > Panes.Count) SetLayout(layout);
        for (var i = 0; i < tabs.Count; i++)
            Open(tabs[i], Panes[i % Panes.Count]);
        ActivePane = Panes[0];
    }

    [RelayCommand]
    public void Close(SessionTabViewModel? tab)
    {
        if (tab is null) return;
        var pane = Panes.FirstOrDefault(p => p.Tabs.Contains(tab));
        if (pane is null) return;

        var index = pane.Tabs.IndexOf(tab);
        pane.Tabs.RemoveAt(index);
        if (pane.SelectedTab == tab) pane.SelectedTab = null;
        if (tab.Terminal is { } terminal) _ = terminal.DisposeAsync().AsTask(); // Verbindung trennen
        if (tab.Vnc is { } vnc) _ = vnc.DisposeAsync().AsTask();
        tab.RaiseClosed();
        if (pane.SelectedTab is null || pane.SelectedTab == tab)
            pane.SelectedTab = pane.Tabs.Count == 0 ? null : pane.Tabs[Math.Min(index, pane.Tabs.Count - 1)];
        pane.NotifyEmptyChanged();
        NotifyTabsChanged();
    }

    /// <summary>Tab aus diesem Workspace entfernen, ohne die Session zu beenden (Wechsel in ein anderes Fenster).</summary>
    public void Detach(SessionTabViewModel tab)
    {
        var pane = Panes.FirstOrDefault(p => p.Tabs.Contains(tab));
        if (pane is null) return;
        var index = pane.Tabs.IndexOf(tab);
        pane.Tabs.RemoveAt(index);
        if (pane.SelectedTab == tab) pane.SelectedTab = pane.Tabs.Count == 0 ? null : pane.Tabs[Math.Min(index, pane.Tabs.Count - 1)];
        tab.IsSelected = pane.SelectedTab == tab;
        pane.NotifyEmptyChanged();
        NotifyTabsChanged();
    }

    public void MoveTab(SessionTabViewModel tab, PaneViewModel target, int? index = null)
    {
        var source = Panes.FirstOrDefault(p => p.Tabs.Contains(tab));
        if (source is null) return;
        if (source == target && index is null) return;

        var oldIndex = source.Tabs.IndexOf(tab);
        source.Tabs.RemoveAt(oldIndex);
        if (source.SelectedTab == tab) source.SelectedTab = null;
        if (source.SelectedTab is null || source.SelectedTab == tab)
            source.SelectedTab = source.Tabs.Count == 0 ? null : source.Tabs[Math.Min(oldIndex, source.Tabs.Count - 1)];

        var insertAt = Math.Clamp(index ?? target.Tabs.Count, 0, target.Tabs.Count);
        target.Tabs.Insert(insertAt, tab);
        target.SelectedTab = tab;
        source.NotifyEmptyChanged();
        target.NotifyEmptyChanged();
        ActivePane = target;
        NotifyTabsChanged();
    }

    /// <summary>Strg+Tab / Strg+Shift+Tab innerhalb des aktiven Bereichs.</summary>
    /// <summary>
    /// Strg+Tab / Strg+Umschalt+Tab: In der geteilten Ansicht zwischen den sichtbaren Sitzungen (Bereichen) wechseln,
    /// sonst zwischen den Tabs. Die neue Sitzung bekommt gleich die Tastatur.
    /// </summary>
    public void SelectNextTab(bool backwards = false)
    {
        var step = backwards ? -1 : 1;
        var panes = Panes.Where(p => p.SelectedTab is not null).ToList();
        if (panes.Count > 1)
        {
            var i = Math.Max(0, panes.IndexOf(ActivePane));
            ActivePane = panes[(i + step + panes.Count) % panes.Count];
        }
        else
        {
            var tabs = ActivePane.Tabs;
            if (tabs.Count < 2 || ActivePane.SelectedTab is null) return;
            var i = tabs.IndexOf(ActivePane.SelectedTab);
            ActivePane.SelectedTab = tabs[(i + step + tabs.Count) % tabs.Count];
        }
        OnPropertyChanged(nameof(ActiveTab));
        ActiveTab?.RequestFocus();
    }

    /// <summary>Strg+Alt+Pfeil: nächsten Bereich aktivieren.</summary>
    public void FocusNextPane()
    {
        if (Panes.Count > 1)
            ActivePane = Panes[(ActivePane.Index + 1) % Panes.Count];
    }

    private void NotifyTabsChanged()
    {
        OnPropertyChanged(nameof(TabCount));
        OnPropertyChanged(nameof(ActiveTab));
    }
}
