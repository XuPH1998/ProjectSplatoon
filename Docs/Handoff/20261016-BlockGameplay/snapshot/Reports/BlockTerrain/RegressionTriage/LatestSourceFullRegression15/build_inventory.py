from pathlib import Path
import datetime
import json
import re
import xml.etree.ElementTree as ET

OUT = Path(__file__).resolve().parent
ROOT = OUT.parents[3]
TESTS = ROOT / 'Assets/Splatoon/Tests'
XMLS = ROOT / 'Reports/BlockTerrain/UnityTests'

def mask_source(text):
    expression = r'@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|//[^\n]*|/\*[\s\S]*?\*/'
    return re.sub(expression, lambda match: ''.join('\n' if ch == '\n' else ' ' for ch in match.group()), text)

classes = {}
explicit = []
for path in sorted(TESTS.rglob('*.cs')):
    text = path.read_text(encoding='utf-8-sig')
    masked = mask_source(text)
    namespace_match = re.search(r'\bnamespace\s+([\w.]+)', masked)
    if namespace_match is None:
        continue
    namespace = namespace_match.group(1)
    spans = []
    for match in re.finditer(r'\bclass\s+(\w+)', masked):
        opening = masked.find('{', match.end())
        if opening < 0:
            continue
        depth = 1
        for end in range(opening + 1, len(masked)):
            depth += (masked[end] == '{') - (masked[end] == '}')
            if not depth:
                spans.append((match.start(), end, namespace + '.' + match.group(1)))
                break
    for attributes in re.finditer(r'\[[^\]]*\]', masked):
        if not re.search(r'\b(?:Test|TestCase|TestCaseSource|UnityTest)\b', attributes.group()):
            continue
        owners = [span for span in spans if span[0] < attributes.start() < span[1]]
        if not owners:
            continue
        owner = max(owners, key=lambda span: span[0])[2]
        record = classes.setdefault(owner, {'selector': owner, 'source': str(path.relative_to(ROOT)).replace('\\', '/'),
                                           'sourceMethodAttributeCount': 0})
        record['sourceMethodAttributeCount'] += 1
        if re.search(r'\bExplicit\b', attributes.group()):
            declaration = re.search(r'\bpublic\s+(?:static\s+)?(?:[\w<>\[\],?]+)\s+(\w+)\s*\(', masked[attributes.end():attributes.end()+500])
            assert declaration is not None, path
            explicit.append({'selector': owner + '.' + declaration.group(1), 'source': record['source'],
                             'line': text.count('\n', 0, attributes.start()) + 1})

completed = []
rejected = []
history = {}
for path in sorted(XMLS.glob('*.xml')):
    try:
        root = ET.parse(path).getroot()
        leaves = root.findall('.//test-case')
        counts = {'total': len(leaves), 'passed': sum(item.get('result') == 'Passed' for item in leaves),
                  'failed': sum(item.get('result', '').startswith('Failed') for item in leaves),
                  'skipped': sum(item.get('result') in ['Skipped', 'Ignored', 'NotRunnable', 'Inconclusive'] for item in leaves)}
        terminal = bool(root.get('end-time')) and all(item.get('end-time') for item in leaves)
        valid = terminal and all(int(root.get(key, '-1')) == value for key, value in counts.items() if key != 'skipped')
        relative = str(path.relative_to(ROOT)).replace('\\', '/')
        if not valid:
            rejected.append({'path': relative, 'reason': 'Not accepted as terminal/count-consistent evidence'})
            continue
        record = {'path': relative, **counts, 'endUtc': root.get('end-time'),
                  'classes': sorted({item.get('classname') for item in leaves if item.get('classname')})}
        completed.append(record)
        for leaf in leaves:
            history.setdefault(leaf.get('fullname'), []).append({'xml': relative, 'result': leaf.get('result'),
                                                                'endUtc': leaf.get('end-time')})
        for classname in record['classes']:
            if classname in classes:
                classes[classname]['latestCompletedXml'] = relative
                classes[classname]['latestCompletedUtc'] = record['endUtc']
                selected = [item for item in leaves if item.get('classname') == classname]
                classes[classname]['latestCompletedCases'] = len(selected)
                classes[classname]['latestCompletedPassed'] = sum(item.get('result') == 'Passed' for item in selected)
    except (ET.ParseError, ValueError, OSError) as error:
        rejected.append({'path': str(path.relative_to(ROOT)).replace('\\', '/'), 'reason': str(error)})

original = ET.parse(XMLS / '20261010-050427.xml').getroot()
original_failures = [item.get('fullname') for item in original.findall('.//test-case') if item.get('result') == 'Failed']
mapping = {
    'Splatoon.Tests.SwimMovementTests.SurfaceChoosesSpeedRecoveryAndModel(0,3,Neutral)':
        'Splatoon.Tests.SwimMovementTests.SurfaceChoosesSpeedRecoveryAndModel(0,3,Neutral,12.6f)',
    'Splatoon.Tests.SwimMovementTests.SurfaceChoosesSpeedRecoveryAndModel(1,8,Friendly)':
        'Splatoon.Tests.SwimMovementTests.SurfaceChoosesSpeedRecoveryAndModel(1,8,Friendly,105.0f)',
}
closures = []
remaining_original = []
for name in original_failures:
    target = mapping.get(name, name)
    passes = [item for item in history.get(target, []) if item['result'] == 'Passed' and item['endUtc'] > original.get('end-time')]
    if passes:
        closures.append({'original': name, 'current': target, 'latestPass': max(passes, key=lambda item: item['endUtc'])})
    else:
        remaining_original.append(name)
latest_unclosed_failures = []
mapped_historical_failures = []
for name, items in history.items():
    latest = max(items, key=lambda item: item['endUtc'])
    if latest['result'] == 'Failed' and latest['endUtc'] >= original.get('end-time'):
        mapped_passes = [item for item in history.get(mapping.get(name), [])
                         if item['result'] == 'Passed' and item['endUtc'] > latest['endUtc']]
        if name in mapping and mapped_passes:
            mapped_historical_failures.append({'original': name, 'current': mapping[name],
                'historicalFailure': latest, 'laterMappedPass': max(mapped_passes, key=lambda item: item['endUtc'])})
            continue
        latest_unclosed_failures.append({'fullname': name, **latest})

application = json.loads((ROOT / 'Reports/BlockTerrain/BuildZoneColumnCache15/application.json').read_text(encoding='utf-8-sig'))
application_time = application['utc'].split('.')[0].replace('T', ' ') + 'Z'
post_application = [item for item in completed if item['endUtc'] >= application_time]
original_classes = {item.get('classname') for item in original.findall('.//test-case')}
result = {
    'auditedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'scope': 'Reports-only source/XML inventory. No Editor, Unity CLI, Player, build, compile or test execution.',
    'assembly': 'Splatoon.Tests',
    'fullSelector': {'mode': 'editor', 'filter': 'Splatoon.Tests', 'filter_type': 'assembly', 'include_explicit': False,
                     'async_tests': True, 'timeout': 7200},
    'runnerDefaultTimeoutSeconds': 1200,
    'sourceFixtures': sorted(classes.values(), key=lambda item: item['selector']),
    'sourceFixtureCount': len(classes),
    'originalClassCount': len(original_classes),
    'sourceFixturesAbsentFromOriginal866Classes': sorted(set(classes) - original_classes),
    'sourceFixturesWithNoCompletedBlockTerrainXml': sorted(name for name, item in classes.items() if 'latestCompletedXml' not in item),
    'explicitSourceSelectors': sorted(explicit, key=lambda item: item['selector']),
    'original866': {'total': 866, 'passed': 788, 'failed': 78, 'path': 'Reports/BlockTerrain/UnityTests/20261010-050427.xml'},
    'originalFailuresWithLaterPass': len(closures),
    'remainingOriginalFailures': remaining_original,
    'latestUnclosedXmlFailures': sorted(latest_unclosed_failures, key=lambda item: item['fullname']),
    'mappedHistoricalFailuresClosedByCurrentParameterCases': mapped_historical_failures,
    'completedXmlCountRead': len(completed),
    'rejectedXml': rejected,
    'postColumnCache15ApplicationCompletedXml': post_application,
    'latestCompletedXml': completed[-12:],
    'originalFailureClosures': closures,
    'latestDirectedRunner': {'session': 8092,
        'command': 'Python Reports/BlockTerrain/run-editor-filters.py --report Reports/BlockTerrain/column-cache15-editor-results.json BlockBuildQueryTests BlockSafetyTests BlockTerrainPublicationTests',
        'actualDirectedTerminalCounts': {'BlockBuildQueryTests': 72, 'BlockSafetyTests': 42, 'BlockTerrainPublicationTests': 13},
        'outerObserverTimedOutForPublication': True,
        'wasResubmitted': False,
        'boundary': 'Session/outer timeout/no resubmission supplied by root; XML actual terminal leaf counts independently read. Assembly testcasecount1551 in Publication XML is discovery only, not 1551 executed.'},
    'limitations': ['Source fixture extraction is not runtime test discovery or a test-case count.',
        'Latest historical XML per fixture is evidence for that execution only; shared source changes invalidate a blanket current-pass claim.',
        'No directory/file hashes were computed in this inventory. Existing historical audits and all original XML remain unchanged.'],
}
(OUT / 'inventory.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
(OUT / 'source-class-selectors.txt').write_text('\n'.join(sorted(classes)) + '\n', encoding='utf-8')
print(json.dumps({key: result[key] for key in ['sourceFixtureCount', 'originalClassCount', 'originalFailuresWithLaterPass',
    'remainingOriginalFailures', 'latestUnclosedXmlFailures', 'postColumnCache15ApplicationCompletedXml', 'rejectedXml']}, ensure_ascii=False))
