"""Run production journal filesystem checks in original game Mono, without Unity."""
import ctypes
import os
from pathlib import Path
import sys

if len(sys.argv) < 2:
    raise SystemExit("Usage: python run_orb_journal_migration_mono.py <original-game-dir> [runner arguments]")

game = Path(sys.argv[1]).resolve()
managed = game / "valheim_Data" / "Managed"
if not managed.is_dir():
    managed = game / "valheim_server_Data" / "Managed"
runtime = game / "MonoBleedingEdge"
runner = Path(__file__).resolve().parent / "bin" / "orb-journal-migration" / "OrbJournalMigrationChecks.exe"
if not managed.is_dir() or not runner.is_file():
    raise SystemExit("Original Managed directory or built migration test runner was not found")

# The cross-process fixture starts this same original Mono host for its child.
os.environ["FLG_ORB_MIGRATION_PYTHON"] = sys.executable
os.environ["FLG_ORB_MIGRATION_RUNNER"] = str(Path(__file__).resolve())
os.environ["FLG_ORB_MIGRATION_GAME"] = str(game)
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
mono.mono_set_assemblies_path(os.pathsep.join(map(str, (managed, runner.parent))).encode())

domain = mono.mono_jit_init_version(b"FLGOrbJournalMigrationChecks", b"v4.0.30319")
if not domain:
    raise SystemExit("Could not initialize original Mono")
assembly = mono.mono_domain_assembly_open(domain, str(runner).encode())
if not assembly:
    raise SystemExit("Could not load journal migration checks")
args = [str(runner).encode()] + [value.encode() for value in sys.argv[2:]]
argv = (ctypes.c_char_p * len(args))(*args)
raise SystemExit(mono.mono_jit_exec(domain, assembly, len(args), argv))
