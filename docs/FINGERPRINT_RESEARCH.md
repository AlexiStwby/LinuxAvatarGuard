# FingerprintGuard — investigación y base de identidad

Fecha: 2026-10-03. Stage 14 completado como investigación. Implementación productiva de marcas/verificador: Stages 15–17. Preparación del producto: Linux, Unity 2022.3.22f1, Vulkan, Built-in, lilToon 2.3.4. La evaluación de esta etapa se ejecutó íntegramente por CPU, sin arrancar Unity, utilizar GPU ni acceder a VRChat.

## Resultado de la decisión

Se implementó una identidad privada independiente y se evaluaron dos candidatos numéricos. El marcador por índice de vértice se descarta. Las bandas radiales se conservan para un piloto estático que pueda rechazar geometrías sin cobertura suficiente. DCT/QIM se conserva para un piloto de albedo opaco. Ninguno se activa en el asistente; crear una identidad no significa haber marcado un avatar.

La huella identifica un build potencialmente relacionado con una copia. Un hash de contenido sirve para integridad y búsqueda exacta; una marca embebida busca conservar señales cuando cambia el archivo. Detectar una marca no demuestra propiedad, falta de autorización ni quién realizó una extracción. Una marca puede ser borrada, modificada o trasladada. Los resultados de esta investigación no prometen impedir ripping ni recuperar cualquier captura GPU.

## Fuentes y elección de candidatos

Chen y Wornell estudian QIM y el compromiso entre información embebida, distorsión y resistencia. Esa relación motiva medir conjuntamente error y supervivencia; sus garantías dependen de modelos del canal y no se trasladan automáticamente a un avatar. Referencia primaria: [Quantization Index Modulation, IEEE 2001, copia del grupo de investigación del MIT](https://dsp-group.mit.edu/wp-content/uploads/2024/11/QuantizationIndexMod.pdf).

Hamidi y colaboradores estudian normas de vértices y selección por saliencia. Su artículo diferencia cambios de conectividad y cambios geométricos. Nuestra baseline radial usa bandas y dither propios; no reproduce su algoritmo, selección de saliencia ni resultados. Referencia primaria: [Blind Robust 3-D Mesh Watermarking based on Mesh Saliency and QIM, 2019](https://arxiv.org/html/1910.12828).

Spread spectrum y modelado perceptual son otras líneas candidatas. El [catálogo del autor Ingemar Cox](https://ingemarcox.cs.ucl.ac.uk/) identifica los trabajos originales. Esta etapa no implementa esos artículos ni copia código de terceros. Las marcas de baja visibilidad y la geometría interna requieren validar culling, exportadores y coste antes de considerarlas una capa adicional.

Para textura, compresión, sRGB, legibilidad y mipmaps deben conservar sus contratos de importación. [TextureImporter de Unity 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/TextureImporter.html) describe esas propiedades. JPEG por CPU es un canal de prueba distinto de BCn/Crunch de Unity.

| Candidato | Riesgo principal | Decisión |
| --- | --- | --- |
| Nombre, GUID, hash exacto o canal UV aislado | Renombrado, exportación o eliminación del canal. | Referencia/integridad, sin tratarlo como marca geométrica resistente. |
| QIM ligado al índice del vértice | Cambia la relación entre símbolo y vértice al reordenar. | Descartado tras el control CPU. |
| QIM en radios normalizados por componente | Cobertura desigual; centro y escala cambian con recorte, duplicación y deformación. | Piloto estático limitado, con abstención y autoverificación. |
| Relaciones locales/saliencia y referencia privada | Correspondencias ambiguas, coste de registro, simetría y cambios de topología. | Investigación posterior al piloto; no asumir invariancia. |
| QIM en frecuencias del albedo | Compresión, recorte y cambios de señal pueden alterar símbolos. | Piloto de albedo opaco; medir los canales reales de Unity. |
| Geometría interna/canary | Puede eliminarse sin alterar la superficie visible. | Señal auxiliar de Stage 18, separada de la huella principal. |

## Identidad privada implementada

[`GuardFingerprintContext`](../Package/Assets/LinuxAvatarGuard/Editor/GuardFingerprintContext.cs) es una API managed de investigación sin llamadas a Unity, IO ni gráficos. Usa dos extracciones independientes del CSPRNG: identidad de 256 bits y seed de carriers de 256 bits. El BuildID existente se pasa explícitamente; las claves OSC, la master seed del codec y los formatos anteriores no se modifican.

Para cada binding original se derivan una seed de 32 bytes y un codeword de 16 bytes. Un codeword de componente de 128 bits no debe confundirse con la identidad de build de 256 bits. Mesh/texture y carrier/codeword tienen dominios diferentes. Framing exacto:

```text
header = UTF8("LAG/fingerprint/v1/" + purpose + NUL + channel + NUL
              + buildId + NUL + bindingId + NUL)
output = HMAC-SHA256(carrierSeed, header || rawFingerprintBytes)
carrier = output de propósito carrier, completo
codeword = primeros 16 bytes del output de propósito codeword
bits = orden MSB primero
```

BuildID: 32 caracteres hexadecimales minúsculos. BindingID: 64 caracteres hexadecimales minúsculos. Es una identidad de la fuente/binding registrada antes de marcar: el futuro detector la obtiene del registro privado, aunque los GUID, nombres o hashes del archivo sospechoso hayan cambiado. No puede exigir que el sospechoso conserve su GUID de Unity.

APIs presentes: `CreateRandom`, `CreateReproducible`, `DeriveCarrierSeed`, `DeriveCodeword`, `ExportPrivateRecord`, `RestorePrivateRecord`, `PublicSummary`, `Dispose`. El DTO privado schema 1 exige tipo, estado `IdentityOnly`, timestamp UTC, secretos Base64 canónicos de 32 bytes y el BuildID esperado. Restaurar una versión/estado desconocido falla. Las arrays se copian al entrar y se limpian al disponer; las strings del DTO exportado y copias del caller no tienen borrado garantizado.

El resumen público contiene exclusivamente schema, estado, BuildID, contadores inicialmente cero y `verifierCalibrated=false`. No incluye un compromiso/hash público de la identidad, seeds, codewords, rutas ni referencias de fuente. El contexto **no guarda automáticamente** registros, modifica assets, declara el avatar Ready ni añade una huella al manifest productivo.

[`FINGERPRINT_PROTOCOL_V1.json`](FINGERPRINT_PROTOCOL_V1.json) fija este contrato y las condiciones de los pilotos. Es una especificación de investigación, no un manifest de un avatar protegido ni un esquema nuevo de claves OSC. El sampler PCG64 de la simulación no fija todavía un generador de carriers productivo portable.

## Almacenamiento de la investigación

El harness guarda sus identidades sintéticas en un directorio privado configurado fuera de proyectos Unity y de repositorios Git: `private-data-root/linux-avatar-guard/fingerprint-research/<runId>/fingerprint-private.json`. Por defecto usa XDG; `--private-data-root` permite indicar un directorio existente. Se recorren los directorios por descriptor con `O_NOFOLLOW`, se crean con 0700 y los archivos con 0600, y no se sobrescribe un RunID existente. Se rechazan registros con symlinks/hardlinks, dueño ajeno, permisos públicos, tipos no regulares, tamaño excesivo o schema desconocido.

En esta máquina el HOME contiene un marcador Git. El store lo rechazó correctamente. Para **las fixtures de investigación**, se configuró `/var/tmp/lag-fingerprint-cpu-1000`, con 0700. Ese espacio puede ser limpiado por el sistema: no se prescribe para respaldos de avatares reales. Los registros sintéticos iniciales se trasladaron fuera del repositorio y las evidencias solo conservan referencias de RunID. La integración productiva deberá proporcionar almacenamiento privado persistente y respaldo transaccional antes de marcar builds reales.

Este almacenamiento y la derivación no constituyen un sandbox frente a otro proceso del mismo usuario. Tampoco autentican por sí solos la fecha o la autoría de un registro local. No se transmiten registros a servicios externos.

## Experimento CPU ejecutado

[`research.py`](../Tests/Fingerprint/research.py) usa NumPy y Pillow ya instalados. OpenBLAS/OMP/MKL se limitan a un hilo y el proceso se lanza con `nice -n 19`. No se descargaron herramientas. Hay tres familias propias: nube antipodal, elipsoide antipodal y nube asimétrica; cada una tiene cuatro identidades independientes. La geometría es una nube de posiciones, **sin topología**, huesos ni normales. Las tres imágenes RGB de 128×128 son albedos procedurales. No se leyó Carukia ni se modificó el proyecto de trabajo.

Baseline radial: centro aritmético, radio máximo, 128 bandas y step `10⁻⁵` respecto del radio. Observación por voto de las muestras de cada banda, con mínimo de cuatro y exclusión de empates. Baseline de textura: DCT ortonormal de un proxy de luminancia sobre RGB codificado, 384 frecuencias seleccionadas, tres carriers por bit y step 0,018. La marca se cuantiza finalmente a RGB8. No se llama a esta operación luminancia física ni se deduce calidad de lilToon a partir de ella.

Se fija **antes de medir** un acuerdo de al menos 109/128 y cobertura mínima 96/128. La salida conserva `confidence=null`: `ResearchMatch`, `NoResearchMatch` o `InconclusiveCoverage`. Es una regla exploratoria; no es un verificador calibrado.

| Comprobación | Resultado |
| --- | --- |
| Contexto C# standalone, sin assemblies Unity | **34 comprobaciones correctas**; compilador/Mono ya instalados. |
| Derivación entre C# y Python | Dos vectores fijos independientes idénticos. |
| IO privado por CPU | **17 tests correctos**, incluyendo permisos, Git/Unity, enlaces, FIFO, límites y no sobrescritura. |
| Trials | 12; 132 observaciones positivas de geometría y 156 de textura. |
| Controles negativos | 1.092 de geometría y 1.284 de textura; cero coincidencias al umbral exploratorio. |
| Preservación | Arrays de fuentes byte idénticas en todos los trials. |
| Reproducción | Mediciones deterministas idénticas desde el mismo snapshot privado. |

Los controles incluyen identidades incorrectas para cada transformación, fuente original sin marcar, otro build de la **misma** fuente y otra fuente. Las observaciones comparten tres fuentes y transformaciones: son correlacionadas. Cero coincidencias en esa matriz no estima una tasa de falsos positivos poblacional ni demuestra unicidad criptográfica del detector.

| Transformación de geometría | Coincidencias / trials | Inconclusos por cobertura |
| --- | --- | --- |
| Sin transformación | 8/12 | 4 |
| Reordenar vértices | 8/12 | 4 |
| Rotación + traslación + escala uniforme | 8/12 | 4 |
| Round-trip Float32 | 8/12 | 4 |
| Cuantización a 10⁻⁵ unidades | 8/12 | 4 |
| Ruido σ = 10⁻⁶ unidades | 8/12 | 4 |
| Ruido σ = 2×10⁻⁵ unidades | 0/12 | 4 |
| Escala no uniforme, eliminación 20% o deformación sintética | 0/12 por caso | 4 por caso |
| Promedio de dos copias marcadas | 8/12 | 4 |

En la nube asimétrica solo son utilizables 81/128 bits (≈63,3%); el sistema se abstiene incluso con 100% de acuerdo entre los observados. No se relaja el mínimo para declarar éxito. El marcador ligado al índice detecta su copia intacta 12/12 y, después del reorder, 0/12: control de fragilidad conservado. La deformación sintética no es una prueba de skinning. Máximo desplazamiento del candidato radial: **4,317×10⁻⁶** respecto de la diagonal original. No se ha demostrado imperceptibilidad con un render.

| Transformación de textura | Coincidencias / trials |
| --- | --- |
| Intacta / PNG | 12/12 por caso |
| JPEG calidad 95, sin subsampling | 12/12 |
| JPEG calidad 75, con y sin 4:2:0 | 1/12 por caso |
| Resize 0,75 y 0,5 | 12/12 por caso |
| Ganancia 1,03 + offset 0,01; gamma 1,05 y 1,2 | 12/12 por caso |
| Blur radio 0,5 | 12/12 |
| Recorte de cuatro píxeles por borde | 0/12 |
| Promedio de dos copias marcadas | 9/12 |

PSNR mínimo de los RGB8 marcados antes del ataque: **54,46 dB**; mayor error RGB absoluto medio **0,0009117**. Las imágenes son pequeñas y procedurales; el resultado no cubre albedos reales, atlas UV, mips, sRGB/Linear de Unity, normales, alpha, BCn ni la imagen final de un avatar.

En el experimento de promedio, se reconocieron **ambos** contribuyentes en 8/12 casos de geometría y 7/12 de textura. No debe atribuirse exclusivamente al candidato con mayor score. Este resultado tampoco implementa ni evalúa un código anti-colusión formal.

Evidencias locales ignoradas por Git: `evidence/fingerprint-research/`. El directorio `cpu-final/` contiene resultados, medidas completas, seis PNG calculadas por CPU, evaluación de ambigüedad, referencia privada y manifest de hashes. `cpu-replay/` reproduce el snapshot; `RESULTS.json` y `WORK_STATE.json` indican el checkpoint. Las PNG **no son screenshots de Unity o VRChat**. Los logs de iteraciones previas y el rechazo del store en HOME se conservan como diagnóstico.

## Diseño de integración para las siguientes etapas

La marca debe aplicarse a la geometría/albedo legible de una **copia propia antes de su ofuscación**. El decoder debe reconstruir esa copia marcada: añadir una marca solo al payload codificado puede hacerla desaparecer al decodificar. La fuente original permanece sin modificar. MetadataGuard actúa después sobre nombres de assets propios y conserva las identidades del registro privado.

Cada registro de componente futuro deberá conservar algoritmo/versionado, binding original, política de selección, parámetros de cuantización, presupuesto, cobertura, hash de la fuente/copia y resultado de autoverificación. Los hashes no equivalen a una marca resistente. Cambiar fuente, presupuesto o algoritmo exige una nueva evaluación; no reutilizar un registro incompatible silenciosamente. Las identidades GUID del fichero sospechoso no son un requisito de verificación.

Stage 15 empieza con meshes estáticas persistentes, legibles y auditadas. Debe revisar duplicados/seams y simetrías, conservar topología y atributos, autoverificar después de importar y **rechazar** cobertura insuficiente. Presupuestos iniciales propuestos: desplazamiento ≤min(10⁻⁵×diagonal, 5×10⁻⁵ unidades de objeto); no aumentar la fuerza automáticamente para forzar detección. Skinning, morphs, escalas del renderer y correspondencias de pose exigen su matriz propia antes de admitir un avatar animado.

Stage 16 empieza con albedo opaco y política de color explícita. Se marca antes de TextureGuard y antes de generar la pirámide; hay que medir cada mip y cada canal de compresión realmente utilizado. Presupuestos propuestos: RGB medio ≤0,0015, máximo ≤0,012 y PSNR ≥45 dB. Hay que medir UV seams, atlas, filtros, sRGB/Gamma/Linear, compresión y swaps. Presupuesto numérico sin validación visual no habilita el asistente.

Los dos pilotos deben guardar el contexto privado antes de publicar una copia Ready, restaurarlo exactamente y retirar únicamente sus assets/registros nuevos ante fallo. No se altera una huella existente para realizar otra preparación. Los resúmenes públicos solo reportan capacidades realmente verificadas. El diseño espera no añadir pases/shader de runtime al embeber una marca en assets existentes, pero su coste aún no está medido.

## Verificador y calibración pendientes — Stage 17

La herramienta futura `Tools → Linux Avatar Guard → Verify Suspected Rip` recibirá archivos elegidos por el usuario y un registro privado local. No observará VRChat ni tendrá envío automático. Tendrá que informar de candidatos de BuildID, componentes/algoritmos comparados, cobertura, score, transformaciones intentadas, versión de calibración y ambigüedad.

Se requieren fuentes/familias, builds y seeds de evaluación separados del ajuste del detector. Incluir bases comerciales compartidas sin marca, builds autorizados distintos de la misma base, otros assets, recortes, transformaciones compuestas y entradas que no coincidan con el contrato. No tratar miles de transformaciones de una sola fuente como miles de muestras independientes.

Se medirán falsos positivos por consulta completa, falsos negativos dentro del contrato y tasas de abstención. Buscar muchos builds, patches, orientaciones, parámetros o versiones aumenta las oportunidades de una coincidencia casual; la calibración debe contemplar esa búsqueda completa. Un acuerdo de bits de 90% no es una probabilidad de autoría de 90%. Los canales/markers del mismo asset tampoco se suman como evidencia independiente sin justificarlo.

Si faltan calibración o cobertura, hay varios candidatos próximos o se sale del canal validado, la conclusión será **inconclusa**, acompañada de datos observados. La ausencia de marca no prueba que un archivo nunca haya pasado por LAG. No habrá una etiqueta 100% de autenticidad/robo. Firmas o sellos de tiempo y pruebas de titularidad, si se incorporan, necesitan un diseño separado.

## Reproducción sin GPU

Usar Python con NumPy/Pillow ya disponibles; el harness no instala paquetes. Elegir un directorio de datos existente fuera de Unity/Git si el XDG local no cumple esa condición. Desde la raíz del repositorio:

```bash
nice -n 19 python3 Tests/Fingerprint/research.py \
  --output evidence/fingerprint-research/my-cpu-run \
  --private-data-root /ruta/privada/de/datos

nice -n 19 python3 Tests/Fingerprint/research.py \
  --output evidence/fingerprint-research/my-cpu-replay \
  --private-data-root /ruta/privada/de/datos \
  --replay-run RUN_ID_HEXADECIMAL_DE_PRIVATE_REFERENCE

nice -n 19 python3 -m unittest discover \
  -s Tests/Fingerprint -p test_private_store.py -v
```

El directorio de salida debe ser nuevo. RunID no contiene la huella. El host [`LAGFingerprintContextCpu.cs`](../Tests/Fingerprint/LAGFingerprintContextCpu.cs) se compila únicamente junto con `GuardFingerprintContext.cs`, con un compilador C# ya instalado, y se ejecuta en Mono/.NET sin assemblies de Unity. `--context-vectors results.json` comprueba los vectores del host durante el estudio. No se requiere iniciar el Editor ni un player.

## Gates que esta etapa no cierra

Pendientes: round-trip FBX real, welding/duplicación, geometría con topología, poses/bones/morphs, mips/BCn/Crunch reales, comparación visual Unity/lilToon, GPU exclusivamente en aplicaciones propias, artefacto procesado por SDK, calibración estadística del registro completo, respaldo/rollback productivo, UI y exportación/importación de un unitypackage nuevo. Stages 15–17 continúan con esa evidencia; la descarga estable 0.2.1 conserva sus binarios.
