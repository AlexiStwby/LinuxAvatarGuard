# Mesh fingerprint — piloto de la etapa 15

Fecha: 2026-10-03. **Etapa parcial:** núcleo matemático y almacenamiento privado probados por CPU; adaptador Unity compilado, sin ejecución nativa, validación visual ni GPU. El asistente y el unitypackage 0.2.1 conservan su comportamiento. No se cargó Carukia ni se modificó su proyecto.

## Qué aporta

[`GuardMeshFingerprintV1`](../Package/Assets/LinuxAvatarGuard/Editor/GuardMeshFingerprint.cs) incorpora una marca geométrica experimental a arrays XYZ Float32 y [`GuardMeshFingerprintUnity`](../Package/Assets/LinuxAvatarGuard/Editor/GuardMeshFingerprintUnity.cs) proporciona una API explícita para preparar una **copia de una mesh estática**. La marca intenta reconocer una copia transformada. No cifra la geometría, no bloquea extracción, no sustituye MeshGuard/TextureGuard y no demuestra autoría o robo. No se activa automáticamente al preparar un avatar.

El contexto de la etapa 14 conserva su schema y estado inicial `IdentityOnly`. La identidad de 256 bits y el seed son privados e independientes de OSC. Para cada binding, el contexto deriva un codeword de 128 bits y un carrier seed. El respaldo del piloto añade un registro de algoritmo, presupuesto y hashes autenticado con HMAC. Un resultado se describe como `CpuSelfVerified` o `PrototypeCpuSelfVerified`; no se convierte en `Ready`, perfil de seguridad o validación de upload.

## Algoritmo versionado

`RadialUniqueMeshFingerprintV1`, versión 1, reemplaza el candidato radial del harness de investigación con un contrato explícito de Float32 y seams. No se presupone que sus marcas sean intercambiables con las de `research.py`.

1. Validar XYZ finitos: máximo 262.144 vértices y coordenadas de autoría en ±16 unidades de objeto. Agrupar posiciones exactamente coincidentes, ordenar lexicográficamente y calcular el centro con suma compensada sobre posiciones únicas. No se fusionan índices ni se altera el array fuente.
2. Normalizar radios por la mayor distancia al centro. Dividir el intervalo radial `[0.1, 0.98)` en 128 bandas; los radios fuera de ese intervalo no transportan símbolos. Cada banda necesita al menos cuatro posiciones únicas. Se exigen ≥96 bandas antes de preparar la marca.
3. Derivar un dither por banda con HMAC-SHA256 del carrier seed y `UTF8("LAG/mesh-fingerprint/radial-unique/v1/dither" + NUL) || uint32BE(band)`. Los 53 bits altos de los primeros ocho bytes forman una fracción; el dither vale `fraction × 2 × step`. Codeword leído MSB primero.
4. Para radio normalizado `r`, dither `d`, paso `q` y bit `b`, calcular `q × (2 × roundEven(((r-d)/q-b)/2) + b) + d`. Mover radialmente solo si el destino conserva su banda. Todos los duplicados reciben las mismas coordenadas nuevas, sin abrir una unión coincidente.
5. Convertir el resultado a Float32 y volver a normalizarlo desde cero. Rechazar si se fusionaron posiciones diferentes, se excedió un presupuesto o no coinciden **todos** los símbolos utilizables, con mínimo de 96. No aumentar fuerza ni rebajar el gate para obtener un resultado.

El paso relativo por defecto es `1e-5`; solo admite `[1e-8, 1e-5]`. El desplazamiento de cada vértice, medido después de redondear a Float32, debe ser ≤`min(1e-5 × diagonalFuente, 5e-5 unidadesObjeto)`. El llamante puede reducir los límites, nunca ampliarlos. No son metros garantizados: la escala de un renderer exige su evaluación específica. Una fuente con poca cobertura radial se rechaza incluso si visualmente parece adecuada.

`PositionHash` incluye el dominio de versión, longitud del array y valores Float32 little endian con framing `BinaryWriter`. El registro privado incluye hashes de fuente/marca, identidad de binding/build, cantidades y presupuestos; todos esos campos se autentican con HMAC-SHA256. Restaurar verifica schema, algoritmo, límites, identidad, autenticación y reproducción exacta. Cambios de la fuente se preservan y se rechazan; no se intentan revertir.

## Observación y abstención

El observador se restaura desde el registro privado y contexto. No necesita GUID, índices originales o archivo fuente del candidato. Repite deduplicación/normalización, vota paridades por banda y excluye bandas con menos de cuatro muestras o empate. Clasifica `InconclusiveCoverage` con <96 símbolos; por encima usa el umbral exploratorio de acuerdo `109/128`. Ese umbral se aplica a la fracción de símbolos utilizables, no obliga a que haya 109 símbolos disponibles.

`ResearchMatch` es una coincidencia exploratoria. `Confidence` permanece `null` y `Calibrated=false`: el umbral y las muestras no permiten una probabilidad de autoría. La autoverificación del encoder es más estricta que este umbral. La etapa 17 debe estudiar controles poblacionales, múltiples comparaciones y copias combinadas antes de habilitar un verificador público. Los límites de recorte, deformación y ambigüedad de la etapa 14 siguen vigentes hasta una nueva medición del algoritmo concreto.

## Adaptador Unity escrito, pendiente de ejecutar

La API exige Unity **2022.3.22f1, Linux Editor, Vulkan, Built-in y Edit Mode**. La comprobación de plataforma se ejecutará cuando se invoque; compilar el archivo no inicia Unity ni utiliza su dispositivo gráfico.

`GuardMeshFingerprintSource.Capture(mesh, scopeHex64)` acepta una fuente persistente, guardada y legible, sin bindposes, atributos de huesos ni blendshapes. Comprueba formato Float32 de posiciones/normales/tangentes/UV, admite color Float32 o UNorm8, atributos finitos, normales/tangentes de longitud compatible, triángulos e índices/bounds válidos. El scope explícito separa bindings de una misma mesh. Captura GUID/local ID, hashes semánticos de todos los atributos/topología/bounds y hashes del archivo fuente y `.meta`. No admite fuentes del directorio generado ni rutas con enlaces.

`Prepare(source, context, outputFolder, privatePath, options, checkpoint)` realiza estos pasos propuestos:

- Planificar, codificar y comprobar los triángulos antes de crear assets. Rechazar triángulos degenerados, cambio de área >1% o giro de normal de cara >0,25°. Estos límites locales requieren pruebas nativas y no equivalen a validación visual.
- Crear el respaldo privado con lease y autoverificación. La fuente se vuelve a revisar en cada checkpoint.
- Clonar la mesh y cambiar posiciones con el mismo número de vértices. Conservar normales/tangentes, UV, colores, índices y layout semántico, sin recalcularlos. Ampliar bounds globales y de cada submesh por el máximo desplazamiento medido más margen.
- Guardar únicamente la copia, reimportarla y comprobar posiciones, atributos, topología, bounds y detección. Generar un manifiesto público sin identidad, seeds, codeword, ruta privada ni GUID de la fuente.
- Verificar archivos generados, fuente y copia antes del commit privado. Ante fallos, retirar solo la carpeta y el registro nuevos si siguen siendo propios e intactos. Conservar archivos añadidos/editados/sustituidos y ediciones de la copia en memoria, e informar errores de cleanup. Pueden quedar directorios padre vacíos.

La carpeta nueva debe cumplir `Assets/LinuxAvatarGuardGenerated/Research/FingerprintMesh/<nombre>`; `<nombre>` tiene 1–64 letras ASCII, números, guiones o guiones bajos. Nunca se sobrescribe una carpeta previa. La API devuelve mesh/carpeta/resumen y no modifica un renderer, prefab, escena, material, shader, Animator o perfil. `ReadPrivateBundle` valida el respaldo contra la fuente y comprueba reproducción con el núcleo.

Unity devuelve una copia de las posiciones; cambiar el número de vértices puede redimensionar otros atributos, por eso se conserva el contador y se auditan los atributos. [`Mesh.vertices`, Unity 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Mesh-vertices.html). Se inspecciona el layout mediante [`GetVertexAttributes`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Mesh.GetVertexAttributes.html), y se escriben descriptores/bounds por submesh mediante [`SetSubMesh`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Mesh.SetSubMesh.html). Se usa [`SaveAssetIfDirty`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.SaveAssetIfDirty.html) para guardar el asset indicado. Son decisiones de implementación; todavía falta comprobar el resultado en el Editor y después del SDK. La auditoría semántica no certifica buffers GPU byte a byte.

## Respaldo Linux por CPU

[`GuardFingerprintPrivateStore`](../Package/Assets/LinuxAvatarGuard/Editor/GuardFingerprintPrivateStore.cs) es un store independiente de Unity, basado en libc y `statx`. Exige ruta absoluta terminada en `.lagprivate`, fuera de Assets/Packages, Git y proyectos Unity. Todos los directorios padre deben existir y no ser enlaces; el directorio privado final debe pertenecer al usuario con modo 0700. No crea padres ni cambia permisos de directorios existentes.

El archivo nace con 0600, se publica mediante enlace atómico sin sobrescribir y se sincronizan archivo/directorio. La lectura exige archivo regular del usuario, 0600, un único hardlink, UTF-8 válido y tamaño ≤1 MiB. FIFO, enlaces, permisos públicos y registros excesivos se rechazan. El lease mantiene descriptors de la carpeta y del archivo original. Commit verifica ubicación, inode y contenido; rollback solo retira su archivo intacto, preservando modificaciones o reemplazos. No es un aislamiento frente a otro proceso del mismo UID/root; no promete cubrir toda intercalación hostil o pérdida de energía.

El formato contiene secretos en JSON privado. Los arrays internos se limpian al terminar; los strings y copias del llamante no tienen borrado garantizado. No imprimir el respaldo, subirlo a Git/BOOTH ni importarlo a Unity. Esta API no proporciona selección de ubicación ni copia de seguridad al usuario: ambas forman parte de la futura integración del producto. Las pruebas usan fixtures temporales propias bajo `/var/tmp`, que se eliminan; esa ruta no se recomienda para respaldo persistente de un avatar.

## Pruebas realizadas sin gráficos

[`run_mesh_cpu.py`](../Tests/Fingerprint/run_mesh_cpu.py) usa únicamente Mono/C# instalados y la biblioteca estándar de Python, con `nice -n 19`. Compila hosts independientes y ejecuta el núcleo/IO; compila por separado el adaptador contra assemblies instalados de Unity. No ejecuta esos assemblies, el Editor o un player. No se descargaron herramientas ni se accedió a VRChat. No constituye una compilación global del proyecto Unity.

**Núcleo: 119 comprobaciones y 32 builds.** Fuente propia de 4.102 posiciones antipodales, repartidas por bandas, con anclas fuera del carrier. Se comprueban conservación de la fuente, contador, determinismo/restauración, registro modificado/contexto ajeno, cobertura, presupuestos, entradas inválidas, mutaciones y disposal. Treinta y dos identidades sobre la misma geometría generan hashes distintos y conservan 128/128 símbolos.

| Candidato sintético, build 0 | Símbolos utilizables | Coincidencias | Resultado exploratorio |
| --- | ---: | ---: | --- |
| Float32 nativo | 128 | 128 | Coincide |
| Vértices reordenados | 128 | 128 | Coincide |
| 1.200 duplicados, 900 de una misma posición | 128 | 128 | Coincide; no sesga el centro |
| Giro Z de 0,93 rad + escala 2,37 + traslación | 128 | 128 | Coincide |
| Reflexión global | 128 | 128 | Coincide |
| Fuente sin marca | 23 | 16 | Abstención por cobertura |
| Escala Y ×1,02 | 122 | 62 | No coincide |
| Ruido uniforme ±4×10⁻⁵ | 110 | 57 | No coincide |

Entre los 32 builds, máximo desplazamiento **1,002443986×10⁻⁵ unidades de objeto** y máximo relativo a diagonal **2,893806527×10⁻⁶**. Candidatos escasos/degenerados se abstienen. Los duplicados de la fuente reciben exactamente las mismas coordenadas marcadas, sin aumentar el peso del punto en el frame. La fixture no contiene caras: no demuestra la conservación de triángulos, normales o apariencia en Unity.

**IO: 61 comprobaciones.** Round-trip UTF-8, frontera de 1 MiB, permisos, no sobrescritura, rechazo de enlaces/FIFO/rutas inseguras, rollback propio, commit ante edición/reemplazo de archivo o carpeta, preservación de modificaciones y ciclo de vida de descriptors en éxitos/fallos.

[`verify_mesh_cpu.py`](../Tests/Fingerprint/verify_mesh_cpu.py) implementa por separado el framing/hash, derivación HMAC, dither y extracción del build sintético público. Coincide con C# en resultados nativos/fuente y hashes; verifica reorder, duplicados y reflexión. Lee binarios XYZ Float32, **no FBX**. No usa claves de un avatar.

Evidencias locales: `evidence/mesh-fingerprint/cpu-final/` contiene logs, informes, binarios XYZ y manifest con comandos/hashes de fuentes/herramientas. `RESULTS.json`, `WORK_STATE.json` y `manifest.json` en la raíz mantienen el checkpoint. Son evidencias ignoradas por Git; no estarán en un checkout público y no son screenshots de Unity.

## Reproducción

Usar un Unity ya instalado con la versión indicada. No hace falta abrirlo:

```bash
nice -n 19 python3 Tests/Fingerprint/run_mesh_cpu.py \
  --unity-data /ruta/Unity/2022.3.22f1/Editor/Data \
  --output /ruta/nueva/lag-mesh-cpu
```

La salida debe ser nueva y estar fuera de Assets/Packages. `run-manifest.json` distingue ejecución del núcleo de compilación del adaptador y deja `unityAdapterExecuted=false`, `gpuUsed=false`, `sdkValidated=false`.

## Condiciones pendientes para cerrar la etapa 15

1. Ejecutar el adaptador en proyecto Unity desechable con mesh propia triangulada, seams, varias submeshes y layouts admitidos. Comprobar atributos/bounds después de guardar/reimportar y conservación exacta de la fuente.
2. Inyectar fallos en los tres checkpoints y en IO/importación. Probar mutaciones tardías de fuente, copia, carpeta y respaldo; preservar modificaciones y no presentar una preparación parcial como lista.
3. Comparar original/copia con lilToon en Unity Linux/Vulkan, medir diferencias visuales y coste. Solo aplicaciones propias, cuando se puedan ejecutar sin interferir con la sesión VR.
4. Medir exportación/importación FBX, welding, cuantización y SDK. Ninguna supervivencia se presupone a partir del array Float32.
5. Diseñar y validar por separado skinning, blendshapes/morphs, renderer scale y coordinación con los codecs/otras capas antes de aplicar la marca a un avatar animado.

La etapa 16 puede continuar con el núcleo de albedo por CPU. La etapa 17 debe calibrar el verificador. La integración en el asistente, regresión con avatares y exportación de otro unitypackage vendrán después de esos gates.
