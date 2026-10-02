# Stage 4: prototipo polimórfico estático

Fecha: 2026-10-01. Estado: API de investigación opt-in implementada y probada en Unity; fuera de la preparación productiva. Codec `static-polymorphic-prototype`, versión 1; IR schema 2.

## Alcance

La preparación normal y todos los perfiles siguen usando `legacy-linear`. El prototipo requiere una llamada explícita a `StaticPolymorphicCodecV1` y no añade una opción al asistente, un perfil OSC, una actualización de avatar remoto ni un nuevo binario de release. El proyecto de Carukia se conserva fuera de las pruebas.

Se admite una mesh estática propia en MeshRenderer, Unity Editor nativo Linux, Vulkan y Built-in Render Pipeline. Se rechazan SkinnedMeshRenderer por la puerta de renderer, datos de pesos/bindposes, todos los blendshapes, carriers UV7/UV8 ocupados y datos no finitos. Coordenadas originales: máximo ±16 unidades por componente. Intensidad: `(0, 0.5]`. El encoder cancela si el round-trip interno supera `1e-5` unidades; la validación independiente cubre escalas distintas.

Este es un contrato de investigación para fixtures, no una certificación de todas las funciones lilToon o avatares VRChat. Integración contextual, animación, persistencia de seed y procesamiento final del SDK pertenecen a etapas posteriores.

## Programa reversible

[GuardCodecInstruction.cs](../Package/Assets/LinuxAvatarGuard/Editor/GuardCodecInstruction.cs) implementa una IR cerrada con parámetros tipados e inmutables:

| Operación | Forward | Inversa |
| --- | --- | --- |
| AxisSwap | Intercambiar dos componentes distintas | El mismo intercambio. |
| AxisFlip | Negar una componente | La misma negación. |
| TriangularShear | `p[a] += f * p[b]`, con `a != b` | Restar el mismo término. |
| BoundedBend | `p[a] += f * p[b] / (1 + abs(p[b]))` | Restar el mismo término. |
| KeyedVectorOffset | Añadir tres productos con `payload * runtime / 255` | Restarlos. |

El bend deja intacta la componente de la que depende; por eso su inversa no exige resolver una ecuación. Su contribución está acotada y `abs(f) <= 0.25`. Los shears comparten ese límite. Las filas de offset generadas son filas Hadamard con signos/permutaciones; siguen siendo de rango 3. La fábrica rechaza ejes inválidos, factores degenerados/no finitos y filas de rango insuficiente.

[GuardStaticCodec.cs](../Package/Assets/LinuxAvatarGuard/Editor/GuardStaticCodec.cs) genera ocho instrucciones: un swap, un flip, dos shears, dos bends y dos offsets. Un stream determinista ordena y parametriza el programa. El decoder emite las operaciones inversas en orden inverso mediante los nodos admitidos, sin aceptar HLSL arbitrario.

Se mantienen UV de índices 6 y 7, dos componentes por canal, y las cuatro propiedades `_LAGKey0..3`. El desplazamiento no depende de la dirección de la normal. El clon conserva topología, normales, tangentes, colores, UV existentes y bounds originales. No hay allocator dinámico ni protección de texturas en esta etapa.

La reconstrucción ocurre en `LIL_CUSTOM_VERTEX_OS` de lilToon. La [especificación oficial del hook](https://lilxyzw.github.io/lilToon/ja_JP/dev/custom_shader_format.html) se contrastó con los includes instalados de lilToon 2.3.4 y con renders reales. El comportamiento de otros hooks/features necesita pruebas propias antes de integrarse.

## Seed, determinismo y fuente

La API acepta un seed explícito de 32 bytes o crea uno con CSPRNG. Copia el buffer del caller y lo mantiene privado en memoria; al disponer el codec limpia su copia. El seed no se incorpora al plan público, shader, material ni reporte. El prototipo no lo persiste: la metadata privada de un build nuevo se implementará en Stage 5.

Se utiliza HMAC-SHA256 con contador little-endian y labels distintos para programa/payload. El muestreo entero hace rejection sampling. Esto da reproducibilidad bajo el mismo seed, fuente, intensidad y valores de runtime; no es una promesa de cifrado fuerte ni un reemplazo de la derivación por binding prevista para Stage 5.

La IR tiene un hash canónico basado en versiones, layout y argumentos numéricos. El plan retiene una huella de atributos, posiciones, topología y bounds del source. `Validate`, `Encode` y `EmitDecoder` vuelven a comprobarla. Cambiar la fuente invalida el plan. Se mantiene además la pertenencia al objeto fuente y al dueño del seed; un plan no se puede ejecutar desde otro codec.

La identidad aún no incorpora GUID/localFileID ni renderer/binding; dos meshes usando el mismo seed pueden compartir programa y stream de payload. No se afirma aislamiento por mesh. Para el shader, los cuatro valores de runtime siguen siendo observables. Una configuración privada del prototipo con los cuatro bytes cero se rechaza, aunque cero es el estado de material inicial y permite medir el aspecto bloqueado.

Ejemplo de llamada explícita desde código Editor de investigación:

```csharp
using (var codec = StaticPolymorphicCodecV1.CreateRandom())
{
    var source = StaticPolymorphicCodecV1.RequireStaticRenderer(ownedRenderer);
    var plan = codec.Plan(source, 0.15f);
    codec.Validate(source, plan);
    var runtime = GuardMesh.NewKey();
    var protectedMesh = codec.Encode(source, plan, runtime);
    var fragment = codec.EmitDecoder(plan);
    // El experimento conserva los valores de runtime en memoria para su render.
    // La integración de perfil/FX/backup corresponde a etapas siguientes.
}
```

El test completo muestra el binding explícito del fragmento con `GuardShaders` y las claves públicas de la fixture. El código de producción del paquete sigue siendo Editor-only; no se añade un componente de runtime que cree una Unity Mesh decodificada.

## Validación observada

Entorno: Unity 2022.3.22f1, SDK 3.10.5 en la regresión, lilToon 2.3.4, Vulkan, Intel Arc A750. [LAGStaticPrototypeValidation.cs](../Tests/LAGStaticPrototypeValidation.cs) utiliza una esfera procedimental asimétrica, textura checker propia y claves públicas fijas.

| Comprobación | Resultado |
| --- | --- |
| Suite del prototipo | 44 comprobaciones correctas. |
| Seeds de fixture | 16; 16 programas y 16 cuerpos de decoder distintos. |
| Determinismo | Buffers/programa idénticos al repetir el contexto y al recrear el codec. |
| Intérprete independiente | Usa double, lee la IR y reconstruye todos los encodings. |
| Error máximo geométrico | ≈ 1,93 × 10⁻⁶ unidades, incluyendo las escalas 0,001 / 0,05 / 20. |
| RMS con programa de otro seed | ≈ 1,1182 unidades en el caso medido. |
| RMS con fórmula legacy | ≈ 1,0019 unidades, usando los mismos valores de runtime. |
| Tres renders desbloqueados | Diferencia RGB media 0 frente a su original. |
| Renders con valores ausentes/incorrectos | Diferencia RGB media > 0,01 en cada ángulo. |
| Assets originales de la fixture | Mesh, material y textura byte a byte iguales después de renderizar. |
| Shaders | 6 derivados compilados, con caminos opaque/cutout-outline/transparent y UsePass representativos. |
| Bundle de fixture | Windows64 construido; no es el bundle procesado por el SDK. |
| Código fuente sin SDK | Compila y ejecuta el prototipo en Vulkan. |
| Entorno no admitido | En Unity OpenGLCore, la API rechaza el plan antes de codificar. |

La suite anterior conserva sus 83 comprobaciones: contrato legacy 18, funcionales 26, adversarial legacy 14 y preparación 25. Sus 13 shaders/bundle y las seis pruebas Python OSC siguen correctos. Total de esta regresión: 127 comprobaciones Unity y 6 pruebas OSC.

Los primeros imports en ambos proyectos tuvieron referencias transitorias sin resolver a `CodecInstruction`. Tras el import se repitieron las ejecuciones: los logs finales terminan con código 0, marcadores correctos y sin errores C# ni de compilación de shader. Se conservan los avisos de cierre/Unity Licensing ya descritos en la baseline; estos renders no constituyen un benchmark.

## Captura GPU del prototipo

RenderDoc 1.46 inició un Unity desechable nuevo, con la esfera propia y el seed público de fixture. No se capturó ni inspeccionó VRChat. Se aplicó la misma configuración de capa por proceso descrita en [GPU_CAPTURE_THREAT_MODEL.md](GPU_CAPTURE_THREAT_MODEL.md), con `LAG_GPU_AUDIT_CODEC=static-prototype`.

Se analizaron dos draws con 515 vértices PostVS y 2304 índices cada uno. Entrada frente al buffer codificado: error 0. Salida frente a la proyección original: error máximo ≈ `1,33e-7`, con el ajuste global Y de Vulkan registrado. Los cuatro valores públicos se observan en el bloque de constantes, offset relativo 736 bytes. Se exportó geometría proyectada directamente del replay.

La captura confirma que también en este prototipo la salida del vertex shader proporciona geometría reconstruida. Tener instrucciones diferentes no evita PostVS. Los OBJ son coordenadas proyectadas de una fixture, no rest poses de un avatar animado. El efecto del prototipo frente a un extractor adaptativo del shader completo aún no se ha medido; el intérprete independiente ya demuestra que conocer el programa y los valores permite reconstruir.

## Evidencias locales y reproducción

Archivos excluidos de Git y de la release:

- `evidence/static-prototype/validation.json` y `view-{0,1,2}-{original,unlocked,locked,wrong}.png`.
- `evidence/static-prototype/windows-bundle/`: artefacto propio.
- `evidence/static-prototype/sdk-free-Vulkan.json` y `sdk-free-OpenGLCore.json`: funcionamiento permitido y rechazo fuera de Vulkan.
- `evidence/gpu-audit/static-prototype/`: captura `.rdc`, referencias, métricas, OBJ e imagen Unity.
- `static-prototype-final-validation.log`, `static-prototype-guard-final.log` y logs de captura/import.

Usar únicamente un proyecto desechable. El runner abre una escena vacía y recrea `Assets/StaticCodecPrototype`; la regresión completa también recrea `Assets/Fixture`. No copiar ni ejecutar estos tests en el proyecto de trabajo del avatar.

```bash
/home/stwby/Unity/Hub/Editor/2022.3.22f1/Editor/Unity \
  -batchmode -force-vulkan \
  -projectPath /home/stwby/LinuxAvatarGuard/ValidationProject \
  -executeMethod LAGStaticPrototypeValidation.RunFullRegression -quit \
  -logFile /home/stwby/LinuxAvatarGuard/static-prototype-final-validation.log
```

La captura usa el comando de GPU threat model con la variable adicional y salida en `evidence/gpu-audit/static-prototype/`; el analizador recibe esa carpeta mediante `LAG_GPU_AUDIT_DIRECTORY`. Los seeds/claves de prueba pertenecen a la fixture; ningún secreto del avatar real aparece en estos scripts o reportes.

## Próxima etapa

Stage 5: identidad estable y derivación por binding/mesh, separación de dominios con vectores verificables y persistencia privada versionada del contexto. Mantener el camino de avatar predeterminado en legacy. Después medir 100 builds completos y un extractor adaptativo; materiales y clips deben ligarse al programa correcto antes de habilitar preparación productiva.
