# Diversidad y extracción adaptativa — Stage 6

Fecha: 2026-10-02. Unity 2022.3.22f1 Linux/Vulkan/Built-in, SDK 3.10.5, lilToon 2.3.4. Fixtures procedurales propias; ningún análisis de VRChat.

## Resultado

Se construyeron y reabrieron **100 AssetBundles Linux** de prefabs estáticos con dos bindings por build. Cada prefab incluye meshes codificadas, materiales, shaders y manifest; tiene respaldo reproducible privado separado. Cuatro muestras adicionales se construyeron para Windows64. Son builds completos de la fixture; no son 100 avatares animados ni bundles finales procesados por el pipeline de subida del SDK.

[Runner](../Tests/LAGDiversityValidation.cs) y [extractor independiente](../Tests/LAGAdaptiveShaderDecoder.cs). El extractor lee las sentencias HLSL emitidas y los carriers, y recibe los cuatro valores públicos de runtime de la fixture. No recibe CodecPlan, IR, seed ni vértices originales para decodificar; los originales solo sirven para medir el error. No decompila bytecode de un bundle ni recupera por sí mismo valores desconocidos. La observabilidad GPU/runtime se midió por separado en Stage 4.

| Métrica | Observado |
| --- | --- |
| Builds completos de fixture | 100 |
| Bindings / shaders generados | 200 / 400 |
| Programas / decoder bodies / constantes únicos | 200 / 200 / 200 |
| Secuencias de tipos / layouts únicos | 197 / 1 |
| Meshes / bundles únicos | 200 / 100 |
| Bundles Linux reabiertos / muestras Windows64 | 100 / 4 |
| Reutilización de otro build / otra mesh | 99 / 100 intentos fallidos |
| RMS mínimo cross-build / cross-mesh | 0.27265182 / 0.14528456 |
| RMS mínimo sin clave / clave incorrecta | 0.05124693 / 0.04745378 |
| Bindings recuperados por extractor adaptativo | 200 |
| Error máximo adaptativo | 1.788357622e-07 |
| Diferencia visual desbloqueada máxima | 7.978451322e-08 |
| Diferencia visual bloqueada mínima | 0.05342188 |
| Comprobaciones | 2714 |
| Variantes antes/después del filtro de fixture | 13504 / 1944 |
| Bytes totales de bundles Linux | 18250673 |
| Tiempo de reconstrucción de bundles con cache | 28.816 s |

La diversidad impide reutilizar sin adaptación los programas de los casos ensayados. El extractor adaptativo recupera **200/200** bindings: variar el programa no demuestra impedir la interpretación del decoder. Layout fijo 6/7: no se afirma diversidad de atributos. 197 secuencias distintas tampoco equivale a 200 órdenes de tipos distintos.

## Debilidad encontrada y rechazo

La primera colección incluyó una mesh cuya pareja de offsets era consecutiva y opuesta. Al aplicar el decoder con clave cero o incorrecta, RMS ≈ 1,9303158 × 10⁻⁸: la clave era irrelevante en ese caso. No se redujo el umbral del test para aceptar ese resultado.

`GuardStaticCodec` ahora rechaza la cancelación directa al planificar/validar/emitir. Además, antes de crear una Mesh codificada mide el RMS sin valores de desbloqueo y cancela si no supera el presupuesto de round-trip de 10⁻⁵. Esta es una condición numérica sobre la fuente codificada, no una prueba criptográfica de independencia de todos los valores posibles. Los seeds que producen un programa rechazado requieren un nuevo contexto. Se conserva el caso fallido como evidencia y regresión.

Para la colección final, el slot 30 utiliza la entrada pública de fixture 100; las otras entradas son 0–29 y 31–99. La entrada 30 conocida se comprueba como rechazo antes de escribir mesh/shader. El GUID de la fuente procedural se fija en el runner para reproducir esa identidad al recrear assets. Fuente, materiales, textura y prefab propios permanecen intactos durante reconstrucción/reapertura; no se usó el avatar comercial.

## Dependencias y variantes

La reapertura detectó que `AssetDatabase.GetDependencies` no incluye automáticamente el provider generado de `UsePass`. Los bundles iniciales podían construirse, pero sus materiales cargados no tenían un shader soportado. El runner incluye explícitamente los shaders de cada build y carga providers antes de facades/materiales. La prueba final verifica soporte Vulkan y renders del prefab cargado. El arreglo afecta al empaquetado de investigación; no certifica ni modifica el pipeline de subida de avatares.

El filtro [LAGDiversityVariantFilter.cs](../Tests/LAGDiversityVariantFilter.cs) solo se activa en la prueba sintética. Mantiene programas para cada pase y compila el subconjunto usado por una cámara mono y material unlit; no valida iluminación completa, fog, stereo, instancing, otros features lilToon ni runtime VRChat. La preparación y la release del usuario no usan este filtro. El target de bundle Linux se configura temporalmente en Vulkan y se restaura después. La vista previa de shaders en el Editor por sí sola no prueba que un bundle funcione.

La primera construcción con demasiadas variantes se interrumpió en el proyecto aislado, preservando los prefabs. La continuación verificó que el codec actual reproduce cada encoding/programa conservado, reemplazó el caso rechazado y reconstruyó los 100 bundles. El tiempo indicado usa cache; los tiempos de generación originales se registran como -1/no medidos en la continuación. No es un benchmark comparable a Stage 9. El total de bytes cubre esta fixture y este subconjunto de variantes.

## Evidencias y reproducción

Evidencia local en `evidence/codec-diversity/`: `validation.json`, 100 `linux-bundles`, cuatro `windows-samples`, 36 imágenes (cuatro builds × tres vistas × original/desbloqueado/bloqueado), `missing-key-probe.json` y `rejected-keyless-candidate-030/`. Los contextos de fixture temporales quedan fuera del proyecto y se eliminan; ninguna clave/captura/modelo de tercero entra en Git.

La ejecución final `security-stages-key-dependence-final.log` terminó con salida 0 y sin errores C#/shader: 2.714 comprobaciones de diversidad, 53 de derivación/contexto, 44 del prototipo y 83 de regresión legacy, además de shaders/bundles de esas suites. Persisten avisos de Licensing/JobTempAlloc/cola al cerrar el Editor; no constituyen mediciones de estabilidad/rendimiento.

Para una colección nueva, copiar paquete y todos los tests genéricos a un proyecto desechable **ValidationProject**, con las dependencias indicadas, y ejecutar:

```sh
LAG_BINDING_AUDIT_ALLOWED=synthetic-unity-only /ruta/Unity \
  -batchmode -force-vulkan -projectPath /ruta/ValidationProject \
  -executeMethod LAGDiversityValidation.Run -quit -logFile /ruta/diversity.log
```

`ResumeGeneratedFixtures` revalida assets propios existentes y reconstruye bundles; `ReplayBuiltFixtures` revalida/reabre bundles conservados. Se requieren providers explícitos y rutas absolutas para el loader nativo. Los runners abren escena vacía y recrean carpetas de fixtures; nunca ejecutarlos sobre trabajo del usuario.

Siguiente gate: [Stage 7 — asignación conservadora de atributos](DYNAMIC_ATTRIBUTE_RESEARCH.md), seguido de bindings de materiales/clips y validación del SDK. El nuevo codec continúa fuera del asistente y no se habilita para Carukia ni para skinning.
