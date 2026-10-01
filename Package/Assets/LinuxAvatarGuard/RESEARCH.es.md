# Investigación — 2026-09-30

Se revisaron fuentes primarias; se tomaron técnicas como referencia, sin copiar los sistemas de obfuscación completos.

| Proyecto / fuente | Hallazgo | Decisión |
|---|---|---|
| [PlagueVRC/AntiRip](https://github.com/PlagueVRC/AntiRip) | Deforma vértices y usa shaders/parametrización para recuperarlos. Advierte de edición invasiva, necesidad de backup y restricciones con shaders y herramientas de avatar. | Adoptar la idea de mesh + GPU, generar una copia independiente y rechazar casos que no se puedan conservar. No reutilizar su obfuscación de nombres ni su código. |
| [rygo6/GTAvaCrypt](https://github.com/rygo6/GTAvaCrypt) | Antecedente de ofuscación de meshes con clave sincronizada; está archivado y dirige mantenimiento a AntiRip. | Referencia histórica; no tratarlo como una dependencia activa ni presentar ofuscación como cifrado fuerte. |
| [Shell4026/ShellProtector](https://github.com/Shell4026/ShellProtector), [README inglés](https://github.com/Shell4026/ShellProtector/blob/main/README.ENG.md) | Protección de texturas con integración de shaders y OSC; enumera compatibilidad lilToon hasta 2.3.4. | Usar OSC local para aportar la clave, con Python estándar en Linux. No portar su cifrado de texturas en esta entrega ni depender de herramientas Windows. |
| [lilToon](https://github.com/lilxyzw/lilToon), [especificación de shaders personalizados](https://lilxyzw.github.io/lilToon/ja_JP/dev/custom_shader_format.html) | Proporciona puntos de extensión de shader. La fuente local 2.3.4 implementa `LIL_CUSTOM_VERTEX_OS` y `LIL_CUSTOM_PROPERTIES`; el antiguo include de cifrado encontrado en Assets no se conecta al shader instalado. | Inyectar el punto de extensión en copias de los shaders utilizados y remapear transitivamente sus UsePass. Conservar los includes de lilToon instalado. |
| [Plataformas oficiales VRChat](https://creators.vrchat.com/platforms/), [configuración entre plataformas](https://creators.vrchat.com/platforms/android/cross-platform-setup/) | Publica contenido Windows, Android e iOS; el target Windows es la versión PC. | Linux/Vulkan se aplica a preparación del Editor, no a un supuesto bundle Linux de VRChat. Conservar compilación desktop en los shaders. |
| [OSC Avatar Parameters](https://docs.vrchat.com/docs/osc-avatar-parameters) | Permite enviar parámetros y observar el cambio de avatar. | Cuatro Int sincronizados con defaults bloqueados; clave privada fuera de Assets. Confirmar ID observado antes del envío automático. |
| [Unity: parámetros del Editor](https://docs.unity3d.com/2022.3/Documentation/Manual/EditorCommandLineArguments.html) | Permite seleccionar Vulkan al iniciar. | Exigir `SystemInfo.graphicsDeviceType == Vulkan`, sin modificar Project Settings. |

## Por qué no basta Vulkan

Inferencia técnica: un backend gráfico dibuja assets, no autentica a su propietario. Los clientes deben tener datos suficientes para reconstruir la imagen. Exigir Vulkan no evita acceso al bundle ni a buffers reconstruidos. Restringir el shader a Vulkan también podría hacerlo invisible para clientes PC con otro backend. Por eso sólo se restringe la herramienta de creación.

## Modelo de amenaza

El objetivo acotado es dificultar extracción simple y edición de la malla sin la clave. No se protege el bundle completo, las texturas, una copia fuente sin protección, los parámetros recibidos por un cliente ni una captura GPU. El algoritmo usa combinaciones lineales de coeficientes: se puede analizar y revertir. No es AES, no es un secreto de hardware y no debe publicitarse como protección imposible de romper.

Los originales deben permanecer privados. La configuración OSC tiene la clave; no compartirla junto al prefab. La sincronización permite que otros usuarios vean el avatar, pero también significa que la clave llega a sus clientes. Ninguno de los proyectos citados demuestra una protección absoluta contra un cliente que ya puede dibujar el avatar.

## Licencias

La implementación nueva de Linux Avatar Guard se entrega bajo MIT. No incluye código de AntiRip, GTAvaCrypt ni ShellProtector. Los shaders generados son derivados de la instalación local de lilToon; el generador conserva su licencia MIT en la carpeta de salida. lilToon, Unity y VRChat SDK son dependencias separadas y no se redistribuyen en el unitypackage.
