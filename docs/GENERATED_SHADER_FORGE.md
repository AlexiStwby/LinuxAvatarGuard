# Stage 8 — ShaderForge contextual

API de investigación para Unity 2022.3.22f1, Linux, Vulkan, Built-in y lilToon 2.3.4 con las fuentes revisadas de Stage 7. El asistente de avatares conserva el codec legacy. Esta entrega no habilita el codec polimórfico en avatares skinned ni certifica un upload del SDK.

## Copias por binding y slot

`GuardShaderForge.Prepare` analiza toda la raíz y codifica sus mallas antes de crear la carpeta de salida. Cada renderer recibe su mesh codificada y una familia de shaders específica. El cache distingue binding, slot, identidad del material original, hash del programa/layout e identidad del shader original. Body y Hair pueden compartir el material original y siguen recibiendo decoders diferentes; dos slots del mismo renderer también reciben copias distintas.

Los shaders, materiales y clips usan nombres derivados de hashes; el namespace de shaders contiene el BuildID, binding y programa. Se copian los providers de `UsePass`, se conservan sus licencias y se comprueban los assets generados por ruta, soporte Vulkan y errores de compilación. El shader desactiva dynamic batching para conservar posiciones en el espacio del objeto. Los nombres de funciones del decoder siguen reconocibles: estos nombres opacos no impiden interpretar el HLSL.

La salida debe ser una carpeta nueva `Assets/LinuxAvatarGuardGenerated/Research/<id>`. Una salida existente, una ruta con traversal o ancestros symlink se rechazan. El prefab independiente, clips, controllers, shaders y manifest quedan en esa carpeta. No se editan las fuentes originales ni se guarda globalmente el proyecto. Un fallo posterior a la creación retira únicamente la salida nueva; el registro de planes se confirma como un lote y no publica automáticamente secretos privados.

## Contrato de animación admitido

La raíz puede contener Transform, MeshFilter, MeshRenderer y un Animator genérico en la raíz, sin Avatar ni root motion. Las mallas deben ser persistentes, legibles, sin pesos, bindposes ni blendshapes, dentro del contrato numérico estático. Deben existir exactamente tantos materiales como submeshes.

El análisis incluye el controller, sus dependencias Motion/Mask, clips de base y reemplazos de AnimatorOverrideController. Solo se admiten capas Override sin sincronización, BlendTrees Simple1D/SimpleDirectional2D y clips persistentes `.anim` sin eventos. La primera matriz ejecutada usa BlendTrees 1D anidados y un override controller; 2D simple pertenece al contrato de pesos pero todavía no tiene una prueba visual específica.

Se admiten curvas de posición, escala, rotación, actividad de GameObjects, habilitación de renderers y propiedades shader numéricas o componentes Color/Vector con tipo comprobado. Cada referencia animada a un material debe apuntar a un slot válido de un renderer inequívoco. Las curvas se copian y remapean por ruta/renderer/slot. Los clips comparten copia dentro de un artefacto y reciben copias diferentes entre artefactos.

Los override controllers se reconstruyen mediante `ApplyOverrides`, evitando el cache nativo desactualizado observado al remapear únicamente sus campos serializados. La API oficial describe la aplicación conjunta de los pares de clips: [AnimatorOverrideController.ApplyOverrides](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AnimatorOverrideController.ApplyOverrides.html).

Scripts, StateMachineBehaviour, Animator anidados, Timeline, Animation legacy, capas additive/synced, BlendTrees Direct/no revisados, clips importados, swaps de mesh, referencias nulas, rutas ambiguas, propiedades de textura animadas y curvas `_LAG*` de entrada se rechazan. También se conservan los gates de MPB preexistentes, streams adicionales, static batching, keywords/shaders/versiones desconocidas y ocupación de atributos. Detén la previsualización de animación antes de preparar; el generador no borra MPBs del original para saltarse el gate.

## Atributos y reproducción privada

`GuardAnimationContext` conserva las posibles variantes por slot y el hash de los archivos/identidades de animación. La política contextual de atributos es **2**; la política estática de Stage 7 sigue siendo **1**, con su fingerprint anterior intacto. Todos los materiales alcanzables contribuyen a las reservas de UV.

ID Mask solo admite claves enteras 0–8 con segmentos constantes/discretos o planos. Cuando hay curvas del selector, se reserva el intervalo entero entre valores base y animados: las transiciones y mezclas pueden consumir valores intermedios aunque cada clip sea discreto. Por ejemplo, base 4 y clips 4–5 reservan UV4/UV5; base 8 y clips 4–5 reservan UV4–UV7 y cancelan por falta de dos carriers. Un swap de materiales sin curvas ID no introduce artificialmente ese intervalo interpolado.

Plan, Encode, Emit y replay privado vuelven a comprobar la animación, materiales, shaders y binding. Modificar un clip/controller/material invalida el plan. El hash del programa incorpora el hash de uso contextual; el codec matemático y los dominios HKDF anteriores no cambian.

El respaldo nuevo usa **schema privado 3**. Se conservan la lectura de schema 1 fijo y schema 2 estático/dinámico de Stage 7. Un registro contextual exige `CreateContextualCodec(binding, analysis)` y no puede reinterpretarse como schema 2 ni reproducirse con otro controller. `CreateCodec` mantiene el modo fijo para bindings nuevos y la reproducción de registros anteriores.

```csharp
// Solo una raíz rígida/genérica que cumple el contrato; no un avatar skinned.
using (var context = GuardBuildContext.CreateRandom())
using (var artifact = GuardShaderForge.Prepare(root, context,
    "Assets/LinuxAvatarGuardGenerated/Research/" + context.BuildId))
{
    context.SavePrivate(); // almacén Linux privado fuera de Unity/Git
    // Revisar artifact.PrefabPath, ShaderAssetPaths y PublicManifestPath.
}
```

El manifest público declara `research-rigid-context-only`, layout/hash por binding y `sdkProcessed=false`, `skinningValidated=false`, `texturesProtected=false`. Los materiales guardados contienen ceros en las cuatro propiedades de desbloqueo. El API no genera una capa OSC/FX productiva ni sube contenido.

## Pruebas y evidencia

Runner público: [LAGShaderForgeValidation.cs](../Tests/LAGShaderForgeValidation.cs). Solo puede ejecutarse en `ValidationProject`, con `LAG_BINDING_AUDIT_ALLOWED=synthetic-unity-only`, Linux/Vulkan/Built-in. Las mallas y materiales son fixtures procedurales propias.

La matriz compara original, desbloqueado y bloqueado desde bundles Linux cargados realmente, en dos vistas y cinco tiempos, además de ejecutar el Animator con BlendTrees/overrides y comprobar su pose analítica. Los clips nativos sin modificaciones también se comparan con los templates bloqueados. Unity elimina metadata de edición de curvas al compilar bundles: las curvas de desbloqueo se añaden a copias temporales de los templates editables, cuyos materiales se remapean a los objetos del bundle. Así Color, ID Mask y claves comparten la misma hoja de propiedades animadas. Estos clips/controllers se destruyen y nunca se guardan ni entran en el bundle. Esta inyección de prueba no certifica OSC, sincronización remota ni FX de VRChat.

Resultado: **182 comprobaciones**, dos bundles Linux, dos Windows64 compilados, cuatro bindings y **64 imágenes**. Diferencia visual desbloqueada máxima **0**; bloqueada mínima **≈ 0,04395888**. Extractor adaptativo: error máximo **≈ 7,0021 × 10⁻⁸**; decoder de otro renderer: RMS mínimo **≈ 0,7727544**. Regresión adicional: **864 comprobaciones** correctas e importación/reproducción privada sin SDK/lilToon.

Las evidencias locales están en `LinuxAvatarGuard/evidence/shader-forge/`: `validation.json`, imágenes PNG y bundles. Los logs de compilación y regresión quedan en `LinuxAvatarGuard/evidence/`. Las evidencias/bundles/respaldos siguen excluidos de Git. Los resultados numéricos finales y regresiones se registran en [ROADMAP_STATUS.md](ROADMAP_STATUS.md).

El extractor independiente sigue reconstruyendo los bindings al leer HLSL y recibir los valores runtime. Un decoder de otro renderer falla, pero un extractor adaptativo y PostVS continúan siendo límites del modelo. La protección de texturas, skinning, procesamiento del SDK, matriz amplia de lighting/stereo/pases y rendimiento permanecen pendientes. Todas las pruebas GPU se ejecutan en Unity; ninguna inspecciona VRChat.
