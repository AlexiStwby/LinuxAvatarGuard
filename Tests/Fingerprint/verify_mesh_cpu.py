#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Independent protocol reader for the public C# synthetic fixture. Standard library/CPU only."""
import argparse
import hashlib
import hmac
import json
import math
from pathlib import Path
import random
import struct


def positions(path):
    raw = path.read_bytes()
    if not raw or len(raw) % 12:
        raise ValueError("packed Float32 XYZ required")
    return [p for p in struct.iter_unpack("<fff", raw)], raw


def dotnet_string(text):
    data = text.encode("utf-8")
    count, prefix = len(data), bytearray()
    while count >= 128:
        prefix.append((count & 127) | 128)
        count >>= 7
    prefix.append(count)
    return bytes(prefix) + data


def position_hash(raw):
    return hashlib.sha256(dotnet_string("LAG/mesh-fingerprint/positions/v1") + struct.pack("<i", len(raw) // 4) + raw).hexdigest()


def pattern():
    # Public test vectors, unrelated to any avatar or user's private context.
    fingerprint = hashlib.sha256(b"stage15-fingerprint-0").digest()
    seed = hashlib.sha256(b"stage15-carrier-0").digest()
    def derive(purpose):
        header = f"LAG/fingerprint/v1/{purpose}\0mesh\0{'0' * 32}\0{'b' * 64}\0".encode()
        return hmac.digest(seed, header + fingerprint, "sha256")
    carrier = derive("carrier")
    codeword = derive("codeword")[:16]
    bits = [(codeword[i // 8] >> (7 - i % 8)) & 1 for i in range(128)]
    dither = []
    for i in range(128):
        digest = hmac.digest(carrier, b"LAG/mesh-fingerprint/radial-unique/v1/dither\0" + i.to_bytes(4, "big"), "sha256")
        dither.append((int.from_bytes(digest[:8], "big") >> 11) * 2 ** -53 * 2e-5)
    return bits, dither


def observe(points):
    unique = sorted(set(points))
    center = tuple(math.fsum(p[j] for p in unique) / len(unique) for j in range(3))
    radii = [math.sqrt(sum((p[j] - center[j]) ** 2 for j in range(3))) for p in unique]
    radius = max(radii)
    counts, votes = [0] * 128, [0] * 128
    bits, dither = pattern()
    if radius:
        for distance in radii:
            value = distance / radius
            band = math.floor((value - 0.1) / 0.88 * 128)
            if 0 <= band < 128:
                counts[band] += 1
                votes[band] += round((value - dither[band]) / 1e-5) & 1
    usable = [i for i in range(128) if counts[i] >= 4 and votes[i] * 2 != counts[i]]
    matched = sum((votes[i] * 2 > counts[i]) == bits[i] for i in usable)
    return {"usableSymbols": len(usable), "matchedSymbols": matched, "uniquePositions": len(unique)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    args = parser.parse_args()
    source, source_raw = positions(args.folder / "source.xyz32.bin")
    marked, marked_raw = positions(args.folder / "marked.xyz32.bin")
    csharp = json.loads((args.folder / "results.json").read_text())
    assert len(source) == len(marked) == csharp["sourceVertices"]
    assert position_hash(source_raw) == csharp["sourcePositionHash"]
    assert position_hash(marked_raw) == csharp["markedPositionHash"]
    observations = {"native": observe(marked), "unmarked_same_source": observe(source)}
    for name in observations:
        for field, value in observations[name].items():
            assert value == csharp["observations"][name][field], (name, field)
    shuffled = list(marked)
    random.Random(17015).shuffle(shuffled)
    observations["reorder"] = observe(shuffled)
    observations["duplicates"] = observe(marked + [marked[5]] * 900 + marked[900:1200])
    observations["reflection"] = observe([tuple(-v for v in p) for p in marked])
    for name in ("native", "reorder", "duplicates", "reflection"):
        assert observations[name]["usableSymbols"] == observations[name]["matchedSymbols"] == 128
    displacement = max(math.dist(a, b) for a, b in zip(source, marked))
    diagonal = math.sqrt(sum((max(p[j] for p in source) - min(p[j] for p in source)) ** 2 for j in range(3)))
    assert 0 < displacement <= 5e-5 and displacement / diagonal <= 1e-5
    report = {"schema": 1, "implementation": "independent-python-stdlib", "gpuUsed": False, "unityStarted": False,
              "observations": observations, "maximumObjectDisplacementFixture0": displacement,
              "maximumRelativeDisplacementFixture0": displacement / diagonal,
              "sourcePositionHash": position_hash(source_raw), "markedPositionHash": position_hash(marked_raw),
              "csharpObservationsAgree": True, "positionHashesAgree": True,
              "sourceBinarySha256": hashlib.sha256(source_raw).hexdigest(), "markedBinarySha256": hashlib.sha256(marked_raw).hexdigest()}
    (args.folder / "independent-reader-results.json").write_text(json.dumps(report, indent=2) + "\n")
    print("Independent mesh CPU reader: C#/Python observations and binary protocol hashes agree.")


if __name__ == "__main__":
    main()
