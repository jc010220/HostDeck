using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.App.Views;

/// <summary>
/// Ordnet die Bereiche des Workspace im 1/2/4-Grid an, mit verschiebbaren Trennern.
/// PaneViews werden pro PaneViewModel wiederverwendet – ein Layoutwechsel erzeugt keine
/// Session-Ansichten neu (wichtig für eingebettete RDP-/Terminal-Fenster in Phase 4).
/// </summary>
public sealed class SplitWorkspace : Grid
{
    public static readonly DependencyProperty WorkspaceProperty = DependencyProperty.Register(
        nameof(Workspace), typeof(WorkspaceViewModel), typeof(SplitWorkspace), new PropertyMetadata(null, OnWorkspaceChanged));

    private readonly Dictionary<PaneViewModel, PaneView> _views = [];

    public WorkspaceViewModel? Workspace
    {
        get => (WorkspaceViewModel?)GetValue(WorkspaceProperty);
        set => SetValue(WorkspaceProperty, value);
    }

    /// <summary>Wird aufgerufen, wenn ein Tree-Eintrag auf einen Bereich gezogen wird.</summary>
    public Action<TreeItemViewModel, PaneViewModel>? OpenInPane { get; set; }

    private static void OnWorkspaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (SplitWorkspace)d;
        if (e.OldValue is WorkspaceViewModel old)
        {
            old.PropertyChanged -= self.OnWorkspacePropertyChanged;
            old.Panes.CollectionChanged -= self.OnPanesChanged;
        }
        if (e.NewValue is WorkspaceViewModel ws)
        {
            ws.PropertyChanged += self.OnWorkspacePropertyChanged;
            ws.Panes.CollectionChanged += self.OnPanesChanged;
        }
        self.Rebuild();
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.Layout)) Rebuild();
    }

    private void OnPanesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        // Nur Trenner und weggefallene Bereiche entfernen. Bestehende PaneViews bleiben im visuellen Baum,
        // sonst würden eingebettete HWNDs (RDP-ActiveX, WebView2) zerstört und Sessions getrennt.
        foreach (var splitter in Children.OfType<GridSplitter>().ToList()) Children.Remove(splitter);
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        if (Workspace is not { } ws)
        {
            Children.Clear();
            return;
        }

        foreach (var stale in _views.Where(kv => !ws.Panes.Contains(kv.Key)).ToList())
        {
            Children.Remove(stale.Value);
            _views.Remove(stale.Key);
        }

        int rows = ws.Rows, cols = ws.Columns;
        for (var r = 0; r < rows; r++)
        {
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 });
            if (r < rows - 1) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (var c = 0; c < cols; c++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
            if (c < cols - 1) ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var i = 0; i < ws.Panes.Count && i < rows * cols; i++)
        {
            var pane = ws.Panes[i];
            if (!_views.TryGetValue(pane, out var view))
            {
                view = new PaneView { DataContext = pane, Workspace = ws };
                view.OpenTreeItem = (item, p) => OpenInPane?.Invoke(item, p);
                _views[pane] = view;
                Children.Add(view);
            }
            SetRow(view, i / cols * 2);
            SetColumn(view, i % cols * 2);
        }

        for (var r = 0; r < rows - 1; r++)
            Children.Add(Splitter(row: r * 2 + 1, column: 0, rowSpan: 1, columnSpan: ColumnDefinitions.Count, horizontal: true));
        for (var c = 0; c < cols - 1; c++)
            Children.Add(Splitter(row: 0, column: c * 2 + 1, rowSpan: RowDefinitions.Count, columnSpan: 1, horizontal: false));
    }

    private static GridSplitter Splitter(int row, int column, int rowSpan, int columnSpan, bool horizontal)
    {
        var splitter = new GridSplitter
        {
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = horizontal ? GridResizeDirection.Rows : GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowsPreview = false,
        };
        if (horizontal) splitter.Height = 6; else splitter.Width = 6;
        SetRow(splitter, row);
        SetColumn(splitter, column);
        SetRowSpan(splitter, rowSpan);
        SetColumnSpan(splitter, columnSpan);
        return splitter;
    }
}
