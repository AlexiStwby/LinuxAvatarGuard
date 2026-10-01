# Release validation — 0.2.1

## 0.2.1 localization

- Compiled successfully in the existing Linux Unity project, with no script errors.
- 116 translation entries verified in Spanish, English and Japanese. All wizard source messages and displayed validation-error prefixes have a translation.
- Spanish verified as the default when no language preference exists; invalid stored values safely fall back to Spanish.
- Language selection writes an Editor preference, without changing avatar assets, profiles or key files. Existing dirty scene state is preserved.
- English and Japanese wizard layouts visually reviewed in real Editor captures. Japanese characters rendered correctly in the tested Linux environment.
- Asset names and paths are preserved in dynamic translated errors; nested OSC validation errors are translated.
- Editor-window reopening and a domain reload retain the selected language. Actual 0.2.1 package import without the SDK is checked separately from the baseline below.

## 0.2.0 mesh/OSC baseline

The mesh decoder, key format, OSC transport and avatar generation algorithm are unchanged in 0.2.1. The following validation was performed for 0.2.0, not repeated avatar uploads for this localization update.

Validated with native Linux Unity 2022.3.22f1 / Vulkan, VRChat SDK Avatars 3.10.5 and lilToon 2.3.4.

- 26 core mesh, shader, animation/controller and preservation checks passed.
- Six Python OSC tests passed, including UDP transport, matching-avatar gating, private-file validation, OSC bundles, detection of an already loaded avatar via a test OSCQuery service, and stopping sends when the avatar changes.
- Wizard produced a protected prefab, separate upload scene, private backup, shell and desktop launchers. Source prefab bytes, working-scene selection and dirty state remained unchanged.
- Existing 0.1.1 avatar migrated without regeneration/reupload; its published ID was linked from the scene.
- Start/stop buttons launched and stopped their own OSC process. A duplicate port is reported without terminating unrelated programs.
- Gesture Manager 3.9.9 Play Mode preview used its parameter API and the FX path; the four parameters matched the private configuration. Edit preview alone is not an Animator validation.
- Official SDK local build of a wizard-generated avatar succeeded. No automatic publication was performed.
- A real avatar on the unchanged mesh/shader algorithm was visually confirmed working by its owner inside VRChat after OSC configuration. A second real user's synchronization is still pending; the Gesture Manager remote clone passed simulation checks.

- Actual public .unitypackage extracted and imported into an SDK-free Unity project: compilation and missing-dependency detection passed.
- Imported animation clip extraction preserved its curves and excluded the source container containing an original mesh.
- Unity export contains only 19 allowlisted tool assets; generated avatar dependencies, keys and Python bytecode are excluded. Skinning and compatibility remain experimental; these tests do not certify every avatar, pose, plugin or shader version.

No third-party avatar data, private keys, accounts, scene fixtures or avatar screenshots are included in the public download.
