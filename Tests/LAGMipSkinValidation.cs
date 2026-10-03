// SPDX-License-Identifier: MIT
// All textures, meshes, skeletons and clips are owned procedural fixtures.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LinuxAvatarGuard;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

public static class LAGMipSkinValidation
{
    const string Folder="Assets/MipSkinFixture";
    static string output;
    static readonly List<string> checks=new List<string>();
    static readonly List<Visual> visuals=new List<Visual>();
    static int recoveredMips;
    static void Check(bool ok,string description){if(!ok)throw new Exception("MIP_SKIN_FAILED: "+description);checks.Add(description);}
    static bool Rejects(Action action)=>LAGBindingValidation.Rejects(action);
    static string Hash(byte[] bytes){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    static Dictionary<string,string> Snapshot(string directory)=>Directory.GetFiles(directory,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>Hash(File.ReadAllBytes(p)));
    static bool Unchanged(Dictionary<string,string> before)=>before.All(p=>File.Exists(p.Key)&&Hash(File.ReadAllBytes(p.Key))==p.Value);
    static void Save(){AssetDatabase.SaveAssets();AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);}
    static string NewOutput(string suffix)
    {
        string path="Assets/LinuxAvatarGuardGenerated/Research/mip-skin-"+suffix;
        if(AssetDatabase.IsValidFolder(path))AssetDatabase.DeleteAsset(path);
        return path;
    }
    static void Start()
    {
        LAGBindingValidation.RequireResearchProject();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);checks.Clear();visuals.Clear();recoveredMips=0;
        if(AssetDatabase.IsValidFolder(Folder))AssetDatabase.DeleteAsset(Folder);AssetDatabase.CreateFolder("Assets","MipSkinFixture");
        output=Path.GetFullPath(Path.Combine(Application.dataPath,"../../evidence/mip-skin",QualitySettings.activeColorSpace.ToString().ToLowerInvariant()));Directory.CreateDirectory(output);
        Check(QualitySettings.globalTextureMipmapLimit==0,"reference matrix uses full-resolution mip limit 0");
    }
    public static Texture2D Texture(string name,TextureFormat format,bool srgb,FilterMode filter,bool custom=true)
    {
        var tex=new Texture2D(64,64,format,true,!srgb){name=name,filterMode=filter,anisoLevel=0,wrapModeU=TextureWrapMode.Repeat,wrapModeV=TextureWrapMode.Clamp,mipMapBias=filter==FilterMode.Point?-.7f:filter==FilterMode.Bilinear?.6f:0};
        for(int level=0;level<(custom?tex.mipmapCount:1);level++)
        {
            int size=Math.Max(1,64>>level);var pixels=new Color32[size*size];
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)pixels[x+y*size]=new Color32((byte)((x*27+y*13+level*43+31)%256),(byte)((x*11+y*37+level*71+83)%256),(byte)((x*47+y*17+level*29+137)%256),255);
            tex.SetPixels32(pixels,level);
        }
        tex.Apply(!custom);AssetDatabase.CreateAsset(tex,Folder+"/"+name+".asset");AssetDatabase.SaveAssetIfDirty(tex);return tex;
    }
    static Material Material(string name,Texture2D texture)
    {
        var material=new Material(Shader.Find("lilToon")){name=name};material.SetTexture("_MainTex",texture);material.SetFloat("_AsUnlit",1);material.SetColor("_Color",Color.white);
        material.SetTextureScale("_MainTex",new Vector2(1.2f,.9f));material.SetTextureOffset("_MainTex",new Vector2(-.31f,.17f));
        AssetDatabase.CreateAsset(material,Folder+"/"+name+".mat");AssetDatabase.SaveAssetIfDirty(material);return material;
    }
    public static Color32[] ReadMip(Texture2D texture,int mip)
    {
        int size=Math.Max(1,texture.width>>mip);var descriptor=new RenderTextureDescriptor(size,size,GraphicsFormat.R8G8B8A8_UNorm,0){msaaSamples=1};
        var rt=RenderTexture.GetTemporary(descriptor);var prior=RenderTexture.active;var image=new Texture2D(size,size,TextureFormat.RGBA32,false,true);
        try{Graphics.CopyTexture(texture,0,mip,rt,0,0);RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,size,size),0,0);image.Apply(false);return image.GetPixels32();}
        finally{RenderTexture.active=prior;RenderTexture.ReleaseTemporary(rt);Object.DestroyImmediate(image);}
    }
    // Parse the public provider's emitted constants, not the plan/seed or encoder implementation.
    public static Color32[] ExtractMip(Color32[] encoded,string provider,int level,int[] key)
    {
        var blocks=Regex.Matches(provider,@"(?s)// LAG_MIP_V2 (\d+) (\d+) (\d+)\s+if\(sourceMip==\d+u\)\s*\{(.*?)(?=// LAG_MIP_V2|uint2 local)").Cast<Match>().Where(m=>int.Parse(m.Groups[1].Value)==level).ToArray();
        if(blocks.Length==0||blocks.Select(m=>m.Groups[4].Value.Trim()).Distinct().Count()!=1)throw new Exception("Incomplete/inconsistent provider mip instructions.");
        int size=int.Parse(blocks[0].Groups[2].Value),grid=int.Parse(blocks[0].Groups[3].Value),side=size/grid;
        var rows=Regex.Matches(blocks[0].Groups[4].Value,@"if\(tile==(\d+)u\) \{atlas=(\d+)u;op=(\d+)u;order=(\d+)u;salts=uint3\((\d+)u,(\d+)u,(\d+)u\);\}").Cast<Match>().Select(m=>Enumerable.Range(1,7).Select(i=>int.Parse(m.Groups[i].Value)).ToArray()).ToArray();
        if(rows.Length!=grid*grid||rows.Select(r=>r[0]).Distinct().Count()!=rows.Length||encoded.Length!=size*size)throw new Exception("Invalid provider mip table.");
        int[][] orders={new[]{0,1,2},new[]{0,2,1},new[]{1,0,2},new[]{1,2,0},new[]{2,0,1},new[]{2,1,0}};var result=new Color32[encoded.Length];
        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
        {
            int tile=x/side+grid*(y/side);var row=rows.Single(r=>r[0]==tile);int lx=x%side,ly=y%side;
            if((row[2]&1)!=0){int t=lx;lx=ly;ly=t;}if((row[2]&2)!=0)lx=side-1-lx;if((row[2]&4)!=0)ly=side-1-ly;
            var p=encoded[row[1]%grid*side+lx+(row[1]/grid*side+ly)*size];int[] c={p.r^row[4]^key[tile%4],p.g^row[5]^key[(tile+1)%4],p.b^row[6]^key[(tile+2)%4]};var v=new byte[3];for(int i=0;i<3;i++)v[orders[row[3]][i]]=(byte)c[i];result[x+y*size]=new Color32(v[0],v[1],v[2],255);
        }
        return result;
    }
    public static void Key(GameObject root,int[] key)
    {foreach(var m in root.GetComponentsInChildren<Renderer>(true).SelectMany(r=>r.sharedMaterials).Distinct())for(int i=0;i<4;i++)if(m.HasProperty(GuardShaders.Property(i)))m.SetFloat(GuardShaders.Property(i),key[i]);}
    static float Mean(Color[] a,Color[] b)=>a.Zip(b,(x,y)=>(Mathf.Abs(x.r-y.r)+Mathf.Abs(x.g-y.g)+Mathf.Abs(x.b-y.b))/3).Average();
    static float Max(Color[] a,Color[] b)=>a.Zip(b,(x,y)=>Mathf.Max(Mathf.Abs(x.r-y.r),Mathf.Abs(x.g-y.g),Mathf.Abs(x.b-y.b))).Max();
    [Serializable] public sealed class Visual{public string name;public float mean,max,missing,wrong;}
    [Serializable] sealed class Report{public string unity,graphics,gpu,colorSpace,scope="owned opaque mip pyramid and generic skinned fixtures";public bool vrchatAnalyzed=false,sdkProcessed=false,productionIntegrated=false;public string[] checks;public Visual[] visuals;public int recoveredMips,images;}
    sealed class TextureCase{public string Name;public Texture2D Source;public Material Material;public GuardTextureArtifact Artifact;}
    public static void RunTextures()
    {
        Start();var cases=new List<TextureCase>();var cameraObject=new GameObject("OwnedMipCamera");var quad=GameObject.CreatePrimitive(PrimitiveType.Quad);Object.DestroyImmediate(quad.GetComponent<Collider>());
        var renderer=quad.GetComponent<MeshRenderer>();var camera=cameraObject.AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.03f,.04f,.05f);camera.orthographic=true;camera.orthographicSize=.7f;camera.nearClipPlane=.1f;camera.farClipPlane=30;camera.allowHDR=false;camera.allowMSAA=false;camera.transform.position=new Vector3(0,0,-4);
        int[] key={0,79,132,241};AssetBundle bundle=null;
        try
        {
            foreach(bool srgb in new[]{false,true})foreach(var format in new[]{TextureFormat.RGBA32,TextureFormat.RGB24})foreach(var filter in new[]{FilterMode.Point,FilterMode.Bilinear,FilterMode.Trilinear})
            {
                string name=(srgb?"srgb":"linear")+"-"+format+"-"+filter;var tex=Texture(name,format,srgb,filter);var material=Material(name,tex);Save();
                using(var codec=new TextureGuardCodecV2(new byte[32],"OwnedMip/"+name))
                {
                    var plan=codec.Plan(tex);Check(plan.Program.MipCount==7&&plan.Program.Mips[5].Grid==2&&plan.Program.Mips[6].Grid==1,"complete independent pyramid with small-mip grids "+name);
                    var artifact=GuardTextureForge.Prepare(material,codec,NewOutput("texture-"+name),key);cases.Add(new TextureCase{Name=name,Source=tex,Material=material,Artifact=artifact});
                    Check(artifact.Texture.mipmapCount==7&&!artifact.Texture.isReadable&&artifact.Texture.ignoreMipmapLimit,"unreadable full-resolution encoded mip payload "+name);
                    using(var old=new TextureGuardCodecV1(new byte[32],"OldReject"))Check(Rejects(()=>old.Plan(tex)),"V1 still rejects mip inputs "+name);
                    var oldPixel=tex.GetPixels32(6);var changed=(Color32[])oldPixel.Clone();changed[0].r^=1;tex.SetPixels32(changed,6);tex.Apply(false);AssetDatabase.SaveAssetIfDirty(tex);
                    Check(Rejects(()=>codec.EmitDecoder(plan)),"lowest source mip mutation invalidates plan "+name);tex.SetPixels32(oldPixel,6);tex.Apply(false);AssetDatabase.SaveAssetIfDirty(tex);
                }
            }
            Save();var sourceFiles=Snapshot(Folder);string directory=output+"/texture-bundles";Directory.CreateDirectory(directory);
            var originalApi=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneLinux64);bool automatic=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64);
            try
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,new[]{GraphicsDeviceType.Vulkan});LAGDiversityVariantFilter.Enabled=true;
                var paths=cases.SelectMany(c=>new[]{AssetDatabase.GetAssetPath(c.Artifact.Texture),AssetDatabase.GetAssetPath(c.Artifact.Material)}.Concat(c.Artifact.ShaderAssetPaths)).Distinct().ToArray();
                var built=BuildPipeline.BuildAssetBundles(directory,new[]{new AssetBundleBuild{assetBundleName="mip-textures.bundle",assetNames=paths}},BuildAssetBundleOptions.StrictMode|BuildAssetBundleOptions.ForceRebuildAssetBundle,BuildTarget.StandaloneLinux64);
                Check(built,"mip textures compile into a native Vulkan bundle");
                bundle=AssetBundle.LoadFromFile(directory+"/mip-textures.bundle");Check(bundle,"native mip bundle opens");Check(bundle.GetAllAssetNames().All(p=>!p.StartsWith(Folder.ToLowerInvariant()+"/",StringComparison.Ordinal)),"native texture bundle excludes original sources");
                foreach(var sample in cases)
                {
                    var material=bundle.LoadAsset<Material>(AssetDatabase.GetAssetPath(sample.Artifact.Material).ToLowerInvariant());var texture=(Texture2D)material.GetTexture("_MainTex");
                    Check(texture.mipmapCount==7&&!texture.isReadable&&texture.ignoreMipmapLimit&&Enumerable.Range(0,4).All(i=>material.GetFloat(GuardShaders.Property(i))==0),"native mip metadata and serialized-zero keys "+sample.Name);
                    string provider=File.ReadAllText(sample.Artifact.ProviderAssetPaths.First());
                    for(int mip=0;mip<7;mip++){var bytes=ReadMip(texture,mip);Check(LAGTextureValidation.ByteError(ExtractMip(bytes,provider,mip,key),sample.Source.GetPixels32(mip))==0,"adaptive provider extractor exactly recovers native mip "+sample.Name+"/"+mip);recoveredMips++;Check(LAGTextureValidation.ByteError(ExtractMip(bytes,provider,mip,new int[4]),sample.Source.GetPixels32(mip))>0,"missing keys do not recover native mip "+sample.Name+"/"+mip);}
                    foreach(float scale in new[]{.25f,1f,4f,16f,64f,256f})
                    {
                        quad.transform.localScale=Vector3.one;string prefix=output+"/mip-"+sample.Name+"-scale-"+scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        renderer.sharedMaterial=sample.Material;var referenceBlock=new MaterialPropertyBlock();referenceBlock.SetVector("_MainTex_ST",new Vector4(scale,scale*.7f,-.31f,.17f));renderer.SetPropertyBlock(referenceBlock);var original=LAGSkinningCharacterization.Render(camera,prefix+"-original.png");
                        renderer.sharedMaterial=material;var block=new MaterialPropertyBlock();block.SetVector("_MainTex_ST",new Vector4(scale,scale*.7f,-.31f,.17f));for(int i=0;i<4;i++)block.SetFloat(GuardShaders.Property(i),key[i]);renderer.SetPropertyBlock(block);var decoded=LAGSkinningCharacterization.Render(camera,prefix+"-unlocked.png");
                        for(int i=0;i<4;i++)block.SetFloat(GuardShaders.Property(i),0);renderer.SetPropertyBlock(block);var missing=LAGSkinningCharacterization.Render(camera,prefix+"-missing.png");for(int i=0;i<4;i++)block.SetFloat(GuardShaders.Property(i),key[i]^127);renderer.SetPropertyBlock(block);var wrong=LAGSkinningCharacterization.Render(camera,prefix+"-wrong.png");
                        var row=new Visual{name=sample.Name+"/"+scale,mean=Mean(original,decoded),max=Max(original,decoded),missing=Mean(original,missing),wrong=Mean(original,wrong)};visuals.Add(row);
                        Check(original.Any(p=>p.g>.2f&&p.b>.2f),"nonempty RGB reference at mip scale "+row.name);
                        Check(row.mean<=.0015f&&row.max<=.025f&&row.missing>.006f&&row.wrong>.006f,"native mip minification/filters/negative UV quality gates "+row.name+" mean="+row.mean+" max="+row.max);
                    }
                }
                Check(Unchanged(sourceFiles),"mip tests leave every original asset/meta byte-identical");
                File.WriteAllText(output+"/texture-validation.json",JsonUtility.ToJson(new Report{unity=Application.unityVersion,graphics=SystemInfo.graphicsDeviceType.ToString(),gpu=SystemInfo.graphicsDeviceName,colorSpace=QualitySettings.activeColorSpace.ToString(),checks=checks.ToArray(),visuals=visuals.ToArray(),recoveredMips=recoveredMips,images=Directory.GetFiles(output,"mip-*.png").Length},true));
                Debug.Log("LAG_MIP_TEXTURE_SUCCESS "+checks.Count+" checks; "+visuals.Count+" visual comparisons; "+recoveredMips+" native mip recoveries; "+QualitySettings.activeColorSpace);
            }
            finally{LAGDiversityVariantFilter.Enabled=false;PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,automatic);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,originalApi);}
        }
        finally{if(bundle)bundle.Unload(true);Object.DestroyImmediate(quad);Object.DestroyImmediate(cameraObject);}
    }
    sealed class Surface : IDisposable
    {
        public GameObject Root;public SkinnedMeshRenderer Skin;public AnimationClip First,Second;public AnimatorController Controller;public Material A,B;
        public void Dispose(){if(Root)Object.DestroyImmediate(Root);}
    }
    static void Curve(AnimationClip clip,string path,Type type,string property,float a,float b)
    {AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(path,type,property),AnimationCurve.Linear(0,a,1,b));}
    static Surface SurfaceFixture()
    {
        var f=new Surface{Root=new GameObject("OwnedSurface")};f.A=Material("surface-a",Texture("surface-rgba",TextureFormat.RGBA32,true,FilterMode.Trilinear,false));f.B=Material("surface-b",Texture("surface-rgb",TextureFormat.RGB24,false,FilterMode.Bilinear));
        var skeleton=new GameObject("Skeleton").transform;skeleton.SetParent(f.Root.transform,false);var bone0=new GameObject("Bone0").transform;bone0.SetParent(skeleton,false);bone0.localPosition=new Vector3(-.65f,-.2f,0);
        var bone1=new GameObject("Bone1").transform;bone1.SetParent(bone0,false);bone1.localPosition=new Vector3(0,.45f,0);
        var body=new GameObject("Body");body.transform.SetParent(f.Root.transform,false);body.transform.localPosition=new Vector3(-.65f,0,0);f.Skin=body.AddComponent<SkinnedMeshRenderer>();f.Skin.bones=new[]{bone0,bone1};f.Skin.rootBone=bone0;f.Skin.quality=SkinQuality.Bone4;f.Skin.updateWhenOffscreen=true;
        var mesh=LAGSkinningCharacterization.Mesh(body.transform,f.Skin.bones);var indices=mesh.triangles;mesh.subMeshCount=2;mesh.SetTriangles(indices.Take(indices.Length/2).ToArray(),0);mesh.SetTriangles(indices.Skip(indices.Length/2).ToArray(),1);AssetDatabase.CreateAsset(mesh,Folder+"/skin.asset");f.Skin.sharedMesh=mesh;f.Skin.sharedMaterials=new[]{f.A,f.B};f.Skin.localBounds=new Bounds(Vector3.zero,new Vector3(5,5,5));
        var rigid=new GameObject("Rigid");rigid.transform.SetParent(f.Root.transform,false);rigid.transform.localPosition=new Vector3(.7f,0,0);var staticMesh=LAGBindingValidation.Fixture();AssetDatabase.CreateAsset(staticMesh,Folder+"/rigid.asset");rigid.AddComponent<MeshFilter>().sharedMesh=staticMesh;rigid.AddComponent<MeshRenderer>().sharedMaterial=f.A;
        for(int direction=0;direction<2;direction++)
        {
            var clip=new AnimationClip{name=direction==0?"OwnedForward":"OwnedReverse",frameRate=60};
            for(int shape=0;shape<3;shape++)Curve(clip,"Body",typeof(SkinnedMeshRenderer),"blendShape."+mesh.GetBlendShapeName(shape),direction==0?0:100,direction==0?100:0);
            foreach(string path in new[]{"Skeleton/Bone0","Skeleton/Bone0/Bone1"})
            {
                bool first=path.EndsWith("Bone0");var start=Vector3.zero;var end=first?new Vector3(45,-30,75):new Vector3(-95,130,-70);for(int i=0;i<3;i++)Curve(clip,path,typeof(Transform),"localEulerAnglesRaw."+"xyz"[i],direction==0?start[i]:end[i],direction==0?end[i]:start[i]);
                for(int i=0;i<3;i++)Curve(clip,path,typeof(Transform),"m_LocalScale."+"xyz"[i],1,first?new[]{1.15f,.85f,1.05f}[i]:new[]{.8f,1.2f,.95f}[i]);
            }
            foreach(string part in new[]{"Body","Rigid"})
            {
                Type type=part=="Body"?typeof(SkinnedMeshRenderer):typeof(MeshRenderer);int slots=part=="Body"?2:1;
                for(int slot=0;slot<slots;slot++)AnimationUtility.SetObjectReferenceCurve(clip,EditorCurveBinding.PPtrCurve(part,type,"m_Materials.Array.data["+slot+"]"),new[]{new ObjectReferenceKeyframe{time=0,value=(slot+direction)%2==0?f.A:f.B},new ObjectReferenceKeyframe{time=.5f,value=(slot+direction)%2==0?f.B:f.A},new ObjectReferenceKeyframe{time=1,value=(slot+direction)%2==0?f.A:f.B}});
                float[] start={1.2f,.9f,-.31f,.17f},end={18f,12.1f,.27f,-.19f};for(int i=0;i<4;i++)Curve(clip,part,type,"material._MainTex_ST."+"xyzw"[i],start[i],end[i]);
            }
            AssetDatabase.CreateAsset(clip,Folder+"/surface-"+direction+".anim");if(direction==0)f.First=clip;else f.Second=clip;
        }
        f.Controller=AnimatorController.CreateAnimatorControllerAtPath(Folder+"/surface.controller");f.Controller.AddParameter("Blend",AnimatorControllerParameterType.Float);
        var state=f.Controller.layers[0].stateMachine.AddState("OwnedMixedState");f.Controller.layers[0].stateMachine.defaultState=state;var tree=new BlendTree{name="OwnedSkinTree",blendType=BlendTreeType.Simple1D,blendParameter="Blend",useAutomaticThresholds=false};tree.AddChild(f.First,0);tree.AddChild(f.Second,1);AssetDatabase.AddObjectToAsset(tree,f.Controller);state.motion=tree;EditorUtility.SetDirty(state);EditorUtility.SetDirty(f.Controller);
        var animator=f.Root.AddComponent<Animator>();animator.enabled=false;animator.runtimeAnimatorController=f.Controller;Save();return f;
    }
    [Serializable] sealed class SurfaceManifest{public TextureEntry[] textureBindings;}
    [Serializable] sealed class TextureEntry{public string bindingId,meshBindingId,programHash,payload,material;public int slot,mipCount,codecVersion;}
    static TextureEntry[] Entries(GuardShaderArtifact artifact)=>JsonUtility.FromJson<SurfaceManifest>(File.ReadAllText(artifact.PublicManifestPath)).textureBindings;
    static GuardBuildContext SurfaceContext(Surface f)
    {
        for(int i=0;i<128;i++)
        {
            var context=GuardBuildContext.CreateReproducible(Hash(Encoding.UTF8.GetBytes("OwnedSurfaceBuild"+i)).Substring(0,32),SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes("OwnedSurfaceSeed"+i)));
            try{var renderer=f.Root.GetComponentsInChildren<MeshRenderer>(true).Single();var binding=MeshBindingIdentity.Capture(f.Root,renderer);var animation=GuardAnimationContext.Capture(f.Root,true,true);using(var codec=context.CreateContextualCodec(binding,animation)){var plan=codec.Plan(renderer.GetComponent<MeshFilter>().sharedMesh,.2f);Object.DestroyImmediate(codec.Encode(renderer.GetComponent<MeshFilter>().sharedMesh,plan,context.RuntimeKey()));}return context;}
            catch(InvalidOperationException e)when(e.Message.Contains("cancela sus offsets keyed")||e.Message.Contains("no supera el error de reconstrucción sin clave")){context.Dispose();}
            catch{context.Dispose();throw;}
        }
        throw new Exception("No valid own rigid control context.");
    }
    static void SkinContracts(Surface f,GuardBuildContext context)
    {
        bool offscreen=f.Skin.updateWhenOffscreen;var bounds=f.Skin.localBounds;
        try
        {
            f.Skin.updateWhenOffscreen=false;f.Skin.localBounds=new Bounds(Vector3.zero,Vector3.one*5);
            using(var c=GuardBuildContext.CreateRandom())using(var codec=c.CreateSkinningCodec(MeshBindingIdentity.Capture(f.Root,f.Skin,true),GuardAnimationContext.Capture(f.Root,true,true)))
            {
                var plan=codec.Plan(f.Skin.sharedMesh,.2f);f.Skin.localBounds=new Bounds(Vector3.one,Vector3.one*5);
                Check(Rejects(()=>codec.EmitDecoder(plan)),"changed author-controlled static skin bounds invalidate the plan");
            }
        }
        finally{f.Skin.updateWhenOffscreen=offscreen;f.Skin.localBounds=bounds;}
        var originalMesh=f.Skin.sharedMesh;
        foreach(bool morph in new[]{false,true})
        {
            var copy=Object.Instantiate(originalMesh);string path=Folder+"/numeric-"+morph+".asset";
            try
            {
                if(!morph)copy.normals=Enumerable.Repeat(Vector3.one*1e20f,copy.vertexCount).ToArray();
                else
                {
                    copy.ClearBlendShapes();var dp=new Vector3[copy.vertexCount];var dn=new Vector3[copy.vertexCount];var dt=new Vector3[copy.vertexCount];
                    for(int shape=0;shape<originalMesh.blendShapeCount;shape++)for(int frame=0;frame<originalMesh.GetBlendShapeFrameCount(shape);frame++)
                    {originalMesh.GetBlendShapeFrameVertices(shape,frame,dp,dn,dt);for(int v=0;v<dn.Length;v++)dn[v]=Vector3.one*1e20f;copy.AddBlendShapeFrame(originalMesh.GetBlendShapeName(shape),originalMesh.GetBlendShapeFrameWeight(shape,frame),dp,dn,dt);}
                }
                AssetDatabase.CreateAsset(copy,path);AssetDatabase.SaveAssetIfDirty(copy);f.Skin.sharedMesh=copy;
                using(var c=GuardBuildContext.CreateRandom())using(var codec=c.CreateSkinningCodec(MeshBindingIdentity.Capture(f.Root,f.Skin,true),GuardAnimationContext.Capture(f.Root,true,true)))
                {var plan=codec.Plan(copy,.2f);Check(Rejects(()=>{var unexpected=codec.Encode(copy,plan,c.RuntimeKey());Object.DestroyImmediate(unexpected);}),"finite but numerically unsafe "+(morph?"morph normal delta":"normal carrier")+" rejects before output");}
            }
            finally{f.Skin.sharedMesh=originalMesh;f.Skin.localBounds=bounds;if(AssetDatabase.IsValidFolder(Path.GetDirectoryName(path)))AssetDatabase.DeleteAsset(path);if(copy)Object.DestroyImmediate(copy);}
        }
        Check(Rejects(()=>MeshBindingIdentity.Capture(f.Root,f.Skin)),"skinning still requires an explicit binding opt-in");var binding=MeshBindingIdentity.Capture(f.Root,f.Skin,true);var animation=GuardAnimationContext.Capture(f.Root,true,true);
        Check(Rejects(()=>context.CreateContextualCodec(binding,animation)),"nonlinear rigid codec rejects the skinned binding");
        using(var codec=context.CreateSkinningCodec(binding,animation))
        {
            var plan=codec.Plan(f.Skin.sharedMesh,.2f);var key=context.RuntimeKey();var wrong=(int[])key.Clone();wrong[0]^=1;
            Check(Rejects(()=>codec.Encode(f.Skin.sharedMesh,plan,wrong)),"skin encoder rejects keys from a different context");
            var encoded=codec.Encode(f.Skin.sharedMesh,plan,key);
            try{Check(encoded.bindposes.SequenceEqual(f.Skin.sharedMesh.bindposes)&&encoded.boneWeights.SequenceEqual(f.Skin.sharedMesh.boneWeights)&&encoded.normals.SequenceEqual(f.Skin.sharedMesh.normals)&&encoded.tangents.SequenceEqual(f.Skin.sharedMesh.tangents),"skin encoding preserves bindposes, weights, normals and tangents exactly");Check(plan.Program.Attributes.FirstUvChannel>=6&&plan.Program.Attributes.SecondUvChannel>=6,"skin previous-position UV4/5 remain reserved");}
            finally{Object.DestroyImmediate(encoded);}
            var old=f.Skin.bones;f.Skin.bones=old.Reverse().ToArray();Check(Rejects(()=>codec.EmitDecoder(plan)),"changed bone ordering invalidates the skin plan");f.Skin.bones=old;
            var source=f.Skin.sharedMesh;var copy=Object.Instantiate(source);copy.ClearBlendShapes();f.Skin.sharedMesh=copy;Check(Rejects(()=>codec.EmitDecoder(plan)),"changed blendshape content invalidates the skin plan");f.Skin.sharedMesh=source;Object.DestroyImmediate(copy);
        }
        using(var clean=GuardBuildContext.CreateRandom())Check(Rejects(()=>GuardShaderForge.PrepareWithTextures(f.Root,clean,NewOutput("old-reject")))&&clean.BindingCount==0&&clean.TextureBindingCount==0,"previous texture forge rejects skin without output/records");
        using(var clean=GuardBuildContext.CreateRandom())Check(Rejects(()=>GuardShaderForge.PrepareWithFeatures(f.Root,clean,NewOutput("mip-disabled"),.2f,false,true))&&clean.BindingCount==0,"mip inputs cannot silently downgrade to V1");
        foreach(string mode in new[]{"mesh","mips","bone"})using(var clean=SurfaceContext(f))
        {
            string path=NewOutput("late-"+mode);LAGMipSkinFault.Prefix=path;LAGMipSkinFault.Mode=mode;LAGMipSkinFault.Source=f.Skin;LAGMipSkinFault.Touched=false;var bones=f.Skin.bones;
            try{Check(Rejects(()=>GuardShaderForge.PrepareWithFeatures(f.Root,clean,path,.2f))&&LAGMipSkinFault.Touched&&!Directory.Exists(path)&&clean.BindingCount==0&&clean.TextureBindingCount==0,"late "+mode+" mutation rolls back all output and record maps");}
            finally{f.Skin.bones=bones;LAGMipSkinFault.Mode=null;LAGMipSkinFault.Source=null;}
        }
    }
    static Vector3[] DecodeSkin(Mesh mesh,string hlsl,int[] key)
    {
        var match=Regex.Match(hlsl,@"// LAG_SKIN_LINEAR_V1 (\d+) (\d+) ([-.\d]+) ([-.\d]+) ([-.\d]+) ([-.\d]+)");if(!match.Success)throw new Exception("No native skin provider program.");int first=int.Parse(match.Groups[1].Value),second=int.Parse(match.Groups[2].Value);var rows=new Vector4();for(int i=0;i<4;i++)rows[i]=float.Parse(match.Groups[3+i].Value,System.Globalization.CultureInfo.InvariantCulture);
        var a=new List<Vector2>();var b=new List<Vector2>();mesh.GetUVs(first,a);mesh.GetUVs(second,b);var p=mesh.vertices;var n=mesh.normals;var k=new Vector4(key[0],key[1],key[2],key[3])/255f;
        for(int i=0;i<p.Length;i++)p[i]-=n[i]*Vector4.Dot(Vector4.Scale(new Vector4(a[i].x,a[i].y,b[i].x,b[i].y),rows),k);return p;
    }
    [Serializable] sealed class PlayerFixtures
    {
        public string scope="owned-procedural-mip-skinned-surface",colorSpace,originalBundle="original.bundle",protectedBundle="protected.bundle",originalPrefab,protectedPrefab,originalClip,protectedClip,skinProvider;
        public int[] publicFixtureUnlockValues;public string[] shaders;
    }
    public static void RunPlayerFixtures()
    {
        Start();var oldApi=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneLinux64);bool automatic=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64);
        string old=Environment.GetEnvironmentVariable("XDG_DATA_HOME"),scratch=Path.Combine(Path.GetTempPath(),"lag-mip-skin-player-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);Environment.SetEnvironmentVariable("XDG_DATA_HOME",scratch);
        try
        {
            using(var f=SurfaceFixture())using(var context=SurfaceContext(f))
            {
                string sourcePrefab=Folder+"/original.prefab";PrefabUtility.SaveAsPrefabAsset(f.Root,sourcePrefab);Save();var before=Snapshot(Folder);
                using(var artifact=GuardShaderForge.PrepareWithFeatures(f.Root,context,NewOutput("player"),.2f))
                {
                    string directory=output+"/player-fixtures";Directory.CreateDirectory(directory);
                    var sourceShaders=new[]{AssetDatabase.GetAssetPath(Shader.Find("lilToon")),AssetDatabase.GetAssetPath(Shader.Find("Hidden/ltspass_opaque"))};
                    var generated=new[]{artifact.PrefabPath}.Concat(artifact.ShaderAssetPaths).Concat(Entries(artifact).SelectMany(e=>new[]{e.payload,e.material})).Concat(new[]{AssetDatabase.GetAssetPath(artifact.CopyOf(f.First))}).Distinct().ToArray();
                    PerformanceShaderFilter.Names=new HashSet<string>(sourceShaders.Concat(artifact.ShaderAssetPaths).Select(p=>AssetDatabase.LoadAssetAtPath<Shader>(p).name));PerformanceShaderFilter.Enabled=true;
                    PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,new[]{GraphicsDeviceType.Vulkan});
                    var bundles=BuildPipeline.BuildAssetBundles(directory,new[]{new AssetBundleBuild{assetBundleName="original.bundle",assetNames=new[]{sourcePrefab,AssetDatabase.GetAssetPath(f.First)}.Concat(sourceShaders).ToArray()},new AssetBundleBuild{assetBundleName="protected.bundle",assetNames=generated}},BuildAssetBundleOptions.StrictMode|BuildAssetBundleOptions.ForceRebuildAssetBundle,BuildTarget.StandaloneLinux64);
                    Check(bundles&&File.Exists(directory+"/original.bundle")&&File.Exists(directory+"/protected.bundle"),"own source/protected Vulkan player bundles build");
                    Check(Unchanged(before),"own native player fixture export preserves source bytes");
                    File.WriteAllText(directory+"/fixtures.json",JsonUtility.ToJson(new PlayerFixtures{colorSpace=QualitySettings.activeColorSpace.ToString(),originalPrefab=sourcePrefab.ToLowerInvariant(),protectedPrefab=artifact.PrefabPath.ToLowerInvariant(),originalClip=AssetDatabase.GetAssetPath(f.First).ToLowerInvariant(),protectedClip=AssetDatabase.GetAssetPath(artifact.CopyOf(f.First)).ToLowerInvariant(),publicFixtureUnlockValues=context.RuntimeKey(),shaders=artifact.ShaderAssetPaths.Select(p=>p.ToLowerInvariant()).ToArray(),skinProvider=File.ReadAllText(artifact.ProviderAssetPaths.First(p=>File.ReadAllText(p).Contains("LAG_SKIN_LINEAR_V1")))},true));
                    Debug.Log("LAG_MIP_SKIN_PLAYER_FIXTURES_SUCCESS "+QualitySettings.activeColorSpace);
                }
            }
        }
        finally{PerformanceShaderFilter.Enabled=false;PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,automatic);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,oldApi);Environment.SetEnvironmentVariable("XDG_DATA_HOME",old);Directory.Delete(scratch,true);}
    }
    public static void RunSurface()
    {
        Start();string old=Environment.GetEnvironmentVariable("XDG_DATA_HOME"),scratch=Path.Combine(Path.GetTempPath(),"lag-mip-skin-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);Environment.SetEnvironmentVariable("XDG_DATA_HOME",scratch);
        AssetBundle bundle=null;var copies=new List<Object>();var oldApi=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneLinux64);bool automatic=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64);
        try
        {
            using(var f=SurfaceFixture())using(var context=SurfaceContext(f))
            {
                var sourceFiles=Snapshot(Folder);SkinContracts(f,context);Check(Unchanged(sourceFiles),"negative surface tests preserve source asset/meta bytes");
                using(var artifact=GuardShaderForge.PrepareWithFeatures(f.Root,context,NewOutput("surface"),.2f))
                {
                    var entries=Entries(artifact);Check(context.BindingCount==2&&context.SkinningBindingCount==1&&context.TextureBindingCount==6&&entries.All(e=>e.mipCount==7&&e.codecVersion==2),"mixed rigid/skinned build records two meshes and six mip texture occurrences");
                    string privatePath=context.SavePrivate(),privateText=File.ReadAllText(privatePath);Check(privateText.Contains("\"schemaVersion\": 5")&&privateText.Contains("\"skinned\": true"),"private schema 5 stores explicit skin and mip policies");
                    foreach(var bad in new[]{privateText.Replace("\"schemaVersion\": 5","\"schemaVersion\": 4"),privateText.Replace("\"skinningVersion\": 1","\"skinningVersion\": 99"),privateText.Replace("\"mipCount\": 7","\"mipCount\": 0"),privateText.Replace("\"textureCodecVersion\": 2","\"textureCodecVersion\": 1")})
                    {try{File.WriteAllText(privatePath,bad);Check(Rejects(()=>GuardBuildContext.LoadPrivate(context.BuildId)),"malformed/downgraded skin+mip private context rejects");}finally{File.WriteAllText(privatePath,privateText);}}
                    using(var restored=GuardBuildContext.LoadPrivate(context.BuildId))using(var replay=GuardShaderForge.PrepareWithFeatures(f.Root,restored,NewOutput("surface-replay"),.2f))
                    {var other=Entries(replay);Check(restored.RuntimeKey().SequenceEqual(context.RuntimeKey())&&other.Select(e=>e.programHash).SequenceEqual(entries.Select(e=>e.programHash)),"private replay preserves old key bytes and all six mip programs");foreach(var e in entries)for(int mip=0;mip<7;mip++)Check(LAGTextureValidation.ByteError(ReadMip(AssetDatabase.LoadAssetAtPath<Texture2D>(e.payload),mip),ReadMip(AssetDatabase.LoadAssetAtPath<Texture2D>(other.Single(o=>o.bindingId==e.bindingId).payload),mip))==0,"private replay exactly reproduces encoded mip "+e.bindingId+"/"+mip);}
                    string dir=output+"/surface-bundles";Directory.CreateDirectory(dir);PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,new[]{GraphicsDeviceType.Vulkan});LAGDiversityVariantFilter.Enabled=true;
                    var assets=new[]{artifact.PrefabPath}.Concat(artifact.ShaderAssetPaths).Concat(entries.SelectMany(e=>new[]{e.payload,e.material})).Concat(new[]{AssetDatabase.GetAssetPath(artifact.CopyOf(f.First)),AssetDatabase.GetAssetPath(artifact.CopyOf(f.Second))}).Distinct().ToArray();
                    var built=BuildPipeline.BuildAssetBundles(dir,new[]{new AssetBundleBuild{assetBundleName="surface.bundle",assetNames=assets}},BuildAssetBundleOptions.StrictMode|BuildAssetBundleOptions.ForceRebuildAssetBundle,BuildTarget.StandaloneLinux64);Check(built,"mixed skin+mip native Vulkan bundle compiles");
                    bundle=AssetBundle.LoadFromFile(dir+"/surface.bundle");Check(bundle&&bundle.GetAllAssetNames().All(p=>!p.StartsWith(Folder.ToLowerInvariant()+"/",StringComparison.Ordinal)),"surface native bundle contains no original sources");
                    var native=Object.Instantiate(bundle.LoadAsset<GameObject>(artifact.PrefabPath.ToLowerInvariant()));copies.Add(native);foreach(var r in f.Root.GetComponentsInChildren<Transform>(true))r.gameObject.layer=28;foreach(var r in native.GetComponentsInChildren<Transform>(true))r.gameObject.layer=29;
                    var nativeClip=bundle.LoadAsset<AnimationClip>(AssetDatabase.GetAssetPath(artifact.CopyOf(f.First)).ToLowerInvariant());var nativeSkin=native.GetComponentInChildren<SkinnedMeshRenderer>(true);string provider=File.ReadAllText(artifact.ProviderAssetPaths.First(p=>File.ReadAllText(p).Contains("LAG_SKIN_LINEAR_V1")));
                    foreach(var e in entries)
                    {
                        var material=bundle.LoadAsset<Material>(e.material.ToLowerInvariant());var tex=(Texture2D)material.GetTexture("_MainTex");Check(tex.mipmapCount==7&&!tex.isReadable&&Enumerable.Range(0,4).All(i=>material.GetFloat(GuardShaders.Property(i))==0),"surface native payload and zero-key material "+e.bindingId);
                    }
                    var cameraObject=new GameObject("OwnedSurfaceCamera");copies.Add(cameraObject);var camera=cameraObject.AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.03f,.04f,.05f);camera.orthographic=true;camera.orthographicSize=1.65f;camera.nearClipPlane=.1f;camera.farClipPlane=30;camera.allowHDR=false;camera.allowMSAA=false;
                    var originalBake=new Mesh();var encodedBake=new Mesh();copies.AddRange(new Object[]{originalBake,encodedBake});float maxVertex=0;
                    foreach(float time in new[]{0f,.25f,.49f,.51f,.75f,.99f})for(int view=0;view<2;view++)
                    {
                        f.First.SampleAnimation(f.Root,time);nativeClip.SampleAnimation(native,time);Key(native,context.RuntimeKey());
                        f.Skin.BakeMesh(originalBake,false);nativeSkin.BakeMesh(encodedBake,false);var decodedPositions=DecodeSkin(encodedBake,provider,context.RuntimeKey());float vertex=originalBake.vertices.Zip(decodedPositions,(a,b)=>(a-b).magnitude).Max();maxVertex=Mathf.Max(maxVertex,vertex);
                        Check(vertex<2e-5f&&originalBake.normals.SequenceEqual(encodedBake.normals),"native animated skin CPU reference matches position/normals at "+time+"/"+view+" max="+vertex);
                        string prefix=output+"/surface-time-"+time.ToString(System.Globalization.CultureInfo.InvariantCulture)+"-view-"+view;camera.transform.position=view==0?new Vector3(0,0,-5):new Vector3(2,1,-5);camera.transform.LookAt(new Vector3(0,.2f,0));camera.cullingMask=1<<28;var original=LAGSkinningCharacterization.Render(camera,prefix+"-original.png");camera.cullingMask=1<<29;var decoded=LAGSkinningCharacterization.Render(camera,prefix+"-unlocked.png");Key(native,new int[4]);var missing=LAGSkinningCharacterization.Render(camera,prefix+"-missing.png");Key(native,context.RuntimeKey().Select(k=>k^127).ToArray());var wrong=LAGSkinningCharacterization.Render(camera,prefix+"-wrong.png");
                        var row=new Visual{name="surface/"+time+"/"+view,mean=Mean(original,decoded),max=Max(original,decoded),missing=Mean(original,missing),wrong=Mean(original,wrong)};visuals.Add(row);Check(original.Any(p=>p.g>.2f&&p.b>.2f),"nonempty posed surface reference "+row.name);Check(row.mean<.0015f&&row.missing>.006f&&row.wrong>.006f,"native surface morph/swaps/ST/minification and key gates "+row.name+" mean="+row.mean+" max="+row.max);
                    }
                    Check(Unchanged(sourceFiles),"positive surface/private/native tests preserve every source asset/meta byte");
                    File.WriteAllText(output+"/surface-validation.json",JsonUtility.ToJson(new Report{unity=Application.unityVersion,graphics=SystemInfo.graphicsDeviceType.ToString(),gpu=SystemInfo.graphicsDeviceName,colorSpace=QualitySettings.activeColorSpace.ToString(),checks=checks.ToArray(),visuals=visuals.ToArray(),images=Directory.GetFiles(output,"surface-*.png").Length},true));
                    File.WriteAllText(output+"/skin-vertex-error.json","{\"maxPositionError\":"+maxVertex.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+",\"bakeIsCpuReference\":true,\"gpuSkinningPlayerPending\":true}");
                    Debug.Log("LAG_MIP_SKIN_SURFACE_SUCCESS "+checks.Count+" checks; "+visuals.Count+" visuals; "+QualitySettings.activeColorSpace);
                }
            }
        }
        finally{foreach(var obj in copies)if(obj)Object.DestroyImmediate(obj);if(bundle)bundle.Unload(true);LAGDiversityVariantFilter.Enabled=false;PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,automatic);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,oldApi);Environment.SetEnvironmentVariable("XDG_DATA_HOME",old);Directory.Delete(scratch,true);}
    }
    public static void RunSurfaceAndExport(){RunSurface();RunPlayerFixtures();}
    public static void RunMatrix(){RunTextures();RunSurface();RunPlayerFixtures();}
    public static void RunStandaloneMipAudit()
    {
        Start();var tex=Texture("standalone-fault",TextureFormat.RGBA32,true,FilterMode.Trilinear);var material=Material("standalone-fault",tex);Save();var before=Snapshot(Folder);string path=NewOutput("standalone-fault");
        LAGMipSkinFault.Prefix=path;LAGMipSkinFault.Mode="mips";LAGMipSkinFault.Touched=false;
        try
        {
            using(var codec=new TextureGuardCodecV2(new byte[32],"OwnedStandaloneMipAudit"))
                Check(Rejects(()=>GuardTextureForge.Prepare(material,codec,path,new[]{31,62,93,124}))&&LAGMipSkinFault.Touched&&!Directory.Exists(path),"standalone V2 also rolls back a late sampler mip-bias mutation");
            Check(Unchanged(before),"standalone V2 late mutation preserves source asset/meta bytes");
            File.WriteAllText(output+"/standalone-audit.json",JsonUtility.ToJson(new Report{unity=Application.unityVersion,graphics=SystemInfo.graphicsDeviceType.ToString(),checks=checks.ToArray()},true));
            Debug.Log("LAG_MIP_STANDALONE_AUDIT_SUCCESS "+checks.Count+" checks");
        }
        finally{LAGMipSkinFault.Mode=null;}
    }
    public static void RunPreviousRegression()
    {
        RunStandaloneMipAudit();LAGTextureIntegrationValidation.RunRegression();
        // All assertions/reports are complete. Avoid waiting for unrelated SDK background tasks.
        if(Application.isBatchMode)EditorApplication.Exit(0);
    }
}
public sealed class LAGMipSkinFault : AssetPostprocessor
{
    public static string Prefix,Mode;public static SkinnedMeshRenderer Source;public static bool Touched;
    static void OnPostprocessAllAssets(string[] imported,string[] deleted,string[] moved,string[] previous)
    {
        if(Mode==null||Touched||Environment.GetEnvironmentVariable("LAG_BINDING_AUDIT_ALLOWED")!="synthetic-unity-only")return;
        string path=imported.FirstOrDefault(p=>p.StartsWith(Prefix,StringComparison.Ordinal)&&(Mode=="mips"?(p.EndsWith("/payload.asset")||Path.GetFileName(p).StartsWith("t_",StringComparison.Ordinal)&&p.EndsWith(".asset")):p.EndsWith("/mesh.asset")));if(path==null)return;Touched=true;
        if(Mode=="mesh"){var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);var p=mesh.vertices;p[0].x+=.01f;mesh.vertices=p;EditorUtility.SetDirty(mesh);}
        if(Mode=="mips"){var t=AssetDatabase.LoadAssetAtPath<Texture2D>(path);t.mipMapBias+=.75f;EditorUtility.SetDirty(t);}
        if(Mode=="bone")Source.bones=Source.bones.Reverse().ToArray();
    }
}
