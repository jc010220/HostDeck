// HostDeck Terminal – xterm.js in WebView2. Bridge-Nachrichten:
//   C# → JS: {t:'o', d} Ausgabe · {t:'paste', d} · {t:'theme', theme} · {t:'font', size} · {t:'focus'}
//   JS → C#: {t:'ready', cols, rows} · {t:'i', d} Eingabe · {t:'r', cols, rows} · {t:'copy', d}
//            {t:'paste-request'} · {t:'menu', sel} · {t:'link', uri} · {t:'shortcut', key} · {t:'font', size}
(function () {
  'use strict';
  const bridge = window.chrome && window.chrome.webview;
  const send = (m) => { if (bridge) bridge.postMessage(m); else (window.__hostdeckOutbox = window.__hostdeckOutbox || []).push(m); };

  const themes = {
    dark: { background: '#1e1e1e', foreground: '#d4d4d4', cursor: '#aeafad', selectionBackground: '#264f78',
            black: '#000000', red: '#cd3131', green: '#0dbc79', yellow: '#e5e510', blue: '#2472c8', magenta: '#bc3fbc', cyan: '#11a8cd', white: '#e5e5e5',
            brightBlack: '#666666', brightRed: '#f14c4c', brightGreen: '#23d18b', brightYellow: '#f5f543', brightBlue: '#3b8eea', brightMagenta: '#d670d6', brightCyan: '#29b8db', brightWhite: '#e5e5e5' },
    light: { background: '#fbfbfb', foreground: '#1e1e1e', cursor: '#1e1e1e', selectionBackground: '#add6ff',
             black: '#000000', red: '#cd3131', green: '#107c10', yellow: '#946c00', blue: '#0451a5', magenta: '#bc05bc', cyan: '#0598bc', white: '#555555',
             brightBlack: '#666666', brightRed: '#e51400', brightGreen: '#14ce14', brightYellow: '#b5a000', brightBlue: '#0451a5', brightMagenta: '#bc05bc', brightCyan: '#0598bc', brightWhite: '#a5a5a5' },
  };

  const term = new Terminal({
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, "Courier New", monospace',
    fontSize: 14,
    cursorBlink: true,
    scrollback: 10000,
    allowProposedApi: true,
    // Doppelklick markiert ein Wort: auch an < > | ; = trennen ⇒ aus „<interface>“ wird nur „interface“ kopiert
    wordSeparator: ' ()[]{}<>\'",`|;=',
    theme: themes.dark,
  });
  const fit = new FitAddon.FitAddon();
  const search = new SearchAddon.SearchAddon();
  term.loadAddon(fit);
  term.loadAddon(search);
  term.loadAddon(new WebLinksAddon.WebLinksAddon((event, uri) => send({ t: 'link', uri })));
  term.loadAddon(new Unicode11Addon.Unicode11Addon());
  term.unicode.activeVersion = '11';

  const container = document.getElementById('terminal');
  term.open(container);
  const doFit = () => { try { fit.fit(); } catch (e) { /* unsichtbar */ } };
  doFit();
  new ResizeObserver(doFit).observe(container);

  term.onData((d) => send({ t: 'i', d }));
  term.onBinary((d) => send({ t: 'i', d }));
  term.onResize(({ cols, rows }) => send({ t: 'r', cols, rows }));

  // MobaXterm-Verhalten: Markieren kopiert, Rechtsklick fügt ein
  term.onSelectionChange(() => { const s = term.getSelection(); if (s) send({ t: 'copy', d: s }); });
  // Einfügen läuft IMMER über C# (Rückfrage bei mehreren Zeilen) – auch Strg+V, Umschalt+Einfg und Browser-Paste
  let lastPasteRequest = 0;
  const requestPaste = (src) => { const now = Date.now(); if (now - lastPasteRequest < 400) return; lastPasteRequest = now; send({ t: 'paste-request', src: src || 'key' }); };
  // Rechtsklick: Menü in HostDeck (Kopieren, Einfügen, Suchen …) – bzw. direkt einfügen, wenn so eingestellt
  container.addEventListener('contextmenu', (e) => { e.preventDefault(); send({ t: 'menu', sel: term.getSelection() || '' }); });
  container.addEventListener('paste', (e) => { e.preventDefault(); e.stopPropagation(); requestPaste(); }, true);
  container.addEventListener('drop', (e) => { e.preventDefault(); e.stopPropagation(); }, true); // kein Text per Drag & Drop

  // Strg+Mausrad: Schriftgröße
  container.addEventListener('wheel', (e) => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    const size = Math.min(32, Math.max(8, term.options.fontSize + (e.deltaY < 0 ? 1 : -1)));
    term.options.fontSize = size; doFit(); send({ t: 'font', size });
  }, { passive: false });

  // Tastenkürzel: App-Kürzel an C# weiterreichen (Fokus liegt im WebView2-HWND)
  const appShortcuts = new Set(['ctrl+shift+k', 'ctrl+shift+p', 'ctrl+tab', 'ctrl+shift+tab', 'ctrl+shift+w', 'ctrl+shift+m', 'ctrl+shift+l', 'ctrl+shift+d',
    'ctrl+alt+arrowright', 'ctrl+f', 'ctrl+shift+b', 'ctrl+,', 'ctrl+alt+l']);
  term.attachCustomKeyEventHandler((e) => {
    if (e.type !== 'keydown') return true;
    const key = [e.ctrlKey && 'ctrl', e.altKey && 'alt', e.shiftKey && 'shift', e.key.toLowerCase()].filter(Boolean).join('+');
    // Layout: Strg+Umschalt+1…4 (e.key wäre mit Umschalt „!“, „"“ … ⇒ physische Taste)
    if (e.ctrlKey && e.shiftKey && !e.altKey && /^Digit[1-4]$/.test(e.code)) { e.preventDefault(); send({ t: 'shortcut', key: 'ctrl+shift+' + e.code.slice(5) }); return false; }
    if (key === 'ctrl+shift+c') { const s = term.getSelection(); if (s) send({ t: 'copy', d: s }); return false; }
    if (key === 'ctrl+shift+v') { e.preventDefault(); requestPaste(); return false; }
    if (key === 'ctrl+shift+f') { openSearch(); return false; }
    if (appShortcuts.has(key)) { e.preventDefault(); send({ t: 'shortcut', key }); return false; }
    return true;
  });

  // Suche
  const bar = document.getElementById('search');
  const input = document.getElementById('searchInput');
  const opts = { caseSensitive: false, decorations: { matchOverviewRuler: '#888', activeMatchColorOverviewRuler: '#ff0' } };
  function openSearch() { bar.classList.add('open'); input.focus(); input.select(); }
  function closeSearch() { bar.classList.remove('open'); search.clearDecorations(); term.focus(); }
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.shiftKey ? search.findPrevious(input.value, opts) : search.findNext(input.value, opts); e.preventDefault(); }
    if (e.key === 'Escape') { closeSearch(); e.preventDefault(); }
  });
  document.getElementById('searchNext').onclick = () => search.findNext(input.value, opts);
  document.getElementById('searchPrev').onclick = () => search.findPrevious(input.value, opts);
  document.getElementById('searchClose').onclick = closeSearch;

  function onMessage(m) {
    switch (m.t) {
      case 'o': term.write(m.d); break;
      case 'paste': term.paste(m.d); break;                       // respektiert Bracketed-Paste
      case 'theme': term.options.theme = themes[m.theme] || themes.dark; document.body.style.background = term.options.theme.background; break;
      case 'font': term.options.fontSize = m.size; doFit(); break;
      case 'focus': term.focus(); break;
      case 'search': openSearch(); break;
      case 'clear': term.clear(); term.focus(); break;              // Verlauf leeren, aktuelle Zeile bleibt
      case 'reset': term.reset(); term.focus(); break;
      case 'select-all': term.selectAll(); break;
      case 'copy-all': {
        const b = term.buffer.active, lines = [];
        for (let i = 0; i < b.length; i++) { const l = b.getLine(i); if (l) lines.push(l.translateToString(true)); }
        while (lines.length && lines[lines.length - 1] === '') lines.pop();
        send({ t: 'copy', d: lines.join('\n') });
        break;
      }
    }
  }
  if (bridge) bridge.addEventListener('message', (ev) => onMessage(ev.data));
  window.__hostdeck = { onMessage, term };                         // für Tests

  term.focus();
  send({ t: 'ready', cols: term.cols, rows: term.rows });
})();
