#!/usr/bin/env python3
"""
spendlight_ui.py - Local web dashboard for Spendlight.

Reads the same logs as spendlight.py and serves an interactive dashboard
(dashboard.html) on http://127.0.0.1:8765. Everything stays on your machine:
the server only binds to localhost and only reads ~/.claude/projects.

Usage:
    python spendlight_ui.py                # opens the dashboard in your browser
    python spendlight_ui.py --port 9000 --no-open
    python spendlight_ui.py --dir D:\\other\\.claude\\projects
"""

import argparse
import json
import sys
import threading
import time
import webbrowser
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from spendlight import (
    FAST_PRICES, PRICES, WEB_SEARCH_PER_REQUEST,
    cost_breakdown, default_root, lookup_price, rates_for, read_logs,
)

HERE = Path(__file__).resolve().parent
DASHBOARD = HERE / "dashboard.html"


def _epoch(ts):
    try:
        return int(datetime.fromisoformat(ts.replace("Z", "+00:00")).timestamp())
    except (ValueError, AttributeError):
        return None


def _project_labels(paths):
    """Short, unique display names: the last path segment, or more when two collide."""
    def tail(p, n):
        parts = [x for x in p.replace("\\", "/").split("/") if x]
        return "/".join(parts[-n:]) if parts else p
    labels = {p: tail(p, 1) for p in paths}
    for n in (2, 3, 4):
        seen = {}
        for p, lab in labels.items():
            seen.setdefault(lab, []).append(p)
        clashes = [ps for ps in seen.values() if len(ps) > 1]
        if not clashes:
            break
        for ps in clashes:
            for p in ps:
                labels[p] = tail(p, n)
    return labels


def price_rows(models):
    used = {lookup_price(m)[0] for m in models}
    rows = []
    for prefix in sorted(PRICES, key=lambda k: (-PRICES[k][0], k)):
        row = {"model": prefix, "used": prefix in used, **rates_for(prefix)}
        if prefix in FAST_PRICES:
            row["fast"] = {"input": FAST_PRICES[prefix][0], "output": FAST_PRICES[prefix][1]}
        rows.append(row)
    return rows


class DataStore:
    def __init__(self, root):
        self.root = root
        self.cache = {}
        self.lock = threading.Lock()

    def payload(self):
        with self.lock:
            started = time.perf_counter()
            meta = {}
            recs = read_logs(self.root, cache=self.cache, meta=meta)
            elapsed = time.perf_counter() - started

        recs.sort(key=lambda r: r["ts"])
        models, projects, sessions = {}, {}, {}
        unpriced = {}
        rows = []
        r6 = lambda x: round(x, 6)
        for r in recs:
            t = _epoch(r["ts"])
            if t is None:
                continue
            u = r["u"]
            b = cost_breakdown(r["model"], u)
            if b is None:
                unpriced[r["model"]] = unpriced.get(r["model"], 0) + 1
                b = dict.fromkeys(("input", "cache_write", "cache_read", "output", "web", "no_cache"), 0.0)
            mi = models.setdefault(r["model"], len(models))
            pi = projects.setdefault(r["project"], len(projects))
            si = sessions.setdefault(r["session"], len(sessions))
            rows.append([
                t, mi, pi, si,
                u["input"], u["cache_5m"] + u["cache_1h"], u["cache_read"], u["output"], u["web_search"],
                r6(b["input"]), r6(b["cache_write"]), r6(b["cache_read"]), r6(b["output"]), r6(b["web"]),
                r6(b["no_cache"]), 1 if u["fast"] else 0,
            ])

        # A session belongs to the project it made the most calls in.
        sess_proj = {}
        for row in rows:
            sess_proj.setdefault(row[3], {}).setdefault(row[2], 0)
            sess_proj[row[3]][row[2]] += 1
        labels = _project_labels(list(projects))
        titles, prompts = meta.get("titles", {}), meta.get("prompts", {})

        return {
            "generatedAt": datetime.now(timezone.utc).isoformat(),
            "dir": str(self.root),
            "parseSeconds": round(elapsed, 3),
            "files": len(self.cache),
            "priceSource": "spendlight.py",
            "fields": ["t", "model", "project", "session",
                       "input", "cacheWrite", "cacheRead", "output", "webSearch",
                       "costInput", "costCacheWrite", "costCacheRead", "costOutput", "costWeb",
                       "costNoCache", "fast"],
            "models": list(models),
            "projects": [{"path": p, "label": labels[p]} for p in projects],
            "sessions": [
                {
                    "id": s,
                    "title": titles.get(s) or prompts.get(s) or "",
                    "project": max(sess_proj.get(i, {0: 0}).items(), key=lambda kv: kv[1])[0],
                }
                for s, i in sessions.items()
            ],
            "records": rows,
            "unpriced": unpriced,
            "prices": price_rows(list(models)),
            "webSearchPer1k": WEB_SEARCH_PER_REQUEST * 1000,
        }


def make_handler(store):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *args):
            pass  # keep the console quiet

        def _send(self, code, body, ctype):
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            # Loopback only: refuse other Host names so web pages can't read usage via DNS rebinding.
            host = (self.headers.get("Host") or "").rsplit(":", 1)[0].strip("[]").lower()
            if host not in ("127.0.0.1", "localhost"):
                return self._send(403, b"Forbidden", "text/plain")
            path = self.path.split("?", 1)[0]
            if path in ("/", "/index.html"):
                try:
                    body = DASHBOARD.read_bytes()
                except OSError:
                    return self._send(500, b"dashboard.html not found next to spendlight_ui.py", "text/plain")
                return self._send(200, body, "text/html; charset=utf-8")
            if path == "/api/data":
                try:
                    body = json.dumps(store.payload(), separators=(",", ":")).encode("utf-8")
                except Exception as e:  # surface parse errors in the UI instead of a dead socket
                    body = json.dumps({"error": str(e)}).encode("utf-8")
                    return self._send(500, body, "application/json")
                return self._send(200, body, "application/json")
            if path == "/api/ping":
                return self._send(200, b'{"app":"spendlight"}', "application/json")
            if path == "/favicon.ico":
                return self._send(204, b"", "image/x-icon")
            return self._send(404, b"Not found", "text/plain")

    return Handler


def main():
    root = default_root()
    ap = argparse.ArgumentParser(description="Local dashboard for Spendlight.")
    ap.add_argument("--dir", type=Path, default=root, help=f"projects log dir (default: {root})")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--no-open", action="store_true", help="don't open a browser tab")
    args = ap.parse_args()

    if not args.dir.is_dir():
        sys.exit(f"Log directory not found: {args.dir}")

    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(DataStore(args.dir)))
    url = f"http://127.0.0.1:{args.port}/"
    print(f"Spendlight dashboard: {url}   (Ctrl+C to stop)")
    if not args.no_open:
        threading.Timer(0.6, lambda: webbrowser.open(url)).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nStopped.")


if __name__ == "__main__":
    main()
