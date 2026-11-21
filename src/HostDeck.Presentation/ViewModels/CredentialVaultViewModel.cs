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
        Selected = Items.FirstOrDefault();
    }

    public ObservableCollection<Credential> Items { get; } = [];

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
        Selected = c;
    }

    [RelayCommand]
    public void Add()
    {
        var c = _tree.Vault.Add(new Credential { Name = "New credentials" });
        Items.Add(c);
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
        Selected = Items.FirstOrDefault();
        return users.Count;
    }

    public int UsageCount(Credential c) => _tree.Root.Descendants().Count(n => n.Settings.CredentialId == c.Id);
}
