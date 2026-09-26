#!/usr/bin/env python3
"""
Builds the self-extracting Linux installer:  install.sh + a .tar.gz payload appended after a
__PAYLOAD_BELOW__ marker line.

Payload layout:
  bin/x64/claude-spend      bin/arm64/claude-spend     (0755)
  share/claude-spend.svg    share/claude-spend-{48,64,128,256}.png

Usage: python package.py --version 1.0.0 --build ../../build --ico ../../ClaudeSpend/app.ico --out ../../dist/X.run
"""

import argparse
import hashlib
import io
import struct
import tarfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent


def ico_pngs(path):
    """Yield (size, png_bytes) for PNG-compressed entries in a .ico file."""
    data = Path(path).read_bytes()
    _, kind, count = struct.unpack_from("<HHH", data, 0)
    assert kind == 1, "not an .ico file"
    for i in range(count):
        w, h, _, _, _, _, size, offset = struct.unpack_from("<BBBBHHII", data, 6 + 16 * i)
        blob = data[offset:offset + size]
        if blob[:8] == b"\x89PNG\r\n\x1a\n":
            yield (w or 256), blob


def add(tar, name, blob, mode):
    info = tarfile.TarInfo(name)
    info.size = len(blob)
    info.mode = mode
    info.mtime = int(time.time())
    info.uid = info.gid = 0
    info.uname = info.gname = ""
    tar.addfile(info, io.BytesIO(blob))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True)
    ap.add_argument("--build", required=True, help="folder containing linux-x64/ and linux-arm64/")
    ap.add_argument("--ico", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    build = Path(args.build)
    binaries = {arch: (build / f"linux-{arch}" / "claude-spend").read_bytes() for arch in ("x64", "arm64")}
    for arch, blob in binaries.items():
        assert blob[:4] == b"\x7fELF", f"linux-{arch}/claude-spend is not a Linux executable"

    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz", compresslevel=9) as tar:
        for arch, blob in binaries.items():
            add(tar, f"bin/{arch}/claude-spend", blob, 0o755)
        add(tar, "share/claude-spend.svg", (HERE / "claude-spend.svg").read_bytes(), 0o644)
        for size, png in ico_pngs(args.ico):
            if size in (48, 64, 128, 256):
                add(tar, f"share/claude-spend-{size}.png", png, 0o644)
    payload = buf.getvalue()

    script = (HERE / "install.sh").read_text(encoding="utf-8").replace("\r\n", "\n")
    size_mb = round(max(len(b) for b in binaries.values()) / 1048576)
    script = (script.replace("@VERSION@", args.version)
                    .replace("@SHA256@", hashlib.sha256(payload).hexdigest())
                    .replace("@SIZE_MB@", str(size_mb)))
    if not script.endswith("\n"):
        script += "\n"

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(script.encode("utf-8") + b"__PAYLOAD_BELOW__\n" + payload)
    print(f"{out}  ({out.stat().st_size / 1048576:.1f} MB, payload sha256 {hashlib.sha256(payload).hexdigest()[:12]}…)")


if __name__ == "__main__":
    main()
