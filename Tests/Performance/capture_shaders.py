#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Use an ALREADY installed/authorized official RenderDoc on this own player only."""
import argparse
import json
import os
from pathlib import Path
import subprocess

from run_benchmark import ROOT, SCOPE, environment, write_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--renderdoc', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=ROOT / 'evidence/performance')
    args = parser.parse_args()
    output, renderdoc = args.output.resolve(), args.renderdoc.resolve()
    manifest = json.loads((output / 'fixtures.json').read_text())
    if manifest['scope'] != SCOPE:
        raise ValueError('Own procedural fixtures required.')
    library = renderdoc / 'lib/librenderdoc.so'
    if not library.is_file() or not (renderdoc / 'bin/qrenderdoc').is_file():
        raise ValueError('Already installed official RenderDoc required; this script downloads nothing.')
    layer = json.loads((renderdoc / 'etc/vulkan/implicit_layer.d/renderdoc_capture.json').read_text())
    layer['layer']['library_path'] = str(library)
    layers = output / 'capture-layer'
    layers.mkdir(exist_ok=True)
    write_json(layers / 'renderdoc_capture.json', layer)
    env = environment()
    env.update(LD_PRELOAD=str(library), LD_LIBRARY_PATH=str(renderdoc / 'lib'),
               VK_ADD_IMPLICIT_LAYER_PATH=str(layers), ENABLE_VULKAN_RENDERDOC_CAPTURE='1')
    for variant in ('original', 'legacy', 'forge'):
        folder = output / 'captures' / variant
        if folder.exists():
            raise ValueError('Existing captures are preserved; choose a fresh output.')
        folder.mkdir(parents=True)
        subprocess.run([str(output / 'player/lag-performance.x86_64'), '-force-vulkan',
            '-screen-fullscreen', '0', '-screen-width', '1920', '-screen-height', '1080',
            '-logFile', str(folder / 'player.log'), '--lag-config', str(output / 'fixtures.json'),
            '--lag-variant', variant, '--lag-instances', '1', '--lag-output', str(folder), '--lag-capture', '1'],
            env=env, check=True, timeout=120, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    # Capture/replay runs are separate from, and never concurrent with, timing runs.
    env = environment()
    env.update(LAG_PERFORMANCE_OUTPUT=str(output), QT_QPA_PLATFORM='xcb')
    subprocess.run([str(renderdoc / 'bin/qrenderdoc'), '--python', str(Path(__file__).with_name('analyze_shader_capture.py'))],
                   env=env, check=True, timeout=180)


if __name__ == '__main__':
    main()
