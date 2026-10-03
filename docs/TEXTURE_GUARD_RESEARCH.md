# TextureGuard — investigación antes del prototipo

Fecha: 2026-10-02. Stage 10. Contrato estudiado: Unity 2022.3.22f1, Built-in, lilToon 2.3.4 y preparación Linux/Vulkan. Esta investigación no activa protección de texturas en el asistente de avatares.

## Datos que recibe el shader

Las fuentes locales de lilToon están verificadas con el digest completo de `AttributeAllocator.ReviewedShaderDigest`. En [`lil_common_frag.hlsl` 2.3.4](https://github.com/lilxyzw/lilToon/blob/2.3.4/Assets/lilToon/Shader/Includes/lil_common_frag.hlsl), `LIL_GET_MAIN_TEX` lee `_MainTex` con `fd.uvMain`, y `OVERRIDE_MAIN` aplica después la corrección de tono y `_Color`. `fd.uvMain` ya contiene selección de UV, escala, offset, scroll/rotación y los ajustes de parallax que correspondan. Los shaders usan proveedores `UsePass`; cambiar únicamente el shader visible no cambia la implementación del pase.

El sampler de `_MainTex` también se reutiliza en varias máscaras y en la primera normal map. Alterar UV0 o el sampler global para descifrar el albedo puede cambiar otras funciones. El piloto debe reemplazar exclusivamente la lectura del albedo en copias de todos los providers, conservar UV y transformación del material, y no cambiar las fuentes instaladas.

Las emisiones tienen sus propias UV, animación y máscaras; las máscaras son datos numéricos que normalmente no usan sRGB. [`lil_common_functions.hlsl` 2.3.4](https://github.com/lilxyzw/lilToon/blob/2.3.4/Assets/lilToon/Shader/Includes/lil_common_functions.hlsl) muestra que `lilUnpackNormalScale` depende de `UNITY_NO_DXT5nm` y `UNITY_ASTC_NORMALMAP_ENCODING`: una textura normal importada no puede tratarse como RGB de albedo. Estos tipos quedan fuera del primer piloto.

## Color y pipeline de importación

Unity puede decodificar sRGB durante la lectura GPU en un proyecto Linear. En Gamma la operación de color es distinta; debe comprobarse en ambos modos. La flag de la fuente y el espacio de color del proyecto son datos diferentes. Referencia: [Unity 2022.3, texturas lineales y sRGB](https://docs.unity3d.com/2022.3/Documentation/Manual/LinearRendering-LinearTextures.html).

El plan registra [`Texture.isDataSRGB`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture-isDataSRGB.html), que describe los bytes de la fuente. `GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat)` describe la vista GPU y puede dar otro resultado en Gamma. Usar la vista GPU como flag permanente del decoder dejaría un shader incorrecto al compilarlo después en Linear. El payload se valida tanto por sus datos lineales como por su formato GPU lineal.

Para XOR de bytes o permutación de canales, leer el payload con un sampler sRGB altera sus valores antes del decoder. La propuesta es almacenar el payload como RGBA32 **lineal**, reconstruir sus bytes primero, aplicar la transferencia sRGB exacta cuando proceda y finalmente filtrar en el espacio que usa el original. El alfa opaco se conserva como 255. No se promete calidad de HDR, normales, alfa transparente ni importadores especiales con esta ruta.

El primer contrato acepta una fuente persistente, legible, RGBA32 sin compresión ni mipmaps, cuadrada y de tamaño acotado. El importador debe ser Default cuando existe. Las flags de importación, identidad, bytes y estado de sampling forman parte de la validación del plan. Una mutación invalida la operación. No se modifica ni se reimporta automáticamente la fuente para hacerla compatible. [TextureImporter](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/TextureImporter.html) describe compresión, sRGB, legibilidad, mipmaps y wrap; [GetPixelData](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2D.GetPixelData.html) permite leer los bytes del mip explícito.

## Bordes, mipmaps y filtros

| Estrategia | Problema | Decisión inicial |
| --- | --- | --- |
| Remapear una UV y usar bilinear sobre tiles permutados | En los bordes interpola un tile vecino distinto del original. | Rechazar esta implementación. |
| Generar mipmaps del payload ya permutado/XOR | Promedia datos codificados y vecindades falsas; no equivale a codificar los mips originales. | No llamar `Apply(true)` sobre payloads. |
| Gutters alrededor de tiles | Incrementan memoria; cada mip necesita vecindades originales y padding propio. | Investigar después del piloto. |
| Decodificar texels y filtrar en coordenadas originales | Respeta el vecindario original. Point necesita una lectura; Bilinear, cuatro. | Elegido para el piloto. |
| Trilinear/anisotrópico y minificación | Necesitan mips correctos, derivadas originales y más lecturas. | No admitidos en el piloto. |
| Compresión BCn/ASTC del payload XOR | Introduce errores en bytes que pueden amplificarse al invertir XOR. | Rechazar compresión, medir un codec específico posteriormente. |

[Unity Apply](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2D.Apply.html) confirma que generar mips usa el mip 0. Una pirámide futura debe generarse primero en el espacio del original y codificar cada nivel independientemente; el pequeño mip final no puede mantener indefinidamente una grilla fija de 4×4. [Introducción a mipmaps](https://docs.unity3d.com/2022.3/Documentation/Manual/texture-mipmaps-introduction.html) explica su relación con resolución y distancia.

El piloto utilizará `Texture2D.Load` sobre coordenadas enteras del payload; no depende de un sampler inline ni cambia el sampler compartido de lilToon. La documentación de [sampler states](https://docs.unity3d.com/2022.3/Documentation/Manual/SL-SamplerStates.html) no acredita inline samplers Vulkan en esta versión, por lo que no se presupone su compatibilidad. Los texels se direccionan como Repeat o Clamp por eje antes de remapearlos, incluyendo UV negativas y cruce del borde 0/1.

## Propuesta de programa reproducible

El plan tipado contiene dieciséis tiles: permutación de destino, una de ocho orientaciones, orden RGB y salts de bytes con selección de los cuatro parámetros runtime existentes. La derivación usa HMAC-SHA256 con un dominio TextureGuard propio, identidad/contenido de la fuente y un scope explícito de binding/material. La seed se mantiene en memoria Editor y no se serializa en el material ni en el programa público. Cambiar build, fuente o scope debe cambiar el programa. El plan se reproduce con la misma seed y entrada.

Encoding recorre texels de la fuente, aplica orientación, permutación RGB y XOR por tile con salts y parámetros runtime, y escribe una textura nueva. El shader emite el mapeo inverso del mismo programa. El payload conserva dimensiones y cuatro bytes por texel. En runtime se almacena el payload codificado; no se crea una textura CPU descifrada. `Apply(false, true)` retira la copia CPU del payload. Esa flag no impide extraer bytes del AssetBundle ni leerlos desde GPU.

## Seguridad y observabilidad

Tile permutation/XOR es ofuscación, no cifrado fuerte. Un análisis del shader junto con los valores runtime permite invertirla. La suite debe conservar un extractor independiente que reconstruya la fuente con esos datos y medir errores; no presentar diversidad como resistencia al extractor adaptativo.

Un AssetBundle del piloto debe contener el payload y los providers, y excluir la textura/material fuente. No debe guardar claves en materiales o documentos públicos. GPU conserva el payload codificado, pero el shader y las constantes permanecen observables y una captura propia puede reconstruir la imagen; Stage 19 deberá ampliar esta evaluación. Todas las capturas/análisis se limitan a Unity y aplicaciones propias, nunca al proceso de VRChat.

Android/Quest restringe los shaders de avatares a los incluidos por el SDK. Este piloto desktop no añade soporte Quest: [limitaciones oficiales de Android](https://creators.vrchat.com/platforms/android/quest-content-limitations/). Compilar un bundle Windows64 será una comprobación de backend; no demuestra ejecución en un cliente VRChat ni compatibilidad con su postprocesamiento.

## Criterios de Stage 11

1. Reproducción exacta de programa/payload con la misma entrada; diversidad entre seeds, scopes y fuentes.
2. Recuperación byte idéntica con un intérprete independiente de todos los tiles/orientaciones/órdenes, y fallo visual para claves ausentes, incorrectas o de otro programa.
3. Comparación GPU con lilToon original: Point/Bilinear, Repeat/Clamp por eje, bordes internos/externos, UV negativas, escalas/offsets, sRGB/linear y proyectos Gamma/Linear.
4. Preservación de archivos/importadores/fuentes, rechazo de mutaciones y rollback de salida propia tras un fallo tardío.
5. Bundle nativo reabierto en Vulkan: dependencies originales ausentes, payload lineal no legible y claves serializadas cero. Compilación Windows64 separada.
6. Reportar máximo/medio de diferencia visual, bytes y tamaño de bundle. Presupuesto provisional para PNG RGBA8: error medio ≤0,0015, máximo ≤0,012; Point debe ser mucho más preciso. No medir FPS desde `Camera.Render` del Editor ni atribuir rendimiento del player anterior al decoder de texturas.

## Próximas condiciones

Stage 12 deberá tratar por binding/slot los materiales y clips compartidos, animaciones de textura, providers distintos por plan y dependencias finales; después ampliar formatos/features con sus pruebas. Hasta entonces, TextureGuard será una API opt-in de investigación. El asistente conserva el pipeline productivo existente y sus manifests siguen reportando cero texturas protegidas.
