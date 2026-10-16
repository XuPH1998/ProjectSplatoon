"""Offline AST/contract verification only; never execute the runner or Unity CLI."""
from pathlib import Path
import ast
from collections import Counter
import copy
import datetime
import difflib
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET

OUT = Path(__file__).resolve().parent
ROOT = OUT.parents[3]
RUNNER = OUT / 'run-editor-filters.py'
WRAPPER = OUT / 'run-full-regression-preserved.py'
GUARD = OUT / 'FullRegressionPreferenceGuard.cs'
SOURCE = ROOT / 'Reports/BlockTerrain/run-editor-filters.py'


def definitions_only(path):
    tree = ast.parse(path.read_text(encoding='utf-8-sig'))
    allowed = [node for node in tree.body if isinstance(node, (ast.Import, ast.ImportFrom, ast.Assign, ast.FunctionDef))]
    namespace = {'__file__': str(path)}
    exec(compile(ast.Module(body=allowed, type_ignores=[]), str(path), 'exec'), namespace)
    return namespace


checks = {}
runner_text = RUNNER.read_text(encoding='utf-8-sig')
wrapper_text = WRAPPER.read_text(encoding='utf-8-sig')
guard_text = GUARD.read_text(encoding='utf-8-sig')
wrapper_tree = ast.parse(wrapper_text)
ps_template = next(node.value for node in ast.walk(wrapper_tree)
                   if isinstance(node, ast.Constant) and isinstance(node.value, str) and
                   node.value.startswith('\n$ErrorActionPreference'))
(OUT / 'process-preflight.ps1').write_text(ps_template.replace('%ROOT%', str(ROOT).replace("'", "''"))
    .replace('%PID%', '12345'), encoding='utf-8')
runner = definitions_only(RUNNER)
wrapper = definitions_only(WRAPPER)
checks['python_ast_parses_without_running_entrypoints'] = True
checks['prepared_roots_point_to_project'] = runner['ROOT'] == wrapper['ROOT'] == ROOT
checks['one_run_tests_subprocess_site'] = runner_text.count('subprocess.run(') == 1
checks['one_full_runner_subprocess_site'] = wrapper_text.count('subprocess.Popen(') == 1
checks['assembly_one_selector_and_explicit_false'] = 'len(args.filters) != 1' in runner_text and \
    "'--filter-type', 'assembly', '--timeout-seconds', '7200', 'Splatoon.Tests'" in wrapper_text and \
    "*(['--include_explicit', 'true'] if include_explicit else [])" in runner_text and 'include_explicit=False' in runner_text
checks['default_timeout1200_full_timeout7200'] = 'timeout_seconds=1200' in runner_text and \
    'default=1200' in runner_text and "'timeoutSeconds': 7200" in wrapper_text
checks['watchdog_includes_launch_and_launch_is_bounded'] = \
    runner_text.index('deadline = time.monotonic() + timeout_seconds') < runner_text.index('subprocess.run(') and \
    'timeout=min(60, timeout_seconds)' in runner_text
checks['no_test_retry_cancel_compile_build_or_editor_start_stop'] = all(word not in runner_text + wrapper_text for word in
    ("'cancel_tests'", "'recompile'", "'build'", "'list_tests'", "'test_status'", "'playmode'", 'kill(', 'terminate('))
checks['fresh_completed_request_absent_before_xml_accept'] = \
    "fresh and state.get('status') == 'completed' and not REQUEST.exists()" in runner_text
checks['19_preferences_no_deleteall'] = 'for (int hero = 1; hero <= 9; hero++)' in guard_text and \
    'data.api.Length != 19' in guard_text and 'DeleteAll' not in guard_text
checks['actual_editor_hive_null_is_failure'] = \
    'Registry.CurrentUser.OpenSubKey(Hive, false)' in guard_text and \
    'Actual Editor preference hive is unavailable; snapshot rejected.' in guard_text
checks['raw_binary_types_and_missing_keys_exact'] = all(text in guard_text for text in
    ('binaryBase64', 'RegistryValueKind.DWord', 'RegistryValueKind.QWord', 'RegistryValueKind.ExpandString',
     'PlayerPrefs.DeleteKey(preference.key)', 'registry.DeleteValue(name, false)',
     'JsonConvert.SerializeObject(before.registry) == JsonConvert.SerializeObject(after.registry)'))
checks['active_collector_and_framework_guard'] = \
    'GetField("m_ActiveCollector", flags)' in guard_text and 'GetMethod("IsRunActive", flags)' in guard_text and \
    'RequireIdle(root); // No preference writes if a test is still active' in guard_text
checks['only_exact_five_output_targets'] = len(wrapper['TARGETS']) == 5 and \
    set(wrapper['TARGETS']) == {'preference.json', 'lobby.png', 'scoreboard.png', 'remote-name.png', 'swim-name-hidden.png'}
checks['old_fixture_backup_isolated_after_verified_snapshot'] = wrapper_text.index("before = script(out,") < \
    wrapper_text.index("TARGETS['preference.json'].unlink()")
checks['pending_observation_defers_editor_and_file_mutations'] = \
    'offline_idle(submitted if manifest' in wrapper_text and \
    "manifest['restorationDeferredReason'] = reason" in wrapper_text and \
    "copy_targets(out / 'FilesAfterPending')" in wrapper_text
checks['restoration_only_recovery_does_not_rerun_tests'] = \
    wrapper_text.index('if args.restore_from:') < wrapper_text.index('process = subprocess.Popen(') and \
    'restore(out, manifest, recovery=True)' in wrapper_text

launch = {'success': True, 'result': 'running', 'StatusPath': 'Temp/pipeline_test_status.json',
          'Mode': 'EditMode', 'FilterApplied': 'assembly: Splatoon.Tests'}
runner['validate_launch']({'success': True, 'data': {'command': 'run_tests', 'result': launch}}, 'Splatoon.Tests', 'assembly')
checks['launch_valid_nested_cli_envelope'] = True
bad = copy.deepcopy(launch)
bad['success'] = False
try:
    runner['validate_launch']({'success': True, 'data': {'command': 'run_tests', 'result': bad}}, 'Splatoon.Tests', 'assembly')
    checks['launch_rejects_inner_failure'] = False
except ValueError:
    checks['launch_rejects_inner_failure'] = True

original_tree = ast.parse(SOURCE.read_text(encoding='utf-8-sig'))
old_launch = next(node.args[0] for node in ast.walk(original_tree)
                  if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and
                  isinstance(node.func.value, ast.Name) and node.func.value.id == 'subprocess' and
                  node.func.attr == 'run')
selector = 'Splatoon.Tests.BlockTerrainPublicationTests.ClampedColumnRunsSettleWithoutPassingTheirLowerRun'
old_arguments = eval(compile(ast.Expression(old_launch), '<original-argv-only>', 'eval'),
    {'CLI': runner['CLI'], 'ROOT': ROOT, 'test_filter': selector, 'include_explicit': False})
new_arguments = runner['build_launch_arguments'](selector)
checks['default_testname_false_argv_exactly_matches_original_working_runner'] = new_arguments == old_arguments
checks['default_false_remains_declared_installed_default'] = 'bool includeExplicit = false' in \
    (ROOT / 'Library/PackageCache/com.unity.pipeline@62c08c808737/Editor/Commands/TestCommands.cs').read_text(encoding='utf-8-sig')
checks['explicit_true_remains_forwarded_for_reviewed_exact_selection'] = \
    runner['build_launch_arguments'](selector, True)[-2:] == ['--include_explicit', 'true']
assembly_arguments = runner['build_launch_arguments']('Splatoon.Tests', False, 'assembly', 7200)
checks['assembly_still_has_explicit_selector_and7200'] = \
    assembly_arguments[assembly_arguments.index('--filter_type') + 1] == 'assembly' and \
    assembly_arguments[assembly_arguments.index('--timeout') + 1] == '7200' and \
    '--include_explicit' not in assembly_arguments
category_arguments = runner['build_launch_arguments']('Example', False, 'category')
checks['category_still_has_explicit_selector'] = \
    category_arguments[category_arguments.index('--filter_type') + 1] == 'category'
actual_compat = json.loads((ROOT / 'Reports/BlockTerrain/LandingOrder16/old-runtime-compat-start.json').read_text(encoding='utf-8-sig'))
runner['validate_launch'](actual_compat, selector, 'testName')
checks['root_actual_compat_launch_with_testname_lowercase_accepted'] = True
bad_filter = copy.deepcopy(actual_compat)
bad_filter['data']['result']['FilterApplied'] = 'testName: ' + selector
try:
    runner['validate_launch'](bad_filter, selector, 'testName')
    checks['non_normalized_response_rejected'] = False
except ValueError:
    checks['non_normalized_response_rejected'] = True
checks['actual_launch_argv_saved_before_subprocess'] = \
    runner_text.index("raw_dir / 'start.argv.json'") < runner_text.index('call = subprocess.run(')

xml = ROOT / 'Reports/BlockTerrain/UnityTests/20261010-174221.xml'
root = ET.parse(xml).getroot()
leaves = root.findall('.//test-case')
state = {'status': 'completed', 'summary': {key: int(root.get(key)) for key in
         ('total', 'passed', 'failed', 'skipped', 'inconclusive')},
         'results': [{'FullName': leaf.get('fullname'), 'Status': leaf.get('result')} for leaf in leaves]}
started = datetime.datetime.strptime(root.get('start-time'), '%Y-%m-%d %H:%M:%SZ').replace(tzinfo=datetime.timezone.utc).timestamp()
actual, association = runner['validate_terminal_xml'](xml, state, started, 'Splatoon.Tests', 'assembly')
checks['real_publication13_not_discovery1551'] = len(actual) == 13 and association['executedCases'] == 13
tail_state = copy.deepcopy(state)
tail_state['results'] = tail_state['results'][-1:]
_, partial = runner['validate_terminal_xml'](xml, tail_state, started, 'Splatoon.Tests', 'assembly')
checks['domain_reload_partial_status_tail_records_boundary'] = partial['statusResultsPartialAfterPossibleDomainReload'] is True
for label, mutate in (
    ('mismatched_summary_rejected', lambda value: value['summary'].update({'total': 1551})),
    ('wrong_fullname_rejected', lambda value: value['results'][0].update({'FullName': 'Wrong.Fixture.Case'})),
    ('duplicate_multiset_overflow_rejected', lambda value: value.update({'results': [value['results'][0]] * len(leaves)})),
):
    changed = copy.deepcopy(state)
    mutate(changed)
    try:
        runner['validate_terminal_xml'](xml, changed, started, 'Splatoon.Tests', 'assembly')
        checks[label] = False
    except ValueError:
        checks[label] = True
try:
    runner['validate_terminal_xml'](xml, state, started, 'Other.Tests', 'assembly')
    checks['wrong_assembly_rejected'] = False
except ValueError:
    checks['wrong_assembly_rejected'] = True

failed_xml = ROOT / 'Reports/BlockTerrain/UnityTests/20261010-184401.xml'
failed_root = ET.parse(failed_xml).getroot()
failed_leaves = failed_root.findall('.//test-case')
failed_state = {'status': 'completed', 'summary': {key: int(failed_root.get(key)) for key in
    ('total', 'passed', 'failed', 'skipped', 'inconclusive')},
    'results': [{'FullName': leaf.get('fullname'), 'Status': leaf.get('result')} for leaf in failed_leaves]}
failed_started = datetime.datetime.strptime(failed_root.get('start-time'), '%Y-%m-%d %H:%M:%SZ').replace(tzinfo=datetime.timezone.utc).timestamp()
failed_actual, failed_association = runner['validate_terminal_xml'](failed_xml, failed_state, failed_started, 'Splatoon.Tests', 'assembly')
checks['real_nunit_failed_child_single_case_is_terminal_failure'] = len(failed_actual) == 1 and \
    failed_actual[0].get('result') == 'Failed' and failed_association['canonicalRootResult'] == 'Failed'
checks['real_nunit_failed_child_raw_root_is_preserved'] = failed_association['rootResult'] == 'Failed(Child)'
for label, outcome in (('arbitrary_failed_suffix_rejected', 'Failed(Anything)'),
                       ('nonterminal_root_running_rejected', 'Running')):
    try:
        runner['canonical_root_result'](outcome)
        checks[label] = False
    except ValueError:
        checks[label] = True

# Exercise exact present/absent byte restoration against scratch files only.
mock = OUT / 'OfflineVerification'
mock.mkdir(exist_ok=True)
scratch = mock / datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
scratch.mkdir()
mock_targets = {name: scratch / 'Live' / name for name in wrapper['TARGETS']}
for index, (name, path) in enumerate(mock_targets.items()):
    path.parent.mkdir(exist_ok=True)
    if index % 2 == 0:
        path.write_bytes(b'original\x00\xff\r\n' + name.encode('utf-8'))
wrapper['TARGETS'] = mock_targets
wrapper['ROOT'] = scratch
baseline = wrapper['copy_targets'](scratch / 'FilesBefore')
for name, path in mock_targets.items():
    path.write_bytes(b'new result\x00' + name.encode('utf-8'))
wrapper['copy_targets'](scratch / 'FilesAfter')
restored = wrapper['restore_targets'](scratch / 'FilesBefore', baseline)
checks['mock_five_targets_original_bytes_and_original_absence_restore'] = all(restored.values())

class MockCompleted:
    returncode = 0
    stderr = b'raw stderr\r\n'
    stdout = json.dumps({'success': True, 'data': {'command': 'run_script',
        'result': {'success': True, 'result': {'saved': True, 'preferences': 19}}}}).encode('utf-8')

original_run = wrapper['subprocess'].run
try:
    wrapper['subprocess'].run = lambda *args, **kwargs: MockCompleted()
    result = wrapper['script'](scratch, 'mock-snapshot', 'Snapshot', 'root', 'output')
    checks['run_script_actual_nested_guard_object_extracted'] = result == {'saved': True, 'preferences': 19}
    def timeout(*args, **kwargs):
        raise subprocess.TimeoutExpired(args[0], 60, output=b'partial\x00stdout', stderr=b'partial\x00stderr')
    wrapper['subprocess'].run = timeout
    try:
        wrapper['command'](scratch, 'mock-timeout', ['do-not-execute'])
    except subprocess.TimeoutExpired:
        pass
    checks['cli_timeout_partial_raw_bytes_preserved'] = \
        (scratch / 'mock-timeout.stdout').read_bytes() == b'partial\x00stdout' and \
        (scratch / 'mock-timeout.stderr').read_bytes() == b'partial\x00stderr'
finally:
    wrapper['subprocess'].run = original_run

(OUT / 'runner.patch').write_text(''.join(difflib.unified_diff(
    (OUT / 'runner-before.py').read_text(encoding='utf-8-sig').splitlines(True), runner_text.splitlines(True),
    fromfile='Reports/BlockTerrain/run-editor-filters.py',
    tofile='Reports/BlockTerrain/RegressionTriage/LatestSourceFullRegression15/run-editor-filters.py')), encoding='utf-8')
result = {'checks': checks, 'passed': sum(checks.values()), 'total': len(checks),
          'sha256': {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in (RUNNER, WRAPPER, GUARD)},
          'unchanged_original_runner_sha256': hashlib.sha256(SOURCE.read_bytes()).hexdigest(),
          'retained_mock_directory': str(scratch.relative_to(ROOT)).replace('\\', '/'),
          'boundary': 'Offline AST/functions with synthetic subprocess results, scratch files and existing read-only XML. No Unity CLI, Editor, Player, Registry, C# compilation, build or real tests executed.'}
(OUT / 'runner-static-review.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
assert all(checks.values()), result
print(json.dumps(result, ensure_ascii=False))
