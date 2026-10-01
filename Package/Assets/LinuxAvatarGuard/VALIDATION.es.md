# Validación

Base funcional 0.1.1: Unity 2022.3.22f1 en Linux/Vulkan, SDK 3.10.5, lilToon 2.3.4. Se probó un avatar real con 14 834 vértices, 123 blendshapes y 5 PhysBones; 217 comparaciones de clips/gestos/blendshapes/poses, más 8 casos FX, y 240 frames con PhysBones. El build oficial del SDK generó un .vrca y se comprobó que conservaba la malla ofuscada, shader y parámetros. Gesture Manager 3.9.9 recibió las cuatro claves por OSC, aplicó las capas FX y sincronizó un clon remoto. El propietario confirmó apariencia normal en el cliente VRChat después de configurar el ID y ejecutar el desbloqueador. Un segundo usuario real sigue pendiente.

0.2.0 conserva el algoritmo de geometría y amplía instalación, integración y gestión de claves. Las pruebas de esta versión incluyen generación de escena/copia sin alterar el original, respaldos y lanzadores, vinculación del ID, migración de una copia existente, compilación del asistente, pruebas de mesh/shader/controller, y seis pruebas Python, incluida detección de un avatar ya cargado con un servicio OSCQuery de prueba y pausa al cambiar de avatar.

Los resultados finales de lanzamiento se resumen en RELEASE-VALIDATION.md del ZIP público. Los proyectos completos, claves, avatares e imágenes de avatares externos se conservan únicamente en el entorno privado de pruebas y no forman parte de la descarga.

Una vista previa estática o un build del SDK no certifican todos los avatares, poses o comportamientos del cliente. Skinning sigue siendo experimental. La ofuscación no es protección absoluta y no protege texturas ni capturas GPU.
# Idiomas en 0.2.1

El asistente incorpora ES / EN / JP, con español por defecto y una preferencia persistente del Editor. Se verificaron las 116 traducciones, el respaldo a español de preferencias inválidas, los errores con nombres de archivos y los estados OSC mostrados. Las capturas reales del Editor se revisaron en los tres idiomas; el japonés se dibujó correctamente en el entorno probado. El cambio de idioma conserva el estado previo de la escena y no modifica el avatar, los perfiles ni las claves. La validación del algoritmo de malla y OSC que sigue corresponde a 0.2.0; no se requiere volver a subir el avatar.
