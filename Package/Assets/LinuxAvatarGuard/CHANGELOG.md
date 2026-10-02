# Changelog

## Unreleased — security foundation
- Editor-only codec contracts and a typed legacy program; existing decoder, UV7/UV8 and OSC format remain compatible.
- Versioned public/private build metadata explicitly reports zero protected textures for the legacy codec.
- Preparation includes backup, profile and scene in its failure handling; failed copies are removed and recorded privately.
- Incomplete profiles are rejected by scene preview/opening and OSC start; legacy profiles remain supported.
- Independent compatibility tests, fault injection and controlled RenderDoc Vulkan analysis on synthetic Unity content.
- This stage does not introduce the polymorphic codec or claim resistance to GPU capture.

## 0.2.1
- ES / EN / JP language selector in the wizard, with Spanish as the default.
- Immediate translation with a persistent Editor preference shared between projects.
- Localized wizard controls, notices, displayed validation errors, OSC status, tool dialogs and protected-material Inspector.
- English instructions updated to match the English UI; Japanese quick start added.
- Existing profiles, keys, menu paths and uploaded avatars are preserved; no regeneration or reupload is required.

## 0.2.0
- Three-step Unity wizard, opened once after installation.
- Automatic avatar-root detection, compatibility validation and independent upload-scene generation.
- Private backups outside Library; per-avatar shell/desktop launcher and start/stop buttons.
- Link the published ID directly from the scene, without JSON editing.
- Detect an already loaded VRChat avatar through local OSCQuery; show key reception confirmation when available.
- Gesture Manager Play Mode preview applies its parameter API instead of bypassing Animator.
- Recognize existing 0.1.1 protected copies without reuploading.
- Build clones in isolated preview scenes; copy dependencies without global asset-save calls.
- Extract imported animation clips without copying model files that contain unprotected meshes.
- Spanish/English documentation, MIT license, notices and a sanitized Booth download.

## 0.1.1
- Preserve SDK default expression parameters before enabling custom expressions.
- Real avatar testing, SDK Windows bundle build, Gesture Manager/OSC and owner-confirmed VRChat appearance.
