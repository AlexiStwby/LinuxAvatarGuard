// SPDX-License-Identifier: MIT
// Procedural assets owned by this project; no avatar package or VRChat process is inspected.
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

public static class LAGTextureIntegrationValidation
{
    const string Folder = "Assets/TextureIntegrationFixture";
    static string output;
    static readonly List<string> checks = new List<string>();
    static readonly List<Visual> visuals = new List<Visual>();
    static int adaptiveCases, rejectedPrograms;
    static float maxVisual, maxChannel, minLocked = float.PositiveInfinity, minWrong = float.PositiveInfinity;
    static string Hash(byte[] data) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
    static byte[] Seed(string scope) { using (var h = SHA256.Create()) return h.ComputeHash(Encoding.UTF8.GetBytes("LAG/owned-texture-integration/v1/" + scope)); }
    static void Check(bool ok, string description) { if (!ok) throw new Exception("TEXTURE_INTEGRATION_FAILED: " + description); checks.Add(description); }
    static bool Rejects(Action a) => LAGBindingValidation.Rejects(a);
    static Dictionary<string,string> Snapshot(string path) => Directory.GetFiles(path,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>Hash(File.ReadAllBytes(p)));
    static bool Unchanged(Dictionary<string,string> before) => before.All(p=>File.Exists(p.Key)&&Hash(File.ReadAllBytes(p.Key))==p.Value);
    static string NewOutput(string name)
    {
        string path="Assets/LinuxAvatarGuardGenerated/Research/texture-integration-"+name;
        if(AssetDatabase.IsValidFolder(path))AssetDatabase.DeleteAsset(path);
        return path;
    }
    static void Save(string directory)
    {
        foreach(string p in AssetDatabase.FindAssets("",new[]{directory}).Select(AssetDatabase.GUIDToAssetPath).Distinct())
            foreach(var a in AssetDatabase.LoadAllAssetsAtPath(p))if(a)AssetDatabase.SaveAssetIfDirty(a);
    }
    sealed class Fixture : IDisposable
    {
        public GameObject Root;
        public string Assets;
        public Material A,B;
        public AnimationClip First,Second;
        public AnimatorController Base;
        public AnimatorOverrideController Override;
        public void Dispose() { if(Root)Object.DestroyImmediate(Root); }
    }
    static Texture2D Texture(string path,TextureFormat format,bool srgb,FilterMode filter,int pattern,bool mips=false)
    {
        const int size=64;
        var tex=new Texture2D(size,size,format,mips,!srgb) {name="OwnedAlbedo",filterMode=filter,wrapModeU=TextureWrapMode.Repeat,wrapModeV=TextureWrapMode.Clamp,anisoLevel=0};
        var pixels=new Color32[size*size];
        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
        {
            bool mark=(x+3*y+pattern)%19<4;
            pixels[x+size*y]=new Color32((byte)(mark?235:20+x*3),(byte)(mark?30+pattern*31:18+y*3),(byte)(mark?73:20+(x+2*y+pattern*39)%225),255);
        }
        tex.SetPixels32(pixels);tex.Apply(mips,false);AssetDatabase.CreateAsset(tex,path);AssetDatabase.SaveAssetIfDirty(tex);return tex;
    }
    static Fixture CreateFixture(string name,bool inverse)
    {
        string assets=Folder+"/"+name;Directory.CreateDirectory(assets);AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var f=new Fixture {Assets=assets,Root=new GameObject("OwnedTextureIntegration")};f.Root.SetActive(false);
        f.A=new Material(Shader.Find("lilToon"));f.B=new Material(Shader.Find("lilToon"));
        f.A.SetTexture("_MainTex",Texture(assets+"/rgba.asset",TextureFormat.RGBA32,!inverse,inverse?FilterMode.Point:FilterMode.Bilinear,0));
        f.B.SetTexture("_MainTex",Texture(assets+"/rgb.asset",TextureFormat.RGB24,inverse,inverse?FilterMode.Bilinear:FilterMode.Point,2));
        foreach(var m in new[]{f.A,f.B}) {m.SetFloat("_AsUnlit",1);m.SetFloat("_Cull",0);m.SetFloat("_IDMaskFrom",0);m.SetColor("_Color",Color.white);}
        AssetDatabase.CreateAsset(f.A,assets+"/a.mat");AssetDatabase.CreateAsset(f.B,assets+"/b.mat");
        var mesh=LAGBindingValidation.Fixture();var indices=mesh.triangles;mesh.subMeshCount=2;
        mesh.SetTriangles(indices.Take(indices.Length/2).ToArray(),0);mesh.SetTriangles(indices.Skip(indices.Length/2).ToArray(),1);
        AssetDatabase.CreateAsset(mesh,assets+"/mesh.asset");
        foreach(string part in new[]{"Left","Right"})
        {
            var go=new GameObject(part);go.transform.SetParent(f.Root.transform,false);go.transform.localPosition=new Vector3(part=="Left"?-.65f:.65f,0,0);
            go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterials=new[]{f.A,f.B};
        }
        f.First=Clip(assets+"/first.anim",f,false);f.Second=Clip(assets+"/second.anim",f,true);
        f.Base=AnimatorController.CreateAnimatorControllerAtPath(assets+"/base.controller");f.Base.AddParameter("Blend",AnimatorControllerParameterType.Float);
        var state=f.Base.layers[0].stateMachine.AddState("OwnedState");f.Base.layers[0].stateMachine.defaultState=state;
        var inner=new BlendTree {name="OwnedInner",blendType=BlendTreeType.Simple1D,blendParameter="Blend",useAutomaticThresholds=false};
        inner.AddChild(f.First,0);inner.AddChild(f.Second,1);AssetDatabase.AddObjectToAsset(inner,f.Base);
        var outer=new BlendTree {name="OwnedOuter",blendType=BlendTreeType.Simple1D,blendParameter="Blend",useAutomaticThresholds=false};
        outer.AddChild(inner,0);outer.AddChild(f.First,1);AssetDatabase.AddObjectToAsset(outer,f.Base);state.motion=outer;EditorUtility.SetDirty(state);EditorUtility.SetDirty(f.Base);
        f.Override=new AnimatorOverrideController(f.Base);
        f.Override.ApplyOverrides(new[]{new KeyValuePair<AnimationClip,AnimationClip>(f.First,f.Second),new KeyValuePair<AnimationClip,AnimationClip>(f.Second,f.First)});
        AssetDatabase.CreateAsset(f.Override,assets+"/override.overrideController");
        var animator=f.Root.AddComponent<Animator>();animator.enabled=false;animator.runtimeAnimatorController=inverse?(RuntimeAnimatorController)f.Override:f.Base;
        Save(assets);return f;
    }
    static AnimationClip Clip(string path,Fixture f,bool reverse)
    {
        var clip=new AnimationClip {name=reverse?"OwnedReverse":"OwnedForward",frameRate=60};
        foreach(string part in new[]{"Left","Right"})
        {
            for(int slot=0;slot<2;slot++)
            {
                bool inv=reverse^(slot==1);
                AnimationUtility.SetObjectReferenceCurve(clip,EditorCurveBinding.PPtrCurve(part,typeof(MeshRenderer),"m_Materials.Array.data["+slot+"]"),new[]{
                    new ObjectReferenceKeyframe{time=0,value=inv?f.B:f.A},new ObjectReferenceKeyframe{time=.5f,value=inv?f.A:f.B},new ObjectReferenceKeyframe{time=1,value=inv?f.B:f.A}});
            }
            var endpoints=reverse?new[]{2.2f,.8f,.2f,-.25f}:new[]{.8f,1.7f,-.2f,.15f};
            for(int i=0;i<4;i++)AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(part,typeof(MeshRenderer),"material._MainTex_ST."+"xyzw"[i]),AnimationCurve.Linear(0,endpoints[i],1,endpoints[3-i]));
            AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(part,typeof(MeshRenderer),"material._Color.r"),AnimationCurve.Linear(0,.85f,1,.6f));
            // SampleAnimation's renderer property sheet initializes unbound vector channels
            // to zero. Bind every color/position channel so RGB and renderer separation survive.
            foreach(char channel in "gba")AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(part,typeof(MeshRenderer),"material._Color."+channel),AnimationCurve.Constant(0,1,1));
            AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(part,typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,part=="Left"?-.65f:.65f));
            AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(part,typeof(Transform),"m_LocalPosition.z"),AnimationCurve.Constant(0,1,0));
            AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(part,typeof(Transform),"m_LocalPosition.y"),AnimationCurve.Linear(0,0,1,reverse?-.12f:.12f));
        }
        AssetDatabase.CreateAsset(clip,path);return clip;
    }
    static GuardBuildContext Context(Fixture f,string scope,bool zeroLane=false)
    {
        var animation=GuardAnimationContext.Capture(f.Root,true);
        for(int i=0;i<4096;i++)
        {
            var seed=Seed(scope+"/"+i);var c=GuardBuildContext.CreateReproducible(Hash(seed).Substring(0,32),seed);
            try
            {
                if(zeroLane&&!c.RuntimeKey().Contains(0)){c.Dispose();continue;}
                foreach(var r in f.Root.GetComponentsInChildren<MeshRenderer>(true))using(var codec=c.CreateContextualCodec(MeshBindingIdentity.Capture(f.Root,r),animation))
                {var mesh=r.GetComponent<MeshFilter>().sharedMesh;var plan=codec.Plan(mesh,.15f);var encoded=codec.Encode(mesh,plan,c.RuntimeKey());Object.DestroyImmediate(encoded);}
                return c;
            }
            catch(InvalidOperationException e)when(e.Message.Contains("cancela sus offsets keyed")||e.Message.Contains("no supera el error de reconstrucción sin clave")) {c.Dispose();rejectedPrograms++;}
            catch{c.Dispose();throw;}
        }
        throw new Exception("No admissible own contextual program");
    }
    static void Contracts(Fixture f)
    {
        var r=f.Root.GetComponentsInChildren<MeshRenderer>(true)[0];var mesh=MeshBindingIdentity.Capture(f.Root,r);var animation=GuardAnimationContext.Capture(f.Root,true);
        var binding=GuardTextureBindingIdentity.Capture(mesh,animation,0,f.A);
        Check(binding.StableId==GuardTextureBindingIdentity.Capture(mesh,animation,0,f.A).StableId,"texture identity is reproducible for identical contextual input");
        Check(binding.StableId!=GuardTextureBindingIdentity.Capture(mesh,animation,1,f.A).StableId,"same material in another slot has another identity");
        Check(Rejects(()=>GuardTextureBindingIdentity.Capture(mesh,animation,-1,f.A))&&Rejects(()=>GuardTextureBindingIdentity.Capture(mesh,animation,2,f.A)),"out-of-range material slots reject");
        Check(Rejects(()=>GuardAnimationContext.Capture(f.Root)),"legacy animation policy still rejects unreviewed texture-transform curves");
        using(var standalone=new TextureGuardCodecV1(Seed("standalone"),"standalone"))Check(Rejects(()=>standalone.Plan((Texture2D)f.B.GetTexture("_MainTex"))),"the Stage 11 standalone contract still rejects RGB24");
        using(var context=Context(f,"contracts"))using(var codec=context.CreateTextureCodec(binding))
        {
            var source=(Texture2D)f.A.GetTexture("_MainTex");var plan=codec.Plan(source);var wrong=context.RuntimeKey();wrong[0]^=1;
            Check(plan.BindingStableId==binding.StableId,"texture plan carries its contextual binding ID");
            Check(Rejects(()=>codec.Encode(source,plan,wrong)),"texture encoding rejects keys from another context");
            Check(Rejects(()=>codec.Plan((Texture2D)f.B.GetTexture("_MainTex"))),"contextual codec rejects a foreign texture");
            Check(Rejects(()=>context.RecordTexturePlan(binding,plan))&&context.TextureBindingCount==0,"texture cannot be recorded without its parent mesh plan");
            f.A.SetTextureOffset("_MainTex",new Vector2(.3f,-.1f));AssetDatabase.SaveAssetIfDirty(f.A);
            try {Check(Rejects(()=>codec.EmitDecoder(plan)),"saved material transform mutation invalidates a contextual texture plan");}
            finally {f.A.SetTextureOffset("_MainTex",Vector2.zero);AssetDatabase.SaveAssetIfDirty(f.A);}
        }
        using(var context=Context(f,"atomic"))using(var foreign=Context(f,"foreign"))
        {
            animation=GuardAnimationContext.Capture(f.Root,true);
            var meshPlans=new List<Tuple<MeshBindingIdentity,CodecPlan>>();
            foreach(var renderer in f.Root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var id=MeshBindingIdentity.Capture(f.Root,renderer);
                using(var codec=context.CreateContextualCodec(id,animation))meshPlans.Add(Tuple.Create(id,codec.Plan(renderer.GetComponent<MeshFilter>().sharedMesh,.15f)));
            }
            var first=GuardTextureBindingIdentity.Capture(meshPlans[0].Item1,animation,0,f.A);
            var second=GuardTextureBindingIdentity.Capture(meshPlans[0].Item1,animation,1,f.A);
            using(var valid=context.CreateTextureCodec(first))using(var invalid=foreign.CreateTextureCodec(second))
            {
                var texturePlans=new[]{Tuple.Create(first,valid.Plan((Texture2D)f.A.GetTexture("_MainTex"))),Tuple.Create(second,invalid.Plan((Texture2D)f.A.GetTexture("_MainTex")))};
                bool rejected=false;
                try {typeof(GuardBuildContext).GetMethod("RecordPlans",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(context,new object[]{meshPlans,animation,texturePlans});}
                catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException||e.InnerException is ArgumentException){rejected=true;}
                Check(rejected&&context.BindingCount==0&&context.TextureBindingCount==0,"batch record failure after valid mesh/texture records atomically restores both maps");
            }
        }
        var original=f.A.GetTexture("_MainTex");
        var mip=Texture(f.Assets+"/negative-mips.asset",TextureFormat.RGBA32,true,FilterMode.Bilinear,1,true);
        f.A.SetTexture("_MainTex",mip);AssetDatabase.SaveAssetIfDirty(f.A);
        using(var c=Context(f,"negative-mips"))
        {
            string path=NewOutput("negative-mips");
            Check(Rejects(()=>GuardShaderForge.PrepareWithTextures(f.Root,c,path))&&!Directory.Exists(path)&&c.BindingCount==0&&c.TextureBindingCount==0,"mipmap source rejects before writes/records; importer is not modified");
        }
        f.A.SetTexture("_MainTex",original);AssetDatabase.SaveAssetIfDirty(f.A);
        // A source albedo reachable through another material's mask must not leak into the artifact.
        f.B.SetTexture("_MainColorAdjustMask",original);AssetDatabase.SaveAssetIfDirty(f.B);
        try
        {
            using(var c=Context(f,"cross-alias"))
            {string path=NewOutput("cross-alias");Check(Rejects(()=>GuardShaderForge.PrepareWithTextures(f.Root,c,path))&&!Directory.Exists(path)&&c.BindingCount==0&&c.TextureBindingCount==0,"cross-material plaintext albedo dependency rejects and rolls back all output/records");}
        }
        finally {f.B.SetTexture("_MainColorAdjustMask",null);AssetDatabase.SaveAssetIfDirty(f.B);}
        var before=Snapshot(f.Assets);
        foreach(string mode in new[]{"key","sampler","source","clip"})
        {
            using(var c=Context(f,"late-"+mode))
            {
                string path=NewOutput("late-"+mode);LAGTextureIntegrationFault.Prefix=path+"/";LAGTextureIntegrationFault.Mode=mode;LAGTextureIntegrationFault.Source=f.A;LAGTextureIntegrationFault.Touched=false;
                try {Check(Rejects(()=>GuardShaderForge.PrepareWithTextures(f.Root,c,path))&&LAGTextureIntegrationFault.Touched&&!Directory.Exists(path)&&c.BindingCount==0&&c.TextureBindingCount==0,"late "+mode+" mutation rolls back output and both private record maps");}
                finally {LAGTextureIntegrationFault.Mode=null;LAGTextureIntegrationFault.Source=null;f.A.SetTextureOffset("_MainTex",Vector2.zero);AssetDatabase.SaveAssetIfDirty(f.A);}
            }
        }
        Check(Unchanged(before),"negative and rollback tests preserve every source asset/meta byte");
    }
    [Serializable] sealed class TextureEntry {public string bindingId,meshBindingId,property,programHash,payload,material;public int slot,codecVersion,mipCount;}
    [Serializable] sealed class Manifest {public bool texturesProtected,sdkProcessed;public TextureEntry[] textureBindings;}
    static TextureEntry[] Entries(GuardShaderArtifact artifact)=>JsonUtility.FromJson<Manifest>(File.ReadAllText(artifact.PublicManifestPath)).textureBindings;
    static void Structure(Fixture f,GuardBuildContext c,GuardShaderArtifact a)
    {
        var entries=Entries(a);string json=File.ReadAllText(a.PublicManifestPath);var animation=GuardAnimationContext.Capture(f.Root,true);
        Check(c.BindingCount==2&&c.TextureBindingCount==8&&entries.Length==8,"two renderer bindings and eight slot/material texture occurrences are registered");
        Check(entries.Select(t=>t.bindingId).Distinct().Count()==8&&entries.Select(t=>t.programHash).Distinct().Count()==8,"shared source textures generate distinct programs across renderer/slot/material bindings");
        Check(entries.Select(t=>AssetDatabase.LoadAssetAtPath<Material>(t.material).shader).Distinct().Count()==8,"each texture occurrence has its own combined mesh/albedo shader family");
        Check(!json.Contains("masterSeed")&&!json.Contains("runtimeKey")&&json.Contains("\"texturesProtected\": true")&&json.Contains("\"sdkProcessed\": false"),"public manifest declares bounded research coverage without private seed/keys");
        var deps=AssetDatabase.GetDependencies(a.PrefabPath,true);
        Check(deps.All(p=>!p.StartsWith(f.Assets+"/",StringComparison.Ordinal)),"prefab dependency closure excludes all original textures, materials, meshes, clips and controllers");
        foreach(var entry in entries)
        {
            var texture=AssetDatabase.LoadAssetAtPath<Texture2D>(entry.payload);var material=AssetDatabase.LoadAssetAtPath<Material>(entry.material);
            Check(texture&&!texture.isReadable&&!texture.isDataSRGB&&texture.format==TextureFormat.RGBA32&&texture.mipmapCount==1&&material.GetTexture("_MainTex")==texture,"encoded payload contract and material reference hold "+entry.bindingId);
            Check(Enumerable.Range(0,4).All(i=>material.GetFloat(GuardShaders.Property(i))==0),"serialized keys are zero "+entry.bindingId);
            Check(string.Equals(material.GetTag("DisableBatching",false,""),"True",StringComparison.OrdinalIgnoreCase),"combined nonlinear shader disables batching "+entry.bindingId);
            var renderer=f.Root.GetComponentsInChildren<MeshRenderer>(true).Single(r=>MeshBindingIdentity.Capture(f.Root,r).StableId==entry.meshBindingId);
            var source=animation.Clips.SelectMany(clip=>AnimationUtility.GetObjectReferenceCurveBindings(clip)).Where(b=>b.path==AnimationUtility.CalculateTransformPath(renderer.transform,f.Root.transform)&&b.propertyName=="m_Materials.Array.data["+entry.slot+"]")
                .SelectMany(b=>animation.Clips.SelectMany(clip=>AnimationUtility.GetObjectReferenceCurve(clip,b))).Select(k=>k.value).OfType<Material>().Distinct()
                .Single(m=>GuardTextureBindingIdentity.Capture(MeshBindingIdentity.Capture(f.Root,renderer),animation,entry.slot,m).StableId==entry.bindingId);
            var original=(Texture2D)source.GetTexture("_MainTex");
            var payload=LAGTextureValidation.ReadPayload(texture);
            string provider=a.ProviderAssetPaths.Single(p=>Path.GetDirectoryName(p)==Path.GetDirectoryName(AssetDatabase.GetAssetPath(material.shader)));
            string hlsl=File.ReadAllText(provider);
            Check(LAGTextureValidation.ByteError(LAGTextureValidation.Extract(payload,original.width,hlsl,c.RuntimeKey()),original.GetPixels32())==0,"independent public-HLSL extractor exactly recovers contextual payload (observed limitation) "+entry.bindingId);adaptiveCases++;
            Check(LAGTextureValidation.ByteError(LAGTextureValidation.Extract(payload,original.width,hlsl,new int[4]),original.GetPixels32())>0,"missing keys do not recover the source bytes "+entry.bindingId);
        }
        foreach(var clip in new[]{f.First,f.Second})
        {
            var copy=a.CopyOf(clip);
            Check(AnimationUtility.GetCurveBindings(clip).All(b=>AnimationUtility.GetEditorCurve(clip,b).keys.SequenceEqual(AnimationUtility.GetEditorCurve(copy,b).keys)),"texture ST/color/transform curves preserve every key "+clip.name);
            foreach(var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var original=AnimationUtility.GetObjectReferenceCurve(clip,b);var frames=AnimationUtility.GetObjectReferenceCurve(copy,b);
                Check(original.Select(k=>k.time).SequenceEqual(frames.Select(k=>k.time))&&frames.All(k=>k.value is Material m&&entries.Any(e=>e.material==AssetDatabase.GetAssetPath(m))),"animated swaps reference only generated materials "+b.path+"/"+b.propertyName);
            }
        }
        string path=c.SavePrivate();string text=File.ReadAllText(path);
        Check(text.Contains("\"schemaVersion\": "+GuardBuildContext.SchemaVersion)&&text.Contains("\"textureBindingSchema\": 1"),"current schema stores explicit texture codec/binding version privately");
        try
        {
            File.WriteAllText(path,text.Replace("\"schemaVersion\": "+GuardBuildContext.SchemaVersion,"\"schemaVersion\": 4"));
            using(var old=GuardBuildContext.LoadPrivate(c.BuildId))
                Check(old.TextureBindingCount==8&&old.RuntimeKey().SequenceEqual(c.RuntimeKey()),"schema 4 V1 texture records preserve count and historical runtime keys");
        }
        finally{File.WriteAllText(path,text);}
        foreach(var mutation in new[]{
            text.Replace("\"schemaVersion\": "+GuardBuildContext.SchemaVersion,"\"schemaVersion\": 3"),
            text.Replace("\"textureCodecVersion\": 1","\"textureCodecVersion\": 99"),
            text.Replace("\"textureBindingSchema\": 1","\"textureBindingSchema\": 0"),
            text.Replace("\"property\": \"_MainTex\"","\"property\": \"_NormalMap\""),
            text.Replace("\"slot\": 0","\"slot\": -1"),
            Regex.Replace(text,@"""meshBindingId"": ""[0-9a-f]{64}""","\"meshBindingId\": \""+new string('f',64)+"\""),
            text.Replace("\"sourceLocalFileId\": 2800000","\"sourceLocalFileId\": 0")})
        {
            try {File.WriteAllText(path,mutation);Check(mutation!=text&&Rejects(()=>GuardBuildContext.LoadPrivate(c.BuildId)),"malformed/downgraded private texture context rejects");}
            finally {File.WriteAllText(path,text);}
        }
        using(var restored=GuardBuildContext.LoadPrivate(c.BuildId))
        {
            Check(restored.RuntimeKey().SequenceEqual(c.RuntimeKey())&&restored.TextureBindingCount==8,"private replay preserves historical runtime key bytes and texture record count");
            Check(Rejects(()=>GuardShaderForge.Prepare(f.Root,restored,NewOutput("downgrade"))),"texture context cannot silently generate a mesh-only artifact");
            using(var replay=GuardShaderForge.PrepareWithTextures(f.Root,restored,NewOutput("replay-"+Path.GetFileName(f.Assets))))
            {
                var replayEntries=Entries(replay);
                Check(entries.Select(e=>e.programHash).SequenceEqual(replayEntries.Select(e=>e.programHash)),"private context exactly reproduces every contextual texture program");
                foreach(var e in entries)
                {
                    var replayEntry=replayEntries.Single(t=>t.bindingId==e.bindingId);
                    Check(LAGTextureValidation.ByteError(LAGTextureValidation.ReadPayload(AssetDatabase.LoadAssetAtPath<Texture2D>(e.payload)),LAGTextureValidation.ReadPayload(AssetDatabase.LoadAssetAtPath<Texture2D>(replayEntry.payload)))==0,"private context exactly reproduces payload "+e.bindingId);
                }
            }
            var original=f.A.GetTexture("_MainTex");f.A.SetTexture("_MainTex",f.B.GetTexture("_MainTex"));AssetDatabase.SaveAssetIfDirty(f.A);
            try {var mesh=MeshBindingIdentity.Capture(f.Root,f.Root.GetComponentsInChildren<MeshRenderer>(true)[0]);var b=GuardTextureBindingIdentity.Capture(mesh,GuardAnimationContext.Capture(f.Root,true),0,f.A);Check(Rejects(()=>restored.CreateTextureCodec(b)),"private context refuses a changed albedo/material binding");}
            finally {f.A.SetTexture("_MainTex",original);AssetDatabase.SaveAssetIfDirty(f.A);}
        }
        string marker=a.Folder+"/sentinel.txt";File.WriteAllText(marker,"preserve own output");
        Check(Rejects(()=>GuardShaderForge.PrepareWithTextures(f.Root,c,a.Folder))&&File.ReadAllText(marker)=="preserve own output","existing generated output is preserved on rejection");
    }
    static AnimationClip KeyClip(AnimationClip template,int[] key,AssetBundle bundle,List<Object> transient)
    {
        var clip=Object.Instantiate(template);clip.hideFlags=HideFlags.DontSave;transient.Add(clip);
        foreach(var b in AnimationUtility.GetObjectReferenceCurveBindings(template))
        {
            var frames=AnimationUtility.GetObjectReferenceCurve(template,b);
            for(int i=0;i<frames.Length;i++)frames[i].value=bundle.LoadAsset<Material>(AssetDatabase.GetAssetPath(frames[i].value).ToLowerInvariant());
            AnimationUtility.SetObjectReferenceCurve(clip,b,frames);
        }
        foreach(string path in AnimationUtility.GetObjectReferenceCurveBindings(template).Select(b=>b.path).Distinct())
            for(int i=0;i<4;i++)AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(path,typeof(MeshRenderer),"material."+GuardShaders.Property(i)),AnimationCurve.Constant(0,1,key[i]));
        return clip;
    }
    static Color[] Render(Camera camera,string path)
    {
        var rt=new RenderTexture(384,256,24,RenderTextureFormat.ARGB32);var tex=new Texture2D(384,256,TextureFormat.RGBA32,false);var old=RenderTexture.active;
        try {camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,384,256),0,0);tex.Apply();File.WriteAllBytes(path,tex.EncodeToPNG());return tex.GetPixels();}
        finally {camera.targetTexture=null;RenderTexture.active=old;Object.DestroyImmediate(tex);Object.DestroyImmediate(rt);}
    }
    static float Difference(Color[] a,Color[] b)=>a.Zip(b,(x,y)=>(Mathf.Abs(x.r-y.r)+Mathf.Abs(x.g-y.g)+Mathf.Abs(x.b-y.b))/3).Average();
    static void Clear(GameObject root)
    {foreach(var r in root.GetComponentsInChildren<MeshRenderer>(true)){r.SetPropertyBlock(null);for(int i=0;i<r.sharedMaterials.Length;i++)r.SetPropertyBlock(null,i);}}
    [Serializable] sealed class Visual {public string name;public float meanDifference,maxChannelDifference,lockedDifference,wrongDifference;}
    static void Compare(Camera camera,GameObject reference,GameObject visible,AnimationClip source,AnimationClip unlocked,AnimationClip locked,AnimationClip wrong,float time,string name)
    {
        Clear(reference);Clear(visible);source.SampleAnimation(reference,time);unlocked.SampleAnimation(visible,time);
        Check(Vector3.Distance(reference.transform.Find("Left").localPosition,visible.transform.Find("Left").localPosition)<1e-6f,"native transform curves reproduce source "+name);
        Check(Mathf.Abs(reference.transform.Find("Right").localPosition.x-reference.transform.Find("Left").localPosition.x-1.3f)<1e-6f&&
            Mathf.Abs(visible.transform.Find("Right").localPosition.x-visible.transform.Find("Left").localPosition.x-1.3f)<1e-6f,"both renderers remain independently visible during sampled animation "+name);
        Check(visible.GetComponentsInChildren<MeshRenderer>(true).All(r=>r.sharedMaterials.All(m=>m.shader.name.StartsWith("LinuxAvatarGuard/"))),"native swaps retain contextual shaders "+name);
        camera.cullingMask=1<<28;var original=Render(camera,output+"/"+name+"-original.png");camera.cullingMask=1<<29;
        var decoded=Render(camera,output+"/"+name+"-unlocked.png");locked.SampleAnimation(visible,time);var missing=Render(camera,output+"/"+name+"-locked.png");wrong.SampleAnimation(visible,time);var incorrect=Render(camera,output+"/"+name+"-wrong.png");
        Check(original.Any(p=>p.g>.4f&&p.b>.4f)&&decoded.Any(p=>p.g>.4f&&p.b>.4f),"source and native unlocked images visibly exercise green and blue channels "+name);
        float mean=Difference(original,decoded),bad=Difference(original,missing),other=Difference(original,incorrect);
        float channel=original.Zip(decoded,(a,b)=>Mathf.Max(Mathf.Abs(a.r-b.r),Mathf.Max(Mathf.Abs(a.g-b.g),Mathf.Abs(a.b-b.b)))).Max();
        maxVisual=Mathf.Max(maxVisual,mean);maxChannel=Mathf.Max(maxChannel,channel);minLocked=Mathf.Min(minLocked,bad);minWrong=Mathf.Min(minWrong,other);
        visuals.Add(new Visual{name=name,meanDifference=mean,maxChannelDifference=channel,lockedDifference=bad,wrongDifference=other});
        Check(mean<=.0015f&&bad>.006f&&other>.006f,"native combined mesh/albedo image meets mean-error and wrong/missing-key gates "+name+"; mean="+mean+", max="+channel+", missing="+bad+", wrong="+other);
    }
    static void Graph(GameObject root,Fixture f,GuardShaderArtifact artifact,GuardBuildContext context,AssetBundle bundle,List<Object> transient,bool protect)
    {
        if(protect)
        {
            var input=root.GetComponent<Animator>().runtimeAnimatorController;
            var pairs=new List<KeyValuePair<AnimationClip,AnimationClip>>();
            if(input is AnimatorOverrideController existing){existing.GetOverrides(pairs);input=existing.runtimeAnimatorController;}
            else pairs.AddRange(input.animationClips.Select(c=>new KeyValuePair<AnimationClip,AnimationClip>(c,c)));
            var templates=new[]{artifact.CopyOf(f.First),artifact.CopyOf(f.Second)}.ToDictionary(c=>c.name,c=>c);
            var graph=new AnimatorOverrideController(input){hideFlags=HideFlags.DontSave};transient.Add(graph);
            graph.ApplyOverrides(pairs.Select(p=>new KeyValuePair<AnimationClip,AnimationClip>(p.Key,KeyClip(templates[(p.Value?p.Value:p.Key).name],context.RuntimeKey(),bundle,transient))).ToArray());root.GetComponent<Animator>().runtimeAnimatorController=graph;
        }
        var animator=root.GetComponent<Animator>();Clear(root);animator.enabled=true;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.Rebind();animator.Update(0);animator.SetFloat("Blend",.35f);animator.Update(.3f);
        Check(animator.isInitialized&&animator.GetCurrentAnimatorStateInfo(0).normalizedTime>.25f,"real Animator graph advances beyond a static pose");
    }
    static void Native(Fixture[] fixtures,GuardBuildContext[] contexts,GuardShaderArtifact[] artifacts)
    {
        var builds=artifacts.Select((a,i)=>new AssetBundleBuild {assetBundleName="integrated-"+i+".bundle",assetNames=new[]{a.PrefabPath,a.PublicManifestPath}.Concat(a.ShaderAssetPaths)
            .Concat(Entries(a).SelectMany(t=>new[]{t.payload,t.material})).Concat(new[]{AssetDatabase.GetAssetPath(a.CopyOf(fixtures[i].First)),AssetDatabase.GetAssetPath(a.CopyOf(fixtures[i].Second))}).Distinct().ToArray()}).ToArray();
        var cameraObject=new GameObject("OwnedTextureIntegrationCamera");var camera=cameraObject.AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.04f,.05f,.06f);camera.orthographic=true;camera.orthographicSize=1.05f;camera.nearClipPlane=.01f;camera.farClipPlane=20;camera.allowHDR=false;camera.allowMSAA=false;
        var oldApis=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneLinux64);bool oldAuto=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64);
        try
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,new[]{GraphicsDeviceType.Vulkan});LAGDiversityVariantFilter.Enabled=true;
            string linux=output+"/linux-bundles";Directory.CreateDirectory(linux);
            var watch=Stopwatch.StartNew();var built=BuildPipeline.BuildAssetBundles(linux,builds,BuildAssetBundleOptions.StrictMode|BuildAssetBundleOptions.ForceRebuildAssetBundle,BuildTarget.StandaloneLinux64);watch.Stop();
            Check(built&&built.GetAllAssetBundles().Length==2,"two native integrated Vulkan bundles build");
            Check(artifacts.SelectMany(a=>a.ShaderAssetPaths).All(p=>!ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(p))),"all combined facades/providers compile without errors during Linux build");
            for(int i=0;i<artifacts.Length;i++)
            {
                var a=artifacts[i];var f=fixtures[i];var c=contexts[i];var bundle=AssetBundle.LoadFromFile(Path.GetFullPath(linux+"/integrated-"+i+".bundle"));GameObject visible=null,reference=null;var transient=new List<Object>();
                try
                {
                    Check(bundle,"native bundle opens "+i);foreach(string path in a.ProviderAssetPaths)Check(bundle.LoadAsset<Shader>(path.ToLowerInvariant()),"native provider loads "+i+"/"+Path.GetFileName(path));
                    var encoded=bundle.LoadAllAssets<Texture2D>();Check(encoded.Length==8&&encoded.All(t=>!t.isReadable&&!t.isDataSRGB&&t.mipmapCount==1&&!GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat)),"native bundle contains exactly eight linear unreadable payloads "+i);
                    var materials=bundle.LoadAllAssets<Material>();Check(materials.Length==8&&materials.All(m=>Enumerable.Range(0,4).All(k=>m.GetFloat(GuardShaders.Property(k))==0)&&encoded.Contains(m.GetTexture("_MainTex"))),"native materials have zero keys and encoded albedos "+i);
                    Check(bundle.GetAllAssetNames().All(p=>!p.StartsWith(f.Assets.ToLowerInvariant()+"/",StringComparison.Ordinal)),"native bundle exposes none of the original source assets "+i);
                    visible=Object.Instantiate(bundle.LoadAsset<GameObject>(a.PrefabPath.ToLowerInvariant()));reference=Object.Instantiate(f.Root);visible.SetActive(true);reference.SetActive(true);
                    foreach(var t in reference.GetComponentsInChildren<Transform>(true))t.gameObject.layer=28;foreach(var t in visible.GetComponentsInChildren<Transform>(true))t.gameObject.layer=29;
                    var unlocked=KeyClip(a.CopyOf(f.First),c.RuntimeKey(),bundle,transient);var locked=KeyClip(a.CopyOf(f.First),new int[4],bundle,transient);var wrongValues=c.RuntimeKey();for(int k=0;k<4;k++)wrongValues[k]^=127;var wrong=KeyClip(a.CopyOf(f.First),wrongValues,bundle,transient);
                    foreach(float time in new[]{0f,.25f,.49f,.51f,.9f})for(int view=0;view<2;view++)
                    {
                        camera.transform.position=view==0?new Vector3(0,.4f,-3.2f):new Vector3(1.2f,.65f,-3.2f);camera.transform.LookAt(new Vector3(0,0,0));
                        Compare(camera,reference,visible,f.First,unlocked,locked,wrong,time,"context-"+i+"-time-"+time.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)+"-view-"+view);
                    }
                    Graph(reference,f,a,c,bundle,transient,false);Graph(visible,f,a,c,bundle,transient,true);
                    Check(Vector3.Distance(reference.transform.Find("Left").localPosition,visible.transform.Find("Left").localPosition)<1e-5f,"real nested BlendTree and override graph reproduces transforms "+i);
                    camera.cullingMask=1<<28;var original=Render(camera,output+"/context-"+i+"-graph-original.png");camera.cullingMask=1<<29;var decoded=Render(camera,output+"/context-"+i+"-graph-unlocked.png");
                    float diff=Difference(original,decoded);maxVisual=Mathf.Max(maxVisual,diff);Check(diff<=.0015f,"real Animator native Vulkan image matches source including texture ST and material swaps "+i+"; mean="+diff);
                }
                finally {if(visible)Object.DestroyImmediate(visible);if(reference)Object.DestroyImmediate(reference);foreach(var obj in transient)if(obj)Object.DestroyImmediate(obj);if(bundle)bundle.Unload(true);}
            }
            string windows=output+"/windows-bundles";Directory.CreateDirectory(windows);var windowsBuild=BuildPipeline.BuildAssetBundles(windows,builds,BuildAssetBundleOptions.StrictMode|BuildAssetBundleOptions.ForceRebuildAssetBundle,BuildTarget.StandaloneWindows64);
            Check(windowsBuild&&windowsBuild.GetAllAssetBundles().Length==2,"two Windows64 combined bundles compile; no D3D/client runtime claim");
            Check(artifacts.SelectMany(a=>a.ShaderAssetPaths).All(p=>!ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(p))),"all combined facades/providers compile without errors during Windows build");
            File.WriteAllText(output+"/resources.json",JsonUtility.ToJson(new Resources {generatedPayloads=16,generatedShaders=artifacts.Sum(a=>a.ShaderAssetPaths.Count),payloadBytes=16*64*64*4,
                sourcePixelBytes=2*64*64*(3+4),linuxBundleBytes=builds.Sum(b=>new FileInfo(linux+"/"+b.assetBundleName).Length),windowsBundleBytes=builds.Sum(b=>new FileInfo(windows+"/"+b.assetBundleName).Length),linuxBundleBuildSeconds=watch.Elapsed.TotalSeconds,
                texelLoadsPoint=1,texelLoadsBilinear=4,mipsSupported=false,compressionSupported=false,runtimeBenchmark=false},true));
        }
        finally {LAGDiversityVariantFilter.Enabled=false;PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,oldAuto);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,oldApis);Object.DestroyImmediate(cameraObject);}
    }
    [Serializable] sealed class Resources {public int generatedPayloads,generatedShaders,payloadBytes,sourcePixelBytes,texelLoadsPoint,texelLoadsBilinear;public long linuxBundleBytes,windowsBundleBytes;public double linuxBundleBuildSeconds;public bool mipsSupported,compressionSupported,runtimeBenchmark;}
    [Serializable] sealed class Report
    {
        public string[] checks;public string unity,graphics,gpu,colorSpace,scope;
        public int adaptiveCases,images,rejectedPrograms,textureBindings,rendererBindings;
        public float maxUnlockedMeanDifference,maxUnlockedChannelDifference,minLockedDifference,minWrongDifference;
        public bool sourcesPreserved=true,sourceShadersPreserved=true,vrchatAnalyzed=false,sdkProcessed=false,productionIntegrated=false;
        public Visual[] visuals;public double[] generationSeconds;
    }
    [Serializable] sealed class BenchmarkVariant {public string id,bundle,prefab;public string[] providers,shaders;}
    [Serializable] sealed class BenchmarkManifest
    {
        public string scope="owned-procedural-rigid-liltoon-fixture";
        public int[] publicFixtureUnlockValues;
        public string[] interleavedOrder={"original","mesh","texture"};
        public int expectedDrawsPerInstance=4,expectedTrianglesPerInstance=384;
        public BenchmarkVariant[] variants;
        public string description="Owned two-renderer/two-slot unlit opaque single-mip albedos; original, contextual mesh, contextual mesh+texture; all variants disable batching; RGBA32/Bilinear sRGB + RGB24/Point linear";
    }
    static BenchmarkVariant BenchmarkControl(Fixture f,GuardBuildContext context,bool encodeMesh,string directory,string buildOutput)
    {
        var animation=GuardAnimationContext.Capture(f.Root,true);var root=Object.Instantiate(f.Root);var shaderPaths=new List<string>();var providers=new List<string>();
        Object.DestroyImmediate(root.GetComponent<Animator>());Directory.CreateDirectory(directory);AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        try
        {
            foreach(var source in f.Root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var binding=MeshBindingIdentity.Capture(f.Root,source);string folder=directory+"/b_"+binding.StableId.Substring(0,16);Directory.CreateDirectory(folder);
                var target=root.transform.Find(AnimationUtility.CalculateTransformPath(source.transform,f.Root.transform)).GetComponent<MeshRenderer>();
                using(var codec=context.CreateContextualCodec(binding,animation))
                {
                    var mesh=source.GetComponent<MeshFilter>().sharedMesh;var plan=codec.Plan(mesh,.15f);
                    var fragment=encodeMesh?codec.EmitDecoder(plan):(DecoderFragment)Activator.CreateInstance(typeof(DecoderFragment),BindingFlags.Instance|BindingFlags.NonPublic,null,
                        new object[]{StaticPolymorphicCodecV1.Id,StaticPolymorphicCodecV1.Version,"",Array.Empty<string>()},null);
                    var shaders=new GuardShaders(folder,"OwnedTextureBenchmark/"+(encodeMesh?"mesh":"original")+"/"+binding.StableId.Substring(0,16),fragment);
                    if(encodeMesh){var encoded=codec.Encode(mesh,plan,context.RuntimeKey());AssetDatabase.CreateAsset(encoded,folder+"/mesh.asset");target.GetComponent<MeshFilter>().sharedMesh=encoded;}
                    var materials=new List<Material>();int slot=0;
                    foreach(var material in source.sharedMaterials)
                    {
                        var copy=new Material(material){shader=shaders.Copy(material.shader),name="OwnedBenchmarkMaterial"};
                        if(encodeMesh)for(int i=0;i<4;i++)copy.SetFloat(GuardShaders.Property(i),0);
                        AssetDatabase.CreateAsset(copy,folder+"/slot-"+(slot++)+".mat");materials.Add(copy);
                    }
                    target.sharedMaterials=materials.ToArray();shaderPaths.AddRange(shaders.GeneratedAssetPaths);providers.AddRange(shaders.ProviderAssetPaths);
                }
            }
            root.name="OwnedBenchmarkRoot";PrefabUtility.SaveAsPrefabAsset(root,directory+"/benchmark.prefab");Save(directory);
            return BuildBenchmarkBundle(encodeMesh?"mesh":"original",directory+"/benchmark.prefab",shaderPaths.ToArray(),providers.ToArray(),buildOutput,null);
        }
        finally{Object.DestroyImmediate(root);}
    }
    static BenchmarkVariant BuildBenchmarkBundle(string id,string prefab,string[] shaders,string[] providers,string buildOutput,string[] extraAssets)
    {
        string directory=buildOutput+"/bundles/"+id;Directory.CreateDirectory(directory);
        var assets=new[]{prefab}.Concat(shaders).Concat(extraAssets??Array.Empty<string>()).Distinct().ToArray();
        var built=BuildPipeline.BuildAssetBundles(directory,new[]{new AssetBundleBuild{assetBundleName=id+".bundle",assetNames=assets}},BuildAssetBundleOptions.StrictMode|BuildAssetBundleOptions.ForceRebuildAssetBundle,BuildTarget.StandaloneLinux64);
        Check(built&&shaders.All(p=>!ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(p))),"owned benchmark "+id+" bundle and all providers compile");
        return new BenchmarkVariant{id=id,bundle="bundles/"+id+"/"+id+".bundle",prefab=prefab.ToLowerInvariant(),providers=providers.Select(p=>p.ToLowerInvariant()).ToArray(),shaders=shaders.Select(p=>p.ToLowerInvariant()).ToArray()};
    }
    public static void BuildPerformanceFixtures()
    {
        LAGBindingValidation.RequireResearchProject();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        string directory=Environment.GetEnvironmentVariable("LAG_TEXTURE_BENCH_OUTPUT");if(string.IsNullOrEmpty(directory)||!Path.IsPathRooted(directory))throw new ArgumentException("Absolute owned benchmark output required.");
        Directory.CreateDirectory(directory);if(File.Exists(directory+"/fixtures.json"))throw new InvalidOperationException("Benchmark output already exists; preserve frozen fixtures.");
        if(AssetDatabase.IsValidFolder(Folder+"/benchmark"))AssetDatabase.DeleteAsset(Folder+"/benchmark");
        var oldApi=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneLinux64);bool oldAuto=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64);
        try
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,new[]{GraphicsDeviceType.Vulkan});LAGDiversityVariantFilter.Enabled=true;
            using(var f=CreateFixture("benchmark",false))using(var context=Context(f,"benchmark"))
            {
                var original=BenchmarkControl(f,context,false,NewOutput("bench-original"),directory);
                var mesh=BenchmarkControl(f,context,true,NewOutput("bench-mesh"),directory);
                using(var artifact=GuardShaderForge.PrepareWithTextures(f.Root,context,NewOutput("bench-texture")))
                {
                    Object.DestroyImmediate(artifact.Root.GetComponent<Animator>());PrefabUtility.SaveAsPrefabAsset(artifact.Root,artifact.Folder+"/benchmark.prefab");
                    var texture=BuildBenchmarkBundle("texture",artifact.Folder+"/benchmark.prefab",artifact.ShaderAssetPaths.ToArray(),artifact.ProviderAssetPaths.ToArray(),directory,Entries(artifact).SelectMany(t=>new[]{t.payload,t.material}).ToArray());
                    File.WriteAllText(directory+"/fixtures.json",JsonUtility.ToJson(new BenchmarkManifest{publicFixtureUnlockValues=context.RuntimeKey(),variants=new[]{original,mesh,texture}},true));
                }
            }
            Debug.Log("LAG_TEXTURE_BENCH_FIXTURES_SUCCESS");
        }
        finally {LAGDiversityVariantFilter.Enabled=false;PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,oldAuto);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,oldApi);}
    }
    public static void Run()
    {
        LAGBindingValidation.RequireResearchProject();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);checks.Clear();visuals.Clear();adaptiveCases=rejectedPrograms=0;maxVisual=maxChannel=0;minLocked=minWrong=float.PositiveInfinity;
        output="../evidence/texture-integration/"+QualitySettings.activeColorSpace.ToString().ToLowerInvariant();Directory.CreateDirectory(output);
        if(AssetDatabase.IsValidFolder(Folder))AssetDatabase.DeleteAsset(Folder);AssetDatabase.CreateFolder("Assets","TextureIntegrationFixture");
        string oldXdg=Environment.GetEnvironmentVariable("XDG_DATA_HOME"),scratch=Path.Combine(Path.GetTempPath(),"lag-texture-integration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);Environment.SetEnvironmentVariable("XDG_DATA_HOME",scratch);
        var fixtures=new List<Fixture>();var contexts=new List<GuardBuildContext>();var artifacts=new List<GuardShaderArtifact>();var times=new List<double>();
        var shaderFiles=Snapshot(Path.GetDirectoryName(AssetDatabase.GetAssetPath(Shader.Find("lilToon"))));
        try
        {
            var first=CreateFixture("generic",false);fixtures.Add(first);Contracts(first);fixtures.Add(CreateFixture("override",true));
            var sourceFiles=Snapshot(Folder);
            for(int i=0;i<fixtures.Count;i++)
            {
                var context=Context(fixtures[i],"native-"+i,i==0);contexts.Add(context);if(i==0)Check(context.RuntimeKey().Contains(0),"integrated build exercises an individual zero runtime key lane");
                var watch=Stopwatch.StartNew();var artifact=GuardShaderForge.PrepareWithTextures(fixtures[i].Root,context,NewOutput("native-"+i));watch.Stop();times.Add(watch.Elapsed.TotalSeconds);artifacts.Add(artifact);Structure(fixtures[i],context,artifact);
            }
            Native(fixtures.ToArray(),contexts.ToArray(),artifacts.ToArray());
            Check(Unchanged(sourceFiles),"positive integration/private replay leave every original asset/meta byte-identical");Check(Unchanged(shaderFiles),"installed lilToon shader/include sources remain byte-identical");
            File.WriteAllText(output+"/validation.json",JsonUtility.ToJson(new Report {checks=checks.ToArray(),unity=Application.unityVersion,graphics=SystemInfo.graphicsDeviceType.ToString(),gpu=SystemInfo.graphicsDeviceName,colorSpace=QualitySettings.activeColorSpace.ToString(),
                scope="Owned rigid generic Animator, nested BlendTree/override, opaque single-mip RGBA32/RGB24 albedo, animated ST; unlit mono; no avatar SDK processing/client analysis",
                adaptiveCases=adaptiveCases,images=Directory.GetFiles(output,"*.png").Length,rejectedPrograms=rejectedPrograms,textureBindings=16,rendererBindings=4,maxUnlockedMeanDifference=maxVisual,maxUnlockedChannelDifference=maxChannel,
                minLockedDifference=minLocked,minWrongDifference=minWrong,visuals=visuals.ToArray(),generationSeconds=times.ToArray()},true));
            Debug.Log("LAG_TEXTURE_INTEGRATION_SUCCESS "+checks.Count+" checks; "+QualitySettings.activeColorSpace+"; "+Directory.GetFiles(output,"*.png").Length+" images");
        }
        finally {foreach(var a in artifacts)a.Dispose();foreach(var c in contexts)c.Dispose();foreach(var f in fixtures)f.Dispose();Environment.SetEnvironmentVariable("XDG_DATA_HOME",oldXdg);if(Directory.Exists(scratch))Directory.Delete(scratch,true);}
    }
    public static void RunRegression()
    {
        LAGTextureValidation.Run();
        LAGTextureValidation.RunRegression();
        Debug.Log("LAG_TEXTURE_INTEGRATION_REGRESSION_SUCCESS");
    }
}
public sealed class LAGTextureIntegrationFault : AssetPostprocessor
{
    public static string Prefix,Mode;public static Material Source;public static bool Touched;
    static void OnPostprocessAllAssets(string[] imported,string[] deleted,string[] moved,string[] previous)
    {
        if(Mode==null||Touched||Environment.GetEnvironmentVariable("LAG_BINDING_AUDIT_ALLOWED")!="synthetic-unity-only")return;
        if(Mode=="key")
        {string path=imported.FirstOrDefault(p=>p.StartsWith(Prefix,StringComparison.Ordinal)&&p.EndsWith(".mat"));if(path!=null){Touched=true;var m=AssetDatabase.LoadAssetAtPath<Material>(path);m.SetFloat("_LAGKey0",17);EditorUtility.SetDirty(m);}}
        else if(Mode=="sampler")
        {string path=imported.FirstOrDefault(p=>p.StartsWith(Prefix,StringComparison.Ordinal)&&p.EndsWith("/payload.asset"));if(path!=null){Touched=true;var t=AssetDatabase.LoadAssetAtPath<Texture2D>(path);t.wrapModeU=TextureWrapMode.Mirror;EditorUtility.SetDirty(t);}}
        else if(Mode=="source"&&imported.Any(p=>p.StartsWith(Prefix,StringComparison.Ordinal)&&p.EndsWith("/payload.asset")))
        {Touched=true;Source.SetTextureOffset("_MainTex",new Vector2(.3f,.4f));EditorUtility.SetDirty(Source);}
        else if(Mode=="clip")
        {string path=imported.FirstOrDefault(p=>p.StartsWith(Prefix,StringComparison.Ordinal)&&p.EndsWith(".anim"));if(path!=null){Touched=true;var clip=AssetDatabase.LoadAssetAtPath<AnimationClip>(path);var b=AnimationUtility.GetObjectReferenceCurveBindings(clip)[0];var frames=AnimationUtility.GetObjectReferenceCurve(clip,b);frames[0].value=Source;AnimationUtility.SetObjectReferenceCurve(clip,b,frames);EditorUtility.SetDirty(clip);}}
    }
}
