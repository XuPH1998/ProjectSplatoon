"""Launch the unchanged performance workload reader on the inactive desktop."""
import importlib.util
from pathlib import Path
import sys

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
BASE = ROOT / 'Reports/BlockTerrain/Windows20/launch-isolated-package-smoke.py'
spec = importlib.util.spec_from_file_location('windows37_guarded_launcher', BASE)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
module.core.__file__ = str(Path(__file__).resolve())
module.core.HARNESS['smoke'] = HERE / 'performance37-harness.py'

if __name__ == '__main__':
    raise SystemExit(module.main())
