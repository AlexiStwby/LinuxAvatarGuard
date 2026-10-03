# MetadataGuard — Stage 13

`GuardMetadataGuard` añade ofuscación de nombres a **copias independientes** desde Unity 2022.3, Linux, Vulkan y Built-in. Es una capa de metadatos: no cifra geometría, no añade protección de texturas y no impide reconstrucción gráfica. La descarga 0.2.1 y la ruta predeterminada del asistente conservan su comportamiento anterior.

## Contrato conservador

La API independiente copia meshes, materiales, clips, controllers, overrides, máscaras y assets de expresiones del SDK. Las texturas, shaders, Avatars y otros tipos no pertenecen a esta capa y pueden seguir compartidos; una copia de metadatos de un avatar sin protección geométrica seguirá conteniendo geometría utilizable. No debe presentarse como reemplazo de MeshGuard/TextureGuard.

Los archivos nuevos reciben nombres opacos por build. Los labels de meshes/materiales/clips/controllers/máscaras/BlendTrees pueden cambiar cuando el grafo revisado no tiene callbacks o behaviours externos. La jerarquía, los nombres y orden de blendshapes, los parámetros, los estados y las capas del Animator se conservan de forma predeterminada. También se conservan los nombres visibles del menú, los tags de Contacts y las propiedades de shader.

No se usan heurísticas para decidir que un nombre funcional es interno. Componentes no revisados, scripts ausentes y dependencias que no se pueden copiar provocan rechazo antes de devolver un resultado. MA, VRCFury y otras integraciones requieren su flujo de horneado/validación; no se elimina ninguna integración ni se promete su compatibilidad por renombrar sus campos.

## Selecciones internas explícitas

`GuardMetadataOptions` permite seleccionar rutas de objetos, nombres de morphs por renderer, parámetros y capas del Animator. Este contrato requiere una raíz genérica con como máximo un Animator en la raíz, sin `Avatar`, descriptor/expresiones SDK, scripts, StateMachineBehaviours, callbacks de AnimationEvent ni componentes de dinámica. Los nombres externos quedan fuera de esta ruta. Los parámetros integrados conocidos de VRChat, `LAG_*`, prefijos de face tracking/OSC y morphs de visemes/parpadeo se rechazan además por política.

Se remapean conjuntamente:

- Rutas descendientes de las curvas float y de referencias a objetos.
- Nombres de blendshapes en la mesh y en `blendShape.*` de los clips.
- Declaraciones, condiciones y campos de speed/time/cycle/mirror del Animator.
- Parámetros de BlendTrees anidados y pesos de Direct Blend.
- Curvas de parámetros animados del Animator y rutas de AvatarMask.
- Claves/valores nativos de AnimatorOverrideController y materiales de los keyframes.

Los estados conservan sus nombres y destinos. Las capas explícitamente internas pueden cambiar de nombre conservando índices sincronizados, pesos y máscaras. El limpiador retira solamente posiciones del grafo del Editor. Los morphs conservan índices, frames, pesos de frame y deltas de posición/normal/tangente. No se recalculan normales, tangentes, bindposes ni pesos de huesos; se restauran bounds y pesos iniciales del renderer copiado. Los morphs de una mesh compartida cambian para todos sus renderers y sus clips: no se crean identidades incompatibles para la misma mesh.

Rutas ambiguas, reemplazos animados de mesh junto a renombrado de morphs y callbacks no revisados quedan fuera del contrato. Unity documenta las operaciones usadas para [curvas de Editor](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AnimationUtility.SetEditorCurve.html) y [frames de blendshape](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Mesh.AddBlendShapeFrame.html). El recorrido de dependencias de runtime no sustituye el inventario de subassets del controller: se revisan también sus BlendTrees y behaviours del Editor.

```csharp
var options = new GuardMetadataOptions {
    InternalObjectPaths = new[] { "Rig", "Rig/InternalBone", "Body" },
    InternalParameters = new[] { "InternalBlend" },
    InternalBlendshapes = new[] {
        new GuardMetadataBlendshapes {
            rendererPath = "Body", names = new[] { "InternalSmile" }
        }
    }
};
using (var copy = GuardMetadataGuard.Prepare(
    genericRoot, buildId, "Assets/LinuxAvatarGuardGenerated/Research/metadata-example", options))
{
    copy.SavePrivateMapping("/ruta/privada/metadata-map.json");
    // copy.PrefabPath contiene la copia guardada; Dispose retira solo la instancia temporal.
}
```

El overload con seed de 32 bytes sirve para reproducción/investigación. La ruta normal usa un seed aleatorio y HMAC-SHA256 con dominio propio de metadatos, BuildID, tipo e identidad. No cambia ninguna derivación de claves de geometría/textura ni el formato OSC. Los aliases son obfuscación; el seed no convierte los nombres en un secreto de runtime.

## Integración opt-in con preparación VRChat

`GuardBuilder.BuildWithMetadata` y `GuardSetup.PrepareWithMetadata` aplican exclusivamente labels/nombres de archivos a assets **ya generados** por la transacción de MeshGuard legacy. Esta ruta conserva jerarquía, visemes, ojos, expresiones, estados/capas, nombres de PhysBones/Contacts y los cuatro parámetros OSC. No incorpora el renombrado de nombres funcionales genéricos al avatar SDK ni activa TextureGuard V2.

El manifest público schema 1 añade campos opcionales `metadataGuardVersion`, `metadataPolicy`, `renamedMetadataAssets` y `renamedMetadataFiles`. Los últimos dos distinguen labels de archivos: callbacks/behaviours pueden impedir cambiar labels aunque se hayan copiado archivos con nombres opacos. Los perfiles anteriores mantienen sus valores por defecto y no requieren regeneración. El mapa privado se guarda en `Library/LinuxAvatarGuard/<id>.json.metadata.json` y se respalda junto a `key.json` como `metadata-map.json`; la importación de un respaldo puede reconocer ese archivo hermano. Se comprueba que pertenezca al mismo BuildID.

Los datos de inversión, nombres fuente y rutas originales nunca se escriben en el manifest público. El escritor privado exige ruta absoluta fuera de cualquier árbol Assets/Packages, rechaza enlaces y respaldos diferentes existentes y aplica permisos 0700 al directorio y 0600 al archivo. `.gitignore` excluye mapas privados. No se añaden controles al asistente ni se exporta una release en Stage 13.

## Transacción y pruebas

Se capturan fuentes de disco/meta y datos en memoria, incluidas las dependencias de autoría del controller. Los clones nativos incluyen los cambios vivos; los grafos que se copian desde archivo requieren estar guardados. La instancia temporal vive en una escena de preview independiente: crearla no ensucia la escena de trabajo; `Dispose` la retira y cierra su preview. No se llama a `SaveAssets` global ni se guarda la escena de trabajo. Los fallos retiran únicamente la carpeta nueva; las mutaciones del llamador en la fuente se conservan. Se revisan también las copias en memoria y el prefab persistido después de sus checkpoints y de importar el manifest.

La suite `Tests/LAGMetadataValidation.cs` usa contenido procedural propio, máscaras, controller sincronizado/anidado, overrides, cambios de material, curvas de morph y parámetros. Construye y reabre un bundle Linux/Vulkan con origen y copia; compara poses y la ejecución real de una transición del Animator. Incluye reproducción con el mismo seed, diversidad entre seeds, nombres reservados, scripts/behaviours/eventos no revisados y fallos inyectados en tres fronteras de la transacción.

Los probes de `Tests/Metadata/` deben importarse fuera de una carpeta Editor, con su asmdef de runtime. La suite principal va en Editor. Esto permite que Unity cree realmente los componentes/behaviours de prueba; el runner verifica que se hayan adjuntado antes de probar su rechazo.

Resultados finales sobre Unity 2022.3.22f1/Linux/Vulkan:

| Suite | Resultado |
| --- | --- |
| Metadata genérica Gamma | 90 comprobaciones; 6 comparaciones nativas; 12 PNG. |
| Metadata genérica Linear | 90 comprobaciones; 6 comparaciones nativas; 12 PNG. |
| SDK 3.10.5 y preparación opt-in | 21 comprobaciones; respaldo y tres fronteras de rollback. |
| Regresiones legacy | 18 codec + 26 funcionales + 14 baseline + 25 preparación = 83; 13 shaders y bundle Windows64 compilados. |
| Importación sin SDK/lilToon | API y prefab serializado correctos. |
| OSC de loopback | 6 tests correctos. |

Las 12 comparaciones de renders nativos tienen diferencia RGB media **0** en las fixtures medidas. Este dato no certifica todos los avatares/iluminación/stereo ni una ejecución D3D: Windows64 se compiló, no se ejecutó. Ambas matrices conservan byte idénticos los archivos/meta fuente y sus renderers iniciales. Los runners finales terminan con código 0; los avisos conocidos de SDK/JobTempAlloc y los avisos temporales de nombres de main assets durante la importación se conservan en los logs. No se certifica estabilidad del Editor ni ausencia de fugas. Los primeros fallos de diagnóstico y las fixtures corregidas no se cuentan como resultados finales.

Los informes, 24 PNG de comparación y dos hojas comparativas están en `evidence/metadata-guard/`. Esa carpeta se ignora en Git. Las regresiones que escriben informes en rutas históricas conservan copias de los resultados anteriores en `prior-validation/` y copias de los resultados nuevos en `regressions/`.

`Tests/LAGMetadataSdkValidation.cs` revisa visemes/ojos/expresiones/PhysBones/Contacts sobre una fixture SDK propia y el respaldo/rollback de `PrepareWithMetadata`. Esta validación de preparación no equivale a procesar o subir un avatar con el SDK. `Tests/LAGMetadataImportSmoke.cs` compila y ejecuta la API sin SDK ni lilToon.

```sh
LAG_METADATA_AUDIT_ALLOWED=owned-unity-only \
LAG_METADATA_COLORSPACE=Gamma /ruta/Unity \
  -batchmode -force-vulkan -projectPath /ruta/ValidationProject \
  -executeMethod LAGMetadataValidation.RunBatch \
  -logFile /ruta/evidence/metadata-guard/gamma-run.log
```

Cambiar `LAG_METADATA_COLORSPACE=Linear` ejecuta la otra matriz. Los runners solo admiten los proyectos desechables identificados. No acceden a VRChat, no necesitan capturas GPU ni descargan herramientas.

## Límites y siguiente etapa

Los nombres funcionales de VRChat permanecen observables. La topología de los controllers, IDs y datos de runtime permiten correlación aunque cambien labels. No se mide resistencia criptográfica, tiempo de ripping ni un aumento de coste para un extractor adaptativo. Esta etapa cumple el contrato conservador de MetadataGuard; Stage 14 investigará fingerprints forenses, sus transformaciones adversarias y controles de detección. La validación completa del artefacto SDK y la distribución conservan sus gates de las etapas posteriores.
