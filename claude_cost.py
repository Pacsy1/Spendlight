#!/usr/bin/env python3
"""
claude_cost.py - Tally the tokens Claude Code has spent (from its local
session logs) and estimate what that would cost at Anthropic API list prices.

For a visual dashboard, run  claude_cost_ui.py  instead.

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

Usage:
    python claude_cost.py                 # summary by model
    python claude_cost.py --by project    # or: day, session, month
    python claude_cost.py --since 2026-09-01 --until 2026-09-30
    python claude_cost.py --dir D:\\other\\.claude\\projects
"""

import argparse
import json
import os
import sys
from collections import defaultdict
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


def main():
    root = default_root()
    ap = argparse.ArgumentParser(description="Estimate Claude Code spend from local logs.")
    ap.add_argument("--dir", type=Path, default=root, help=f"projects log dir (default: {root})")
    ap.add_argument("--by", choices=["model", "project", "day", "month", "session"], default="model")
    ap.add_argument("--since", help="YYYY-MM-DD (inclusive)")
    ap.add_argument("--until", help="YYYY-MM-DD (inclusive)")
    args = ap.parse_args()

    if not args.dir.is_dir():
        sys.exit(f"Log directory not found: {args.dir}")

    groups = defaultdict(lambda: {"input": 0, "cache_w": 0, "cache_read": 0, "output": 0,
                                  "calls": 0, "cost": 0.0})
    unpriced = defaultdict(int)
    total = groups["__total__"]

    for r in read_logs(args.dir, args.since, args.until):
        u = r["u"]
        c = cost_of(r["model"], u)
        if c is None:
            unpriced[r["model"]] += 1
            c = 0.0
        key = {"model": r["model"], "project": r["project"], "day": r["day"],
               "month": r["day"][:7], "session": r["session"]}[args.by]
        for g in (groups[key], total):
            g["input"] += u["input"]
            g["cache_w"] += u["cache_5m"] + u["cache_1h"]
            g["cache_read"] += u["cache_read"]
            g["output"] += u["output"]
            g["calls"] += 1
            g["cost"] += c

    rows = [(k, v) for k, v in groups.items() if k != "__total__"]
    if args.by in ("day", "month"):
        rows.sort(key=lambda kv: kv[0])
    else:
        rows.sort(key=lambda kv: kv[1]["cost"], reverse=True)

    label = args.by.capitalize()
    width = max([len(label), 5] + [min(len(k), 60) for k, _ in rows])
    header = f"{label:<{width}}  {'Calls':>7}  {'Input':>9}  {'CacheWr':>9}  {'CacheRd':>9}  {'Output':>9}  {'Cost USD':>11}"
    print(header)
    print("-" * len(header))
    for k, v in rows + [("TOTAL", total)]:
        if k == "TOTAL":
            print("-" * len(header))
        name = k if len(k) <= 60 else "..." + k[-57:]
        print(f"{name:<{width}}  {v['calls']:>7}  {fmt_tokens(v['input']):>9}  {fmt_tokens(v['cache_w']):>9}  "
              f"{fmt_tokens(v['cache_read']):>9}  {fmt_tokens(v['output']):>9}  ${v['cost']:>10,.2f}")

    all_tokens = total["input"] + total["cache_w"] + total["cache_read"] + total["output"]
    print(f"\nTotal tokens: {all_tokens:,}   Estimated API cost: ${total['cost']:,.2f}")
    if unpriced:
        print("No price known for (counted as $0): " +
              ", ".join(f"{m} ({n} calls)" for m, n in unpriced.items()))


if __name__ == "__main__":
    main()
