using System.Windows;
using HostDeck.Presentation.Snippets;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

public partial class SnippetDialog : FluentWindow
{
    private readonly Snippet _snippet;

    /// <summary>„Share with the team“ gewählt.</summary>
    public bool Shared { get; private set; }

    public SnippetDialog(Snippet snippet, bool teamAvailable = false)
    {
        _snippet = snippet;
        InitializeComponent();
        ShareBox.Visibility = teamAvailable ? Visibility.Visible : Visibility.Collapsed;
        ShareBox.IsChecked = snippet.IsTeam;
        NameBox.Text = snippet.Name;
        CategoryBox.Text = snippet.Category;
        CommandBox.Text = snippet.Command.Replace("\r", Environment.NewLine);
        ExecuteBox.IsChecked = snippet.Execute;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(CommandBox.Text)) return;
        _snippet.Name = NameBox.Text.Trim();
        _snippet.Category = CategoryBox.Text.Trim();
        _snippet.Command = CommandBox.Text.Replace("\r\n", "\n").TrimEnd('\n');
        _snippet.Execute = ExecuteBox.IsChecked == true;
        Shared = ShareBox.IsChecked == true;
        DialogResult = true;
    }
}
