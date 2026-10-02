# Stage 7 — asignación dinámica de atributos

Fecha: 2026-10-02. Implementación de investigación en `main`; release pública 0.2.1 y asistente de avatares conservan el codec legacy. Solo se ha probado con geometría propia y Unity; no se inspecciona VRChat.

## Contrato implementado

[AttributeAllocator](../Package/Assets/LinuxAvatarGuard/Editor/GuardAttributeAllocator.cs) ofrece un contrato positivo para Unity 2022.3.22f1, Linux, Vulkan, Built-in y el paquete lilToon 2.3.4. No interpreta HLSL arbitrario. Verifica procedencia, versión, nombres de variantes, soporte y digest de **101 fuentes `.shader`/`.hlsl`/`.cginc`**. Variantes estándar: opaque, cutout, transparent, one-pass y two-pass transparent, con/sin outline; Lite, Multi, tessellation, forks y shaders custom quedan fuera.

Digest revisado: `46fda1de8dedeb5f57c58ab069a4c66b6937b867cb55dec54f73c987d344d892`. Se decodifica cada archivo como texto con `File.ReadAllText`, se normaliza CRLF/CR a LF, se calcula SHA-256 UTF-8 y se ordena por ruta relativa ordinal. El manifiesto concatenado usa `ruta + NUL + digest + LF` y se vuelve a hashear. El BOM de entrada no forma parte del texto. Un archivo añadido o una modificación de un include cancela el análisis; no se actualiza automáticamente la lista de fuentes de confianza.

El contrato revisado tiene pases Built-in forward/forward-add, shadow-caster, meta y outline donde corresponde. No introduce posición/velocidad previa en appdata. Se rechazan keywords de material y los keywords globales de posición/velocidad previa. Los contratos SRP/motion vectors no se admiten.

| Atributo — índice Unity | Política 1 |
| --- | --- |
| UV0–3 | Reservados para shading, decals y AudioLink. |
| UV4–7 | Candidatos solo cuando **no existe el atributo**, independientemente de dimensión/formato, y ningún material lo selecciona con `_IDMaskFrom`. |
| Normales, tangentes y colores | Conservados; nunca carriers. |
| Canales elegidos | Dos atributos distintos `Float32 × 2`; cuatro coeficientes repartidos entre ambos. |

La presencia se consulta con [Mesh.HasVertexAttribute](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Mesh.HasVertexAttribute.html); una lista UV vacía no constituye una prueba de disponibilidad semántica. ID Mask reserva el canal incluso cuando sus máscaras están desactivadas. Los selectores no enteros, no finitos o fuera de 0–8 se rechazan. En estas fuentes el `Int` histórico de ShaderLab para `_IDMaskFrom` se consulta como propiedad flotante; las pruebas usan `SetFloat`, igual que ese contrato, no el almacenamiento de `Integer` moderno.

Se inspeccionan **todos** los materiales/submeshes. Se exige un material persistente por submesh, sin slots nulos/adicionales. El hash de análisis incluye identidad del binding, fuentes, identidad del material/shader/texturas, propiedades tipadas, transformaciones de textura y estado de render/pases. No depende de instanceIDs transitorios. No es un hash de los píxeles de textura ni un fingerprint de autoría.

El contexto se limita a raíces estáticas: se rechazan Animator, Animation, Timeline y scripts, aunque estén desactivados, tanto dentro de la raíz como en sus ancestros. Se rechazan también MaterialPropertyBlock globales/per-material, streams adicionales/enlighten, static batching y overrides de batching. [Los vertex streams adicionales pueden sustituir atributos de la malla principal](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/MeshRenderer-additionalVertexStreams.html); por ello un UV libre en MeshFilter no basta. Los shaders del prototipo generan `DisableBatching=True` para conservar las coordenadas de objeto que necesita el decoder no lineal, conforme al [contrato de tags de Unity](https://docs.unity3d.com/2022.3/Documentation/Manual/SL-SubShaderTags.html). La ruta legacy conserva sus shaders. Skinning y blendshapes mantienen el rechazo del prototipo. Un script externo que modifique objetos fuera de ese ámbito no queda auditado; el llamador debe mantener estática la fixture. El análisis no certifica un avatar ni plugins de procesamiento.

## Planificación y reproducción

```csharp
var binding = MeshBindingIdentity.Capture(root, meshRenderer);
using (var context = GuardBuildContext.CreateRandom())
using (var codec = context.CreateDynamicCodec(binding))
{
    var plan = codec.Plan(sourceMesh, 0.15f);
    var protectedMesh = codec.Encode(sourceMesh, plan, context.RuntimeKey());
    var decoder = codec.EmitDecoder(plan);
    context.RecordPlan(binding, plan);
    var privateBackup = context.SavePrivate();
    // El llamador guarda la copia y conserva/libera protectedMesh según su ciclo de vida.
}
```

Es una API de Editor para pruebas estáticas, sin instalación/integración automática de un avatar. El llamador debe gestionar la transacción de assets/escena y generar materiales por binding. No se añaden componentes runtime ni un decoder CPU al avatar.

El propósito HKDF **4 — AttributeLayout** separa el seed de asignación de Program (1), Payload (2) y runtime-global (3). El allocator ordena candidatos por SHA-256 del seed de layout y su índice, desempata por índice y toma dos. Mismos contexto/binding/política reproducen la elección. Falta de dos candidatos cancela antes de asignar una Mesh o escribir shaders.

Plan, Validate, Encode y Emit vuelven a comprobar disponibilidad, binding, fuente y hash de materiales/shaders. El decoder emite los defines y referencias `input.uvN` del layout concreto. El programa dinámico usa **IR schema 3**, con canales, componentes, política y hash de uso en su hash canónico. Los programas fijos schema 2 conservan sus bytes/hash/decoder; no se cambian el codec/version matemático ni los dominios previos de derivación.

El contexto privado nuevo usa **schema 2**, registra layout/política/hash de análisis y comprueba su reproducción. Lectura de schema 1: asignación fija explícita UV6/UV7, sin reinterpretación dinámica. `CreateCodec(binding)` conserva el modo fijo para nuevos bindings y reproduce el modo registrado al restaurar; `CreateDynamicCodec(binding)` opta por el allocator y rechaza convertir un registro fijo. Se conservan el almacén Linux 0700/0600 y la prohibición de respaldos dentro de Unity/Git. Un cambio de material después de guardar invalida la reproducción dinámica y requiere un contexto nuevo.

## Validación ejecutada

[LAGAttributeValidation](../Tests/LAGAttributeValidation.cs) pasó **683 comprobaciones** en el proyecto desechable. Las identidades de las meshes de prueba están fijadas mediante GUIDs derivados de datos públicos de fixture; no se publican modelos, seeds privados ni valores de usuario.

| Prueba | Resultado |
| --- | --- |
| 16 combinaciones de UV4–7 ocupados × dimensiones 2/3/4 | Canales ocupados reservados, datos/formato/dimensión preservados, cancelación si quedan menos de dos. |
| ID Mask 0–8, varios materiales/variantes/submeshes | Reservas correctas, incluidas máscaras desactivadas. |
| Mutaciones de selector, otras propiedades, keywords, pases, material, MPB, streams, batching y UV fuente | Encode/Emit cancelan antes de escribir. |
| Animación, scripts/ancestros, slots inválidos, shader desconocido y fuentes modificadas | Rechazo conservador. |
| Persistencia/tampering schema 2 y replay histórico schema 1 sin campos de asignación | Reproducción exacta; layouts/políticas incompatibles y downgrade rechazados. |
| 100 contextos privados de fixture admitidos | Los **12 layouts ordenados** entre cuatro canales aparecen. Un candidato sin dependencia de clave (índice 7) es rechazado por el guard previo y se contabiliza aparte. |
| 6 pares no ordenados × 10 variantes lilToon | 78 shaders, incluidos providers UsePass, generados y cargados con soporte Vulkan desde seis AssetBundles Linux reales. |
| Reapertura nativa y extracción adaptativa | Todos los pares se reconstruyen leyendo el decoder y valores públicos proporcionados; máximo error observado **1,2288 × 10⁻⁷** unidades. |
| 6 bundles × 3 ángulos × original/desbloqueado/bloqueado | **54 PNG**, 18 comparaciones: diferencia desbloqueada máxima **0**; bloqueada mínima **0,00834097** (media RGB absoluta normalizada). |
| Regresión compartida | 53 binding + 44 prototipo + 83 legacy/preparación/backend; 180 comprobaciones. |
| Proyecto sin SDK ni lilToon | Compilación del código y reproducción privada del modo fijo correctas en Vulkan. |

Evidencia local, excluida de Git: `LinuxAvatarGuard/evidence/dynamic-attributes/validation.json`, `sdk-free.json`, `uv-*-view-*-{original,unlocked,locked}.png` y `linux-bundles/`. Logs finales: `evidence/dynamic-attributes-final.log` y `dynamic-attributes-sdk-free-final.log`. El manifest público de cada fixture describe política, formato y canales; los bundles excluyen contexto privado y mesh original. Materiales guardados conservan valores de desbloqueo cero. La factory carga el shader generado por ruta de asset, para evitar devolver un shader anterior con el mismo nombre global.

Las imágenes usan un material unlit/mono y geometría propia. La compilación de bundles usa el filtro **solo de pruebas** de Stage 6, conservando un programa por pase; las 10 variantes soportadas no equivalen a ejecutar todas las combinaciones de iluminación/fog/stereo/instancing. Los renders comparan forward; no certifican la apariencia de todos los pases. Las pruebas no procesan un avatar final mediante SDK ni reemplazan la validación de Carukia. Los avisos de licenciamiento/cierre del Editor no son una medición de rendimiento o estabilidad.

## Límite de seguridad y siguiente etapa

La asignación deja de depender de UV6/UV7 fijos y evita sobrescribir atributos usados. **No bloquea extracción adaptativa:** el shader describe cómo localizar los carriers, y los valores runtime siguen compartidos/observables. El extractor de esta prueba lee HLSL fuente y recibe valores públicos de fixture; no recupera claves desconocidas ni decompila bytecode. La observabilidad GPU/PostVS de etapas anteriores permanece.

La fragmentación implementada reparte cuatro coeficientes en dos UV. No se afirma XOR, secret sharing, protección de textura ni soporte de colores/tangentes/lookup como carriers. Para ampliar versiones/features se necesita revisar otro contrato y ejecutar su matriz.

Stage 8 debe incorporar contexto de renderers/material slots/clips, cambios de material y propiedades animadas, generación explícita de providers/configuración y gates de compatibilidad antes de habilitar el codec nuevo en preparación de avatares. Skinning y validación del resultado procesado por SDK siguen pendientes. El proyecto funcional del usuario y su upload permanecen fuera de estas pruebas.

Fuentes primarias del contrato: [release lilToon 2.3.4](https://github.com/lilxyzw/lilToon/releases/tag/2.3.4), fuentes locales verificadas de `lil_common_appdata.hlsl`/`lil_common_vert.hlsl` y los providers Built-in; [investigación previa](DYNAMIC_ATTRIBUTE_RESEARCH.md) registra los consumidores y reservas. No se presume equivalencia con `master` futuro.
