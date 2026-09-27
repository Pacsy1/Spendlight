# Spendlight

See how many tokens you've used in [Claude Code](https://claude.com/claude-code) and what that usage would cost at Anthropic API list prices, broken down by model, project, session and time.

Everything runs locally. It reads Claude Code's own session logs on your machine (`~/.claude/projects`), and your usage never leaves it.

> On a Pro or Max plan you aren't billed per token. The figures show what the same usage would cost on the API.

## What you get

- **Dashboard:** spend over time (bars or running total; by model, token type or project), a date range picker with presets, click-to-drill-in and drag-to-zoom, cost by model and project, where the money goes (tokens vs cost by token type), a weekday × hour heatmap, and a sortable, searchable sessions table.
- **Headline numbers:** total cost with change vs the previous period, tokens, API calls, cost per active day, cache hit rate, money saved by prompt caching and monthly pace.
- **Any currency:** costs in US dollars or any of 29 other currencies, converted at the European Central Bank's daily reference rates.
- **Live:** refreshes every minute while open.
- Light and dark themes.

## Ways to run it

| | How | Needs |
|---|---|---|
| **Windows app** | `Spendlight-Setup-<version>.exe`: per-user install, no admin | Windows 10/11 (WebView2, built into Windows 11) |
| **Linux app** | `bash Spendlight-Linux-<version>.run` | x86-64 or ARM64, glibc; a Chromium-family browser for an app window (otherwise your default browser) |
| **Terminal** | `python spendlight.py` | Python 3.8+, nothing else |
| **Python, dashboard** | `python spendlight_ui.py` → http://127.0.0.1:8765 | Python 3.8+ |

### In the terminal

[`spendlight.py`](spendlight.py) is a single file with no dependencies: copy it anywhere and run it. By default it prints a summary of the last 30 days, with charts drawn in text:

```bash
python spendlight.py                        # last 30 days
python spendlight.py --days 7               # or --all, or --since 2026-09-01 --until 2026-09-15
python spendlight.py --currency EUR         # any currency the ECB publishes (--list-currencies)
python spendlight.py --by project           # a table: model, project, session, day, week or month
python spendlight.py --by day --csv > days.csv
python spendlight.py --json                 # for scripts
```

Colours turn off automatically when the output isn't a terminal (or with `--no-color` / `NO_COLOR`), and terminals that can't show Unicode get plain ASCII.

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
spendlight.py              Python: log reader, pricing, currencies, the terminal edition
spendlight_ui.py           Python: local server for the dashboard

Spendlight.Core/           C#: log reader, pricing, data payload, loopback server (shared)
Spendlight/                Windows app: WinForms + WebView2 window
Spendlight.Linux/          Linux app: serves on 127.0.0.1, opens an app-style browser window

installer/windows/          Setup + Uninstall (.NET Framework 4.8 + WebView2, HTML UI)
installer/linux/            Self-extracting .run installer (bash; terminal UI or zenity dialogs) + packager
build.ps1                   Builds everything into dist/
```

## Verifying a download

Releases are built by [GitHub Actions](.github/workflows/release.yml) from the tagged source. Each installer carries a signed [build provenance attestation](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations), which proves it was built from this repository by that workflow and hasn't been changed since:

```bash
gh attestation verify Spendlight-Setup-<version>.exe --repo Pacsy1/spendlight
```

Each release also lists SHA-256 checksums in `SHA256SUMS-<version>.txt`.

## Exactly what the installers do

Nothing is hidden, and all of it is in [`installer/`](installer/):

**Windows setup** (runs as you, never as administrator):
- **Files:** copies `Spendlight.exe`, `Uninstall.exe` and `LICENSE.txt` into `%LOCALAPPDATA%\Programs\Spendlight` (or the folder you choose).
- **Shortcuts:** adds Start menu and desktop shortcuts, if you ticked them.
- **Registry:** adds one entry under `HKCU\…\Uninstall\Spendlight`, so it appears in Settings › Apps.
- **Temp files:** unpacks the WebView2 DLLs it needs to draw its window into `%TEMP%\SpendlightSetup`.
- **Updates:** if an older copy is running, asks it to close (like clicking its X) and never force-kills anything.
- **Uninstalling:** copies itself to `%TEMP%` and runs from there, the same approach as Inno Setup and NSIS. That's how the installed `Uninstall.exe` can be removed. It deletes only the files above, by exact name, and deletes the folder only if it's then empty.

**Linux installer:** puts the app in `~/.local/share/spendlight`, links `spendlight` and `spendlight-uninstall` into `~/.local/bin`, and adds a menu entry and icon. It verifies its own SHA-256 checksum before unpacking. Use `--extract DIR` to inspect the contents without installing.

**The app** only reads files under `~/.claude/projects`. It makes one kind of internet request, and only if you pick a currency other than USD: it downloads the European Central Bank's public exchange-rate file ([eurofxref-daily.xml](https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml)), at most every 12 hours. That request sends nothing about you or your usage.

## Code signing

`build.ps1` signs the app, the uninstaller and the setup (inner files first, so everything the installer drops is signed too) whenever a code-signing certificate is configured. With none configured, it builds unsigned.

| Variable | Meaning |
|---|---|
| `SPENDLIGHT_SIGN_THUMBPRINT` | Thumbprint of a code-signing certificate in your Windows certificate store (works with hardware tokens and cloud-token certificates that show up there) |
| `SPENDLIGHT_SIGN_PFX` / `SPENDLIGHT_SIGN_PFX_PASSWORD` | Or: a `.pfx` file and its password |
| `SPENDLIGHT_TIMESTAMP_URL` | RFC 3161 timestamp server (default `http://timestamp.digicert.com`) |

## License

Copyright © 2026 Pacsy1

Spendlight is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details. You should have received a copy of the GNU General Public License along with this program (see [LICENSE](LICENSE)). If not, see <https://www.gnu.org/licenses/>.

## Privacy

- **Logs stay local:** they're read from disk on every refresh and never stored or uploaded.
- **Local server:** the Linux app and the Python dashboard listen on `127.0.0.1` only and reject requests whose `Host` header isn't loopback.
- **Windows app:** serves the page from memory with no network listener.
- **Exchange rates:** fetched from the European Central Bank only when you choose a currency other than USD, cached for 12 hours, and reused when you're offline. Nothing is sent with the request.
