# Estado y plan de mejora de seguridad

Fecha: 2026-10-02. Versión productiva auditada: 0.2.1 (`7f3ebc3`). Entrada: `LinuxAvatarGuard_SECURITY_ROADMAP.md`, especialmente apartados 37–45.

## Decisión de esta iteración

El roadmap identifica correctamente la necesidad de diversidad, validación y trazabilidad. El codec actual puede ser reconstruido con una fórmula estable si se conocen sus cuatro parámetros. La baseline añadida reproduce esa debilidad; no constituye una nueva protección.

La primera iteración entregó auditoría, baseline y diseño. Las continuaciones implementaron el adaptador legacy, metadata/rollback, análisis GPU controlado y el prototipo estático de Stage 4. Stage 5 añade identidad y derivación por binding con contexto privado reproducible; Stage 6 mide diversidad y extracción adaptativa; Stage 7 incorpora asignación conservadora de atributos estáticos; Stage 8 añade ShaderForge contextual para raíces rígidas con Animator genérico. La fórmula legacy, UV, formato OSC y avatares publicados siguen compatibles; los binarios de 0.2.1 no se sustituyen. TextureGuard, fingerprints, skinning e integración productiva del codec nuevo siguen pendientes.

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
| 10 | TextureGuard research | Pendiente; entregar investigación de sampling/import/mipmaps. |
| 11 | TextureGuard prototype | Pendiente; piloto de albedo opaco y copias independientes. |
| 12 | TextureGuard integration | Pendiente; ampliar por tipo de textura tras validar calidad/coste. |
| 13 | MetadataGuard | Pendiente; preservar nombres funcionales y schema OSC legacy. |
| 14 | Fingerprint research | Pendiente; definir amenazas, controles y detector. |
| 15 | Mesh fingerprint | Pendiente; sobrevivir transformaciones y reorder con error visual medido. |
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
