#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Run with the already approved qrenderdoc --python, on this own player's captures only.

SPIR-V operation counts describe compiled intermediate code, NOT native GPU instruction
counts or executed work. Performance measurements run separately without capture injection.
"""
from collections import Counter
import hashlib
import json
import os
import re
from pathlib import Path
import struct
import subprocess
import traceback

import renderdoc as rd


def walk(actions):
    for action in actions:
        yield action
        yield from walk(action.children)


def spirv_counts(raw):
    if len(raw) % 4:
        raise ValueError('Unaligned SPIR-V module')
    words = struct.unpack('<' + 'I' * (len(raw) // 4), raw)
    if len(words) < 5 or words[0] != 0x07230203:
        raise ValueError('Not a SPIR-V module')
    offset, inside, functions = 5, False, 0
    all_ops, function_ops = Counter(), Counter()
    while offset < len(words):
        size, opcode = words[offset] >> 16, words[offset] & 0xffff
        if not size or offset + size > len(words):
            raise ValueError('Invalid SPIR-V instruction')
        if opcode == 54:  # OpFunction, per the SPIR-V binary instruction grammar.
            inside = True
            functions += 1
        all_ops[opcode] += 1
        if inside:
            function_ops[opcode] += 1
        if opcode == 56:  # OpFunctionEnd
            inside = False
        offset += size
    if inside or not functions:
        raise ValueError('Incomplete SPIR-V function graph')
    return {'module_bytes': len(raw), 'module_operations': sum(all_ops.values()),
            'function_operations': sum(function_ops.values()), 'functions': functions,
            'function_opcode_histogram': dict(sorted(function_ops.items()))}


def run(root):
    manifest = json.loads((root / 'fixtures.json').read_text())
    if manifest['scope'] != 'owned-procedural-rigid-liltoon-fixture':
        raise ValueError('Only owned procedural fixtures are accepted')
    reports = []
    for variant in ('original', 'legacy', 'forge'):
        folder = root / 'captures' / variant
        run_report = json.loads((folder / 'run.json').read_text())
        if not run_report['captureOnly'] or run_report['graphics'] != 'Vulkan' or run_report['scope'] != manifest['scope']:
            raise ValueError('This is not an own-player capture run')
        captures = list(folder.glob('owned-player*.rdc'))
        if len(captures) != 1:
            raise ValueError('Expected exactly one own-player capture')
        cap, controller = rd.OpenCaptureFile(), None
        try:
            if cap.OpenFile(str(captures[0]), '', None) != rd.ResultCode.Succeeded:
                raise RuntimeError('OpenFile failed')
            result, controller = cap.OpenCapture(rd.ReplayOptions(), None)
            if result != rd.ResultCode.Succeeded:
                raise RuntimeError('Replay failed: ' + str(result))
            draws = []
            unique = {}
            for action in walk(controller.GetRootActions()):
                if not action.flags & rd.ActionFlags.Drawcall or action.numIndices != manifest['trianglesPerInstance'] * 3 // 2:
                    continue
                controller.SetFrameEvent(action.eventId, True)
                pipe = controller.GetPipelineState()
                stages = {}
                for stage, name in ((rd.ShaderStage.Vertex, 'vertex'), (rd.ShaderStage.Fragment, 'fragment')):
                    reflection = pipe.GetShaderReflection(stage)
                    if reflection is None or reflection.encoding != rd.ShaderEncoding.SPIRV:
                        raise RuntimeError('Expected native Vulkan SPIR-V reflection')
                    raw = bytes(reflection.rawBytes)
                    digest = hashlib.sha256(raw).hexdigest()
                    if digest not in unique:
                        binary = folder / (name + '-' + digest[:16] + '.spv')
                        binary.write_bytes(raw)
                        subprocess.run(['spirv-dis', str(binary), '-o', str(binary.with_suffix('.spvasm'))], check=True)
                        unique[digest] = dict(spirv_counts(raw), stage=name, sha256=digest, binary=binary.name)
                        if name=='vertex' and 'KHR_pipeline_executable_properties' in controller.GetDisassemblyTargets(True):
                            native=controller.DisassembleShader(pipe.GetGraphicsPipelineObject(),reflection,'KHR_pipeline_executable_properties')
                            (folder/(name+'-'+digest[:16]+'.driver.txt')).write_text(str(native))
                            # RenderDoc returns all pipeline stages together for this target.
                            # Parse ONLY the vertex executable section; these are driver-reported
                            # GEN instruction counts, not line counts or estimated GPU cycles.
                            section=str(native).split('======== vertex ========',1)[-1].split('========',1)[0]
                            statistics={m[0].strip():int(m[1]) for m in re.findall(r'^([^\n:]+):\s+(\d+)\s+//',section,re.M)}
                            if 'Instructions' in statistics:unique[digest]['nativeVertexStatistics']=statistics
                    stages[name] = digest
                draws.append({'event': action.eventId, 'indices': action.numIndices, 'shaders': stages})
            # Camera.Render queues the warmup before StartFrameCapture; depending on
            # render-thread flushing, that owned warmup can appear alongside the two draws.
            if len(draws) not in (2, 4):
                raise ValueError('Unexpected owned fixture mesh draw count: ' + str(len(draws)))
            vertex_hashes={d['shaders']['vertex'] for d in draws}
            if len(vertex_hashes)!=(2 if variant=='forge' else 1):
                raise ValueError('Expected one shared or two contextual native vertex programs')
            reports.append({'variant': variant, 'capture': str(captures[0].relative_to(root)),
                            'disassemblyTargets': [str(t) for t in controller.GetDisassemblyTargets(True)], 'draws': draws,
                            'uniqueShaders': list(unique.values())})
        finally:
            if controller is not None:
                controller.Shutdown()
            cap.Shutdown()
    native_available=all('nativeVertexStatistics' in shader for report in reports for shader in report['uniqueShaders'] if shader['stage']=='vertex')
    return {'status': 'verified', 'scope': manifest['scope'], 'vrchatInspected': False,
            'renderdocVersion': rd.GetVersionString(), 'hardwareIsaInstructionCountAvailable': native_available,
            'note': 'SPIR-V operations include function delimiters. Native vertex statistics come from the Intel driver through KHR_pipeline_executable_properties; counts do not imply executed work or frame time.',
            'variants': reports}


if __name__ == '__main__':
    root = Path(os.environ['LAG_PERFORMANCE_OUTPUT']).resolve()
    try:
        report, code = run(root), 0
    except Exception:
        report, code = {'status': 'failed', 'error': traceback.format_exc()}, 1
    (root / 'shader-instructions.json').write_text(json.dumps(report, indent=2) + '\n')
    # Finish this analysis process without opening an interactive RenderDoc UI.
    os._exit(code)
