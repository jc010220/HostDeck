using System.Collections.Concurrent;

namespace HostDeck.Tests.Presentation;

/// <summary>
/// Einfacher „UI-Thread“ mit SynchronizationContext wie in WPF: Dialoge dürfen nur hier aufgerufen werden.
/// Tests ohne Kontext übersehen sonst Fehler wie „The calling thread must be STA“.
/// </summary>
internal sealed class UiThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public UiThread()
    {
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }) { IsBackground = true, Name = "Test-UI" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == ThreadId) { d(state); return; }
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(_ => { try { d(state); } catch (Exception ex) { error = ex; } finally { done.Set(); } }, null);
        done.Wait();
        if (error is not null) throw error;
    }

    /// <summary>Führt eine asynchrone Aktion vollständig auf dem UI-Thread aus.</summary>
    public Task<T> Run<T>(Func<Task<T>> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try { tcs.SetResult(await action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    public Task<T> Run<T>(Func<T> action) => Run(() => Task.FromResult(action()));

    public void Dispose() => _queue.CompleteAdding();
}
