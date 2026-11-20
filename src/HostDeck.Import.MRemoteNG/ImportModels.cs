using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG.Crypto;

namespace HostDeck.Import.MRemoteNG;

/// <summary>Anfrage an die UI: "Bitte Master-Passwort für Datei X eingeben".</summary>
public sealed record MasterPasswordRequest(string? SourceName, int Attempt, int MaxAttempts, bool PreviousAttemptFailed);

public enum CredentialExtractionMode
{
    /// <summary>Zugangsdaten bleiben inline an den Knoten (1:1 wie in mRemoteNG).</summary>
    Disabled,
    /// <summary>Identische Kombinationen (User/Domain/Passwort), die an ≥ 2 Knoten vorkommen, wandern in den Vault.</summary>
    SharedOnly,
    /// <summary>Alle explizit gesetzten Kombinationen wandern in den Vault.</summary>
    All,
}

public sealed class ConfConsImportOptions
{
    /// <summary>Bereits bekanntes Master-Passwort (z.B. CLI/Tests). Wird nach dem Standardschlüssel probiert.</summary>
    public string? MasterPassword { get; init; }

    /// <summary>
    /// Callback für die Passwortabfrage (UI-Dialog). Rückgabe <c>null</c>/leer = Abbruch.
    /// Wird nur aufgerufen, wenn weder der Standardschlüssel noch <see cref="MasterPassword"/> passen.
    /// </summary>
    public Func<MasterPasswordRequest, string?>? MasterPasswordProvider { get; init; }

    public int MaxPasswordAttempts { get; init; } = 3;

    public CredentialExtractionMode CredentialExtraction { get; init; } = CredentialExtractionMode.Disabled;

    /// <summary>Nicht typisiert abgebildete Attribute in <see cref="TreeNode.Extensions"/> aufbewahren.</summary>
    public bool PreserveUnknownAttributes { get; init; } = true;
}

/// <summary>Metadaten der importierten Datei.</summary>
public sealed record ConfConsFileInfo
{
    public required double ConfVersion { get; init; }
    public required string CipherDescription { get; init; }
    public ConfConsCipherEngine? Engine { get; init; }
    public ConfConsCipherMode? Mode { get; init; }
    public int? KdfIterations { get; init; }
    public bool FullFileEncryption { get; init; }
    public bool LegacyFullFileEncryption { get; init; }
    public bool DpapiProtected { get; init; }
    /// <summary>True, wenn ein eigenes Master-Passwort (nicht "mR3m") verwendet wurde.</summary>
    public bool CustomMasterPassword { get; init; }
}

public sealed record ImportStatistics(int Folders, int Connections, int SecretsDecrypted, int SecretsFailed, int CredentialsExtracted);

public sealed class ConfConsImportResult
{
    public required ConnectionTree Tree { get; init; }
    public required ConfConsFileInfo File { get; init; }
    public required ImportStatistics Statistics { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>Datei ist kein (unterstütztes) confCons.xml.</summary>
public sealed class ConfConsFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Master-Passwort falsch oder Abfrage abgebrochen.</summary>
public sealed class ConfConsAuthenticationException(string message, bool cancelled) : Exception(message)
{
    public bool Cancelled { get; } = cancelled;
}
