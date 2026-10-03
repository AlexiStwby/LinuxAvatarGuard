// SPDX-License-Identifier: MIT
#if UNITY_EDITOR && LAG_VRCSDK
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LinuxAvatarGuard;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

public static class LAGMetadataSdkValidation
{
    const string Folder = "Assets/MetadataSdkFixture";
    static readonly List<string> checks = new List<string>();
    static void Check(bool ok, string text) { if (!ok) throw new Exception("METADATA_SDK_FAILED: " + text); checks.Add(text); }
    static void Save() { foreach (string path in AssetDatabase.FindAssets("", new[] { Folder }).Select(AssetDatabase.GUIDToAssetPath).Distinct()) foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path).Where(o => o)) AssetDatabase.SaveAssetIfDirty(o); }
    static string[] Entries(string folder) => Directory.Exists(folder) ? Directory.GetFileSystemEntries(folder).OrderBy(s => s).ToArray() : Array.Empty<string>();
    static Component Dynamics(GameObject root, string type, string parameter)
    {
        var componentType = TypeCache.GetTypesDerivedFrom<Component>().Single(t => t.FullName == type);
        var component = root.AddComponent(componentType); var so = new SerializedObject(component);
        var field = so.FindProperty("parameter"); Check(field != null && field.propertyType == SerializedPropertyType.String, "installed dynamics exposes its parameter field " + componentType.Name);
        field.stringValue = parameter; so.ApplyModifiedPropertiesWithoutUndo(); return component;
    }
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("LAG_METADATA_AUDIT_ALLOWED") != "owned-unity-only" || Path.GetFileName(Path.GetDirectoryName(Application.dataPath)) != "ValidationProject")
            throw new InvalidOperationException("Only the explicitly enabled disposable SDK fixture is allowed.");
        checks.Clear(); string previousXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string registry = Path.GetFullPath("UserSettings/LinuxAvatarGuardProfiles.json"); byte[] originalRegistry = File.Exists(registry) ? File.ReadAllBytes(registry) : null;
        string sandbox = Path.GetFullPath("../evidence/metadata-guard/sdk/private-" + Guid.NewGuid().ToString("N"));
        GameObject root = null; GuardProfile profile = null; GuardMetadataArtifact labels = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", sandbox);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder); AssetDatabase.CreateFolder("Assets", "MetadataSdkFixture");
            root = new GameObject("OwnedSdkMetadataAvatar"); root.AddComponent<Animator>(); var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            var mesh = Object.Instantiate(AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Fixture/mesh.asset")); mesh.name = "PrivateSdkMesh";
            mesh.ClearBlendShapes();
            mesh.AddBlendShapeFrame("vrc.v_aa", 100, new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount]);
            mesh.AddBlendShapeFrame("eyeBlinkLeft", 100, new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount]);
            mesh.bindposes = new[] { Matrix4x4.identity }; mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, mesh.vertexCount).ToArray(); AssetDatabase.CreateAsset(mesh, Folder + "/mesh.asset");
            var material = new Material(AssetDatabase.LoadAssetAtPath<Material>("Assets/Fixture/original.mat")) { name = "PrivateSdkMaterial" }; AssetDatabase.CreateAsset(material, Folder + "/material.mat");
            var bone = new GameObject("StableBone"); bone.transform.SetParent(root.transform, false);
            var skin = body.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh; skin.sharedMaterial = material;
            skin.bones = new[] { bone.transform }; skin.rootBone = bone.transform; skin.localBounds = mesh.bounds;
            descriptor.VisemeSkinnedMesh = skin; descriptor.VisemeBlendShapes = Enumerable.Repeat("vrc.v_aa", 15).ToArray();
            var descriptorSerialized = new SerializedObject(descriptor);
            var eyelidRenderer = descriptorSerialized.FindProperty("customEyeLookSettings.eyelidsSkinnedMesh");
            var eyelidIndices = descriptorSerialized.FindProperty("customEyeLookSettings.eyelidsBlendshapes");
            Check(eyelidRenderer != null && eyelidIndices != null, "installed descriptor exposes eyelid object/index references");
            eyelidRenderer.objectReferenceValue = skin; eyelidIndices.arraySize = 3;
            for (int i = 0; i < 3; i++) eyelidIndices.GetArrayElementAtIndex(i).intValue = 1; descriptorSerialized.ApplyModifiedPropertiesWithoutUndo();
            var parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>(); parameters.name = "PrivateExpressions";
            parameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "InternalBlend", valueType = VRCExpressionParameters.ValueType.Float, defaultValue = .25f, saved = true, networkSynced = true } };
            AssetDatabase.CreateAsset(parameters, Folder + "/parameters.asset");
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>(); menu.name = "PrivateMenu";
            menu.controls = new List<VRCExpressionsMenu.Control> { new VRCExpressionsMenu.Control { name = "Keep this display label", type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "InternalBlend" } } }; AssetDatabase.CreateAsset(menu, Folder + "/menu.asset");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(Folder + "/FX.controller"); controller.AddParameter("InternalBlend", AnimatorControllerParameterType.Float);
            var clip = new AnimationClip { name = "PrivateSdkViseme" }; AssetDatabase.CreateAsset(clip, Folder + "/viseme.anim");
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.vrc.v_aa"), AnimationCurve.Linear(0, 0, 1, 100));
            controller.layers[0].stateMachine.AddState("StableState").motion = clip;
            descriptor.customExpressions = true; descriptor.expressionParameters = parameters; descriptor.expressionsMenu = menu;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = false, animatorController = controller } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>(); descriptor.customizeAnimationLayers = true;
            Dynamics(body, "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone", "PhysicsStable");
            Dynamics(body, "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver", "ContactStable"); Save();
            Check(EditorSceneManager.SaveScene(root.scene, Folder + "/work.unity"), "disposable SDK working scene saved");
            EditorSceneManager.MarkSceneDirty(root.scene); var activeScene = SceneManager.GetActiveScene(); string originalDescriptor = EditorJsonUtility.ToJson(descriptor);
            string metadataFolder = "Assets/LinuxAvatarGuardGenerated/Research/metadata-sdk-labels";
            if (AssetDatabase.IsValidFolder(metadataFolder)) AssetDatabase.DeleteAsset(metadataFolder);
            string skinBefore = EditorJsonUtility.ToJson(skin, true);
            try { labels = GuardMetadataGuard.Prepare(root, new string('c', 32), metadataFolder); }
            catch
            {
                Directory.CreateDirectory("../evidence/metadata-guard/sdk"); File.WriteAllText("../evidence/metadata-guard/sdk/skin-before.json", skinBefore);
                File.WriteAllText("../evidence/metadata-guard/sdk/skin-after.json", EditorJsonUtility.ToJson(skin, true)); throw;
            }
            var copiedDescriptor = labels.Root.GetComponent<VRCAvatarDescriptor>();
            Check(copiedDescriptor.VisemeBlendShapes.SequenceEqual(descriptor.VisemeBlendShapes) && copiedDescriptor.VisemeSkinnedMesh.sharedMesh.GetBlendShapeName(0) == "vrc.v_aa", "viseme names/order and renderer reference survive conservative metadata copy");
            var copiedSerialized = new SerializedObject(copiedDescriptor);
            Check(copiedSerialized.FindProperty("customEyeLookSettings.eyelidsSkinnedMesh").objectReferenceValue == copiedDescriptor.VisemeSkinnedMesh && copiedSerialized.FindProperty("customEyeLookSettings.eyelidsBlendshapes").GetArrayElementAtIndex(0).intValue == 1, "eye tracking keeps local renderer reference and morph index");
            Check(copiedDescriptor.expressionParameters.parameters[0].name == "InternalBlend" && copiedDescriptor.expressionsMenu.controls[0].name == "Keep this display label" && copiedDescriptor.expressionsMenu.controls[0].parameter.name == "InternalBlend", "expression parameters and menu labels/parameter bindings remain stable");
            foreach (var c in labels.Root.GetComponentsInChildren<Component>(true).Where(c => c.GetType().Name == "VRCPhysBone" || c.GetType().Name == "VRCContactReceiver"))
                Check(new SerializedObject(c).FindProperty("parameter").stringValue == (c.GetType().Name == "VRCPhysBone" ? "PhysicsStable" : "ContactStable"), "dynamics parameter remains stable " + c.GetType().Name);
            bool denied = false;
            try { GuardMetadataGuard.Prepare(root, new string('c', 32), metadataFolder + "-unsafe", new GuardMetadataOptions { InternalParameters = new[] { "InternalBlend" } }); }
            catch (InvalidOperationException) { denied = true; }
            Check(denied && !Directory.Exists(metadataFolder + "-unsafe"), "SDK/external-facing functional renames are denied before allocation");
            // Dynamics are checked above; the existing geometry builder needs a final render-only copy.
            foreach (var c in body.GetComponents<Component>().Where(c => c.GetType().Name == "VRCPhysBone" || c.GetType().Name == "VRCContactReceiver").ToArray()) Object.DestroyImmediate(c);
            string[] assetBefore = Entries("Assets/LinuxAvatarGuardGenerated"), keyBefore = Entries("Library/LinuxAvatarGuard");
            var registryBefore = File.Exists(registry) ? File.ReadAllBytes(registry) : null;
            foreach (var at in new[] { PreparationCheckpoint.AssetsBuilt, PreparationCheckpoint.ProfileRegistered, PreparationCheckpoint.SceneSaved })
            {
                bool failed = false;
                try { GuardSetup.PrepareWithMetadata(root, .1f, checkpoint => { if (checkpoint == at) throw new IOException("owned metadata preparation failure"); }); }
                catch (IOException) { failed = true; }
                Check(failed && Entries("Assets/LinuxAvatarGuardGenerated").SequenceEqual(assetBefore) && Entries("Library/LinuxAvatarGuard").SequenceEqual(keyBefore), "metadata assets/private map/key roll back at " + at);
                Check(registryBefore == null ? !File.Exists(registry) : File.ReadAllBytes(registry).SequenceEqual(registryBefore), "profile registry restored at " + at);
            }
            profile = GuardSetup.PrepareWithMetadata(root, .1f); GuardProfiles.RequirePrepared(profile);
            var protectedRoot = AssetDatabase.LoadAssetAtPath<GameObject>(profile.prefabPath); var protectedDescriptor = protectedRoot.GetComponent<VRCAvatarDescriptor>();
            var key = GuardProfiles.Key(profile); var manifest = GuardBuildManifest.Read(Path.GetDirectoryName(profile.prefabPath) + "/security-manifest.json");
            Check(manifest.metadataGuardVersion == 1 && manifest.metadataPolicy == "conservative-asset-labels" && manifest.renamedMetadataAssets > 0 && manifest.renamedMetadataFiles > 0 && manifest.status == "Ready", "integrated opt-in preparation reports real metadata coverage and Ready state");
            Check(key.format == 1 && key.parameters.Length == 4 && key.keys.Length == 4 && key.parameters.All(p => protectedDescriptor.expressionParameters.parameters.Any(e => e.name == p)), "OSC schema and four generated parameter names are preserved");
            Check(File.Exists(Path.Combine(profile.toolsPath, "metadata-map.json")) && !File.Exists(Path.GetDirectoryName(profile.prefabPath) + "/metadata-map.json"), "private metadata map is backed up alongside key.json outside Assets");
            Check(protectedDescriptor.expressionParameters.parameters.Any(p => p.name == "InternalBlend") && protectedDescriptor.VisemeBlendShapes.SequenceEqual(descriptor.VisemeBlendShapes) && protectedDescriptor.expressionsMenu.controls[0].parameter.name == "InternalBlend", "integrated builder preserves existing expressions and viseme semantics");
            Check(SceneManager.GetActiveScene() == activeScene && activeScene.isDirty && EditorJsonUtility.ToJson(descriptor) == originalDescriptor, "successful metadata preparation preserves source descriptor and unsaved working-scene edits");
            Directory.CreateDirectory("../evidence/metadata-guard/sdk");
            File.WriteAllText("../evidence/metadata-guard/sdk/results.json", JsonUtility.ToJson(new Report { checks = checks.ToArray(), sdkVersion = "3.10.5", sdkProcessed = false, preparesWithMetadata = true }, true));
            Debug.Log("LAG_METADATA_SDK_SUCCESS " + checks.Count + " checks");
        }
        finally
        {
            labels?.Dispose();
            if (profile != null) { AssetDatabase.DeleteAsset(Path.GetDirectoryName(profile.prefabPath).Replace('\\', '/')); foreach (string p in new[] { "Library/LinuxAvatarGuard/" + profile.buildId + ".json", "Library/LinuxAvatarGuard/" + profile.buildId + ".json.metadata.json" }) if (File.Exists(p)) File.Delete(p); }
            if (root) Object.DestroyImmediate(root);
            if (originalRegistry != null) File.WriteAllBytes(registry, originalRegistry); else if (File.Exists(registry)) File.Delete(registry);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previousXdg); if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
        }
    }
    public static void RunBatch()
    { try { Run(); EditorApplication.Exit(0); } catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); } }
    [Serializable] sealed class Report { public string[] checks; public string sdkVersion; public bool sdkProcessed, preparesWithMetadata; }
}
#endif
