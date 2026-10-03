# Mipmaps y skinning — continuación de TextureGuard

Desarrollo opt-in para **Unity 2022.3.22f1, Linux, Vulkan, Built-in y lilToon 2.3.4**. Añade `TextureGuardCodecV2` y `SkinnedLinearCodecV1`, con copias independientes y respaldo privado schema 5. La ruta del asistente y la descarga 0.2.1 siguen siendo las ya publicadas. No se han procesado ni capturado clientes de VRChat para estas pruebas.

## Pirámide de mipmaps

Cada nivel original se codifica por separado, incluyendo los niveles de 2×2 y 1×1. Programa, permutación y sales se derivan por nivel. El grid es `min(4, tamaño del nivel)`. Los mipmaps originales pueden haber sido generados por Unity o contener bytes distintos definidos por el autor; se conserva esa pirámide exacta.

El payload nuevo es RGBA32 lineal, conserva filtro, wrap y mip bias y se sube mediante `Apply(false, true)`. Nunca se usa `Apply(true)` para regenerar niveles desde píxeles codificados: Unity [documenta que esa llamada reconstruye los mipmaps desde el nivel 0](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2D.Apply.html). `ignoreMipmapLimit=true` mantiene el payload completo; los [límites de resolución y el comportamiento de texturas sin copia CPU](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2D-ignoreMipmapLimit.html) requieren evaluar por separado los ajustes dinámicos del cliente.

El provider real de lilToon consulta el LOD de hardware mediante [`CalculateLevelOfDetail`](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-to-calculate-lod), utilizando las UV originales y el sampler con su bias. Lee texels con `Load`, deshace la codificación y aplica el filtrado sobre los colores recuperados. En Linear, la transferencia sRGB se realiza por texel antes de interpolar. Point necesita una lectura; Bilinear cuatro; Trilinear hasta ocho, más la consulta de LOD. El coste de Stage 12 sobre una textura de un nivel no mide esta ruta nueva.

| Entrada | Contrato V2 |
| --- | --- |
| Formato | RGBA32 o RGB24 legible, opaco en todos los niveles, sin compresión. |
| Dimensiones | Cuadradas, potencias de dos, 16–1024. |
| Niveles | Cadena completa o un solo nivel; no cadenas parciales. |
| Filtros | Point, Bilinear y Trilinear. |
| Wrap | Repeat/Clamp por eje. |
| Bias | Finito, −8 a +8; el sampler nuevo lo conserva. |
| Anisotropía, streaming, compresión, alpha | Fuera del contrato. |
| Calidad validada | `globalTextureMipmapLimit=0`; cambios dinámicos pendientes. |
| Otras propiedades de textura | Solo `_MainTex` opaco tiene decoder. |

V1 conserva su identidad, hashes y dominio histórico; continúa rechazando fuentes con mipmaps. V2 usa un dominio de derivación, identidad de binding y programa distintos. No se convierte silenciosamente una fuente con mips a V1.

## Skinning y blendshapes

La inversión del codec rígido no lineal después de linear-blend skinning no equivale a invertir antes de deformar. Por ello, las mallas skinned usan un codec lineal separado. Para cada vértice, con normal `n` y escalar keyed `a` transportado en dos UV libres:

```text
p_codificado = p + n·a
Δp_codificado = Δp + Δn·a    // cada frame de cada blendshape
p_recuperado = p_skinned_codificado − n_skinned·a
```

La cancelación depende de que la normal de entrada al hook siga siendo la normal **sin normalizar** que produjo el skinning. Normalizarla antes de restar rompería la igualdad. Se conserva tanto el delta de normal como el de tangente de todos los frames, incluidos morphs con deltas de normal no nulos. No se recalculan normales ni se sustituyen bindposes o pesos. Unity define la [correspondencia entre bindposes e índices de hueso](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Mesh-bindposes.html); esa correspondencia y el orden de huesos deben permanecer iguales en la copia.

El experimento alternativo de reencuadrar la malla y compensar bindposes solo está en el test de caracterización: permite obtener la geometría original al posar sin clave y no es la implementación del producto.

Se preservan los bounds controlados por el autor cuando `updateWhenOffscreen=false`. Con ese flag habilitado, Unity [recalcula los bounds por frame](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Renderer-localBounds.html) a partir de la pose de cada malla; los de la malla codificada pueden diferir de los originales. Se conserva el flag y esos bounds automáticos no se usan como identidad estable. La matriz actual centra la geometría en cámara; el culling junto a los límites del frustum y la persistencia de overrides de bounds requieren pruebas adicionales antes del asistente.

Se reservan UV de índices **4 y 5** para streams de skinning/posición anterior; se elige el par ordenado de índices **6 y 7** únicamente si está libre y su uso por materiales fue revisado. Un carrier ocupado provoca rechazo. Los shaders generados desactivan batching y el manifest distingue expresamente el codec skinned del rígido.

| Entrada | Contrato skinned |
| --- | --- |
| Mesh y esqueleto | Legibles, normales no nulas, bindposes afines no singulares; hasta 256 huesos pertenecientes a la raíz. |
| Pesos | Una a cuatro influencias, pesos finitos normalizados; sin reparación automática. |
| Morphs | Nombres únicos, frames ordenados finitos; compensación de deltas de posición y preservación de normales/tangentes. |
| Animator | Genérico; clips/controllers persistentes del contrato contextual. |
| Materiales/animaciones | Copias por slot y remapeo de swaps/ST; curvas de blendshape revisadas. |
| Humanoid, SDK/VRChat, Cloth | Rechazados por esta ruta de investigación. |
| Offsets | Intensidad positiva ≤0,5; rango revisado y round-trip de vértices/morphs ≤2×10⁻⁵. |
| UV ocupadas, property blocks de entrada, referencias externas a huesos | Rechazo temprano. |

`BakeMesh` sirve de referencia numérica, pero [Unity siempre lo calcula en CPU, incluso con GPU Skinning habilitado](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/SkinnedMeshRenderer.BakeMesh.html). La validación nativa utiliza builds Release separados con GPU Skinning habilitado/deshabilitado y compara **renders** de ambos caminos. No presenta `BakeMesh` como evidencia GPU.

Esta ruta sigue siendo **ofuscación lineal**. Los cuatro valores de runtime y el provider público permiten un extractor adaptativo, y la geometría corregida se observa después del vertex shader. El soporte de huesos y mipmaps resuelve compatibilidad; no demuestra impedir GPU ripping ni cifrado fuerte.

## Integración y respaldo

```csharp
using (var context = GuardBuildContext.CreateRandom())
using (var artifact = GuardShaderForge.PrepareWithFeatures(
    ownedGenericRoot, context,
    "Assets/LinuxAvatarGuardGenerated/Research/owned-mip-skin",
    strength: 0.2f, allowMipmaps: true, allowSkinning: true))
{
    string backup = context.SavePrivate(); // Fuera de Assets y Git.
    // Revisar artifact.PrefabPath y PublicManifestPath.
}
```

`PrepareWithSkinning` permite el experimento de geometría sin texturas. `Prepare` y `PrepareWithTextures` mantienen sus contratos previos; no habilitan skinning o mips por defecto. Estas APIs no crean perfiles OSC, no publican avatares y no habilitan la nueva ruta en el asistente.

Schema privado **5** conserva los bytes runtime y la derivación histórica de las mallas/V1. Añade dominios tipados HKDF-SHA256 para programa/payload/layout skinned y programa de textura V2. Los registros declaran la política de skinning/mips y verifican su reproducción desde la fuente. Se leen schemas 1–4; registros skinned o de mips no pueden degradarse a schema 4. Los respaldos V1 de texturas siguen siendo compatibles con schema 4.

Antes de escribir se analizan y codifican todos los bindings. Después de importar, se comprueba la huella completa de la malla codificada, incluyendo morphs y bindposes, el sampler/pirámide del payload, claves serializadas cero, shaders, huesos y cierre de dependencias. Una mutación tardía revierte la carpeta nueva y ambos mapas de registros. La fuente, sus importadores y archivos `.meta` permanecen intactos.

## Pruebas reproducibles

Solo proyectos desechables y fixtures propias. Copiar el paquete y los tests a `ValidationProject` con las versiones fijadas. Repetir en sesiones independientes Gamma/Linear:

```bash
LAG_BINDING_AUDIT_ALLOWED=synthetic-unity-only /ruta/Unity \
  -batchmode -force-vulkan -projectPath /ruta/ValidationProject \
  -executeMethod LAGMipSkinValidation.RunTextures -quit \
  -logFile /ruta/mips.log

LAG_BINDING_AUDIT_ALLOWED=synthetic-unity-only /ruta/Unity \
  -batchmode -force-vulkan -projectPath /ruta/ValidationProject \
  -executeMethod LAGMipSkinValidation.RunSurfaceAndExport -quit \
  -logFile /ruta/surface.log
```

El test recrea `Assets/MipSkinFixture`; no ejecutarlo en el proyecto del avatar. La matriz de textura cubre dos formatos, sRGB/linear, tres filtros, seis escalas de minificación, UV negativas y bias positivo/negativo. Un extractor independiente interpreta el provider emitido y recupera **cada mip nativo**, incluida la última imagen de 1×1. Claves ausentes/incorrectas sirven de controles negativos; se exige referencia visual no vacía.

La superficie usa dos huesos, hasta dos influencias por vértice, `SkinQuality.Bone4` y `updateWhenOffscreen=true`. La validación de bounds estáticos cuando ese flag está deshabilitado se limita al rechazo de una mutación del plan. Rendering/culling con ese flag deshabilitado y paletas de tres/cuatro influencias quedan pendientes.

La superficie mezcla una malla rígida con una skinned, tres blendshapes de múltiples frames con deltas de normal/tangente, huesos con rotación/escala no uniforme, seis tiempos, dos vistas, swaps y ST animado. Se prueban rechazo de mutaciones de huesos/morphs, política legacy, downgrade de schema, reproducción privada exacta y rollback de tres fallos tardíos.

Los bundles propios de `player-fixtures/` se abren en `PerformancePlayerProject`, sin SDK/lilToon de Editor. Añadir el módulo integrado `com.unity.modules.animation` al manifest de ese proyecto y copiar `Tests/Performance`. Construir para cada espacio de color y `LAG_VALIDATION_GPU_SKINNING=true/false`:

```bash
LAG_PERFORMANCE_ALLOWED=owned-fixtures-only LAG_PERFORMANCE_MODE=mip-skin-validation \
LAG_VALIDATION_GPU_SKINNING=true LAG_VALIDATION_COLORSPACE=Gamma \
LAG_PERFORMANCE_OUTPUT=/ruta/evidence/players/gamma-gpu /ruta/Unity \
  -batchmode -force-vulkan -projectPath /ruta/PerformancePlayerProject \
  -executeMethod LinuxAvatarGuard.Performance.PlayerBuild.Run -quit \
  -logFile /ruta/player-build.log

LAG_PERFORMANCE_ALLOWED=owned-fixtures-only \
  /ruta/evidence/players/gamma-gpu/player/lag-performance.x86_64 -force-vulkan \
  --lag-config /ruta/evidence/mip-skin/gamma/player-fixtures/fixtures.json \
  --lag-skinning gpu --lag-output /ruta/evidence/mip-skin/gamma/gpu-run \
  -logFile /ruta/player-run.log
```

Cambiar tanto el setting de build como su etiqueta a `false`/`cpu` para CPU, y el color en **ambos proyectos** para Linear. El informe del build registra el setting real; una constante compilada comprueba la etiqueta runtime. Las capturas se guardan localmente en `LinuxAvatarGuard/evidence/mip-skin/`, ignoradas por Git. Solo incluyen contenido procedural propio y valores públicos de esa fixture.

La captura opcional se activa con `--lag-capture` únicamente en ese player propio, utilizando la instalación de RenderDoc ya autorizada y una capa Vulkan local al proceso. Captura la pose 0,75 con morphs activos. [analyze_mip_skin_capture.py](../Tests/Performance/analyze_mip_skin_capture.py) exige un informe de fixture propia aprobado, distingue dispatches por recursos de blendshape/huesos y lee posiciones PostVS finitas. En la captura analizada se observaron **18 dispatches de blendshapes, 3 de skinning esquelético y 9 draws con posiciones PostVS legibles**. No basta con habilitar el setting GPU Skinning para declarar esa ruta observada.

## Resultados medidos

Intel Arc A750, Unity 2022.3.22f1, Vulkan, lilToon 2.3.4; fixtures propias mono/unlit. Diferencias RGB normalizadas 0–1:

| Prueba | Gamma | Linear |
| --- | --- | --- |
| Textura V2 | 377 checks; 72 comparaciones; 84 mips recuperados byte exactos. | 377 checks; 72 comparaciones; 84 mips recuperados byte exactos. |
| Mayor diferencia media de textura | 0,0000876832 | 0,0001263920 |
| Mayor diferencia de un canal de textura | 0,00392163 | 0,01568628 |
| Superficie nativa en Editor | 108 checks; 12 comparaciones. | 111 checks; 12 comparaciones. |
| Mayor diferencia media de superficie | 0,0000314085 | 0,0000380173 |
| Player GPU Skinning habilitado | 74 checks; 12 comparaciones. | 74 checks; 12 comparaciones. |
| Player GPU Skinning deshabilitado | 74 checks; 12 comparaciones. | 74 checks; 12 comparaciones. |
| Mayor diferencia media en players | 0,0000484940 | 0,0000595591 |

Los tres checks adicionales de Linear cubren bounds estáticos y rechazo de datos finitos que pierden precisión al codificar normales/deltas de morph. La referencia CPU de los players tiene error máximo de posición **1,87638×10⁻⁷ unidades**; las magnitudes de normal observadas son 0,58119–1,36253. No se normalizan para realizar la inversión. Cada player produce 48 PNG; las matrices principales de mipmaps y superficie producen 672 PNG en total. Los dos montajes locales enlazan capturas originales/desbloqueadas/controles sin alterar los archivos de captura.

Los controles con clave ausente e incorrecta superan los umbrales de diferencia visual en todas las comparaciones aprobadas. Se conserva la recuperación exacta por extractor HLSL como limitación observada. En V1 se repitió la integración de 311 checks, incluida lectura de respaldos de textura schema 4. La suite previa comprende 1341 checks más la auditoría standalone V2 de tres checks, además de shaders/bundles; importación/API sin SDK ni lilToon y seis tests OSC también se verificaron.

Los logs del Editor conservan avisos conocidos de Licensing y asignaciones pendientes/JobTempAlloc al cerrar. La primera regresión llegó a todos sus marcadores y después esperó 300 segundos por trabajo asíncrono de paquetes; el wrapper se ajustó para salir explícitamente después de completar las aserciones/reportes. Esto no certifica estabilidad o ausencia de fugas del Editor. Los players Release terminan con código 0 y sus informes distinguen las pruebas funcionales de un benchmark.

Resultados finales y límites se registran en [ROADMAP_STATUS.md](ROADMAP_STATUS.md). Aún requieren evaluación propia la compresión, streaming, calidad dinámica, lighting/stereo/instancing completos, Humanoid y funciones del SDK de avatares, postprocesamiento de build, integración de wizard y coste de rendimiento de esta ruta. La implementación no convierte automáticamente Carukia ni certifica un avatar listo para subir.
