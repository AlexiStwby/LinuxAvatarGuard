# Stage 9 — Performance benchmark

Primer benchmark reproducible del original, legacy y ShaderForge contextual, sobre geometría procedural propia. Se ejecuta exclusivamente en una aplicación Unity Linux/Vulkan, sin SDK ni acceso al proceso de VRChat. El asistente conserva la ruta de avatar validada; ShaderForge sigue limitado a investigación con meshes rígidas.

## Fixture y protocolo

- Unity **2022.3.22f1**, lilToon **2.3.4**, Built-in; Intel Arc A750, i5-12600KF, Mesa/vulkan-intel **26.2.3**, kernel **7.2.6-1-cachyos**.
- **1920 × 1080**, mono/unlit/opaque, cámara ortográfica, sin MSAA, sombras, fog, instancing ni VSync. Player **Release**, Frame Timing Stats activo.
- Un conjunto tiene **dos renderers/materiales, 25.026 vértices y 49.152 triángulos**. Body/Hair comparten una mesh de origen. Se prueban **1 y 16 conjuntos**; las copias de cada variante reutilizan sus meshes/materiales.
- Las tres versiones vienen de bundles Linux nativos. Providers `UsePass` se cargan antes de las fachadas. Materiales serializados guardan valores de desbloqueo **cero**; solo se aplican valores públicos de fixture en memoria.
- Mismo filtro de variantes para original y familias generadas: mono/unlit, sin las variantes de lighting/stereo/instancing no ensayadas. Los conteos representan este filtro, no toda la matriz lilToon.
- **36 procesos independientes**: seis permutaciones de original/legacy/forge, dos cantidades. Calentamiento mínimo de 2 segundos/300 frames, después 8 segundos de medición. **309.150 frames** registrados.
- Control adicional en una ventana por cantidad: todas las variantes residentes, 2 segundos de calentamiento por variante, **12 rondas pareadas**, seis permutaciones repetidas, 1 segundo por bloque. Se descartan 16 frames tras cada cambio para separar el coste de activación y el retraso del contador GPU.
- PNG/readback/compresión y RenderDoc se ejecutan fuera del intervalo medido. No se inyecta RenderDoc durante las mediciones.

Los relojes CPU/GPU y el escritorio no se bloquean. El usuario cerró VRChat antes de medir. Las diferencias pequeñas requieren interpretar la dispersión: las medianas GPU del original variaron entre 0,248–0,482 ms en procesos individuales. El control alternado conserva la misma ventana/contexto; sus diferencias pareadas son la referencia principal para el coste incremental.

## Tiempo de render

Resultados de procesos independientes: mediana de las seis medianas por ejecución; p95 es la mediana de sus seis percentiles 95. Se conserva cada muestra y ejecución, sin combinar los frames como si fueran repeticiones independientes.

| Copias | Variante | GPU mediana / p95, ms | CPU main mediana / p95, ms | CPU render mediana, ms | FPS medio por ejecución, mediana |
| --- | --- | --- | --- | --- | --- |
| 1 | Original | 0,416 / 0,445 | 0,523 / 1,124 | 0,0397 | 1.690 |
| 1 | Legacy | 0,429 / 0,470 | 0,543 / 1,154 | 0,0410 | 1.635 |
| 1 | ShaderForge | 0,436 / 0,476 | 0,538 / 1,139 | 0,0408 | 1.646 |
| 16 | Original | 2,794 / 3,048 | 2,949 / 3,219 | 0,0503 | 353 |
| 16 | Legacy | 2,906 / 3,064 | 2,955 / 3,228 | 0,0509 | 351 |
| 16 | ShaderForge | 2,955 / 3,095 | 2,982 / 3,274 | 0,0511 | 347 |

La diferencia entre medianas aisladas de ShaderForge/original es +0,020 ms con una copia y +0,160 ms con dieciséis. La variación observada impide atribuir íntegramente esa diferencia al decoder. El control alternado y sus intervalos se registran en `evidence/performance/control/summary.json`; las rondas, configuración y muestras están disponibles para revisar la conclusión.

El control final aporta **93.953 frames** adicionales: 85.151 con una copia y 8.802 con dieciséis. Para cada ronda se calcula la diferencia contra el original y se obtiene la mediana de esas doce diferencias. El bootstrap exploratorio remuestrea rondas pareadas, con semilla fija y 10.000 repeticiones; no trata los frames correlacionados como repeticiones ni aplica corrección por comparaciones múltiples.

| Copias | Variante | GPU mediana / p95, ms | Incremento GPU pareado, ms | Intervalo bootstrap 95%, ms |
| --- | --- | --- | --- | --- |
| 1 | Original | 0,294 / 0,318 | 0 | — |
| 1 | Legacy | 0,297 / 0,337 | +0,00383 | −0,00229 a +0,00638 |
| 1 | ShaderForge | 0,299 / 0,337 | +0,00712 | −0,00044 a +0,00961 |
| 16 | Original | 3,742 / 4,083 | 0 | — |
| 16 | Legacy | 3,772 / 4,083 | +0,00948 | −0,00286 a +0,03135 |
| 16 | ShaderForge | 3,781 / 4,117 | +0,02862 | +0,01056 a +0,03846 |

Con una copia, ambos intervalos incluyen cero: no se afirma resolver un coste de pocos microsegundos. Con dieciséis, ShaderForge muestra un incremento pareado pequeño en esta muestra, aproximadamente **0,76%** de la mediana original. No es una garantía de rendimiento en otros escenarios. Se conservaron 94 muestras con contadores de geometría transitorios (92/2), y todas las medianas por bloque coinciden con el original. La repetición final terminó sin excepciones en ambos players.

CPU main/frame incluye esperas de la aplicación; no se presenta como tiempo de cómputo exclusivo del decoder. Los FPS pertenecen a esta fixture sintética sin límite de framerate. La instrumentación añade coste a todas las variantes. [Unity: FrameTimingManager](https://docs.unity3d.com/2022.3/Documentation/Manual/frame-timing-manager.html) documenta su impacto, retraso de cuatro frames y soporte de Linux/Vulkan.

## Recursos y preparación

| Métrica | Original | Legacy | ShaderForge |
| --- | --- | --- | --- |
| Meshes distintas residentes | 1 | 1 | 2 |
| Materiales / shaders-provider assets | 2 / 2 | 2 / 2 | 2 / 4 |
| Draw calls, mediana: 1 / 16 copias | 2 / 32 | 2 / 32 | 2 / 32 |
| SetPass, mediana | 2 | 2 | 2 |
| Buffers GPU de geometría, bytes | 748.080 | 948.288 | 1.896.576 |
| DRM resident local0, MiB: 1 y 16 copias | 76,43 | 76,43 | 78,43 |
| Bundle comprimido, bytes | 485.564 | 672.541 | 1.226.498 |
| Variantes antes / después del filtro | 62 / 9 | 62 / 9 | 124 / 18 |
| Preparación adicional, segundos | 0 | 1,010 | 4,761 |
| Build de bundle, segundos | 0,857 | 0,967 | 1,064 |

Preparación/build son mediciones únicas con cachés existentes, e incluyen importación; no representan un build limpio ni percentiles. El player común de medición ocupa unos 67,7 MB y se reporta aparte del bundle.

Los dos UV Float32×2 añaden **200.208 bytes** a la mesh legacy. ShaderForge separa la mesh por binding, aunque la fuente sea compartida: en esta fixture utiliza **2,54×** los buffers de geometría originales y **2,53×** el bundle. En meshes originalmente distintas la duplicación de la fuente compartida será diferente. Las dieciséis copias reutilizan los mismos recursos; esto no modela dieciséis avatares diferentes ni skinning.

`Used Buffers Bytes` describe buffers GPU; no toda la VRAM. La memoria DRM se lee solo de los clientes del PID propio, deduplicando descriptores por PCI/client-id y limitando muestras al intervalo medido. `local0` es la región local de i915 en esta GPU; los buffers compartidos pueden contabilizarse en clientes distintos. La residencia observada depende del allocator, framebuffer y driver. Se conservan todas las clases y RSS, sin convertirlas en una afirmación de VRAM física única. [Unity: rendering counters](https://docs.unity3d.com/2022.3/Documentation/Manual/ProfilerRendering.html), [kernel: DRM client statistics](https://docs.kernel.org/gpu/drm-usage-stats.html).

## Shaders compilados

Tres capturas RenderDoc **1.46** del propio player, posteriores a las mediciones. Se exportan SPIR-V y estadísticas/assembly del driver mediante `KHR_pipeline_executable_properties`.

| Shader vertex | Operaciones SPIR-V dentro de funciones | Instrucciones GEN reportadas por Intel |
| --- | --- | --- |
| Original | 6.679 | 3.392 |
| Legacy | 6.741 | 3.412 |
| ShaderForge, binding A | 6.831 | 3.442 |
| ShaderForge, binding B | 6.825 | 3.444 |

Los fragment shaders son idénticos por SHA-256 en las tres variantes. Los conteos SPIR-V incluyen delimitadores; los conteos GEN proceden de la sección **vertex**, separada de fragment. Describen el ejecutable completo con ramas lilToon, no las instrucciones ejecutadas por cada vértice ni ciclos medidos. Se mantienen spills/fills del driver en el informe; sus estimaciones de ciclos no sustituyen al tiempo GPU. El warmup encolado puede aparecer en la captura junto a los dos draws; el conteo se deduplica por hash del programa.

## Presupuestos y decisiones

Los informes calculan umbrales **provisionales de aviso** para repetir sobre los mismos bundles/hardware: mediana × 1,10 + amplitud de medianas entre repeticiones/rondas. El 10% es margen de política explícito. Los umbrales amplios reflejan la variación; no certifican otros avatares, equipos o VRChat. Un aviso requiere nuevas rondas pareadas antes de aceptar una regresión.

Condiciones verificables de esta fixture: diferencia visual media desbloqueada ≤ 0,002; diferencia bloqueada ≥ 0,003; draw/triangle median por bloque igual al original; muestras GPU únicas suficientes. Los PNG nativos tienen diferencia desbloqueada **0** en ambas cantidades. Se conserva cualquier contador transitorio en CSV; no se eliminan outliers de tiempo para mejorar resultados.

El coste de recursos por binding está demostrado. Antes de ampliar el contrato conviene medir escalado con meshes/materiales/clips distintos y perfilar la preparación, conservando aislamiento entre bindings y detección de mutaciones. Una deduplicación requiere identidad completa de programa/payload/layout/contexto; compartir únicamente la fuente no basta. El gate de skinning continúa activo.

## Reproducción y evidencias

Los scripts públicos están en [Tests/Performance](../Tests/Performance). Se requiere un checkout, el `ValidationProject` desechable con la versión lilToon fijada y un Unity Linux ya instalado. No copiar estas herramientas al proyecto de un usuario final. Las herramientas no descargan dependencias.

```sh
python3 Tests/Performance/run_benchmark.py --build --unity /ruta/Unity/Editor/Unity --output /ruta/nueva/evidence/performance
python3 Tests/Performance/run_interleaved.py --build --unity /ruta/Unity/Editor/Unity --output /ruta/nueva/evidence/performance
# Separado y solo con RenderDoc previamente instalado/autorizado:
python3 Tests/Performance/capture_shaders.py --renderdoc /ruta/renderdoc_1.46 --output /ruta/nueva/evidence/performance
```

`LAGPerformanceValidation` exige Linux/Vulkan/Built-in y el proyecto desechable; crea solo su carpeta de fixture y la carpeta de investigación conocida. El proyecto del player separado no incorpora SDK, fuentes lilToon ni avatares de terceros. Los bundles aportan las dependencias compiladas. Copias originales y fuentes lilToon permanecen byte idénticas; las claves reales del usuario no se leen ni modifican.

En `evidence/performance/`: `fixtures.json`, `forge-manifest.json`, `summary.json`, `run-plan.json`, `host-conditions.json`, `gpu-comparison.png`, `runs/*/{frames.csv,run.json,process-memory.json,*.png}`, `control/summary.json`, `shader-instructions.json`, bundles y capturas/assembly. Los hashes de bundles y assemblies fijan el experimento. Evidencias/binarios/proyectos se excluyen de Git y de la release 0.2.1; el repositorio publica el método y resultados agregados.

Este ensayo cubre meshes rígidas opacas y unlit. Quedan pendientes Animator durante carga sostenida, skinning/blendshapes, texturas complejas, variantes lighting/stereo/outline, múltiples avatares con recursos diferentes y el resultado final procesado por SDK. No se ha capturado ni analizado VRChat.
