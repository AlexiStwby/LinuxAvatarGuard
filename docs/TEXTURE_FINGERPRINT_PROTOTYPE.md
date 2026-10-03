# Texture fingerprint — piloto de la etapa 16

Fecha: 2026-10-03. **Etapa parcial:** núcleo, reproducción y canales de imagen probados exclusivamente por CPU; adaptador Unity compilado, todavía sin ejecutar. Stages 15/16 siguen pendientes de validación nativa y visual. No se inició Unity, player o RenderDoc, se accedió a VRChat, se cargó Carukia ni se descargaron herramientas. El asistente y el unitypackage 0.2.1 mantienen la ruta estable.

## Alcance

[`GuardTextureFingerprintV1`](../Package/Assets/LinuxAvatarGuard/Editor/GuardTextureFingerprint.cs) marca una **copia** de albedo opaco y [`GuardTextureFingerprintUnity`](../Package/Assets/LinuxAvatarGuard/Editor/GuardTextureFingerprintUnity.cs) propone una copia independiente de textura/material lilToon. La marca busca una señal de trazabilidad en el albedo completo; no cifra datos ni impide extracción. Una coincidencia no prueba autoría, robo, fecha o exclusividad. No es un detector de screenshots de un avatar ni promete reconocer cualquier región de un atlas.

Es independiente de claves OSC, MeshGuard y TextureGuard. La identidad privada de 256 bits de Stage 14 conserva su schema; el componente usa un codeword de 128 bits por binding. Crear la identidad sigue significando `IdentityOnly`. El registro del componente se describe como `CpuSelfVerified`, y el artefacto de investigación como `PrototypeCpuSelfVerified`, nunca Ready o upload validado.

## Contrato y algoritmo

Algoritmo `DctRgb8TextureFingerprintV1`, versión 1. Entrada: RGBA8 **sRGB codificado**, alfa 255, cuadrada power-of-two de 128–1024. Mantiene tamaño, filas y alfa. Raster `BottomLeftRowMajorRgba8V1`, coherente con el orden de `GetPixels32` de Unity; no se confunden las filas con el origen superior de Pillow. Las normales, máscaras, transparencias, HDR y datos lineales no forman parte del piloto.

1. Capturar una copia del array fuente y su hash. Canonicalizar el proxy `0.2126 R + 0.7152 G + 0.0722 B` sobre RGB codificado normalizado a `[0,1]` en un frame 128×128. Es un **proxy de señal**, no luminancia física. La reducción usa promedio exacto del footprint; para candidatos menores que 128 se usa interpolación bilineal por centros, con bordes clamp.
2. Calcular DCT-II ortonormal separable. Pool de 624 frecuencias `(u,v)` con `0≤u,v≤34` y `3≤u+v≤34`; no se marca DC.
3. Barajar el pool Fisher–Yates con una secuencia HMAC-SHA256 del carrier seed. Dominio `UTF8("LAG/texture-fingerprint/dct-rgb8/v1/layout" + NUL) || uint32BE(counter)`. Consumir palabras uint32BE y rejection sampling para evitar sesgo del módulo. Los primeros 384 lugares forman tres carriers únicos por símbolo.
4. Dither independiente por carrier: HMAC-SHA256 con dominio `UTF8("LAG/texture-fingerprint/dct-rgb8/v1/dither" + NUL) || uint32BE(carrierIndex)`. Los 53 bits altos de los primeros ocho bytes forman una fracción; `d = fraction × 2 × q`. Codeword MSB primero.
5. Para coeficiente `c`, bit `b`, paso `q` y dither `d`, escribir `q × (2 × roundEven(((c-d)/q-b)/2) + b) + d`. Invertir DCT; replicar el residual de cada celda del frame sobre su bloque de autoría y sumarlo por igual a R/G/B. Clamp y round-even a RGB8. Esta versión no interpola el residual ni redimensiona la salida.
6. Medir el error del RGB8 final y observarlo de nuevo. Rechazar cualquier resultado fuera del presupuesto, sin cambio real o con <96 símbolos utilizables o cualquier símbolo utilizable incorrecto. La fuente permanece intacta. No repetir con más fuerza para obtener éxito.

Presupuestos inampliables: RGB absoluto medio ≤**0,0015**, máximo ≤**0,012**, PSNR RGB codificado ≥**45 dB**. Paso por defecto **0,018**, intervalo permitido `[0.004, 0.018]`. El llamante puede endurecer los presupuestos. Reducir el paso puede impedir la autoverificación; no se garantiza una marca con cualquier configuración admitida. Calidad numérica no equivale a invisibilidad, especialmente en atlas, superficies planas, UV seams o materiales con correcciones fuertes.

`PixelHash` usa framing `BinaryWriter`: dominio, política de color/raster, width/height, longitud y bytes RGBA. El registro privado incluye algoritmo/schema, estado/color/raster, build/binding, hashes, dimensiones, cobertura y presupuestos, todos autenticados con HMAC-SHA256. Restaurar exige contexto compatible, fuente exacta y reproducción idéntica del registro. Esta selección HMAC versionada no es intercambiable con los carriers PCG del estudio de Stage 14.

## Observador y límites

El observador carga el registro/contexto y analiza arrays elegidos por el llamante; no solicita GUID o fichero original del candidato. Exige RGBA8 opaco cuadrado, máximo 2048. Debajo de 32×32 se abstiene; entradas inválidas se rechazan. Proyecta el candidato al frame y extrae paridades.

Excluye carriers a menos de 0,06 pasos de una frontera de decisión de media unidad. Un símbolo necesita ≥2 carriers utilizables y voto sin empate. Con <96 símbolos devuelve `InconclusiveCoverage`; si hay cobertura usa acuerdo exploratorio ≥`109/128` entre los símbolos disponibles para `ResearchMatch`. `Confidence=null`, `Calibrated=false`. El gate del encoder exige coincidencia de todos los símbolos utilizables y es más estricto que este umbral.

El umbral no estima probabilidad de autoría. Los controles comparten tres fuentes y están correlacionados; cero coincidencias observadas no establece una FPR universal. Promediar copias reconoce a veces varios contribuyentes, por lo que el futuro verificador debe informar ambigüedad. Una ausencia tampoco prueba que el asset nunca haya sido marcado. Stage 17 necesita datos separados, controles de bases compartidas y calibración por consulta completa.

## Adaptador Unity compilado, sin ejecución

`GuardTextureFingerprintSource.Capture(material, scopeHex64)` requiere Unity 2022.3.22f1 Linux/Vulkan Built-in en Edit Mode, material persistente/guardado con shader `lilToon`, tag Opaque y sin keywords. `_MainTex` debe ser Texture2D persistente original, legible sRGB RGB24/RGBA32 de 128–1024, opaca, sin compresión/mips/streaming, Point/Bilinear y wrap Repeat/Clamp con anisotropía ≤1. El importador, si es TextureImporter, debe ser Default sRGB sin compresión/mips/streaming; **no se cambia automáticamente**. Se rechazan refs del mismo albedo en otras propiedades. No se admiten fuentes generadas ni rutas con enlaces.

La captura conserva GUID/local ID del material/textura, scope, píxeles y metadata/sampler/importador/dependencias, propiedades del material y hashes del archivo fuente/`.meta`. Revalida fuente, snapshot y archivos durante la preparación; los cambios del usuario se preservan y hacen fallar la operación.

`Prepare` acepta únicamente carpeta **nueva** `Assets/LinuxAvatarGuardGenerated/Research/FingerprintTexture/<nombre>` y respaldo absoluto `.lagprivate` fuera de Git/Unity, con directorio existente 0700. Reutiliza el store Linux con archivos 0600, descriptors, creación sin sobrescribir, commit verificado y rollback de Stage 15. Antes de crear assets, escribe el respaldo y exige leerlo, restaurar el contexto y reproducir la marca desde su JSON tipado.

Después crea Texture2D RGBA32 sRGB del mismo tamaño, copia sampler/bias/flags, escribe píxeles y clona el material cambiando exclusivamente la referencia `_MainTex` y su nombre generado. Compara las propiedades efectivas normalizando solo nombre, flags y referencia albedo en un clon temporal. Guarda únicamente los assets nuevos, reimporta y comprueba píxeles/estado/material/detección. El manifiesto público incluye cantidades, política y métricas, sin identidad, seeds, codeword, fuente original ni ruta privada. No asigna el material a un renderer, escena, prefab, Animator o perfil; no modifica shaders ni marca Ready.

Los checkpoints `PrivateSaved`, `CopiesSaved` y `ManifestSaved` permiten inyectar fallos futuros. El rollback solo retira carpeta/registro nuevos intactos; preserva archivos añadidos/editados/sustituidos y cambios de copias en memoria e informa fallos compuestos. Pueden quedar padres vacíos. La implementación de estos controles **aún no se ejecutó en Unity** y no cubre toda intercalación hostil de otro proceso del mismo UID.

Unity documenta que [`GetPixels32`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2D.GetPixels32.html) lee la copia CPU en filas desde abajo a la izquierda. Se escribe la copia mediante [`SetPixels32`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2D.SetPixels32.html). [`Apply`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2D.Apply.html) sube los cambios a GPU; esa llamada existe en el adaptador para su ejecución futura y **no se invoca en las pruebas actuales**. Compilar contra assemblies instalados no comprueba Gamma/Linear, sampler, shader ni resultado del SDK.

## Evidencia por CPU

[`LAGTextureFingerprintCpu.cs`](../Tests/Fingerprint/LAGTextureFingerprintCpu.cs): **187 comprobaciones**. Tres imágenes propias de 128×128 —gradiente/ondas, tejido procedural y parches— con cuatro contextos/bindings: doce builds. Se añaden imágenes de gradiente de 256, 512 y 1024: **15 vectores nativos**. Comprueba fuente/alfa/dimensiones, determinismo, restauración, calidad, observación, registros alterados, identidad ajena, límites, entradas inválidas, mutaciones y disposal.

Mínimo PSNR entre los 15: **54,512741627 dB**. Mayor error RGB absoluto medio: **0,0009006874234**; máximo: **0,007843137255** (dos niveles de RGB8). Todos conservan 128/128 símbolos y las fuentes. Las resoluciones mayores solo incluyen una familia/un contexto: no representan todos los albedos reales.

[`verify_texture_cpu.py`](../Tests/Fingerprint/verify_texture_cpu.py) implementa por separado selección HMAC, dither, DCT, reconstrucción, hashes, autenticación y observación. Reproduce los **15 RGB8 byte a byte** y sus registros; C# y Python coinciden en **1.086 observaciones**: 240 positivos de doce builds, 60 positivos de tres tamaños mayores, 780 negativos y seis de promedios entre dos copias. No es un verificador de producción.

| Canal CPU sobre las 12 imágenes de 128×128 | Coincidencias exploratorias | Abstenciones |
| --- | ---: | ---: |
| Nativo / PNG / JPEG95, por caso | 12/12 | 0 |
| JPEG75 sin subsampling / 4:2:0 | 4/12 / 2/12 | 0 |
| DDS BC1 / BC3 de Pillow, por caso | 4/12 | 0 |
| Resize 75% / 50%, por caso | 12/12 | 0 |
| Resize 25% | 7/12 | 0 |
| Ganancia 1,03 + offset 0,01; gamma 1,05 / 1,2; blur 0,5, por caso | 12/12 | 0 |
| Primer mip de promedio RGB codificado / luz lineal, por caso | 12/12 | 0 |
| Segundo mip de promedio RGB codificado / luz lineal | 7/12 / 5/12 | 0 |
| Recorte de cuatro píxeles por borde | 0/12 | 1 |
| Rotación 90° | 0/12 | 0 |

Los tres tamaños mayores conservan la marca en 3/3 por canal probado salvo rotación (0/3), incluido recorte de cuatro píxeles. Esto muestra dependencia del contenido/tamaño; no se extiende el éxito de esos tres gradientes a todas las compresiones o crops.

Los 780 controles incluyen otro build de la misma fuente bajo cada transformación, imágenes sin marcar y otras fuentes. **Cero coincidencias exploratorias**, sin FPR poblacional estimada. En **3/3 promedios** de dos copias se reconocen ambos contribuyentes. No elegir un único propietario por mayor score.

Se usan NumPy/Pillow ya instalados, un hilo numérico y `nice -n 19`. [Pillow documenta la lectura/escritura DDS DXT1/DXT5](https://pillow.readthedocs.io/en/stable/handbook/image-file-formats.html#dds); el estudio hace encode/decode BC1/BC3 por CPU con ese encoder, **no el encoder de Unity/Crunch**. Los mips son simulaciones mediante promedio 2×2, con y sin transferencia sRGB; no se genera una pirámide nativa ni se presupone equivalencia con ella. Se conserva la versión y hash del plugin DDS en el informe.

Evidencias ignoradas por Git: `evidence/texture-fingerprint/cpu-final/` contiene raw RGBA8, vectores sintéticos, seis PNG calculadas por CPU, archivo de consultas, observaciones, medidas, logs y manifest de hashes. Root `RESULTS.json`, `WORK_STATE.json` y `manifest.json` conservan el checkpoint. Las imágenes **no son screenshots de Unity/VRChat ni del avatar comercial**.

## Reproducción y gates pendientes

Usar Unity ya instalado y NumPy/Pillow existentes con escritura DDS disponible; el runner no instala dependencias. Desde el repositorio:

```bash
nice -n 19 python3 Tests/Fingerprint/run_texture_cpu.py \
  --unity-data /ruta/Unity/2022.3.22f1/Editor/Data \
  --output /ruta/nueva/lag-texture-cpu
```

La salida debe ser nueva y estar fuera de Assets/Packages. Los logs y `run-manifest.json` distinguen ejecución matemática de compilación aislada: `unityAdapterExecuted=false`, `gpuUsed=false`, `sdkValidated=false`.

Para cerrar la etapa: ejecutar el adaptador en proyecto desechable, validar material/sampler/color en Gamma y Linear con lilToon, píxeles después de guardar/importar, replay JSON y fallos en checkpoints preservando mutaciones. Evaluar atlas/UV seams, fuentes reales, BC/Crunch/mips nativos, mip límites y filtros. Integrar marca **antes** de TextureGuard y antes de generar la pirámide; comprobar capas combinadas, metadata, swaps animados, dependencias y artefacto del SDK. No habilitar el asistente por PSNR o éxito sintético.

Stage 17 continúa con verificador/calibración por CPU; las validaciones gráficas pendientes de Stages 15/16 se realizarán únicamente en aplicaciones propias de Unity cuando esté permitido. No analizar el proceso de VRChat. UI, regresión de avatares y exportación de un nuevo unitypackage permanecen posteriores a esos gates.
