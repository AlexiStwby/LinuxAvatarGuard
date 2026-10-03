#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Measure frozen owned TextureGuard bundles in the SDK-free Linux/Vulkan player.

Reuses the Stage 9 probe and own-process memory sampler. No downloads, client access,
capture injection or analysis of any other process. Close VRChat before invoking.
"""
import argparse
import csv
import hashlib
import json
import random
import statistics
import subprocess
from pathlib import Path

from run_benchmark import ROOT, SCOPE, describe, environment, run_one, write_json
from run_interleaved import bootstrap_median

NAMES = ('original', 'mesh', 'texture')


def read_frames(folder, paired=False):
    report = json.loads((folder / 'run.json').read_text())
    if (report['scope'] != SCOPE or report['status'] != 'measured'
            or report['development'] or report['vsync'] or report['colorSpace'] != 'Gamma'
            or not report['frameTimingEnabled'] or (report['width'], report['height']) != (1920, 1080)):
        raise ValueError('Own-player measurement contract failed: ' + str(folder))
    with (folder / 'frames.csv').open() as stream:
        rows = list(csv.DictReader(stream))
    if len(rows) != report['samples'] or len(rows) < 50:
        raise ValueError('Insufficient or mismatched frame samples.')
    if paired and report['variantOrder'] != list(NAMES):
        raise ValueError('Wrong paired variant order.')
    return report, rows


def metrics(rows, instances):
    if (statistics.median(float(r['draw_calls']) for r in rows) != 4 * instances
            or statistics.median(float(r['triangles']) for r in rows) != 384 * instances):
        raise ValueError('Steady geometry/draw counts differ between controlled variants.')
    return {key: describe([float(r[key]) for r in rows]) for key in
            ('gpu_ms', 'cpu_main_ms', 'cpu_render_ms', 'interval_ms')}


def run(output, repeats, duration):
    # Each repeat contains every variant; permute order reproducibly to reduce drift bias.
    rng = random.Random(1212)
    for instances in (1, 16):
        for repeat in range(repeats):
            order = list(NAMES)
            rng.shuffle(order)
            for variant in order:
                run_one(output, variant, instances, repeat, duration, repeat == 0)
    for instances in (1, 16):
        folder = output / 'control' / str(instances)
        folder.mkdir(parents=True, exist_ok=False)
        command = [str(output / 'control/player/lag-performance.x86_64'), '-force-vulkan',
                   '-screen-fullscreen', '0', '-screen-width', '1920', '-screen-height', '1080',
                   '-logFile', str(folder / 'player.log'), '--lag-config', str(output / 'fixtures.json'),
                   '--lag-instances', str(instances), '--lag-output', str(folder)]
        env = environment()
        env['LAG_PERFORMANCE_MODE'] = 'interleaved'
        print('Paired own TextureGuard player:', instances, 'instances, 12 rounds', flush=True)
        subprocess.run(command, env=env, check=True, timeout=180,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def analyze(output, repeats):
    from PIL import Image
    import numpy as np

    cases, image_checks, frames = [], [], 0
    for instances in (1, 16):
        isolated = []
        for variant in NAMES:
            runs = []
            for repeat in range(repeats):
                folder = output / 'runs' / f'{instances}-{repeat}-{variant}'
                report, rows = read_frames(folder)
                frames += len(rows)
                memory = json.loads((folder / 'process-memory.json').read_text())
                runs.append({'report': report, 'metrics': metrics(rows, instances),
                             'rss_median_bytes': statistics.median(m['rss_bytes'] for m in memory if m['rss_bytes'] is not None),
                             'process_memory_file': str((folder / 'process-memory.json').relative_to(output))})
            isolated.append({'variant': variant, 'runs': runs})
        reference = np.asarray(Image.open(output / 'runs' / f'{instances}-0-original/unlocked.png').convert('RGB'), dtype=float) / 255
        for variant in NAMES[1:]:
            current = np.asarray(Image.open(output / 'runs' / f'{instances}-0-{variant}/unlocked.png').convert('RGB'), dtype=float) / 255
            difference = np.abs(reference - current)
            check = {'instances': instances, 'variant': variant, 'mean': float(difference.mean()), 'max': float(difference.max())}
            if check['mean'] > .0015:
                raise ValueError('Own-player unlocked image exceeded the quality gate.')
            image_checks.append(check)
        mesh_image = np.asarray(Image.open(output / 'runs' / f'{instances}-0-mesh/unlocked.png').convert('RGB'), dtype=float) / 255
        texture_image = np.asarray(Image.open(output / 'runs' / f'{instances}-0-texture/unlocked.png').convert('RGB'), dtype=float) / 255
        extra = np.abs(texture_image - mesh_image)
        additional = {'instances': instances, 'variant': 'texture_over_mesh', 'mean': float(extra.mean()),
                      'max': float(extra.max()), 'channels_over_3_bytes': int((extra > 3 / 255 + 1e-9).sum())}
        if additional['mean'] > .0015 or additional['max'] > .012:
            raise ValueError('Additional texture error exceeded the single-mip color budget.')
        image_checks.append(additional)
        report, rows = read_frames(output / 'control' / str(instances), paired=True)
        frames += len(rows)
        if report['rounds'] != 12:
            raise ValueError('Wrong round count.')
        blocks = []
        for round_id in range(12):
            for variant in NAMES:
                block = [r for r in rows if r['round'] == str(round_id) and r['variant'] == variant]
                if len(block) < 50 or len({r['timestamp'] for r in block}) != len(block):
                    raise ValueError('Insufficient/duplicate paired timing samples.')
                blocks.append({'round': round_id, 'variant': variant, 'metrics': metrics(block, instances)})
        paired = []
        for variant in NAMES:
            selected = [b for b in blocks if b['variant'] == variant]
            comparisons = {}
            for baseline in NAMES[:2]:
                comparisons[baseline] = {}
                for key in ('gpu_ms', 'cpu_main_ms', 'cpu_render_ms', 'interval_ms'):
                    values = [b['metrics'][key]['median'] - next(x for x in blocks if x['round'] == b['round'] and x['variant'] == baseline)['metrics'][key]['median'] for b in selected]
                    comparisons[baseline][key] = bootstrap_median(values)
            paired.append({'variant': variant,
                           'gpu_median_ms': statistics.median(b['metrics']['gpu_ms']['median'] for b in selected),
                           'fps_from_median_interval': 1000 / statistics.median(b['metrics']['interval_ms']['median'] for b in selected),
                           'overhead': comparisons})
        cases.append({'instances': instances, 'isolated': isolated, 'paired': paired, 'blocks': blocks, 'paired_report': report})
    paths = sorted((output / 'bundles').rglob('*.bundle')) + [output / 'fixtures.json']
    summary = {'scope': SCOPE, 'scope_detail': 'Two-renderer/two-slot owned rigid unlit opaque albedos; Gamma only; no production/SDK/client validation',
               'vrchatAnalyzed': False, 'frames': frames, 'isolatedRuns': repeats * 6, 'pairedRounds': 24,
               'imageChecks': image_checks, 'cases': cases,
               'bundleBytes': {str(p.relative_to(output)): p.stat().st_size for p in paths if p.suffix == '.bundle'},
               'artifactSha256': {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest() for p in paths},
               'limits': 'Exploratory 12-round paired estimates; no fixed GPU clocks; no stereo, lighting, mipmaps, compression, skinned avatar or VRChat analysis; RSS includes shaders and other engine allocations, not just textures.'}
    write_json(output / 'summary.json', summary)
    print(json.dumps({'frames': frames, 'paired': [{'instances': c['instances'], 'variants': [
        {'variant': g['variant'], 'gpu_ms': g['gpu_median_ms'], 'texture_over_mesh_ms': g['overhead']['mesh']['gpu_ms']} for g in c['paired']]} for c in cases]}, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--repeats', type=int, default=3)
    parser.add_argument('--duration', type=float, default=8)
    parser.add_argument('--analyze', action='store_true')
    args = parser.parse_args()
    if not 1 <= args.repeats <= 10 or not 1 <= args.duration <= 120:
        parser.error('Invalid own benchmark repetition/duration matrix.')
    output = args.output.resolve()
    manifest = json.loads((output / 'fixtures.json').read_text())
    if manifest['scope'] != SCOPE or manifest['interleavedOrder'] != list(NAMES):
        raise ValueError('Frozen TextureGuard fixtures required.')
    if not args.analyze:
        run(output, args.repeats, args.duration)
    analyze(output, args.repeats)


if __name__ == '__main__':
    main()
