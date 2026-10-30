"""Inspect integrated Orb diagnostics in original Mono. No Unity or world startup."""
import ctypes
import os
from pathlib import Path
import sys

if len(sys.argv) not in (4, 5):
    raise SystemExit("Usage: python run_orb_diagnostics_mono.py <original-game-dir> <FeedLikeGrandma.dll> <BepInEx-core-dir> [lifecycle|data]")

mode = sys.argv[4] if len(sys.argv) == 5 else "original"
if mode not in ("original", "lifecycle", "data"):
    raise SystemExit("Mode must be lifecycle or data, or omitted for original-DLL checks")

game, plugin, core = (Path(value).resolve() for value in sys.argv[1:4])
managed = game / "valheim_Data" / "Managed"
if not managed.is_dir():
    managed = game / "valheim_server_Data" / "Managed"
runtime = game / "MonoBleedingEdge"
runner = Path(__file__).resolve().parent / "bin" / ("orb-diagnostic-data" if mode == "data" else "orb-diagnostic-runtime") / ("OrbDiagnosticDataChecks.exe" if mode == "data" else "OrbDiagnosticRuntimeChecks.exe")
if not managed.is_dir() or not runner.is_file() or not plugin.is_file():
    raise SystemExit("Original Managed directory or built runner/plugin not found")

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
domain = mono.mono_jit_init_version(b"FLGIntegratedOrbDiagnostics", b"v4.0.30319")
if not domain:
    raise SystemExit("Could not initialize original Mono")
assembly = mono.mono_domain_assembly_open(domain, str(runner).encode())
if not assembly:
    raise SystemExit("Could not load diagnostic checks")
args = [str(runner).encode()] if mode == "data" else [str(path).encode() for path in (runner, plugin, managed, core)]
if mode == "lifecycle":
    args.append(b"lifecycle")
argv = (ctypes.c_char_p * len(args))(*args)
raise SystemExit(mono.mono_jit_exec(domain, assembly, len(args), argv))
