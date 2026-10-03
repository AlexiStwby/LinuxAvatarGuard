# TextureGuard — integración contextual de investigación

Stage 12. Unity **2022.3.22f1**, Linux/Vulkan, Built-in y fuentes de **lilToon 2.3.4** con el digest revisado. El asistente de avatares continúa usando la ruta legacy que ya fue probada; esta API no prepara un avatar skinned para publicar.

La [continuación de mipmaps/skinning](MIP_SKIN_RESEARCH.md) incorpora APIs nuevas y schema 5. Este documento conserva el contrato y las mediciones de V1/Stage 12. Los registros V1 de schema 4 siguen siendo legibles; los nuevos registros no se degradan a esa versión.

## Qué integra

`GuardShaderForge.PrepareWithTextures` analiza la raíz, sus mallas rígidas, el Animator genérico y todos los materiales que pueden aparecer en cada slot. Genera una copia independiente de meshes, albedos, materiales, clips, controllers, BlendTrees y overrides. Los originales y sus importadores permanecen intactos.

Cada **renderer × slot × material × `_MainTex`** tiene una `GuardTextureBindingIdentity`. La identidad incorpora el binding de malla, identidad/contenido/importador del albedo, estado del material y fingerprint del contexto de animación. Dos slots que usan la misma textura reciben programas diferentes; un cambio de fuente o contexto invalida la reproducción privada.

El shader de cada ocurrencia combina el decoder contextual de geometría y el decoder de albedo. La implementación de textura se inserta en los providers reales de lilToon; no basta con modificar la fachada. Las cuatro propiedades runtime se comparten entre ambos codecs y los materiales guardados contienen **cero**. El payload sigue siendo una textura lineal RGBA32 sin copia CPU legible; no se crea una textura descifrada en runtime.

## Contrato admitido

| Entrada | Soporte de Stage 12 |
| --- | --- |
| MeshRenderer rígido y Animator genérico | Contrato positivo de Stage 8; copias contextualizadas. |
| Material y albedo | `lilToon` opaco, persistente, guardado, sin keywords; todos los materiales posibles deben cumplir. |
| Fuente RGBA32 | Legible, todos los texels opacos, sin compresión. |
| Fuente RGB24 | Admitida en la ruta contextual; convertida a RGBA32 únicamente en el payload nuevo. |
| Sampling | Point/Bilinear; Repeat/Clamp por eje; sin anisotropía/streaming; un solo mip. |
| Dimensiones | Cuadradas, power-of-two, 16–1024. |
| Material swaps | Remapeados por renderer/slot; tiempos y referencias contextuales preservados. |
| Animación `_MainTex_ST.xyzw` | Permitida explícitamente mediante `GuardAnimationContext.Capture(root, true)`; curvas finitas preservadas. |
| Curvas de color, transforms y activación | Contrato positivo contextual previo. |
| Claves runtime | Valores históricos 0–255, incluyendo lanes individuales cero; se exige coincidencia con el contexto y se rechaza el vector todo cero. |
| Skinning, blendshapes, SDK/avatar procesado | Pendientes; se rechazan antes de generar una copia. |
| Mipmaps, trilinear, compresión del payload, normales/emisión/máscaras protegidas | Pendientes; no se habilitan silenciosamente. |
| Referencias animadas directas a texturas | Rechazadas; usar material swaps dentro del contrato. |

RGB24 almacena tres canales de ocho bits según [Unity, TextureFormat.RGB24](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/TextureFormat.RGB24.html). La conversión contextual obtiene Color32 mediante [GetPixels32](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2D.GetPixels32.html), con alfa opaco, y conserva bytes/flags del original. Esto amplía el formato de entrada; **no añade compresión**. La API standalone de Stage 11 conserva su contrato RGBA32 y claves 1–255.

## Respaldo y reproducción

`GuardBuildContext` usa schema privado **4**. Conserva la derivación y los bytes runtime históricos de las mallas; deriva los programas de textura mediante HKDF-SHA256 con un dominio propio, BuildID e identidad contextual. No cambia los perfiles OSC productivos.

El archivo privado contiene seed, versiones y registros ordenados de mallas/texturas. Se guarda fuera de `Assets` y Git, bajo `$XDG_DATA_HOME/linux-avatar-guard/prototype-builds/` o el directorio de datos del usuario. El store mantiene directorios 0700, archivos 0600, creación sin sobrescritura y comprobaciones de propietario/tipo/enlaces. Las fuentes necesarias para reproducirlo deben conservarse por separado; el respaldo no contiene los píxeles originales.

Se mantienen las lecturas de schemas 1/2/3 para contextos de malla. Un documento con registros de textura no puede bajar a esos schemas. Un contexto restaurado rechaza bindings desconocidos, programas ajenos y fuentes/contextos modificados. Un contexto con texturas no puede pasar inadvertidamente por `Prepare` de solo geometría.

```csharp
using (var context = GuardBuildContext.CreateRandom())
using (var artifact = GuardShaderForge.PrepareWithTextures(
    ownedRigidRoot, context,
    "Assets/LinuxAvatarGuardGenerated/Research/owned-integrated-experiment"))
{
    string privatePath = context.SavePrivate(); // Fuera de Unity/Git.
    // artifact.PrefabPath y PublicManifestPath permiten revisar la copia.
}

using (var restored = GuardBuildContext.LoadPrivate(buildId))
using (var replay = GuardShaderForge.PrepareWithTextures(
    sameUnmodifiedRoot, restored,
    "Assets/LinuxAvatarGuardGenerated/Research/owned-integrated-replay"))
{
    // Mismos programas y bytes codificados; assets nuevos con GUID propios.
}
```

La salida debe ser una carpeta nueva. Algunos programas de geometría se rechazan por su evaluación numérica/keyless; requieren otro contexto. El API no sustituye una salida previa ni crea perfiles del asistente.

## Transacción y artefacto final

Antes de escribir, se analizan/codifican todas las ocurrencias y se detectan referencias compartidas del albedo por propiedades sin decoder. Después de importar/copiar, se comprueban fuentes, shaders/providers, sampler/formato del payload, claves cero, referencias de renderer y remapeo de clips. El cierre de dependencias del prefab debe excluir todo albedo/material original protegido, incluso cuando una referencia indirecta aparece en una propiedad distinta.

Los planes de malla y textura se registran en un lote que restaura ambos mapas si falla cualquier registro. Un error tardío elimina únicamente la carpeta nueva y las instancias temporales; las salidas existentes se preservan. El manifest público lista binding, slot, propiedad, programa y rutas generadas, declara el estado de investigación y `sdkProcessed: false`; no contiene seed ni claves esperadas.

## Validación reproducible

Copiar las fuentes y tests en un proyecto **desechable** `ValidationProject` con las versiones fijadas. El runner recrea `Assets/TextureIntegrationFixture`; no ejecutarlo en el proyecto de trabajo del avatar.

```bash
LAG_BINDING_AUDIT_ALLOWED=synthetic-unity-only /ruta/Unity \
  -batchmode -force-vulkan -projectPath /ruta/ValidationProject \
  -executeMethod LAGTextureIntegrationValidation.Run -quit \
  -logFile /ruta/texture-integration.log
```

Repetir tras cambiar el proyecto desechable a Gamma y a Linear, en sesiones independientes. Los tests cubren dos renderers y dos slots por fixture, materiales compartidos, RGBA32/RGB24 sRGB/linear, filtros Point/Bilinear, ST animado, nested BlendTrees y overrides. Construyen/reabren bundles Linux/Vulkan y compilan Windows64. La compilación Windows no demuestra ejecución D3D ni en VRChat.

El extractor independiente interpreta las constantes del **HLSL del provider**, verifica que sus copias de programa coincidan y recupera los payloads de GPU. Recuperación byte idéntica con shader y valores runtime demuestra la limitación de esta ofuscación; no se declara resistencia a extracción adaptativa o capturas GPU.

`LAGTextureIntegrationImportSmoke.Run` valida importación/API sin SDK ni lilToon, reproducción de mallas privadas schemas 1/2/3/4 y la API standalone de textura. Requiere el proyecto desechable `ImportValidationProject` y `LAG_TEXTURE_IMPORT_ALLOWED=owned-api-smoke`. No ejecuta ShaderForge contextual sin las fuentes de lilToon.

Las métricas finales están en [ROADMAP_STATUS.md](ROADMAP_STATUS.md). Evidencia local ignorada por Git: `evidence/texture-integration/{gamma,linear}/`, `sdk-free.json` y logs de ejecución.

## Coste y requisitos antes del asistente

La validación de recursos registra payloads, shaders, bytes, tamaño/build de bundles y generación. Point necesita una lectura de texel; Bilinear cuatro. La copia contextual multiplica payloads/familias para una fuente compartida; no deduplicarlos entre programas distintos es una decisión de diversidad con coste real.

El benchmark usa únicamente el player propio SDK-free de Stage 9, Linux/Vulkan, Release, Gamma, mono/unlit, 1920×1080 y sin VSync. Compara original, MeshGuard contextual y MeshGuard+TextureGuard sobre geometría/sampling iguales y batching desactivado en todos. Las lecturas de `/proc` y DRM/RSS se limitan al PID del player lanzado. Las imágenes se toman antes del warmup/timing; las rondas pareadas descuentan el baseline MeshGuard para estimar el coste adicional del albedo. Código: [run_texture_benchmark.py](../Tests/Performance/run_texture_benchmark.py).

Para repetirlo, preparar los proyectos desechables de [Stage 9](PERFORMANCE_BENCHMARK.md) y copiar la versión actual de `Tests/Performance` al proyecto SDK-free. Usar Gamma en ambos proyectos, una salida absoluta nueva y cerrar las aplicaciones que compitan por GPU durante la medición. Los valores de desbloqueo de `fixtures.json` pertenecen exclusivamente a la fixture propia.

```bash
LAG_BINDING_AUDIT_ALLOWED=synthetic-unity-only \
LAG_TEXTURE_BENCH_OUTPUT=/ruta/nueva/texture-benchmark /ruta/Unity \
  -batchmode -force-vulkan -projectPath /ruta/ValidationProject \
  -executeMethod LAGTextureIntegrationValidation.BuildPerformanceFixtures -quit \
  -logFile /ruta/texture-fixtures.log

LAG_PERFORMANCE_ALLOWED=owned-fixtures-only LAG_PERFORMANCE_MODE=isolated \
LAG_PERFORMANCE_OUTPUT=/ruta/nueva/texture-benchmark /ruta/Unity \
  -batchmode -force-vulkan -projectPath /ruta/PerformancePlayerProject \
  -executeMethod LinuxAvatarGuard.Performance.PlayerBuild.Run -quit \
  -logFile /ruta/texture-player.log

LAG_PERFORMANCE_ALLOWED=owned-fixtures-only LAG_PERFORMANCE_MODE=interleaved \
LAG_PERFORMANCE_OUTPUT=/ruta/nueva/texture-benchmark/control /ruta/Unity \
  -batchmode -force-vulkan -projectPath /ruta/PerformancePlayerProject \
  -executeMethod LinuxAvatarGuard.Performance.PlayerBuild.Run -quit \
  -logFile /ruta/texture-paired-player.log

python3 Tests/Performance/run_texture_benchmark.py \
  --output /ruta/nueva/texture-benchmark
```

`--analyze` recalcula el informe a partir de los archivos guardados sin lanzar players. Las fixtures se congelan antes de medir y el runner rechaza carpetas de ejecuciones existentes.

Falta validar pirámides originales codificadas por mip, derivadas/minificación y presupuesto de calidad/coste, después los formatos/features adicionales. El payload XOR no admite compresión con pérdidas sin un codec específico. También falta integrar y validar skinning, funciones de avatar y artefactos procesados por el SDK antes de exponer TextureGuard en el asistente o una release para Booth. El cliente VRChat queda fuera de los análisis GPU y de estas pruebas.
