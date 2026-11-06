#!/usr/bin/env python3
"""
Every NuGet package a project references - transitive ones included - must be listed in
THIRD-PARTY-NOTICES.md: its id (or its family, "SkiaSharp.NativeAssets.*") and its version in
the same section. A new or updated package then fails the release workflow until its notice
is written, instead of shipping without it.

Usage (after a restore): check-third-party-notices.py PROJECT [PROJECT ...]
"""
import json
import pathlib
import re
import subprocess
import sys

notices_path = pathlib.Path(__file__).resolve().parent.parent / "THIRD-PARTY-NOTICES.md"
notices = notices_path.read_text(encoding="utf-8")
# The text before the license texts, split into its sections (a heading and what follows)
listing = notices.split("\n## The MIT License", 1)[0]
sections = re.split(r"\n(?=#{2,3} )", listing)


def mentions(section, package):
    """The section names the package, or its family: "SkiaSharp.NativeAssets.*" """
    if re.search(r"(?<![\w.])" + re.escape(package) + r"(?![\w.])", section, re.IGNORECASE):
        return True
    return any(package.lower().startswith(family.lower() + ".")
               for family in re.findall(r"([\w.]+)\.\*", section))


packages = {}
for project in sys.argv[1:]:
    listed = subprocess.run(
        ["dotnet", "list", project, "package", "--include-transitive", "--format", "json"],
        check=True, capture_output=True, text=True).stdout
    for p in json.loads(listed)["projects"]:
        for framework in p.get("frameworks", []):
            for package in framework.get("topLevelPackages", []) + framework.get("transitivePackages", []):
                packages[package["id"]] = package["resolvedVersion"]

missing = []
for package, version in sorted(packages.items(), key=lambda p: p[0].lower()):
    named = [s for s in sections if mentions(s, package)]
    if not named:
        missing.append(f"{package} {version}: not listed")
    elif not any(re.search(r"(?<![\w.])" + re.escape(version) + r"(?![\w.])", s) for s in named):
        missing.append(f"{package} {version}: listed, but not with this version")

for problem in missing:
    print(f"::error file=THIRD-PARTY-NOTICES.md::{problem}")
print(f"{len(packages)} packages, {len(missing)} without their notice in {notices_path.name}")
sys.exit(1 if missing else 0)
