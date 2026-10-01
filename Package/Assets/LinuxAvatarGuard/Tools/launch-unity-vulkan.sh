#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
set -euo pipefail
if [[ $(uname -s) != Linux ]]; then
  echo 'Linux Avatar Guard requiere Linux.' >&2
  exit 1
fi
if [[ $# -lt 2 ]]; then
  echo 'Uso: bash launch-unity-vulkan.sh /ruta/Editor/Unity /ruta/proyecto [argumentos Unity]' >&2
  exit 2
fi
lag_unity_editor=$1
lag_unity_project=$2
shift 2
if [[ ! -x "$lag_unity_editor" || ! -d "$lag_unity_project/Assets" || ! -d "$lag_unity_project/ProjectSettings" ]]; then
  echo 'No se encontró el ejecutable Unity o un proyecto válido.' >&2
  exit 2
fi
exec "$lag_unity_editor" -force-vulkan -projectPath "$lag_unity_project" "$@"
