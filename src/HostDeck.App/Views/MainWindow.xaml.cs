using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using HostDeck.Presentation.ViewModels;
using Wpf.Ui.Controls;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace HostDeck.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        SidebarColumn.Width = new GridLength(Math.Clamp(viewModel.Settings.SidebarWidth, 200, 700));
        Workspace.OpenInPane = viewModel.OpenInPane;
        RegisterShortcuts();
    }

    private void RegisterShortcuts()
    {
        void Bind(Key key, ModifierKeys modifiers, Action action) =>
            InputBindings.Add(new KeyBinding(new RelayCommand(action), key, modifiers));

        Bind(Key.F, ModifierKeys.Control, () => { SearchBox.Focus(); SearchBox.SelectAll(); });
        Bind(Key.L, ModifierKeys.Control, () => QuickConnectBox.Focus());
        Bind(Key.O, ModifierKeys.Control, () => _vm.ImportMRemoteNGCommand.Execute(null));
        Bind(Key.N, ModifierKeys.Control, () => _vm.NewConnectionCommand.Execute(null));
        Bind(Key.N, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.NewFolderCommand.Execute(null));
        Bind(Key.W, ModifierKeys.Control, () => _vm.Workspace.Close(_vm.Workspace.ActiveTab));
        Bind(Key.Tab, ModifierKeys.Control, () => _vm.Workspace.SelectNextTab());
        Bind(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.Workspace.SelectNextTab(backwards: true));
        Bind(Key.Right, ModifierKeys.Control | ModifierKeys.Alt, () => _vm.Workspace.FocusNextPane());
        Bind(Key.D1, ModifierKeys.Control | ModifierKeys.Alt, () => _vm.Workspace.SetLayout(WorkspaceLayout.Single));
        Bind(Key.D2, ModifierKeys.Control | ModifierKeys.Alt, () => _vm.Workspace.SetLayout(WorkspaceLayout.TwoColumns));
        Bind(Key.D3, ModifierKeys.Control | ModifierKeys.Alt, () => _vm.Workspace.SetLayout(WorkspaceLayout.TwoRows));
        Bind(Key.D4, ModifierKeys.Control | ModifierKeys.Alt, () => _vm.Workspace.SetLayout(WorkspaceLayout.Grid));
        Bind(Key.M, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.ToggleMultiExecutionCommand.Execute(null));
    }

    // ---- Quick-Connect / Suche ----------------------------------------------------------------------

    private void OnQuickConnectKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !QuickConnectBox.IsDropDownOpen)
        {
            _vm.QuickConnectCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.QuickConnectText = "";
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _vm.SessionTree.FilterText = "";
                e.Handled = true;
                break;
            case Key.Down: // in die Trefferliste springen
                var first = _vm.SessionTree.AllItems.FirstOrDefault(i => i.IsVisible && (i.IsMatch || !i.IsFolder));
                if (first is not null) first.IsSelected = true;
                SessionTreeView.Focus();
                e.Handled = true;
                break;
            case Key.Enter: // ersten Treffer öffnen
                var match = _vm.SessionTree.AllItems.FirstOrDefault(i => i.IsVisible && !i.IsFolder);
                if (match is not null) _vm.OpenCommand.Execute(match);
                e.Handled = true;
                break;
        }
    }

    // ---- Session-Tree --------------------------------------------------------------------------------

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _vm.SessionTree.SelectedItem = e.NewValue as TreeItemViewModel;

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Doppelklick nur auf Verbindungen auswerten; Ordner klappen per Standardverhalten auf/zu
        if (_vm.SessionTree.SelectedItem is { IsFolder: false } item && e.OriginalSource is FrameworkElement { DataContext: TreeItemViewModel clicked } && clicked == item)
        {
            _vm.OpenCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        var item = _vm.SessionTree.SelectedItem;
        if (item is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                _vm.OpenCommand.Execute(item);
                e.Handled = true;
                break;
            case Key.Delete:
                ConfirmDelete(item);
                e.Handled = true;
                break;
        }
    }

    private void OnMenuOpen(object sender, RoutedEventArgs e) => _vm.OpenCommand.Execute(_vm.SessionTree.SelectedItem);

    private void OnMenuOpenTiled(object sender, RoutedEventArgs e)
    {
        var item = _vm.SessionTree.SelectedItem;
        _vm.OpenAllTiledCommand.Execute(item?.IsFolder == true ? item : item?.Parent);
    }

    private void OnMenuDelete(object sender, RoutedEventArgs e)
    {
        if (_vm.SessionTree.SelectedItem is { } item) ConfirmDelete(item);
    }

    private void ConfirmDelete(TreeItemViewModel item)
    {
        var what = item.IsFolder ? $"den Ordner „{item.Name}“ mit allen Einträgen" : $"die Verbindung „{item.Name}“";
        var result = System.Windows.MessageBox.Show(this, $"Soll {what} gelöscht werden?", "Löschen",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result == MessageBoxResult.Yes) _vm.DeleteCommand.Execute(item);
    }

    private void OnSecurityMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } element)
        {
            menu.PlacementTarget = element;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    private void OnSidebarResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _vm.Settings.SidebarWidth = SidebarColumn.ActualWidth;
        _vm.SaveSettings();
    }
}
