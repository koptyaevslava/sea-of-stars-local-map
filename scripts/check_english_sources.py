from __future__ import annotations

import re
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
CHECKED_SUFFIXES = {".cs", ".py", ".md", ".txt", ".json", ".yml", ".yaml", ".csproj"}
SKIPPED_PARTS = {".git", "bin", "obj", "dist", "payload"}
CYRILLIC = re.compile(r"[\u0400-\u04ff]")


def main() -> None:
    failures: list[str] = []
    for path in sorted(ROOT.rglob("*")):
        if not path.is_file() or path.suffix.lower() not in CHECKED_SUFFIXES:
            continue
        if any(part in SKIPPED_PARTS for part in path.relative_to(ROOT).parts):
            continue
        text = path.read_text(encoding="utf-8")
        for number, line in enumerate(text.splitlines(), 1):
            if CYRILLIC.search(line):
                failures.append(f"{path.relative_to(ROOT)}:{number}")
    if failures:
        raise SystemExit("Cyrillic text found in source or documentation:\n" + "\n".join(failures))
    print("Source and documentation language check passed.")


if __name__ == "__main__":
    main()
