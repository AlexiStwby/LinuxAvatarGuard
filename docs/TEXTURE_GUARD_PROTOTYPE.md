# TextureGuard — piloto de albedo opaco

Stage 11. API de investigación opt-in; requiere Unity 2022.3.22f1/Linux/Vulkan/Built-in y fuentes lilToon 2.3.4 verificadas. El asistente de avatares conserva su pipeline actual. La [investigación previa](TEXTURE_GUARD_RESEARCH.md) define el motivo de las restricciones.

## Implementación y datos

[`GuardTextureCodec.cs`](../Package/Assets/LinuxAvatarGuard/Editor/GuardTextureCodec.cs) separa `ITextureCodec`, `TexturePlan`, `TextureProgram`, `TextureTile` y `TextureDecoderFragment`. El programa es inmutable y contiene dieciséis tiles con destinos distintos, orientación de ocho posibilidades, una de seis permutaciones RGB y salts. HMAC-SHA256 deriva un stream reproducible de la seed de 32 bytes, fingerprint de la fuente y scope del binding/material. La seed se copia/retira al disponer el codec; el programa y HLSL no guardan los valores runtime esperados.

Encoding usa los cuatro parámetros de 1–255 existentes para XOR por tile/canal y escribe una copia RGBA32 lineal con dimensiones originales. Retira la copia CPU del payload con `Apply(false,true)`. Solo existe decoding C# en el **test Editor independiente**; la solución no añade un componente runtime que produzca una textura CPU descifrada.

El shader usa `Texture2D.Load` para recuperar bytes del payload. Deshace XOR/orden RGB y convierte sRGB según los datos originales y el espacio de color de la compilación. Point lee un texel; Bilinear recupera cuatro texels del vecindario original y después interpola. Repeat/Clamp se aplican por eje antes de localizar el tile. UV0, `fd.uvMain`, escala/offset y el sampler compartido de lilToon se conservan.

`Texture.isDataSRGB` describe los bytes originales, mientras `graphicsFormat` describe la vista GPU. El programa conserva la primera flag; esto evita convertir una vista GPU Gamma en una decisión permanente del decoder. Los resultados Gamma y Linear se verifican por separado.

[`GuardTextureForge.cs`](../Package/Assets/LinuxAvatarGuard/Editor/GuardTextureForge.cs) prepara un material y su albedo, conserva la licencia lilToon y genera todos los providers UsePass con nombres opacos. Reutiliza `GuardShaders` con un fragmento opcional; sus constructores/productos existentes no reciben ese fragmento. El hook se inserta solo tras includes **activos** de `lil_common.hlsl`, preservando el workaround comentado de la fachada lilToon.

## Contrato

| Entrada | Piloto admitido |
| --- | --- |
| Textura | Persistente/guardada, legible, RGBA32, alfa 255 en cada texel. |
| Dimensiones | Cuadrada power-of-two, 16–1024; misma resolución en el payload. |
| Importador | Default cuando existe; sin compresión, mipmaps ni streaming. |
| Sampling | Point/Bilinear; Repeat/Clamp por eje; anisotropía ≤1. |
| Color | Bytes sRGB o lineales; pruebas de proyecto Gamma y Linear. |
| Material | Asset guardado lilToon opaco, sin keywords; no alias del albedo en otra propiedad. |
| Salida | Carpeta nueva propia bajo `Assets/LinuxAvatarGuardGenerated/Research/`; rechazo de enlaces/traversal/existentes. |
| Backend | Preparación/render probado Vulkan; bundle Windows64 compilado con D3D11, sin afirmar ejecución. |

La validación incluye identidad, pixels, sampling y estado del importador. Cambiar una entrada invalida el plan. Una modificación tardía de la fuente o claves distintas de cero en el material generado cancela y retira la carpeta nueva. Los archivos originales, settings de importación y fuentes instaladas no se modifican.

El piloto protege exclusivamente `_MainTex`. Otras texturas siguen sus referencias y no se declaran protegidas. No se ha validado la matriz completa de normales, emisión, iluminación, stereo y propiedades animadas. Materiales transparentes/cutout, HDR, compresión, mipmaps y anisotropía/trilinear requieren ampliaciones específicas.

## Uso Editor de investigación

```csharp
// Valores procedentes del contexto privado del experimento, fuera de Assets/Git.
using (var codec = new TextureGuardCodecV1(privateSeed32Bytes, materialScope))
{
    GuardTextureArtifact result = GuardTextureForge.Prepare(
        originalMaterial, codec,
        "Assets/LinuxAvatarGuardGenerated/Research/owned-texture-experiment",
        runtimeValues);
    // Asignar result.Material exclusivamente al renderer de la copia de prueba.
}
```

El caller es responsable de conservar seed/scope/runtime fuera de Assets si necesita reproducir el experimento en otra sesión. El piloto no registra nuevos perfiles OSC ni modifica el private schema 3 del MeshGuard existente; la persistencia integrada pertenecerá a Stage 12.

Al construir un bundle de prueba, incluir explícitamente textura/material generados, prefab, manifest y `ShaderAssetPaths`, incluidos los providers. Revisar el grafo completo de dependencias. `AssetBundle.LoadAllAssets` no enumera necesariamente objetos presentes solo como dependencias; su ausencia de la lista no prueba ausencia del bundle. La suite enumera las salidas explícitas y verifica además los bindings del prefab reabierto y el grafo de autoría.

## Pruebas reproducibles

[`LAGTextureValidation.cs`](../Tests/LAGTextureValidation.cs) usa imágenes y un quad procedurales propios. Copiar el código fuente del paquete y los runners genéricos al proyecto desechable `ValidationProject`, con el asmdef de validación. Requiere lilToon 2.3.4 local y permisos para pruebas sintéticas, no el avatar comercial.

```bash
LAG_BINDING_AUDIT_ALLOWED=synthetic-unity-only /ruta/al/Editor/Unity \
  -batchmode -force-vulkan -projectPath /ruta/ValidationProject \
  -executeMethod LAGTextureValidation.Run -quit -logFile /ruta/texture-validation.log
```

Ejecutar en dos arranques separados con Color Space Gamma y Linear, respectivamente. Comprobar código de salida cero, marcador `LAG_TEXTURE_VALIDATION_SUCCESS` y ausencia de errores C#/shader/excepciones. `RunRegression` ejecuta las suites de ShaderForge, atributos, binding, prototipo estático y fundación legacy/SDK cuando está instalado. La validación SDK-free de esta iteración comprueba compilación y API de texturas con fuentes copiadas, no importa un unitypackage nuevo.

Evidencia local: `evidence/texture-guard/{gamma,linear}/validation.json`, `resources.json`, PNG y bundles; `sdk-free.json`, `final-runs.json` y logs en `evidence/`. Estos archivos permanecen ignorados por Git. Los resultados finales y métricas se registran en [ROADMAP_STATUS.md](ROADMAP_STATUS.md).

## Alcance de seguridad y siguiente integración

El payload no se usa como albedo directamente. El extractor del test interpreta **HLSL emitido más parámetros conocidos** y debe recuperar todos los programas; su éxito demuestra una limitación vigente frente a extracción específica/adaptativa. No decompila shaders binarios ni obtiene claves desconocidas. XOR/tile permutation no es cifrado fuerte, y no resuelve observabilidad GPU.

La prueba inicial del bundle descubrió un fallo de inserción sobre `//#include`, corregido con anclaje de includes activos y una regresión específica. El primer inventario de `LoadAllAssets<Texture2D>` devolvió cero porque se habían empaquetado las texturas solo como dependencias; se corrigió la enumeración del harness y se añadió revisión de dependencias/bindings. Ninguno de esos primeros intentos se cuenta como ejecución final correcta.

Todavía falta medir este decoder en un player propio con el método de Stage 9; el tiempo de `Camera.Render` no constituye un benchmark. Los counters/avisos del Editor al cerrar tampoco se presentan como VRAM física ni como prueba de ausencia de leaks.

Stage 12 deberá integrar planes por binding/slot, persistencia privada, remapeo de materiales/clips y validación del artefacto final; ampliar formatos/mipmaps y medir calidad/coste antes de exponer una opción en el asistente. La ruta productiva y sus manifests siguen declarando cero texturas protegidas. Carukia y el cliente VRChat no forman parte de las pruebas de este piloto.
