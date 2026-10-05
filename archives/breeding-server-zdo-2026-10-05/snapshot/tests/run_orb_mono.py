"""Run isolated checks in the game's original Mono; defaults to Orb persistence. No world is started."""
import ctypes
import os
from pathlib import Path
import sys

if len(sys.argv) not in (4, 5):
    raise SystemExit("Usage: python run_orb_mono.py <game directory> <plugin.dll> <BepInEx core directory> [test.exe]")

game, plugin, core = (Path(value).resolve() for value in sys.argv[1:4])
managed = game / "valheim_Data" / "Managed"
if not managed.is_dir():
    managed = game / "valheim_server_Data" / "Managed"
runtime = game / "MonoBleedingEdge"
runner = Path(__file__).resolve().parent / "bin" / "orb-transactions" / "OrbTransactionChecks.exe"
if len(sys.argv) == 5:
    runner = Path(sys.argv[4]).resolve()
if not managed.is_dir() or not runner.is_file():
    raise SystemExit("Original game Managed directory or built test executable was not found")

# Keep the handle alive while Mono loads its native dependencies.
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
mono.mono_set_assemblies_path(os.pathsep.join(map(str, (managed, core, plugin.parent, runner.parent))).encode())
domain = mono.mono_jit_init_version(b"FeedLikeGrandmaOrbChecks", b"v4.0.30319")
if not domain:
    raise SystemExit("Could not initialize original game Mono")
assembly = mono.mono_domain_assembly_open(domain, str(runner).encode())
if not assembly:
    raise SystemExit("Could not load isolated test runner")
args = [str(runner).encode(), str(plugin).encode()]
argv = (ctypes.c_char_p * len(args))(*args)
raise SystemExit(mono.mono_jit_exec(domain, assembly, len(args), argv))
