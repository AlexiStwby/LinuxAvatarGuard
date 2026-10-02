// SPDX-License-Identifier: MIT
// Controlled RenderDoc capture of synthetic geometry in a disposable Unity project only.
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using LinuxAvatarGuard;
using Object = UnityEngine.Object;

public static class LAGGpuCapture
{
    [DllImport("librenderdoc.so", CallingConvention = CallingConvention.Cdecl)]
    static extern int RENDERDOC_GetAPI(int version, out IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetPath([MarshalAs(UnmanagedType.LPStr)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StartCapture(IntPtr device, IntPtr window);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint EndCapture(IntPtr device, IntPtr window);
    static T Function<T>(IntPtr api, int index) where T : class =>
        Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(api, index * IntPtr.Size), typeof(T)) as T;

    public static void Run()
    {
        if (Application.platform != RuntimePlatform.LinuxEditor ||
            SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Vulkan ||
            Environment.GetEnvironmentVariable("LAG_GPU_AUDIT_ALLOWED") != "synthetic-unity-only" ||
            Path.GetFullPath(Application.dataPath) != "/home/stwby/LinuxAvatarGuard/ValidationProject/Assets")
            throw new InvalidOperationException("GPU audit requires the explicitly allowed disposable Unity project. " +
                "platform=" + Application.platform + "; graphics=" + SystemInfo.graphicsDeviceType +
                "; explicit opt-in=" + (Environment.GetEnvironmentVariable("LAG_GPU_AUDIT_ALLOWED") == "synthetic-unity-only") +
                "; dataPath=" + Application.dataPath);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        string choice = Environment.GetEnvironmentVariable("LAG_GPU_AUDIT_CODEC") ?? "legacy";
        if (choice != "legacy" && choice != "static-prototype") throw new ArgumentException("Only known synthetic codec fixtures are allowed.");
        bool prototype = choice == "static-prototype";
        var output = Path.GetFullPath(prototype ? "../evidence/gpu-audit/static-prototype" : "../evidence/gpu-audit"); Directory.CreateDirectory(output);
        string capturePrefix = prototype ? "static-prototype-unlocked" : "legacy-unlocked";
        // API 1.6.0: stable function slots from the official renderdoc_app.h.
        if (RENDERDOC_GetAPI(10600, out var api) != 1) throw new Exception("RenderDoc API unavailable");
        var setPath = Function<SetPath>(api, 11);
        var start = Function<StartCapture>(api, 19);
        var end = Function<EndCapture>(api, 21);
        string shaders = "Assets/GpuAudit";
        if (!AssetDatabase.IsValidFolder(shaders)) AssetDatabase.CreateFolder("Assets", "GpuAudit");
        var objects = new System.Collections.Generic.List<Object>();
        var experimental = prototype ? new StaticPolymorphicCodecV1(Enumerable.Range(0,32).Select(i=>(byte)i).ToArray()) : null;
        try
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere); objects.Add(sphere);
            Object.DestroyImmediate(sphere.GetComponent<Collider>());
            var source = Object.Instantiate(sphere.GetComponent<MeshFilter>().sharedMesh); objects.Add(source);
            IMeshCodec codec = prototype ? (IMeshCodec)experimental : MeshCodecs.Legacy;
            var plan = codec.Plan(source, .15f);
            var encoded = codec.Encode(source, plan, new[] { 83, 127, 191, 239 }); objects.Add(encoded);
            sphere.GetComponent<MeshFilter>().sharedMesh = encoded;
            var material = new Material(new GuardShaders(shaders, Guid.NewGuid().ToString("N"),codec.EmitDecoder(plan)).Copy(Shader.Find("lilToon")));
            objects.Add(material); material.name = "SyntheticGpuAuditMaterial";
            material.SetFloat("_AsUnlit", 1); material.SetColor("_Color", new Color(.25f, .7f, .9f));
            var key = new[] { 83, 127, 191, 239 };
            for (int i = 0; i < 4; i++) material.SetFloat(GuardShaders.Property(i), key[i]);
            material.enableInstancing = false;
            sphere.GetComponent<Renderer>().sharedMaterial = material;
            var cameraObject = new GameObject("SyntheticGpuAuditCamera"); objects.Add(cameraObject);
            var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
            camera.transform.position = new Vector3(0, 0, -3); camera.transform.LookAt(Vector3.zero);
            camera.orthographic = true; camera.orthographicSize = .7f; camera.nearClipPlane = .1f; camera.farClipPlane = 10;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.05f, .06f, .08f);
            var target = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32); objects.Add(target);
            target.Create(); camera.targetTexture = target;
            camera.Render(); // Warm the exact shader path before capture.
            var gpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            var originalMvp = gpuProjection * camera.worldToCameraMatrix * sphere.transform.localToWorldMatrix;
            File.WriteAllText(Path.Combine(output, "synthetic-reference.json"), JsonUtility.ToJson(new Reference {
                fixture = "procedural Unity sphere; no commercial assets", vertexCount = source.vertexCount,
                indices = source.triangles, original = source.vertices, encoded = encoded.vertices,
                originalClip = source.vertices.Select(v => originalMvp * new Vector4(v.x, v.y, v.z, 1)).ToArray(),
                worldToClip = MatrixValues(gpuProjection * camera.worldToCameraMatrix), graphics = SystemInfo.graphicsDeviceType.ToString(),
                codecId = codec.CodecId, codecVersion = codec.CodecVersion, capturePrefix = capturePrefix,
                programHash = prototype ? StaticPolymorphicCodecV1.ProgramHash(plan) : null
            }, true));
            setPath(Path.Combine(output, capturePrefix)); start(IntPtr.Zero, IntPtr.Zero);
            camera.Render();
            var previous = RenderTexture.active; RenderTexture.active = target;
            var image = new Texture2D(256, 256, TextureFormat.RGBA32, false); objects.Add(image);
            try
            {
                image.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); image.Apply();
                File.WriteAllBytes(Path.Combine(output, "unity-unlocked.png"), image.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; }
            if (end(IntPtr.Zero, IntPtr.Zero) != 1) throw new Exception("RenderDoc frame capture failed");
            Debug.Log("LAG_GPU_CAPTURE_SUCCESS synthetic Unity Vulkan only");
        }
        finally { foreach (var item in objects.AsEnumerable().Reverse()) if (item) Object.DestroyImmediate(item); experimental?.Dispose(); }
    }
    static float[] MatrixValues(Matrix4x4 matrix) => Enumerable.Range(0, 16).Select(i => matrix[i / 4, i % 4]).ToArray();
    [Serializable] sealed class Reference
    {
        public string fixture, graphics, codecId, capturePrefix, programHash; public int vertexCount, codecVersion;
        public int[] indices; public Vector3[] original, encoded; public Vector4[] originalClip; public float[] worldToClip;
    }
}
