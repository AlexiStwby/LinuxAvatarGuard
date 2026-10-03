// SPDX-License-Identifier: MIT
// Complement to isolated memory runs: compare shaders in one window/process and paired rounds.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace LinuxAvatarGuard.Performance
{
    public sealed class InterleavedProbe : MonoBehaviour
    {
        string[] names = { "original", "legacy", "forge" };
        static readonly int[][] Orders = { new[]{0,1,2},new[]{0,2,1},new[]{1,0,2},new[]{1,2,0},new[]{2,0,1},new[]{2,1,0} };
        struct Sample
        {
            public int round, variant; public double interval, cpuMain, cpuRender, gpu;
            public ulong timestamp; public long draws, triangles;
        }
        [Serializable] sealed class Report
        {
            public string scope, mode="within-process-interleaved", status, gpu, cpu, driver, unity, colorSpace;
            public int instances, rounds=12, samples, width, height, discardedSwitchFrames=16;
            public int geometryCounterMismatches;
            public int expectedDrawsPerInstance,expectedTrianglesPerInstance;
            public string[] variantOrder;
            public double secondsPerBlock=1, warmupSecondsPerVariant=2;
            public bool frameTimingEnabled, development, vsync, allVariantsResident=true;
        }
        readonly Sample[] samples = new Sample[160000];
        readonly FrameTiming[] latest = new FrameTiming[1];
        readonly List<AssetBundle> bundles = new List<AssetBundle>();
        readonly List<GameObject>[] groups = { new List<GameObject>(), new List<GameObject>(), new List<GameObject>() };
        ProfilerRecorder draws, triangles; Report report; string output;
        double phaseStart, previous; int warmupIndex, round, position, current, switchedFrames, sampleCount;
        ulong lastTimestamp; bool done, quitting;
        static string Arg(string key)
        {
            var args=Environment.GetCommandLineArgs();int i=Array.IndexOf(args,key);
            return i>=0&&i+1<args.Length?args[i+1]:throw new ArgumentException("Missing "+key);
        }
        void Start()
        {
            try
            {
                if(Environment.GetEnvironmentVariable("LAG_PERFORMANCE_ALLOWED")!="owned-fixtures-only"||
                    Environment.GetEnvironmentVariable("LAG_PERFORMANCE_MODE")!="interleaved"||
                    Application.platform!=RuntimePlatform.LinuxPlayer||Application.productName!="LinuxAvatarGuardPerformanceFixture"||
                    SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Vulkan||GraphicsSettings.currentRenderPipeline!=null)
                    throw new InvalidOperationException("Only the explicitly enabled own Linux/Vulkan interleaved player is allowed.");
                output=Arg("--lag-output");if(!Path.IsPathRooted(output))throw new ArgumentException("Absolute output required.");Directory.CreateDirectory(output);
                string config=Arg("--lag-config");var manifest=JsonUtility.FromJson<FixtureManifest>(File.ReadAllText(config));
                if(manifest.scope!="owned-procedural-rigid-liltoon-fixture")throw new InvalidOperationException("Owned fixture required.");
                if(manifest.interleavedOrder!=null&&manifest.interleavedOrder.Length!=0)
                {
                    if(manifest.interleavedOrder.Length!=3||manifest.interleavedOrder.Distinct().Count()!=3||manifest.interleavedOrder.Any(id=>!manifest.variants.Any(v=>v.id==id)))
                        throw new ArgumentException("Three distinct owned variants are required.");
                    names=manifest.interleavedOrder;
                }
                int count=int.Parse(Arg("--lag-instances"),CultureInfo.InvariantCulture);if(count!=1&&count!=16)throw new ArgumentException("Unknown instance matrix.");
                Application.runInBackground=true;Application.targetFrameRate=-1;QualitySettings.vSyncCount=0;QualitySettings.antiAliasing=0;QualitySettings.shadows=ShadowQuality.Disable;
                RenderSettings.fog=false;RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.white;
                for(int v=0;v<names.Length;v++)
                {
                    var item=manifest.variants.Single(x=>x.id==names[v]);
                    var bundle=AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(config),item.bundle));if(!bundle)throw new Exception("Native bundle unavailable.");bundles.Add(bundle);
                    foreach(string shaderPath in item.providers.Concat(item.shaders).Distinct())
                    {var shader=bundle.LoadAsset<Shader>(shaderPath);if(!shader||!shader.isSupported)throw new Exception("Native shader unsupported: "+shaderPath);}
                    var prefab=bundle.LoadAsset<GameObject>(item.prefab);if(!prefab)throw new Exception("Native prefab unavailable.");
                    int side=count==1?1:4;
                    for(int i=0;i<count;i++)
                    {
                        var root=Instantiate(prefab);groups[v].Add(root);
                        root.transform.position=new Vector3((i%side-(side-1)*.5f)*2.2f,(i/side-(side-1)*.5f)*1.8f,0);
                        foreach(var material in root.GetComponentsInChildren<MeshRenderer>(true).SelectMany(r=>r.sharedMaterials).Distinct())
                            for(int k=0;k<4;k++)if(material.HasProperty("_LAGKey"+k))material.SetFloat("_LAGKey"+k,manifest.publicFixtureUnlockValues[k]);
                    }
                }
                var camera=new GameObject("OwnedInterleavedCamera").AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.035f,.045f,.055f);
                camera.orthographic=true;camera.orthographicSize=count==1?1.1f:4.2f;camera.nearClipPlane=.1f;camera.farClipPlane=30;camera.transform.position=new Vector3(0,0,-10);
                camera.allowHDR=false;camera.allowMSAA=false;camera.useOcclusionCulling=false;
                report=new Report{scope=manifest.scope,instances=count,width=Screen.width,height=Screen.height,frameTimingEnabled=FrameTimingManager.IsFeatureEnabled(),
                    variantOrder=names,expectedDrawsPerInstance=manifest.expectedDrawsPerInstance>0?manifest.expectedDrawsPerInstance:2,
                    expectedTrianglesPerInstance=manifest.expectedTrianglesPerInstance>0?manifest.expectedTrianglesPerInstance:49152,
                    development=Debug.isDebugBuild,vsync=QualitySettings.vSyncCount!=0,gpu=SystemInfo.graphicsDeviceName,cpu=SystemInfo.processorType,driver=SystemInfo.graphicsDeviceVersion,unity=Application.unityVersion,colorSpace=QualitySettings.activeColorSpace.ToString()};
                if(report.width!=1920||report.height!=1080||!report.frameTimingEnabled)throw new Exception("Resolution or timing contract failed.");
                draws=ProfilerRecorder.StartNew(ProfilerCategory.Render,"Draw Calls Count",1);triangles=ProfilerRecorder.StartNew(ProfilerCategory.Render,"Triangles Count",1);
                if(!draws.Valid||!triangles.Valid)throw new Exception("Rendering counters unavailable.");
                Activate(0);GC.Collect();phaseStart=Time.realtimeSinceStartupAsDouble;previous=phaseStart;
            }
            catch(Exception error){Fail(error);}
        }
        void Activate(int index)
        {current=index;for(int v=0;v<names.Length;v++)foreach(var root in groups[v])root.SetActive(v==index);switchedFrames=0;}
        void Update()
        {
            if(done||report==null)return;
            try
            {
                double now=Time.realtimeSinceStartupAsDouble;FrameTimingManager.CaptureFrameTimings();
                if(warmupIndex<3)
                {
                    if(now-phaseStart>=report.warmupSecondsPerVariant)
                    {
                        warmupIndex++;
                        if(warmupIndex<3)Activate(warmupIndex);
                        else{GC.Collect();Activate(Orders[0][0]);}
                        phaseStart=Time.realtimeSinceStartupAsDouble;
                    }
                    previous=now;return;
                }
                // The timing API is delayed by four frames. Discard sixteen rendered frames
                // after EVERY switch, including CPU activation and previous-variant GPU data.
                if(++switchedFrames>report.discardedSwitchFrames&&FrameTimingManager.GetLatestTimings(1,latest)==1&&latest[0].frameStartTimestamp!=lastTimestamp)
                {
                    var t=latest[0];lastTimestamp=t.frameStartTimestamp;
                    if(t.gpuFrameTime>0)
                    {
                        if(sampleCount>=samples.Length)throw new Exception("Interleaved sample capacity exceeded.");
                        // Rendering counters describe a different (previous) frame from the
                        // delayed GPU timing. Keep transient values in the RAW report; validate
                        // steady block medians afterward instead of dropping timing outliers.
                        if(draws.LastValue!=report.expectedDrawsPerInstance*report.instances||triangles.LastValue!=report.expectedTrianglesPerInstance*report.instances)report.geometryCounterMismatches++;
                        samples[sampleCount++]=new Sample{round=round,variant=current,interval=(now-previous)*1000,cpuMain=t.cpuMainThreadFrameTime,cpuRender=t.cpuRenderThreadFrameTime,
                            gpu=t.gpuFrameTime,timestamp=lastTimestamp,draws=draws.LastValue,triangles=triangles.LastValue};
                    }
                }
                previous=now;
                if(now-phaseStart>=report.secondsPerBlock)
                {
                    if(++position==3){position=0;round++;}
                    if(round==report.rounds){done=true;Write();Application.Quit(0);return;}
                    Activate(Orders[round%Orders.Length][position]);phaseStart=Time.realtimeSinceStartupAsDouble;
                }
            }
            catch(Exception error){Fail(error);}
        }
        void Write()
        {
            report.status="measured";report.samples=sampleCount;
            using(var file=new StreamWriter(Path.Combine(output,"frames.csv")))
            {
                file.WriteLine("round,variant,interval_ms,cpu_main_ms,cpu_render_ms,gpu_ms,timestamp,draw_calls,triangles");
                for(int i=0;i<sampleCount;i++)
                {var s=samples[i];file.WriteLine(s.round+","+names[s.variant]+","+string.Join(",",new[]{s.interval,s.cpuMain,s.cpuRender,s.gpu}.Select(x=>x.ToString("R",CultureInfo.InvariantCulture)))+","+s.timestamp+","+s.draws+","+s.triangles);}
            }
            File.WriteAllText(Path.Combine(output,"run.json"),JsonUtility.ToJson(report,true));
        }
        void Fail(Exception error){done=true;Debug.LogException(error);if(output!=null)File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());Application.Quit(1);}
        void OnApplicationQuit(){quitting=true;}
        void OnDestroy(){draws.Dispose();triangles.Dispose();if(!quitting)foreach(var bundle in bundles)if(bundle)bundle.Unload(false);}
    }
}
