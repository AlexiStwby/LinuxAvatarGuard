// SPDX-License-Identifier: MIT
// Only owned procedural fixtures and disposable projects. No client process is inspected.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LinuxAvatarGuard;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
#if LAG_VRCSDK
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
#endif
using Object = UnityEngine.Object;

public static class LAGMetadataValidation
{
    const string Folder = "Assets/MetadataFixture";
    const string Generated = "Assets/LinuxAvatarGuardGenerated/Research/";
    const string Output = "../evidence/metadata-guard";
    static readonly List<string> checks = new List<string>();
    static readonly List<float> visualErrors = new List<float>();
    static string tag;
    static void Check(bool value, string text) { if (!value) throw new Exception("METADATA_FAILED: " + text); checks.Add(text); }
    static bool Rejects(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } catch (IOException) { return true; } }
    static string Hash(byte[] data) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
    static byte[] Seed(string value) { using (var h = SHA256.Create()) return h.ComputeHash(Encoding.UTF8.GetBytes(value)); }
    static string PathOf(string name) => Generated + "metadata-" + tag + "-" + name;
    static void Save(string folder)
    { foreach (string path in AssetDatabase.FindAssets("", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Distinct()) foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path).Where(o => o)) AssetDatabase.SaveAssetIfDirty(o); }
    [Serializable] sealed class Map { public Name[] names; }
    [Serializable] sealed class Name { public string kind, identity, original, renamed; }
    static string Alias(GuardMetadataArtifact artifact, string kind, string original) =>
        JsonUtility.FromJson<Map>(artifact.PrivateMappingJson).names.Single(n => n.kind == kind && n.original == original).renamed;
    sealed class Fixture : IDisposable
    {
        public GameObject Root;
        public Mesh Mesh;
        public Material A, B;
        public AnimationClip First, Second;
        public AnimatorController Base;
        public AnimatorOverrideController Override;
        public AvatarMask Mask;
        public void Dispose() { if (Root) Object.DestroyImmediate(Root); }
    }
    static Fixture CreateFixture()
    {
        if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        AssetDatabase.CreateFolder("Assets", "MetadataFixture");
        var f = new Fixture { Root = new GameObject("OwnedMetadataAvatar") };
        var rig = new GameObject("PrivateRig"); rig.transform.SetParent(f.Root.transform, false);
        var bone = new GameObject("PrivateBone"); bone.transform.SetParent(rig.transform, false);
        f.Mesh = new Mesh { name = "PrivateMeshLabel", vertices = new[] { new Vector3(-.6f, -.5f, 0), new Vector3(.6f, -.5f, 0), new Vector3(-.6f, .5f, 0), new Vector3(.6f, .5f, 0) },
            normals = Enumerable.Repeat(Vector3.back, 4).ToArray(), tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 4).ToArray(),
            uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }, triangles = new[] { 0, 2, 1, 1, 2, 3 },
            bindposes = new[] { Matrix4x4.identity }, boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray() };
        foreach (int weight in new[] { 50, 100 })
            f.Mesh.AddBlendShapeFrame("InternalSmile", weight, new[] { Vector3.zero, Vector3.zero, new Vector3(-.1f * weight / 100, .12f * weight / 100, 0), new Vector3(.1f * weight / 100, .12f * weight / 100, 0) },
                Enumerable.Repeat(new Vector3(.02f * weight / 100, 0, 0), 4).ToArray(), Enumerable.Repeat(new Vector3(0, .01f * weight / 100, 0), 4).ToArray());
        foreach (string shape in new[] { "vrc.v_aa", "eyeBlinkLeft" }) f.Mesh.AddBlendShapeFrame(shape, 100, Enumerable.Repeat(new Vector3(0, .01f, 0), 4).ToArray(), new Vector3[4], new Vector3[4]);
        f.Mesh.RecalculateBounds(); AssetDatabase.CreateAsset(f.Mesh, Folder + "/SecretMesh.asset");
        var shader = Shader.Find("Unlit/Color"); Check(shader && shader.isSupported, "owned unlit Vulkan shader exists");
        f.A = new Material(shader) { name = "PrivateBlue", color = new Color(.15f, .6f, .9f, 1) };
        f.B = new Material(shader) { name = "PrivateOrange", color = new Color(.9f, .35f, .1f, 1) };
        AssetDatabase.CreateAsset(f.A, Folder + "/PrivateBlue.mat"); AssetDatabase.CreateAsset(f.B, Folder + "/PrivateOrange.mat");
        var body = new GameObject("PrivateBody"); body.transform.SetParent(f.Root.transform, false);
        var skin = body.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = f.Mesh; skin.sharedMaterial = f.A;
        skin.bones = new[] { bone.transform }; skin.rootBone = bone.transform; skin.updateWhenOffscreen = true; skin.SetBlendShapeWeight(0, 23);
        var accessory = new GameObject("PrivateAccessory"); accessory.transform.SetParent(f.Root.transform, false); accessory.transform.localPosition = new Vector3(1, 0, 0); accessory.transform.localScale = Vector3.one * .22f;
        accessory.AddComponent<MeshFilter>().sharedMesh = f.Mesh; accessory.AddComponent<MeshRenderer>().sharedMaterial = f.A;
        f.First = Clip(Folder + "/PrivateMotion.anim", f.A, f.B, false); f.Second = Clip(Folder + "/PrivateOverride.anim", f.A, f.B, true);
        f.Base = AnimatorController.CreateAnimatorControllerAtPath(Folder + "/PrivateController.controller");
        foreach (string parameter in new[] { "InternalBlend", "InternalGate", "InternalDirect", "InternalSpeed", "InternalCycle", "InternalTime", "InternalMirror", "IsLocal", "LAG_existing_0" })
            f.Base.AddParameter(parameter, parameter == "InternalGate" || parameter == "InternalMirror" || parameter == "IsLocal" ? AnimatorControllerParameterType.Bool : AnimatorControllerParameterType.Float);
        var pp = f.Base.parameters; foreach (var p in pp) if (p.name == "InternalSpeed") p.defaultFloat = 1; f.Base.parameters = pp;
        var nested = new BlendTree { name = "PrivateNestedTree", blendType = BlendTreeType.Direct, blendParameter = "InternalBlend", blendParameterY = "InternalDirect" };
        AssetDatabase.AddObjectToAsset(nested, f.Base); nested.AddChild(f.First); var children = nested.children; children[0].directBlendParameter = "InternalDirect"; nested.children = children;
        var tree = new BlendTree { name = "PrivateTree", blendType = BlendTreeType.Simple1D, blendParameter = "InternalBlend" };
        AssetDatabase.AddObjectToAsset(tree, f.Base); tree.AddChild(f.First, 0); tree.AddChild(nested, 1);
        var machine = f.Base.layers[0].stateMachine;
        var idle = machine.AddState("Idle", new Vector3(250, 150, 0)); idle.motion = f.First; idle.writeDefaultValues = false;
        var active = machine.AddState("Active", new Vector3(500, 150, 0)); active.motion = tree; active.writeDefaultValues = false;
        active.speedParameter = "InternalSpeed"; active.speedParameterActive = true; active.cycleOffsetParameter = "InternalCycle"; active.cycleOffsetParameterActive = true;
        active.timeParameter = "InternalTime"; active.timeParameterActive = false; active.mirrorParameter = "InternalMirror"; active.mirrorParameterActive = false;
        var transition = idle.AddTransition(active); transition.duration = 0; transition.hasExitTime = false; transition.AddCondition(AnimatorConditionMode.If, 0, "InternalGate");
        var nestedMachine = machine.AddStateMachine("Nested", new Vector3(700, 250, 0)); var nestedState = nestedMachine.AddState("NestedIdle"); nestedState.motion = f.First;
        var entry = nestedMachine.AddEntryTransition(nestedState); entry.AddCondition(AnimatorConditionMode.Greater, .1f, "InternalBlend");
        var any = nestedMachine.AddAnyStateTransition(nestedState); any.duration = 0; any.AddCondition(AnimatorConditionMode.If, 0, "InternalGate");
        f.Mask = new AvatarMask { name = "PrivateMask", transformCount = 4 };
        string[] paths = { "", "PrivateBody", "PrivateRig", "PrivateRig/PrivateBone" };
        for (int i = 0; i < paths.Length; i++) { f.Mask.SetTransformPath(i, paths[i]); f.Mask.SetTransformActive(i, true); }
        AssetDatabase.CreateAsset(f.Mask, Folder + "/PrivateMask.mask"); f.Base.AddLayer("InternalSynchronized");
        var layers = f.Base.layers; layers[0].avatarMask = f.Mask; layers[1].syncedLayerIndex = 0; layers[1].defaultWeight = 0; f.Base.layers = layers;
        f.Override = new AnimatorOverrideController(f.Base) { name = "PrivateOverrideController" }; f.Override[f.First] = f.Second;
        AssetDatabase.CreateAsset(f.Override, Folder + "/PrivateOverride.overrideController"); f.Root.AddComponent<Animator>().runtimeAnimatorController = f.Override;
        Save(Folder); Check(PrefabUtility.SaveAsPrefabAsset(f.Root, Folder + "/source.prefab"), "owned source prefab saved");
        return f;
    }
    static AnimationClip Clip(string path, Material a, Material b, bool reverse)
    {
        var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(path), frameRate = 60 };
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("PrivateBody", typeof(SkinnedMeshRenderer), "blendShape.InternalSmile"), AnimationCurve.Linear(0, reverse ? 90 : 10, 1, reverse ? 10 : 90));
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("PrivateRig/PrivateBone", typeof(Transform), "localEulerAnglesRaw.z"), AnimationCurve.Linear(0, reverse ? 15 : -15, 1, reverse ? -15 : 15));
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "InternalBlend"), AnimationCurve.Linear(0, 0, 1, 1));
        foreach (string renderer in new[] { "PrivateBody", "PrivateAccessory" }) AnimationUtility.SetObjectReferenceCurve(clip,
            EditorCurveBinding.PPtrCurve(renderer, renderer == "PrivateBody" ? typeof(SkinnedMeshRenderer) : typeof(MeshRenderer), "m_Materials.Array.data[0]"),
            new[] { new ObjectReferenceKeyframe { time = 0, value = reverse ? b : a }, new ObjectReferenceKeyframe { time = .5f, value = reverse ? a : b } });
        AssetDatabase.CreateAsset(clip, path); return clip;
    }
    static GuardMetadataOptions Internals() => new GuardMetadataOptions {
        InternalObjectPaths = new[] { "PrivateBody", "PrivateAccessory", "PrivateRig", "PrivateRig/PrivateBone" },
        InternalParameters = new[] { "InternalBlend", "InternalGate", "InternalDirect", "InternalSpeed", "InternalCycle", "InternalTime", "InternalMirror" },
        InternalBlendshapes = new[] { new GuardMetadataBlendshapes { rendererPath = "PrivateBody", names = new[] { "InternalSmile" } } }, RenameAnimatorLayers = true
    };
    static void MeshEqual(Mesh original, Mesh copy)
    {
        Check(original.vertices.SequenceEqual(copy.vertices) && original.normals.SequenceEqual(copy.normals) && original.tangents.SequenceEqual(copy.tangents), "positions, normals and tangents remain identical");
        Check(original.triangles.SequenceEqual(copy.triangles) && original.uv.SequenceEqual(copy.uv) && original.bindposes.SequenceEqual(copy.bindposes) && original.boneWeights.SequenceEqual(copy.boneWeights), "indices, UVs, bindposes and bone weights remain identical");
        Check(original.blendShapeCount == copy.blendShapeCount, "morph indices/count remain stable");
        for (int s = 0; s < original.blendShapeCount; s++)
        {
            Check(original.GetBlendShapeFrameCount(s) == copy.GetBlendShapeFrameCount(s), "morph frame count " + s);
            for (int frame = 0; frame < original.GetBlendShapeFrameCount(s); frame++)
            {
                var v = new Vector3[original.vertexCount]; var n = new Vector3[v.Length]; var t = new Vector3[v.Length];
                var cv = new Vector3[v.Length]; var cn = new Vector3[v.Length]; var ct = new Vector3[v.Length];
                original.GetBlendShapeFrameVertices(s, frame, v, n, t); copy.GetBlendShapeFrameVertices(s, frame, cv, cn, ct);
                Check(original.GetBlendShapeFrameWeight(s, frame) == copy.GetBlendShapeFrameWeight(s, frame) && v.SequenceEqual(cv) && n.SequenceEqual(cn) && t.SequenceEqual(ct), "morph position/normal/tangent frame identical " + s + "/" + frame);
            }
        }
    }
    static void Structure(Fixture f, GuardMetadataArtifact safe, GuardMetadataArtifact renamed)
    {
        Check(safe.Summary.assets > 0 && safe.Summary.functionalNamesPreserved && safe.Root.transform.Find("PrivateBody"), "conservative mode only changes asset labels");
        Check(safe.CopyOf(f.Mesh).GetBlendShapeName(0) == "InternalSmile" && safe.CopyOf(f.Base).parameters.Select(p => p.name).SequenceEqual(f.Base.parameters.Select(p => p.name)), "conservative mode retains morphs and parameter names");
        Check(renamed.Summary.objects == 4 && renamed.Summary.blendshapes == 1 && renamed.Summary.parameters == 7 && renamed.Summary.animatorLayers == 2, "explicit selection counts match report");
        MeshEqual(f.Mesh, renamed.CopyOf(f.Mesh));
        Check(renamed.CopyOf(f.Mesh).GetBlendShapeName(0) == Alias(renamed, "blendshape", "InternalSmile") && renamed.CopyOf(f.Mesh).GetBlendShapeName(1) == "vrc.v_aa" && renamed.CopyOf(f.Mesh).GetBlendShapeName(2) == "eyeBlinkLeft", "only the selected internal morph is renamed");
        var skin = renamed.Root.GetComponentInChildren<SkinnedMeshRenderer>(); Check(Mathf.Abs(skin.GetBlendShapeWeight(0) - 23) < .00001f && skin.bones[0] == skin.rootBone && skin.rootBone.IsChildOf(renamed.Root.transform), "initial morph weights and bone object references survive copying");
        var sourceController = f.Base; var controller = renamed.CopyOf(f.Base);
        Check(controller.parameters.Any(p => p.name == "IsLocal") && controller.parameters.Any(p => p.name == "LAG_existing_0") && !controller.parameters.Any(p => p.name == "InternalBlend"), "reserved/runtime parameters stay stable while selected internal names change");
        var layer = controller.layers[0]; var active = layer.stateMachine.states.Single(s => s.state.name == "Active").state;
        Check(active.speedParameter == Alias(renamed, "parameter", "InternalSpeed") && active.cycleOffsetParameter == Alias(renamed, "parameter", "InternalCycle") && active.timeParameter == Alias(renamed, "parameter", "InternalTime") && active.mirrorParameter == Alias(renamed, "parameter", "InternalMirror"), "all state parameter fields remap, including inactive fields");
        var idle = layer.stateMachine.states.Single(s => s.state.name == "Idle").state;
        Check(idle.transitions[0].conditions[0].parameter == Alias(renamed, "parameter", "InternalGate") && idle.transitions[0].destinationState == active, "transition condition and state destination remain consistent");
        var nested = layer.stateMachine.stateMachines[0].stateMachine;
        Check(nested.entryTransitions[0].conditions[0].parameter == Alias(renamed, "parameter", "InternalBlend") && nested.anyStateTransitions[0].conditions[0].parameter == Alias(renamed, "parameter", "InternalGate"), "nested entry and any-state conditions remap");
        var trees = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(controller)).OfType<BlendTree>().ToArray();
        Check(trees.Length == 2 && trees.All(t => t.blendParameter == Alias(renamed, "parameter", "InternalBlend")) && trees.Single(t => t.blendType == BlendTreeType.Direct).children[0].directBlendParameter == Alias(renamed, "parameter", "InternalDirect"), "nested blend trees and direct blend parameters remap");
        Check(controller.layers[1].syncedLayerIndex == sourceController.layers[1].syncedLayerIndex && controller.layers[1].defaultWeight == 0, "synchronized layer index/weight remain identical");
        Check(layer.stateMachine.states.All(s => s.position == Vector3.zero) && layer.stateMachine.stateMachines.All(s => s.position == Vector3.zero), "only editor graph positions are cleaned");
        var mask = renamed.CopyOf(f.Mask);
        Check(mask.GetTransformPath(1) == Alias(renamed, "object", "PrivateBody") && mask.GetTransformPath(3) == Alias(renamed, "object", "PrivateRig") + "/" + Alias(renamed, "object", "PrivateBone"), "AvatarMask paths follow the renamed hierarchy");
        var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(); renamed.CopyOf(f.Override).GetOverrides(pairs);
        Check(pairs.Any(p => p.Key == renamed.CopyOf(f.First) && p.Value == renamed.CopyOf(f.Second)), "override controller keeps native key/value clip mappings");
        var bindings = AnimationUtility.GetCurveBindings(renamed.CopyOf(f.Second));
        Check(bindings.Any(b => b.path == Alias(renamed, "object", "PrivateBody") && b.propertyName == "blendShape." + Alias(renamed, "blendshape", "InternalSmile")), "morph animation path/property remap together");
        Check(bindings.Any(b => b.path == "" && b.type == typeof(Animator) && b.propertyName == Alias(renamed, "parameter", "InternalBlend")), "animated Animator parameter curves remap");
        foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(renamed.CopyOf(f.Second)))
            Check(AnimationUtility.GetObjectReferenceCurve(renamed.CopyOf(f.Second), b).All(k => k.value == renamed.CopyOf(f.A) || k.value == renamed.CopyOf(f.B)), "object-reference keyframes target generated materials " + b.path);
        foreach (string path in AssetDatabase.FindAssets("", new[] { renamed.Folder + "/Assets" }).Select(AssetDatabase.GUIDToAssetPath)) Check(!Path.GetFileName(path).Contains("Private"), "generated asset filename excludes source label");
        string publicJson = File.ReadAllText(renamed.PublicManifestPath);
        Check(!publicJson.Contains("Private") && !publicJson.Contains("InternalSmile") && !publicJson.Contains("seed") && !publicJson.Contains("names"), "public report excludes source names and reversal map");
    }
    static void Denials(Fixture f)
    {
        string output = PathOf("denied");
        foreach (string reserved in new[] { "IsLocal", "LAG_existing_0", "Viseme", "FT/Face" }) Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), output, new GuardMetadataOptions { InternalParameters = new[] { reserved } })) && !Directory.Exists(output), "reserved parameter rejected before allocation " + reserved);
        foreach (string reserved in new[] { "vrc.v_aa", "eyeBlinkLeft" }) Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), output, new GuardMetadataOptions { InternalBlendshapes = new[] { new GuardMetadataBlendshapes { rendererPath = "PrivateBody", names = new[] { reserved } } } })) && !Directory.Exists(output), "reserved morph rejected before allocation " + reserved);
        Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), "Assets/../outside")), "path traversal rejected");
        Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, "not-a-build-id", output)), "invalid build id rejected");
        Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), output, seed: new byte[4])), "incorrect seed length rejected");
        var unknown = f.Root.AddComponent(TypeCache.GetTypesDerivedFrom<MonoBehaviour>().Single(t => t.Name == "LAGMetadataUnknown"));
        Check(unknown, "runtime unknown-component probe is actually attached");
        Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), output)), "unknown integration rejected before instantiation"); Object.DestroyImmediate(unknown);
        var duplicate = new GameObject("PrivateBody"); duplicate.transform.SetParent(f.Root.transform, false);
        Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), output, Internals())), "ambiguous sibling paths rejected"); Object.DestroyImmediate(duplicate);
        var nestedAnimator = f.Root.transform.Find("PrivateRig").gameObject.AddComponent<Animator>();
        Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), output, Internals())), "multiple/nested Animator roots rejected for functional names"); Object.DestroyImmediate(nestedAnimator);
        AnimationUtility.SetAnimationEvents(f.First, new[] { new AnimationEvent { functionName = "ExternalConsumer", stringParameter = "InternalBlend", time = .1f } }); Save(Folder);
        Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), output, Internals())), "external animation callbacks prevent functional renaming");
        AnimationUtility.SetAnimationEvents(f.First, Array.Empty<AnimationEvent>()); Save(Folder);
        var behaviour = f.Base.layers[0].stateMachine.states[0].state.AddStateMachineBehaviour(TypeCache.GetTypesDerivedFrom<StateMachineBehaviour>().Single(t => t.Name == "LAGMetadataBehaviour"));
        Check(behaviour, "runtime state-machine probe is actually attached"); Save(Folder);
        Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), output, Internals())), "unreviewed state-machine behaviours prevent functional renaming"); Object.DestroyImmediate(behaviour, true); Save(Folder);
        foreach (var checkpoint in new[] { GuardMetadataCheckpoint.AssetsCopied, GuardMetadataCheckpoint.NamesRemapped, GuardMetadataCheckpoint.PrefabSaved })
            Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), output, Internals(), checkpoint: at => { if (at == checkpoint) throw new IOException("owned injected failure"); })) && !Directory.Exists(output) && !File.Exists(output + ".meta"), "rollback removes the entire partial output at " + checkpoint);
        var oldColor = f.A.color;
        Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), output, checkpoint: at => { if (at == GuardMetadataCheckpoint.AssetsCopied) f.A.color = Color.magenta; })) && !Directory.Exists(output) && f.A.color == Color.magenta, "late source asset mutation is detected and caller edit preserved");
        f.A.color = oldColor; Save(Folder);
        Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), output, checkpoint: at => {
            if (at == GuardMetadataCheckpoint.PrefabSaved) AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets("t:Material", new[] { output })[0])).color = Color.magenta;
        })) && !Directory.Exists(output), "late in-memory generated material mutation cancels output");
        Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, new string('d', 32), output, checkpoint: at => {
            if (at == GuardMetadataCheckpoint.PrefabSaved) File.AppendAllText(output + "/metadata.prefab", "\n# owned late mutation\n");
        })) && !Directory.Exists(output), "late persisted prefab mutation cancels output");
    }
    static Color32[] Render(Camera camera, string path)
    {
        var rt = new RenderTexture(512, 384, 24, RenderTextureFormat.ARGB32); var image = new Texture2D(512, 384, TextureFormat.RGBA32, false); var old = RenderTexture.active;
        try { camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, 512, 384), 0, 0); image.Apply(); File.WriteAllBytes(path, image.EncodeToPNG()); return image.GetPixels32(); }
        finally { camera.targetTexture = null; RenderTexture.active = old; Object.DestroyImmediate(image); Object.DestroyImmediate(rt); }
    }
    static void Compare(Camera camera, GameObject source, GameObject generated, string prefix)
    {
        foreach (var t in source.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 28;
        foreach (var t in generated.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 29;
        camera.cullingMask = 1 << 28; var original = Render(camera, prefix + "-original.png");
        camera.cullingMask = 1 << 29; var protectedImage = Render(camera, prefix + "-metadata.png");
        double sum = 0; for (int i = 0; i < original.Length; i++) sum += (Math.Abs(original[i].r - protectedImage[i].r) + Math.Abs(original[i].g - protectedImage[i].g) + Math.Abs(original[i].b - protectedImage[i].b)) / (255d * 3);
        float error = (float)(sum / original.Length); visualErrors.Add(error); Check(error <= .00001f, "native render equivalence " + Path.GetFileName(prefix) + " error=" + error);
        Check(original.Any(p => p.b > 100 || p.r > 100), "native comparison contains visible geometry " + Path.GetFileName(prefix));
    }
    static void Native(Fixture f, GuardMetadataArtifact renamed)
    {
        string output = Output + "/" + tag + "/native"; Directory.CreateDirectory(output);
        var originalApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneLinux64); bool originalAuto = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64);
        AssetBundle bundle = null; GameObject before = null, after = null, cameraObject = null;
        bool originalActive = f.Root.activeSelf; f.Root.SetActive(false); renamed.Root.SetActive(false);
        try
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64, false); PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64, new[] { GraphicsDeviceType.Vulkan });
            var manifest = BuildPipeline.BuildAssetBundles(output, new[] { new AssetBundleBuild { assetBundleName = "metadata.bundle", assetNames = new[] { Folder + "/source.prefab", renamed.PrefabPath,
                AssetDatabase.GetAssetPath(f.Second), AssetDatabase.GetAssetPath(renamed.CopyOf(f.Second)) } } }, BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneLinux64);
            Check(manifest && manifest.GetAllAssetBundles().Length == 1, "native Linux/Vulkan bundle built");
            bundle = AssetBundle.LoadFromFile(Path.GetFullPath(output + "/metadata.bundle")); Check(bundle, "native bundle reopened");
            before = Object.Instantiate(bundle.LoadAsset<GameObject>((Folder + "/source.prefab").ToLowerInvariant())); after = Object.Instantiate(bundle.LoadAsset<GameObject>(renamed.PrefabPath.ToLowerInvariant()));
            before.SetActive(true); after.SetActive(true); before.GetComponent<Animator>().enabled = false; after.GetComponent<Animator>().enabled = false;
            var sourceClip = bundle.LoadAsset<AnimationClip>(AssetDatabase.GetAssetPath(f.Second).ToLowerInvariant()); var clip = bundle.LoadAsset<AnimationClip>(AssetDatabase.GetAssetPath(renamed.CopyOf(f.Second)).ToLowerInvariant());
            Check(sourceClip && clip, "both native animation clips reopened");
            cameraObject = new GameObject("OwnedMetadataCamera"); var camera = cameraObject.AddComponent<Camera>(); camera.transform.position = new Vector3(.3f, 0, -3); camera.transform.LookAt(new Vector3(.3f, 0, 0));
            camera.orthographic = true; camera.orthographicSize = .9f; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.025f, .025f, .025f); camera.nearClipPlane = .01f; camera.farClipPlane = 10;
            foreach (float time in new[] { 0, .25f, .5f, .75f, 1f })
            { sourceClip.SampleAnimation(before, time); clip.SampleAnimation(after, time); Compare(camera, before, after, output + "/pose-" + time.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)); }
            foreach (var root in new[] { before, after }) { var animator = root.GetComponent<Animator>(); animator.enabled = true; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.Rebind(); animator.Update(0); }
            before.GetComponent<Animator>().SetBool("InternalGate", true); after.GetComponent<Animator>().SetBool(Alias(renamed, "parameter", "InternalGate"), true);
            foreach (var root in new[] { before, after }) { var animator = root.GetComponent<Animator>(); animator.Update(.1f); animator.Update(.2f); }
            Check(before.GetComponent<Animator>().GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash("Active") && after.GetComponent<Animator>().GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash("Active"), "native remapped bool drives the same real transition");
            Compare(camera, before, after, output + "/animator-active");
        }
        finally
        {
            if (before) Object.DestroyImmediate(before); if (after) Object.DestroyImmediate(after); if (cameraObject) Object.DestroyImmediate(cameraObject); if (bundle) bundle.Unload(true);
            f.Root.SetActive(originalActive); renamed.Root.SetActive(originalActive); PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64, originalAuto); PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64, originalApis);
        }
    }
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("LAG_METADATA_AUDIT_ALLOWED") != "owned-unity-only" || Path.GetFileName(Path.GetDirectoryName(Application.dataPath)) != "ValidationProject")
            throw new InvalidOperationException("Only the explicitly enabled disposable validation project is allowed.");
        checks.Clear(); visualErrors.Clear(); var oldColorSpace = PlayerSettings.colorSpace;
        tag = Environment.GetEnvironmentVariable("LAG_METADATA_COLORSPACE") == "Linear" ? "linear" : "gamma";
        PlayerSettings.colorSpace = tag == "linear" ? ColorSpace.Linear : ColorSpace.Gamma;
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        string privateFolder = Path.GetFullPath(Output + "/private-" + tag); GuardMetadataArtifact safe = null, renamed = null, replay = null, diverse = null;
        try
        {
            foreach (string name in new[] { "safe", "internal", "replay", "diverse", "denied" }) if (AssetDatabase.IsValidFolder(PathOf(name))) AssetDatabase.DeleteAsset(PathOf(name));
            using (var f = CreateFixture())
            {
                Check(EditorSceneManager.SaveScene(f.Root.scene, Folder + "/clean-work.unity"), "owned clean working scene saved for dirty-state verification");
                var sourceFiles = Directory.GetFiles(Folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Hash(File.ReadAllBytes(p)));
                var seed = Seed("owned-metadata-seed"); string id = new string('a', 32);
                safe = GuardMetadataGuard.Prepare(f.Root, id, PathOf("safe"), seed: seed);
                renamed = GuardMetadataGuard.Prepare(f.Root, id, PathOf("internal"), Internals(), seed);
                replay = GuardMetadataGuard.Prepare(f.Root, id, PathOf("replay"), Internals(), seed);
                Check(replay.PrivateMappingJson == renamed.PrivateMappingJson, "same source/build/seed reproduces all alias mappings");
                diverse = GuardMetadataGuard.Prepare(f.Root, id, PathOf("diverse"), Internals(), Seed("owned-different-seed"));
                Check(!f.Root.scene.isDirty && EditorSceneManager.IsPreviewScene(renamed.Root.scene), "temporary metadata copies preserve the clean working-scene flag in separate preview scenes");
                Check(Alias(diverse, "blendshape", "InternalSmile") != Alias(renamed, "blendshape", "InternalSmile") && Alias(diverse, "parameter", "InternalBlend") != Alias(renamed, "parameter", "InternalBlend"), "another seed changes internal metadata aliases");
                Structure(f, safe, renamed);
                string privatePath = privateFolder + "/metadata-map.json";
                if (Directory.Exists(privateFolder)) Directory.Delete(privateFolder, true);
                renamed.SavePrivateMapping(privatePath); Check(File.Exists(privatePath) && File.ReadAllText(privatePath) == renamed.PrivateMappingJson, "private reversal map is saved outside Assets");
                Check(Rejects(() => GuardMetadataGuard.WritePrivateMap(privateFolder + "/wrong-build.metadata.json", renamed.PrivateMappingJson, new string('e', 32))) && !File.Exists(privateFolder + "/wrong-build.metadata.json"), "another build's private map is rejected before writing");
                Check(Rejects(() => renamed.SavePrivateMapping(Path.GetFullPath("Packages/metadata-map.json"))), "private map under Packages rejected");
                Check(Rejects(() => renamed.SavePrivateMapping(Path.GetFullPath(renamed.Folder + "/metadata-map.json"))), "private map under Assets rejected");
                Check(Rejects(() => diverse.SavePrivateMapping(privatePath)) && File.ReadAllText(privatePath) == renamed.PrivateMappingJson, "existing private map preserved against a different build mapping");
                Check(Rejects(() => GuardMetadataGuard.Prepare(f.Root, id, renamed.Folder)), "existing generated output is preserved");
                string link = Path.GetFullPath(privateFolder + "/owned-link");
                using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/ln", "-s " + Path.GetFullPath(renamed.Folder) + " " + link) { UseShellExecute = false }))
                { process.WaitForExit(); Check(process.ExitCode == 0, "owned symbolic-link test fixture created"); }
                Check(Rejects(() => renamed.SavePrivateMapping(link + "/metadata-map.json")) && !File.Exists(renamed.Folder + "/metadata-map.json"), "symlink cannot route the private map into Assets");
                File.Delete(link);
                Native(f, renamed);
                Check(sourceFiles.All(p => Hash(File.ReadAllBytes(p.Key)) == p.Value), "source assets and meta remain byte identical after positive preparation/native replay");
                Denials(f);
                File.WriteAllText(Output + "/" + tag + "/results.json", JsonUtility.ToJson(new Report { unity = Application.unityVersion, graphics = SystemInfo.graphicsDeviceType.ToString(), colorSpace = tag,
                    checks = checks.ToArray(), visualErrors = visualErrors.ToArray(), maximumVisualError = visualErrors.Max(), sourcePreserved = true, sdkProcessed = false }, true));
                Debug.Log("LAG_METADATA_SUCCESS " + checks.Count + " checks, " + visualErrors.Count + " native comparisons, " + tag);
            }
        }
        finally { safe?.Dispose(); renamed?.Dispose(); replay?.Dispose(); diverse?.Dispose(); PlayerSettings.colorSpace = oldColorSpace; }
    }
    public static void RunBatch()
    {
        try { Run(); EditorApplication.Exit(0); }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }
    [Serializable] sealed class Report { public string unity, graphics, colorSpace; public string[] checks; public float[] visualErrors; public float maximumVisualError; public bool sourcePreserved, sdkProcessed; }
}
