"""Writes the table of artwork AetherFrame downloads when a player uses it (art on demand).

Every runtime PNG under AetherFrame/Assets/Components, except Celestial Dream's (which stays inside
the plugin), is hosted at a commit of this repository:

    https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/<commit>/AetherFrame/Assets/<path>

The plugin downloads a file only from the commit the table names for it, and uses it only when its
length and SHA-256 are exactly the table's. So hosted bytes never change: a path already in the
table keeps its pin, and a file whose bytes differ from its recorded ones is refused (new bytes
need a new artwork id and a new path). New files are pinned to the commit given with --commit,
which must be the commit that adds them, on master (see AetherFrame/Assets/README.md).

It writes:

- AetherFrame/Domain/Components/ArtFiles.g.cs (generated: never edit by hand)
- AetherFrame/Assets/ArtFiles.txt (the same table, one "<commit> <sha256> <length> <path>" per line)

Run from the repository root:

    python tools/art/write_art_files.py              # check that the table matches the files
    python tools/art/write_art_files.py --write --commit <40-hex commit that adds new files>

Standard library only.
"""

import argparse
import hashlib
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(ROOT, "AetherFrame", "Assets")
COMPONENTS = os.path.join(ASSETS, "Components")
TABLE = os.path.join(ASSETS, "ArtFiles.txt")
OUTPUT = os.path.join(ROOT, "AetherFrame", "Domain", "Components", "ArtFiles.g.cs")

# Kept inside the plugin: tintable, in no Art Style, and small.
EMBEDDED_FOLDERS = {"CelestialDream"}

COMMIT = re.compile(r"^[0-9a-f]{40}$")
PATH = re.compile(r"^Components/[A-Za-z0-9_-]+/[A-Za-z0-9_-]+\.png$")
SHA = re.compile(r"^[0-9a-f]{64}$")


def hosted_files():
    """Every hosted PNG on disk, as (path below Assets with '/', full path), sorted by path."""
    found = []
    for folder in sorted(os.listdir(COMPONENTS)):
        if folder in EMBEDDED_FOLDERS:
            continue
        directory = os.path.join(COMPONENTS, folder)
        if not os.path.isdir(directory):
            continue
        for name in sorted(os.listdir(directory)):
            full = os.path.join(directory, name)
            if os.path.isdir(full):
                raise SystemExit(f"{full}: hosted art folders hold files only")
            if not name.endswith(".png"):
                continue
            path = f"Components/{folder}/{name}"
            if not PATH.match(path):
                raise SystemExit(f"{path}: hosted paths are Components/<Folder>/<File>.png in letters, digits, '_' and '-'")
            found.append((path, full))
    return sorted(found)


def digest(full):
    with open(full, "rb") as source:
        data = source.read()
    return hashlib.sha256(data).hexdigest(), len(data)


def read_table():
    rows = {}
    if not os.path.exists(TABLE):
        return rows
    with open(TABLE, encoding="ascii") as source:
        for number, line in enumerate(source, 1):
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            commit, sha, length, path = line.split(" ")
            if not (COMMIT.match(commit) and SHA.match(sha) and length.isdigit() and PATH.match(path)):
                raise SystemExit(f"ArtFiles.txt line {number} is malformed")
            if path in rows:
                raise SystemExit(f"ArtFiles.txt names {path} twice")
            rows[path] = (commit, sha, int(length))
    return rows


def build(new_commit):
    old = read_table()
    rows = {}
    problems = []
    for path, full in hosted_files():
        sha, length = digest(full)
        if path in old:
            commit, old_sha, old_length = old[path]
            if (old_sha, old_length) != (sha, length):
                problems.append(f"{path}: its bytes changed ({old_sha[:12]} -> {sha[:12]}); hosted bytes never change, so new art needs a new id and path")
            rows[path] = (commit, old_sha, old_length)
        elif new_commit is None:
            problems.append(f"{path}: not in the table; run with --write --commit <the commit that adds it>")
        else:
            rows[path] = (new_commit, sha, length)
    for path in old:
        if path not in rows:
            problems.append(f"{path}: in the table but not on disk; hosted files are never removed while a released plugin names them")
    return rows, problems


def table_text(rows):
    lines = [
        "# The artwork AetherFrame downloads when a player uses it (art on demand), written by",
        "# tools/art/write_art_files.py: <commit> <sha256> <length> <path below AetherFrame/Assets>.",
        "# Hosted at https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/<commit>/AetherFrame/Assets/<path>.",
    ]
    lines += [f"{commit} {sha} {length} {path}" for path, (commit, sha, length) in sorted(rows.items())]
    return "\n".join(lines) + "\n"


def csharp_text(rows):
    pins = []
    for path, (commit, _, _) in sorted(rows.items()):
        if commit not in pins:
            pins.append(commit)
    lines = [
        "// <auto-generated>",
        "// Generated by tools/art/write_art_files.py from AetherFrame/Assets/Components. Don't edit by hand:",
        "// run the script again. Every hosted file, the commit it is downloaded from, its length and its SHA-256.",
        "// </auto-generated>",
        "",
        "namespace AetherFrame.Domain.Components;",
        "",
        "public static partial class ArtFiles",
        "{",
    ]
    for index, commit in enumerate(pins, 1):
        lines.append(f'    private const string Pin{index} = "{commit}";')
    lines += [
        "",
        "    /// <summary>Every hosted file, by path.</summary>",
        "    private static readonly ArtFile[] Table =",
        "    [",
    ]
    for path, (commit, sha, length) in sorted(rows.items()):
        lines.append(f'        new("{path}", {length}, "{sha}", Pin{pins.index(commit) + 1}),')
    lines += ["    ];", "}", ""]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--write", action="store_true", help="write the table and the C# file")
    parser.add_argument("--commit", help="the 40-hex commit new files are pinned to")
    options = parser.parse_args()
    if options.commit is not None and not COMMIT.match(options.commit):
        raise SystemExit("--commit takes a full 40-character lowercase commit id")

    rows, problems = build(options.commit if options.write else None)
    if problems:
        print("\n".join(problems), file=sys.stderr)
        raise SystemExit(1)

    expected = {TABLE: table_text(rows), OUTPUT: csharp_text(rows)}
    if options.write:
        for target, text in expected.items():
            with open(target, "w", encoding="ascii", newline="\n") as out:
                out.write(text)
        print(f"{len(rows)} hosted files written to {os.path.relpath(TABLE, ROOT)} and {os.path.relpath(OUTPUT, ROOT)}")
        return

    stale = []
    for target, text in expected.items():
        current = open(target, encoding="ascii").read().replace("\r\n", "\n") if os.path.exists(target) else None
        if current != text:
            stale.append(os.path.relpath(target, ROOT))
    if stale:
        print("out of date: " + ", ".join(stale) + "; run with --write", file=sys.stderr)
        raise SystemExit(1)
    print(f"{len(rows)} hosted files: the table matches the files")


if __name__ == "__main__":
    main()
