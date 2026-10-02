# Análisis GPU controlado en Unity

Fecha: 2026-10-01. Codec analizado: `legacy-linear`, versión 1. Entorno: Unity 2022.3.22f1, lilToon 2.3.4, Vulkan, Intel Arc A750 y Mesa 26.2.3.

## Alcance autorizado

El usuario autorizó descargar y utilizar RenderDoc para Unity o una aplicación de prueba propia. Toda la experimentación de este informe se realizó en el Unity nuevo del proyecto desechable `ValidationProject`, con una esfera procedimental. No se capturó, inspeccionó ni modificó el proceso de VRChat. Esta restricción se mantiene para etapas posteriores del análisis GPU; las comprobaciones visuales que el usuario haga normalmente en VRChat son una actividad distinta.

RenderDoc 1.46 se descargó como build portátil desde el sitio oficial, después de contrastar la versión con la [release oficial](https://github.com/baldurk/renderdoc/releases/tag/v1.46). Instalación local: `~/.local/share/linux-avatar-guard-tools/renderdoc_1.46/`. SHA256 calculado del tarball descargado: `6fedf15eab1288d9a81889ba8a4452dca53f5c10de8162be32a0f8972f62dc09`. Este hash identifica el archivo usado; no se presenta como una firma verificada del distribuidor.

La configuración Vulkan se suministró mediante `VK_ADD_IMPLICIT_LAYER_PATH` y variables solo del proceso de prueba. No se registró una capa global ni se cambió la configuración del proceso de VRChat. La captura no se ejecuta sobre un proceso existente: RenderDoc inicia el Unity desechable indicado por su comando.

## Experimento

1. Crear una esfera Unity, clonar su mesh y codificarla con el codec actual y valores públicos de ejemplo.
2. Generar un derivado de lilToon con el mismo decoder utilizado por el paquete.
3. Renderizar la copia desbloqueada a un RenderTexture de 256 × 256 y calentar el shader.
4. Capturar ese render utilizando la API de aplicación de RenderDoc.
5. Reproducir la captura y leer vertex buffers, constantes y salida del vertex shader. No aplicar el decoder LAG al buffer de salida.
6. Comparar entradas con la mesh codificada de referencia y salidas con la proyección de la mesh original conocida de la fixture.

El runner [LAGGpuCapture.cs](../Tests/LAGGpuCapture.cs) exige Linux Editor, Vulkan, permiso explícito por variable y el proyecto desechable. El analizador [analyze_gpu_capture.py](../Tests/analyze_gpu_capture.py) solo acepta el marcador de esta fixture y una captura local indicada explícitamente. Los scripts son herramientas de investigación; no se incluyen en el paquete productivo.

## Resultado observado

| Dato | Resultado |
| --- | --- |
| Draws analizados | 2, eventos 45 y 101 de esta captura. |
| Vértices PostVS por draw | 515. |
| Índices por draw | 2304. |
| Error máximo de entrada frente a la mesh codificada | 0. |
| RMS de entrada frente a geometría original, correspondencia más cercana | ≈ 0,1191 unidades. |
| RMS PostVS frente a proyección original | ≈ 2,51 × 10⁻⁸. |
| Error máximo PostVS frente a proyección original | ≈ 1,19 × 10⁻⁷. |
| Vector de valores de runtime de la fixture | Observable en el constant block 0, offset relativo 736 bytes. |
| Exportación desde PostVS | OBJ proyectado obtenido del replay, con sus índices. |

La comparación contempla explícitamente la convención Y entre la proyección Unity de RenderTexture y `gl_Position` Vulkan. En este entorno se requiere invertir globalmente Y de la referencia; el reporte conserva también el RMS sin ese ajuste. No se cambió la tolerancia ni se aplicó una corrección por vértice para obtener el resultado. La matriz y las posiciones originales solo se utilizan para medir la fixture conocida.

La entrada GPU contiene la esfera ofuscada. Después de ejecutar el shader, los vértices corresponden a la geometría corregida proyectada. La captura también contiene los cuatro valores públicos de la fixture. El experimento confirma que la reconstrucción tardía actual no bloquea la observación de geometría después del vertex shader.

La [documentación oficial del Mesh Viewer](https://github.com/baldurk/renderdoc/blob/v1.x/docs/window/mesh_viewer.rst) y el [ejemplo oficial de lectura PostVS](https://github.com/baldurk/renderdoc/blob/v1.x/docs/python_api/examples/mesh_output.rst) explican esta superficie de observación. Aquí se comprobó sobre contenido propio en vez de extrapolar únicamente desde esas capacidades.

## Evidencia local

Dentro de `evidence/gpu-audit/`:

- `legacy-unlocked_capture.rdc`: captura Vulkan de la fixture.
- `unity-unlocked.png`: imagen obtenida por Unity del render capturado.
- `synthetic-reference.json`: referencia propia para comprobar el resultado.
- `gpu-analysis.json`: métricas y resultado `verified`.
- `captured-post-vs-45.obj` y `captured-post-vs-101.obj`: geometría proyectada de los draws.
- `renderdoc-inspection.json`: metadatos de entrada, salida y bindings; diagnóstico local.
- `vulkan-layer/renderdoc_capture.json`: configuración de capa con ruta local a la biblioteca.

Estos archivos se excluyen de Git y de la distribución. No incluyen claves del avatar comercial. El log del proceso es `gpu-unity-capture.log`.

## Reproducción y límites operativos

El JSON de capa viene del build oficial; su `library_path` debe apuntar a la biblioteca de la instalación local. Se usa como **capa implícita añadida solo al proceso**. Un primer intento usando `VK_LAYER_PATH` como capa explícita omitió su filtro de extensiones, provocó un rechazo de Wayland y el fallback de Unity a OpenGL. El guard del runner lo rechazó. La configuración corregida mantuvo Vulkan; no se relajó la comprobación.

```bash
env \
  VK_ADD_IMPLICIT_LAYER_PATH=/home/stwby/LinuxAvatarGuard/evidence/gpu-audit/vulkan-layer \
  ENABLE_VULKAN_RENDERDOC_CAPTURE=1 \
  LAG_GPU_AUDIT_ALLOWED=synthetic-unity-only \
  /home/stwby/.local/share/linux-avatar-guard-tools/renderdoc_1.46/bin/renderdoccmd capture \
  -w -c /home/stwby/LinuxAvatarGuard/evidence/gpu-audit/legacy-unlocked \
  /home/stwby/Unity/Hub/Editor/2022.3.22f1/Editor/Unity \
  -batchmode -force-vulkan \
  -projectPath /home/stwby/LinuxAvatarGuard/ValidationProject \
  -executeMethod LAGGpuCapture.Run -quit \
  -logFile /home/stwby/LinuxAvatarGuard/gpu-unity-capture.log

env QT_QPA_PLATFORM=xcb \
  LAG_GPU_AUDIT_DIRECTORY=/home/stwby/LinuxAvatarGuard/evidence/gpu-audit \
  /home/stwby/.local/share/linux-avatar-guard-tools/renderdoc_1.46/bin/qrenderdoc \
  --python /home/stwby/LinuxAvatarGuard/Tests/analyze_gpu_capture.py
```

El build portátil incluye el plugin Qt `xcb`; la prueba con `offscreen` no arrancó. El analizador se ejecuta antes de abrir la ventana principal y cierra exclusivamente su propio proceso cuando termina. No instala plugins Qt ni requiere paquetes adicionales.

Esto demuestra exposición de una fixture estática en este entorno. No es una captura de un avatar real, no mide rendimiento y no prueba recuperar una rest pose de cualquier modelo animado. Los OBJ contienen coordenadas proyectadas/NDC, no un avatar listo para subir.

## Consecuencias para el diseño

- El codec polimórfico debe evaluarse por el coste que añade a la extracción de assets y al análisis del decoder; no debe anunciar resistencia a PostVS a partir de variar canales o constantes.
- Fragmentar el payload cambia la entrada. Si el vertex shader vuelve a producir posiciones completas, la salida sigue siendo una superficie de reconstrucción.
- El master seed privado puede individualizar programas, pero los valores y constantes que utiliza el draw deben considerarse observables.
- Las pruebas de extracción deben incluir un atacante adaptativo que lea el shader/configuración del build y la posibilidad de observación de salida.
- Mantener pruebas numéricas de skinning y normalización en Unity. Una futura captura de geometría animada se realizará sobre fixtures propias en Unity o un player propio, dentro del mismo alcance autorizado.
- No bloquear herramientas gráficas, modificar drivers ni intervenir en VRChat para intentar ocultar esta superficie.

## Continuación: prototipo polimórfico estático

El prototipo de Stage 4 se implementó y repitió la captura controlada, con `LAG_GPU_AUDIT_CODEC=static-prototype`. El runner y analizador aceptan únicamente los dos codecs conocidos de estas fixtures. La evidencia nueva se guarda en `evidence/gpu-audit/static-prototype/`, conservando la evidencia legacy anterior.

Resultado: dos draws, 515 vértices y 2304 índices por draw; entrada codificada exacta y geometría PostVS corregida con error máximo ≈ `1,33e-7`. Los valores públicos de runtime siguen observables en el constant block 0, offset relativo 736 bytes. La convención Y y el RMS sin ajuste se conservan en el reporte. No se inspeccionó VRChat.

Se confirma que variar la secuencia, introducir bends reversibles y usar offsets vectoriales no oculta la salida reconstruida del vertex shader. La validación visual y numérica del nuevo programa es independiente de esta conclusión de exposición. Detalle del contrato, pruebas y límites en [STATIC_POLYMORPHIC_PROTOTYPE.md](STATIC_POLYMORPHIC_PROTOTYPE.md).

El prototipo sigue limitado a meshes estáticas y opt-in. El modelo GPU condiciona sus afirmaciones de seguridad; la matriz animada, el análisis adaptativo y la evaluación completa de Stage 19 permanecen pendientes.
