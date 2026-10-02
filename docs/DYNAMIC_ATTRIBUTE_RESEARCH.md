# Preparación de Stage 7 — asignación de atributos

Fecha: 2026-10-02. Investigación sobre fuentes locales de lilToon 2.3.4, Unity 2022.3.22f1 y Built-in. El allocator dinámico aún no está implementado; el prototipo conserva UV7/UV8 (índices Unity 6/7).

## Hallazgos que condicionan la implementación

Un canal sin datos en `Mesh` puede seguir teniendo significado para el shader. En `lil_common_vert.hlsl`, ID Mask selecciona `input.uv0.x` hasta `input.uv7.x` según `_IDMaskFrom` (8 selecciona VertexID). Escribir payload en un canal vacío seleccionado por ID Mask puede modificar máscaras. En `lil_vert_audiolink.hlsl`, AudioLink vertex mask selecciona UV0–3 con `_AudioLinkMask_UVMode` cuando `_AudioLinkVertexUVMode` vale 3. Decals y otros caminos también consumen UV0–3. Referencia primaria: [fuentes de lilToon](https://github.com/lilxyzw/lilToon/tree/master/Assets/lilToon/Shader/Includes); la revisión concreta se realizó sobre la versión instalada 2.3.4, no se presume equivalencia con `master` futuro.

`lil_common_appdata.hlsl` reserva TEXCOORD4 para posición previa si `LIL_APP_PREVPOS` está definido, y TEXCOORD5 para velocidad previa si `LIL_APP_PREVEL` está definido. Los defines pueden venir de motion vectors, `LIL_REQUIRE_APP_PREVPOS`, `LIL_REQUIRE_APP_PREVEL` o `_ADD_PRECOMPUTED_VELOCITY`. Exigir `LIL_REQUIRE_APP_TEXCOORD4/5` no elimina esa reserva. Por ello, la asignación debe verificar el contrato de cada pase y emitir el hook correcto. [Fuente primaria appdata](https://github.com/lilxyzw/lilToon/blob/master/Assets/lilToon/Shader/Includes/lil_common_appdata.hlsl).

| Atributo | Condición conservadora prevista |
| --- | --- |
| UV0–3 | Reservados para funciones lilToon; no candidatos iniciales. |
| UV4–5 (índices 4/5) | Candidatos solo con prueba de que no se usan posición/velocidad previas ni ID Mask, en todos los pases admitidos. |
| UV6–7 (índices 6/7) | Candidatos solo sin datos existentes y sin consumidor ID Mask/custom. |
| Colores y tangentes | Reservados; shading/alpha/normal mapping y animación no están cubiertos por una prueba de reemplazo. |
| Lookup texture | Fuera del primer allocator; sampling, tamaño, coste y precisión requieren investigación propia. |

En la interfaz Unity, los índices 4/5/6/7 se suelen presentar como UV5/UV6/UV7/UV8. El código y los manifests deben registrar índices explícitos para evitar esa ambigüedad.

## Plan concreto y gates

1. Analizar atributos, todos los materiales/submeshes, versión/fuentes de shader, keywords y pases. Rechazar custom hooks/consumidores desconocidos; no inferir disponibilidad solo de `GetUVs` vacío. Conservar hash del análisis para invalidar un plan si cambia un material.
2. Iniciar con fixtures estáticas sin Animator/Animation ni material swaps. Propiedades animables como `_IDMaskFrom` requieren análisis contextual de clips en Stage 8; hasta entonces, se reservan sus canales o se rechaza ese caso.
3. Asignar dos carriers Float32 de dos componentes entre los canales realmente disponibles, con propósito HKDF separado y política versionada. El layout forma parte de hashes/programa/configuración; distintos layouts deben emitir referencias `input.uvN` y defines correspondientes.
4. Persistir layout/política en un schema nuevo del contexto privado, manteniendo lectura explícita de schema 1 con su layout fijo. No reinterpretar silenciosamente respaldos existentes. El manifest público informa carriers, nunca seed/valores runtime.
5. Matriz positiva/negativa: canales vacíos/ocupados, ID Mask en cada canal, nombres/UV de 2–4 componentes, material múltiple, keyword/cambio posterior, falta de dos carriers y cada pase permitido. Comparar original/desbloqueado/bloqueado en Vulkan y verificar que la fuente permanece intacta.
6. Repetir diversidad sobre layouts realmente implementados. Stage 6 mide un único layout fijo; no se afirma diversidad de atributos en esa etapa. Comprobar que el extractor adaptativo puede localizar carriers a partir del decoder.

La primera implementación no debe elegir un canal si no puede demostrar disponibilidad. La fragmentación adicional o el uso de colores/tangentes se incorpora solo tras sus pruebas de compatibilidad. No hay cambio al avatar funcional, al asistente ni a la release 0.2.1 por este análisis.
