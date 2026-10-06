"""Build the release zips from a fresh Release build.

Both zips carry the same BTBridge.dll (the mod is platform-neutral .NET); only the install guide
differs. Output: releases/<tag>/BTBridge-<tag>-windows.zip and -macos.zip, plus SHA256SUMS.txt.

Usage: python scripts/package.py v0.1-alpha
"""
import hashlib
import json
import subprocess
import sys
import zipfile
from pathlib import Path

repo = Path(__file__).resolve().parent.parent
tag = sys.argv[1] if len(sys.argv) > 1 else "v0.1-alpha"
project = repo / "mod" / "BTBridge"
build_out = repo / "releases" / tag / "_build"
dest = repo / "releases" / tag

subprocess.run(["dotnet", "build", str(project), "-c", "Release", "-nologo", "-v", "q", "-o", str(build_out)], check=True)
dll = build_out / "BTBridge.dll"
mod_json = build_out / "mod.json"
for f in (dll, mod_json):
    if not f.is_file() or f.stat().st_size == 0:
        sys.exit(f"missing or empty build output: {f}")
manifest = json.loads(mod_json.read_text(encoding="utf-8"))
assert manifest["Settings"]["AllowCheats"] is False, "release must ship with cheats off"

dest.mkdir(parents=True, exist_ok=True)
sums = []
for platform in ("windows", "macos"):
    zpath = dest / f"BTBridge-{tag}-{platform}.zip"
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(dll, "BTBridge/BTBridge.dll")
        z.write(mod_json, "BTBridge/mod.json")
        z.write(repo / "packaging" / f"INSTALL-{platform}.md", f"INSTALL-{platform}.md")
        z.write(repo / "LICENSE", "LICENSE")
    digest = hashlib.sha256(zpath.read_bytes()).hexdigest()
    sums.append(f"{digest}  {zpath.name}")
    print(f"{zpath.relative_to(repo)}  {zpath.stat().st_size} bytes  sha256 {digest}")

(dest / "SHA256SUMS.txt").write_text("\n".join(sums) + "\n", encoding="utf-8")
for f in build_out.iterdir():
    f.unlink()
build_out.rmdir()
