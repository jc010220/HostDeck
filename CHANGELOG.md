# Changelog

## 1.1.0

First feature release after 1.0 – everything since 1.0.0 in short:

- **Libraries:** shared `confCons.xml` with KeePass-style sync (Ctrl+S), several libraries at once (teams or your own
  file on OneDrive), read-only mode for jump hosts, shared snippets, team backups next to the file, library passwords.
- **Security:** independent security reviews with all findings fixed (see [SECURITY.md](SECURITY.md)); personal logins
  bound to confirmed targets; auto-lock (off by default, Ctrl+Alt+L); build provenance attestations and SHA-256 checksums for every release.
- **Terminal:** right-click menu (copy, paste, find, copy all, clear, reset, reconnect, duplicate, duplicate in split
  view), paste protection, font size from settings, new sessions get the keyboard right away.
- **RDP:** Ctrl+Alt+Esc releases the keyboard from a session; Ctrl+Shift+K, Ctrl+Alt+L, Ctrl+Alt+→ work from inside a session.
- **Windows & workspace:** detached windows restored with position, window placement remembered, split view
  duplicates (Ctrl+Shift+D), collapsible sidebar that pops up for Ctrl+L / Ctrl+F.
- **Backups:** personal backups with GFS retention and restore, team backups with a team-wide rule.
- **Usability:** quick search Ctrl+K, settings with side navigation (Ctrl+,), quick connect protocol picker,
  three-state theme, English UI.
