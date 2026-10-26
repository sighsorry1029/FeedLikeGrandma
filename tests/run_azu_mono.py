"""Run Azu contract/IL checks in original Mono, without a world or Unity lifecycle.

Optional composition:<plugins-directory> checks the installed FineDining and
ItemDataManager transpiler bodies through explicit startup-suppressed fixtures.
Use '-' for the Azu DLL argument in that mode; Azu need not be installed there.
"""
import ctypes
import os
from pathlib import Path
import sys
import subprocess

if len(sys.argv) not in (5, 6):
    raise SystemExit("Usage: python run_azu_mono.py <original-game-dir> <FeedLikeGrandma.dll> <AzuAutoStore.dll> <BepInEx-core-dir> [composition:<plugin-directory>]")
if len(sys.argv) == 6 and sys.argv[5].startswith("composition:"):
    profile = sys.argv[5][len("composition:"):]
    for case in ("fine-first", "fine-last", "crafty-first", "crafty-last", "cuisine-first", "cuisine-last",
                 "sleep-first", "sleep-last", "combined-first", "combined-last", "mutations"):
        result = subprocess.run([sys.executable, __file__, *sys.argv[1:5], "compose:" + case + ":" + profile])
        if result.returncode:
            raise SystemExit(result.returncode)
    raise SystemExit(0)
if len(sys.argv) == 5:
    # Harmony's generic operand cache is process-wide and may conflate identical
    # full type names in synthetic assembly copies. Bind each variant in a fresh
    # original Mono process, just as only one Azu build is present in a game.
    for phase in ("original", "identity", "missing-route", "ownership-rpc"):
        result = subprocess.run([sys.executable, __file__, *sys.argv[1:], phase])
        if result.returncode:
            raise SystemExit(result.returncode)
    raise SystemExit(0)
game, flg, azu, core = (Path(value).resolve() for value in sys.argv[1:5])
managed = game / "valheim_Data" / "Managed"
if not managed.is_dir():
    managed = game / "valheim_server_Data" / "Managed"
runtime = game / "MonoBleedingEdge"
runner = Path(__file__).resolve().parent / "bin" / "azu-compatibility" / "AzuCompatibilityChecks.exe"
is_composition = sys.argv[5].startswith("compose:")
if not managed.is_dir() or not all(path.is_file() for path in (runner, flg)) or (not is_composition and not azu.is_file()):
    raise SystemExit("Original Managed directory or built test/input DLL was not found")

dll_directory = os.add_dll_directory(str(runtime / "EmbedRuntime"))
mono = ctypes.CDLL(str(runtime / "EmbedRuntime" / "mono-2.0-bdwgc.dll"))
mono.mono_set_dirs.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
mono.mono_set_assemblies_path.argtypes = [ctypes.c_char_p]
mono.mono_jit_init_version.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
mono.mono_jit_init_version.restype = ctypes.c_void_p
mono.mono_domain_assembly_open.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
mono.mono_domain_assembly_open.restype = ctypes.c_void_p
mono.mono_jit_exec.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(ctypes.c_char_p)]
mono.mono_jit_exec.restype = ctypes.c_int
mono.mono_set_dirs(str(managed).encode(), str(runtime / "etc").encode())
mono.mono_set_assemblies_path(os.pathsep.join(map(str, (managed, core, flg.parent, azu.parent, runner.parent))).encode())
domain = mono.mono_jit_init_version(b"FLGAzuCompatibilityChecks", b"v4.0.30319")
if not domain:
    raise SystemExit("Could not initialize original Mono")
assembly = mono.mono_domain_assembly_open(domain, str(runner).encode())
if not assembly:
    raise SystemExit("Could not load Azu compatibility checks")
args = [str(path).encode() for path in (runner, flg, azu, managed, core)]
args.append(sys.argv[5].encode())
argv = (ctypes.c_char_p * len(args))(*args)
raise SystemExit(mono.mono_jit_exec(domain, assembly, len(args), argv))
