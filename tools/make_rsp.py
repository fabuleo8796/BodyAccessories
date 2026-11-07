# Writes a csc response file for a Body Accessories plugin build (no .NET SDK on this PC: Unity's bundled Roslyn compiles).
# python tools/make_rsp.py <name> <out dll> <source folder> [<source folder> ...]  -> _private/<name>.rsp
# References: the game's dotnet runtime (mscorlib, netstandard, System.*), BepInEx core, and every BepInEx\interop
# DLL except the 3 KB UnityEngine.dll facade (modding kit trap 3). The interop folder is listed fresh each time,
# because game updates add and drop DLLs.
import os
import sys

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Flipping is Hard"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

name, out, folders = sys.argv[1], sys.argv[2], sys.argv[3:]
lines = ["-nologo", "-nostdlib", "-target:library", "-langversion:10", "-unsafe", "-optimize", "-deterministic",
         "-nowarn:CS1701,CS1702,CS0618", f'-out:"{os.path.abspath(out)}"']

dotnet = os.path.join(GAME, "dotnet")
for f in sorted(os.listdir(dotnet)):
    if f.endswith(".dll") and "Native" not in f and (f in ("mscorlib.dll", "netstandard.dll") or f.startswith("System.")):
        lines.append(f'-r:"{os.path.join(dotnet, f)}"')
for f in ["BepInEx.Core", "BepInEx.Unity.IL2CPP", "BepInEx.Unity.Common", "0Harmony", "Il2CppInterop.Runtime", "Il2CppInterop.Common"]:
    lines.append(f'-r:"{os.path.join(GAME, "BepInEx", "core", f + ".dll")}"')
interop = os.path.join(GAME, "BepInEx", "interop")
for f in sorted(os.listdir(interop)):
    if f.endswith(".dll") and f != "UnityEngine.dll":
        lines.append(f'-r:"{os.path.join(interop, f)}"')
lib = os.path.join(ROOT, "lib")  # other mods this one builds against (FIHSync.dll, a soft dependency)
if os.path.isdir(lib):
    for f in sorted(os.listdir(lib)):
        if f.endswith(".dll"):
            lines.append(f'-r:"{os.path.join(lib, f)}"')
for folder in folders:
    res = os.path.join(folder, "Resources")  # embedded as-is, named after the file
    if os.path.isdir(res):
        for f in sorted(os.listdir(res)):
            lines.append(f'-resource:"{os.path.abspath(os.path.join(res, f))}",{f}')
    for f in sorted(os.listdir(folder)):
        if f.endswith(".cs"):
            lines.append(f'"{os.path.abspath(os.path.join(folder, f))}"')

path = os.path.join(ROOT, "_private", name + ".rsp")
with open(path, "w", encoding="utf-8") as fh:
    fh.write("\n".join(lines) + "\n")
print(path, len(lines), "lines")
