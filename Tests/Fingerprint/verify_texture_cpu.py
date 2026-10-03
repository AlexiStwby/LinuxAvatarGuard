#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Independent DCT/HMAC/record reader and CPU image-channel study. Existing NumPy/Pillow only."""
import os
for variable in ("OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS", "VECLIB_MAXIMUM_THREADS", "NUMEXPR_NUM_THREADS"):
    os.environ[variable] = "1"
import argparse
from collections import defaultdict
import hashlib
import hmac
import io
import json
import math
from pathlib import Path
import struct
import subprocess
import time
import numpy as np
from PIL import Image, ImageFilter, DdsImagePlugin

FRAME = 128
BASIS = np.cos(np.pi * (np.arange(FRAME)[None, :] + 0.5) * np.arange(FRAME)[:, None] / FRAME) * np.sqrt(2 / FRAME)
BASIS[0] /= math.sqrt(2)
WEIGHTS = np.array([0.2126, 0.7152, 0.0722])


def dotnet_string(text):
    raw, prefix = text.encode(), bytearray()
    value = len(raw)
    while value >= 128:
        prefix.append((value & 127) | 128)
        value >>= 7
    prefix.append(value)
    return bytes(prefix) + raw


def derive(vector, purpose):
    index, record = vector["contextIndex"], vector["record"]
    identity = hashlib.sha256(f"stage16-fingerprint-{index}".encode()).digest()
    seed = hashlib.sha256(f"stage16-carrier-{index}".encode()).digest()
    header = f"LAG/fingerprint/v1/{purpose}\0texture\0{record['buildId']}\0{record['bindingId']}\0".encode()
    return hmac.digest(seed, header + identity, "sha256")


def pattern(vector):
    seed, step = derive(vector, "carrier"), vector["record"]["step"]
    code = np.unpackbits(np.frombuffer(derive(vector, "codeword")[:16], dtype=np.uint8))
    pool = [(u, v) for u in range(35) for v in range(35) if 3 <= u + v <= 34]
    def integers():
        counter = 0
        while True:
            block = hmac.digest(seed, b"LAG/texture-fingerprint/dct-rgb8/v1/layout\0" + struct.pack(">I", counter), "sha256")
            yield from struct.unpack(">8I", block)
            counter += 1
    draws = integers()
    for i in range(len(pool) - 1, 0, -1):
        limit = 2 ** 32 - 2 ** 32 % (i + 1)
        value = next(draws)
        while value >= limit:
            value = next(draws)
        j = value % (i + 1)
        pool[i], pool[j] = pool[j], pool[i]
    coords = np.array(pool[:384])
    dither = np.array([(int.from_bytes(hmac.digest(seed, b"LAG/texture-fingerprint/dct-rgb8/v1/dither\0" + struct.pack(">I", i), "sha256")[:8], "big") >> 11) * 2 ** -53 * 2 * step for i in range(384)])
    return code, coords, dither


def pixel_hash(rgba):
    size = rgba.shape[0]
    data = dotnet_string("LAG/texture-fingerprint/pixels/v1") + dotnet_string("EncodedSrgbRgb8V1") + dotnet_string("BottomLeftRowMajorRgba8V1")
    return hashlib.sha256(data + struct.pack("<iii", size, size, rgba.nbytes) + rgba.tobytes()).hexdigest()


def authenticate(vector):
    r = vector["record"]
    value = dotnet_string("LAG/texture-fingerprint/record-auth/v1") + struct.pack("<ii", r["schema"], r["algorithmVersion"])
    for name in ("algorithm", "state", "colorDomain", "raster", "buildId", "bindingId", "sourcePixelHash", "markedPixelHash"):
        value += dotnet_string(r[name])
    value += struct.pack("<iii", r["sourceWidth"], r["sourceHeight"], r["usableSymbols"])
    value += struct.pack("<dddd", r["step"], r["maximumMeanAbsoluteRgb"], r["maximumRgb"], r["minimumPsnrDb"])
    return hmac.digest(derive(vector, "carrier"), value, "sha256").hex()


def canonical_luma(rgba):
    rgb = rgba[:, :, :3].astype(np.float64) / 255
    luma, size = rgb @ WEIGHTS, rgba.shape[0]
    if size == FRAME:
        return luma
    if size > FRAME:
        weights = np.maximum(0, np.minimum((np.arange(FRAME)[:, None] + 1) * size / FRAME, np.arange(size)[None, :] + 1) - np.maximum(np.arange(FRAME)[:, None] * size / FRAME, np.arange(size)[None, :])) / (size / FRAME)
        return weights @ luma @ weights.T
    coordinate = np.clip((np.arange(FRAME) + 0.5) * size / FRAME - 0.5, 0, size - 1)
    first = np.floor(coordinate).astype(int)
    second, blend = np.minimum(first + 1, size - 1), coordinate - first
    rows = (1 - blend[:, None]) * luma[first, :] + blend[:, None] * luma[second, :]
    return (1 - blend[None, :]) * rows[:, first] + blend[None, :] * rows[:, second]


def encode(source, vector, marker):
    code, coords, dither = marker
    frame = canonical_luma(source)
    spectrum = BASIS @ frame @ BASIS.T
    values, step = spectrum[coords[:, 0], coords[:, 1]], vector["record"]["step"]
    spectrum[coords[:, 0], coords[:, 1]] = step * (2 * np.rint(((values - dither) / step - np.repeat(code, 3)) / 2) + np.repeat(code, 3)) + dither
    delta = BASIS.T @ spectrum @ BASIS - frame
    scale = source.shape[0] // FRAME
    delta = np.repeat(np.repeat(delta, scale, axis=0), scale, axis=1)
    result = source.copy()
    result[:, :, :3] = np.rint(np.clip(source[:, :, :3].astype(float) + 255 * delta[:, :, None], 0, 255)).astype(np.uint8)
    return result


def observe(rgba, vector, marker):
    if rgba.shape[0] < 32:
        return {"usableSymbols": 0, "matchedSymbols": 0, "usableCarriers": 0, "decision": "InconclusiveCoverage", "confidence": None}
    code, coords, dither = marker
    spectrum = BASIS @ canonical_luma(rgba) @ BASIS.T
    values = (spectrum[coords[:, 0], coords[:, 1]] - dither) / vector["record"]["step"]
    usable = (np.abs(values - np.floor(values) - 0.5) >= 0.06).reshape(128, 3)
    parity = (np.rint(values).astype(np.int64) & 1).reshape(128, 3)
    count, votes = usable.sum(axis=1), (parity * usable).sum(axis=1)
    symbols = (count >= 2) & (2 * votes != count)
    number, matched = int(symbols.sum()), int(((2 * votes > count) == code)[symbols].sum())
    decision = "InconclusiveCoverage" if number < 96 else "ResearchMatch" if matched / number >= 109 / 128 else "NoResearchMatch"
    return {"usableSymbols": number, "matchedSymbols": matched, "usableCarriers": int(usable.sum()), "decision": decision, "confidence": None}


def roundtrip(rgba, format_name, **options):
    # All Pillow operations occur in its top-left raster; convert back to the core's bottom-left.
    buffer = io.BytesIO()
    Image.fromarray(rgba[::-1, :, :3]).save(buffer, format_name, **options)
    buffer.seek(0)
    with Image.open(buffer) as image:
        return np.array(image.convert("RGBA"))[::-1].copy()


def color(rgba, operation):
    result = rgba.copy()
    result[:, :, :3] = np.rint(np.clip(operation(result[:, :, :3].astype(float) / 255), 0, 1) * 255).astype(np.uint8)
    return result


def mip(rgba, linear):
    rgb = rgba[:, :, :3].astype(float) / 255
    if linear:
        rgb = np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)
    size = rgba.shape[0] // 2
    rgb = rgb.reshape(size, 2, size, 2, 3).mean(axis=(1, 3))
    if linear:
        rgb = np.where(rgb <= 0.0031308, rgb * 12.92, 1.055 * rgb ** (1 / 2.4) - 0.055)
    result = np.full((size, size, 4), 255, dtype=np.uint8)
    result[:, :, :3] = np.rint(np.clip(rgb, 0, 1) * 255).astype(np.uint8)
    return result


def attacks(rgba):
    image, size = Image.fromarray(rgba[::-1]), rgba.shape[0]
    result = {"native": rgba, "png": roundtrip(rgba, "PNG"), "jpeg95": roundtrip(rgba, "JPEG", quality=95, subsampling=0),
              "jpeg75": roundtrip(rgba, "JPEG", quality=75, subsampling=0), "jpeg75_420": roundtrip(rgba, "JPEG", quality=75, subsampling=2),
              "dds_bc1_pillow": roundtrip(rgba, "DDS", pixel_format="DXT1"), "dds_bc3_pillow": roundtrip(rgba, "DDS", pixel_format="DXT5"),
              "color_affine": color(rgba, lambda x: x * 1.03 + 0.01), "gamma105": color(rgba, lambda x: x ** 1.05), "gamma120": color(rgba, lambda x: x ** 1.2),
              "blur05": np.array(image.filter(ImageFilter.GaussianBlur(0.5)))[::-1].copy(), "crop_border4": np.array(image.crop((4, 4, size - 4, size - 4)))[::-1].copy(),
              "rotate90": np.array(image.transpose(Image.Transpose.ROTATE_90))[::-1].copy()}
    for ratio in (0.75, 0.5, 0.25):
        result["resize" + str(int(ratio * 100))] = np.array(image.resize((int(size * ratio), int(size * ratio)), Image.Resampling.BILINEAR))[::-1].copy()
    for linear in (False, True):
        first = mip(rgba, linear)
        label = "linear" if linear else "encoded"
        result["mip_" + label + "_1"], result["mip_" + label + "_2"] = first, mip(first, linear)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    parser.add_argument("--mono", required=True, type=Path)
    args = parser.parse_args()
    start = time.monotonic()
    vectors = json.loads((args.folder / "vectors.json").read_text())
    patterns = [pattern(v) for v in vectors]
    images, sources = [], []
    for index, v in enumerate(vectors):
        source = np.frombuffer((args.folder / v["sourceFile"]).read_bytes(), dtype=np.uint8).reshape(v["size"], v["size"], 4)
        marked = np.frombuffer((args.folder / v["markedFile"]).read_bytes(), dtype=np.uint8).reshape(source.shape)
        assert pixel_hash(source) == v["record"]["sourcePixelHash"] and pixel_hash(marked) == v["record"]["markedPixelHash"]
        assert authenticate(v) == v["record"]["authentication"]
        assert np.array_equal(encode(source, v, patterns[index]), marked), ("independent encoding", index)
        observed = observe(marked, v, patterns[index])
        assert observed["matchedSymbols"] == observed["usableSymbols"] == 128
        images.append(marked)
        sources.append(source)
        if v["contextIndex"] == 0 and v["size"] == 128:
            Image.fromarray(source[::-1]).save(args.folder / f"family{v['family']}-source-cpu.png")
            Image.fromarray(marked[::-1]).save(args.folder / f"family{v['family']}-marked-cpu.png")
    queries, expectations, metadata = [], [], []
    def add(rgba, vector, kind, attack, source_vector):
        assert rgba.shape[0] == rgba.shape[1] and np.all(rgba[:, :, 3] == 255)
        name = f"q{len(queries):04d}"
        queries.append((name, vector, rgba))
        expectations.append({"name": name, "vector": vector, "observation": observe(rgba, vectors[vector], patterns[vector])})
        metadata.append({"kind": kind, "attack": attack, "sourceVector": source_vector})
    for index in range(12):
        for label, candidate in attacks(images[index]).items():
            add(candidate, index, "positive", label, index)
            for other in range(index // 4 * 4, index // 4 * 4 + 4):
                if other != index:
                    add(candidate, other, "negative_wrong_build", label, index)
        for other in range(index // 4 * 4, index // 4 * 4 + 4):
            add(sources[index], other, "negative_unmarked", "native", index)
        add(images[index], (index + 4) % 12, "negative_other_source", "native", index)
    for index in range(12, 15):
        for label, candidate in attacks(images[index]).items():
            add(candidate, index, "positive_larger", label, index)
    for family in range(3):
        a, b = family * 4, family * 4 + 1
        average = np.rint((images[a].astype(float) + images[b].astype(float)) / 2).astype(np.uint8)
        for index in (a, b):
            add(average, index, "collusion_contributor", "average_two", a)
    archive = args.folder / "queries.bin"
    with archive.open("wb") as stream:
        stream.write(dotnet_string("LAG-texture-cpu-queries-v1") + struct.pack("<i", len(queries)))
        for name, index, rgba in queries:
            stream.write(dotnet_string(name) + struct.pack("<iiii", index, rgba.shape[1], rgba.shape[0], rgba.nbytes) + rgba.tobytes())
    result_path = args.folder / "csharp-channel-observations.json"
    completed = subprocess.run(["nice", "-n", "19", str(args.mono), str(args.folder / "LAGTextureFingerprintCpu.exe"), "--observe", str(args.folder), str(archive), str(result_path)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    (args.folder / "channel-observer.log").write_text(completed.stdout)
    if completed.returncode:
        raise RuntimeError(completed.stdout)
    actual = json.loads(result_path.read_text())
    assert len(actual) == len(expectations)
    for expected, observed in zip(expectations, actual):
        assert expected == observed, ("C#/Python disagree", expected, observed)
    grouped = defaultdict(lambda: {"trials": 0, "matches": 0, "inconclusive": 0})
    measurements = []
    for value, meta in zip(actual, metadata):
        key, decision = meta["kind"] + ":" + meta["attack"], value["observation"]["decision"]
        grouped[key]["trials"] += 1
        grouped[key]["matches"] += decision == "ResearchMatch"
        grouped[key]["inconclusive"] += decision == "InconclusiveCoverage"
        measurements.append({**meta, **value})
    negatives = [v for v, m in zip(actual, metadata) if m["kind"].startswith("negative")]
    collusion = [v for v, m in zip(actual, metadata) if m["kind"] == "collusion_contributor"]
    summary = {"schema": 1, "independentExactEncodingVectors": len(vectors), "independentRecordAuthenticationVectors": len(vectors),
               "csharpPythonObservationComparisons": len(actual), "observationsAgree": True, "negativeComparisons": len(negatives),
               "negativeResearchMatches": sum(v["observation"]["decision"] == "ResearchMatch" for v in negatives),
               "collusionBothContributorsMatch": sum(all(v["observation"]["decision"] == "ResearchMatch" for v in collusion[i:i+2]) for i in range(0, len(collusion), 2)),
               "groups": dict(sorted(grouped.items())), "gpuUsed": False, "unityStarted": False, "vrchatAccessed": False, "downloads": False,
               "unityBcCompressionTested": False, "cpuBcEncoder": "Existing Pillow DDS DXT1/DXT5, not Unity/Crunch encoder", "visualUnityTested": False,
               "verifierCalibrated": False, "populationFalsePositiveRate": None, "wallSeconds": time.monotonic() - start,
               "dependencies": {"numpy": np.__version__, "pillow": Image.__version__, "ddsPluginSha256": hashlib.sha256(Path(DdsImagePlugin.__file__).read_bytes()).hexdigest()}}
    (args.folder / "independent-reader-results.json").write_text(json.dumps(summary, indent=2) + "\n")
    (args.folder / "channel-measurements.json").write_text(json.dumps(measurements, indent=2) + "\n")
    print(f"Independent texture CPU reader: {len(vectors)} exact encoding/auth vectors; {len(actual)} C#/Python observations agree; {summary['negativeResearchMatches']} negative research matches.")


if __name__ == "__main__":
    main()
