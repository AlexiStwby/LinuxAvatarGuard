// SPDX-License-Identifier: MIT
// Native rendering of owned fixtures only; GPU/CPU skinning are distinct player builds.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;

namespace LinuxAvatarGuard.Performance
{
    [Serializable] public sealed class MipSkinFixtureManifest
    {
        public string scope,colorSpace,originalBundle,protectedBundle,originalPrefab,protectedPrefab,originalClip,protectedClip,skinProvider;
        public int[] publicFixtureUnlockValues;
        public string[] shaders;
    }
    public sealed class MipSkinProbe : MonoBehaviour
    {
        [Serializable] sealed class Row {public float time,mean,max,missing,wrong,cpuPositionError,cpuNormalError,normalMagnitudeMin,normalMagnitudeMax;public int view;}
        [Serializable] sealed class Report
        {
            public string scope="owned-procedural-mip-skinned-surface",status,unity,graphics,gpu,driver,colorSpace,error;
            public bool development,gpuSkinningBuildEnabled,capturePerformed,vrchatAnalyzed=false,sdkIncluded=false,bakeIsCpuReference=true;
            public string requestedSkinning; public int images,poses,views,frameWidth,frameHeight;
            public string[] checks;public Row[] rows;
        }
        readonly List<string> checks=new List<string>();readonly List<Row> rows=new List<Row>();
        readonly List<AssetBundle> bundles=new List<AssetBundle>();
        GameObject original,guarded;AnimationClip originalClip,guardedClip;SkinnedMeshRenderer originalSkin,guardedSkin;
        Mesh originalBake,encodedBake;Camera camera;MipSkinFixtureManifest manifest;Report report;string output;
        int firstUv,secondUv;Vector4 skinRows;Color[] reference,unlocked,missing,wrong;Row row;
        IntPtr captureApi;
        static string Arg(string name)
        {var args=Environment.GetCommandLineArgs();int i=Array.IndexOf(args,name);if(i<0||i+1>=args.Length)throw new ArgumentException("Missing own-player argument: "+name);return args[i+1];}
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);checks.Add(label);}
        bool Step(Action action)
        {try{action();return true;}catch(Exception e){if(report!=null){report.status="failed";report.error=e.ToString();Write();}if(output!=null)File.WriteAllText(Path.Combine(output,"failure.txt"),e.ToString());Debug.LogException(e);Application.Quit(1);return false;}}
        void Start()
        {
            if(!Step(Initialize))return;StartCoroutine(Validate());
        }
        void Initialize()
        {
            if(Environment.GetEnvironmentVariable("LAG_PERFORMANCE_ALLOWED")!="owned-fixtures-only"||Application.productName!="LinuxAvatarGuardPerformanceFixture"||Application.platform!=RuntimePlatform.LinuxPlayer||SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Vulkan||GraphicsSettings.currentRenderPipeline!=null)
                throw new InvalidOperationException("Explicit owned Linux/Vulkan/Built-in player required.");
            output=Arg("--lag-output");if(!Path.IsPathRooted(output))throw new ArgumentException("Absolute evidence path required.");Directory.CreateDirectory(output);
            report=new Report{status="running",unity=Application.unityVersion,graphics=SystemInfo.graphicsDeviceType.ToString(),gpu=SystemInfo.graphicsDeviceName,driver=SystemInfo.graphicsDeviceVersion,colorSpace=QualitySettings.activeColorSpace.ToString(),development=Debug.isDebugBuild,requestedSkinning=Arg("--lag-skinning"),frameWidth=512,frameHeight=512};
            Check(report.requestedSkinning=="gpu"||report.requestedSkinning=="cpu","explicit labelled CPU/GPU build");
#if LAG_TEST_GPU_SKINNING
            report.gpuSkinningBuildEnabled=true;
#endif
            Check(report.gpuSkinningBuildEnabled==(report.requestedSkinning=="gpu"),"compiled skinning configuration matches requested label");
            Check(!report.gpuSkinningBuildEnabled||SystemInfo.supportsComputeShaders,"GPU skinning build has compute support");
            string config=Arg("--lag-config");manifest=JsonUtility.FromJson<MipSkinFixtureManifest>(File.ReadAllText(config));
            Check(manifest.scope==report.scope&&manifest.colorSpace==report.colorSpace,"owned fixture scope and native bundle color space match player");
            Check(manifest.publicFixtureUnlockValues.Length==4,"four public own-fixture unlock values, no user key accessed");
            AssetBundle Load(string file){var b=AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(config),file));if(!b)throw new Exception("Own bundle unavailable: "+file);bundles.Add(b);return b;}
            var source=Load(manifest.originalBundle);var encoded=Load(manifest.protectedBundle);
            foreach(string path in manifest.shaders){var s=encoded.LoadAsset<Shader>(path);Check(s&&s.isSupported,"supported native generated shader "+path);}
            original=Instantiate(source.LoadAsset<GameObject>(manifest.originalPrefab));guarded=Instantiate(encoded.LoadAsset<GameObject>(manifest.protectedPrefab));
            originalClip=source.LoadAsset<AnimationClip>(manifest.originalClip);guardedClip=encoded.LoadAsset<AnimationClip>(manifest.protectedClip);Check(original&&guarded&&originalClip&&guardedClip,"native prefab and both animation clips loaded");
            originalSkin=original.GetComponentInChildren<SkinnedMeshRenderer>(true);guardedSkin=guarded.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Check(originalSkin&&guardedSkin&&originalSkin.sharedMesh.blendShapeCount==3,"native skin with three multi-frame blendshapes");
            Check(originalSkin.sharedMesh.bindposes.SequenceEqual(guardedSkin.sharedMesh.bindposes)&&originalSkin.sharedMesh.boneWeights.SequenceEqual(guardedSkin.sharedMesh.boneWeights),"native bindposes and bone weights remain exact");
            Check(originalSkin.sharedMesh.normals.SequenceEqual(guardedSkin.sharedMesh.normals)&&originalSkin.sharedMesh.tangents.SequenceEqual(guardedSkin.sharedMesh.tangents),"native normals and tangents remain exact");
            var match=Regex.Match(manifest.skinProvider,@"// LAG_SKIN_LINEAR_V1 (\d+) (\d+) ([-.\d]+) ([-.\d]+) ([-.\d]+) ([-.\d]+)");Check(match.Success,"independent public-HLSL skin extractor reads native provider");firstUv=int.Parse(match.Groups[1].Value);secondUv=int.Parse(match.Groups[2].Value);for(int i=0;i<4;i++)skinRows[i]=float.Parse(match.Groups[3+i].Value,CultureInfo.InvariantCulture);
            foreach(var t in original.GetComponentsInChildren<Transform>(true))t.gameObject.layer=28;foreach(var t in guarded.GetComponentsInChildren<Transform>(true))t.gameObject.layer=29;
            foreach(var r in guarded.GetComponentsInChildren<Renderer>(true))foreach(var m in r.sharedMaterials)
            {var tex=m.GetTexture("_MainTex") as Texture2D;Check(tex&&tex.mipmapCount==7&&!tex.isReadable&&tex.ignoreMipmapLimit,"native unreadable full-resolution mip pyramid");for(int i=0;i<4;i++)Check(m.GetFloat("_LAGKey"+i)==0,"native serialized material key remains zero");}
            QualitySettings.vSyncCount=0;QualitySettings.antiAliasing=0;QualitySettings.globalTextureMipmapLimit=0;QualitySettings.skinWeights=SkinWeights.FourBones;QualitySettings.shadows=ShadowQuality.Disable;RenderSettings.fog=false;RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.white;Application.runInBackground=true;
            camera=new GameObject("OwnedNativeSkinCamera").AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.03f,.04f,.05f);camera.orthographic=true;camera.orthographicSize=1.65f;camera.nearClipPlane=.1f;camera.farClipPlane=30;camera.allowHDR=false;camera.allowMSAA=false;camera.useOcclusionCulling=false;
            originalBake=new Mesh();encodedBake=new Mesh();Write();
        }
        void Sample(float time,int view)
        {
            originalClip.SampleAnimation(original,time);guardedClip.SampleAnimation(guarded,time);Keys(manifest.publicFixtureUnlockValues);
            camera.transform.position=view==0?new Vector3(0,0,-5):new Vector3(2,1,-5);camera.transform.LookAt(new Vector3(0,.2f,0));
            originalSkin.BakeMesh(originalBake,false);guardedSkin.BakeMesh(encodedBake,false);var p=encodedBake.vertices;var n=encodedBake.normals;var a=new List<Vector2>();var b=new List<Vector2>();encodedBake.GetUVs(firstUv,a);encodedBake.GetUVs(secondUv,b);var key=new Vector4(manifest.publicFixtureUnlockValues[0],manifest.publicFixtureUnlockValues[1],manifest.publicFixtureUnlockValues[2],manifest.publicFixtureUnlockValues[3])/255f;
            for(int i=0;i<p.Length;i++)p[i]-=n[i]*Vector4.Dot(Vector4.Scale(new Vector4(a[i].x,a[i].y,b[i].x,b[i].y),skinRows),key);
            row=new Row{time=time,view=view,cpuPositionError=originalBake.vertices.Zip(p,(x,y)=>(x-y).magnitude).Max(),cpuNormalError=originalBake.normals.Zip(n,(x,y)=>(x-y).magnitude).Max(),normalMagnitudeMin=n.Min(v=>v.magnitude),normalMagnitudeMax=n.Max(v=>v.magnitude)};
            Check(row.cpuPositionError<2e-5f&&row.cpuNormalError<2e-6f,"CPU reference at pose "+time+"/"+view); // BakeMesh is always CPU, not GPU evidence.
        }
        void Keys(int[] values)
        {
            foreach(var r in guarded.GetComponentsInChildren<Renderer>(true))
            {var block=new MaterialPropertyBlock();r.GetPropertyBlock(block);for(int i=0;i<4;i++)block.SetFloat("_LAGKey"+i,values[i]);r.SetPropertyBlock(block);}
        }
        Color[] Photograph(string label,int layer)
        {
            camera.cullingMask=1<<layer;var rt=RenderTexture.GetTemporary(512,512,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Default);var prior=RenderTexture.active;var tex=new Texture2D(512,512,TextureFormat.RGB24,false);
            try{camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,512,512),0,0);tex.Apply(false);File.WriteAllBytes(Path.Combine(output,"pose-"+row.time.ToString(CultureInfo.InvariantCulture)+"-view-"+row.view+"-"+label+".png"),tex.EncodeToPNG());return tex.GetPixels();}
            finally{camera.targetTexture=null;RenderTexture.active=prior;RenderTexture.ReleaseTemporary(rt);Destroy(tex);}
        }
        static float Mean(Color[] a,Color[] b)=>a.Zip(b,(x,y)=>(Mathf.Abs(x.r-y.r)+Mathf.Abs(x.g-y.g)+Mathf.Abs(x.b-y.b))/3).Average();
        static float Max(Color[] a,Color[] b)=>a.Zip(b,(x,y)=>Mathf.Max(Mathf.Abs(x.r-y.r),Mathf.Abs(x.g-y.g),Mathf.Abs(x.b-y.b))).Max();
        IEnumerator Validate()
        {
            Color[] first=null;float poseDifference=0;
            foreach(float time in new[]{0f,.25f,.49f,.51f,.75f,.99f})for(int view=0;view<2;view++)
            {
                bool capture=time==.75f&&view==0&&Array.IndexOf(Environment.GetCommandLineArgs(),"--lag-capture")>=0;
                if(!Step(()=>{if(capture)BeginCapture();Sample(time,view);}))yield break;yield return null;yield return new WaitForEndOfFrame();
                if(!Step(()=>{reference=Photograph("original",28);unlocked=Photograph("unlocked",29);if(capture)FinishCapture();Keys(new int[4]);}))yield break;yield return new WaitForEndOfFrame();
                if(!Step(()=>{missing=Photograph("missing",29);Keys(manifest.publicFixtureUnlockValues.Select(k=>k^127).ToArray());}))yield break;yield return new WaitForEndOfFrame();
                if(!Step(()=>
                {
                    wrong=Photograph("wrong",29);row.mean=Mean(reference,unlocked);row.max=Max(reference,unlocked);row.missing=Mean(reference,missing);row.wrong=Mean(reference,wrong);
                    Check(reference.Any(p=>p.g>.2f&&p.b>.2f),"nonempty native reference pose "+time+"/"+view);
                    Check(row.mean<.0015f&&row.missing>.006f&&row.wrong>.006f,"native render/keys pose "+time+"/"+view+" mean="+row.mean+" missing="+row.missing+" wrong="+row.wrong);
                    if(view==0){if(first==null)first=reference;else poseDifference=Mathf.Max(poseDifference,Mean(first,reference));}rows.Add(row);Write();
                }))yield break;
            }
            if(!Step(()=>{Check(poseDifference>.005f,"native animation advances and changes rendered surface");report.poses=6;report.views=2;report.status="passed";Write();Debug.Log("LAG_MIP_SKIN_PLAYER_SUCCESS "+report.requestedSkinning+" "+report.colorSpace+"; "+rows.Count+" visuals; "+checks.Count+" checks");}))yield break;
            Application.Quit(0);
        }
        void Write(){report.checks=checks.ToArray();report.rows=rows.ToArray();report.images=Directory.GetFiles(output,"pose-*.png").Length;File.WriteAllText(Path.Combine(output,"run.json"),JsonUtility.ToJson(report,true));}
        // Optional own-process capture with the existing, explicitly authorized RenderDoc installation.
        [DllImport("librenderdoc.so",CallingConvention=CallingConvention.Cdecl)]static extern int RENDERDOC_GetAPI(int version,out IntPtr api);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate void SetPath([MarshalAs(UnmanagedType.LPStr)]string path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate void StartCapture(IntPtr device,IntPtr window);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate uint EndCapture(IntPtr device,IntPtr window);
        static T Function<T>(IntPtr api,int index)where T:class=>Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(api,index*IntPtr.Size),typeof(T))as T;
        void BeginCapture()
        {
            Check(RENDERDOC_GetAPI(10600,out captureApi)==1,"authorized RenderDoc attached only to own player");
            Function<SetPath>(captureApi,11)(Path.Combine(output,"owned-mip-skin"));Function<StartCapture>(captureApi,19)(IntPtr.Zero,IntPtr.Zero);
        }
        void FinishCapture(){Check(Function<EndCapture>(captureApi,21)(IntPtr.Zero,IntPtr.Zero)==1,"own Vulkan frame capture completes");report.capturePerformed=true;}
        void OnDestroy()
        {if(originalBake)Destroy(originalBake);if(encodedBake)Destroy(encodedBake);foreach(var b in bundles)if(b)b.Unload(true);}
    }
}
