"""Turn build-log errors into GitHub annotations.

Annotations are readable through the public checks API, unlike raw job logs,
so this makes failures diagnosable without signing in. GitHub keeps at most
10 error annotations per step, so errors are deduplicated and packed into a
few multi-line annotations. The tail of each log is attached as a notice.

usage: python3 report_errors.py LOG [LOG...]
"""
import re
import sys

ERROR = re.compile(r"(: error |error [A-Z]+\d+|clang: error|ld: |Undefined symbols|fatal error|Unhandled exception|MSB\d{4}:)", re.I)


def esc(s: str) -> str:
    return s.replace("%", "%25").replace("\r", "").replace("\n", "%0A")


def strip_ts(line: str) -> str:
    # GitHub log lines may be prefixed with an ISO timestamp.
    return re.sub(r"^\d{4}-\d\d-\d\dT[\d:.]+Z\s", "", line.rstrip("\n"))


for path in sys.argv[1:]:
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            lines = [strip_ts(l) for l in f]
    except FileNotFoundError:
        continue
    seen, errors = set(), []
    for line in lines:
        if ERROR.search(line) and "warning" not in line.lower():
            key = re.sub(r"\s+\[[^\]]*\.csproj[^\]]*\]$", "", line.strip())
            if key not in seen:
                seen.add(key)
                errors.append(key[:600])
    for i in range(0, min(len(errors), 60), 12):
        chunk = errors[i:i + 12]
        print(f"::error title={path} errors {i + 1}-{i + len(chunk)} of {len(errors)}::{esc(chr(10).join(chunk))}")
    tail = "\n".join(l[:300] for l in lines[-40:])
    print(f"::notice title={path} (last 40 lines)::{esc(tail)}")
