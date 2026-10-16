"""Prepare-only; reuse the inactive-desktop launcher with narrow Standalone prefs preservation."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
BASE = HERE.parent / 'FullAssemblyRegression96' / 'launch-isolated-harness.py'
BASE_SHA256 = 'f5824b02b5be79fec0ceb655f41d5a55b14da5fc8bd725fcab89e316a34153f3'

if hashlib.sha256(BASE.read_bytes()).hexdigest() != BASE_SHA256:
    raise RuntimeError('The reviewed isolated launcher identity changed; review before reuse')
spec = importlib.util.spec_from_file_location('package_smoke_isolated_launcher', BASE)
core = importlib.util.module_from_spec(spec)
spec.loader.exec_module(core)
# The original import has already resolved native support from its own HERE.
# Keep that directory, but native launch must bootstrap this registration again.
core.__file__ = str(Path(__file__).resolve())
core.HARNESS['smoke'] = HERE / 'package-smoke-harness.py'
guard_spec = importlib.util.spec_from_file_location('standalone_preference_guard', HERE / 'standalone-preference-guard.py')
guard = importlib.util.module_from_spec(guard_spec)
guard_spec.loader.exec_module(guard)
original_request = core.request


def guarded_request(path, require_player=False):
    return guard.validate_request(path, original_request(path, require_player), require_player)


core.request = guarded_request


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    selection = parser.add_mutually_exclusive_group(required=True)
    selection.add_argument('--request', type=Path,
                           help='Structured request JSON; prepare only unless --execute is supplied')
    selection.add_argument('--bootstrap', type=Path, help=argparse.SUPPRESS)
    parser.add_argument('--execute', action='store_true')
    options = parser.parse_args(argv)
    if options.bootstrap:
        if options.execute:
            parser.error('Bootstrap does not accept --execute')
        return guard.guarded_bootstrap(core, options.bootstrap.resolve())
    data = core.request(options.request, require_player=options.execute)
    if not options.execute:
        print(json.dumps({'preparedOnly': True, **data}, ensure_ascii=False, indent=2))
        return 0
    return core.launch(data)


if __name__ == '__main__':
    raise SystemExit(main())
