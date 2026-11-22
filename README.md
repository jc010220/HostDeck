# HostDeck

Connection manager for Windows 11: mRemoteNG-compatible, with MobaXterm-style usability.
RDP · SSH (+ SFTP) · Telnet · raw TCP · VNC · HTTP/HTTPS · encrypted database · quick search.

## Installation

**Recommended:** run `HostDeck-win-Setup.exe` from the [latest release](../../releases/latest).
It installs per user (no admin rights needed), adds a Start menu entry and can update itself automatically.
Alternatively, extract `HostDeck-win-Portable.zip` (no automatic updates).

As long as the installer is not code-signed, Windows SmartScreen shows "Unknown publisher" →
*More info* → *Run anyway* (see [Code signing](#code-signing)).

On first start, set a master password (at least 8 characters, optionally "remember on this PC").
If mRemoteNG is installed, HostDeck offers to import `%APPDATA%\mRemoteNG\confCons.xml`.

## Features

| Area | |
|---|---|
| **Protocols** | RDP (native Windows client, live resolution, DPI scaling, RD Gateway, Restricted Admin), SSH (password/key/OTP, host key verification), Telnet (auto-login for network devices), raw TCP, VNC (built-in client: ZRLE/Hextile), HTTP/HTTPS (WebView2, login from the vault) |
| **Usability** | Quick search **Ctrl+K** (fuzzy over name, IP, folder, tags and commands; only commands with `>`), quick connect `ssh user@host` (protocol picker next to the field; known ports like `:3389`/`:22`/`:5900` select RDP/SSH/VNC automatically), filter, drag & drop, favorites, recently used, duplicate connections |
| **Windows** | Tabs, split view 1/2/2/4 with movable splitters, drag tabs into their own windows (second monitor), save workspaces and restore them on startup, collapsible sidebar |
| **Terminal** | xterm.js: colors, Unicode, search, zoom, select = copy, right-click = paste, syntax highlighting (errors/warnings/IPs), multi-execution, snippets with placeholders |
| **SFTP** | Sidebar automatically follows `cd` in the terminal, upload via drag & drop, download, rename, delete |
| **Team** | Shared team library (`confCons.xml` on a network share) with KeePass-style sync (**Ctrl+S**), read-only mode for jump hosts |
| **Security** | AES-256 encrypted database (SQLCipher v4) with Argon2id, credential vault with search, secure RDP defaults, import security check, no plain-text passwords on disk |
| **mRemoteNG** | Import including password decryption (all formats 2.5–2.9, master password, full-file encryption), export back |
| **Operations** | Online status in the tree, backups, automatic updates (network share/HTTP/GitHub), error log, theme System/Dark/Light |

### Keyboard shortcuts

| Key | Action |
|---|---|
| **Ctrl+K** (in the terminal Ctrl+Shift+K) | Quick search: Enter connects, Ctrl+Enter opens in split view, Alt+Enter shows in tree |
| Ctrl+, | Settings |
| Ctrl+L / Ctrl+F | Quick connect / filter the tree |
| F2 / F4 / Del | Rename / properties / delete |
| Ctrl+S | Sync team library |
| Ctrl+D (in the tree) | Duplicate connection: properties open right away so you only adjust name and host |
| Ctrl+N / Ctrl+Shift+N | New connection / new folder |
| Ctrl+Tab, Ctrl+W (terminal: Ctrl+Shift+W) | Switch / close tab |
| Ctrl+Alt+1…4 | Layout |
| Ctrl+Shift+M | Multi-execution |
| Ctrl+Shift+B | Show/hide the sidebar (also via the leftmost toolbar button) |
| Esc / Ctrl+C (terminal), "Cancel" in the status bar | Cancel a connection attempt |
| Terminal: Ctrl+Shift+C/V/F, Ctrl+mouse wheel | Copy / paste / search / zoom |
| Vault: Ctrl+F | Search credentials |

## Team library (shared confCons.xml)

Like a shared KeePass database: everyone works on the same `confCons.xml` on the network share.

- **Setup:** Ctrl+K → "Connect team library …" (or *Settings → Team library*). A **Team** folder appears in the tree.
- **Changes by others** are pulled in automatically (checked every 15 s); your own unsaved changes are kept.
- **Your changes** are saved with **Ctrl+S** (or the "Team" toolbar button; the badge shows pending changes).
  HostDeck merges per entry, so nobody's work is overwritten. If two people changed the *same* entry differently,
  HostDeck asks which version to keep. An entry deleted by one person and changed by another is kept.
- **Safe writes:** a lock file prevents simultaneous writes, the file is replaced atomically, and the previous version is
  backed up encrypted in the HostDeck database (last 10). Deleting the Team folder only disconnects; the file stays.
- **Personal logins (e.g. RADIUS):** create them once in the vault and assign them to a team folder or entry. The
  assignment stays local and is never written to the shared file. Shared passwords in the file keep working.
- **Jump host without access to the share:** copy the file via RDP into a fixed folder (e.g. `C:\HostDeck\Team`) and set
  that folder as the team library with "Read-only"; the newest `.xml` in it is loaded automatically.
- **Team snippets:** tick "Share with the team" in the snippet dialog. They are stored next to the team file
  (`<name>.hostdeck-snippets.json`, encrypted with the team password if one is set) and saved immediately – no Ctrl+S
  needed. Personal snippets stay in your own database. On a jump host, copy this file together with the XML.
- **Personal folders:** right-click the tree → "New personal folder (top level, only for me)", or click the empty area of
  the tree first (removes the selection) and then "New folder". Everything outside the Team folder is personal.
- **mRemoteNG-compatible:** the file's format, encryption and master password are preserved. Note: mRemoteNG itself does
  not merge; when a colleague saves with mRemoteNG, the file is overwritten with their version.
- **Important:** without its own master password, mRemoteNG uses a publicly known key, so anyone with read access can read
  the passwords in the file. Protect it via Ctrl+K → "Set team file password …" (in mRemoteNG: root node "Connections" →
  property "Password protect"); everyone is asked for it once. Personal logins belong in the vault.

## Security

- Database: SQLCipher (AES-256), key derived from the master password with Argon2id (64 MiB, 3 passes); optionally also
  bound to the Windows user (DPAPI, "remember on this PC").
- No secrets in `settings.json` or in the log; the update token and team password are stored encrypted in the database.
- SSH: host keys are verified (warning on change); SFTP uses the same confirmed key. A stored password is sent only once.
- SFTP downloads: file names from the server are sanitized (no writing outside the target folder), existing files are
  never overwritten, downloads are marked as "from the internet" (SmartScreen warns for programs).
- RDP: the server certificate is verified (warning); drives, printers and ports are not redirected by default.
- Terminal: multi-line paste asks for confirmation; links open only http(s) in the default browser.
- Web tabs: only http(s), no external protocol handlers, stored credentials are only sent to the configured device; over
  plain HTTP only after explicit consent. Certificate exceptions are bound to host, port and the exact certificate.
- Personal vault logins in team entries are bound to a target you confirmed (protocol, host, port and RD Gateway). The first
  use per entry and every change of the target require explicit confirmation before your login is sent.
- Telnet sends a stored password only after consent per entry; raw TCP never sends credentials automatically.
- RDP: if a requested protection (certificate check, Restricted Admin, Remote Credential Guard) cannot be enabled, the
  connection is not started.
- mRemoteNG export: without an export password no passwords are written; with one, the whole file is encrypted.
- Quick connect never stores passwords in the history; VNC clipboard sharing is off by default (explicit "Paste clipboard",
  opt-in "Receive clipboard"); resource limits for VNC servers and imported/team files (max. 128 MB).
- Web certificate exceptions apply to exactly one certificate on one host and port until HostDeck is closed (never saved).
- Updates: HTTPS, GitHub or network share only (no plain HTTP). Releases are not code-signed yet – see [Code signing](#code-signing).

## Data and backups

`%APPDATA%\HostDeck\connections.hdk` + `connections.hdk.key` (always back up both), settings in `settings.json`,
error log in `%LOCALAPPDATA%\HostDeck\logs`.

- **Automatic backups** (*Settings → Backups*, on by default): once a day an encrypted copy of database + key file is written
  to the backup folder (default `%APPDATA%\HostDeck\backups`; a network share, another drive or OneDrive is better).
- **Retention (grandfather-father-son):** keeps the newest backup of each of the last 7 days, 4 weeks and 12 months
  (adjustable) and deletes older automatic backups. The newest backup is always kept; only automatic backups (`HostDeck-AutoBackup_*`)
  are ever deleted.
- **Manual backup:** *Create backup …* suggests `HostDeck-Backup_yyyyMMdd.hdk`; these files are never touched by the retention.
- Restore: close HostDeck and copy the two files as `connections.hdk` / `connections.hdk.key` to `%APPDATA%\HostDeck`. They open
  with the master password that was valid at backup time.

## Updates in a company

1. Create a release: GitHub → *Actions* → *Release* → *Run workflow* (enter a version), or push a tag like `v1.0.1`.
   GitHub Actions builds the installer and update packages and publishes them as a release.
2. Either point HostDeck directly at the GitHub repository (*Settings → Updates*; for a private repository also enter a
   fine-grained token with "Contents: Read-only"), or copy the release files (`RELEASES`, `releases.win.json`, `*.nupkg`,
   `HostDeck-win-Setup.exe`) to a share such as `\\server\software\HostDeck` and enter that share.
3. Updates are downloaded in the background and installed on exit.

## Code signing

The release workflow signs automatically as soon as one of these is configured as a repository secret:
`SIGN_PFX_BASE64` + `SIGN_PFX_PASSWORD` (code signing certificate) **or** `AZURE_TRUSTED_SIGNING_JSON`
(+ `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `AZURE_TENANT_ID`) for Azure Trusted Signing.

## Development

```bash
dotnet test
```

```bash
dotnet run --project src/HostDeck.App
```

Architecture: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md). Integration tests against real servers via
`HOSTDECK_TEST_SSH*` and `HOSTDECK_TEST_VNC*` (see the test classes).
