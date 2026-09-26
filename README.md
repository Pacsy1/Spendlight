# Spendlight

**A spend tracker for Claude Code.** See how many tokens you've used in [Claude Code](https://claude.com/claude-code) and what that usage would cost at Anthropic API list prices, broken down by model, project, session and time.

Everything runs locally. Spendlight reads Claude Code's own session logs on your machine (`~/.claude/projects`) and never sends them anywhere.

> On a Pro or Max plan you aren't billed per token. The figures show what the same usage would cost on the API.

*Formerly known as Claude Code Spend.*

## What you get

- **Dashboard:** spend over time (bars or running total; by model, token type or project), a date range picker with presets, click-to-drill-in and drag-to-zoom, cost by model and project, where the money goes (tokens vs cost by token type), a weekday × hour heatmap, and a sortable, searchable sessions table.
- **Headline numbers:** total cost with change vs the previous period, tokens, API calls, cost per active day, cache hit rate, money saved by prompt caching and monthly pace.
- **Live:** refreshes every minute while open.
- Light and dark themes.

## Ways to run it

| | How | Needs |
|---|---|---|
| **Windows app** | `Spendlight-Setup-<version>.exe`: per-user install, no admin | Windows 10/11 (WebView2, built into Windows 11) |
| **Linux app** | `bash Spendlight-Linux-<version>.run` | x86-64 or ARM64, glibc; a Chromium-family browser for an app window (otherwise your default browser) |
| **Python, command line** | `python spendlight.py [--by model\|project\|day\|month\|session] [--since YYYY-MM-DD]` | Python 3 |
| **Python, dashboard** | `python spendlight_ui.py` → http://127.0.0.1:8765 | Python 3 |

The Windows installer has a silent mode (`/S`, optionally `--dir <path> --no-desktop --no-startmenu --launch`). The Linux installer takes `--yes`, `--system` (with sudo) and `--uninstall`. Run it with `--help` to see everything.

## How cost is calculated

Each assistant turn in the logs records `message.model` and `message.usage` (input, cache write split into 5-minute and 1-hour TTL, cache read, output, web searches, fast mode). One API response is often logged on several lines, and resumed sessions copy history into new files, so records are de-duplicated by message ID + request ID.

Prices are Anthropic's first-party API list prices per million tokens:

- **Cache writes:** 1.25× input (5-minute TTL), 2× input (1-hour TTL).
- **Cache reads:** 0.1× input, except where a model has its own rate.
- **Web search:** $10 per 1,000 searches.

The price tables live in [`spendlight.py`](spendlight.py) (Python) and [`Spendlight.Core/Pricing.cs`](Spendlight.Core/Pricing.cs) (apps). Update both when prices change or new models ship. Models without a price are counted as $0 and flagged in the UI.

## Building

Requires the **.NET 8 SDK** and **Python 3**. On Windows:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This writes `dist/Spendlight-Setup-<version>.exe` and `dist/Spendlight-Linux-<version>.run`. Set the version in [`Directory.Build.props`](Directory.Build.props).

## Project layout

```
dashboard.html              The dashboard UI, shared by every host (hand-drawn SVG charts, no dependencies)
spendlight.py               Python: log reader, pricing, command-line summary
spendlight_ui.py            Python: local server for the dashboard

Spendlight.Core/            C#: log reader, pricing, data payload, loopback server (shared)
Spendlight/                 Windows app: WinForms + WebView2 window
Spendlight.Linux/           Linux app: serves on 127.0.0.1, opens an app-style browser window

installer/windows/          Setup + Uninstall (.NET Framework 4.8 + WebView2, HTML UI)
installer/linux/            Self-extracting .run installer (bash; terminal UI or zenity dialogs) + packager
build.ps1                   Builds everything into dist/
```

## Privacy

- **Logs stay local:** they're read from disk on every refresh and never stored or uploaded.
- **Local server:** the Linux app and the Python dashboard listen on `127.0.0.1` only and reject requests whose `Host` header isn't loopback.
- **Windows app:** serves the page from memory with no network listener.

## Disclaimer

Spendlight is an independent project. It is not affiliated with, endorsed by, or sponsored by Anthropic. Claude and Claude Code are trademarks of Anthropic.
