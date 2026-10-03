#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Stage 14 CPU feasibility study. Synthetic data only; no Unity, Vulkan or process capture.

The radial/DCT QIM experiments are deliberately small baselines, not production watermark codecs.
NumPy and Pillow must already be installed. The script never downloads dependencies.
"""
from __future__ import annotations

import argparse
import base64
from datetime import datetime, timezone
import hashlib
import hmac
import io
import json
import math
import os
from pathlib import Path
import secrets
import stat
import sys
import time

# Bound numerical work before importing NumPy. This process never requests a GPU backend.
for thread_variable in ("OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
    os.environ[thread_variable] = "1"
import numpy as np
from PIL import Image, ImageFilter

REPO = Path(__file__).resolve().parents[2]
BITS = 128
MESH_STEP = 1e-5
TEXTURE_STEP = 0.018
THRESHOLD = 109 / 128  # Predetermined feasibility threshold, never presented as calibrated confidence.
MIN_COVERAGE = 0.75
FAMILIES = ("balanced_shell", "balanced_ellipsoid", "asymmetric_cloud")


def hex_digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def validate_identity(record: dict) -> dict:
    if record.get("schema") != 1 or record.get("recordType") != "LAG-Fingerprint-Identity-v1" or record.get("state") != "IdentityOnly":
        raise ValueError("Unsupported private identity record")
    build = record.get("buildId", "")
    if len(build) != 32 or any(c not in "0123456789abcdef" for c in build):
        raise ValueError("Invalid private BuildID")
    for field in ("fingerprintBase64", "carrierSeedBase64"):
        encoded = record[field]
        decoded = base64.b64decode(encoded, validate=True)
        if len(decoded) != 32 or base64.b64encode(decoded).decode() != encoded:
            raise ValueError("Invalid private identity length/encoding")
    return record


def identity() -> dict:
    # Independent CSPRNG values. No OSC/runtime/codec key enters this domain.
    now = datetime.now(timezone.utc)
    return {
        "schema": 1, "recordType": "LAG-Fingerprint-Identity-v1", "state": "IdentityOnly",
        "buildId": secrets.token_hex(16),
        "createdUtc": now.strftime("%Y-%m-%dT%H:%M:%S.") + f"{now.microsecond:06d}0Z",
        "fingerprintBase64": base64.b64encode(secrets.token_bytes(32)).decode(),
        "carrierSeedBase64": base64.b64encode(secrets.token_bytes(32)).decode(),
    }


def derive(record: dict, purpose: str, channel: str, binding: str) -> bytes:
    validate_identity(record)
    header = f"LAG/fingerprint/v1/{purpose}\0{channel}\0{record['buildId']}\0{binding}\0".encode()
    return hmac.digest(base64.b64decode(record["carrierSeedBase64"]), header + base64.b64decode(record["fingerprintBase64"]), "sha256")


def marker(record: dict, channel: str, family: int) -> tuple[np.ndarray, bytes]:
    binding = hex_digest(f"lag-stage14-fixture-v1/{family}".encode())
    code = np.unpackbits(np.frombuffer(derive(record, "codeword", channel, binding)[:16], dtype=np.uint8))
    return code, derive(record, "carrier", channel, binding)


def rng_from(seed: bytes, label: str) -> np.random.Generator:
    # PCG64 is a research sampler, not the future production carrier protocol.
    return np.random.default_rng(int.from_bytes(hmac.digest(seed, label.encode(), "sha256"), "big"))


def decision(observed: np.ndarray, code: np.ndarray, usable: np.ndarray) -> dict:
    count = int(usable.sum())
    matched = int(np.sum(observed[usable] == code[usable]))
    score = matched / count if count else 0.0
    coverage = count / BITS
    matched_enough = coverage >= MIN_COVERAGE and score >= THRESHOLD
    state = "InconclusiveCoverage" if coverage < MIN_COVERAGE else "ResearchMatch" if matched_enough else "NoResearchMatch"
    return {"usableBits": count, "matchedBits": matched, "bitAgreement": score, "coverage": coverage,
            "researchMatch": matched_enough, "decision": state, "confidence": None}


def frame(points: np.ndarray) -> tuple[np.ndarray, float, np.ndarray]:
    center = points.mean(axis=0)
    radii = np.linalg.norm(points - center, axis=1)
    scale = float(radii.max())
    if not math.isfinite(scale) or scale <= 0 or not np.isfinite(points).all():
        raise ValueError("Invalid geometry")
    return center, scale, radii / scale


def mesh_fixture(family: int) -> np.ndarray:
    random = np.random.default_rng(17014 + family)
    half = 2048
    direction = random.normal(size=(half, 3))
    direction /= np.linalg.norm(direction, axis=1)[:, None]
    radius = random.uniform(0.12, 0.94, size=half)
    points = direction * radius[:, None]
    if family == 1:
        points *= np.array([1.0, 0.73, 1.21])
    if family < 2:
        points = np.concatenate((points, -points))
        points /= np.linalg.norm(points, axis=1).max()
        points *= 0.965
        points = np.concatenate((points, np.eye(3), -np.eye(3)))
    else:
        points = random.normal(size=(4096, 3)) * np.array([0.5, 0.19, 0.34])
        points[:, 1] += 0.15 * np.sin(points[:, 0] * 5)
        points -= points.mean(axis=0)
        points /= np.linalg.norm(points, axis=1).max()
    return np.ascontiguousarray(points, dtype=np.float64)


def mesh_carriers(seed: bytes) -> np.ndarray:
    return rng_from(seed, "mesh-qim-dither").uniform(0, 2 * MESH_STEP, size=BITS)


def mesh_embed(points: np.ndarray, code: np.ndarray, seed: bytes) -> np.ndarray:
    center, scale, radius = frame(points)
    bin_id = np.floor((radius - 0.1) / 0.88 * BITS).astype(int)
    valid = (bin_id >= 0) & (bin_id < BITS)
    ids = bin_id[valid]
    dither = mesh_carriers(seed)[ids]
    target = MESH_STEP * (2 * np.rint(((radius[valid] - dither) / MESH_STEP - code[ids]) / 2) + code[ids]) + dither
    # Do not move across the radial-band boundary. Recovered frame still may drift after embedding.
    same_bin = np.floor((target - 0.1) / 0.88 * BITS).astype(int) == ids
    target = np.where(same_bin, target, radius[valid])
    result = points.copy()
    result[valid] = center + (points[valid] - center) * (target / radius[valid])[:, None]
    return result


def mesh_observe(points: np.ndarray, seed: bytes) -> tuple[np.ndarray, np.ndarray]:
    _, _, radius = frame(points)
    bin_id = np.floor((radius - 0.1) / 0.88 * BITS).astype(int)
    valid = (bin_id >= 0) & (bin_id < BITS)
    ids = bin_id[valid]
    parity = np.rint((radius[valid] - mesh_carriers(seed)[ids]) / MESH_STEP).astype(np.int64) & 1
    counts = np.bincount(ids, minlength=BITS)
    votes = np.bincount(ids, weights=parity, minlength=BITS)
    observed = (votes > counts / 2).astype(np.uint8)
    usable = (counts >= 4) & (votes != counts / 2)
    return observed, usable


def index_embed(points: np.ndarray, code: np.ndarray) -> np.ndarray:
    result = points.copy()
    bits = code[np.arange(len(points)) % BITS]
    result[:, 0] = MESH_STEP * (2 * np.rint((points[:, 0] / MESH_STEP - bits) / 2) + bits)
    return result


def index_observe(points: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    ids = np.arange(len(points)) % BITS
    parity = np.rint(points[:, 0] / MESH_STEP).astype(np.int64) & 1
    counts = np.bincount(ids, minlength=BITS)
    votes = np.bincount(ids, weights=parity, minlength=BITS)
    return (votes > counts / 2).astype(np.uint8), (counts >= 4) & (votes != counts / 2)


def mesh_attacks(points: np.ndarray, other: np.ndarray, trial: int) -> dict[str, np.ndarray]:
    random = np.random.default_rng(50000 + trial)
    axis = np.array([0.4, 0.7, -0.2]); axis /= np.linalg.norm(axis)
    angle = 0.93
    skew = np.array([[0, -axis[2], axis[1]], [axis[2], 0, -axis[0]], [-axis[1], axis[0], 0]])
    rotation = np.eye(3) * np.cos(angle) + (1 - np.cos(angle)) * np.outer(axis, axis) + np.sin(angle) * skew
    pose_proxy = points.copy(); pose_proxy[:, 0] += 0.012 * np.sin(points[:, 1] * 5)
    return {
        "identity": points.copy(), "reorder": points[random.permutation(len(points))],
        "similarity": points @ rotation.T * 2.37 + np.array([1.2, -0.4, 2.0]),
        "float32_roundtrip": points.astype(np.float32).astype(np.float64),
        "quantize_1e-5": np.round(points / 1e-5) * 1e-5,
        "noise_sigma_1e-6": points + random.normal(0, 1e-6, size=points.shape),
        "noise_sigma_2e-5": points + random.normal(0, 2e-5, size=points.shape),
        "nonuniform_scale": points * np.array([1, 1.02, 0.98]),
        "drop_20_percent": points[random.choice(len(points), int(len(points) * 0.8), replace=False)],
        "deformation_proxy": pose_proxy, "collusion_average_2": (points + other) / 2,
    }


_DCT = None


def dct_basis() -> np.ndarray:
    global _DCT
    if _DCT is None:
        n = 128
        _DCT = np.cos(np.pi * (np.arange(n)[None, :] + 0.5) * np.arange(n)[:, None] / n) * np.sqrt(2 / n)
        _DCT[0] /= np.sqrt(2)
    return _DCT


def texture_fixture(family: int) -> np.ndarray:
    random = np.random.default_rng(22014 + family)
    y, x = np.mgrid[0:128, 0:128] / 127
    if family == 0:
        values = np.stack((0.22 + 0.48 * x, 0.30 + 0.34 * y, 0.45 + 0.12 * np.sin(x * 14) * np.cos(y * 11)), axis=2)
    elif family == 1:
        fabric = 0.065 * np.sin(x * 105) * np.cos(y * 112) + random.normal(0, 0.028, size=x.shape)
        values = np.stack((0.38 + fabric, 0.52 + fabric, 0.64 + fabric), axis=2)
    else:
        patch = ((x + y > 0.85) & (x - y < 0.33)).astype(float)
        values = np.stack((0.19 + 0.50 * patch, 0.57 - 0.20 * patch + 0.08 * x, 0.31 + 0.25 * (x > 0.6)), axis=2)
    return np.rint(np.clip(values, 0, 1) * 255).astype(np.uint8)


def texture_carriers(seed: bytes) -> tuple[np.ndarray, np.ndarray]:
    coords = np.array([(u, v) for u in range(35) for v in range(35) if 3 <= u + v <= 34])
    random = rng_from(seed, "texture-qim-carriers")
    chosen = coords[random.permutation(len(coords))[:BITS * 3]].reshape(BITS, 3, 2)
    return chosen, random.uniform(0, 2 * TEXTURE_STEP, size=(BITS, 3))


def as_rgb128(pixels: np.ndarray) -> np.ndarray:
    image = Image.fromarray(pixels)
    if image.size != (128, 128): image = image.resize((128, 128), Image.Resampling.BILINEAR)
    return np.asarray(image.convert("RGB"), dtype=np.float64) / 255


def luma(rgb: np.ndarray) -> np.ndarray:
    return rgb @ np.array([0.2126, 0.7152, 0.0722])


def texture_embed(pixels: np.ndarray, code: np.ndarray, seed: bytes) -> np.ndarray:
    rgb = as_rgb128(pixels); original_luma = luma(rgb); basis = dct_basis()
    spectrum = basis @ original_luma @ basis.T
    coords, dither = texture_carriers(seed)
    values = spectrum[coords[:, :, 0], coords[:, :, 1]]
    spectrum[coords[:, :, 0], coords[:, :, 1]] = TEXTURE_STEP * (2 * np.rint(((values - dither) / TEXTURE_STEP - code[:, None]) / 2) + code[:, None]) + dither
    delta = basis.T @ spectrum @ basis - original_luma
    return np.rint(np.clip(rgb + delta[:, :, None], 0, 1) * 255).astype(np.uint8)


def texture_observe(pixels: np.ndarray, seed: bytes) -> tuple[np.ndarray, np.ndarray]:
    basis = dct_basis(); spectrum = basis @ luma(as_rgb128(pixels)) @ basis.T
    coords, dither = texture_carriers(seed)
    values = spectrum[coords[:, :, 0], coords[:, :, 1]]
    parity = np.rint((values - dither) / TEXTURE_STEP).astype(np.int64) & 1
    return (parity.sum(axis=1) >= 2).astype(np.uint8), np.ones(BITS, dtype=bool)


def image_roundtrip(pixels: np.ndarray, fmt: str, **settings) -> np.ndarray:
    output = io.BytesIO(); Image.fromarray(pixels).save(output, format=fmt, **settings)
    output.seek(0)
    with Image.open(output) as image: return np.array(image.convert("RGB"))


def texture_attacks(pixels: np.ndarray, other: np.ndarray) -> dict[str, np.ndarray]:
    image = Image.fromarray(pixels)
    return {
        "identity": pixels.copy(), "png_roundtrip": image_roundtrip(pixels, "PNG"),
        "jpeg_q95": image_roundtrip(pixels, "JPEG", quality=95, subsampling=0),
        "jpeg_q75": image_roundtrip(pixels, "JPEG", quality=75, subsampling=0),
        "jpeg_q75_420": image_roundtrip(pixels, "JPEG", quality=75, subsampling=2),
        "resize_0.75": np.array(image.resize((96, 96), Image.Resampling.BILINEAR)),
        "resize_0.5": np.array(image.resize((64, 64), Image.Resampling.BILINEAR)),
        "color_affine": np.rint(np.clip(pixels.astype(float) / 255 * 1.03 + 0.01, 0, 1) * 255).astype(np.uint8),
        "gamma_1.05": np.rint((pixels.astype(float) / 255) ** 1.05 * 255).astype(np.uint8),
        "gamma_1.2": np.rint((pixels.astype(float) / 255) ** 1.2 * 255).astype(np.uint8),
        "blur_0.5": np.array(image.filter(ImageFilter.GaussianBlur(0.5))),
        "crop_border_4": np.array(image.crop((4, 4, 124, 124))),
        "collusion_average_2": np.rint((pixels.astype(float) + other.astype(float)) / 2).astype(np.uint8),
    }


def private_dataset(repetitions: int, null_candidates: int) -> dict:
    return {"schema": 1, "purpose": "LAG-stage14-cpu-study-only", "repetitions": repetitions,
            "nullCandidates": null_candidates, "trials": [
                {"family": family, "replicate": replicate, "identity": identity(), "otherIdentity": identity(),
                 "nullIdentities": [identity() for _ in range(null_candidates)]}
                for family in range(3) for replicate in range(repetitions)]}


DIRECTORY_FLAGS = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC


def open_directory(path: Path) -> int:
    # Descriptor traversal avoids following a replaced symlink during private reads.
    fd = os.open("/", DIRECTORY_FLAGS)
    try:
        for name in path.parts[1:]:
            next_fd = os.open(name, DIRECTORY_FLAGS, dir_fd=fd)
            os.close(fd); fd = next_fd
        result = fd; fd = -1
        return result
    finally:
        if fd >= 0: os.close(fd)


def private_root() -> Path:
    xdg = os.environ.get("XDG_DATA_HOME")
    study_data = os.environ.get("LAG_FP_RESEARCH_DATA_HOME")
    if study_data and not Path(study_data).is_absolute(): raise ValueError("Explicit private study data root must be absolute")
    base = Path(study_data) if study_data else Path(xdg) if xdg and Path(xdg).is_absolute() else Path.home() / ".local" / "share"
    root = Path(os.path.abspath(base / "linux-avatar-guard" / "fingerprint-research"))
    for parent in (root, *root.parents):
        if parent.name in ("Assets", "Packages") or (parent / ".git").exists() or (parent / "ProjectSettings").is_dir():
            raise ValueError("Private fingerprint study storage must stay outside Unity projects/Git")
    return root


def require_private_directory(fd: int) -> None:
    info = os.fstat(fd)
    if info.st_uid != os.geteuid() or not stat.S_ISDIR(info.st_mode) or stat.S_IMODE(info.st_mode) != 0o700:
        raise ValueError("Private directory requires current owner and 0700")


def write_private_dataset(dataset: dict) -> str:
    root = private_root(); fd = open_directory(root.parent.parent)
    run_id = secrets.token_hex(16)
    try:
        for name in (root.parent.name, root.name, run_id):
            created = False
            try:
                os.mkdir(name, mode=0o700, dir_fd=fd); created = True
            except FileExistsError:
                if name == run_id: raise
            next_fd = os.open(name, DIRECTORY_FLAGS, dir_fd=fd)
            os.close(fd); fd = next_fd
            if created: os.fchmod(fd, 0o700)
            require_private_directory(fd)
        file_fd = os.open("fingerprint-private.json", os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600, dir_fd=fd)
        os.fchmod(file_fd, 0o600)
        with os.fdopen(file_fd, "w", encoding="utf-8") as stream:
            json.dump(dataset, stream, indent=2); stream.write("\n"); stream.flush(); os.fsync(stream.fileno())
        os.fsync(fd)
        return run_id
    finally:
        os.close(fd)


def read_private(path: Path) -> dict:
    folder = open_directory(Path(os.path.abspath(path.parent)))
    try:
        require_private_directory(folder)
        fd = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=folder)
    finally:
        os.close(folder)
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_nlink != 1 or stat.S_IMODE(info.st_mode) != 0o600:
            raise ValueError("Private input requires current owner, regular file, one link and 0600")
        with os.fdopen(fd, "rb", closefd=False) as stream: data = stream.read(1024 * 1024 + 1)
        if len(data) > 1024 * 1024: raise ValueError("Private research input too large")
        result = json.loads(data)
        if result.get("schema") != 1 or result.get("purpose") != "LAG-stage14-cpu-study-only": raise ValueError("Unknown private research dataset")
        return result
    finally:
        os.close(fd)


def write_json(path: Path, value: dict) -> None:
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def aggregate(entries: list[dict]) -> dict:
    groups: dict[str, list[dict]] = {}
    for entry in entries: groups.setdefault(entry["case"], []).append(entry)
    return {name: {"trials": len(group), "researchMatches": sum(bool(item["researchMatch"]) for item in group),
                   "inconclusiveCoverage": sum(item["decision"] == "InconclusiveCoverage" for item in group),
                   "minimumAgreement": min(item["bitAgreement"] for item in group),
                   "meanAgreement": float(np.mean([item["bitAgreement"] for item in group])),
                   "minimumCoverage": min(item["coverage"] for item in group)} for name, group in groups.items()}


def check_vectors(path: Path) -> None:
    result = json.loads(path.read_text())
    known = {"schema": 1, "recordType": "LAG-Fingerprint-Identity-v1", "state": "IdentityOnly", "buildId": "a" * 32,
             "fingerprintBase64": base64.b64encode(bytes(range(32))).decode(), "carrierSeedBase64": base64.b64encode(bytes(range(32, 64))).decode()}
    if derive(known, "carrier", "mesh", "b" * 64).hex() != result["meshCarrierVector"] or derive(known, "codeword", "mesh", "b" * 64)[:16].hex() != result["meshCodewordVector"]:
        raise ValueError("C#/Python independent derivation vectors differ")


def run(output: Path, dataset: dict) -> dict:
    start = time.monotonic(); cpu_start = time.process_time()
    mesh_positive, texture_positive, mesh_negative, texture_negative, baseline = [], [], [], [], []
    errors, collusion = [], []
    for trial_index, trial in enumerate(dataset["trials"]):
        family = trial["family"]; record = trial["identity"]
        points = mesh_fixture(family); pixels = texture_fixture(family)
        source_points_hash = hex_digest(points.tobytes()); source_pixels_hash = hex_digest(pixels.tobytes())
        mesh_code, mesh_seed = marker(record, "mesh", family); texture_code, texture_seed = marker(record, "texture", family)
        marked_points = mesh_embed(points, mesh_code, mesh_seed); marked_pixels = texture_embed(pixels, texture_code, texture_seed)
        other_mesh_code, other_mesh_seed = marker(trial["otherIdentity"], "mesh", family)
        other_texture_code, other_texture_seed = marker(trial["otherIdentity"], "texture", family)
        other_points = mesh_embed(points, other_mesh_code, other_mesh_seed); other_pixels = texture_embed(pixels, other_texture_code, other_texture_seed)
        mesh_cases = mesh_attacks(marked_points, other_points, trial_index); texture_cases = texture_attacks(marked_pixels, other_pixels)
        for channel, candidate, observe, first_code, first_seed, second_code, second_seed in (
            ("mesh", mesh_cases["collusion_average_2"], mesh_observe, mesh_code, mesh_seed, other_mesh_code, other_mesh_seed),
            ("texture", texture_cases["collusion_average_2"], texture_observe, texture_code, texture_seed, other_texture_code, other_texture_seed),
        ):
            first_observed, first_usable = observe(candidate, first_seed)
            second_observed, second_usable = observe(candidate, second_seed)
            first = decision(first_observed, first_code, first_usable)
            second = decision(second_observed, second_code, second_usable)
            collusion.append({"trial": trial_index, "channel": channel, "first": first, "second": second,
                              "bothContributorsMatch": first["researchMatch"] and second["researchMatch"]})
        for channel, cases, observe, code, seed, positives, negatives in (
            ("mesh", mesh_cases, mesh_observe, mesh_code, mesh_seed, mesh_positive, mesh_negative),
            ("texture", texture_cases, texture_observe, texture_code, texture_seed, texture_positive, texture_negative),
        ):
            for case, candidate in cases.items():
                observed, usable = observe(candidate, seed)
                positives.append({"trial": trial_index, "family": FAMILIES[family], "case": case, **decision(observed, code, usable)})
                for null_index, null_record in enumerate(trial["nullIdentities"]):
                    null_code, null_seed = marker(null_record, channel, family)
                    null_observed, null_usable = observe(candidate, null_seed)
                    negatives.append({"trial": trial_index, "family": FAMILIES[family], "case": "wrong_identity/" + case,
                                      "nullCandidate": null_index, **decision(null_observed, null_code, null_usable)})
            controls = {"unmarked_same_source": points if channel == "mesh" else pixels,
                        "different_build_same_source": other_points if channel == "mesh" else other_pixels,
                        "different_source": mesh_fixture((family + 1) % 3) if channel == "mesh" else texture_fixture((family + 1) % 3)}
            for name, candidate in controls.items():
                observed, usable = observe(candidate, seed)
                negatives.append({"trial": trial_index, "family": FAMILIES[family], "case": name, **decision(observed, code, usable)})
        indexed = index_embed(points, mesh_code)
        for name, candidate in (("identity", indexed), ("reorder", indexed[np.random.default_rng(trial_index).permutation(len(indexed))])):
            observed, usable = index_observe(candidate)
            baseline.append({"trial": trial_index, "family": FAMILIES[family], "case": name, **decision(observed, mesh_code, usable)})
        diagonal = float(np.linalg.norm(points.max(axis=0) - points.min(axis=0)))
        delta = np.linalg.norm(marked_points - points, axis=1)
        rgb_delta = (marked_pixels.astype(float) - pixels.astype(float)) / 255
        mse = float(np.mean(rgb_delta ** 2))
        errors.append({"trial": trial_index, "family": FAMILIES[family], "meshVertices": len(points),
                       "meshMaxDisplacement": float(delta.max()), "meshRmsDisplacement": float(np.sqrt(np.mean(delta ** 2))),
                       "meshMaxRelativeToDiagonal": float(delta.max() / diagonal),
                       "textureMeanAbsoluteRgb": float(np.mean(np.abs(rgb_delta))), "textureMaximumRgb": float(np.max(np.abs(rgb_delta))),
                       "texturePsnrDb": -10 * math.log10(mse) if mse else None,
                       "sourcePointsPreserved": hex_digest(points.tobytes()) == source_points_hash,
                       "sourcePixelsPreserved": hex_digest(pixels.tobytes()) == source_pixels_hash})
        if trial["replicate"] == 0:
            Image.fromarray(pixels).save(output / (FAMILIES[family] + "-source.png"))
            Image.fromarray(marked_pixels).save(output / (FAMILIES[family] + "-cpu-marked.png"))
        print(f"CPU trial {trial_index + 1}/{len(dataset['trials'])}: {FAMILIES[family]}", flush=True)
    summary = {
        "schema": 1, "stage": 14, "scope": "CPU mathematical feasibility, own synthetic point clouds/RGB images",
        "fingerprintBits": 256, "componentCodewordBits": BITS,
        "researchThreshold": THRESHOLD, "minimumCoverage": MIN_COVERAGE, "confidenceCalibrated": False,
        "meshStepRelativeToMaxRadius": MESH_STEP, "textureDctStep": TEXTURE_STEP,
        "trials": len(dataset["trials"]), "meshPositive": aggregate(mesh_positive), "texturePositive": aggregate(texture_positive),
        "meshNegative": aggregate(mesh_negative), "textureNegative": aggregate(texture_negative), "indexBaseline": aggregate(baseline),
        "meshFalseResearchMatches": sum(bool(x["researchMatch"]) for x in mesh_negative), "meshNegativeComparisons": len(mesh_negative),
        "textureFalseResearchMatches": sum(bool(x["researchMatch"]) for x in texture_negative), "textureNegativeComparisons": len(texture_negative),
        "collusionBothContributorsMatch": {channel: sum(item["bothContributorsMatch"] for item in collusion if item["channel"] == channel)
                                          for channel in ("mesh", "texture")},
        "maximumMeshDisplacementRelativeToDiagonal": max(item["meshMaxRelativeToDiagonal"] for item in errors),
        "minimumTexturePsnrDb": min(item["texturePsnrDb"] for item in errors),
        "maximumTextureMeanAbsoluteRgb": max(item["textureMeanAbsoluteRgb"] for item in errors),
        "sourcesPreserved": all(item["sourcePointsPreserved"] and item["sourcePixelsPreserved"] for item in errors),
        "graphicsApiUsed": False, "unityEditorStarted": False, "vrchatAccessed": False, "toolsDownloaded": False,
        "privateStudyRecordsOutsideUnityAndGit": True,
        "fbxRoundtripTested": False, "unityBcCompressionTested": False, "skinningTested": False, "visualUnityTested": False,
        "limitations": ["Three synthetic families; correlated samples, no population FPR guarantee.",
                        "Radial frame/coverage may drift; no topology, normals, tangents, morph or SDK validation.",
                        "JPEG is tested; it is not Unity BCn/Crunch compression.",
                        "Predefined research threshold is not attribution confidence or proof of theft."],
        "wallSeconds": time.monotonic() - start, "processCpuSeconds": time.process_time() - cpu_start,
    }
    write_json(output / "results.json", summary)
    write_json(output / "measurements.json", {"meshPositive": mesh_positive, "texturePositive": texture_positive,
               "meshNegative": mesh_negative, "textureNegative": texture_negative, "indexBaseline": baseline, "distortion": errors})
    write_json(output / "collusion.json", {"measurements": collusion})
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="New directory under evidence/fingerprint-research")
    parser.add_argument("--repetitions", type=int, default=4)
    parser.add_argument("--null-candidates", type=int, default=8)
    replay = parser.add_mutually_exclusive_group()
    replay.add_argument("--replay-private", type=Path)
    replay.add_argument("--replay-run", help="Private research run ID from private-reference.json")
    parser.add_argument("--context-vectors", type=Path)
    parser.add_argument("--private-data-root", type=Path, help="Existing absolute data directory outside Unity/Git; otherwise XDG_DATA_HOME")
    args = parser.parse_args()
    if args.private_data_root: os.environ["LAG_FP_RESEARCH_DATA_HOME"] = str(args.private_data_root)
    if not 1 <= args.repetitions <= 8 or not 1 <= args.null_candidates <= 16: raise ValueError("Study size outside CPU budget")
    output = Path(os.path.abspath(args.output))
    allowed = REPO / "evidence" / "fingerprint-research"
    if output == allowed or not output.is_relative_to(allowed): raise ValueError("Output must be a new child of evidence/fingerprint-research")
    for part in (output, *output.parents):
        if part.is_symlink(): raise ValueError("Output must not traverse symlinks")
    if args.context_vectors: check_vectors(args.context_vectors)
    if args.replay_run:
        if len(args.replay_run) != 32 or any(c not in "0123456789abcdef" for c in args.replay_run): raise ValueError("Invalid private run ID")
        replay_path = private_root() / args.replay_run / "fingerprint-private.json"
    else:
        replay_path = args.replay_private.absolute() if args.replay_private else None
    dataset = read_private(replay_path) if replay_path else private_dataset(args.repetitions, args.null_candidates)
    if not 1 <= len(dataset["trials"]) <= 24: raise ValueError("Replay exceeds study size limit")
    for trial in dataset["trials"]:
        if trial["family"] not in range(3) or not 0 <= trial["replicate"] < 8 or not 1 <= len(trial["nullIdentities"]) <= 16: raise ValueError("Invalid bounded trial")
        for record in [trial["identity"], trial["otherIdentity"], *trial["nullIdentities"]]: validate_identity(record)
    output.mkdir(parents=True, exist_ok=False)
    run_id = write_private_dataset(dataset)
    write_json(output / "private-reference.json", {"schema": 1, "storage": "private-data-root/linux-avatar-guard/fingerprint-research", "runId": run_id,
                                                "explicitDataRoot": bool(os.environ.get("LAG_FP_RESEARCH_DATA_HOME"))})
    summary = run(output, dataset)
    manifest = {"schema": 1, "files": {}}
    for file in sorted(output.glob("*")):
        if file.is_file(): manifest["files"][file.name] = hex_digest(file.read_bytes())
    manifest["sourceSha256"] = hex_digest(Path(__file__).read_bytes())
    write_json(output / "evidence-manifest.json", manifest)
    print(json.dumps({key: summary[key] for key in ("trials", "sourcesPreserved", "meshFalseResearchMatches", "textureFalseResearchMatches", "wallSeconds")}), flush=True)
    return 0


if __name__ == "__main__":
    try: sys.exit(main())
    except Exception as error:
        print(f"CPU study failed: {error}", file=sys.stderr)
        sys.exit(1)
