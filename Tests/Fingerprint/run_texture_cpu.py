#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Run Stage 16 CPU checks/channel study and compile its Unity adapter without launching Unity."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity-data", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path, help="New CPU evidence directory outside Unity assets")
    args = parser.parse_args()
    for variable in ("OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS", "VECLIB_MAXIMUM_THREADS", "NUMEXPR_NUM_THREADS"):
        os.environ[variable] = "1"
    if sys.platform != "linux":
        parser.error("Linux required")
    root = Path(__file__).resolve().parents[2]
    data, output = args.unity_data.resolve(), args.output.absolute()
    mono, csc = data / "MonoBleedingEdge/bin/mono", data / "MonoBleedingEdge/lib/mono/4.5/csc.exe"
    references = [data / "Managed/UnityEngine" / name for name in ("UnityEngine.CoreModule.dll", "UnityEngine.JSONSerializeModule.dll", "UnityEditor.CoreModule.dll")]
    references += [data / "MonoBleedingEdge/lib/mono/4.5/Facades/netstandard.dll"]
    for path in [mono, csc] + references:
        if not path.is_file():
            parser.error(f"Installed dependency missing: {path}")
    if output.exists() or output.is_symlink():
        parser.error("Output must be new; evidence is not overwritten")
    if any(part in ("Assets", "Packages") for part in output.parts):
        parser.error("CPU evidence belongs outside Unity assets")
    # No package installation. The independent study imports the existing modules with one numerical thread.
    probe = subprocess.run([sys.executable, "-c", "import PIL,numpy"], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if probe.returncode:
        parser.error("Existing NumPy/Pillow required; nothing was downloaded or installed")
    output.mkdir(parents=True)
    source = root / "Package/Assets/LinuxAvatarGuard/Editor"
    context, core, store, adapter = [source / name for name in ("GuardFingerprintContext.cs", "GuardTextureFingerprint.cs", "GuardFingerprintPrivateStore.cs", "GuardTextureFingerprintUnity.cs")]
    commands = []
    def run(label, command):
        command = [str(value) for value in command]
        completed = subprocess.run(["nice", "-n", "19"] + command, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        (output / (label + ".log")).write_text(completed.stdout)
        commands.append({"label": label, "command": command, "returncode": completed.returncode})
        if completed.stdout:
            print(completed.stdout, end="", flush=True)
        if completed.returncode:
            raise RuntimeError(label + " failed")
    compiler = [mono, csc, "-nologo", "-langversion:8.0"]
    host = output / "LAGTextureFingerprintCpu.exe"
    run("compile-texture-core", compiler + ["-target:exe", "-r:System.Runtime.Serialization.dll", "-out:" + str(host), context, core, Path(__file__).with_name("LAGTextureFingerprintCpu.cs")])
    run("texture-core", [mono, host, output])
    print("Comparing CPU PNG/JPEG/DDS/resize/color/mip candidates with independent Python and C# readers...", flush=True)
    run("independent-reader", [sys.executable, Path(__file__).with_name("verify_texture_cpu.py"), output, "--mono", mono])
    run("compile-unity-adapter", compiler + ["-target:library", "-out:" + str(output / "GuardTextureFingerprint.UnityAdapter.dll")] + ["-r:" + str(p) for p in references] + [context, core, store, adapter])
    result = json.loads((output / "results.json").read_text())
    study = json.loads((output / "independent-reader-results.json").read_text())
    sources = [context, core, store, adapter, Path(__file__), Path(__file__).with_name("LAGTextureFingerprintCpu.cs"), Path(__file__).with_name("verify_texture_cpu.py")]
    digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
    report = {"schema": 1, "stage": 16, "status": "CpuPilotValidatedAdapterCompiled", "cpuCoreChecks": result["checks"], "nativeVectors": result["nativeVectors"],
              "independentExactEncodingVectors": study["independentExactEncodingVectors"], "independentRecordAuthenticationVectors": study["independentRecordAuthenticationVectors"],
              "csharpPythonObservations": study["csharpPythonObservationComparisons"], "negativeComparisons": study["negativeComparisons"], "negativeResearchMatches": study["negativeResearchMatches"],
              "unityAdapterStandaloneCompilePassed": True, "unityProjectCompiled": False, "unityAdapterExecuted": False, "unityStarted": False,
              "gpuUsed": False, "vrchatAccessed": False, "realAvatarUsed": False, "sdkValidated": False, "newUnitypackageExported": False, "dependenciesDownloaded": False,
              "commands": commands, "sourceHashes": {str(path.resolve().relative_to(root)): digest(path) for path in sources},
              "toolHashes": {str(path): digest(path) for path in [mono, csc] + references}, "dependencies": study["dependencies"]}
    (output / "run-manifest.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"Stage 16 CPU pilot: {result['checks']} checks; {study['csharpPythonObservationComparisons']} independent observations agree. Unity adapter compiled only.")


if __name__ == "__main__":
    main()
