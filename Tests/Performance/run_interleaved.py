#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Paired within-process control. Run AFTER run_benchmark.py, using its frozen bundles."""
import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import random
import shutil
import statistics
import subprocess

from run_benchmark import ROOT, SCOPE, describe, environment, write_json


def build(unity, output):
    project = ROOT / 'PerformancePlayerProject'
    if (project / '.lag-owned-performance-project').read_text() != 'owned-fixtures-only\n':
        raise ValueError('Explicit disposable player project required.')
    for source in (ROOT / 'Tests/Performance').rglob('*'):
        if source.is_file() and source.suffix in ('.cs', '.asmdef'):
            target = project / 'Assets/Performance' / source.relative_to(ROOT / 'Tests/Performance')
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
    env = environment()
    env.update(LAG_PERFORMANCE_MODE='interleaved', LAG_PERFORMANCE_OUTPUT=str(output / 'control'))
    subprocess.run([str(unity), '-batchmode', '-quit', '-force-vulkan', '-projectPath', str(project),
                    '-executeMethod', 'LinuxAvatarGuard.Performance.PlayerBuild.Run',
                    '-logFile', str(output / 'control/player-build.log')], env=env, check=True)


def run(output):
    env = environment()
    env['LAG_PERFORMANCE_MODE'] = 'interleaved'
    for instances in (1, 16):
        folder = output / 'control' / str(instances)
        if folder.exists():
            raise ValueError('Use a fresh control output; existing results are preserved.')
        folder.mkdir(parents=True)
        command = [str(output / 'control/player/lag-performance.x86_64'), '-force-vulkan',
                   '-screen-fullscreen', '0', '-screen-width', '1920', '-screen-height', '1080',
                   '-logFile', str(folder / 'player.log'), '--lag-config', str(output / 'fixtures.json'),
                   '--lag-instances', str(instances), '--lag-output', str(folder)]
        print('Interleaved own player:', instances, 'instances, 12 paired rounds', flush=True)
        subprocess.run(command, env=env, check=True, timeout=180, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def bootstrap_median(values):
    # Resample paired ROUNDS, not individual frames: avoid treating correlated frames as repeats.
    rng = random.Random(5869)
    medians = sorted(statistics.median(rng.choices(values, k=len(values))) for _ in range(10000))
    return {'paired_rounds': len(values), 'median': statistics.median(values),
            'min': min(values), 'max': max(values), 'bootstrap95Lower': medians[249], 'bootstrap95Upper': medians[9749],
            'values': values, 'note': 'Exploratory paired-round bootstrap, no familywise adjustment or fixed GPU clocks.'}


def analyze(output):
    cases = []
    for instances in (1, 16):
        folder = output / 'control' / str(instances)
        report = json.loads((folder / 'run.json').read_text())
        if report['scope'] != SCOPE or report['status'] != 'measured' or report['mode'] != 'within-process-interleaved':
            raise ValueError('Wrong/unmeasured control run.')
        with (folder / 'frames.csv').open() as stream:
            frames = list(csv.DictReader(stream))
        if len(frames) != report['samples']:
            raise ValueError('Control frame count differs.')
        blocks = []
        for round_number in range(12):
            for variant in ('original', 'legacy', 'forge'):
                rows = [f for f in frames if f['round'] == str(round_number) and f['variant'] == variant]
                if len(rows) < 50 or len({r['timestamp'] for r in rows}) != len(rows):
                    raise ValueError('Insufficient unique frames in a paired block.')
                if statistics.median(float(r['draw_calls']) for r in rows)!=2*instances or statistics.median(float(r['triangles']) for r in rows)!=49152*instances:
                    raise ValueError('Steady-state geometry/render counts differ within a block.')
                blocks.append({'round': round_number, 'variant': variant,
                    'metrics': {key: describe([float(f[key]) for f in rows]) for key in
                                ('gpu_ms', 'cpu_main_ms', 'cpu_render_ms', 'interval_ms')}})
        groups = []
        for variant in ('original', 'legacy', 'forge'):
            selected = [b for b in blocks if b['variant'] == variant]
            group = {'variant': variant, 'metrics': {}, 'paired_overhead_ms': {}}
            for metric in ('gpu_ms', 'cpu_main_ms', 'cpu_render_ms', 'interval_ms'):
                group['metrics'][metric] = {'median': statistics.median(b['metrics'][metric]['median'] for b in selected),
                    'p95': statistics.median(b['metrics'][metric]['p95'] for b in selected)}
                values = [b['metrics'][metric]['median'] - next(x for x in blocks if x['round'] == b['round'] and x['variant'] == 'original')['metrics'][metric]['median'] for b in selected]
                group['paired_overhead_ms'][metric] = bootstrap_median(values)
            # Provisional warning thresholds for future runs with THESE bundles on the same hardware.
            group['suggested_gpu_regression_warning_ms'] = group['metrics']['gpu_ms']['median'] * 1.1 + (
                max(b['metrics']['gpu_ms']['median'] for b in selected) - min(b['metrics']['gpu_ms']['median'] for b in selected))
            groups.append(group)
        cases.append({'instances': instances, 'report': report, 'groups': groups, 'blocks': blocks})
    summary = {'scope': SCOPE, 'vrchatInspected': False, 'cases': cases,
        'allVariantsResident': True, 'memoryCostMeasuredSeparately': True,
        'artifactSha256': {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest() for path in
            [output / 'control/player/lag-performance_Data/Managed/LinuxAvatarGuard.Performance.dll'] +
            [output / 'bundles' / (v + '.bundle') for v in ('original', 'legacy', 'forge')]}}
    write_json(output / 'control/summary.json', summary)
    print(json.dumps({'cases': [{'instances': c['instances'], 'groups': [{'variant': g['variant'],
        'gpu': g['metrics']['gpu_ms'], 'paired': g['paired_overhead_ms']['gpu_ms']} for g in c['groups']]} for c in cases]}, indent=2))
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'evidence/performance')
    parser.add_argument('--build', action='store_true')
    parser.add_argument('--unity', type=Path)
    parser.add_argument('--analyze', action='store_true')
    args = parser.parse_args()
    output = args.output.resolve()
    if json.loads((output / 'fixtures.json').read_text())['scope'] != SCOPE:
        raise ValueError('Frozen owned fixtures required.')
    (output / 'control').mkdir(exist_ok=True)
    if args.build:
        if not args.unity or not args.unity.is_file():
            parser.error('--build requires an installed --unity editor')
        build(args.unity.resolve(), output)
    if not args.analyze:
        run(output)
    analyze(output)


if __name__ == '__main__':
    main()
