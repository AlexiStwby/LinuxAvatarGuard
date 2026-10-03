#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Compile/test the Stage 15 CPU pilot without launching Unity or any graphics application."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity-data", required=True, type=Path, help="Existing Unity 2022.3.22f1 Editor/Data directory")
    parser.add_argument("--output", required=True, type=Path, help="New directory for synthetic public results")
    args = parser.parse_args()
    if sys.platform != "linux":
        parser.error("Linux required for private-store libc checks")
    root = Path(__file__).resolve().parents[2]
    data, output = args.unity_data.resolve(), args.output.absolute()
    mono = data / "MonoBleedingEdge/bin/mono"
    csc = data / "MonoBleedingEdge/lib/mono/4.5/csc.exe"
    references = [data / "Managed/UnityEngine" / name for name in
                  ("UnityEngine.CoreModule.dll", "UnityEngine.JSONSerializeModule.dll", "UnityEditor.CoreModule.dll")]
    references += [data / "MonoBleedingEdge/lib/mono/4.5/Facades/netstandard.dll"]
    for path in [mono, csc] + references:
        if not path.is_file():
            parser.error(f"Installed dependency missing: {path}")
    if output.exists() or output.is_symlink():
        parser.error("Output must be new; previous evidence is not overwritten")
    if any(part in ("Assets", "Packages") for part in output.parts):
        parser.error("CPU evidence belongs outside Unity assets")
    output.mkdir(parents=True)
    source = root / "Package/Assets/LinuxAvatarGuard/Editor"
    context = source / "GuardFingerprintContext.cs"
    mesh = source / "GuardMeshFingerprint.cs"
    store = source / "GuardFingerprintPrivateStore.cs"
    adapter = source / "GuardMeshFingerprintUnity.cs"
    commands = []
    def run(label, command):
        command = [str(part) for part in command]
        result = subprocess.run(["nice", "-n", "19"] + command, cwd=root, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, text=True)
        (output / (label + ".log")).write_text(result.stdout)
        commands.append({"label": label, "command": command, "returncode": result.returncode})
        if result.returncode:
            print(result.stdout, end="", file=sys.stderr)
            raise RuntimeError(label + " failed")
        if result.stdout:
            print(result.stdout, end="")
    compiler = [mono, csc, "-nologo", "-langversion:8.0"]
    host = output / "LAGMeshFingerprintCpu.exe"
    store_host = output / "LAGFingerprintPrivateStoreCpu.exe"
    run("compile-mesh-core", compiler + ["-target:exe", "-out:" + str(host), context, mesh, Path(__file__).with_name("LAGMeshFingerprintCpu.cs")])
    run("mesh-core", [mono, host, output])
    run("independent-reader", [sys.executable, Path(__file__).with_name("verify_mesh_cpu.py"), output])
    run("compile-private-store", compiler + ["-target:exe", "-out:" + str(store_host), store, Path(__file__).with_name("LAGFingerprintPrivateStoreCpu.cs")])
    run("private-store", [mono, store_host, output / "private-store-results.json"])
    run("compile-unity-adapter", compiler + ["-target:library", "-out:" + str(output / "GuardMeshFingerprint.UnityAdapter.dll")] +
        ["-r:" + str(p) for p in references] + [context, mesh, store, adapter])
    core_result = json.loads((output / "results.json").read_text())
    store_result = json.loads((output / "private-store-results.json").read_text())
    digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
    sources = [context, mesh, store, adapter, Path(__file__), Path(__file__).with_name("LAGMeshFingerprintCpu.cs"),
               Path(__file__).with_name("LAGFingerprintPrivateStoreCpu.cs"), Path(__file__).with_name("verify_mesh_cpu.py")]
    report = {"schema": 1, "stage": 15, "status": "CpuPilotValidatedAdapterCompiled", "cpuChecks": core_result["checks"] + store_result["checks"],
              "meshCoreChecks": core_result["checks"], "privateStoreChecks": store_result["checks"], "diverseSyntheticBuilds": 32,
              "independentPythonReaderPassed": True, "adapterStandaloneCompilePassed": True,
              "unityProjectCompiled": False, "unityAdapterExecuted": False, "unityStarted": False, "gpuUsed": False,
              "realAvatarUsed": False, "sdkValidated": False, "newUnitypackageExported": False,
              "commands": commands, "sourceHashes": {str(path.resolve().relative_to(root)): digest(path) for path in sources},
              "toolHashes": {str(path): digest(path) for path in [mono, csc] + references}}
    (output / "run-manifest.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"Stage 15 CPU pilot: {report['cpuChecks']} checks; adapter compiled only. Unity/native/visual validation pending.")


if __name__ == "__main__":
    main()
