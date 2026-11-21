using System.Net.Sockets;
using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Presentation.Services;

public enum Reachability { Unknown, Checking, Online, Offline }

/// <summary>
/// Online-Status im Baum: TCP-Verbindungsaufbau zum Dienst-Port (kein ICMP, kein Portscan) – nur für sichtbare
/// Verbindungen in aufgeklappten Ordnern, höchstens alle <see cref="Interval"/>, begrenzt parallel.
/// </summary>
public sealed class ReachabilityMonitor(SessionTreeViewModel tree, Func<bool> enabled, SynchronizationContext? ui) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<Guid, DateTime> _lastChecked = [];

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxParallel { get; init; } = 16;

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                if (enabled()) await CheckDueAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { AppLog.Error("Online-Status", ex); } // ein einzelner Fehler stoppt die Überwachung nicht
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Alle sichtbaren Verbindungen prüfen, deren letzter Test älter als <see cref="Interval"/> ist.</summary>
    public async Task CheckDueAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        // Baum-Sammlungen gehören dem UI-Thread ⇒ dort einen Schnappschuss ziehen
        List<TreeItemViewModel> visible = [];
        if (ui is null || SynchronizationContext.Current == ui) visible = VisibleConnections().ToList();
        else ui.Send(_ => visible = VisibleConnections().ToList(), null);
        List<TreeItemViewModel> due;
        lock (_lastChecked)
        {
            due = visible.Where(i => !_lastChecked.TryGetValue(i.Node.Id, out var t) || now - t >= Interval).ToList();
            foreach (var item in due) _lastChecked[item.Node.Id] = now;
        }

        using var gate = new SemaphoreSlim(MaxParallel);
        await Task.WhenAll(due.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var (host, port) = Target(item);
                if (port > 65535) return;
                if (string.IsNullOrWhiteSpace(host) || port <= 0) return;
                Post(() => item.Reachability = item.Reachability == Reachability.Unknown ? Reachability.Checking : item.Reachability);
                var online = await ProbeAsync(host, port, Timeout, ct).ConfigureAwait(false);
                Post(() => item.Reachability = online ? Reachability.Online : Reachability.Offline);
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
    }

    /// <summary>Sofort neu prüfen (z. B. nach dem Aufklappen eines Ordners).</summary>
    public void Invalidate()
    {
        lock (_lastChecked) _lastChecked.Clear();
    }

    internal IEnumerable<TreeItemViewModel> VisibleConnections()
    {
        IEnumerable<TreeItemViewModel> Walk(IEnumerable<TreeItemViewModel> items)
        {
            foreach (var i in items.Where(i => i.IsVisible))
            {
                if (!i.IsFolder) yield return i;
                else if (i.IsExpanded) foreach (var c in Walk(i.Children)) yield return c;
            }
        }
        return Walk(tree.Items).ToList();
    }

    private static (string Host, int Port) Target(TreeItemViewModel item)
    {
        var node = (ConnectionNode)item.Node;
        var host = node.Hostname.Trim();
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return (uri.Host, uri.Port);
        var port = SettingsResolver.Resolve(node).Port ?? 0;
        return (host.Split('/')[0], port);
    }

    public static async Task<bool> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }

    private void Post(Action action)
    {
        if (ui is null) action();
        else ui.Post(_ => action(), null);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
