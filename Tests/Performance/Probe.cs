// SPDX-License-Identifier: MIT
// Own Linux player only. No VRChat process, API, asset or private user key is accessed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace LinuxAvatarGuard.Performance
{
    [Serializable] public sealed class FixtureManifest
    {
        public string scope; public int[] publicFixtureUnlockValues; public FixtureVariant[] variants;
        public string[] interleavedOrder;
        public int expectedDrawsPerInstance, expectedTrianglesPerInstance;
    }
    [Serializable] public sealed class FixtureVariant
    {
        public string id, bundle, prefab; public string[] providers, shaders;
    }
    public sealed class Probe : MonoBehaviour
    {
        [Serializable] sealed class CounterStatus { public string name, unit; public bool valid; }
        [Serializable] sealed class RunResult
        {
            public string scope, variant, unity, gpu, graphics, driver, cpu, operatingSystem, status, colorSpace;
            public int instances, width, height, processorCount, gpuMemoryReportedMiB, samples, uniqueGpuSamples, droppedTimingReads;
            public long measurementStartedUnixMs, measurementEndedUnixMs;
            public double warmupSeconds, measurementSeconds, loadSeconds;
            public bool development, frameTimingEnabled, vsync, captureOnly;
            public CounterStatus[] counters;
        }
        struct Sample
        {
            public double elapsed, interval, cpuTotal, cpuMain, cpuRender, cpuPresentWait, gpu;
            public ulong timestamp;
            public long drawCalls, setPass, vertices, triangles, buffers, renderTextures, totalMemory, gcMemory;
        }
        readonly Sample[] samples = new Sample[50000];
        readonly FrameTiming[] timing = new FrameTiming[1];
        readonly Dictionary<string, ProfilerRecorder> counters = new Dictionary<string, ProfilerRecorder>();
        readonly List<CounterStatus> counterStatuses = new List<CounterStatus>();
        readonly List<GameObject> roots = new List<GameObject>();
        AssetBundle bundle; new Camera camera; string output; RunResult report;
        double readyTime, beginTime, previousTime, duration; int sampleCount, gpuCount, timingMisses; ulong lastTimestamp;
        bool measuring, done;

        static string Argument(string key, string fallback = null)
        {
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key);
            return index < 0 ? fallback : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing value: " + key);
        }
        void Start()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("LAG_PERFORMANCE_ALLOWED") != "owned-fixtures-only" ||
                    Application.productName != "LinuxAvatarGuardPerformanceFixture" || Application.platform != RuntimePlatform.LinuxPlayer ||
                    SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan || GraphicsSettings.currentRenderPipeline != null)
                    throw new InvalidOperationException("Explicit own Linux/Vulkan player required.");
                output = Argument("--lag-output"); if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output)) throw new ArgumentException("Absolute output required.");
                Directory.CreateDirectory(output);
                string config = Argument("--lag-config"), variant = Argument("--lag-variant");
                var manifest = JsonUtility.FromJson<FixtureManifest>(File.ReadAllText(config));
                if (manifest.scope != "owned-procedural-rigid-liltoon-fixture") throw new InvalidOperationException("Only owned benchmark fixtures allowed.");
                var item = manifest.variants.Single(v => v.id == variant);
                int count = int.Parse(Argument("--lag-instances", "1"), CultureInfo.InvariantCulture);
                if (count != 1 && count != 16) throw new ArgumentException("Only documented instance counts allowed.");
                duration = double.Parse(Argument("--lag-duration", "12"), CultureInfo.InvariantCulture);
                if (duration < 1 || duration > 120) throw new ArgumentException("Duration out of range.");
                Application.runInBackground = true; Application.targetFrameRate = -1;
                QualitySettings.vSyncCount = 0; QualitySettings.antiAliasing = 0; QualitySettings.shadows = ShadowQuality.Disable;
                RenderSettings.fog = false; RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Color.white;
                double loadStart = Time.realtimeSinceStartupAsDouble;
                bundle = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(config), item.bundle));
                if (!bundle) throw new Exception("Native bundle failed to load.");
                foreach (string path in item.providers.Concat(item.shaders).Distinct())
                { var shader = bundle.LoadAsset<Shader>(path); if (!shader || !shader.isSupported) throw new Exception("Unsupported native shader: " + path); }
                var prefab = bundle.LoadAsset<GameObject>(item.prefab); if (!prefab) throw new Exception("Native fixture prefab missing.");
                int side = count == 1 ? 1 : 4;
                for (int i = 0; i < count; i++)
                {
                    var root = Instantiate(prefab); roots.Add(root); root.SetActive(true);
                    root.transform.position = new Vector3((i % side - (side - 1) * .5f) * 2.2f, (i / side - (side - 1) * .5f) * 1.8f, 0);
                }
                SetKeys(manifest.publicFixtureUnlockValues);
                camera = new GameObject("OwnedBenchmarkCamera").AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.035f, .045f, .055f);
                camera.orthographic = true; camera.orthographicSize = count == 1 ? 1.1f : 4.2f;
                camera.nearClipPlane = .1f; camera.farClipPlane = 30; camera.transform.position = new Vector3(0, 0, -10);
                camera.allowHDR = false; camera.allowMSAA = false; camera.useOcclusionCulling = false;
                report = new RunResult {
                    scope = manifest.scope, variant = variant, instances = count, unity = Application.unityVersion,
                    gpu = SystemInfo.graphicsDeviceName, graphics = SystemInfo.graphicsDeviceType.ToString(), driver = SystemInfo.graphicsDeviceVersion, colorSpace = QualitySettings.activeColorSpace.ToString(),
                    cpu = SystemInfo.processorType, processorCount = SystemInfo.processorCount, operatingSystem = SystemInfo.operatingSystem,
                    gpuMemoryReportedMiB = SystemInfo.graphicsMemorySize, width = Screen.width, height = Screen.height,
                    development = Debug.isDebugBuild, frameTimingEnabled = FrameTimingManager.IsFeatureEnabled(), vsync = QualitySettings.vSyncCount != 0,
                    loadSeconds = Time.realtimeSinceStartupAsDouble - loadStart, captureOnly = Argument("--lag-capture", "0") == "1"
                };
                if (report.width != 1920 || report.height != 1080 || !report.frameTimingEnabled) throw new Exception("Resolution/frame timing configuration differs from contract.");
                if (Argument("--lag-images", "0") == "1" || report.captureOnly)
                {
                    // GPU readback, image encoding and optional RenderDoc capture finish BEFORE warmup/timing.
                    Photograph(Path.Combine(output, "unlocked.png"), report.captureOnly);
                    if (variant != "original") { SetKeys(new int[4]); Photograph(Path.Combine(output, "locked.png"), false); SetKeys(manifest.publicFixtureUnlockValues); }
                }
                if (report.captureOnly) { report.status = "capture-only"; WriteReport(); done = true; Application.Quit(0); return; }
                StartCounter("Draw Calls Count", ProfilerCategory.Render); StartCounter("SetPass Calls Count", ProfilerCategory.Render);
                StartCounter("Vertices Count", ProfilerCategory.Render); StartCounter("Triangles Count", ProfilerCategory.Render);
                StartCounter("Used Buffers Bytes", ProfilerCategory.Render); StartCounter("Render Textures Bytes", ProfilerCategory.Render);
                StartCounter("Total Used Memory", ProfilerCategory.Memory); StartCounter("GC Used Memory", ProfilerCategory.Memory);
                // Allocate/collect once, then avoid per-frame allocations and file IO during measurement.
                GC.Collect(); readyTime = Time.realtimeSinceStartupAsDouble; previousTime = readyTime;
                File.WriteAllText(Path.Combine(output, "ready.json"), "{\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id + "}");
            }
            catch (Exception error) { Fail(error); }
        }
        void StartCounter(string name, ProfilerCategory category)
        {
            var recorder = ProfilerRecorder.StartNew(category, name, 1); counters.Add(name, recorder);
            counterStatuses.Add(new CounterStatus { name = name, valid = recorder.Valid, unit = recorder.Valid ? recorder.UnitType.ToString() : "unavailable" });
        }
        long Counter(string name) { var recorder = counters[name]; return recorder.Valid && recorder.Count > 0 ? recorder.LastValue : -1; }
        void SetKeys(int[] values)
        {
            if (values == null || values.Length != 4) throw new ArgumentException("Public fixture requires four unlock values.");
            foreach (var mat in roots.SelectMany(r => r.GetComponentsInChildren<MeshRenderer>()).SelectMany(r => r.sharedMaterials).Distinct())
                for (int i = 0; i < 4; i++) if (mat.HasProperty("_LAGKey" + i)) mat.SetFloat("_LAGKey" + i, values[i]);
        }
        void Update()
        {
            if (done || report == null) return;
            try
            {
                double now = Time.realtimeSinceStartupAsDouble;
                FrameTimingManager.CaptureFrameTimings();
                if (!measuring)
                {
                    // Minimum two seconds AND 300 frames, excluding startup, compilation and readback.
                    if (now - readyTime < 2 || Time.frameCount < 300) { previousTime = now; return; }
                    measuring = true; beginTime = now; report.warmupSeconds = now - readyTime;
                    report.measurementStartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                var sample = new Sample { elapsed = now - beginTime, interval = (now - previousTime) * 1000,
                    cpuTotal = -1, cpuMain = -1, cpuRender = -1, cpuPresentWait = -1, gpu = -1,
                    drawCalls = Counter("Draw Calls Count"), setPass = Counter("SetPass Calls Count"),
                    vertices = Counter("Vertices Count"), triangles = Counter("Triangles Count"),
                    buffers = Counter("Used Buffers Bytes"), renderTextures = Counter("Render Textures Bytes"),
                    totalMemory = Counter("Total Used Memory"), gcMemory = Counter("GC Used Memory") };
                if (FrameTimingManager.GetLatestTimings(1, timing) == 1 && timing[0].frameStartTimestamp != lastTimestamp)
                {
                    var t = timing[0]; lastTimestamp = t.frameStartTimestamp; sample.timestamp = lastTimestamp;
                    sample.cpuTotal = t.cpuFrameTime; sample.cpuMain = t.cpuMainThreadFrameTime;
                    sample.cpuRender = t.cpuRenderThreadFrameTime; sample.cpuPresentWait = t.cpuMainThreadPresentWaitTime;
                    if (t.gpuFrameTime > 0 && !double.IsNaN(t.gpuFrameTime)) { sample.gpu = t.gpuFrameTime; gpuCount++; }
                }
                else timingMisses++;
                if (sampleCount < samples.Length) samples[sampleCount++] = sample;
                else throw new Exception("Sample capacity exceeded; increase duration/frame cap explicitly.");
                previousTime = now;
                if (now - beginTime >= duration)
                {
                    done = true; report.measurementSeconds = now - beginTime; report.samples = sampleCount;
                    report.measurementEndedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    report.uniqueGpuSamples = gpuCount; report.droppedTimingReads = timingMisses;
                    report.status = gpuCount > sampleCount * .8 && sampleCount >= 100 ? "measured" : "insufficient-gpu-timing";
                    WriteCsv(); WriteReport(); Application.Quit(report.status == "measured" ? 0 : 2);
                }
            }
            catch (Exception error) { Fail(error); }
        }
        void WriteCsv()
        {
            using (var file = new StreamWriter(Path.Combine(output, "frames.csv")))
            {
                file.WriteLine("elapsed_s,interval_ms,cpu_total_ms,cpu_main_ms,cpu_render_ms,cpu_present_wait_ms,gpu_ms,timestamp,draw_calls,setpass,vertices,triangles,buffers_bytes,render_textures_bytes,total_memory_bytes,gc_memory_bytes");
                for (int i = 0; i < sampleCount; i++)
                { var s = samples[i]; file.WriteLine(string.Join(",", new[] { s.elapsed, s.interval, s.cpuTotal, s.cpuMain, s.cpuRender, s.cpuPresentWait, s.gpu }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "," + string.Join(",", new[] { s.timestamp.ToString(CultureInfo.InvariantCulture), s.drawCalls.ToString(), s.setPass.ToString(), s.vertices.ToString(), s.triangles.ToString(), s.buffers.ToString(), s.renderTextures.ToString(), s.totalMemory.ToString(), s.gcMemory.ToString() })); }
            }
        }
        void WriteReport() { report.counters = counterStatuses.ToArray(); File.WriteAllText(Path.Combine(output, "run.json"), JsonUtility.ToJson(report, true)); }
        void Fail(Exception error)
        {
            done = true; Debug.LogException(error);
            if (output != null) File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
            Application.Quit(1);
        }
        void OnDestroy() { foreach (var recorder in counters.Values) recorder.Dispose(); if (bundle) bundle.Unload(true); }

        // API slots are pinned to official RenderDoc 1.6.0, using the already approved installation.
        [DllImport("librenderdoc.so", CallingConvention = CallingConvention.Cdecl)] static extern int RENDERDOC_GetAPI(int version, out IntPtr api);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetPath([MarshalAs(UnmanagedType.LPStr)] string path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StartCapture(IntPtr device, IntPtr window);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint EndCapture(IntPtr device, IntPtr window);
        static T Function<T>(IntPtr api, int index) where T : class => Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(api, index * IntPtr.Size), typeof(T)) as T;
        void Photograph(string path, bool capture)
        {
            IntPtr api = IntPtr.Zero;
            if (capture)
            { if (RENDERDOC_GetAPI(10600, out api) != 1) throw new Exception("RenderDoc unavailable in own capture process."); Function<SetPath>(api, 11)(Path.Combine(output, "owned-player")); }
            var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(1920, 1080, TextureFormat.RGBA32, false); var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = target; camera.Render();
                if (capture) Function<StartCapture>(api, 19)(IntPtr.Zero, IntPtr.Zero);
                camera.Render(); RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); image.Apply();
                File.WriteAllBytes(path, image.EncodeToPNG());
                if (capture && Function<EndCapture>(api, 21)(IntPtr.Zero, IntPtr.Zero) != 1) throw new Exception("Own frame capture failed.");
            }
            finally { RenderTexture.active = previous; camera.targetTexture = null; Destroy(image); Destroy(target); }
        }
    }
}
