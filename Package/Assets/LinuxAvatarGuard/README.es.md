# Linux Avatar Guard 0.2.1 — gratis y experimental

Ofuscación de geometría para avatares VRChat PC con lilToon. Prepara una copia independiente desde Unity Linux + Vulkan y la reconstruye con cuatro parámetros enviados por OSC local. No es cifrado fuerte: no protege texturas, no impide capturas GPU ni garantiza impedir todo ripping.

## Instalación

1. En tu proyecto de avatar instala VRChat SDK Avatars y lilToon con ALCOM/VCC. Necesitas Python 3 instalado en Linux. Gesture Manager es opcional para probar Play Mode.
2. Abre Unity nativo para Linux con Vulkan. Si usas Hub y no está en Vulkan, cierra el Editor y ejecútalo con `-force-vulkan -projectPath '/ruta/proyecto'`. `Tools/launch-unity-vulkan.sh` puede ayudarte.
3. Importa `LinuxAvatarGuard-0.2.1.unitypackage`. El asistente se abre la primera vez; también está en Tools > Linux Avatar Guard > Asistente. No requiere copiar scripts a mano ni instalar módulos de Python.

El selector **Idioma** está arriba del asistente: **ES** (español), **EN** (inglés) y **JP** (japonés). Español es el idioma predeterminado. Cambia al momento y conserva tu elección al cerrar o reiniciar Unity, también entre proyectos. Traduce botones, avisos, errores mostrados en el asistente, estados OSC, diálogos propios e Inspector de materiales protegidos. Los menús de Unity conservan sus rutas existentes; no cambia los nombres de tus objetos, archivos ni datos privados.

## Tres pasos

### 1. Preparar

Selecciona el avatar de trabajo (puede ser un prefab o su raíz en la escena) y pulsa **Comprobar y preparar avatar**. El asistente encuentra la raíz, valida compatibilidad, genera el prefab protegido y una escena de subida separada. El desplazamiento habitual viene configurado; puedes cambiarlo en Ajustes avanzados. La copia con skinning sigue siendo experimental: valida gestos, visemas, PhysBones y poses antes de usarla.

MA, AAO y VRCFury: primero hornea/exporta una copia final con sus herramientas. LAG rechaza sus componentes de procesamiento, porque una optimización posterior podría eliminar los UV de protección. No los borra automáticamente ni promete compatibilidad que no se haya validado.

### 2. Revisar y subir

Pulsa **Abrir escena para subir**. Se abre una escena con la copia y el panel oficial del SDK. La escena de trabajo se conserva; si tiene cambios sin guardar Unity te permite guardarlos o cancelar la apertura.

**Ver desbloqueado / Ver bloqueado** permiten revisar la apariencia. En Edit Mode la vista previa usa una propiedad temporal, no guarda la clave en materiales. Esa imagen no valida por sí sola el Animator. En Play Mode usa Gesture Manager: selecciona esa copia en el simulador y los botones aplicarán los parámetros de su módulo, que atraviesan las capas FX reales.

Publica la versión Windows PC con el SDK. La herramienta nunca publica por ti. **Si el avatar original ya tenía Blueprint ID, la copia lo conserva:** comprueba ese ID antes de publicar para decidir si actualizas esa entrada o creas otra mediante el flujo del SDK.

### 3. Activar OSC

Después de publicar, vuelve al asistente y pulsa **Vincular avatar publicado**: detecta el ID en la copia abierta. No hace falta editar JSON. Para un avatar que no esté abierto puedes pegar el ID en el campo opcional.

Activa OSC en VRChat y pulsa **Iniciar desbloqueo OSC**. El asistente muestra si está esperando, enviando o si VRChat confirmó las cuatro claves. El programa intenta detectar por OSCQuery el avatar ya cargado; si ese servicio no está disponible, cambia a otro avatar y vuelve para recibir `/avatar/change`. En ese caso el envío UDP no confirma recepción.

**Sin OSC y las claves correctas la malla se ve deformada:** es el estado bloqueado. No subas una captura de ese estado pensando que representa la apariencia normal. Si se deforma después de abrir el juego, inicia el desbloqueador y comprueba el ID vinculado.

## Usar sin Unity

Pulsa **Abrir lanzador para usar sin Unity**. Se crea `Desbloquear.sh` y un acceso `Desbloquear.desktop` privado. Tu escritorio puede pedir marcar el acceso como confiable. Puedes ejecutar el `.sh` con Bash si tu administrador de archivos no ofrece Ejecutar. Inicia sólo una copia del desbloqueador, ya que necesita el puerto OSC 9001.

La clave y una copia del desbloqueador se guardan en `${XDG_DATA_HOME:-~/.local/share}/linux-avatar-guard/<buildId>/`, con directorios 700 y archivos privados 600. Ese respaldo sobrevive a borrar Library y a quitar la herramienta de Unity. **No lo publiques, no lo incluyas en Assets/Packages y no lo compartas con el avatar.** El registro de perfiles del proyecto está en UserSettings y no contiene los bytes secretos. Conserva una copia privada adicional si reinstalas el sistema.

Si la herramienta no encuentra Python, instala `python3` con el gestor de tu distribución. Si 9001 está ocupado por otro programa OSC, cierra o coordina ese programa; LAG no cambia sus puertos automáticamente.

## Actualizar desde 0.1.1

Importa 0.2.1 encima de la carpeta existente. Selecciona tu copia protegida y pulsa **Reconocer copia protegida existente**. Lee su clave de Library o te permite elegir el respaldo privado si Library ya no existe; comprueba que el buildId corresponde a esa copia. Detecta el ID publicado y prepara los lanzadores. No requiere regenerar ni volver a publicar un avatar funcional. Actualizar desde 0.2.0 sólo añade idiomas y conserva los perfiles existentes.

## Compatibilidad y límites

Validado: Unity 2022.3.22f1, SDK Avatars 3.10.5 y lilToon 2.3.4, Linux Editor nativo con Vulkan y Built-in Render Pipeline. El cliente recibe un avatar Windows PC; quien lo observa no necesita Linux. Android/Quest e iOS, URP/HDRP y otras versiones no están certificados.

Soporta variantes estándar lilToon opaque/cutout/transparent y outline. No Lite, Multi, Fur, Gem, Refraction, Tessellation ni shaders personalizados. Rechaza scripts ausentes, Cloth, MeshColliders, UV7/UV8 ocupados, mallas no legibles, normales no unitarias, deltas de normales de blendshapes, cambios animados de mesh o escala y huesos escalados. No elimina esas características para hacer pasar la validación.

Usa UV7/UV8 y añade cuatro Int sincronizados (32 bits), cuatro capas FX de 256 estados y shaders derivados. Conserva expresiones originales y copia las dependencias que modifica; los originales permanecen intactos. Los clips embebidos en modelos se extraen a archivos .anim, para no copiar archivos de modelo con geometría original. Si hay animaciones con cambios sin guardar, pide guardarlos antes de copiar; no guarda todos los assets del proyecto.

Las claves llegan a otros clientes para dibujar el avatar. Recuperarlas o analizar el shader permite reconstruir la malla. Si se bloquean shaders o animaciones mediante Safety, puede verse deformado. No se instala un bypass de Safety. Comprueba el rendimiento con el SDK.

## Desinstalar o volver al original

Usa tu avatar original. Las copias están en `Assets/LinuxAvatarGuardGenerated/<buildId>/`; borra sólo la que ya no quieras. Quitar `Assets/LinuxAvatarGuard` desinstala el Editor. Los respaldos privados se conservan fuera del proyecto y se borran aparte si ya no los necesitas. Detén el desbloqueador antes de quitar sus archivos.

## Licencia

MIT: uso, modificación y redistribución permitidos conservando LICENSE.txt. No se incluyen avatares, SDK, lilToon ni Gesture Manager; cada dependencia conserva su licencia. Los shaders generados incluyen la licencia lilToon de la instalación del usuario. Consulta THIRD-PARTY-NOTICES.md, RESEARCH.es.md y VALIDATION.es.md.
