# Derivación por malla y binding — Stage 5

Fecha: 2026-10-02. API de investigación en `main`, sin nueva release. El asistente sigue usando legacy.

Este documento registra la entrega de Stage 5. [Stage 7](DYNAMIC_ATTRIBUTE_ALLOCATION.md) añade el propósito HKDF 4 para layout, `CreateDynamicCodec`, IR schema 3 y contexto privado schema 2. Los dominios previos y programas fijos conservan sus bytes; la lectura histórica de schema 1 mantiene UV6/UV7 explícitos.

## Identidad y derivación

[MeshBindingIdentity](../Package/Assets/LinuxAvatarGuard/Editor/GuardMeshIdentity.cs) captura la fuente antes de clonar/codificar. Combina GUID y localFileID del asset, huella canónica de contenido, segmentos relativos de jerarquía con nombre e índice de hermano, tipo e índice del componente renderer. Campos con longitud/tipo mediante `BinaryWriter`, hash SHA-256. Nombres duplicados, nombres con `/` y subassets no se identifican solo por el nombre.

La huella mantiene el contrato de Stage 4: posiciones, atributos con formato/dimensión/stream, normales, tangentes, colores, UV0–7, topología, índices y bounds. Solo meshes estáticas; skinning y blendshapes se rechazan antes de capturar/planificar. No es una identidad general de meshes animadas ni un fingerprint de autoría.

La raíz delimita el binding; se excluyen su nombre y ubicación externa. Un clon de la jerarquía y el renombrado del asset conservando GUID/localFileID/contenido mantienen la identidad. Renombrar/mover/reordenar el renderer dentro de la raíz, sustituir la fuente o mutar su contenido invalida el plan. Reimportar o reemplazar `.meta` puede cambiar la identidad. Meshes transitorias deben guardarse antes; no se modifica su importador.

[MeshKeyDerivation](../Package/Assets/LinuxAvatarGuard/Editor/GuardMeshKeyDerivation.cs) usa HKDF-SHA256 extract + expand de [RFC 5869](https://www.rfc-editor.org/rfc/rfc5869). Master seed: 32 bytes. BuildID: 32 caracteres hexadecimales minúsculos. Salt público: SHA-256 de `UTF8("LAG/build-salt/v1\0" + BuildID)`. `info` contiene dominio, versión de derivación, codec/version, BuildID, BindingStableID, propósito y longitud. Programa y payload reciben claves derivadas distintas de 32 bytes, además de sus dominios separados en el stream HMAC.

Los cuatro bytes de desbloqueo se derivan en un dominio global separado. **Siguen compartidos por el build y observables en el cliente.** La derivación separa programas/payloads; no añade secretos de runtime independientes ni convierte la geometría en cifrado fuerte. La exposición PostVS medida en Stage 4 permanece. Stage 6 detectó un programa con offsets consecutivos opuestos: ahora Plan/Validate/Emit lo rechazan y Encode cancela antes de crear la Mesh si el RMS sin valores de desbloqueo no supera 10⁻⁵. El programa/encoding de los casos admitidos conserva sus bytes; algunos seeds antes admitidos ahora se rechazan y requieren un contexto nuevo.

## Contexto privado

[GuardBuildContext](../Package/Assets/LinuxAvatarGuard/Editor/GuardBuildContext.cs) ofrece `CreateRandom()` con CSPRNG y `CreateReproducible(BuildID, seed)`. Reproducir requiere mismos BuildID, seed, identidad, intensidad y versiones; el timestamp no altera la derivación. `CreateCodec(binding)` valida la identidad al planificar, codificar y emitir HLSL. Un codec ligado al contexto rechaza valores de codificación distintos de sus cuatro bytes derivados, evitando generar una copia que el respaldo no desbloquee. El constructor standalone de Stage 4 conserva su comportamiento para sus fixtures.

`RecordPlan(binding, plan)` reproduce el programa esperado antes de registrarlo y rechaza programas de otro contexto. No certifica que un avatar haya sido preparado/subido. `SavePrivate()` registra schema, versiones de derivación/identidad/codec, BuildID, master seed, timestamp UTC y bindings con hash de programa e intensidad. El DTO solo se usa para persistencia privada; plan/IR/decoder no contienen el master seed.

Ruta: `$XDG_DATA_HOME/linux-avatar-guard/prototype-builds/<BuildID>.lagprivate`, o `~/.local/share/...` si XDG no es una ruta absoluta válida. Se rechazan ubicaciones dentro de Unity/Git, traversal por symlinks, archivos symlink/hard-linked/no regulares, dueño distinto y permisos no privados al leer. Directorios privados: 0700; archivos: 0600 desde la creación.

[GuardPrivateContextStore](../Package/Assets/LinuxAvatarGuard/Editor/GuardPrivateContextStore.cs) usa descriptores Linux (`openat`, `O_NOFOLLOW`, `statx`), límite 1 MiB, escritura/`fsync` y publicación atómica create-only mediante `linkat`/`unlinkat`. Nunca reemplaza un BuildID existente. Limpia temporales; un fallo tras publicar retira el nuevo enlace. Un cierre abrupto podría dejar un temporal/copia y necesita recuperación manual; no es una transacción completa de avatar. Requiere libc con `statx` y filesystem compatible con permisos/link/fsync; falla si no puede verificar el contrato. Layout `statx` conforme al [UAPI Linux](https://github.com/torvalds/linux/blob/master/include/uapi/linux/stat.h) y [manual Linux](https://man7.org/linux/man-pages/man2/statx.2.html).

`LoadPrivate(BuildID)` verifica versiones, formatos, timestamp, duplicados y cantidades. Solo permite bindings registrados; compara fuente/metadata y reproduce el hash antes de devolver su codec. Cambiar intensidad de un binding restaurado se rechaza. Arrays secretos se copian/limpian al disponer; cadenas JSON administradas no tienen borrado garantizado. Un proceso del mismo usuario puede leer archivos/memoria; los permisos no forman un sandbox local.

Los contextos no entran en `GuardProfile`, OSC o manifests legacy. No se migran perfiles/claves ni se cambia el Carukia funcional.

## Pruebas

[LAGBindingValidation.cs](../Tests/LAGBindingValidation.cs): **53 comprobaciones correctas** en Unity 2022.3.22f1 Linux/Vulkan/Built-in, SDK 3.10.5 y lilToon 2.3.4. Superficie procedural, assets/subassets propios y seed de fixture público. Los contextos temporales quedan fuera del proyecto y se eliminan después.

Vectores SHA-256 A.1/A.2/A.3 del RFC, límite HKDF, dominios, cambios de BuildID/seed/binding, clones/renombrado, programas separados sobre fuente compartida, reproducción exacta desde disco, mutaciones/incompatibilidades, permisos/tipos de archivo. Incluye FIFO para verificar rechazo sin bloqueo y aliases symlink. Las 44 pruebas de Stage 4 siguen pasando, con renders Vulkan y bundle Windows64.

Evidencia local: `evidence/binding-derivation/validation.json`. El primer import recompiló tras errores transitorios de resolución de tipos nuevos. La repetición final tiene marcador de éxito, salida 0 y ningún error C#/shader. Evidencias/logs no se publican en Git.

Copiar fuente/tests a un proyecto **desechable llamado `ValidationProject`** y ejecutar:

```sh
LAG_BINDING_AUDIT_ALLOWED=synthetic-unity-only /ruta/Unity \
  -batchmode -force-vulkan -projectPath /ruta/ValidationProject \
  -executeMethod LAGBindingValidation.RunWithPrototypeRegression -quit \
  -logFile /ruta/binding-validation.log
```

Abre una escena vacía y recrea `Assets/BindingCodecFixture`. No analiza/interactúa con VRChat. Siguiente gate: 100 builds sintéticos completos y extractor adaptativo, antes de carriers/contexto productivos y avatares animados.
