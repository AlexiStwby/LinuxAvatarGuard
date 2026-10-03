#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Replay ONLY an explicitly marked owned mip/skinning Vulkan player capture.
Use the already installed/authorized qrenderdoc --python. Downloads nothing.
"""
import hashlib
import json
import os
import math
import struct
from pathlib import Path
import traceback
import renderdoc as rd


def walk(actions):
    for action in actions:
        yield action
        yield from walk(action.children)


def run(root):
    report = json.loads((root / 'run.json').read_text())
    if (report['scope'] != 'owned-procedural-mip-skinned-surface' or
            report['status'] != 'passed' or report['graphics'] != 'Vulkan' or
            not report['capturePerformed'] or report['vrchatAnalyzed'] or report['sdkIncluded']):
        raise ValueError('Passed, explicitly marked own-player Vulkan capture required.')
    files = list(root.glob('owned-mip-skin*.rdc'))
    if len(files) != 1:
        raise ValueError('Exactly one own-player capture required.')
    cap, controller = rd.OpenCaptureFile(), None
    try:
        if cap.OpenFile(str(files[0]), '', None) != rd.ResultCode.Succeeded:
            raise RuntimeError('Own capture cannot open.')
        result, controller = cap.OpenCapture(rd.ReplayOptions(), None)
        if result != rd.ResultCode.Succeeded:
            raise RuntimeError('Own replay failed: ' + str(result))
        dispatches, draws = [], []
        for action in walk(controller.GetRootActions()):
            if action.flags & rd.ActionFlags.Dispatch:
                controller.SetFrameEvent(action.eventId, True)
                pipe = controller.GetPipelineState()
                shader = pipe.GetShaderReflection(rd.ShaderStage.Compute)
                if shader is None:
                    continue
                names = [r.name for r in shader.readOnlyResources] + [r.name for r in shader.readWriteResources]
                dispatches.append({'event': action.eventId, 'name': getattr(action, 'customName', ''),
                    'groups': list(action.dispatchDimension), 'entryPoint': shader.entryPoint,
                    'resources': names, 'constantBlocks': [b.name for b in shader.constantBlocks],
                    'shaderSha256': hashlib.sha256(bytes(shader.rawBytes)).hexdigest()})
            if action.flags & rd.ActionFlags.Drawcall and action.numIndices in (288, 576):
                controller.SetFrameEvent(action.eventId, True)
                pipe = controller.GetPipelineState()
                vertex = pipe.GetShaderReflection(rd.ShaderStage.Vertex)
                if vertex is None:
                    continue
                post = controller.GetPostVSData(0, 0, rd.MeshDataStage.VSOut)
                if post.indexByteStride not in (2, 4) or post.vertexResourceId == rd.ResourceId():
                    raise RuntimeError('Unexpected owned indexed PostVS layout.')
                indices = bytes(controller.GetBufferData(post.indexResourceId, post.indexByteOffset, post.numIndices * post.indexByteStride))
                used = sorted(set(struct.unpack('<' + ('H' if post.indexByteStride == 2 else 'I') * post.numIndices, indices)))
                raw = bytes(controller.GetBufferData(post.vertexResourceId, post.vertexByteOffset, (max(used) + 1) * post.vertexByteStride))
                positions = [struct.unpack_from('<4f', raw, i * post.vertexByteStride) for i in used]
                if not positions or not all(math.isfinite(v) for p in positions for v in p):
                    raise RuntimeError('Nonfinite/empty owned PostVS output.')
                draws.append({'event': action.eventId, 'indices': action.numIndices,
                    'vertexInputs': [v.name for v in pipe.GetVertexInputs()],
                    'vertexConstants': [b.name for b in vertex.constantBlocks],
                    'postVSAvailable': post.vertexResourceId != rd.ResourceId(),
                    'postVSStride': post.vertexByteStride, 'postVSReadVertices': len(positions),
                    'postVSPositionSha256': hashlib.sha256(b''.join(struct.pack('<4f', *p) for p in positions)).hexdigest()})
        if not draws:
            raise RuntimeError('Owned mesh draws missing.')
        skinning = sum(any('inSkin' in n for n in d['resources']) and any('inMatrices' in n for n in d['resources']) for d in dispatches)
        morphs = sum(any('inBlendShapeVertices' in n for n in d['resources']) for d in dispatches)
        if report['gpuSkinningBuildEnabled'] and (not skinning or not morphs):
            raise RuntimeError('A build flag alone is insufficient: owned skeletal and morph compute dispatches required.')
        return {'status': 'verified', 'scope': report['scope'], 'renderdoc': rd.GetVersionString(),
            'capture': files[0].name, 'gpuSkinningBuildEnabled': report['gpuSkinningBuildEnabled'],
            'vrchatAnalyzed': False, 'dispatches': dispatches, 'draws': draws,
            'dispatchCount': len(dispatches),
            'skeletalSkinningDispatches': skinning, 'blendshapeDispatches': morphs,
            'note': 'A GPU Skinning build setting does not prove a GPU skinning path. No dispatch is reported when none was captured; resource names must identify executed skinning.'}
    finally:
        if controller is not None:
            controller.Shutdown()
        cap.Shutdown()


if __name__ == '__main__':
    root = Path(os.environ['LAG_MIP_SKIN_CAPTURE_OUTPUT']).resolve()
    try:
        report, code = run(root), 0
    except Exception:
        report, code = {'status': 'failed', 'error': traceback.format_exc()}, 1
    (root / 'gpu-dispatch-inspection.json').write_text(json.dumps(report, indent=2) + '\n')
    os._exit(code)
