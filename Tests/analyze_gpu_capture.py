#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Run via qrenderdoc --python; analyze only the explicitly supplied synthetic Unity capture."""
import json
import math
import os
from pathlib import Path
import struct
import traceback

import renderdoc as rd


def walk(actions):
    for action in actions:
        yield action
        yield from walk(action.children)


def describe(value):
    return {name: str(getattr(value, name)) for name in dir(value)
            if not name.startswith('_') and not callable(getattr(value, name))}


def vectors(values, count):
    names = 'xyzw'[:count]
    return [tuple(value[name] for name in names) for value in values]


def nearest_error(values, reference):
    distances = [min(sum((a - b) ** 2 for a, b in zip(v, r)) for r in reference) for v in values]
    return math.sqrt(sum(distances) / len(distances)), math.sqrt(max(distances))


def inspect_draw(controller, pipe, mesh, reference, root, event):
    if mesh.vertexResourceId == rd.ResourceId() or mesh.indexByteStride not in (2, 4):
        raise RuntimeError('Unsupported synthetic PostVS layout')
    raw_indices = controller.GetBufferData(mesh.indexResourceId, mesh.indexByteOffset, mesh.numIndices * mesh.indexByteStride)
    indices = struct.unpack('<' + ('H' if mesh.indexByteStride == 2 else 'I') * mesh.numIndices, raw_indices)
    used = sorted(set(index + mesh.baseVertex for index in indices))
    raw_post = controller.GetBufferData(mesh.vertexResourceId, mesh.vertexByteOffset, (max(used) + 1) * mesh.vertexByteStride)
    post = [struct.unpack_from('<4f', raw_post, index * mesh.vertexByteStride) for index in used]
    expected_clip = vectors(reference['originalClip'], 4)
    direct_rms, direct_max = nearest_error(post, expected_clip)
    flipped_rms, flipped_max = nearest_error(post, [(x, -y, z, w) for x, y, z, w in expected_clip])
    # Unity's render-texture GPU projection and Vulkan gl_Position can differ in Y convention.
    # Compare both global conventions explicitly, without altering the geometry or tolerance.
    y_flip = flipped_max < direct_max
    post_rms, post_max = (flipped_rms, flipped_max) if y_flip else (direct_rms, direct_max)
    attrs = pipe.GetVertexInputs()
    position = attrs[0]
    if position.format.compCount != 3 or position.format.compByteWidth != 4 or position.perInstance:
        raise RuntimeError('Synthetic position attribute format changed')
    buffer = pipe.GetVBuffers()[position.vertexBuffer]
    raw_input = controller.GetBufferData(buffer.resourceId, buffer.byteOffset, reference['vertexCount'] * buffer.byteStride)
    positions = [struct.unpack_from('<3f', raw_input, index * buffer.byteStride + position.byteOffset)
                 for index in range(reference['vertexCount'])]
    input_rms, input_max = nearest_error(positions, vectors(reference['encoded'], 3))
    input_plain_rms, _ = nearest_error(positions, vectors(reference['original'], 3))
    key_locations = []
    for block in pipe.GetConstantBlocks(rd.ShaderStage.Vertex):
        desc = block.descriptor
        data = bytes(controller.GetBufferData(desc.resource, desc.byteOffset, desc.byteSize))
        # Public, fixed values of this owned fixture, never a user's avatar keys.
        offset = data.find(struct.pack('<4f', 83.0, 127.0, 191.0, 239.0))
        if offset >= 0:
            key_locations.append({'blockIndex': block.access.index, 'relativeByteOffset': offset})
    # Export projected geometry fetched from the replay, without applying the LAG decoder on CPU.
    dense = {index: i + 1 for i, index in enumerate(used)}
    lines = ['# Synthetic Unity fixture: captured PostVS NDC geometry, not general rest-pose reconstruction']
    lines += ['v ' + ' '.join(str(v / p[3]) for v in p[:3]) for p in post]
    lines += ['f ' + ' '.join(str(dense[index + mesh.baseVertex]) for index in indices[i:i + 3])
              for i in range(0, len(indices), 3)]
    (root / ('captured-post-vs-' + str(event) + '.obj')).write_text('\n'.join(lines) + '\n')
    return {'event': event, 'postVSVertices': len(post), 'indices': mesh.numIndices,
            'postVSOriginalClipRms': post_rms, 'postVSOriginalClipMax': post_max,
            'projectionYConventionFlipped': y_flip, 'unadjustedClipRms': direct_rms,
            'inputEncodedRms': input_rms, 'inputEncodedMax': input_max,
            'inputOriginalRms': input_plain_rms, 'runtimeKeyVectorLocations': key_locations,
            'decodedGeometryObservable': post_max < 1e-5 and input_max < 1e-6 and input_plain_rms > .01,
            'knownSyntheticRuntimeKeysObservable': bool(key_locations)}


def run(root):
    reference = json.loads((root / 'synthetic-reference.json').read_text())
    if reference['fixture'] != 'procedural Unity sphere; no commercial assets' or reference['graphics'] != 'Vulkan':
        raise ValueError('Only the synthetic Unity Vulkan fixture is accepted')
    capture = root / 'legacy-unlocked_capture.rdc'
    cap = rd.OpenCaptureFile()
    controller = None
    try:
        opened = cap.OpenFile(str(capture), '', None)
        if opened != rd.ResultCode.Succeeded:
            raise RuntimeError('OpenFile failed: ' + str(opened))
        result, controller = cap.OpenCapture(rd.ReplayOptions(), None)
        if result != rd.ResultCode.Succeeded:
            raise RuntimeError('Replay failed: ' + str(result))
        draws = [a for a in walk(controller.GetRootActions()) if a.flags & rd.ActionFlags.Drawcall]
        actions = []
        analyses = []
        for action in draws:
            if action.numIndices != len(reference['indices']):
                continue
            controller.SetFrameEvent(action.eventId, True)
            pipe = controller.GetPipelineState()
            refl = pipe.GetShaderReflection(rd.ShaderStage.Vertex)
            if refl is None:
                continue
            mesh = controller.GetPostVSData(0, 0, rd.MeshDataStage.VSOut)
            analyses.append(inspect_draw(controller, pipe, mesh, reference, root, action.eventId))
            actions.append({'event': action.eventId, 'numIndices': action.numIndices,
                            'vertexInputs': [describe(v) for v in pipe.GetVertexInputs()],
                            'vertexBuffers': [describe(v) for v in pipe.GetVBuffers()],
                            'postVS': describe(mesh),
                            'signature': [describe(v) for v in refl.outputSignature],
                            'constantBlocks': [{'name': b.name, 'variables': [v.name for v in b.variables]} for b in refl.constantBlocks],
                            'constantBindings': [{'access': describe(v.access), 'descriptor': describe(v.descriptor)} for v in pipe.GetConstantBlocks(rd.ShaderStage.Vertex)],
                            'constantReadSignature': controller.GetCBufferVariableContents.__doc__})
        if not actions:
            raise RuntimeError('Synthetic mesh draw not found')
        (root / 'renderdoc-inspection.json').write_text(json.dumps({'status': 'inspected', 'draws': actions}, indent=2))
        if not any(a['decodedGeometryObservable'] and a['knownSyntheticRuntimeKeysObservable'] for a in analyses):
            raise RuntimeError('Expected legacy GPU exposure was not demonstrated: ' + json.dumps(analyses))
        return {'status': 'verified', 'candidateDraws': len(actions), 'onlySyntheticUnity': True,
                'capture': capture.name, 'renderdocVersion': rd.GetVersionString(), 'draws': analyses,
                'scope': 'static synthetic geometry in Unity Vulkan; no VRChat process inspected'}
    finally:
        if controller is not None:
            controller.Shutdown()
        cap.Shutdown()


if __name__ == '__main__':
    root = Path(os.environ['LAG_GPU_AUDIT_DIRECTORY']).resolve()
    try:
        report = run(root)
        code = 0
    except Exception:
        report = {'status': 'failed', 'error': traceback.format_exc()}
        code = 1
    (root / 'gpu-analysis.json').write_text(json.dumps(report, indent=2))
    # qrenderdoc otherwise opens its interactive UI after --python. Close only this analysis process.
    os._exit(code)
