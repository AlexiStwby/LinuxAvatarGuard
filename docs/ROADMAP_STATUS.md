# Estado y plan de mejora de seguridad

Fecha: 2026-10-03. Versión productiva auditada: 0.2.1 (`7f3ebc3`). Entrada: `LinuxAvatarGuard_SECURITY_ROADMAP.md`, especialmente apartados 37–45.

## Decisión de esta iteración

El roadmap identifica correctamente la necesidad de diversidad, validación y trazabilidad. El codec actual puede ser reconstruido con una fórmula estable si se conocen sus cuatro parámetros. La baseline añadida reproduce esa debilidad; no constituye una nueva protección.

La primera iteración entregó auditoría, baseline y diseño. Las continuaciones implementaron el adaptador legacy, metadata/rollback, análisis GPU controlado y el prototipo estático de Stage 4. Stage 5 añade identidad y derivación por binding con contexto privado reproducible; Stage 6 mide diversidad y extracción adaptativa; Stage 7 incorpora asignación conservadora de atributos estáticos; Stage 8 añade ShaderForge contextual para raíces rígidas con Animator genérico. Stages 10/11 añaden investigación y piloto de albedo opaco de TextureGuard. Stage 12 incorpora mipmaps/skinning de investigación; Stage 13 entrega MetadataGuard opt-in y Stage 14 completa investigación de fingerprints e identidad privada por CPU. Stage 15 añade el núcleo geométrico validado por CPU y un adaptador Unity compilado; su ejecución y validación visual siguen pendientes. La fórmula legacy, UV, formato OSC y avatares publicados siguen compatibles; los binarios de 0.2.1 no se sustituyen. La integración productiva de esas capas y del codec nuevo continúa pendiente.

El análisis GPU se limita a Unity y aplicaciones de prueba propias por instrucción del usuario. RenderDoc se descargó con su permiso; no se realiza análisis GPU sobre VRChat.

## Stage 1 — Repository audit

**Status:** completado para la arquitectura presente.

**Changes:** mapa de código, rutas públicas/privadas, fórmula, OSC, integración lilToon, copia de dependencias, validación y ciclo de vida. Comparación por subsistema y amenazas L0–L6 en [SECURITY_ARCHITECTURE_CURRENT.md](SECURITY_ARCHITECTURE_CURRENT.md).

**Tests:** inspección del código actual y contraste con la baseline ejecutada. Referencias externas primarias enlazadas en la auditoría y el diseño.

**Known issues:** fórmula universal; clave compartida por mesh; canales fijos; texturas originales; falta de revisión del bundle final; preparación completa sin transacción; dependencias de nombres/prefijos. La protección de captura GPU no está demostrada.

**Next stage:** completar la revisión del artefacto procesado por el SDK y prototipar el codec estático bajo las condiciones del diseño.

## Stage 2 — Tests for current LAG behavior

**Status:** baseline funcional y adversarial ejecutada; cobertura completa pendiente.

**Changes:** [LAGSecurityBaseline.cs](../Tests/LAGSecurityBaseline.cs) añade un decoder independiente y experimentos sobre fixtures propias. `.gitignore` permite publicar los documentos y el test, y excluye nuevas categorías privadas previstas por el roadmap.

**Tests:**

| Suite | Resultado observado | Alcance |
| --- | --- | --- |
| `LAGValidation.Run` | 26/26 comprobaciones | Preservación de fuente, shaders, FX, materiales animados, expresiones y fixtures estáticas/skinned en Vulkan. |
| `LAGSecurityBaseline.Run` | 14/14 comprobaciones | Round-trip independiente, claves ausente/incorrecta, datos conservados y debilidades conocidas del codec actual. |
| `test_osc.py` | 6/6 tests | Configuración privada, formato OSC, loopback, avatar vinculado y OSCQuery simulado. |

Entorno verificado: Unity 2022.3.22f1, SDK Avatars 3.10.5, lilToon 2.3.4, Intel Arc A750, Vulkan. Proyecto aislado `ValidationProject`; el proyecto de Carukia no se modificó.

En la repetición final:

- Diferencia visual desbloqueada, fixture de un hueso y fixture de dos huesos: 0.
- RMS del decoder independiente en plano sintético: 0; clave ausente ≈ 0,1143 y clave incorrecta ≈ 0,0947 unidades. Los payloads usan aleatoriedad; estas métricas corresponden a la ejecución final de esta continuación.
- 100/100 payloads diferentes, todos reconstruidos por un decoder sin cambiar su fórmula. Son ejecuciones del encoder, no builds/uploads completos.
- Recuperación de los cuatro valores en un experimento de plaintext conocido con correspondencias: correcta. La prueba no presupone que un atacante siempre tenga la malla original.

Las aserciones `KNOWN WEAKNESS` pasan al reproducir una vulnerabilidad legacy. Cuando el nuevo codec la cambie, se conserva esta suite para legacy y se añade una suite con expectativas propias para el codec nuevo.

El primer arranque tuvo una compilación transitoria sin resolver `GuardText` y recompiló correctamente después. La repetición final no tiene errores C# ni errores de compilación de shader y terminó con código 0. Ambos arranques registran aviso de token de Unity Licensing y, durante el cierre, `m_TaskQueue.empty()` y avisos `JobTempAlloc`. Las suites terminaron antes de esos mensajes; no se ha aislado su causa. Esta ejecución no sirve como benchmark de memoria/rendimiento ni como certificación de estabilidad del Editor.

**Known issues:** faltan extracción del AssetBundle procesado por el SDK, 100 builds completos, matriz extensa de poses/features y sincronización en otro cliente VRChat. La captura GPU estática y tres fronteras de fallo tardío de preparación se cubren en los apartados siguientes; siguen pendientes capturas animadas en Unity y fallos dentro de cada etapa. La comprobación anterior del usuario en Carukia es evidencia de funcionamiento de ese avatar; no sustituye estas pruebas de seguridad.

**Next stage:** establecer fixtures y mediciones pendientes en el diseño del adaptador; no interpretar el resultado como cobertura terminada.

## Stage 3 — Codec IR design

**Status:** adaptador y programa legacy tipado implementados y probados. El generador polimórfico se implementó después en Stage 4.

**Changes:** [GuardCodec.cs](../Package/Assets/LinuxAvatarGuard/Editor/GuardCodec.cs) implementa `IMeshCodec`, `CodecPlan`, `CodecProgram`, `AttributeLayout`, `DecoderFragment` y `LegacyLinearCodecV1`. El builder y el generador de shader pasan por el contrato; se mantienen las APIs anteriores. La validación del mesh puede ejecutarse sin generar una copia aleatoria. [POLYMORPHIC_CODEC_DESIGN.md](POLYMORPHIC_CODEC_DESIGN.md) conserva el diseño de las etapas siguientes.

**Tests:** 18 comprobaciones de [LAGCodecValidation.cs](../Tests/LAGCodecValidation.cs), incluido decoder idéntico al fixture congelado antes del refactor, valores límite de runtime, preservación y rechazo de planes ajenos. Error máximo de round-trip observado ≈ 4,47 × 10⁻⁸; error frente a fórmula legacy: 0. Las 26 regresiones funcionales y las 14 comprobaciones adversariales legacy siguen correctas.

**Known issues:** la fórmula del roadmap `decode → skinning` requiere demostrar un orden compatible con Unity/lilToon. Las transformaciones afines pueden colapsarse y un extractor adaptativo podría interpretarlas. La derivación de constantes por mesh no convierte valores sincronizados en secretos independientes.

**Next stage:** el prototipo estático de Stage 4 se implementó en la continuación siguiente. No extender sus operaciones a meshes skinned sin resolver el contrato de deformación.

## Stage 4 — Polymorphic Mesh Codec prototype

**Status:** prototipo estático opt-in implementado y probado; no habilitado en la preparación productiva.

**Changes:** [GuardStaticCodec.cs](../Package/Assets/LinuxAvatarGuard/Editor/GuardStaticCodec.cs) genera ocho instrucciones de [GuardCodecInstruction.cs](../Package/Assets/LinuxAvatarGuard/Editor/GuardCodecInstruction.cs), incluidas dos operaciones no lineales acotadas y dos offsets keyed en tres ejes. Seed de 32 bytes, HMAC counter stream con dominios versionados para programa/payload, IR schema 2 y hash canónico. La fuente queda vinculada al plan mediante una huella de geometría, atributos y topología; una mutación invalida encoding y emisión. No se añade decoder CPU de runtime.

La API exige Linux Editor, Vulkan y Built-in. Rechaza datos de skinning, todos los blendshapes, UV7/UV8 ocupados, valores no finitos y coordenadas fuera de ±16 unidades por componente; además cancela si el round-trip del encoder supera 10⁻⁵ unidades. `RequireStaticRenderer` rechaza SkinnedMeshRenderer. El builder y el asistente siguen usando exclusivamente legacy.

**Tests:** 44 comprobaciones de [LAGStaticPrototypeValidation.cs](../Tests/LAGStaticPrototypeValidation.cs). 16 seeds de fixture producen 16 programas y 16 cuerpos de decoder distintos; error máximo del intérprete independiente ≈ 1,93 × 10⁻⁶ unidades, incluyendo escalas 0,001 / 0,05 / 20. RMS al aplicar un programa de otro seed ≈ 1,1182; RMS al aplicar la fórmula legacy ≈ 1,0019. En tres ángulos, diferencia visual desbloqueada 0; las imágenes con valores ausentes/incorrectos difieren >0,01. Seis shaders generados sin errores y bundle Windows64 de fixture construido.

La suite completa anterior conserva 18 + 26 + 14 + 25 = 83 comprobaciones correctas, además de sus 13 shaders/bundle. Las seis pruebas OSC siguen correctas. El código fuente compila y el prototipo funciona sin SDK en Vulkan; una ejecución OpenGLCore confirma el rechazo temprano. Captura RenderDoc del prototipo en Unity: dos draws, geometría PostVS corregida y cuatro valores de runtime observables; error máximo frente a proyección original ≈ 1,33 × 10⁻⁷. Detalle en [STATIC_POLYMORPHIC_PROTOTYPE.md](STATIC_POLYMORPHIC_PROTOTYPE.md).

**Known issues:** integración de avatar/FX/OSC, carriers dinámicos, builds completos procesados por SDK, benchmark y matriz animada pendientes. La persistencia/derivación por binding se incorporó en Stage 5; la evaluación estática de 100 fixtures se registra en Stage 6. La diversidad se mide sobre 16 programas/encodings, no 16 uploads. El intérprete de la prueba reconstruye todos al leer la IR; el polimorfismo no demuestra impedir un extractor adaptativo. La observabilidad PostVS permanece.

**Next stage:** Stage 5, identidad estable y derivación por binding/mesh con vectores verificables y metadata privada versionada. Mantener el prototipo fuera de la ruta predeterminada hasta resolver integración contextual y skinning.

## Stage 5 — Per-Mesh Key Derivation

**Status:** implementado para el prototipo estático, separado de preparación/perfiles.

**Changes:** identidad por GUID/localFileID/contenido/jerarquía/componente, HKDF-SHA256 con dominios de programa/payload por binding y contexto privado versionado. La persistencia Linux usa 0700/0600, descriptores sin seguir symlinks, publicación atómica sin sobrescritura y validación de dueño/tipo/links/permisos. [PER_MESH_DERIVATION.md](PER_MESH_DERIVATION.md) detalla contratos y límites.

**Tests:** 53 comprobaciones correctas: vectores RFC 5869 A.1/A.2/A.3, dominios, identidades, mutaciones, reproducción exacta desde disco e IO adversarial. Las 44 pruebas de Stage 4 siguen correctas. Solo fixtures propias en Unity Vulkan; ningún análisis de VRChat.

**Known issues:** cuatro valores de runtime compartidos/observables; no secretos independientes por renderer en OSC. Identidad estática, dependiente de fuente/meta/jerarquía conservadas. Cadenas privadas no tienen borrado garantizado; no sandbox contra el mismo usuario. No integración productiva/FX/SDK ni skinning.

**Next stage:** Stage 6, diversidad de 100 builds sintéticos completos y extractor adaptativo. El gate de avatares procesados por SDK continúa pendiente.

## Stage 6 — Codec diversity tests

**Status:** completado para 100 builds completos de fixture estática; 100 avatares procesados por SDK y matriz animada pendientes.

**Changes:** dos bindings por build, shaders/materiales/prefab/manifest y contexto privado separados; empaquetado explícito de providers UsePass; reapertura nativa y extractor HLSL independiente. La prueba detectó una cancelación de offsets que eliminaba la dependencia de clave. Plan/Validate/Emit ahora la rechazan y Encode cancela si el RMS sin clave no supera 10⁻⁵ antes de crear la Mesh. El caso rechazado se conserva como regresión/evidencia.

**Tests:** 2.714 comprobaciones correctas. 100 bundles Linux reabiertos, cuatro muestras Windows64, 200 programas/decoder bodies/constantes/meshes únicos, 197 secuencias y un layout fijo. 99 ataques cross-build y 100 cross-mesh fallan sin adaptación. El extractor adaptativo recupera 200/200, error máximo ≈ 1,79 × 10⁻⁷. Doce vistas de cuatro bundles: diferencia desbloqueada máxima ≈ 7,98 × 10⁻⁸; diferencia bloqueada mínima ≈ 0,05342. Las 53 + 44 + 83 comprobaciones de contexto/prototipo/legacy pasan; ejecución final limpia, salida 0. [CODEC_DIVERSITY_RESULTS.md](CODEC_DIVERSITY_RESULTS.md) detalla evidencia, alcance, variantes y tiempos.

**Known issues:** no resistencia demostrada a extractor adaptativo ni PostVS; claves observables y compartidas. El extractor usa HLSL fuente y valores de fixture proporcionados; no decompila el bytecode del bundle ni recupera claves desconocidas. Subconjunto de variantes unlit/mono; no certifica iluminación/stereo/animaciones/runtime ni resultado final del SDK. Tiempo con cache y generación original no medida; no benchmark.

**Next stage:** [Stage 7 — asignación conservadora](DYNAMIC_ATTRIBUTE_ALLOCATION.md), entregada a continuación para fixtures estáticas. Materiales/clips/contexto productivos siguen pendientes.

## Stage 7 — Dynamic Attribute Allocation

**Status:** completado para el contrato estático revisado; integración de avatares/animación pendiente.

**Changes:** allocator de dos carriers Float32 × 2 entre UV de índice 4–7, reservas por atributos existentes e ID Mask en todos los materiales/submeshes, contrato positivo de Unity/lilToon/fuentes/keywords y validación de mutaciones. Se rechazan animación/scripts, MPB, vertex streams adicionales/enlighten y static batching; el shader del prototipo desactiva dynamic batching. Propósito HKDF separado, IR schema 3 y respaldo privado schema 2, con reproducción fija explícita de schema 1. Factory por ruta de asset; asistente y codec legacy conservados. [DYNAMIC_ATTRIBUTE_ALLOCATION.md](DYNAMIC_ATTRIBUTE_ALLOCATION.md) detalla API, versiones y límites.

**Tests:** 683 comprobaciones correctas. Matriz de 16 ocupaciones × 3 dimensiones, consumidores ID Mask 0–8, mutaciones, contextos desconocidos y persistencia adversarial. 100 contextos admitidos producen 12 layouts ordenados; un candidato sin dependencia de clave se rechaza aparte. Seis bundles Linux con los seis pares y 10 variantes, 78 shaders/provider assets cargados con soporte Vulkan. 54 imágenes/18 vistas: diferencia desbloqueada máxima 0, bloqueada mínima ≈ 0,00834097. Extractor adaptativo recupera cada par, error máximo ≈ 1,2288 × 10⁻⁷. Regresión de 53 + 44 + 83 comprobaciones y compilación/reproducción privada sin SDK ni lilToon correctas.

**Known issues:** lectura del shader y valores runtime proporcionados permiten reconstrucción adaptativa; PostVS sigue observable. Contrato fijado a Unity 2022.3.22f1/lilToon 2.3.4/Built-in/Linux/Vulkan, sin skinning, clips ni shaders custom. Renders unlit/mono, filtro de variantes solo de fixture, no certificación de todos los pases/lighting/stereo ni resultado final del SDK. Colores, tangentes, lookup y protección de texturas pendientes.

**Next stage:** Stage 8, Generated Shader Forge contextual por renderer/material slot/clips, cambios de material y propiedades animadas, providers/configuración y gates verificables antes de integración productiva.

## Stage 8 — Generated ShaderForge

**Status:** implementado y validado como API de investigación rígida/genérica. El asistente conserva legacy; integración de avatares skinned/SDK pendiente.

**Changes:** [GuardShaderForge.cs](../Package/Assets/LinuxAvatarGuard/Editor/GuardShaderForge.cs) genera una copia independiente con familias de shaders por binding, materiales por renderer/slot y remapeo contextual de clips/controllers/BlendTrees/overrides. Nombres opacos, providers explícitos, licencia preservada, comprobación de shaders y rollback de carpeta nueva; registro de planes por lote. [GuardAnimationContext.cs](../Package/Assets/LinuxAvatarGuard/Editor/GuardAnimationContext.cs) analiza identidades/archivos, swaps y propiedades animadas, reserva intervalos de ID Mask durante mezclas y rechaza grafos/bindings fuera del contrato. Override controllers reconstruidos mediante API pública para actualizar bindings nativos. Política contextual 2 y schema privado 3; reproducción de schemas 1/2 y política estática 1 conservada. Contrato/API: [GENERATED_SHADER_FORGE.md](GENERATED_SHADER_FORGE.md).

**Tests:** 182 comprobaciones correctas de [LAGShaderForgeValidation.cs](../Tests/LAGShaderForgeValidation.cs). Dos contextos, cuatro bindings, 16 shaders/provider assets, dos bundles Linux Vulkan cargados y dos Windows64 compilados. Clips/slots compartidos, BlendTrees 1D anidados, override con movimientos invertidos, persistencia/downgrade, mutaciones y fallo tardío durante importación. El Animator se inicializa, avanza y produce la pose analítica esperada; los clips nativos sin modificaciones reproducen el resultado bloqueado de los templates. 64 imágenes: 20 comparaciones original/desbloqueado/bloqueado y dos comparaciones adicionales de Animator. Diferencia desbloqueada máxima **0**, bloqueada mínima **≈ 0,04395888**. Originales y fuentes lilToon byte idénticos. Cross-renderer decoder RMS mínimo **≈ 0,7727544**; extractor adaptativo recupera los cuatro bindings, error máximo **≈ 7,0021 × 10⁻⁸**.

**Regressions:** 684 comprobaciones Stage 7 (una nueva verifica schema 2 histórico) + 53 de binding + 44 del prototipo + 83 de implementación = **864**, todas correctas. Backend adicional: 13 shaders en bundle Windows64, separado del conteo de pruebas. Importación/API y reproducción privada fija schema 3 correctas en proyecto sin SDK ni lilToon. Total de comprobaciones contadas de esta iteración: **1.046**. Evidencias locales: `evidence/shader-forge/validation.json`, PNG/bundles/`sdk-free.json`; logs `evidence/shader-forge-validation-final.log`, `shader-forge-regression-final.log`, `shader-forge-sdk-free-final.log`.

**Known issues:** mallas skinned/blendshapes, SDK, sincronización/OSC/FX productivos y performance pendientes. El contrato incluye 2D simple, pero la primera matriz visual solo ejecuta 1D. Renders unlit/mono con filtro de variantes de fixture; Windows se compila sin afirmar ejecución D3D. Las claves solo se añaden a clips/controllers temporales de prueba con templates editables y materiales del bundle; se destruyen y nunca se guardan. Unity elimina metadata de edición de curvas en bundles, y el API no intenta modificarlos como assets de autoría. Se conservan warnings de objetos BlendTree editor-only convertidos por el builder y JobTempAlloc al cierre; no certifican el coste ni descartan problemas de rendimiento. HLSL más valores runtime permiten extracción adaptativa y PostVS sigue observable.

**Next stage:** Stage 9, benchmark original/legacy/contextual en fixtures propias y hardware fijo: CPU/GPU, VRAM, draw calls, variantes, tamaños/tiempos de build y presupuestos. Mantener el gate de skinning hasta una validación específica; ninguna captura/análisis se realizará en VRChat.

## Stage 9 — Performance benchmark

**Status:** primer benchmark rígido completado en player propio Linux/Vulkan sin SDK. Rendimiento de avatares skinned/procesados por SDK pendiente.

**Changes:** [LAGPerformanceValidation.cs](../Tests/LAGPerformanceValidation.cs) prepara tres bundles equivalentes de geometría propia; originales/fuentes lilToon byte idénticos y materiales serializados con valores de desbloqueo cero. [Tests/Performance](../Tests/Performance) aporta player Release, medición de frames/counters, lectura DRM solo del PID propio, rondas alternadas y captura/análisis RenderDoc separados. Los informes fijan hashes de bundles/assemblies y umbrales provisionales del hardware medido. Contrato/método/resultados: [PERFORMANCE_BENCHMARK.md](PERFORMANCE_BENCHMARK.md).

**Tests:** 30 comprobaciones de preparación; 36 ejecuciones aisladas y 24 rondas pareadas (72 bloques). **403.103 frames** finales, diez PNG de validación y cinco PNG de captura, tres capturas Vulkan. GPU/CPU, FPS, buffers/DRM/RSS, draw calls, variantes, generación y tamaño/build de bundles medidos. Diferencia visual desbloqueada **0** en ambos codecs y cantidades; medianas de draws/triángulos iguales. Control ShaderForge/original con dieciséis copias: incremento GPU pareado **≈ 0,02862 ms**, intervalo exploratorio **0,01056–0,03846 ms**; una copia no resuelve diferencia frente al ruido. Instrucciones vertex GEN reportadas por Intel: original **3.392**, legacy **3.412**, contextual **3.442/3.444**. Fragment shaders idénticos por SHA-256.

**Known issues:** en la fixture con mesh original compartida, contextual utiliza **2,54×** los buffers de geometría y **2,53×** el bundle; preparar/importar también cuesta más. Native GPU timing instrumentado, relojes/escritorio sin fijar, unlit/mono/opaque y filtro de variantes de investigación. Memoria DRM es contabilidad por clientes con posibles buffers compartidos; no se presenta como VRAM física única. Los controles alternados mantienen las tres variantes residentes y no se usan para atribuir memoria. Se conservaron todos los valores transitorios de counters. La importación inicial del player tuvo compilación transitoria resuelta; un fallo de instrumentación al comprobar cada frame y una excepción al descargar bundles durante cierre fueron corregidos en los helpers. Las ejecuciones y compilación finales son correctas, sin excepciones de player ni errores C#/shader. No se analiza VRChat; skinning, Animator bajo carga, SDK y matriz gráfica ampliada siguen pendientes.

**Next stage:** Stage 10, investigación TextureGuard: sampling/import/mipmaps, calidad, exposición GPU y coste antes de un piloto. Mantener el gate de skinning y usar los presupuestos/métodos de Stage 9 como referencia.

## Stage 10 — TextureGuard research

**Status:** investigación y contrato del primer piloto entregados.

**Changes:** [TEXTURE_GUARD_RESEARCH.md](TEXTURE_GUARD_RESEARCH.md) contrasta fuentes lilToon 2.3.4 y documentación primaria de Unity/VRChat. Identifica lectura de albedo/normal/emisión/máscaras, sampler compartido y providers UsePass, transferencia sRGB, importadores, mipmaps, filtrado y compresión. Propone programa tipado reproducible, payload lineal y lectura por texel en coordenadas originales, sin cambiar UV0 ni samplers compartidos.

**Tests:** inspección de los includes/pases revisados por digest y definición previa de criterios medibles para Stage 11: extracción independiente, matriz Gamma/Linear y Point/Bilinear/Repeat/Clamp, calidad visual, mutaciones/rollback, dependencias del bundle y ausencia de claves serializadas. Este apartado de investigación no presenta esas pruebas como ya terminadas.

**Known issues:** XOR/tile permutation son ofuscación y se pueden revertir con shader más parámetros runtime; la observabilidad GPU permanece. Normales/HDR/alfa, compresión, streaming, trilinear/anisotropía y mips requieren codecs y validación propios. El piloto inicial acepta fuentes opacas RGBA32 de un solo mip; no es una conversión automática de texturas de avatar ni una certificación del SDK/cliente.

**Next stage:** Stage 11 se implementó y validó en la continuación siguiente. Stage 12 tratará integración contextual y ampliación de formatos; el asistente productivo conserva cero texturas protegidas.

## Stage 11 — TextureGuard prototype

**Status:** piloto de albedo opaco implementado y validado como API de investigación. Integración de avatares/perfiles pendiente.

**Changes:** [GuardTextureCodec.cs](../Package/Assets/LinuxAvatarGuard/Editor/GuardTextureCodec.cs) aporta contrato/plan/programa inmutables con 16 tiles, ocho orientaciones, seis órdenes RGB y XOR con salts/valores runtime. Derivación HMAC-SHA256 separada por fuente/scope. Payload RGBA32 lineal no legible; decoder GPU por texel conserva vecindarios Repeat/Clamp y filtra después de invertir los bytes. Flag de datos sRGB independiente de la vista GPU. [GuardTextureForge.cs](../Package/Assets/LinuxAvatarGuard/Editor/GuardTextureForge.cs) genera material/textura/providers independientes, licencia y manifest ResearchOnly; mutaciones tardías y claves serializadas no cero cancelan y retiran la salida nueva. Documentación/API: [TEXTURE_GUARD_PROTOTYPE.md](TEXTURE_GUARD_PROTOTYPE.md).

**Tests:** [LAGTextureValidation.cs](../Tests/LAGTextureValidation.cs), **293 comprobaciones por espacio de color (586)**, **128 comparaciones visuales y 518 PNG**. Dieciséis combinaciones por modo: sRGB/linear × Point/Bilinear × Repeat/Clamp por cada eje; tres escalas/offsets con UV negativas y fronteras, más reapertura nativa de cada material/prefab. Un bundle Linux/Vulkan y un Windows64 por modo; 32 shaders por modo (16 fachadas y 16 providers). Recuperación independiente de **32 programas por modo**, todos byte idénticos; claves ausentes/incorrectas y programas ajenos fallan. Importador PNG/source assets/includes preservados, planes invalidados por mutaciones, rollback tardío y rechazo de claves no cero tras importar.

| Medición final | Gamma | Linear |
| --- | ---: | ---: |
| Error máximo de recuperación de bytes | 0 | 0 |
| Diferencia media máxima de imagen | 0,00011443 | 0,00036424 |
| Diferencia máxima de canal RGBA8 | ≈1/255 | ≈2/255 |
| Diferencia media mínima sin clave | 0,27759 | 0,20235 |
| Diferencia media mínima con clave errónea | 0,27726 | 0,20197 |
| Bytes de payload GPU: 16×64×64×4 | 262.144 | 262.144 |
| Bundle Linux comprimido, bytes | 1.044.881 | 1.179.970 |
| Bundle Windows64 comprimido, bytes | 336.546 | 370.133 |

**Regressions:** 182 ShaderForge + 684 atributos + 53 binding + 44 prototipo + 83 fundación = **1.046 comprobaciones** correctas; 13 shaders/bundle Windows adicionales. Seis tests OSC correctos. API compilada/ejecutada sin SDK ni lilToon en Vulkan con fuentes copiadas. Las cuatro ejecuciones finales terminan con código 0, sin errores C#/shader ni excepciones. Evidencia en `evidence/texture-guard/{gamma,linear}/validation.json`, PNG/bundles/resources, `sdk-free.json`, `final-runs.json`; logs `evidence/texture-validation-{gamma,linear}-final.log`, `texture-regression-final.log`, `texture-sdk-free-final.log`.

**Known issues:** el extractor específico interpreta HLSL emitido más valores runtime de fixture y recupera todos los programas; no hay resistencia demostrada a extracción adaptativa/GPU ni recuperación de claves desconocidas. Solo albedo opaco RGBA32 sin mips/compresión, en mono/unlit con filtro de variantes de test. Falta matriz de iluminación/stereo/animación, máscaras/emisión/normales, sampling avanzado y rendimiento en player. Las fuentes y el cliente Carukia/VRChat no se analizan. Los logs del proyecto con SDK conservan mensajes de licencia recuperados y avisos Persistent/TransformAccessArray al terminar; la regresión también conserva `m_TaskQueue.empty()`/JobTempAlloc de iteraciones anteriores. Causa no aislada: no se certifica estabilidad ni memoria/rendimiento del Editor. El primer build detectó inserción junto a un include comentado; se corrigió y se añadió regresión. La primera enumeración LoadAllAssets fue incompleta; ahora se incluyen salidas explícitas y se comprueba el grafo/bindings nativos.

**Next stage:** Stage 12, integración contextual por binding/slot, respaldo privado y remapeo de materiales/clips, seguida de ampliaciones de formatos/mipmaps con presupuesto de calidad/coste. Mantener fuera del asistente hasta validar el artefacto procesado y las funciones de avatar admitidas. El manifiesto productivo sigue reportando cero texturas protegidas.

## Stage 12 — TextureGuard integration

**Status:** integración contextual de investigación implementada para mallas rígidas/Animator genérico. La ruta productiva, skinning y artefacto procesado por SDK continúan pendientes; no se añade una opción al asistente.

**Changes:** [GuardTextureBinding.cs](../Package/Assets/LinuxAvatarGuard/Editor/GuardTextureBinding.cs) identifica renderer/slot/material/albedo y su fuente/contexto. `GuardBuildContext` deriva seeds mediante HKDF con dominio de textura y guarda registros privados **schema 4**, conservando schemas 1/2/3 y las claves históricas de malla. `GuardShaderForge.PrepareWithTextures` combina decoders, copia payloads/materiales y remapea clips/controllers/BlendTrees/overrides por ocurrencia. Registro por lote de mallas/texturas con rollback conjunto, rechazo de fuentes originales en dependencias finales, alias de albedo sin proteger, mutaciones tardías y claves guardadas distintas de cero. RGB24 se convierte solamente en el payload nuevo; ST animado opt-in preservado. Claves contextualizadas admiten bytes individuales cero y deben coincidir con el contexto. Contrato/API: [TEXTURE_GUARD_INTEGRATION.md](TEXTURE_GUARD_INTEGRATION.md).

**Tests:** [LAGTextureIntegrationValidation.cs](../Tests/LAGTextureIntegrationValidation.cs), **309 Gamma + 309 Linear = 618 comprobaciones**, **44 comparaciones nativas y 168 PNG**. Compara bundles Vulkan reabiertos con las fuentes, material swaps, cuatro canales de ST, los tres canales de color, dos renderers separados y evaluación real de nested BlendTrees/overrides. También comprueba reproducción byte idéntica de programas/payloads, esquemas privados adversariales, lanes runtime individuales cero y transacciones con cuatro mutaciones tardías. **32 payloads** recuperados byte idénticos por el extractor independiente; esto confirma su reversibilidad. Cuatro bundles Linux/Vulkan reabiertos y cuatro Windows64 compilados en total; la compilación Windows se comprueba separadamente de la ejecución Vulkan. Los archivos/importadores/fuentes lilToon originales permanecieron byte idénticos. Ambas ejecuciones terminaron con código 0, sin errores C#/shader ni excepciones de la suite.

| Matriz de integración | Gamma | Linear |
| --- | ---: | ---: |
| Mayor error medio desbloqueado, canales 0–1 | 0,00002673 | 0,00006607 |
| Mayor diferencia de canal RGBA8 | ≈1/255 | ≈2/255 |
| Menor diferencia media sin claves | 0,06705 | 0,06616 |
| Menor diferencia media con claves incorrectas | 0,06621 | 0,05599 |
| Payloads / shaders generados | 16 / 32 | 16 / 32 |
| Bytes de payload / píxeles fuente | 262.144 / 57.344 | 262.144 / 57.344 |
| Bytes de los dos bundles Linux | 2.506.485 | 2.221.126 |
| Bytes de los dos bundles Windows | 560.674 | 566.159 |
| Generación por fixture, segundos | 30,57 / 31,79 | 28,44 / 28,52 |
| Build Linux de las dos fixtures, segundos | 120,88 | 113,45 |

El gate de error medio es 0,0015; los estados sin claves/incorrectos deben diferir más de 0,006. Tiempos de generación/build son observaciones únicas con caché existente y no percentiles de builds limpios. El payload medido sigue siendo de un solo mip.

**Performance:** **281.108 frames**, 18 ejecuciones aisladas y 24 rondas pareadas/72 bloques en un player propio SDK-free Release, Vulkan, Gamma, mono/unlit, Intel Arc A750 y 1920×1080, sin VSync. Dos renderers/dos slots por copia; medianas de draws y triángulos iguales: 4/384 para una copia y 64/6.144 para dieciséis. Los bundles y valores demo se congelaron antes de las correcciones de canales de los clips de la suite visual; el benchmark elimina Animator en todas las variantes y no ejecuta esos clips.

| Copias | Original GPU, ms | MeshGuard GPU, ms | Mesh+Texture GPU, ms | Incremento Texture−Mesh, ms | IC exploratorio por ronda |
| ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 0,41706 | 0,43135 | 0,69526 | 0,26531 | 0,24831–0,27086 |
| 16 | 0,71242 | 0,74435 | 1,09849 | 0,34286 | 0,33966–0,35077 |

Los FPS derivados de la mediana del intervalo de frame son ≈1.906/1.840/1.199 con una copia y ≈1.166/1.105/763 con dieciséis, para original/mesh/texture. Son valores de esta aplicación propia, no predicciones de VRChat ni un presupuesto de avatares completos. Las diferencias GPU/CPU y los datos por ronda/repetición se conservan en JSON/CSV; no se usan frames correlacionados como repeticiones independientes.

El error adicional **Texture frente a Mesh** en el player alcanza **1/255**, con medias ≈0,00001009/0,00001106 para 1/16 copias. Una diferencia aislada de silueta llega a 43/255 frente al original en tres canales de la comparación Mesh de una copia; ya aparece en el control sin TextureGuard (36 píxeles no idénticos, error medio ≈6,87×10⁻⁸). No se presenta el render como bit idéntico.

Por proceso, el DRM local total/residente sube ≈**14,03 MiB** con la variante texturada y RSS ≈**5,6–6,1 MiB** frente a MeshGuard. Son asignaciones completas del proceso/driver, incluyendo shaders y pipelines; no VRAM exclusiva de texels. Bundles control: original **158.351**, mesh **191.992**, mesh+texture **713.082 bytes**. Point realiza una lectura de texel y Bilinear cuatro. La diversidad por slot/material tiene coste de memoria/shaders y no se declara gratuita.

**Regressions:** **184 ShaderForge + 684 atributos + 53 binding + 44 prototipo + 83 fundación = 1.048**, con 13 shaders Windows adicionales. El piloto standalone conserva **293 Gamma + 293 Linear = 586** comprobaciones y sus 518 PNG. Seis tests OSC de loopback correctos. [LAGTextureIntegrationImportSmoke.cs](../Tests/LAGTextureIntegrationImportSmoke.cs) compila/ejecuta APIs sin SDK ni lilToon, reproduce mallas privadas schemas 1/2/3/4 y valida la textura standalone; no ejecuta el forge contextual sin fuentes de lilToon.

**Known issues:** solo albedo opaco y un mip; faltan formatos/features adicionales, mipmaps, compresión y análisis de instrucciones del fragment shader. No se certifican skinning, blendshapes, SDK, iluminación/stereo ni runtime D3D/cliente. El extractor HLSL+parámetros sigue recuperando todos los texels; no hay resistencia demostrada a extracción adaptativa/GPU. Persisten los avisos de cierre del Editor con SDK (Persistent/TransformAccessArray y, en algunas ejecuciones, `m_TaskQueue.empty()`/JobTempAlloc); no se atribuyen a este cambio ni se declara estabilidad/memoria limpia. Las primeras ejecuciones fallidas por comparación de tags, constructor interno del harness, lectura del provider y canales parciales de la fixture se corrigen y no cuentan como resultados finales.

**Evidence:** `evidence/texture-integration/{gamma,linear}/`, `benchmark/summary.json`/CSV/DRM y `process-resources.json`, `sdk-free.json`, `final-runs.json` y `evidence-manifest.json`; comparativas `comparison.png`, `performance.png/.svg`, `quality.png` y logs de ejecución. Evidencia ignorada por Git; únicamente contenido propio y procesos de Unity/player propio. VRChat permaneció cerrado durante el benchmark y no se analizó/capturó su proceso. No se publica un unitypackage nuevo en esta etapa.

**Next stage:** Stage 13, MetadataGuard, conservando rutas/nombres/parámetros funcionales. Las ampliaciones de TextureGuard (pirámides por mip, minificación y formatos) y la validación de avatar/SDK quedan como requisitos abiertos antes de habilitar el asistente.

## Stage 12 — Continuación: mipmaps y skinning

**Status:** implementados y validados como APIs opt-in de investigación. [MIP_SKIN_RESEARCH.md](MIP_SKIN_RESEARCH.md) documenta la matemática, el contrato y la reproducción. El asistente y los binarios publicados conservan su ruta anterior; no se certifica un avatar Humanoid/SDK.

**Changes:** `TextureGuardCodecV2` codifica cada nivel original por separado, conserva la pirámide sin regenerarla y usa LOD hardware con Point/Bilinear/Trilinear. `SkinnedLinearCodecV1` compensa posición y deltas de normal de blendshapes antes de skinning y resta el carrier de normal sin normalizar en el hook. No modifica bindposes, pesos, normales ni tangentes originales. Skinning reserva UV4/5 y exige UV6/7 libres. ShaderForge v3 combina ambos codecs en una copia mixta por binding/slot/material, audita mallas completas después de importar y restaura bounds estáticos/pesos iniciales de morph. Schema 5 conserva lectura 1–4 y rechaza downgrade de registros nuevos.

**Tests:**

| Suite / entorno | Resultado |
| --- | --- |
| Mips, Gamma + Linear | 377 + 377 checks; 144 comparaciones; 168 recuperaciones nativas byte exactas; 576 PNG. |
| Superficie animada, Gamma + Linear | 108 + 111 checks; 24 comparaciones; 96 PNG; errores/control negativo correctos. |
| Players Release propios, GPU Skinning on/off × Gamma/Linear | Cuatro ejecuciones correctas, 74 checks/12 comparaciones/48 PNG cada una. |
| RenderDoc propio, pose con morphs activos | 18 dispatches de blendshape + 3 esqueléticos; 9 draws con posiciones PostVS leídas. |
| Compatibilidad V1/schema 4 | 311 checks de integración Gamma; originales intactos. |
| Regresiones previas + auditoría standalone V2 | 1341 + 3 checks, shaders/bundles; seis tests OSC. |
| Importación sin SDK ni lilToon | APIs actuales compiladas; schemas 1/2/3/4/5 y texturas V1/V2 ejecutados. |

Mayor diferencia RGB media de texturas: **0,000126392**; mayor diferencia media en los players: **0,0000595591**. La referencia CPU de posición tiene error máximo **1,87638×10⁻⁷ unidades**. `BakeMesh` siempre es referencia CPU: la evidencia de compute GPU proviene de la captura propia. Los originales/meta de fixtures permanecen byte idénticos. No se descargaron herramientas adicionales ni se analizó VRChat; se utilizó RenderDoc ya autorizado.

La primera regresión completó sus aserciones y luego esperó trabajo asíncrono de paquetes hasta 300 segundos. Se añadió salida explícita del runner después de los reportes; los avisos conocidos de cierre del Editor/JobTempAlloc se conservan y no se certifica estabilidad o ausencia de fugas. Los players funcionales terminan con código 0; no son un benchmark de rendimiento.

**Known issues:** codec skinned lineal y extractor adaptativo capaces de recuperación; PostVS observable. Fixtures con dos influencias y actualización offscreen activa; paletas de tres/cuatro influencias, rendering/culling offscreen deshabilitado, bordes del frustum, iluminación/stereo completos, compresión/streaming/calidad dinámica, Humanoid, artefacto SDK, wizard y presupuesto de rendimiento de V2 pendientes. La capa nueva resuelve compatibilidad demostrada; no elimina ripping por GPU.

**Evidence:** archivos locales ignorados en `evidence/mip-skin/`: informes Gamma/Linear, cuatro players, captura propia, `RESULTS.json`, `skinning-comparison.png` y `mipmaps-comparison.png`. Evidencia anterior preservada en `prior-validation/` antes de repetir suites con rutas fijas. Checkpoint y hashes permiten continuar desde esta entrega.

**Next stage:** ampliar las pruebas de avatar/culling y medir esta ruta; preparar integración y exportación de una nueva release únicamente después de sus gates. Las demás etapas 13–25 del roadmap continúan según la tabla.

## Stage 13 — MetadataGuard

**Status:** implementado como capa opt-in con contrato conservador. [METADATA_GUARD.md](METADATA_GUARD.md) especifica nombres admitidos, exclusiones, privacidad y reproducción. La ruta predeterminada del asistente y los binarios 0.2.1 conservan su comportamiento.

**Changes:** `GuardMetadataGuard` copia grafos de autoría y emite aliases HMAC por build con un dominio independiente. El modo conservador mantiene jerarquía, morphs, parámetros, estados/capas y campos externos. La ruta genérica explícita remapea objetos, morphs, declaraciones/condiciones/campos del Animator, BlendTrees/direct weights, parámetros animados, AvatarMasks y overrides nativos; conserva índices/frames/deltas de morph, pesos de huesos y bounds iniciales. Los subassets del Editor se revisan además de las dependencias de runtime. Integraciones/componentes no revisados, callbacks y selecciones reservadas/ambiguas se rechazan. Se limpian exclusivamente posiciones del grafo del Editor. Fuentes, datos en memoria y prefab persistido se auditan frente a mutaciones tardías.

`GuardBuilder.BuildWithMetadata`/`GuardSetup.PrepareWithMetadata` integran labels/archivos sobre la copia legacy. Manifest schema 1 añade campos opcionales de versión/política y contadores separados de labels/archivos; los formatos de claves y derivaciones anteriores se conservan. El mapa privado se guarda fuera de Assets/Packages, se valida contra BuildID, se respalda junto a key.json y se retira con las claves/assets si la preparación falla. Directorio/archivo privados: 0700/0600. El reconocimiento de copias admite el mapa hermano de un respaldo existente.

**Tests:** **90 Gamma + 90 Linear = 180 comprobaciones**, 12 comparaciones de bundles Linux/Vulkan reabiertos, **error RGB medio 0** y 24 PNG. `LAGMetadataValidation` revisa poses y una transición real de Animator; nombres reservados, fuentes/meta byte idénticos, escena de trabajo limpia preservada mediante previews, reproducción/diversidad, máscaras, controllers sincronizados/anidados, morphs con varios frames, material swaps, overrides, callbacks no revisados, rutas privadas/symlinks y rollback. La fixture SDK pasa **21 comprobaciones** de visemes/ojos/expresiones/PhysBones/Contacts y preparación opt-in con fallos en tres checkpoints. Importación sin SDK/lilToon correcta. Regresiones: **83 comprobaciones** (18 codec + 26 funcional + 14 baseline + 25 preparación), 13 shaders y bundle Windows64 compilados; seis tests OSC. Runners finales con código 0; avisos conocidos del SDK/JobTempAlloc y de main asset names durante importación conservados. Evidencia local en `evidence/metadata-guard/`, sin acceso a VRChat ni descargas nuevas.

**Known issues:** ofuscar nombres no cifra datos ni elimina topología, IDs, parámetros externos o PostVS. No se mide un aumento del tiempo de extracción. Los nombres funcionales SDK permanecen estables; los renombrados internos se limitan al contrato genérico explícito. Texturas/shaders/Avatars y tipos ajenos pueden seguir compartidos en una copia independiente de metadatos. Preparación con SDK no equivale a validación de su artefacto procesado, upload o cliente. No se certifica compatibilidad completa Humanoid, MA/VRCFury, comportamiento de scripts externos ni estabilidad/fugas del Editor. No se publica un unitypackage en esta etapa.

**Next stage:** Stage 14, investigación de fingerprints: amenazas, transformaciones de extracción, controles y métricas de falsos positivos/negativos antes de crear marcas forenses.

## Stage 14 — FingerprintGuard research

**Status:** investigación completada, contexto privado implementado y candidatos evaluados por CPU. [FINGERPRINT_RESEARCH.md](FINGERPRINT_RESEARCH.md) recoge fuentes primarias, decisiones, amenazas, evidencia y gates; [FINGERPRINT_PROTOCOL_V1.json](FINGERPRINT_PROTOCOL_V1.json) fija el contrato de investigación. Crear el contexto no marca un avatar: su estado inicial es `IdentityOnly` y el asistente conserva la ruta publicada.

**Changes:** `GuardFingerprintContext` genera identidad y carrier seed independientes de 256 bits, separadas de OSC/codec, y deriva codewords de 128 bits por binding/mesh/texture con HMAC-SHA256 y dominios versionados. Restauración estricta de DTO schema 1, arrays copiadas/limpiadas y resumen público sin secretos ni contadores de protección falsos. No tiene llamadas Unity/gráficos ni IO automático. El harness sintético por CPU añade baseline por índice, QIM radial/DCT, controles negativos de la misma fuente/otros builds, abstención por cobertura y comparación de dos contribuyentes promediados. Sus snapshots se guardan por descriptor en espacio local privado externo a Unity/Git con 0700/0600 y sin sobrescritura.

**Checks:** host C# standalone compilado/ejecutado con las herramientas ya instaladas: **34 comprobaciones**; dos vectores HMAC contrastados con Python. Store de investigación: **17 tests** de permisos, enlaces, FIFO, límites, rutas y colisiones. Doce trials de tres familias propias: **132 observaciones geométricas + 156 de textura**, además de **1.092 + 1.284 controles negativos**, cero coincidencias al umbral exploratorio. El marcador por índice falla 12/12 tras reorder. El radial conserva reorder/similitud en 8/12 y se abstiene en cuatro por falta de cobertura; ruido mayor, escala no uniforme y recorte/deformación fallan. DCT detecta 12/12 en PNG, JPEG95, resize/color suaves, pero solo 1/12 con JPEG75 y 0/12 con recorte. Promediar dos copias reconoce ambos contribuyentes en 8/12 geométricos y 7/12 de textura; no habilita atribución exclusiva. Desplazamiento máximo relativo a la diagonal ≈4,317×10⁻⁶; PSNR RGB8 mínimo 54,46 dB. Fuentes intactas y reproducción numérica idéntica desde snapshot privado. No se inició Unity, no se accedió a VRChat/GPU ni se descargaron herramientas.

**Known issues:** nube de posiciones sin topología/skin y tres imágenes procedurales; casos correlacionados y umbral sin calibración poblacional. Cero coincidencias no prueba una FPR universal. Score no equivale a probabilidad de autoría y se conserva `confidence=null`. Pendientes FBX/welding, correspondencias de pose, albedos/compresión/mips reales, comparación Unity/lilToon, SDK, IO/rollback productivo, verificador/UI y nuevo unitypackage. La marca no impide extracción GPU ni acredita propiedad/robo. El HOME local contiene Git: el store rechazó esa ubicación; las fixtures utilizan datos privados bajo `/var/tmp`, espacio de investigación que puede limpiarse y no es el respaldo productivo de un avatar.

**Evidence:** `evidence/fingerprint-research/`, ignorado por Git: host C#, informes CPU/replay, seis PNG sintéticas, medidas, ambigüedad, logs, hashes y checkpoint. Registros privados fuera del repositorio; las imágenes no son screenshots del prefab.

**Next stage:** Stage 15, piloto Unity estático con copia no destructiva, reglas para seams/duplicados, autoverificación, presupuesto y rollback. Mantener pendientes las pruebas gráficas mientras el usuario esté en VRChat/VR; después Stage 16 de albedo y Stage 17 de verificación/calibración.

## Stage 15 — Mesh fingerprint, piloto por CPU

**Status:** avance parcial: núcleo Float32 y almacenamiento privado ejecutados por CPU; adaptador Unity compilado por separado, todavía sin ejecutar. [MESH_FINGERPRINT_PROTOTYPE.md](MESH_FINGERPRINT_PROTOTYPE.md) fija el contrato y las pruebas que faltan. La etapa no se declara cerrada y el asistente permanece en la ruta estable.

**Changes:** `GuardMeshFingerprintV1` deduplica posiciones exactas para que las uniones coincidentes reciban el mismo desplazamiento, normaliza por posiciones únicas y aplica QIM radial de 128 símbolos con dither HMAC versionado. Planificación/restauración, registro autenticado por contexto/binding y observador de investigación sin probabilidad de autoría. Autoverificación Float32 exige ≥96 símbolos utilizables y coincidencia de todos; límites inampliables de desplazamiento. `GuardFingerprintPrivateStore` añade IO Linux por descriptor, 0700/0600, creación sin sobrescribir, commit verificado y rollback con preservación de archivos editados/sustituidos. `GuardMeshFingerprintUnity` captura una fuente persistente y propone una copia estática con auditoría de atributos/topología, bounds globales/submesh ampliados, límites locales de triángulos y manifiesto público sin secretos; nunca asigna la copia a un avatar ni declara Ready.

**Checks:** **119 comprobaciones** del núcleo, 32 identidades/builds sintéticos sobre una fixture de 4.102 posiciones, **61 comprobaciones** de IO Linux. Fuente intacta y restauración exacta; 128/128 símbolos coinciden en nativo, reorder, duplicados sesgados, reflexión y una transformación uniforme Float32. El lector independiente Python concuerda con C# en resultados y hashes. Máximo desplazamiento observado entre 32 builds ≈1,00245×10⁻⁵ unidades de objeto, relativo a diagonal ≈2,89381×10⁻⁶. Ruido ±4×10⁻⁵ y escala no uniforme fallan; fuente sin marca y candidatos insuficientes no se atribuyen. Compilación aislada del adaptador contra bibliotecas instaladas de Unity 2022.3.22f1 correcta. No se inicia Unity, player ni RenderDoc; sin GPU, VRChat, avatar comercial ni descargas.

**Known issues:** fixture de posiciones antipodales sin triángulos; no demuestra comportamiento de una mesh Unity, diversidad de avatares o fiabilidad poblacional. Los controles de atributos, triángulos, reimportación y rollback del adaptador están escritos y compilados, pero aún no ejecutados. Pendientes fixture Unity con topología/seams, fallos en checkpoints, comparación lilToon, FBX/welding, compresión, SDK y contrato de skinning/morphs. Solo piloto estático; no se aplica a Carukia. La marca aporta una señal experimental de trazabilidad, no impide ripping ni prueba propiedad. `confidence=null` y verificador no calibrado.

**Evidence:** `evidence/mesh-fingerprint/cpu-final/`, ignorado por Git: hosts ejecutados, binarios XYZ Float32 sintéticos, informes C#/Python/IO, logs, compilación aislada y hashes de fuentes/herramientas. `RESULTS.json` y `WORK_STATE.json` en la raíz conservan el checkpoint. No son screenshots de un prefab.

**Next stage:** completar la ejecución de Stage 15 en proyecto Unity desechable cuando pueda iniciarse el Editor, sin inspeccionar VRChat. Medir apariencia/triángulos, reimportación y rollback antes de cerrar la etapa. Stage 16 permite continuar después con el núcleo de marcas de albedo por CPU; Stage 17 debe calibrar controles negativos y manejar ambigüedad.

## Fundación de validación y ciclo de vida

**Status:** metadata y rollback de preparación implementados; validador del SDK pendiente.

**Changes:** [GuardBuildManifest.cs](../Package/Assets/LinuxAvatarGuard/Editor/GuardBuildManifest.cs) registra schema, herramienta, codec, canales, cantidades y estado. `security-manifest.json` es público y no incluye valores de desbloqueo; `private-manifest.json` queda junto al respaldo, con permisos privados. El formato OSC sigue siendo 1. La protección de texturas figura explícitamente como cero.

`GuardSetup.Prepare` restaura el registro, retira solo assets y claves del nuevo BuildID y escribe un estado Failed privado si falla tras construir los assets. El builder también delimita qué carpeta/clave creó antes de borrarlas. Una preparación solo pasa a Ready después de guardar la escena y completar su metadata. Vista previa, apertura y arranque OSC rechazan estados incompletos; perfiles anteriores sin estado siguen siendo legacy.

Unity no permite crear una escena aditiva con escenas sin nombre pendientes. Se rechaza esa condición antes de asignar resultados y se pide que el usuario guarde esas escenas; los cambios de escenas ya guardadas se preservan. No se guarda automáticamente el trabajo del usuario.

**Tests:** 25 comprobaciones de [LAGPreparationValidation.cs](../Tests/LAGPreparationValidation.cs). Fallos inyectados en AssetsBuilt, ProfileRegistered y SceneSaved verifican restauración exacta del registro, retirada de claves nuevas, preservación de escena con cambios pendientes y rechazo de estados Failed/desconocidos. Incluye acceso legacy sin estado y rechazo temprano de escenas sin nombre. Resultados: `evidence/preparation-validation.json`.

**Known issues:** se cubren fallos en fronteras completadas, no toda combinación de disco lleno, permisos rotos o fallo del propio rollback. Si el rollback falla se informa el error compuesto. Falta conectar validación al SDK y revisar su resultado final; RequirePrepared no certifica el contenido de un AssetBundle.

**Next stage:** inventario/revisión de dependencias después de procesar el SDK y prueba de mutaciones tardías.

## Stage 19 — Primer experimento GPU adelantado

**Status:** fixture estática capturada y analizada en Unity; evaluación GPU completa pendiente.

**Changes:** [GPU_CAPTURE_THREAT_MODEL.md](GPU_CAPTURE_THREAT_MODEL.md) documenta RenderDoc 1.46, alcance autorizado, capa Vulkan solo del proceso y resultados. Se observan 515 vértices PostVS, geometría corregida proyectada y valores de runtime de la fixture en constantes.

**Tests:** dos draws verificados; entrada GPU coincide con la mesh codificada. Salida coincide con la proyección original con error máximo ≈ 1,19 × 10⁻⁷ al contemplar la convención Y de Vulkan. Geometría proyectada exportada directamente del replay, sin aplicar el decoder LAG en CPU.

**Known issues:** contenido sintético estático; no rest pose general, captura de avatar real ni benchmark. Demuestra exposición del codec legacy, no resistencia GPU del codec futuro. No se analiza el proceso de VRChat.

**Next stage:** adaptar esta evaluación al prototipo nuevo y añadir fixtures animadas propias en Unity.

## Ajustes propuestos al roadmap

Se conserva la numeración del documento para poder seguirlo. Propongo adelantar las **precondiciones** de Stage 19 (modelo GPU), Stage 21 (validación) y el manifest/versionado, antes de habilitar Stage 4 en avatares reales. Sus implementaciones completas siguen teniendo fases propias. Esto evita diseñar un codec sobre supuestos que el procesamiento del SDK o el render contradigan.

| Prioridad | Trabajo concreto | Criterio de cierre |
| --- | --- | --- |
| P0: contrato y compatibilidad | Adaptador legacy, identificación versionada y lectura de perfiles anteriores | Mismo comportamiento que 0.2.1; las claves y copias publicadas siguen desbloqueándose. |
| P0: baseline final | Fixture SDK, inventario de bundle y modelo GPU sobre contenido propio | Separar lo almacenado de lo renderizado; comprobar ausencia de secretos y fuentes originales en artefactos esperados. |
| P0: preparación coherente | Transacción completa y estados `Preparing/Ready/Failed` | Fallos inyectados en backup, perfil y escena no dejan una copia parcial presentada como lista. |
| P1: nuevo codec estático | IR reversible, seed controlado y variantes por mesh | Round-trip, determinismo y diversidad; rechazar operaciones numéricamente inestables. |
| P1: integración contextual | Materiales, clips y carriers por binding | Ningún renderer recibe un decoder/layout de otra mesh; intercambios animados correctos. |
| P1: evaluación adversarial | 100 builds, decoder legacy y extractor adaptativo | Resultados medidos y fallos de reconstrucción descritos, sin equiparar variedad con invulnerabilidad. |
| P1: avatares animados | Contrato de skinning y regresión de features | Sin deformación en matriz de poses; opt-in experimental hasta superar los casos admitidos. |
| P2: otras capas | TextureGuard, metadata, fingerprints y canaries | Investigación, pruebas y límites propios antes de cada activación. |

No añadir opciones de seguridad a la interfaz que todavía no tengan implementación y evidencia. Mantener selección simple de avatar y copia independiente; los perfiles futuros deben describir soporte y coste comprobados.

## Seguimiento de las 25 etapas

| Stage | Trabajo del roadmap | Estado actual / condición de avance |
| --- | --- | --- |
| 1 | Repository audit | Auditoría entregada. |
| 2 | Current behavior tests | Baseline ejecutada; bundle, cliente, poses y fallos pendientes. |
| 3 | Codec IR | Contratos y adaptador legacy implementados; 18 comprobaciones. |
| 4 | Polymorphic prototype | Implementado como API estática opt-in; 44 comprobaciones y captura GPU en Unity. |
| 5 | Per-mesh derivation | Implementado para estático; 53 comprobaciones y contexto privado reproducible. |
| 6 | Diversity tests | 100 builds estáticos completos + extractor; 2.714 comprobaciones; SDK/animación pendientes. |
| 7 | Dynamic attributes | Allocator estático validado: 683 comprobaciones originales, doce layouts y seis pares Vulkan. |
| 8 | ShaderForge integration | Investigación rígida/genérica implementada: 182 comprobaciones, copias por binding/slot y clips/controllers; skinning/SDK pendientes. |
| 9 | Performance benchmark | Benchmark rígido completado: 36 procesos, 24 rondas pareadas, 403.103 frames y recursos/ISA medidos; SDK/skinning pendientes. |
| 10 | TextureGuard research | Investigación entregada; contrato/restricciones del piloto y criterios de calidad definidos. |
| 11 | TextureGuard prototype | Piloto de albedo opaco validado: 586 checks Gamma/Linear, 128 comparaciones, 518 PNG y bundles Vulkan/Windows. |
| 12 | TextureGuard integration | V1 rígida y benchmark implementados; continuación V2 de mipmaps y codec skinned lineal opt-in. Compresión/avatar SDK e integración en el asistente pendientes. |
| 13 | MetadataGuard | Implementado y validado con contrato conservador; selecciones genéricas explícitas y preparación legacy opt-in. Nombres externos/OSC preservados; sin activación en el asistente ni validación del artefacto SDK. |
| 14 | Fingerprint research | Investigación CPU completada; identidad privada de 256 bits, contrato, controles/abstención y plan de calibración. Sin marcas productivas ni activación en el asistente. |
| 15 | Mesh fingerprint | Piloto Float32/IO validado por CPU (180 checks) y adaptador Unity compilado. Ejecución Unity, apariencia, FBX/SDK y skinning pendientes; etapa parcial. |
| 16 | Texture fingerprint | Pendiente; evaluar compresión, resize y color. |
| 17 | Fingerprint verifier | Pendiente; confianza calibrada y falsos positivos/negativos. |
| 18 | CanaryGuard | Pendiente; opcional, removible y de coste medido. |
| 19 | GPU threat-model testing | Captura estática de legacy y del prototipo en Unity realizada; matriz animada y análisis adaptativo pendientes. |
| 20 | Security profiles | Pendiente; solo agrupar capacidades realmente soportadas. |
| 21 | Pre-upload validator | Metadata, estados y rollback implementados; callbacks/revisión final pendientes. |
| 22 | Security report | Diseño de campos; no reemplaza el reporte actual. |
| 23 | Full regression | Pendiente; SDK, cliente y funciones de avatar admitidas. |
| 24 | Documentation | Documentación de esta iteración entregada; guías finales pendientes. |
| 25 | 1.0 release candidate | Pendiente; todas las condiciones anteriores y empaquetado verificadas. |

## Primeros cambios revisables

1. `refactor(meshguard): add legacy codec adapter`: conservar APIs públicas; introducir contexto/contrato y probar equivalencia con la ruta anterior.
2. `feat(build): add versioned manifests and build status`: añadir versionado en builds nuevos, compatibilidad de lectura y transacción de preparación.
3. `test(security): inspect SDK build dependencies`: fixture propia y revisión del resultado procesado, con rechazo de referencias inesperadas y secretos.
4. `docs(security): model GPU and skinning exposure`: estudio controlado de buffers, stages y normalización; documentar la información recuperable.
5. `feat(meshguard): add static polymorphic prototype`: experimento opt-in, fuera de la ruta predeterminada; no extenderlo automáticamente a Carukia.
6. `test(meshguard): measure diversity and adaptive decoding`: 100 builds, error geométrico, ataques propios y métricas de coste.

Los puntos 1 y la fundación del 2 están implementados; el análisis inicial del punto 4 se realizó para ambos codecs, y el prototipo estático del punto 5 se implementó como API opt-in. El punto 6 se implementó sobre fixtures estáticas propias; el punto 3 y la validación con avatares procesados por SDK siguen pendientes. Cada cambio debe dejar pruebas anteriores correctas y un estado de etapa actualizado. La implementación completa de otras capas se mantiene separada.

La continuación se valida con `LAGImplementationValidation.Run`: codec, funcionales, baseline adversarial, preparación y shaders/bundle Windows64. También se comprueba compilación del código fuente sin SDK y la localización ES/EN/JP. Los nuevos binarios se prepararán en una release posterior; no se afirma importar un unitypackage nuevo cuando se ha probado código copiado.

## Cómo reproducir la baseline

Usar un proyecto **desechable** con Unity/SDK/lilToon indicados. Copiar el paquete y los tests genéricos en una assembly Editor con `LinuxAvatarGuard.Validation.asmdef`. El runner `RunAudit` abre una escena vacía y el test funcional recrea `Assets/Fixture`; no ejecutarlo en un proyecto de trabajo con cambios sin guardar. No necesita el avatar comercial.

```bash
/home/stwby/Unity/Hub/Editor/2022.3.22f1/Editor/Unity \
  -batchmode -force-vulkan \
  -projectPath /home/stwby/LinuxAvatarGuard/ValidationProject \
  -executeMethod LAGSecurityBaseline.RunAudit -quit \
  -logFile /home/stwby/LinuxAvatarGuard/security-roadmap-baseline-clean.log

python3 -m unittest discover \
  -s /home/stwby/LinuxAvatarGuard/Tests -p test_osc.py -v
```

Confirmar código de salida, ausencia de errores de compilación y marcadores `LAG_VALIDATION_SUCCESS 26 checks` / `LAG_SECURITY_BASELINE_SUCCESS 14 checks`. Leer también avisos de cierre: un marcador correcto no significa que todo el log esté libre de incidencias.

Evidencia local en [validation.json](../evidence/validation.json), [security-baseline.json](../evidence/security-baseline.json) y capturas generadas por la fixture en `evidence/`. Son datos de validación local ignorados por Git; estos enlaces no estarán disponibles en un checkout público. Ninguna configuración privada del avatar se incluye en los documentos ni en los tests públicos.
