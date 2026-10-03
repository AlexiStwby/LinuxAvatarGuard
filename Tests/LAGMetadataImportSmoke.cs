// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using LinuxAvatarGuard;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class LAGMetadataImportSmoke
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("LAG_METADATA_IMPORT_ALLOWED") != "owned-api-smoke" ||
            Path.GetFileName(Path.GetDirectoryName(Application.dataPath)) != "ImportValidationProject" ||
            Application.platform != RuntimePlatform.LinuxEditor || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
            throw new InvalidOperationException("Only the explicitly enabled SDK-free import project is allowed.");
        if (typeof(GuardWindow).Assembly.GetType("LinuxAvatarGuard.GuardBuilder") != null || Shader.Find("lilToon"))
            throw new Exception("The import smoke requires no VRChat SDK or lilToon.");
        const string folder = "Assets/MetadataApiSmoke";
        const string output = "Assets/LinuxAvatarGuardGenerated/Research/metadata-api-smoke";
        GameObject root = null, primitive = null; GuardMetadataArtifact result = null;
        try
        {
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            if (AssetDatabase.IsValidFolder(output)) AssetDatabase.DeleteAsset(output);
            AssetDatabase.CreateFolder("Assets", "MetadataApiSmoke");
            root = new GameObject("OwnedMetadataApiRoot"); primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            primitive.name = "InternalVisual"; primitive.transform.SetParent(root.transform, false); Object.DestroyImmediate(primitive.GetComponent<Collider>());
            var mesh = Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh); AssetDatabase.CreateAsset(mesh, folder + "/source.asset"); primitive.GetComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(Shader.Find("Unlit/Color")) { name = "OwnMaterial", color = Color.cyan }; AssetDatabase.CreateAsset(material, folder + "/source.mat"); primitive.GetComponent<MeshRenderer>().sharedMaterial = material;
            string original = MeshBindingIdentity.ContentFingerprint(mesh);
            result = GuardMetadataGuard.Prepare(root, new string('b', 32), output, new GuardMetadataOptions { InternalObjectPaths = new[] { "InternalVisual" } });
            if (result.Summary.status != "Ready" || result.Summary.objects != 1 || result.Summary.assets != 2 || result.Root.transform.Find("InternalVisual") ||
                MeshBindingIdentity.ContentFingerprint(result.CopyOf(mesh)) != original || material.color != Color.cyan || root.transform.GetChild(0).name != "InternalVisual")
                throw new Exception("Metadata API smoke failed its geometry/ownership/alias invariants.");
            var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabPath);
            if (!loaded || loaded.GetComponentInChildren<MeshRenderer>().sharedMaterial == material || loaded.GetComponentInChildren<MeshFilter>().sharedMesh == mesh)
                throw new Exception("Metadata prefab retains a source asset.");
            Directory.CreateDirectory("../evidence/metadata-guard");
            File.WriteAllText("../evidence/metadata-guard/sdk-free.json", JsonUtility.ToJson(result.Summary, true));
            Debug.Log("LAG_METADATA_IMPORT_SUCCESS no SDK/lilToon, metadata API and serialized prefab verified");
        }
        finally { result?.Dispose(); if (root) Object.DestroyImmediate(root); }
    }
    public static void RunBatch()
    { try { Run(); EditorApplication.Exit(0); } catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); } }
}
