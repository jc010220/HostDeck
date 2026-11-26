using CommunityToolkit.Mvvm.Input;
using HostDeck.Core.Model;
using HostDeck.Core.Sync;

namespace HostDeck.Presentation.ViewModels;

/// <summary>
/// Strg+Z im Baum: letzte Änderung (Löschen, Verschieben, Umbenennen, Eigenschaften, Duplizieren, Import …) zurücknehmen.
/// Vor jedem Speichern wird der vorherige Stand gemerkt (höchstens 30 Schritte). Änderungen anderer aus einer
/// Bibliothek lassen sich nicht „rückgängig“ machen – nach einem Abgleich beginnt der Verlauf neu.
/// </summary>
public sealed partial class MainViewModel
{
    private const int MaxUndo = 30;
    private readonly List<(List<TreeNode> Nodes, string Signature)> _undo = [];
    private readonly List<(List<TreeNode> Nodes, string Signature)> _redo = [];
    private (List<TreeNode> Nodes, string Signature)? _lastState;
    private bool _restoringUndo;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private static string Signature(ConnectionTree tree)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(FolderNode folder)
        {
            foreach (var child in folder.Children)
            {
                sb.Append(child.Id).Append(':').Append(folder.Id).Append(':').Append(TreeMerge.Fingerprint(child))
                  .Append(':').Append(child.Settings.CredentialId).Append(';');
                if (child is FolderNode f) Walk(f);
            }
        }
        Walk(tree.Root);
        return sb.ToString();
    }

    private (List<TreeNode>, string) CaptureState() =>
        (SessionTree.Tree.Root.Children.Select(c => TreeMerge.CopyWithIds(c, includePersonal: true)).ToList(), Signature(SessionTree.Tree));

    /// <summary>Nach dem Laden bzw. nach einem Abgleich: Verlauf neu beginnen.</summary>
    private void ResetUndo()
    {
        _undo.Clear();
        _redo.Clear();
        _lastState = _loadSucceeded ? CaptureState() : null;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    /// <summary>Nach jedem erfolgreichen Speichern: vorherigen Stand merken, falls sich der Baum wirklich geändert hat.</summary>
    private void RecordUndoStep()
    {
        if (_restoringUndo) return;
        var current = CaptureState();
        if (_lastState is { } last && last.Signature != current.Item2)
        {
            _undo.Add(last);
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            _redo.Clear(); // neue Änderung ⇒ „Wiederholen“ verfällt (wie in jedem Editor)
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }
        _lastState = current;
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0)
        {
            StatusText = "Nothing to undo.";
            return;
        }
        var (nodes, _) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        if (_lastState is { } now) _redo.Add(now);
        Restore(nodes);
        StatusText = _undo.Count > 0 ? $"Undone ({_undo.Count} more step(s) possible – Ctrl+Z, redo with Ctrl+Y)" : "Undone (redo with Ctrl+Y)";
    }

    /// <summary>Strg+Y: zuletzt Rückgängig Gemachtes wiederherstellen.</summary>
    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0)
        {
            StatusText = "Nothing to redo.";
            return;
        }
        var (nodes, _) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        if (_lastState is { } now) _undo.Add(now);
        Restore(nodes);
        StatusText = "Redone";
    }

    private void Restore(List<TreeNode> nodes)
    {
        var root = SessionTree.Tree.Root;
        foreach (var child in root.Children.ToList()) root.Remove(child);
        foreach (var node in nodes) root.Add(TreeMerge.CopyWithIds(node, includePersonal: true));
        _restoringUndo = true;
        try
        {
            SessionTree.Load(SessionTree.Tree);
            Persist();
        }
        finally { _restoringUndo = false; }
        _lastState = CaptureState();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        Reachability?.Invalidate();
    }
}
