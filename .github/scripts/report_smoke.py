"""Post the simulator smoke test's evidence as GitHub annotations.

Artifacts and raw logs need a signed-in GitHub session to download, but
annotations are public through the checks API, so this makes a failed launch
diagnosable from anywhere: app stdout/stderr, OpenUtau's own log file, the
crash report (exception + crashed thread) and the simulator syslog tail.

usage: python3 report_smoke.py SMOKE_DIR
"""
import glob
import json
import os
import sys

root = sys.argv[1]


def esc(s: str) -> str:
    return s.replace("%", "%25").replace("\r", "").replace("\n", "%0A")


def notice(title: str, text: str, limit: int = 3500) -> None:
    text = text.strip()
    if not text:
        text = "(empty)"
    if len(text) > limit:
        text = "..." + text[-limit:]
    print(f"::notice title={title}::{esc(text)}")


def read(path: str) -> str:
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


def tail(text: str, n: int) -> str:
    return "\n".join(text.splitlines()[-n:])


notice("smoke: process", read(os.path.join(root, "process.txt")))
notice("smoke: app stdout", tail(read(os.path.join(root, "stdout.txt")), 40))
notice("smoke: app stderr", tail(read(os.path.join(root, "stderr.txt")), 40))

# OpenUtau's Serilog file lives somewhere under the copied Library folder.
logs = sorted(glob.glob(os.path.join(root, "app-library", "**", "*.txt"), recursive=True)
              + glob.glob(os.path.join(root, "app-library", "**", "*.log"), recursive=True),
              key=os.path.getmtime)
if logs:
    notice("smoke: OpenUtau log " + os.path.basename(logs[-1]), tail(read(logs[-1]), 40))
else:
    notice("smoke: OpenUtau log", "no log file found under Library")

# Crash reports (.ips): first line is a JSON header, the rest one JSON body.
for ips in sorted(glob.glob(os.path.join(root, "*.ips")))[-2:]:
    raw = read(ips)
    parts = raw.split("\n", 1)
    summary = [os.path.basename(ips)]
    try:
        body = json.loads(parts[1])
        exc = body.get("exception", {})
        summary.append(f"exception: {exc.get('type')} {exc.get('signal')} {exc.get('subtype', '')}")
        term = body.get("termination", {})
        if term:
            summary.append(f"termination: {term.get('indicator')} {term.get('reasons', '')}")
        asi = body.get("asi")
        if asi:
            summary.append("asi: " + json.dumps(asi)[:1500])
        images = body.get("usedImages", [])
        threads = body.get("threads", [])
        crashed = next((t for t in threads if t.get("triggered")), threads[0] if threads else None)
        if crashed:
            summary.append("crashed thread:")
            for fr in crashed.get("frames", [])[:25]:
                img = images[fr["imageIndex"]].get("name", "?") if fr.get("imageIndex", -1) < len(images) else "?"
                summary.append(f"  {img} {fr.get('symbol', '')}+{fr.get('symbolLocation', '')}")
    except Exception as e:  # noqa: BLE001 - best effort
        summary.append(f"(could not parse: {e})")
        summary.append(raw[:2500])
    notice("smoke: crash report", "\n".join(summary))

syslog = read(os.path.join(root, "syslog.txt"))
interesting = [l for l in syslog.splitlines()
               if any(k in l for k in ("rror", "xception", "crash", "Crash", "abort", "Terminat", "ios-port", "dyld"))]
notice("smoke: syslog (filtered)", tail("\n".join(interesting) or syslog, 40))
