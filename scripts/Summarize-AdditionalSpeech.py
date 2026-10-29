"""Summarize completed installed CPU speech measurements without mixing scopes."""
import json
import statistics
import argparse
import xml.etree.ElementTree as ET
from pathlib import Path

root = Path(__file__).resolve().parents[1]
version = ET.parse(root / 'packaging/AppxManifest.xml').getroot().find('{*}Identity').attrib['Version']
parser = argparse.ArgumentParser()
parser.add_argument('--include-idle-followup', action='store_true')
parser.add_argument('--report-name', default='additional-speech-installed',
                    choices=['additional-speech-installed', 'kitten-mini-installed', 'pocket-installed'])
args = parser.parse_args()
directory = root / 'artifacts/test-results' / args.report_name
report = json.loads((directory / 'additional-speech.json').read_text(encoding='utf-8-sig'))
assert report['complete'] and report['packaged'], 'INSTALLED_REPORT_INCOMPLETE'
failures = [item for item in report['checks'] if not item['passed']]
idle_followup = None
if failures and args.include_idle_followup:
    assert {item['id'] for item in failures} == {'voicevox:metan:normal.idle', 'voicevox:takehiro:normal.idle'}, 'OTHER_CHECK_FAILED'
    idle_followup = json.loads((directory / ('idle-followup-' + version + '.json')).read_text(encoding='utf-8'))
    assert len(idle_followup['checks']) == 2 and all(c['passed'] for c in idle_followup['checks']), 'IDLE_FOLLOWUP_FAILED'
else:
    assert not failures, 'CHECK_FAILED'
passed = sum(bool(item['passed']) for item in report['checks'])
rows = []
for voice in dict.fromkeys(m['voice'] for m in report['measurements']):
    values = [m for m in report['measurements'] if m['voice'] == voice]
    warm = [m['result'] for m in values if m['scenario'] == 'warm']
    if not warm:
        continue
    cold = next(m['result'] for m in values if m['scenario'] == 'cold')
    idle = next((m['result'] for m in values if m['scenario'] == 'idle'), None)
    latency = [m['ClientFirstAudioMilliseconds'] / 1000 for m in warm]
    rows.append({
        'voice': voice, 'coldSeconds': round(cold['ClientFirstAudioMilliseconds'] / 1000, 3),
        'warmMedianSeconds': round(statistics.median(latency), 3),
        'warmMinSeconds': round(min(latency), 3), 'warmMaxSeconds': round(max(latency), 3),
        'workingSetAfterSpeechMiB': [round(min(m['WorkingSetBytes'] for m in warm) / 1048576, 1), round(max(m['WorkingSetBytes'] for m in warm) / 1048576, 1)],
        'privateAfterSpeechMiB': round(warm[-1]['PrivateBytes'] / 1048576, 1),
        'peakShortSpeechMiB': round(max(m['PeakWorkingSetBytes'] for m in warm) / 1048576, 1),
        'idleWorkingSetMiB': round(idle['WorkingSetBytes'] / 1048576, 1) if idle else None,
        'idlePrivateMiB': round(idle['PrivateBytes'] / 1048576, 1) if idle else None,
    })
summary = {'version': version, 'scope': report['measurementScope'], 'passed': passed, 'total': len(report['checks']),
           'failedChecks': failures, 'idleFollowup': idle_followup, 'voices': rows,
           'playback': [m for m in report['measurements'] if m['scenario'] in ('play', 'streamed-paragraph')],
           'cancellation': [m for m in report['measurements'] if m['scenario'] in ('after-cancel', 'cancel-playback')]}
(directory / ('summary-' + version + '.json')).write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
(directory / ('additional-speech-' + version + '.json')).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'passed': passed, 'total': len(report['checks']), 'failedChecks': failures, 'voices': rows}, indent=2))
