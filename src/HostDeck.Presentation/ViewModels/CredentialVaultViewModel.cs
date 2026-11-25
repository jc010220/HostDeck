using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HostDeck.Core.Model;

namespace HostDeck.Presentation.ViewModels;

/// <summary>Credential-Vault verwalten: zentrale Zugangsdaten, die Ordnern/Verbindungen zugewiesen werden.</summary>
public sealed partial class CredentialVaultViewModel : ObservableObject
{
    private readonly ConnectionTree _tree;

    public CredentialVaultViewModel(ConnectionTree tree)
    {
        _tree = tree;
        foreach (var c in tree.Vault.Items.OrderBy(c => c.Name)) Items.Add(c);
        RefreshVisible();
        Selected = Items.FirstOrDefault();
    }

    public ObservableCollection<Credential> Items { get; } = [];

    /// <summary>Nach <see cref="Filter"/> gefilterte Einträge (Anzeige in der Liste).</summary>
    public ObservableCollection<Credential> VisibleItems { get; } = [];

    /// <summary>Suche in Name, Benutzer, Domäne und Notizen (Groß-/Kleinschreibung egal, alle Wörter müssen passen).</summary>
    [ObservableProperty] private string _filter = "";

    partial void OnFilterChanged(string value) => RefreshVisible();

    public static bool Matches(Credential c, string filter)
    {
        var words = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var text = $"{c.Name} {c.Username} {c.Domain} {c.DisplayUser} {c.Notes}";
        return words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshVisible()
    {
        var selected = Selected;
        VisibleItems.Clear();
        foreach (var c in Items.Where(c => Matches(c, Filter))) VisibleItems.Add(c);
        if (selected is not null && VisibleItems.Contains(selected)) Selected = selected;
        else if (selected is not null || VisibleItems.Count > 0) Selected = VisibleItems.FirstOrDefault();
    }

    [ObservableProperty] private Credential? _selected;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _domain = "";
    [ObservableProperty] private string _privateKey = "";
    [ObservableProperty] private string _notes = "";
    /// <summary>Neues Passwort/Passphrase aus PasswordBox; <c>null</c> = unverändert.</summary>
    public string? NewPassword { get; set; }
    public string? NewPassphrase { get; set; }

    public bool HasSelection => Selected is not null;
    public string UsageText => Selected is null ? "" : $"Used by {UsageCount(Selected)} folder(s)/connection(s).";

    partial void OnSelectedChanged(Credential? value)
    {
        Name = value?.Name ?? "";
        Username = value?.Username ?? "";
        Domain = value?.Domain ?? "";
        PrivateKey = value?.PrivateKey ?? "";
        Notes = value?.Notes ?? "";
        NewPassword = null;
        NewPassphrase = null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(UsageText));
    }

    /// <summary>Formularwerte in den ausgewählten Eintrag schreiben.</summary>
    [RelayCommand]
    public void Save()
    {
        if (Selected is not { } c) return;
        c.Name = string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name.Trim();
        c.Username = Username.Trim();
        c.Domain = Domain.Trim();
        c.PrivateKey = string.IsNullOrWhiteSpace(PrivateKey) ? null : PrivateKey.Trim() + "\n";
        c.Notes = Notes;
        if (!string.IsNullOrEmpty(NewPassword)) c.Password = NewPassword;
        if (!string.IsNullOrEmpty(NewPassphrase)) c.PrivateKeyPassphrase = NewPassphrase;
        var index = Items.IndexOf(c);
        Items[index] = c; // Liste aktualisieren
        var visible = VisibleItems.IndexOf(c);
        if (visible >= 0) VisibleItems[visible] = c;
        Selected = c;
    }

    [RelayCommand]
    public void Add()
    {
        var c = _tree.Vault.Add(new Credential { Name = "New credentials" });
        Items.Add(c);
        Filter = ""; // neuen Eintrag sicher anzeigen
        if (!VisibleItems.Contains(c)) VisibleItems.Add(c);
        Selected = c;
    }

    /// <summary>Entfernt den Eintrag; Verweise darauf werden gelöst (Knoten erben dann wieder).</summary>
    public int Remove()
    {
        if (Selected is not { } c) return 0;
        var users = _tree.Root.Descendants().Where(n => n.Settings.CredentialId == c.Id).ToList();
        foreach (var n in users) n.Settings.CredentialId = null;
        _tree.Vault.Remove(c.Id);
        Items.Remove(c);
        VisibleItems.Remove(c);
        Selected = VisibleItems.FirstOrDefault();
        return users.Count;
    }

    /// <summary>Mehrere Einträge auf einmal entfernen; liefert die Zahl gelöster Verweise.</summary>
    public int RemoveMany(IReadOnlyCollection<Credential> credentials)
    {
        var ids = credentials.Select(c => c.Id).ToHashSet();
        var users = _tree.Root.Descendants().Where(n => n.Settings.CredentialId is { } id && ids.Contains(id)).ToList();
        foreach (var n in users) n.Settings.CredentialId = null;
        foreach (var c in credentials)
        {
            _tree.Vault.Remove(c.Id);
            Items.Remove(c);
            VisibleItems.Remove(c);
        }
        Selected = VisibleItems.FirstOrDefault();
        return users.Count;
    }

    public int UsageCount(Credential c) => _tree.Root.Descendants().Count(n => n.Settings.CredentialId == c.Id);
}
