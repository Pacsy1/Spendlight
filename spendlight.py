#!/usr/bin/env python3
"""
spendlight.py - Spendlight in your terminal. One file, Python 3.8+, no dependencies.

Tallies the tokens Claude Code has spent (from its local session logs) and
estimates what that would cost at Anthropic API list prices: a summary with
charts, or tables, JSON and CSV for scripting.

    python spendlight.py                      last 30 days: summary and charts
    python spendlight.py --days 7             ...or any number of days
    python spendlight.py --all                everything in the logs
    python spendlight.py --since 2026-09-01 --until 2026-09-15
    python spendlight.py --by project         a table (model, project, session, day, week, month)
    python spendlight.py --currency EUR       any currency the European Central Bank publishes
    python spendlight.py --by day --csv       for spreadsheets; --json for scripts
    python spendlight.py --list-currencies

For the visual dashboard, run  spendlight_ui.py  (or install the Spendlight app).

Copyright (C) 2026 Pacsy1. Free software under the GNU GPL v3 or later (see LICENSE).
https://github.com/Pacsy1/spendlight

Where the data comes from
-------------------------
Claude Code writes one JSONL file per session under
    ~/.claude/projects/<encoded-project-path>/<session-id>.jsonl
(subagent transcripts live in subfolders, so the whole tree is scanned).
Every assistant turn is a line with  message.model  and  message.usage:
    input_tokens                  - uncached input
    cache_creation_input_tokens   - input written to the prompt cache
      cache_creation.ephemeral_5m_input_tokens / ephemeral_1h_input_tokens
    cache_read_input_tokens       - input served from the prompt cache
    output_tokens                 - output (includes thinking tokens)
    server_tool_use.web_search_requests
    speed                         - "standard" or "fast"
One API response is often logged as several lines (one per content block),
each repeating the same usage, so lines are de-duplicated by message id +
request id (keeping the largest numbers seen for that message). The same
de-duplication also stops resumed sessions, which copy earlier history into
a new file, from being counted twice.

Pricing (USD per million tokens, Anthropic first-party API list prices)
----------------------------------------------------------------------
Cache writes: 1.25x input for the 5-minute TTL, 2x input for the 1-hour TTL.
Cache reads: 0.1x input, except where a model has its own rate (Fable 5.1:
$0.25, Opus 5.5: $0.20). Web search: $10 per 1,000 searches.

NOTE: If you're on a Pro/Max subscription you don't pay per token - this is
what the same usage *would* cost on the API.

Currencies
----------
Other currencies use the European Central Bank's daily reference rates
(eurofxref-daily.xml), fetched only when you ask for a currency other than USD
and cached for 12 hours. Nothing about your usage is sent anywhere.
"""

import argparse
import csv
import json
import os
import re
import shutil
import sys
import time
import urllib.request
from collections import defaultdict
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

# model-id prefix -> (input, output, cache_read or None for 0.1x input)
PRICES = {
    "claude-fable-5-1":  (10.00, 50.00, 0.25),
    "claude-mythos-5-1": (10.00, 50.00, 0.25),
    "claude-fable-5":    (10.00, 50.00, 1.00),
    "claude-mythos-5":   (10.00, 50.00, 1.00),
    "claude-opus-5-5":   (4.00, 20.00, 0.20),
    "claude-opus-5":     (5.00, 25.00, None),
    "claude-opus-4-8":   (5.00, 25.00, None),
    "claude-opus-4-7":   (5.00, 25.00, None),
    "claude-opus-4-6":   (5.00, 25.00, None),
    "claude-opus-4-5":   (5.00, 25.00, None),
    "claude-opus-4-1":   (15.00, 75.00, None),
    "claude-opus-4":     (15.00, 75.00, None),
    "claude-sonnet-5":   (2.00, 10.00, None),
    "claude-sonnet-4":   (3.00, 15.00, None),   # 4, 4.5, 4.6
    "claude-3-7-sonnet": (3.00, 15.00, None),
    "claude-haiku-4-5":  (1.00, 5.00, None),
    "claude-3-5-haiku":  (0.80, 4.00, None),
    "claude-3-haiku":    (0.25, 1.25, None),
}

# Fast mode (Opus only) has its own input/output rates.
FAST_PRICES = {
    "claude-opus-5-5": (8.00, 40.00),
    "claude-opus-5":   (10.00, 50.00),
}

WEB_SEARCH_PER_REQUEST = 10.00 / 1000

TOKEN_FIELDS = ("input", "cache_5m", "cache_1h", "cache_read", "output", "web_search")


def lookup_price(model):
    """Longest matching prefix wins, so 'claude-opus-5-5' beats 'claude-opus-5'."""
    m = model.lower()
    for prefix in sorted(PRICES, key=len, reverse=True):
        if m.startswith(prefix):
            return prefix, PRICES[prefix]
    return None, None


def rates_for(model, fast=False):
    """Effective $/MTok rates for a model, or None if unknown."""
    prefix, price = lookup_price(model)
    if price is None:
        return None
    inp, out, cache_read = price
    if fast and prefix in FAST_PRICES:
        inp, out = FAST_PRICES[prefix]
    if cache_read is None:
        cache_read = inp * 0.1
    return {
        "input": inp,
        "output": out,
        "cache_write_5m": inp * 1.25,
        "cache_write_1h": inp * 2.0,
        "cache_read": cache_read,
    }


def cost_breakdown(model, u):
    """Dollar cost of one response split by token type, or None if unpriced.
    'no_cache' is what the same request would have cost without prompt caching."""
    r = rates_for(model, u["fast"])
    if r is None:
        return None
    all_input = u["input"] + u["cache_5m"] + u["cache_1h"] + u["cache_read"]
    return {
        "input": u["input"] * r["input"] / 1e6,
        "cache_write": (u["cache_5m"] * r["cache_write_5m"] + u["cache_1h"] * r["cache_write_1h"]) / 1e6,
        "cache_read": u["cache_read"] * r["cache_read"] / 1e6,
        "output": u["output"] * r["output"] / 1e6,
        "web": u["web_search"] * WEB_SEARCH_PER_REQUEST,
        "no_cache": (all_input * r["input"] + u["output"] * r["output"]) / 1e6
        + u["web_search"] * WEB_SEARCH_PER_REQUEST,
    }


def cost_of(model, u):
    b = cost_breakdown(model, u)
    if b is None:
        return None
    return b["input"] + b["cache_write"] + b["cache_read"] + b["output"] + b["web"]


def parse_usage(usage):
    created = usage.get("cache_creation_input_tokens") or 0
    split = usage.get("cache_creation") or {}
    c1h = split.get("ephemeral_1h_input_tokens") or 0
    c5m = split.get("ephemeral_5m_input_tokens") or 0
    if c1h + c5m < created:          # older logs have no TTL split: assume 5m
        c5m = created - c1h
    return {
        "input": usage.get("input_tokens") or 0,
        "cache_5m": c5m,
        "cache_1h": c1h,
        "cache_read": usage.get("cache_read_input_tokens") or 0,
        "output": usage.get("output_tokens") or 0,
        "web_search": (usage.get("server_tool_use") or {}).get("web_search_requests") or 0,
        "fast": usage.get("speed") == "fast",
    }


def _first_prompt_text(msg):
    content = msg.get("content")
    if isinstance(content, str):
        text = content
    elif isinstance(content, list):
        parts = [b.get("text", "") for b in content if isinstance(b, dict) and b.get("type") == "text"]
        if not parts:
            return None
        text = " ".join(parts)
    else:
        return None
    text = " ".join(text.split())
    # Skip Claude Code's own wrappers (slash-command echoes, reminders, etc.)
    if not text or text.startswith("<"):
        return None
    return text[:120]


def parse_file(path):
    """Parse one session log. Returns (records, titles, prompts) where titles
    and prompts map sessionId -> custom title / first user prompt."""
    records, titles, prompts = [], {}, {}
    try:
        fh = open(path, encoding="utf-8", errors="replace")
    except OSError:
        return records, titles, prompts
    with fh:
        for line in fh:
            is_usage = '"usage"' in line
            is_title = '"custom-title"' in line
            is_user = '"type":"user"' in line
            if not (is_usage or is_title or is_user):
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            kind = rec.get("type")
            sid = rec.get("sessionId") or path.stem
            if kind == "custom-title":
                if rec.get("customTitle"):
                    titles[sid] = rec["customTitle"]
                continue
            msg = rec.get("message")
            if not isinstance(msg, dict):
                continue
            if kind == "user":
                if sid not in prompts and not rec.get("isMeta"):
                    text = _first_prompt_text(msg)
                    if text:
                        prompts[sid] = text
                continue
            if not isinstance(msg.get("usage"), dict):
                continue
            model = msg.get("model") or "unknown"
            if model.startswith("<"):        # "<synthetic>" = local, not billed
                continue
            ts = rec.get("timestamp") or ""
            key = (msg.get("id"), rec.get("requestId")) if msg.get("id") else (str(path), rec.get("uuid"))
            records.append({
                "key": key,
                "model": model,
                "ts": ts,
                "day": ts[:10],
                "project": rec.get("cwd") or path.parent.name,
                "session": sid,
                "u": parse_usage(msg["usage"]),
            })
    return records, titles, prompts


def read_logs(root, since=None, until=None, cache=None, meta=None):
    """Return one de-duplicated record per API response.

    cache: optional dict reused between calls; files whose size and mtime are
           unchanged are not re-parsed.
    meta:  optional dict that receives {"titles": {...}, "prompts": {...}}.
    """
    seen = {}
    titles, prompts = {}, {}
    live_paths = set()
    for path in sorted(root.rglob("*.jsonl")):
        try:
            st = path.stat()
        except OSError:
            continue
        sig = (st.st_size, st.st_mtime_ns)
        live_paths.add(path)
        if cache is not None and path in cache and cache[path][0] == sig:
            recs, t, p = cache[path][1]
        else:
            recs, t, p = parse_file(path)
            if cache is not None:
                cache[path] = (sig, (recs, t, p))
        titles.update(t)
        for sid, text in p.items():
            prompts.setdefault(sid, text)
        for r in recs:
            if (since and r["day"] < since) or (until and r["day"] > until):
                continue
            prev = seen.get(r["key"])
            if prev:
                # Same response logged again: keep the largest counts.
                for k in TOKEN_FIELDS:
                    if r["u"][k] > prev["u"][k]:
                        prev["u"] = dict(prev["u"], **{k: r["u"][k]})
                continue
            seen[r["key"]] = dict(r)   # copy: cached records must stay untouched
    if cache is not None:
        for stale in set(cache) - live_paths:
            del cache[stale]
    if meta is not None:
        meta["titles"] = titles
        meta["prompts"] = prompts
    return list(seen.values())


def fmt_tokens(n):
    for unit, size in (("B", 1e9), ("M", 1e6), ("K", 1e3)):
        if n >= size:
            return f"{n / size:.2f}{unit}"
    return str(int(n))


def default_root():
    return Path(os.environ.get("CLAUDE_CONFIG_DIR") or Path.home() / ".claude") / "projects"


# ─────────────────────────────── currencies ───────────────────────────────

# The euro plus every currency in the ECB's daily reference rates.
CURRENCIES = {
    "USD": "United States Dollar", "EUR": "Euro", "GBP": "British Pound", "JPY": "Japanese Yen",
    "CHF": "Swiss Franc", "CAD": "Canadian Dollar", "AUD": "Australian Dollar", "NZD": "New Zealand Dollar",
    "CNY": "Chinese Yuan", "HKD": "Hong Kong Dollar", "SGD": "Singapore Dollar", "KRW": "South Korean Won",
    "INR": "Indian Rupee", "IDR": "Indonesian Rupiah", "MYR": "Malaysian Ringgit", "PHP": "Philippine Peso",
    "THB": "Thai Baht", "ILS": "Israeli New Shekel", "TRY": "Turkish Lira", "ZAR": "South African Rand",
    "BRL": "Brazilian Real", "MXN": "Mexican Peso", "SEK": "Swedish Krona", "NOK": "Norwegian Krone",
    "DKK": "Danish Krone", "ISK": "Icelandic Króna", "PLN": "Polish Złoty", "CZK": "Czech Koruna",
    "HUF": "Hungarian Forint", "RON": "Romanian Leu",
}
# How each currency is written at home: symbol, after the number?, space between? Ambiguous
# symbols get their distinct form (CA$, HK$, CN¥). Digits stay 1,234.56. Same table as the dashboard.
MONEY_STYLE = {
    "USD": ("$", 0, 0), "EUR": ("€", 1, 1), "GBP": ("£", 0, 0), "JPY": ("¥", 0, 0), "CHF": ("CHF", 0, 1),
    "CAD": ("CA$", 0, 0), "AUD": ("A$", 0, 0), "NZD": ("NZ$", 0, 0), "CNY": ("CN¥", 0, 0), "HKD": ("HK$", 0, 0),
    "SGD": ("S$", 0, 0), "KRW": ("₩", 0, 0), "INR": ("₹", 0, 0), "IDR": ("Rp", 0, 1), "MYR": ("RM", 0, 0),
    "PHP": ("₱", 0, 0), "THB": ("฿", 0, 0), "ILS": ("₪", 1, 1), "TRY": ("₺", 0, 0), "ZAR": ("R", 0, 1),
    "BRL": ("R$", 0, 1), "MXN": ("MX$", 0, 0), "SEK": ("kr", 1, 1), "NOK": ("kr", 1, 1), "DKK": ("kr.", 1, 1),
    "ISK": ("kr.", 1, 1), "PLN": ("zł", 1, 1), "CZK": ("Kč", 1, 1), "HUF": ("Ft", 1, 1), "RON": ("lei", 1, 1),
}
ZERO_DECIMALS = {"JPY", "KRW", "ISK", "HUF", "IDR"}

# What people call them, for --currency: country names (short, formal, native), ISO codes, nicknames.
ALIASES = {
    "USD": "us|usa|u.s.|u.s.a.|america|united states|united states of america|american dollar|dollar|buck|$",
    "EUR": "eu|europe|european union|eurozone|euro area|germany|deutschland|france|italy|spain|netherlands|holland|"
           "belgium|austria|ireland|portugal|finland|greece|slovakia|slovenia|estonia|latvia|lithuania|luxembourg|"
           "malta|cyprus|croatia|bulgaria|€",
    "GBP": "uk|gb|gbr|united kingdom|great britain|britain|england|scotland|wales|northern ireland|pound|"
           "pound sterling|sterling|quid|£",
    "JPY": "jp|jpn|japan|nippon|nihon|yen|¥", "CHF": "ch|che|switzerland|swiss|schweiz|suisse|liechtenstein|franc",
    "CAD": "ca|can|canada|loonie", "AUD": "au|aus|australia", "NZD": "nz|nzl|new zealand|aotearoa|kiwi",
    "CNY": "cn|chn|china|prc|people's republic of china|renminbi|rmb|yuan", "HKD": "hk|hkg|hong kong",
    "SGD": "sg|sgp|singapore", "KRW": "kr|kor|korea|south korea|republic of korea|won|₩",
    "INR": "in|ind|india|bharat|rupee|₹", "IDR": "id|idn|indonesia|rupiah", "MYR": "my|mys|malaysia|ringgit",
    "PHP": "ph|phl|philippines|pilipinas|piso", "THB": "th|tha|thailand|baht", "ILS": "il|isr|israel|shekel|sheqel|nis",
    "TRY": "tr|tur|turkey|türkiye|turkiye|lira", "ZAR": "za|zaf|rsa|south africa|rand",
    "BRL": "br|bra|brazil|brasil|real|reais", "MXN": "mx|mex|mexico|méxico|peso", "SEK": "se|swe|sweden|sverige|krona",
    "NOK": "no|nor|norway|norge", "DKK": "dk|dnk|denmark|danmark", "ISK": "is|isl|iceland|ísland",
    "PLN": "pl|pol|poland|polska|zloty", "CZK": "cz|cze|czechia|czech republic|česko|koruna",
    "HUF": "hu|hun|hungary|magyarország|magyarorszag|forint", "RON": "ro|rou|romania|românia|leu|lei",
}


def _search_key(s):
    import unicodedata
    s = "".join(ch for ch in unicodedata.normalize("NFD", s) if not unicodedata.combining(ch)).lower()
    s = re.sub(r"\s+", " ", s.replace("ł", "l").replace(".", "").replace("'", "").replace("’", "")).strip()
    return s[4:] if s.startswith("the ") else s


def resolve_currency(text):
    """'HUF', 'hungary', 'the United States of America', '€' -> ISO code, or None."""
    q = _search_key(text)
    for code, name in CURRENCIES.items():
        if q in {_search_key(t) for t in [code, name] + ALIASES.get(code, "").split("|")}:
            return code
    return None
ECB_URL = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml"


def _data_dir():
    if os.name == "nt":
        return Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local") / "Spendlight"
    return Path(os.environ.get("XDG_CACHE_HOME") or Path.home() / ".cache") / "spendlight"


def fetch_rates(max_age=12 * 3600, timeout=8):
    """The ECB's daily reference rates as {"source", "date", "base": "EUR", "rates": {code: per EUR},
    "stale"}. Cached on disk for max_age seconds; if the network is unavailable an older cached copy
    is returned with stale=True. Raises RuntimeError when there is nothing to fall back on."""
    cache = _data_dir() / "rates.json"
    cached = None
    try:
        cached = json.loads(cache.read_text(encoding="utf-8"))
        if time.time() - cached.get("fetched", 0) < max_age:
            return dict(cached, stale=False)
    except (OSError, ValueError):
        cached = None
    try:
        req = urllib.request.Request(ECB_URL, headers={"User-Agent": "Spendlight"})
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            xml = resp.read().decode("utf-8", "replace")
        day = re.search(r"time=['\"](\d{4}-\d{2}-\d{2})['\"]", xml)
        rates = {c: float(v) for c, v in re.findall(r"currency=['\"]([A-Z]{3})['\"]\s+rate=['\"]([0-9.]+)['\"]", xml)}
        if not day or "USD" not in rates:
            raise ValueError("unexpected response")
        rates["EUR"] = 1.0
        data = {"source": "European Central Bank", "date": day.group(1), "base": "EUR",
                "rates": rates, "fetched": time.time()}
        try:
            cache.parent.mkdir(parents=True, exist_ok=True)
            cache.write_text(json.dumps(data), encoding="utf-8")
        except OSError:
            pass
        return dict(data, stale=False)
    except Exception as e:  # network, TLS, parsing
        if cached:
            return dict(cached, stale=True)
        raise RuntimeError(f"couldn't get exchange rates from the European Central Bank ({e})") from e


class Money:
    """Formats US-dollar amounts in the chosen currency."""

    def __init__(self, code="USD", rate=1.0):
        self.code, self.rate = code, rate

    @classmethod
    def for_currency(cls, text):
        code = resolve_currency(text)
        if code is None:
            raise RuntimeError(f"don't know the currency '{text}' (see --list-currencies)")
        if code == "USD":
            return cls(), None
        info = fetch_rates()
        rates = info["rates"]
        if code not in rates:
            raise RuntimeError(f"the ECB doesn't publish a rate for {code} (see --list-currencies)")
        return cls(code, rates[code] / rates["USD"]), info

    def __call__(self, usd, compact=False):
        v = usd * self.rate
        dec = 0 if self.code in ZERO_DECIMALS else 2
        a, lead = abs(v), "-" if v < 0 else ""
        if compact and a >= 1000:
            for unit, size in (("B", 1e9), ("M", 1e6), ("K", 1e3)):
                if a >= size:
                    num = f"{a / size:.1f}".rstrip("0").rstrip(".") + unit
                    break
        elif 0 < a < 10 ** -dec:
            lead, num = "<", f"{10 ** -dec:.{dec}f}"
        elif compact and a >= 100:
            num = f"{a:,.0f}"
        else:
            num = f"{a:,.{dec}f}"
        sym, after, space = MONEY_STYLE.get(self.code, (self.code, 0, 1))
        gap = " " if space else ""
        return f"{lead}{num}{gap}{sym}" if after else f"{lead}{sym}{gap}{num}"


# ─────────────────────────────── terminal ───────────────────────────────

# The dashboard's categorical palette (dark variant), so models keep their colors everywhere.
SLOTS = ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"]
OTHER, MUTED, GOOD = "#8a8a8a", "#898781", "#0ca30c"


class Term:
    def __init__(self, no_color=False):
        out = sys.stdout
        try:
            "█▏▕●┤└─·–".encode(out.encoding or "ascii")
            self.unicode = True
        except (UnicodeEncodeError, LookupError):
            self.unicode = False
        self.color = (not no_color and out.isatty() and not os.environ.get("NO_COLOR")
                      and os.environ.get("TERM") != "dumb")
        if self.color and os.name == "nt":
            self.color = _enable_windows_vt()
        self.truecolor = os.environ.get("COLORTERM") in ("truecolor", "24bit") or bool(os.environ.get("WT_SESSION"))
        self.width = max(60, min(shutil.get_terminal_size((100, 24)).columns - 2, 110))
        self.block, self.dot, self.dash = ("█", "●", "–") if self.unicode else ("#", "*", "-")

    def paint(self, text, hexcolor=None, bold=False, dim=False):
        if not self.color:
            return text
        codes = []
        if bold:
            codes.append("1")
        if dim:
            codes.append("2")
        if hexcolor:
            r, g, b = (int(hexcolor[i:i + 2], 16) for i in (1, 3, 5))
            if self.truecolor:
                codes.append(f"38;2;{r};{g};{b}")
            else:  # nearest xterm-256 colour cube entry
                codes.append(f"38;5;{16 + 36 * round(r / 51) + 6 * round(g / 51) + round(b / 51)}")
        return f"\033[{';'.join(codes)}m{text}\033[0m" if codes else text

    def hbar(self, frac, width, hexcolor):
        cells = max(0.0, min(1.0, frac)) * width
        if not self.unicode:
            s = "#" * int(round(cells))
        else:
            full, rem = int(cells), int((cells - int(cells)) * 8)
            s = "█" * full + ("▏▎▍▌▋▊▉"[rem - 1] if rem else "")
            if not s and frac > 0:
                s = "▏"
        return self.paint(s, hexcolor) + " " * (width - len(s))


# Plain-ASCII stand-ins for terminals that can't show Unicode (e.g. an old Windows code page).
ASCII = str.maketrans({"·": "-", "–": "-", "…": "...", "▲": "+", "▼": "-", "●": "*", "│": "|", "└": "+", "─": "-"})


def emit(text=""):
    print(text if TERM_UNICODE else text.translate(ASCII))


TERM_UNICODE = True


def _enable_windows_vt():
    try:
        import ctypes
        k = ctypes.windll.kernel32
        h = k.GetStdHandle(-11)
        mode = ctypes.c_uint32()
        if not k.GetConsoleMode(h, ctypes.byref(mode)):
            return False
        return bool(k.SetConsoleMode(h, mode.value | 0x0004))  # ENABLE_VIRTUAL_TERMINAL_PROCESSING
    except Exception:
        return False


def vlen(s):
    return len(re.sub(r"\033\[[0-9;]*m", "", s))


def pad(s, width, right=False):
    gap = " " * max(0, width - vlen(s))
    return gap + s if right else s + gap


def clip(s, width):
    return s if len(s) <= width else s[: max(1, width - 1)] + "…"


def model_name(model):
    """claude-opus-5-5 -> Opus 5.5, claude-3-5-haiku -> Haiku 3.5."""
    s = re.sub(r"-\d{8}$", "", re.sub(r"^claude-", "", model))
    m = re.match(r"^([a-z]+)-(\d+)(?:-(\d+))?$", s)
    if m:
        return f"{m.group(1).capitalize()} {m.group(2)}" + (f".{m.group(3)}" if m.group(3) else "")
    m = re.match(r"^(\d+)-(\d+)-([a-z]+)$", s)
    if m:
        return f"{m.group(3).capitalize()} {m.group(1)}.{m.group(2)}"
    return model


# ─────────────────────────────── analysis ───────────────────────────────

def load(root):
    """All priced records as dicts with local time, costs and tokens; plus session titles and unpriced models."""
    meta = {}
    out, unpriced = [], defaultdict(int)
    for r in read_logs(root, meta=meta):
        try:
            when = datetime.fromisoformat(r["ts"].replace("Z", "+00:00")).astimezone()
        except ValueError:
            continue
        b = cost_breakdown(r["model"], r["u"])
        if b is None:
            unpriced[r["model"]] += 1
            b = dict.fromkeys(("input", "cache_write", "cache_read", "output", "web", "no_cache"), 0.0)
        u = r["u"]
        out.append({
            "when": when, "day": when.date(), "model": r["model"], "project": r["project"], "session": r["session"],
            "input": u["input"], "cache_write": u["cache_5m"] + u["cache_1h"], "cache_read": u["cache_read"],
            "output": u["output"], "web": u["web_search"], "c": b,
            "cost": b["input"] + b["cache_write"] + b["cache_read"] + b["output"] + b["web"],
        })
    out.sort(key=lambda x: x["when"])
    titles = {**meta.get("prompts", {}), **meta.get("titles", {})}
    return out, titles, dict(unpriced)


def tokens(x):
    return x["input"] + x["cache_write"] + x["cache_read"] + x["output"]


def project_label(path):
    parts = [p for p in re.split(r"[\\/]", path) if p]
    return parts[-1] if parts else path


def resolve_range(args, recs):
    today = date.today()
    if args.since or args.until:
        start = date.fromisoformat(args.since) if args.since else (recs[0]["day"] if recs else today)
        end = date.fromisoformat(args.until) if args.until else today
        label = f"{start:%b %d, %Y} – {end:%b %d, %Y}"
    elif args.all:
        start, end = (recs[0]["day"] if recs else today), today
        label = "all time"
    else:
        start, end = today - timedelta(days=args.days - 1), today
        label = f"last {args.days} days" if args.days != 1 else "today"
    return start, end, label


def group(recs, key):
    g = defaultdict(lambda: {"cost": 0.0, "tokens": 0, "calls": 0, "input": 0, "cache_write": 0,
                             "cache_read": 0, "output": 0, "sessions": set(), "first": None})
    for x in recs:
        e = g[key(x)]
        e["cost"] += x["cost"]
        e["tokens"] += tokens(x)
        e["calls"] += 1
        for k in ("input", "cache_write", "cache_read", "output"):
            e[k] += x[k]
        e["sessions"].add(x["session"])
        e["first"] = e["first"] or x["when"]
    return g


def model_colors(all_recs):
    """Color follows the model (ranked by all-time cost), like the dashboard."""
    cost = defaultdict(float)
    for x in all_recs:
        cost[x["model"]] += x["cost"]
    order = sorted(cost, key=cost.get, reverse=True)
    keep = 8 if len(order) <= 8 else 7
    return {m: (SLOTS[i] if i < keep else OTHER) for i, m in enumerate(order)}


# ─────────────────────────────── summary view ───────────────────────────────

def print_summary(t, money, fx, recs, prev, all_recs, titles, start, end, label, top, unpriced):
    W = t.width
    P = t.paint
    ln = emit
    cost = sum(x["cost"] for x in recs)
    tok = sum(tokens(x) for x in recs)
    inp_all = sum(x["input"] + x["cache_write"] + x["cache_read"] for x in recs)
    hit = sum(x["cache_read"] for x in recs) / inp_all if inp_all else 0
    saved = sum(x["c"]["no_cache"] for x in recs) - cost
    days_active = len({x["day"] for x in recs})
    sessions = {x["session"] for x in recs}

    mark = P("▂▄▆" if t.unicode else "::", SLOTS[0])
    span_text = f"{start:%b %d} – {end:%b %d, %Y}"
    ln()
    ln(f"  {mark} {P('Spendlight', bold=True)}  {P('·', MUTED)}  {P(label, MUTED)}  {P(span_text, MUTED)}")
    ln()
    headline = P(money(cost), bold=True)
    delta = ""
    if prev is not None:
        pc = sum(x["cost"] for x in prev)
        if pc > 0:
            ch = (cost - pc) / pc
            arrow = ("▲" if ch >= 0 else "▼") if t.unicode else ("+" if ch >= 0 else "-")
            delta = P(f"{arrow} {abs(ch) * 100:.0f}% vs the previous {(end - start).days + 1} days ({money(pc)})", MUTED)
        else:
            delta = P(f"no spend in the previous {(end - start).days + 1} days", MUTED)
    ln(f"  {headline}   {delta}")
    ln(f"  {P('estimated API cost', MUTED)}")
    ln()
    facts = [("tokens", fmt_tokens(tok)), ("calls", f"{len(recs):,}"), ("sessions", str(len(sessions))),
             ("active days", str(days_active)), ("cache hit", f"{hit * 100:.1f}%"),
             ("saved by caching", money(saved))]
    ln("  " + P("  ·  ", MUTED).join(f"{P(v, bold=True)} {P(k, MUTED)}" for k, v in facts))
    if fx:
        stale = " (offline: last known rates)" if fx.get("stale") else ""
        rate_note = "1 USD = {:,.4f} {} · ECB reference rate of {}{}".format(money.rate, money.code, fx["date"], stale)
        ln(f"  {P(rate_note, MUTED)}")
    if not recs:
        ln(f"\n  {P('No Claude Code usage in this range.', MUTED)}\n")
        return

    # Daily spend: one column per day (or week, when the range is long).
    ln()
    ln(f"  {P('Spend over time', bold=True)}")
    n_days = (end - start).days + 1
    span = 1 if n_days <= W - 14 else 7
    buckets = []
    d = start
    while d <= end:
        buckets.append((d, min(d + timedelta(days=span - 1), end)))
        d += timedelta(days=span)
    by_day = defaultdict(float)
    for x in recs:
        by_day[x["day"]] += x["cost"]
    values = [sum(by_day[a + timedelta(days=i)] for i in range((b - a).days + 1)) for a, b in buckets]
    colw = 2 if len(values) * 2 <= W - 14 else 1
    height = 7
    peak = max(values) or 1
    levels = " ▁▂▃▄▅▆▇█" if t.unicode else " ..::||##"
    axis_w = max(len(money(peak, True)), 2) + 1
    for row in range(height, 0, -1):
        line = ""
        for v in values:
            fill = v / peak * height - (row - 1)
            ch = levels[8] if fill >= 1 else levels[max(1, int(round(fill * 8)))] if fill > 0 else " "
            line += ch + (" " if colw == 2 else "")
        lab = money(peak, True) if row == height else ""
        ln(f"  {P(lab.rjust(axis_w), MUTED)} {P('│' if t.unicode else '|', MUTED)}{P(line, SLOTS[0])}")
    ln(f"  {P('0'.rjust(axis_w), MUTED)} {P(('└' + '─' * (len(values) * colw)) if t.unicode else ('+' + '-' * (len(values) * colw)), MUTED)}")
    first, last = f"{buckets[0][0]:%b %d}", f"{buckets[-1][0]:%b %d}"
    gap = len(values) * colw - len(first) - len(last)
    ln(f"  {' ' * (axis_w + 2)}{P(first + ' ' * max(1, gap) + last, MUTED)}" + (P("   (weekly)", MUTED) if span == 7 else ""))
    best = max(range(len(values)), key=values.__getitem__)
    busiest = "Busiest " + ("week of " if span == 7 else "") + f"{buckets[best][0]:%a %b %d}: {money(values[best])}"
    ln(f"  {P(busiest, MUTED)}")

    colors = model_colors(all_recs)
    name_w = 22
    bar_w = max(10, W - name_w - 36)

    # By model
    ln()
    ln(f"  {P(pad('By model', name_w + bar_w + 4), bold=True)}{P(pad('cost', 11, True) + pad('share', 8, True) + pad('tokens', 9, True), MUTED)}")
    g = group(recs, lambda x: x["model"])
    rows = sorted(g.items(), key=lambda kv: kv[1]["cost"], reverse=True)
    mx = rows[0][1]["cost"] or 1
    for m, e in rows[:top]:
        name = clip(model_name(m), name_w - 2)
        share = "{:.0f}%".format(e["cost"] / cost * 100) if cost else "–"
        ln(f"  {P(t.dot, colors.get(m, OTHER))} {pad(name, name_w - 2)}  {t.hbar(e['cost'] / mx, bar_w, colors.get(m, OTHER))}  "
           f"{pad(money(e['cost']), 11, True)}{pad(share, 8, True)}{pad(fmt_tokens(e['tokens']), 9, True)}")

    # Where the money goes
    ln()
    ln(f"  {P('Where the money goes', bold=True)}  {P('share of tokens vs share of cost', MUTED)}")
    kinds = [("Output", "output", "output", SLOTS[0]), ("Cache read", "cache_read", "cache_read", SLOTS[1]),
             ("Cache write", "cache_write", "cache_write", SLOTS[2]), ("Input", "input", "input", SLOTS[3])]
    half = max(8, (W - name_w - 30) // 2)
    for lab, tk, ck, col in kinds:
        kt = sum(x[tk] for x in recs)
        kc = sum(x["c"][ck] for x in recs)
        tshare, cshare = (kt / tok if tok else 0), (kc / cost if cost else 0)
        ln(f"  {P(t.dot, col)} {pad(lab, name_w - 2)}  {t.hbar(tshare, half, col)} {pad(f'{tshare * 100:.0f}%', 4, True)} tokens   "
           f"{t.hbar(cshare, half, col)} {pad(f'{cshare * 100:.0f}%', 4, True)} cost")
    web = sum(x["c"]["web"] for x in recs)
    if web:
        ln(f"  {P(t.dot, SLOTS[4])} {pad('Web search', name_w - 2)}  {money(web)}")

    # Projects
    ln()
    ln(f"  {P(pad('Top projects', name_w + bar_w + 4), bold=True)}{P(pad('cost', 11, True) + pad('sessions', 10, True), MUTED)}")
    g = group(recs, lambda x: x["project"])
    rows = sorted(g.items(), key=lambda kv: kv[1]["cost"], reverse=True)
    mx = rows[0][1]["cost"] or 1
    for p, e in rows[:top]:
        ln(f"    {pad(clip(project_label(p), name_w - 2), name_w - 2)}  {t.hbar(e['cost'] / mx, bar_w, SLOTS[0])}  "
           f"{pad(money(e['cost']), 11, True)}{pad(str(len(e['sessions'])), 10, True)}")
    if len(rows) > top:
        ln(f"    {P(f'… and {len(rows) - top} more (see --by project)', MUTED)}")

    # Sessions
    ln()
    title_w = max(20, W - 48)
    ln(f"  {P(pad('Priciest sessions', title_w + 4), bold=True)}{P(pad('started', 14) + pad('calls', 7, True) + pad('cost', 12, True), MUTED)}")
    g = group(recs, lambda x: x["session"])
    rows = sorted(g.items(), key=lambda kv: kv[1]["cost"], reverse=True)
    proj_of = {x["session"]: x["project"] for x in recs}
    for s, e in rows[:top]:
        title = titles.get(s) or "Untitled ({})".format(s[:8])
        proj = clip(project_label(proj_of[s]), 18)
        room = title_w - 2                       # always leave a gap before the date column
        name = clip(title, max(8, room - len(proj) - 3)) + P(" · " + proj, MUTED)
        calls = "{:,}".format(e["calls"])
        ln(f"    {pad(name, title_w)}{pad(e['first'].strftime('%b %d %H:%M'), 14)}{pad(calls, 7, True)}{pad(money(e['cost']), 12, True)}")

    ln()
    if unpriced:
        missing = ", ".join("{} ({} calls)".format(m, n) for m, n in unpriced.items())
        ln(f"  {P('No price known for: ' + missing + ' — counted as 0.', '#e66767')}")
    ln(f"  {P('Estimated at Anthropic API list prices. On a Pro or Max plan you are not billed per token.', MUTED)}")
    ln()


# ─────────────────────────────── table / csv / json ───────────────────────────────

def table_rows(recs, by, titles):
    keyf = {
        "model": lambda x: x["model"],
        "project": lambda x: x["project"],
        "session": lambda x: x["session"],
        "day": lambda x: x["day"].isoformat(),
        "week": lambda x: (x["day"] - timedelta(days=x["day"].weekday())).isoformat(),
        "month": lambda x: x["day"].strftime("%Y-%m"),
    }[by]
    g = group(recs, keyf)
    rows = []
    for k, e in g.items():
        name = {"model": model_name, "project": project_label}.get(by, lambda v: v)(k)
        if by == "session":
            name = titles.get(k) or k[:8]
        rows.append({"key": k, "name": name, "calls": e["calls"], "tokens": e["tokens"], "input": e["input"],
                     "cache_write": e["cache_write"], "cache_read": e["cache_read"], "output": e["output"],
                     "cost_usd": round(e["cost"], 6)})
    if by in ("day", "week", "month"):
        rows.sort(key=lambda r: r["key"])
    else:
        rows.sort(key=lambda r: r["cost_usd"], reverse=True)
    return rows


def print_table(t, money, rows, by):
    P = t.paint
    total = sum(r["cost_usd"] for r in rows) or 1
    name_w = min(max([len(by)] + [len(str(r["name"])) for r in rows]), max(20, t.width - 72)) + 2
    cols = [("Calls", 8), ("Tokens", 9), ("Input", 9), ("Cache wr", 9), ("Cache rd", 9), ("Output", 9), ("Cost", 13), ("Share", 7)]
    head = pad(by.capitalize(), name_w) + "".join(pad(c, w, True) for c, w in cols)
    emit(P(head, bold=True))
    emit(P(t.dash * vlen(head), MUTED))

    def line(name, r):
        vals = [f"{r['calls']:,}", fmt_tokens(r["tokens"]), fmt_tokens(r["input"]), fmt_tokens(r["cache_write"]),
                fmt_tokens(r["cache_read"]), fmt_tokens(r["output"]), money(r["cost_usd"]),
                f"{r['cost_usd'] / total * 100:.1f}%"]
        return pad(clip(str(name), name_w - 2), name_w) + "".join(pad(v, w, True) for v, (_, w) in zip(vals, cols))

    for r in rows:
        emit(line(r["name"], r))
    tot = {k: sum(r[k] for r in rows) for k in ("calls", "tokens", "input", "cache_write", "cache_read", "output", "cost_usd")}
    emit(P(t.dash * vlen(head), MUTED))
    emit(P(line("Total", tot), bold=True))


# ─────────────────────────────── main ───────────────────────────────

def main():
    root = default_root()
    ap = argparse.ArgumentParser(
        prog="spendlight",
        description="Spendlight: what your Claude Code usage would cost at Anthropic API list prices.",
        epilog="Examples: spendlight.py --days 7 | --all --currency EUR | --by project | --by day --csv > days.csv")
    ap.add_argument("--days", type=int, default=30, help="how many days back, including today (default 30)")
    ap.add_argument("--all", action="store_true", help="everything in the logs")
    ap.add_argument("--since", metavar="YYYY-MM-DD", help="first day (inclusive)")
    ap.add_argument("--until", metavar="YYYY-MM-DD", help="last day (inclusive)")
    ap.add_argument("--by", choices=["model", "project", "session", "day", "week", "month"], help="print a table grouped by this")
    ap.add_argument("--currency", default="USD", metavar="CODE", help="show costs in this currency (ECB rates): a code like EUR or a country like 'hungary'")
    ap.add_argument("--top", type=int, default=6, help="rows per section in the summary (default 6)")
    ap.add_argument("--json", action="store_true", help="machine-readable output")
    ap.add_argument("--csv", action="store_true", help="CSV table (implies --by day unless --by is given)")
    ap.add_argument("--no-color", action="store_true", help="plain text, no colors")
    ap.add_argument("--list-currencies", action="store_true", help="list supported currencies and exit")
    ap.add_argument("--dir", type=Path, default=root, help=f"Claude Code logs folder (default: {root})")
    args = ap.parse_args()

    if args.list_currencies:
        for code, name in CURRENCIES.items():
            print(f"{code}  {name}")
        print("\n--currency also takes country names, e.g. --currency hungary or --currency \"united states\".")
        return
    if args.days < 1:
        ap.error("--days must be at least 1")
    if not args.dir.is_dir():
        sys.exit(f"spendlight: Claude Code logs not found at {args.dir} (use --dir)")
    try:
        money, fx = Money.for_currency(args.currency)
    except RuntimeError as e:
        sys.exit(f"spendlight: {e}")

    t = Term(args.no_color or args.json or args.csv)
    global TERM_UNICODE
    TERM_UNICODE = t.unicode
    all_recs, titles, unpriced = load(args.dir)
    try:
        start, end, label = resolve_range(args, all_recs)
    except ValueError:
        ap.error("dates must look like 2026-09-01")
    recs = [x for x in all_recs if start <= x["day"] <= end]
    span = (end - start).days + 1
    prev = None if args.all else [x for x in all_recs if start - timedelta(days=span) <= x["day"] < start]
    currency = {"code": money.code, "per_usd": money.rate, **({"rates_date": fx["date"], "source": fx["source"]} if fx else {})}

    if args.csv or (args.by and not args.json):
        rows = table_rows(recs, args.by or "day", titles)
        if args.csv:
            w = csv.writer(sys.stdout, lineterminator="\n")
            w.writerow([args.by or "day", "calls", "tokens", "input", "cache_write", "cache_read", "output", f"cost_{money.code.lower()}"])
            for r in rows:
                w.writerow([r["name"], r["calls"], r["tokens"], r["input"], r["cache_write"], r["cache_read"], r["output"],
                            round(r["cost_usd"] * money.rate, 4)])
        else:
            emit(t.paint(f"\nSpendlight · {label} ({start:%b %d} – {end:%b %d, %Y})\n", MUTED))
            print_table(t, money, rows, args.by)
            emit()
        return

    if args.json:
        cost = sum(x["cost"] for x in recs)
        out = {
            "range": {"from": start.isoformat(), "to": end.isoformat(), "label": label},
            "currency": currency,
            "cost": round(cost * money.rate, 4),
            "tokens": sum(tokens(x) for x in recs),
            "calls": len(recs),
            "sessions": len({x["session"] for x in recs}),
            "by": {by: [dict(r, cost=round(r["cost_usd"] * money.rate, 4)) for r in table_rows(recs, by, titles)]
                   for by in ([args.by] if args.by else ["model", "project", "session", "day"])},
            "unpriced_models": unpriced,
        }
        json.dump(out, sys.stdout, indent=2, default=str)
        print()
        return

    print_summary(t, money, fx, recs, prev, all_recs, titles, start, end, label, max(1, args.top), unpriced)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(130)
    except BrokenPipeError:  # e.g. piped into `head`
        sys.stderr.close()
