# Linux Avatar Guard 0.2.0 — Free experimental release

Mesh obfuscation for PC VRChat avatars using lilToon. Native Linux Unity Editor + Vulkan only. Not strong encryption: does not protect textures or prevent GPU capture or recovery from synchronized keys.

## Install

Install VRChat SDK Avatars and lilToon in your avatar project using ALCOM/VCC. Install Python 3 from your Linux distribution. Open Unity with `-force-vulkan` and import LinuxAvatarGuard-0.2.0.unitypackage. The wizard opens automatically once, or use Tools > Linux Avatar Guard > Asistente. The current Editor UI is Spanish; buttons are listed below.

## Three steps

1. Select your working avatar. **Comprobar y preparar avatar** validates it, creates an independent protected prefab and a separate upload scene, and backs up the private key outside the project.
2. **Abrir escena para subir** opens the prepared scene and the official SDK panel. **Ver desbloqueado / Ver bloqueado** preview unlocked/locked appearance. For animation testing, enter Play Mode and select the avatar in Gesture Manager before using the buttons. Upload to Windows PC with the SDK. The copy preserves any existing Blueprint ID: check it before publishing. LAG never uploads automatically.
3. After upload, **Vincular avatar publicado** detects the ID without editing JSON. Enable OSC in VRChat and press **Iniciar desbloqueo OSC**. It detects an already loaded avatar through local OSCQuery when available; otherwise switch away and back. **Detener desbloqueo** stops it.

**A deformed mesh means the avatar is locked.** Start the unlocker and check the linked ID. **Abrir lanzador para usar sin Unity** opens the private folder with Desbloquear.sh and Desbloquear.desktop. Your desktop may require trusting the shortcut; the shell launcher can also be run with Bash. Only one process can use OSC output port 9001.

Private key/launcher folder: `${XDG_DATA_HOME:-~/.local/share}/linux-avatar-guard/<buildId>/`. It survives deleting Unity Library. Never distribute this folder or put keys in Assets/Packages. Directory permissions 700, private files 600. No Python libraries or Windows executables are required.

From 0.1.1: import the new package, select the protected copy and use **Reconocer copia protegida existente**. If the Library key is missing, choose your private backup. Existing working avatars do not need rebuilding or reuploading.

Tested baseline: Unity 2022.3.22f1, SDK Avatars 3.10.5, lilToon 2.3.4, Built-in RP. Linux is required for preparation/unlocking; other PC clients can render the avatar. No certified Android/Quest/iOS, URP/HDRP, custom shaders, or other package versions. Skinning remains experimental: test gestures, visemes, PhysBones and extreme poses.

MA/AAO/VRCFury require a baked final COPY before protection. Unsupported meshes, shader variants, Cloth, MeshColliders, UV7/8 usage, normal-changing blendshapes and scale/mesh animation are rejected rather than removed. Adds 32 synced bits, shader copies and 1,024 FX states. Blocking custom shaders/animations can expose the locked appearance. No Safety bypass.

MIT license. No avatars or third-party packages are redistributed. See README.es.md for full restrictions and THIRD-PARTY-NOTICES.md for references.
