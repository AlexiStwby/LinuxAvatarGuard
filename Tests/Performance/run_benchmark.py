#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Build and measure only an SDK-free Unity Linux player with owned procedural fixtures.

No downloads, system changes, VRChat access or third-party process capture. Run from the
source checkout; an already installed Unity 2022.3.22f1 Linux editor is required for --build.
"""
import argparse
import csv
import hashlib
import itertools
import json
import math
import os
from pathlib import Path
import re
import shutil
import statistics
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
SCOPE = 'owned-procedural-rigid-liltoon-fixture'


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + '\n')


def environment():
    env = dict(os.environ)
    env['LAG_PERFORMANCE_ALLOWED'] = 'owned-fixtures-only'
    # Timing runs must not inherit a capture/overlay injection from another invocation.
    env.pop('LD_PRELOAD', None)
    env.pop('ENABLE_VULKAN_RENDERDOC_CAPTURE', None)
    return env


def build(unity, output):
    validation = ROOT / 'ValidationProject'
    if not (validation / 'ProjectSettings/ProjectVersion.txt').is_file():
        raise ValueError('Prepare the disposable ValidationProject with the pinned lilToon first.')
    shutil.copyfile(ROOT / 'Tests/LAGPerformanceValidation.cs', validation / 'Assets/Editor/LAGPerformanceValidation.cs')
    env = environment()
    env.update(LAG_BINDING_AUDIT_ALLOWED='synthetic-unity-only', LAG_PERFORMANCE_OUTPUT=str(output))
    for project, method, log in [(validation, 'LAGPerformanceValidation.Run', 'fixture-build.log')]:
        print('Building owned fixtures', flush=True)
        subprocess.run([str(unity), '-batchmode', '-quit', '-force-vulkan', '-projectPath', str(project),
                        '-executeMethod', method, '-logFile', str(output / log)], env=env, check=True)
    project = ROOT / 'PerformancePlayerProject'
    marker = project / '.lag-owned-performance-project'
    if project.exists() and (not marker.is_file() or marker.read_text() != 'owned-fixtures-only\n'):
        raise ValueError('Refusing to modify an unmarked player project.')
    project.mkdir(exist_ok=True)
    marker.write_text('owned-fixtures-only\n')
    for folder in ('Assets', 'Packages', 'ProjectSettings'):
        (project / folder).mkdir(exist_ok=True)
    # Built-in modules only: this step does not install any downloaded UPM package.
    write_json(project / 'Packages/manifest.json', {'dependencies': {
        'com.unity.modules.' + name: '1.0.0' for name in
        ('assetbundle', 'jsonserialize', 'imageconversion', 'screencapture', 'imgui')}})
    shutil.copyfile(validation / 'ProjectSettings/ProjectVersion.txt', project / 'ProjectSettings/ProjectVersion.txt')
    target = project / 'Assets/Performance'
    target.mkdir(exist_ok=True)
    for source in (ROOT / 'Tests/Performance').rglob('*'):
        if source.is_file() and source.suffix in ('.cs', '.asmdef'):
            destination = target / source.relative_to(ROOT / 'Tests/Performance')
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)
    print('Building SDK-free release player', flush=True)
    subprocess.run([str(unity), '-batchmode', '-quit', '-force-vulkan', '-projectPath', str(project),
                    '-executeMethod', 'LinuxAvatarGuard.Performance.PlayerBuild.Run',
                    '-logFile', str(output / 'player-build.log')], env=env, check=True)


def process_memory(pid):
    """Read only THIS launched player's DRM clients/RSS; deduplicate duplicated fds."""
    result = {'elapsed_s': time.monotonic(), 'unix_ms': int(time.time() * 1000), 'clients': [], 'rss_bytes': None}
    base = Path('/proc') / str(pid)
    try:
        status = (base / 'status').read_text()
        match = re.search(r'^VmRSS:\s+(\d+) kB', status, re.M)
        if match:
            result['rss_bytes'] = int(match[1]) * 1024
        seen = set()
        for info in (base / 'fdinfo').iterdir():
            try:
                fields = dict(line.split(':', 1) for line in info.read_text().splitlines() if ':' in line)
            except (OSError, ValueError):
                continue
            if 'drm-client-id' not in fields:
                continue
            client = (fields.get('drm-pdev', '').strip(), fields['drm-client-id'].strip())
            if client in seen:
                continue
            seen.add(client)
            memory = {}
            for key, value in fields.items():
                if key.startswith(('drm-total-', 'drm-resident-', 'drm-shared-', 'drm-active-', 'drm-memory-')):
                    match = re.fullmatch(r'\s*(\d+)\s*(KiB|MiB|B)?\s*', value)
                    if match:
                        memory[key] = int(match[1]) * {'KiB': 1024, 'MiB': 1048576, 'B': 1, None: 1}[match[2]]
            result['clients'].append({'pci': client[0], 'client_id': client[1], 'bytes': memory})
    except OSError:
        pass
    return result


def run_one(output, variant, instances, repeat, duration, images):
    folder = output / 'runs' / f'{instances}-{repeat}-{variant}'
    if folder.exists():
        raise ValueError('Run folder already exists; use a fresh --output or --analyze: ' + str(folder))
    folder.mkdir(parents=True)
    player = output / 'player/lag-performance.x86_64'
    command = [str(player), '-force-vulkan', '-screen-fullscreen', '0', '-screen-width', '1920', '-screen-height', '1080',
               '-logFile', str(folder / 'player.log'), '--lag-config', str(output / 'fixtures.json'),
               '--lag-variant', variant, '--lag-instances', str(instances), '--lag-duration', str(duration),
               '--lag-output', str(folder), '--lag-images', '1' if images else '0']
    print(f'Measuring {variant}, {instances} instances, repetition {repeat}', flush=True)
    process = subprocess.Popen(command, env=environment(), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    start = time.monotonic()
    memory = []
    try:
        while process.poll() is None:
            if (folder / 'ready.json').is_file():
                sample = process_memory(process.pid)
                sample['elapsed_s'] -= start
                memory.append(sample)
            if time.monotonic() - start > duration + 120:
                raise TimeoutError('Own player exceeded startup/measurement timeout.')
            time.sleep(.5)
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        write_json(folder / 'process-memory.json', memory)
    if process.returncode:
        raise RuntimeError(f'Own player exited {process.returncode}; inspect {folder / "player.log"}')
    return folder


def describe(values):
    values = sorted(v for v in values if math.isfinite(v) and v >= 0)
    if not values:
        return {'available': False, 'samples': 0}
    return {'available': True, 'samples': len(values), 'median': statistics.median(values),
            'p95': values[math.ceil(len(values) * .95) - 1], 'min': values[0], 'max': values[-1]}


def image_difference(a, b):
    from PIL import Image, ImageChops, ImageStat
    with Image.open(a) as left, Image.open(b) as right:
        if left.size != right.size:
            raise ValueError('Screenshot resolutions differ.')
        return sum(ImageStat.Stat(ImageChops.difference(left.convert('RGB'), right.convert('RGB'))).mean) / (3 * 255)


def analyze(output):
    fixtures = json.loads((output / 'fixtures.json').read_text())
    runs = []
    for path in sorted((output / 'runs').glob('*/run.json')):
        run = json.loads(path.read_text())
        if run['scope'] != SCOPE or run['status'] != 'measured' or run['graphics'] != 'Vulkan':
            raise ValueError('Unmeasured/unexpected fixture run: ' + str(path))
        with (path.parent / 'frames.csv').open() as stream:
            frames = list(csv.DictReader(stream))
        if len(frames) != run['samples'] or run['uniqueGpuSamples'] < len(frames) * .8:
            raise ValueError('Insufficient unique GPU frame timings.')
        run['directory'] = str(path.parent.relative_to(output))
        run['metrics'] = {key: describe([float(f[key]) for f in frames]) for key in frames[0] if key not in ('timestamp', 'elapsed_s')}
        if run['metrics']['draw_calls'].get('median') != fixtures['renderersPerInstance'] * run['instances']:
            raise ValueError('Unexpected draw count or missing rendering counter: ' + str(path))
        if run['metrics']['triangles'].get('median') != fixtures['trianglesPerInstance'] * run['instances']:
            raise ValueError('The measured geometry differs between variants: ' + str(path))
        run['average_fps'] = (len(frames) - 1) / float(frames[-1]['elapsed_s'])
        memory = json.loads((path.parent / 'process-memory.json').read_text())
        memory = [m for m in memory if run['measurementStartedUnixMs'] <= m['unix_ms'] <= run['measurementEndedUnixMs']]
        fields = sorted({k for m in memory for c in m['clients'] for k in c['bytes']})
        run['process_memory'] = {k: describe([sum(c['bytes'].get(k, 0) for c in m['clients']) for m in memory if m['clients']]) for k in fields}
        run['process_memory']['rss_bytes'] = describe([m['rss_bytes'] for m in memory if m['rss_bytes'] is not None])
        runs.append(run)
    groups = []
    for count in sorted({r['instances'] for r in runs}):
        baseline = output / 'runs' / f'{count}-1-original/unlocked.png'
        for variant in ('original', 'legacy', 'forge'):
            members = [r for r in runs if r['instances'] == count and r['variant'] == variant]
            if not members:
                continue
            group = {'instances': count, 'variant': variant, 'repetitions': len(members), 'metrics': {}}
            for metric in members[0]['metrics']:
                measured = [m['metrics'][metric] for m in members if m['metrics'][metric]['available']]
                group['metrics'][metric] = ({'available': True, 'median': statistics.median(v['median'] for v in measured),
                    'p95': statistics.median(v['p95'] for v in measured), 'run_median_min': min(v['median'] for v in measured),
                    'run_median_max': max(v['median'] for v in measured)} if measured else {'available': False})
            screenshot = output / 'runs' / f'{count}-1-{variant}/unlocked.png'
            group['unlocked_image_difference'] = image_difference(baseline, screenshot)
            if group['unlocked_image_difference'] > .002:
                raise ValueError('Visual parity failed: ' + variant)
            if variant != 'original':
                group['locked_image_difference'] = image_difference(baseline, screenshot.with_name('locked.png'))
                if group['locked_image_difference'] < .003:
                    raise ValueError('Locked visual protection fixture failed: ' + variant)
            groups.append(group)
    if not groups:
        raise ValueError('No complete measurements found.')
    hardware = {(r['unity'], r['gpu'], r['cpu'], r['driver'], r['width'], r['height'], r['development'], r['vsync']) for r in runs}
    if len(hardware) != 1:
        raise ValueError('Hardware or player configuration changed between runs.')
    for group in groups:
        original = next(g for g in groups if g['instances'] == group['instances'] and g['variant'] == 'original')
        group['overhead_vs_original_ms'] = {k: group['metrics'][k]['median'] - original['metrics'][k]['median'] for k in
            ('interval_ms', 'cpu_main_ms', 'cpu_render_ms', 'gpu_ms') if group['metrics'][k]['available'] and original['metrics'][k]['available']}
        members = [r for r in runs if r['instances'] == group['instances'] and r['variant'] == group['variant']]
        pairs = []
        for member in members:
            repeat = member['directory'].split('/')[1].split('-')[1]
            baseline = next(r for r in runs if r['instances'] == group['instances'] and r['variant'] == 'original' and r['directory'].split('/')[1].split('-')[1] == repeat)
            pairs.append(member['metrics']['gpu_ms']['median'] - baseline['metrics']['gpu_ms']['median'])
        group['paired_gpu_overhead_ms'] = {'median': statistics.median(pairs), 'min': min(pairs), 'max': max(pairs), 'values': pairs}
        # Provisional future regression limits, anchored to these repeated measurements.
        # 10% headroom plus the observed repeat range is a policy, not a VRChat performance claim.
        group['suggested_regression_limits_ms'] = {k: group['metrics'][k]['median'] * 1.1 +
            group['metrics'][k]['run_median_max'] - group['metrics'][k]['run_median_min'] for k in
            ('cpu_main_ms', 'cpu_render_ms', 'gpu_ms') if group['metrics'][k]['available']}
    summary = {'scope': SCOPE, 'vrchatInspected': False, 'skinningTested': False, 'sdkProcessed': False,
               'timingInstrumentationEnabled': True, 'runPlan': json.loads((output / 'run-plan.json').read_text()),
               'memoryNote': 'Unity GPU buffer bytes and distinct own-process DRM client memory classes. Shared buffers can occur in multiple clients; these are not unique physical card VRAM totals.',
               'groups': groups, 'runs': runs, 'fixtures': fixtures}
    summary['artifactSha256'] = {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in [output / 'fixtures.json', output / 'player/lag-performance.x86_64', output / 'player/lag-performance_Data/Managed/LinuxAvatarGuard.Performance.dll'] +
        [output / variant['bundle'] for variant in fixtures['variants']]}
    write_json(output / 'summary.json', summary)
    try:
        import matplotlib
        matplotlib.use('Agg')
        import matplotlib.pyplot as plt
        fig, axes = plt.subplots(1, 2, figsize=(11, 4.2))
        for ax, count in zip(axes, (1, 16)):
            selected = [g for g in groups if g['instances'] == count]
            for x, group in enumerate(selected):
                metric = group['metrics']['gpu_ms']
                ax.bar(x, metric['median'], color={'original': '#718096', 'legacy': '#38a169', 'forge': '#805ad5'}[group['variant']])
                ax.plot(x, metric['p95'], 'k_', markersize=17)
                ax.plot([x, x], [metric['run_median_min'], metric['run_median_max']], color='black')
            ax.set_xticks(range(len(selected)), [g['variant'] for g in selected])
            ax.set_title(f'{count} rigid fixture(s), 1080p Vulkan')
            ax.set_ylabel('GPU ms (median; black mark = p95)')
            ax.grid(axis='y', alpha=.2); ax.set_axisbelow(True)
        fig.suptitle('Linux Avatar Guard — own Unity player, instrumented release build')
        fig.tight_layout(); fig.savefig(output / 'gpu-comparison.png', dpi=170); plt.close(fig)
    except ImportError:
        pass  # The raw report does not require external plotting libraries.
    print(json.dumps({'runs': len(runs), 'groups': [{k: g[k] for k in ('instances', 'variant', 'overhead_vs_original_ms', 'unlocked_image_difference')} for g in groups]}, indent=2))
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', type=Path)
    parser.add_argument('--output', type=Path, default=ROOT / 'evidence/performance')
    parser.add_argument('--build', action='store_true')
    parser.add_argument('--analyze', action='store_true')
    parser.add_argument('--repetitions', type=int, choices=(3, 6), default=6)
    parser.add_argument('--duration', type=float, default=8)
    args = parser.parse_args()
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=True)
    if args.analyze:
        analyze(output); return
    if args.build:
        if args.unity is None or not args.unity.is_file():
            parser.error('--build requires an already installed --unity editor')
        build(args.unity.resolve(), output)
    fixture = json.loads((output / 'fixtures.json').read_text())
    if fixture['scope'] != SCOPE:
        raise ValueError('Only owned procedural fixture manifests are accepted.')
    orders = list(itertools.permutations(('original', 'legacy', 'forge')))
    # Use a Latin square when explicitly requesting the shorter three-repeat run.
    if args.repetitions == 3:
        orders = [('original', 'legacy', 'forge'), ('legacy', 'forge', 'original'), ('forge', 'original', 'legacy')]
    write_json(output / 'run-plan.json', {'instances': [1, 16], 'orders': orders, 'duration_s': args.duration,
        'instrumentation': 'FrameTimingManager + ProfilerRecorder; no RenderDoc injection during timing'})
    for instances in (1, 16):
        for repeat, order in enumerate(orders, 1):
            for variant in order:
                run_one(output, variant, instances, repeat, args.duration, repeat == 1)
    analyze(output)


if __name__ == '__main__':
    main()
