# Diseño del codec y migración

Estado: diseño de evolución. Contrato legacy, prototipo estático y derivación/contexto por binding implementados. Carriers dinámicos e integración productiva pendientes. Fecha: 2026-10-02. Contrato de Stage 5 en [PER_MESH_DERIVATION.md](PER_MESH_DERIVATION.md).

## Objetivo y límites

Separar el codec actual de la preparación de avatares para permitir programas reversibles distintos por build y mesh, conservar el resultado visual y medir cuánto trabajo adicional exige la extracción. El nuevo sistema debe seguir siendo Linux/Vulkan para autoría, compatible con las funciones de lilToon admitidas y no destructivo.

La diversidad es una propiedad comprobable. La imposibilidad de un extractor universal no es una garantía alcanzable por generar shaders distintos: el cliente recibe el decoder necesario para renderizar. Hay que evaluar un extractor que interprete el programa del build, además de intentar reutilizar un programa antiguo sin adaptarlo.

No añadir un componente de runtime que reconstruya y persista una mesh completa en CPU. Esta restricción se aplica a la copia subida y a su ejecución, no al avatar original que el usuario conserva en Unity ni a los arrays de referencia de las pruebas.

## Separación propuesta

| Componente | Responsabilidad | Integración actual |
| --- | --- | --- |
| `BuildContext` | Versiones, identidad del build, opciones, referencia a secretos privados y estado transaccional | Extraer progresivamente de `GuardBuilder`/`GuardSetup`. |
| `MeshIdentity` | Identidad del asset original y binding del renderer | Sustituir deduplicación por mesh cuando se necesite contexto distinto. |
| `IMeshCodec` | Planificar, codificar y emitir un decoder validable | Adaptador inicial alrededor de `GuardMesh`/`GuardShaders`. |
| `CodecProgram` | IR tipada, operaciones, constantes y contrato numérico | Nuevo modelo; no contiene seed/master key. |
| `AttributeLayout` | Carriers permitidos, dimensiones, precisión y reserva | Inicialmente UV7/UV8 para legacy. |
| `ShaderForge` | Convertir IR validada en extensión lilToon y remapear pases | Evolución del generador existente. |
| `MaterialBinding` | Asociar material y curvas de animación al codec del renderer | Reemplazar el cache global de materiales solo cuando el nuevo codec lo requiera. |
| `BuildValidator` | Rechazos previos y revisión del artefacto procesado | Mantener validación actual y ampliarla. |
| `SecurityManifest` | Versionado público y registro privado separado | Conservar formatos anteriores y añadir schema explícito. |

Borrador del contrato objetivo; la primera implementación usa `Mesh`, `float` e `int[]` para conservar las APIs actuales:

```csharp
interface IMeshCodec
{
    string CodecId { get; }
    int CodecVersion { get; }
    CodecPlan Plan(MeshAnalysis source, CodecBuildContext context);
    EncodedMesh Encode(Mesh source, CodecPlan plan, RuntimeKey runtimeKey);
    DecoderFragment EmitDecoder(CodecPlan plan, LilToonTarget target);
    ValidationResult Validate(CodecPlan plan, CodecValidationContext context);
}
```

`CodecPlan` reúne `CodecProgram`, layout, contrato de normales/animación y necesidades de material. `EncodedMesh` conserva las métricas de error, bounds y dependencias generadas. El encoder no escribe perfiles ni publica assets por su cuenta. `DecoderFragment` declara propiedades, atributos, hooks y pases requeridos; el generador rechaza conflictos antes de escribir shaders.

El primer implementador es `LegacyLinearCodecV1`, que delega en el código existente sin alterar fórmula, aleatoriedad, canales, nombres, presupuesto OSC ni formatos. Los builds antiguos no deben pasar por el generador de IR nuevo para volver a funcionar.

Implementación presente: `GuardCodec.cs` mantiene el programa legacy con schema 1 y añade instrucciones tipadas para el prototipo con schema 2. `GuardCodecInstruction.cs` implementa swap, flip, shear triangular, bend acotado y offset vectorial keyed. `GuardStaticCodec.cs` genera ocho instrucciones con seed explícito y dominios separados para programa/payload; valida mutaciones de fuente y emite la inversa HLSL. Detalles y evidencia en [STATIC_POLYMORPHIC_PROTOTYPE.md](STATIC_POLYMORPHIC_PROTOTYPE.md).

El decoder legacy continúa comprobándose contra su fixture congelado. `GuardBuildManifest` y la preparación productiva conservan el codec legacy; el prototipo no se puede seleccionar en el asistente ni registra perfiles OSC. Se añade mediante una llamada explícita a la API de investigación sobre un MeshRenderer estático propio.

## IR y validez numérica

Cada operación tiene tipo, versión, argumentos tipados, espacio de coordenadas, requisitos de atributos y límites. Debe definir transformación CPU, inversa, emisión HLSL de la inversa y validaciones. La emisión de código parte de nodos permitidos; no acepta texto HLSL libre desde manifests.

Operaciones candidatas del prototipo estático:

- Permutaciones de ejes y signos.
- Mezclas afines con matrices cuyo condicionamiento y rango se hayan comprobado.
- Offsets dependientes del payload y de los valores de runtime.
- Permutaciones y mezclas del payload independientes de la posición.

El decoder invierte las operaciones en orden inverso. Deben comprobarse overflow, valores no finitos, precisión, cancelación, bounds, efectos sobre tangentes/normales y comportamiento con compresión. Operaciones lineales y afines pueden colapsarse algebraicamente: contar nodos o cambiar su orden no basta para demostrar diversidad significativa.

Las operaciones modulares cuantizadas y XOR requieren un formato de bits realmente preservado. No aplicarlas sobre floats de posición o UV susceptibles de compresión sin demostrar un round-trip exacto. `SV_VertexID` requiere probar estabilidad frente a reordenación, batching y procesamiento del SDK; no debe servir como identidad persistente por defecto.

La IR describe el programa, pero no vuelve secreto su contenido. El shader compilado, sus constantes y los buffers siguen formando parte de la superficie observable.

## Identidad, seeds y claves

Identidad propuesta del asset:

```text
MeshAssetID = Hash(sourceGUID, localFileID, canonicalGeometryHash, codecSchema)
MeshBindingID = Hash(MeshAssetID, rendererPath, LOD, bindingContext)
meshSeed = HKDF-SHA256(masterBuildSeed, BuildID, MeshBindingID, purposeLabel)
```

Utilizar serialización canónica con longitudes y tipos, no concatenación ambigua de nombres. El hash de contenido incluye topología, atributos relevantes, bindposes y blendshapes antes de codificar. Los assets sin GUID necesitan un identificador reproducible del fixture/contenido. Cambiar la mesh fuente debe invalidar el plan anterior. Distinguir identidad de asset e instancia permite decidir cuándo compartir una mesh codificada y cuándo producir otra.

El master seed se genera con CSPRNG de 256 bits y queda en almacenamiento privado. La implementación de HKDF debe validarse con vectores conocidos y separación de dominios para programa, payload y futuros fingerprints. No usar `System.Random` como fuente criptográfica ni asumir que produce la misma secuencia en todos los runtimes.

Hay que separar tres conceptos:

| Dato | Tratamiento |
| --- | --- |
| Master seed/master build key | Privado; nunca sincronizado ni incluido en shader/material/Assets. |
| Valores de runtime | Observables; inicialmente cuatro Int con el transporte actual. |
| Programa y constantes por mesh | Derivados durante preparación; lo necesario para decodificar llega al cliente y puede observarse. |

Derivar programas diferentes de un master privado proporciona diversidad. Si todos aceptan los mismos cuatro valores observables, no proporciona aislamiento criptográfico de claves entre meshes. Enviar cuatro Int realmente independientes por mesh costaría `32 × meshes` bits y cambiaría FX/expresiones. Esa alternativa exige justificar su coste y no resuelve por sí sola la captura GPU.

Para reproducir una prueba, se fija explícitamente seed, BuildID, mesh identidad y versiones. Se comparan buffers y programas canónicos. No prometer AssetBundles byte a byte idénticos: GUID de assets generados, timestamps, compilador y serialización pueden variar. Los seeds de prueba pertenecen a fixtures sintéticas; ningún seed privado real se vuelca al log.

## Carriers y materiales

El asignador necesita conocer los atributos de la mesh y las funciones activas del shader. Un UV vacío no demuestra que lilToon, una máscara, una animación o un procesamiento posterior no lo necesite. Ante uso desconocido, rechazar el carrier o mantener un layout ya validado.

Primera versión: mantener UV7/UV8 para legacy; experimentar con otros UV altos solo en una matriz positiva de compatibilidad. No sobrescribir normales, tangentes o colores para ganar diversidad. Si se usa una textura auxiliar, incluir precisión, sampling, mipmaps y referencias del bundle en su contrato.

El cache futuro debe distinguir al menos:

```text
MaterialCacheKey = (originalMaterial, MeshBindingID, programHash, layoutHash, shaderTarget)
```

Un material compartido por Body y Hair puede necesitar dos copias con diferentes decoders. Cada curva que intercambia materiales debe remapearse con su ruta de renderer y slot; el mismo clip puede necesitar copias contextuales. Debe preservarse la relación con blends, expresiones y variantes `UsePass`. Introducir el codec sin este cambio produciría mallas que usan un programa ajeno, con riesgo de deformación.

## Contrato de skinning: puerta obligatoria

El prototipo inicial se limita a `MeshRenderer` y contenido propio. Permutar ejes o aplicar matrices antes de subir una mesh skinned no garantiza una inversa correcta en el hook de lilToon después de que Unity haya aplicado deformaciones. En general, las transformaciones propuestas no conmutan con linear blend skinning; la normalización añade otra fuente de error.

Antes de habilitar una operación en `SkinnedMeshRenderer`:

1. Documentar en qué espacio actúa cada etapa y qué datos recibe el shader en el pipeline objetivo.
2. Probar al menos un hueso, pesos mezclados, poses extremas, escalas admitidas, blendshapes de posición, mirada, visemas y animaciones combinadas.
3. Comparar error máximo por vértice, RMS, silueta y render de varios ángulos; una media de píxeles puede ocultar defectos pequeños.
4. Repetir con compresión, procesamiento del SDK y un player propio. Todo análisis GPU se limita a Unity/aplicaciones propias; una revisión visual normal del usuario en VRChat puede complementar compatibilidad.
5. Mantener el rechazo explícito si no puede demostrarse preservación.

[`SkinnedMeshRenderer.BakeMesh`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/SkinnedMeshRenderer.BakeMesh.html) sirve como referencia de CPU para fixtures propias, respetando su espacio relativo al renderer y su tratamiento de escalas. No demuestra por sí mismo la posición del hook de lilToon ni el comportamiento del shader final.

La ruta `decode → skinning` del roadmap es una hipótesis a verificar en el pipeline disponible. No se sustituirán automáticamente bindposes, pesos o controles faciales para forzarla.

## Manifest, transacción y revisión de subida

Separar `LAGVersion`, `SchemaVersion` y `CodecVersion`. El manifest público conserva BuildID, codecs/layouts o sus hashes, cantidades de assets, objetivos de compatibilidad y resultado de validaciones; no contiene secretos ni rutas privadas. La estructura del codec que el cliente necesita puede ser pública sin revelar el master seed.

Añadir un `private-manifest.json` junto a la configuración actual en el directorio privado existente, con schema y migraciones explícitas. Mantener `key.json format=1` mientras se utilice el transporte actual. Si cambia la estructura/nombre de los parámetros, un sender versionado deberá leer tanto el formato antiguo como el nuevo.

El resolver de perfil debe consultar metadata/manifest del asset o registro privado y conservar los fallbacks actuales. La aleatorización de nombres de shader no se puede activar hasta que este resolver sustituya la dependencia del prefijo conocido.

La preparación debe tener una transacción que abarque assets, perfil, respaldo y escena. Solo pasar a `Ready` tras verificar todo. Ante fallo, marcar `Failed` y retirar exclusivamente los resultados del nuevo BuildID; nunca borrar perfiles previos ni guardar la escena original para completar el rollback. Si se conservan restos para diagnóstico, no presentarlos como listos para subir.

El validador pre-upload debe actuar solo sobre avatares identificados como protegidos por LAG, tolerar versiones legacy conocidas y rechazar referencias inconsistentes, claves serializadas o un codec/layout no validado. Además hay que revisar el grafo después de transformaciones del SDK y, en fixtures, el contenido del bundle. Un callback al comienzo del build no cubre modificaciones posteriores. Seleccionar los hooks y su orden contra SDK 3.10.5 antes de implementarlos.

## Migración sin regenerar el avatar publicado

1. Introducir el adaptador legacy con tests comparativos de la ruta previa y la nueva; no cambiar las firmas públicas de uso actuales al inicio.
2. Reconocer los manifiestos ausentes de builds 0.2.x como legacy, no como inválidos por defecto. No reescribir claves, parámetros, shader ni Blueprint ID al abrirlos.
3. Añadir schema/codec a builds nuevos y registrar validaciones. Respaldar archivos antes de cualquier migración privada y hacerla idempotente.
4. Exponer el nuevo codec como opción experimental de preparación de otra copia; conservar la existente y el perfil anterior.
5. Probar la copia nueva en escenas aisladas y Gesture Manager, luego en cliente con el SDK oficial. El cambio de codec requiere un build/subida nuevo; no modifica retroactivamente un avatar remoto.
6. Cambiar el valor predeterminado únicamente después de superar la matriz de seguridad, preservación y rendimiento. Conservar desbloqueo y perfiles legacy.

## Aceptación y medición

| Área | Evidencia exigida antes de habilitar producción |
| --- | --- |
| Preservación | Hashes de assets originales iguales; topología, binds y funciones admitidas intactas. |
| Determinismo | Buffers y programa iguales bajo contexto explícitamente fijo; seeds distintos producen variedad medible. |
| Round-trip | Error máximo y RMS registrados; presupuesto por escala del avatar fijado antes de evaluar el prototipo. |
| GPU/lilToon | Compilación de pases y variantes, imágenes desde varios ángulos, sin deformación en poses admitidas. |
| Diversidad | 100 builds completos, métricas de programa, layout, constantes y shader normalizado; informar similitudes. |
| Adversarial | Decoder legacy, cross-build/cross-mesh y extractor adaptativo; documentar éxitos de reconstrucción. |
| Bundle | Ausencia de secretos y de meshes originales referenciadas; verificación después de procesar el SDK. |
| Fallos | Fallos inyectados en codificación, importación, backup, registro y guardado de escena dejan estado coherente. |
| Rendimiento | Original/legacy/nuevo en mismo hardware, resolución y escenario: medianas/p95, CPU/GPU, VRAM, variantes, draw calls, tamaño y tiempo de build. |
| Compatibilidad | SDK, Unity y lilToon fijados; features y casos rechazados declarados. |

Los presupuestos numéricos de rendimiento se fijarán con un benchmark previo; no se deducen de una captura estática ni se atribuyen al generador sin medición. La tolerancia visual 0,001 de las fixtures actuales seguirá como regresión, acompañada de límites geométricos y silueta para el nuevo codec.

## Capas posteriores

TextureGuard comienza con investigación y un piloto de albedo opaco en copias. La permutación de tiles cambia vecindades: exige resolver mipmaps, bordes/gutters, derivadas, filtrado anisotrópico y compresión. Remapear UV0 puede afectar muchas texturas del mismo material. Emisión/máscaras requieren sus casos propios y normal maps deben incorporarse después de validar su packing y sampling. Las texturas visibles siguen expuestas a captura; no modificar importadores originales.

MetadataGuard necesita un grafo de nombres utilizados por animación, visemas, tracking, PhysBones, Contacts e integradores. Renombrar solo campos internos con uso demostrado; ante duda, preservar. Las integraciones que hoy se rechazan sin hornear no pasan a estar soportadas por cambiar nombres.

FingerprintGuard requiere investigación y detector antes de anunciar confianza. Un identificador de 128 bits no mide probabilidad de detección ni demuestra autoría. Calibrar controles negativos, falsos positivos/negativos, reorder, FBX, transforms, pequeñas modificaciones y compresión. La salida debe expresar coincidencia compatible y evidencia, sin convertirla automáticamente en prueba de robo.

Canaries son opcionales, removibles y de coste medido. Su ausencia no invalida un avatar legítimo ni sustituye el detector.

Orden, dependencias y trabajo pendiente: [ROADMAP_STATUS.md](ROADMAP_STATUS.md).
