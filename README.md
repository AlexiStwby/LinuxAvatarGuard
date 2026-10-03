# Linux Avatar Guard

Gratis, open source y experimental. Ofuscación de geometría para avatares VRChat PC con lilToon, preparada desde Unity nativo para Linux con Vulkan. La herramienta crea una copia independiente; conserva el avatar original y guarda las claves fuera del proyecto.

**[Descargar 0.2.1](https://github.com/AlexiStwby/LinuxAvatarGuard/releases/tag/v0.2.1)** · [Guía en español](Package/Assets/LinuxAvatarGuard/README.es.md) · [English guide](Package/Assets/LinuxAvatarGuard/QUICKSTART.en.md) · [日本語ガイド](Package/Assets/LinuxAvatarGuard/QUICKSTART.jp.md) · [MIT](LICENSE)

![Linux Avatar Guard](dist/booth/Cover.png)

## Instalación y uso

1. Prepara un proyecto de avatar con VRChat SDK Avatars, lilToon y Python 3. Abre Unity en Linux con Vulkan e importa `LinuxAvatarGuard-0.2.1.unitypackage` desde la release.
2. Selecciona el avatar y abre **Tools > Linux Avatar Guard > Asistente**. Pulsa **Comprobar y preparar avatar** y revisa la copia en su escena de subida.
3. Publica con el SDK, pulsa **Vincular avatar publicado** y después **Iniciar desbloqueo OSC** con OSC activado en VRChat. También puedes usar el lanzador privado sin mantener Unity abierto.

El asistente permite elegir ES / EN / JP arriba de la ventana; español es el idioma predeterminado. El cambio es inmediato y se guarda entre sesiones del Editor. No necesitas editar JSON ni instalar módulos de Python. Desde 0.1.1 puedes reconocer una copia existente sin regenerar o volver a publicar un avatar funcional. La actualización desde 0.2.0 conserva los perfiles existentes.

Validado con Unity 2022.3.22f1, SDK Avatars 3.10.5 y lilToon 2.3.4 en Built-in Render Pipeline. La preparación es exclusiva de Linux; el avatar publicado es Windows PC. Gesture Manager es opcional para comprobar las capas FX en Play Mode.

## Alcance

Es ofuscación, no cifrado fuerte. No protege texturas, no impide capturas GPU y no garantiza impedir toda extracción. Las claves se sincronizan para que otros clientes dibujen el avatar y pueden recuperarse. Sin OSC, con claves incorrectas o con shaders/animaciones bloqueados por Safety, la malla puede verse deformada.

Skinning y compatibilidad siguen siendo experimentales. Para Modular Avatar, Avatar Optimizer o VRCFury, hornea primero una copia final; no se eliminan automáticamente sus componentes. Consulta la [guía](Package/Assets/LinuxAvatarGuard/README.es.md) para variantes lilToon compatibles, restricciones y copias de seguridad.

## Código y desarrollo

- `Package/Assets/LinuxAvatarGuard/`: scripts del Editor, desbloqueador OSC y documentación que se distribuyen a Unity.
- `Tests/`: pruebas genéricas de geometría, shaders y OSC; no contienen modelos de terceros.
- `dist/booth/`: imágenes originales, descripción y validación de la release. Los archivos descargables se publican en GitHub Releases.

Para trabajar desde el código, copia `Package/Assets/LinuxAvatarGuard/` y su archivo `.meta` a `Assets/` en tu proyecto compatible. No copies `Tests/` al proyecto de un usuario final.

El código en `main` incorpora una fundación de seguridad aún sin nueva release: contrato del codec legacy, manifests versionados y recuperación ante fallos de preparación. La [auditoría](docs/SECURITY_ARCHITECTURE_CURRENT.md), el [diseño del codec](docs/POLYMORPHIC_CODEC_DESIGN.md) y el [estado del roadmap](docs/ROADMAP_STATUS.md) describen los resultados y el trabajo pendiente. El [prototipo polimórfico estático](docs/STATIC_POLYMORPHIC_PROTOTYPE.md) está implementado como API de investigación opt-in; el asistente de avatares conserva legacy. El [experimento GPU](docs/GPU_CAPTURE_THREAT_MODEL.md) usa RenderDoc exclusivamente sobre contenido sintético propio en Unity.

La [derivación por malla/binding](docs/PER_MESH_DERIVATION.md) separa programas y payloads y conserva el contexto de reproducción en archivos privados fuera de Unity/Git. Los valores de runtime siguen compartidos y observables.

La [evaluación de 100 builds sintéticos](docs/CODEC_DIVERSITY_RESULTS.md) mide diversidad, reapertura de bundles, renders y extracción adaptativa. Detectó y corrigió la aceptación de un programa sin dependencia de clave. La [asignación dinámica de atributos](docs/DYNAMIC_ATTRIBUTE_ALLOCATION.md) elige dos UV demostrablemente libres entre los índices 4–7, con contrato lilToon revisado, validación de mutaciones y respaldos compatibles. Las pruebas cubren doce layouts y seis pares en bundles Vulkan; el extractor adaptativo sigue reconstruyendo la geometría. Estos cambios todavía no habilitan el codec nuevo en el asistente.

El [ShaderForge contextual](docs/GENERATED_SHADER_FORGE.md) añade copias por renderer/slot y remapea los cambios de material de clips/controllers de un Animator genérico. Sigue limitado a mallas rígidas de investigación; el asistente utiliza la ruta de avatares ya validada.

El [benchmark de rendimiento](docs/PERFORMANCE_BENCHMARK.md) compara original, legacy y ShaderForge en un player Linux/Vulkan propio: 36 ejecuciones, rondas alternadas, CPU/GPU, recursos y shaders compilados. Conserva la apariencia desbloqueada y cuantifica el coste de copias por binding; los resultados describen fixtures rígidas y el hardware medido.

La [investigación de TextureGuard](docs/TEXTURE_GUARD_RESEARCH.md) y su [piloto de albedo opaco](docs/TEXTURE_GUARD_PROTOTYPE.md) añaden copias de textura/material con programas reproducibles por fuente/scope. Se validan Gamma/Linear, Point/Bilinear, Repeat/Clamp y bundles Vulkan/Windows con 586 comprobaciones y 518 imágenes de contenido propio. El extractor específico sigue recuperando los datos al disponer del HLSL y los parámetros. Es una API de investigación sin integración en el asistente; fuentes comprimidas y funciones completas de avatar requieren las siguientes etapas; V2 añade mipmaps mediante una API opt-in separada.

La [integración contextual de TextureGuard](docs/TEXTURE_GUARD_INTEGRATION.md) añade albedos por renderer/slot/material, respaldo privado schema 4 compatible con schemas anteriores, RGB24 y remapeo de material swaps y ST animado. La copia combina los decoders de geometría y albedo y valida sus dependencias sin modificar los originales. Se prueba en fixtures rígidas propias y se mide en un player Linux/Vulkan independiente; la continuación de mipmaps/skinning amplía el contrato de investigación, mientras compresión, avatar Humanoid y postprocesamiento del SDK siguen pendientes antes de llevarla al asistente.

La [continuación de mipmaps y skinning](docs/MIP_SKIN_RESEARCH.md) añade una pirámide de albedo con niveles codificados por separado, filtros Point/Bilinear/Trilinear y un codec lineal específico para huesos y blendshapes. `PrepareWithFeatures` combina mallas rígidas/skinned de un Animator genérico en copias auditadas; el respaldo schema 5 sigue leyendo schemas 1–4. La validación utiliza bundles y players propios Linux/Vulkan con GPU Skinning habilitado y deshabilitado. La integración con avatares Humanoid/SDK, culling completo, calidad dinámica y rendimiento de esta ruta aún requieren pruebas antes de llevarla al asistente.

[MetadataGuard (etapa 13)](docs/METADATA_GUARD.md) añade copias de metadatos con aliases por build, respaldo privado y rollback. Su modo conservador mantiene los nombres funcionales de VRChat; las selecciones internas genéricas remapean curvas, morphs, parámetros, máscaras y overrides de forma conjunta. `PrepareWithMetadata` integra únicamente labels/archivos sobre MeshGuard legacy como opt-in. La ruta predeterminada y la descarga 0.2.1 siguen siendo las publicadas. Los nombres ofuscados no impiden una extracción adaptativa o GPU.

La [investigación de FingerprintGuard (etapa 14)](docs/FINGERPRINT_RESEARCH.md) incorpora una identidad privada de 256 bits independiente de OSC y un estudio reproducible por CPU de marcas geométricas y de albedo. El [piloto geométrico de la etapa 15](docs/MESH_FINGERPRINT_PROTOTYPE.md) añade un núcleo Float32 probado por CPU y un adaptador Unity compilado para generar copias estáticas con respaldo privado. La ejecución del adaptador, la apariencia, FBX y SDK siguen pendientes. Se registran límites de cobertura, recorte, compresión y atribución ambigua; el verificador aún no ofrece confianza calibrada. Estas APIs de investigación permanecen fuera del asistente y del unitypackage estable.

Pruebas OSC, desde la raíz del repositorio:

```sh
python3 -m unittest discover -s Tests -p 'test_osc.py' -v
```

Para exportar, usa **Tools > Linux Avatar Guard > Exportar herramienta para distribuir** en Unity y guarda el archivo como `dist/LinuxAvatarGuard-0.2.1.unitypackage`. Después ejecuta `python3 Tests/build_release.py` para crear los ZIP públicos y sus hashes. El exportador excluye dependencias de avatares y claves. Consulta el [informe de validación](dist/booth/RELEASE-VALIDATION.md).

Antes de publicar una release con los cambios de desarrollo, actualiza su versión y verifica el nuevo unitypackage importándolo en un proyecto limpio. Las comprobaciones de esta continuación usan el código fuente copiado; la descarga 0.2.1 conserva sus binarios publicados.

Aceptamos issues y pull requests. Indica versiones de Unity/SDK/lilToon y pasos para reproducir el problema; no adjuntes claves privadas ni avatares de terceros sin permiso.

## English

Free, MIT-licensed experimental mesh obfuscation for VRChat PC avatars with lilToon. Preparation requires native Linux Unity with Vulkan; the uploaded avatar targets Windows PC. The wizard creates an independent copy, keeps private keys outside the project, and provides a local OSC launcher. The editor UI supports Spanish (default), English and Japanese; an [English guide](Package/Assets/LinuxAvatarGuard/QUICKSTART.en.md) is included.

Download the `.unitypackage` from [Releases](https://github.com/AlexiStwby/LinuxAvatarGuard/releases), prepare a copy, upload through the official SDK, link its published ID, and start OSC. This is not strong encryption or complete anti-ripping protection. Textures and GPU capture are not protected; synchronized keys and shaders can be analyzed to reconstruct the mesh.

## Licencia y referencias

[MIT](LICENSE). No se incluyen avatares externos, SDK, lilToon ni Gesture Manager. Cada dependencia conserva su licencia. Consulta los [avisos de terceros](Package/Assets/LinuxAvatarGuard/THIRD-PARTY-NOTICES.md) y la [investigación de proyectos públicos](Package/Assets/LinuxAvatarGuard/RESEARCH.es.md).
