# Security

HostDeck stores server credentials. This document explains how they are protected, what HostDeck does **not**
protect against, and how to report a vulnerability. It describes the current code – if something here and the code
disagree, that is a bug; please report it.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's private reporting instead:
*Security* tab of this repository → **Report a vulnerability**. You will get an answer as soon as possible; fixes are
released through the normal update channel and mentioned in the release notes.

## Threat model in short

| Scenario | Protected? |
|---|---|
| Someone copies `connections.hdk` + `connections.hdk.key` (stolen laptop disk, backup, cloud sync, share) | **Yes** – without the master password the data is AES-256 ciphertext; every password guess costs an Argon2id computation (64 MiB, 3 passes). |
| Someone gets an automatic or manual backup (`HostDeck-Personal-*.hdk`) | **Yes** – backups are byte copies of the encrypted database and need the master password valid at backup time. Since 1.0.35 their key file never contains the "remember on this PC" (DPAPI) slot. |
| Malicious server: manipulated file names via SFTP, huge VNC frames, terminal escape/link tricks, invalid certificates | **Mitigated** – see "Network and protocol hardening". |
| Manipulated `confCons.xml` (import or team file): XXE, entity expansion, absurd KDF parameters, oversized file | **Mitigated** – DTDs and external entities are prohibited, KDF iterations ≤ 100,000, files ≤ 128 MB. |
| A team member with write access changes a team entry to point to their own server, weakens its RDP security, swaps a shared snippet or removes the file password | **Mitigated** – see "Libraries". Shared passwords stored in the team file are visible to all team members by design. |
| Someone with write access to the update share (`\\server\software\HostDeck`) | **No** – until releases are signed, a share or HTTPS update source is trusted completely. Make the share writable for administrators only, or use the GitHub source. |
| Malware or another person running code **as your Windows user while HostDeck is unlocked** | **No.** Such code can read process memory, log keystrokes or take screenshots. No password manager can prevent that. |
| "Remember on this PC" (DPAPI) enabled and someone can run code as your Windows user | **No** – the database then unlocks without the master password for that Windows account by design. Leave it off on shared or high-risk machines. |
| Administrator / SYSTEM on the machine, physical access to an unlocked session | **No.** |

## Data at rest

Files in `%APPDATA%\HostDeck\`:

```
master password ──Argon2id (64 MiB, t=3, p=4, 128-bit random salt)──► KEK (256 bit)
KEK ──AES-256-GCM──► DEK (random 256 bit) ──► SQLCipher v4 raw key (connections.hdk)
optional: Windows DPAPI (CurrentUser, app-specific entropy) ──► DEK ("remember on this PC")
```

* `connections.hdk` – SQLite database encrypted with SQLite3 Multiple Ciphers in **SQLCipher v4** format (AES-256-CBC
  per page, HMAC-SHA512 page authentication). Without the key it is indistinguishable from random data.
* `connections.hdk.key` – JSON with the Argon2id parameters and salt and the AES-GCM-wrapped DEK. It contains no
  secret in clear. Tampering is detected (GCM tag); weakened KDF parameters are never reused when the password is changed.
* Changing the master password re-wraps the same database key (DEK). Old copies of the key file still open with the **old**
  password. After a suspected compromise of the password, create a new database (export → new database → import) and delete old backups.
* There is **no back door** and no recovery. "Forgot password" renames the old files instead of deleting them.
* Not secret and stored in plain text: `settings.json` (window layout, paths, update source – **no** passwords or tokens)
  and the log in `%LOCALAPPDATA%\HostDeck\logs` (never contains passwords, keys or tokens).

**How strong is it without the master password?** It is as strong as the master password. Argon2id makes each guess
cost memory and time (roughly 0.1–0.3 s per guess per CPU core), which defeats GPU farms against long passwords but not
against "Summer2026". Use a passphrase of several words (15+ characters).

## Data in memory (while unlocked)

Honest status – .NET and the protocol libraries limit what is possible:

* **Keys:** the KEK is zeroed right after use (`CryptographicOperations.ZeroMemory`). The DEK lives in a `byte[]` that is
  zeroed when the database is closed. Since 1.0.33 the database key is handed to SQLite as bytes (`sqlite3_key`) and
  never becomes a .NET string. SQLite keeps its own copy in native memory while the database is open.
* **Passwords of connections** are decrypted into .NET `string` objects when the database is loaded and stay in memory
  while HostDeck runs. Strings are immutable and are only removed by the garbage collector; they can appear in crash
  dumps or the page file. This cannot be avoided completely because the libraries HostDeck hands them to require
  strings: SSH.NET, the Windows RDP ActiveX control (`ClearTextPassword`) and WebView2 (HTTP authentication).
* The master password is read from a WPF `PasswordBox` and exists briefly as a string; derived bytes are zeroed.
* **Auto-lock** (off by default; *Settings → Behavior*) hides all HostDeck windows, open message boxes and RDP full-screen
  windows until the master password is entered. It is a screen lock: sessions keep running and decrypted data stays in memory.
* HostDeck does **not** write crash dumps itself. Windows Error Reporting may – disable full dumps on sensitive machines.

## Libraries (shared `confCons.xml`)

* The team file stays in mRemoteNG format: each password is encrypted with AES-256-GCM, key from PBKDF2-HMAC-SHA1 of the
  team password (10,000 iterations when the password is set in HostDeck). **Without a team password, mRemoteNG uses a
  publicly known default key** – then only the share permissions protect the passwords. HostDeck warns about this.
* Team snippets: separate file, AES-256-GCM with PBKDF2-HMAC-SHA256 (210,000 iterations) from the team password.
* Several libraries can be connected (e.g. one per team, or a personal file on OneDrive); each has its own password,
  read-only flag, snippets and backups. The rules below apply to each of them.
* Everyone with write access to the share is trusted to edit team entries. Mitigations against a malicious or
  compromised colleague:
  * personal vault logins are bound to the confirmed target (protocol, host, port, RD Gateway – and, for RDP, whether
    server authentication or NLA/CredSSP were turned off) and are never sent to a new or changed target without explicit
    confirmation. Editing a folder (e.g. assigning your login to it) never confirms targets you did not change yourself;
  * a shared snippet is shown and must be confirmed before its first run and after every change of its command;
    an unencrypted snippet file is rejected when the library has a password; timestamps from the file are capped;
  * if the file suddenly has no password although one is known, HostDeck stops syncing and asks – it never silently
    re-saves all passwords with mRemoteNG's public default key;
  * in folder mode (jump host), a different "newest .xml" is only used after confirmation;
  * personal logins are never written to the team file; conflicting edits are shown; earlier versions are kept
    (team backups, last 10 versions in the personal DB).
* Writing uses a lock file (`*.hostdeck-lock` containing user, machine and UTC time). The lock is held only for the
  few seconds of a write; a lock older than two minutes (crashed PC, lost VPN) is treated as stale and broken. Before
  replacing the file HostDeck re-reads it and merges again if someone else saved in between; the file is replaced atomically.

## Network and protocol hardening

* **SSH:** host keys are verified (trust on first use with fingerprint dialog, clear warning and rejection on change –
  also when the server suddenly presents a different key *type*, so a downgrade to an unknown key is not shown as "first use");
  SFTP uses the same confirmed key. A stored password is sent only once (no retry loop until `MaxAuthTries`).
* **SFTP downloads:** server file names are sanitized (no path separators, reserved device names incl. `COM¹`, control or
  invisible Unicode format characters such as right-to-left override),
  existing files are never overwritten, downloads get the Mark of the Web (SmartScreen/Office warn).
* **RDP:** server certificate is verified (warn); NLA/CredSSP on; drives/printers/ports are not redirected by default.
  If a requested protection (certificate check, Restricted Admin, Remote Credential Guard) cannot be enabled, the
  connection is not started.
* **Telnet/HTTP:** stored passwords are only sent over these unencrypted protocols after explicit consent per entry,
  host and port; raw TCP never sends credentials.
* **Terminal (WebView2):** own WebView2 profile; navigation is locked to the embedded page, messages are only accepted
  from that origin, no host objects, links open only `http(s)` in the default browser. Every paste (Ctrl+V, Shift+Insert,
  right-click, Ctrl+Shift+V) goes through HostDeck; multi-line text (any line break, including bare CR and Unicode
  separators) asks first, bracketed-paste markers are removed, dropping text is blocked.
* **Web tabs (WebView2):** separate WebView2 profile from the terminal; **no** WebMessage channel, **no** host objects,
  no dev tools, only `http(s)`, no external protocol handlers, stored credentials only for the configured host/port/scheme.
  Certificate exceptions are bound to host, port and SHA-256 fingerprint and last until HostDeck is closed.
* **VNC:** clipboard sharing is off by default; all rectangles from the server (including CopyRect sources) are bounds-checked; resource limits (desktop ≤ 16384 px per side and ≤ 50 M pixels,
  ZRLE blocks ≤ 64 MB, clipboard ≤ 1 MB). VNC itself is unencrypted – use it over VPN or SSH tunnels.
* **mRemoteNG import:** `DtdProcessing.Prohibit`, `XmlResolver = null` (XXE, entity expansion), size and KDF limits.
  Exports contain passwords only if you set an export password, which also encrypts the whole file.
* **Quick connect** never stores passwords in its history (`user:password@host` is rejected).

## Supply chain and updates

* Every release is built from a public commit by the GitHub Actions workflow in `.github/workflows/release.yml` –
  nothing is built on a developer machine. Each release lists the workflow run and commit. Workflow actions are pinned to
  commit SHAs, permissions are minimal per job, and inputs never reach scripts directly.
* Each release has `SHA256SUMS.txt` and a **build provenance attestation**. Verify a download with:
  ```
  gh attestation verify HostDeck-win-Setup.exe --repo jc010220/HostDeck
  ```
* Builds are deterministic (`ContinuousIntegrationBuild`, `Deterministic`); the self-contained installer is not yet
  guaranteed to be bit-for-bit reproducible.
* **Code signing is not yet in place.** The workflow signs automatically once a certificate (PFX or Azure Trusted
  Signing) is configured. Until then Windows SmartScreen shows "unknown publisher".
* Automatic updates (Velopack) accept only HTTPS, GitHub or network-share sources (no plain HTTP). Packages are checked
  against the SHA-256 in the release feed – which comes from the same source, so it protects against damaged downloads,
  **not** against someone who can write to the source. A publisher signature check will follow once releases are signed;
  until then keep an update share writable for administrators only.
* Dependencies are pinned centrally (`Directory.Packages.props`); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
  The bundled SQLite is 3.54.1 (CVE-2025-6965 fixed).

## Recommendations for companies

1. Set a library password (select the library folder → gear menu → *Set library password*) and restrict write access on the share.
2. Keep personal admin logins (RADIUS, domain accounts) in each user's vault, not in the team file.
3. Use a long master password; leave "remember on this PC" off on shared machines and jump hosts.
4. Point personal backups to another drive or share.
5. Verify downloads (`gh attestation verify`) until releases are code-signed; keep an update share writable for administrators only.
