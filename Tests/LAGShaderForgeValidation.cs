// SPDX-License-Identifier: MIT
// Owned procedural fixtures only; this runner never inspects VRChat or an imported avatar.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LinuxAvatarGuard;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

public static class LAGShaderForgeValidation
{
    const string Folder="Assets/ShaderForgeFixture";
    const string Output="../evidence/shader-forge";
    static readonly List<string> checks=new List<string>();
    static int rejectedPrograms;
    static float maxError,maxVisual,minLocked=float.PositiveInfinity,minCross=float.PositiveInfinity;
    static string Hash(byte[] b){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(b)).Replace("-","").ToLowerInvariant();}
    static byte[] Seed(string text){using(var h=SHA256.Create())return h.ComputeHash(Encoding.UTF8.GetBytes("LAG/owned-forge/v1/"+text));}
    static void Check(bool ok,string description){if(!ok)throw new Exception("SHADER_FORGE_VALIDATION_FAILED: "+description);checks.Add(description);}
    static bool Rejects(Action a)=>LAGBindingValidation.Rejects(a);
    static void Save(string folder)
    {
        foreach(string path in AssetDatabase.FindAssets("",new[]{folder}).Select(AssetDatabase.GUIDToAssetPath).Distinct())
        {foreach(var asset in AssetDatabase.LoadAllAssetsAtPath(path))if(asset)AssetDatabase.SaveAssetIfDirty(asset);}
    }
    sealed class Fixture : IDisposable
    {
        public GameObject Root;
        public AnimationClip First,Second;
        public AnimatorController Base;
        public AnimatorOverrideController Override;
        public Material A,B;
        public string Assets;
        public void Dispose(){if(Root)Object.DestroyImmediate(Root);}
    }
    static Mesh OwnedMesh(string path,bool hair)
    {
        var mesh=LAGBindingValidation.Fixture();var triangles=mesh.triangles;mesh.subMeshCount=2;
        mesh.SetTriangles(triangles.Take(triangles.Length/2).ToArray(),0);mesh.SetTriangles(triangles.Skip(triangles.Length/2).ToArray(),1);
        if(hair){mesh.vertices=mesh.vertices.Select(p=>new Vector3(p.x*.7f,p.y*.8f,p.z*.9f+.04f*p.x)).ToArray();mesh.RecalculateNormals();mesh.RecalculateTangents();mesh.RecalculateBounds();}
        for(int c=1;c<=3;c++)mesh.SetUVs(c,mesh.vertices.Select(p=>new Vector3(.2f+p.x*.1f,.3f+p.y*.1f,.4f+p.z*.1f)).ToList());
        mesh.colors=mesh.vertices.Select(p=>new Color(.6f,.7f+p.y*.1f,.8f,1)).ToArray();AssetDatabase.CreateAsset(mesh,path);
        File.WriteAllText(path+".meta",Regex.Replace(File.ReadAllText(path+".meta"),@"(?m)^guid: [0-9a-f]{32}$","guid: "+Hash(Seed(path)).Substring(0,32)));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);return AssetDatabase.LoadAssetAtPath<Mesh>(path);
    }
    static AnimationCurve Step(float a,float b)
    {
        var curve=new AnimationCurve(new Keyframe(0,a),new Keyframe(.5f,b),new Keyframe(1,a));
        for(int i=0;i<curve.length;i++)AnimationUtility.SetKeyRightTangentMode(curve,i,AnimationUtility.TangentMode.Constant);
        return curve;
    }
    static AnimationClip Clip(string path,Material a,Material b,bool inverted)
    {
        var clip=new AnimationClip {name=inverted?"OwnedReverse":"OwnedSwap",frameRate=60};
        foreach(string renderer in new[]{"Body","Hair"})for(int slot=0;slot<2;slot++)
        {
            var binding=EditorCurveBinding.PPtrCurve(renderer,typeof(MeshRenderer),"m_Materials.Array.data["+slot+"]");
            bool reverse=inverted^(slot==1);
            AnimationUtility.SetObjectReferenceCurve(clip,binding,new[]{new ObjectReferenceKeyframe{time=0,value=reverse?b:a},new ObjectReferenceKeyframe{time=.5f,value=reverse?a:b},new ObjectReferenceKeyframe{time=1,value=reverse?b:a}});
        }
        AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve("Body",typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Linear(0,-.72f,1,inverted?-.92f:-.52f));
        AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve("Hair",typeof(Transform),"m_LocalPosition.y"),AnimationCurve.Linear(0,.2f,1,inverted?-.15f:.4f));
        AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve("Hair",typeof(Transform),"m_LocalScale.x"),AnimationCurve.Linear(0,1,1,inverted?1.1f:.85f));
        foreach(string renderer in new[]{"Body","Hair"})
        {
            AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(renderer,typeof(MeshRenderer),"material._IDMaskFrom"),Step(inverted?5:4,inverted?4:5));
            AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(renderer,typeof(MeshRenderer),"material._Color.r"),AnimationCurve.Linear(0,inverted?.8f:.2f,1,inverted?.25f:.75f));
            foreach(var component in new[]{new KeyValuePair<string,float>("g",.65f),new KeyValuePair<string,float>("b",.9f),new KeyValuePair<string,float>("a",1)})
                AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(renderer,typeof(MeshRenderer),"material._Color."+component.Key),AnimationCurve.Constant(0,1,component.Value));
        }
        AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve("Hair",typeof(GameObject),"m_IsActive"),AnimationCurve.Constant(0,1,1));
        AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve("Body",typeof(MeshRenderer),"m_Enabled"),AnimationCurve.Constant(0,1,1));
        AssetDatabase.CreateAsset(clip,path);return clip;
    }
    static Fixture CreateFixture(string name)
    {
        string assets=Folder+"/"+name;Directory.CreateDirectory(assets);AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var f=new Fixture {Assets=assets,Root=new GameObject("OwnedShaderForgeRoot")};f.Root.SetActive(false);
        f.A=new Material(Shader.Find("lilToon"));f.B=new Material(Shader.Find("Hidden/lilToonCutout"));
        foreach(var mat in new[]{f.A,f.B}){mat.SetFloat("_AsUnlit",1);mat.SetFloat("_IDMaskFrom",4);mat.SetColor("_Color",new Color(.3f,.65f,.9f,1));mat.SetFloat("_Cull",0);}
        AssetDatabase.CreateAsset(f.A,assets+"/owned-a.mat");AssetDatabase.CreateAsset(f.B,assets+"/owned-b.mat");
        foreach(string part in new[]{"Body","Hair"})
        {
            var go=new GameObject(part);go.transform.SetParent(f.Root.transform,false);go.transform.localPosition=new Vector3(part=="Body"?-.72f:.72f,part=="Body"?0:.2f,0);
            go.AddComponent<MeshFilter>().sharedMesh=OwnedMesh(assets+"/"+part+".asset",part=="Hair");go.AddComponent<MeshRenderer>().sharedMaterials=new[]{f.A,f.A};
        }
        f.First=Clip(assets+"/first.anim",f.A,f.B,false);f.Second=Clip(assets+"/second.anim",f.A,f.B,true);
        f.Base=AnimatorController.CreateAnimatorControllerAtPath(assets+"/owned.controller");f.Base.AddParameter("Blend",AnimatorControllerParameterType.Float);
        var state=f.Base.layers[0].stateMachine.AddState("OwnedState");f.Base.layers[0].stateMachine.defaultState=state;
        var inner=new BlendTree{name="OwnedInner",blendType=BlendTreeType.Simple1D,blendParameter="Blend",useAutomaticThresholds=false};
        inner.AddChild(f.First,0);inner.AddChild(f.Second,1);AssetDatabase.AddObjectToAsset(inner,f.Base);
        var outer=new BlendTree{name="OwnedOuter",blendType=BlendTreeType.Simple1D,blendParameter="Blend",useAutomaticThresholds=false};
        outer.AddChild(inner,0);outer.AddChild(f.First,1);AssetDatabase.AddObjectToAsset(outer,f.Base);state.motion=outer;EditorUtility.SetDirty(f.Base);EditorUtility.SetDirty(state);
        f.Override=new AnimatorOverrideController(f.Base);f.Override.ApplyOverrides(new[]{new KeyValuePair<AnimationClip,AnimationClip>(f.First,f.Second),new KeyValuePair<AnimationClip,AnimationClip>(f.Second,f.First)});
        AssetDatabase.CreateAsset(f.Override,assets+"/owned.overrideController");var animator=f.Root.AddComponent<Animator>();animator.enabled=false;animator.runtimeAnimatorController=f.Base;Save(assets);
        return f;
    }
    static GuardBuildContext Context(Fixture f,string scope)
    {
        var animation=GuardAnimationContext.Capture(f.Root);
        for(int i=0;i<256;i++)
        {
            byte[] seed=Seed(scope+"-"+i);var c=GuardBuildContext.CreateReproducible(Hash(seed).Substring(0,32),seed);
            try
            {
                foreach(var r in f.Root.GetComponentsInChildren<MeshRenderer>(true))using(var codec=c.CreateContextualCodec(MeshBindingIdentity.Capture(f.Root,r),animation))
                {var plan=codec.Plan(r.GetComponent<MeshFilter>().sharedMesh,.15f);var encoded=codec.Encode(r.GetComponent<MeshFilter>().sharedMesh,plan,c.RuntimeKey());Object.DestroyImmediate(encoded);}
                return c;
            }
            catch(InvalidOperationException e)when(e.Message.Contains("cancela sus offsets keyed")||e.Message.Contains("no supera el error de reconstrucción sin clave")) {c.Dispose();rejectedPrograms++;}
            catch{c.Dispose();throw;}
        }
        throw new Exception("No accepted owned contextual program");
    }
    static Dictionary<string,string> Snapshot(string folder)
    {return Directory.GetFiles(folder,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>Hash(File.ReadAllBytes(p)));}
    static void NegativeChecks(Fixture f)
    {
        var original=f.Root.GetComponent<Animator>().runtimeAnimatorController;
        var captured=GuardAnimationContext.Capture(f.Root);var binding=MeshBindingIdentity.Capture(f.Root,f.Root.transform.Find("Body").GetComponent<Renderer>());
        using(var context=Context(f,"negative"))using(var codec=context.CreateContextualCodec(binding,captured))
        {
            var plan=codec.Plan(bindingRendererMesh(f),.15f);
            AnimationUtility.SetEditorCurve(f.First,EditorCurveBinding.FloatCurve("Body",typeof(MeshRenderer),"material._Color.r"),AnimationCurve.Linear(0,.4f,1,.5f));
            Check(Rejects(()=>codec.Validate(bindingRendererMesh(f),plan)),"dirty clip invalidates a captured program");Save(f.Assets);
            Check(Rejects(()=>codec.Validate(bindingRendererMesh(f),plan)),"saved clip change invalidates animation fingerprint");
        }
        Action<string,Action,Action> reject=(name,change,restore)=>
        {try{change();Check(Rejects(()=>GuardAnimationContext.Capture(f.Root)),name);}finally{restore();Save(f.Assets);}};
        var id=EditorCurveBinding.FloatCurve("Body",typeof(MeshRenderer),"material._IDMaskFrom");
        reject("continuous animated ID selector is rejected",()=>{AnimationUtility.SetEditorCurve(f.First,id,AnimationCurve.Linear(0,4,1,5));Save(f.Assets);},()=>AnimationUtility.SetEditorCurve(f.First,id,Step(4,5)));
        reject("out-of-range animated ID selector is rejected",()=>{AnimationUtility.SetEditorCurve(f.First,id,Step(4,9));Save(f.Assets);},()=>AnimationUtility.SetEditorCurve(f.First,id,Step(4,5)));
        reject("runtime key input animation is rejected",()=>{AnimationUtility.SetEditorCurve(f.First,EditorCurveBinding.FloatCurve("Body",typeof(MeshRenderer),"material._LAGKey0"),AnimationCurve.Constant(0,1,0));Save(f.Assets);},()=>AnimationUtility.SetEditorCurve(f.First,EditorCurveBinding.FloatCurve("Body",typeof(MeshRenderer),"material._LAGKey0"),null));
        reject("animation events are rejected",()=>{AnimationUtility.SetAnimationEvents(f.First,new[]{new AnimationEvent{time=.2f,functionName="Unreviewed"}});Save(f.Assets);},()=>AnimationUtility.SetAnimationEvents(f.First,Array.Empty<AnimationEvent>()));
        reject("texture property float animation is rejected",()=>{AnimationUtility.SetEditorCurve(f.First,EditorCurveBinding.FloatCurve("Body",typeof(MeshRenderer),"material._MainTex"),AnimationCurve.Constant(0,1,0));Save(f.Assets);},()=>AnimationUtility.SetEditorCurve(f.First,EditorCurveBinding.FloatCurve("Body",typeof(MeshRenderer),"material._MainTex"),null));
        reject("missing transform paths are rejected",()=>{AnimationUtility.SetEditorCurve(f.First,EditorCurveBinding.FloatCurve("Missing",typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,0));Save(f.Assets);},()=>AnimationUtility.SetEditorCurve(f.First,EditorCurveBinding.FloatCurve("Missing",typeof(Transform),"m_LocalPosition.x"),null));
        var swap=EditorCurveBinding.PPtrCurve("Body",typeof(MeshRenderer),"m_Materials.Array.data[0]");var originalFrames=AnimationUtility.GetObjectReferenceCurve(f.First,swap);
        reject("null material swaps are rejected",()=>{AnimationUtility.SetObjectReferenceCurve(f.First,swap,new[]{new ObjectReferenceKeyframe{time=0,value=null}});Save(f.Assets);},()=>AnimationUtility.SetObjectReferenceCurve(f.First,swap,originalFrames));
        var outside=EditorCurveBinding.PPtrCurve("Body",typeof(MeshRenderer),"m_Materials.Array.data[9]");
        reject("out-of-range material slots are rejected",()=>{AnimationUtility.SetObjectReferenceCurve(f.First,outside,originalFrames);Save(f.Assets);},()=>AnimationUtility.SetObjectReferenceCurve(f.First,outside,null));
        var meshSwap=EditorCurveBinding.PPtrCurve("Body",typeof(MeshFilter),"m_Mesh");
        reject("animated mesh swaps are rejected",()=>{AnimationUtility.SetObjectReferenceCurve(f.First,meshSwap,new[]{new ObjectReferenceKeyframe{time=0,value=bindingRendererMesh(f)}});Save(f.Assets);},()=>AnimationUtility.SetObjectReferenceCurve(f.First,meshSwap,null));
        var layers=f.Base.layers;var savedMode=layers[0].blendingMode;
        reject("additive controller layers are rejected",()=>{layers[0].blendingMode=AnimatorLayerBlendingMode.Additive;f.Base.layers=layers;Save(f.Assets);},()=>{layers[0].blendingMode=savedMode;f.Base.layers=layers;});
        var external=new BlendTree{name="OwnedExternal",blendType=BlendTreeType.Direct};AssetDatabase.CreateAsset(external,f.Assets+"/external.asset");var state=f.Base.layers[0].stateMachine.defaultState;var motion=state.motion;
        reject("external direct BlendTrees are rejected",()=>{state.motion=external;EditorUtility.SetDirty(state);Save(f.Assets);},()=>{state.motion=motion;EditorUtility.SetDirty(state);});
        var duplicate=new GameObject("Body");duplicate.transform.SetParent(f.Root.transform,false);
        Check(Rejects(()=>GuardAnimationContext.Capture(f.Root)),"ambiguous child paths are rejected");Object.DestroyImmediate(duplicate);
        var nested=f.Root.transform.Find("Hair").gameObject.AddComponent<Animator>();Check(Rejects(()=>GuardAnimationContext.Capture(f.Root)),"nested Animator is rejected");Object.DestroyImmediate(nested);
        f.A.SetFloat("_IDMaskFrom",8);EditorUtility.SetDirty(f.A);Save(f.Assets);
        string output="Assets/LinuxAvatarGuardGenerated/Research/negative-blend";
        if(AssetDatabase.IsValidFolder(output))AssetDatabase.DeleteAsset(output);
        using(var c=GuardBuildContext.CreateRandom())Check(Rejects(()=>GuardShaderForge.Prepare(f.Root,c,output))&&!Directory.Exists(output)&&c.BindingCount==0,"ID Mask blending across base 8 and animated 4–5 leaves no two carriers; cancels before any output/records");
        f.A.SetFloat("_IDMaskFrom",4);EditorUtility.SetDirty(f.A);Save(f.Assets);
        var animator=f.Root.GetComponent<Animator>();animator.runtimeAnimatorController=original;
        var probe=f.Root.AddComponent<LAGShaderForgeProbe>();using(var c=GuardBuildContext.CreateRandom())Check(Rejects(()=>GuardShaderForge.Prepare(f.Root,c,output))&&!Directory.Exists(output),"custom components cancel before any generated folder");Object.DestroyImmediate(probe);
        using(var c=GuardBuildContext.CreateRandom())Check(Rejects(()=>GuardShaderForge.Prepare(f.Root,c,"Assets/../outside")),"output traversal is rejected");
        using(var c=Context(f,"rollback"))
        {
            string late="Assets/LinuxAvatarGuardGenerated/Research/owned-late-failure";if(AssetDatabase.IsValidFolder(late))AssetDatabase.DeleteAsset(late);
            var before=Snapshot(f.Assets);GuardShaderArtifact result=null;
            LAGShaderForgeFaultPostprocessor.Root=f.Root;LAGShaderForgeFaultPostprocessor.Prefix=late+"/";LAGShaderForgeFaultPostprocessor.Touched=false;
            try
            {
                Check(Rejects(()=>result=GuardShaderForge.Prepare(f.Root,c,late))&&LAGShaderForgeFaultPostprocessor.Touched&&!Directory.Exists(late)&&c.BindingCount==0,"late import mutation rolls back generated folder and leaves private records uncommitted");
                Check(before.All(p=>Hash(File.ReadAllBytes(p.Key))==p.Value),"late rollback leaves every source file byte identical");
            }
            finally
            {
                LAGShaderForgeFaultPostprocessor.Root=null;result?.Dispose();
                var added=f.Root.GetComponent<LAGShaderForgeProbe>();if(added)Object.DestroyImmediate(added);
            }
        }
    }
    static Mesh bindingRendererMesh(Fixture f)=>f.Root.transform.Find("Body").GetComponent<MeshFilter>().sharedMesh;
    static void Structure(Fixture f,GuardBuildContext c,GuardShaderArtifact a)
    {
        var generated=a.Root.GetComponentsInChildren<MeshRenderer>(true);var source=f.Root.GetComponentsInChildren<MeshRenderer>(true);
        Check(generated[0].sharedMaterial!=generated[1].sharedMaterial&&generated[0].sharedMaterial.shader!=generated[1].sharedMaterial.shader,"shared source material has separate copies and shaders per renderer");
        Check(generated.All(r=>r.sharedMaterials[0]!=r.sharedMaterials[1]),"same material in different slots has distinct contextual copies");
        Check(generated.All(r=>r.sharedMaterials.All(m=>Enumerable.Range(0,4).All(i=>m.GetFloat(GuardShaders.Property(i))==0))),"all saved material keys are zero");
        Check(a.ProviderAssetPaths.Count>0&&a.ShaderAssetPaths.All(p=>Path.GetFileName(p).StartsWith("s_")),"opaque shader filenames include explicitly tracked UsePass providers");
        Check(a.ShaderAssetPaths.All(p=>{var s=AssetDatabase.LoadAssetAtPath<Shader>(p);return s&&s.isSupported&&!ShaderUtil.ShaderHasError(s)&&!s.name.Contains("Body")&&!s.name.Contains("Hair")&&!s.name.Contains("lilToon");}),"all generated shaders compile with per-context opaque names");
        Check(generated.All(r=>r.sharedMaterials.All(m=>string.Equals(m.GetTag("DisableBatching",false,""),"True",StringComparison.OrdinalIgnoreCase))),"generated nonlinear shaders disable dynamic batching");
        var deps=AssetDatabase.GetDependencies(a.PrefabPath,true);
        Check(deps.All(p=>!p.StartsWith(f.Assets+"/",StringComparison.Ordinal)),"generated prefab excludes source meshes, materials, clips and controllers");
        foreach(var original in new[]{f.First,f.Second})
        {
            var copy=a.CopyOf(original);Check(copy!=original&&AssetDatabase.GetAssetPath(copy).StartsWith(a.Folder+"/"),"clip is copied once within its artifact context");
            Check(AnimationUtility.GetCurveBindings(original).All(b=>AnimationUtility.GetEditorCurve(original,b).keys.SequenceEqual(AnimationUtility.GetEditorCurve(copy,b).keys)),"float/transform/toggle/ID curves preserve exact keys");
            foreach(var binding in AnimationUtility.GetObjectReferenceCurveBindings(original))
            {
                var input=AnimationUtility.GetObjectReferenceCurve(original,binding);var output=AnimationUtility.GetObjectReferenceCurve(copy,binding);
                Check(input.Select(k=>k.time).SequenceEqual(output.Select(k=>k.time))&&output.All(k=>k.value is Material m&&AssetDatabase.GetAssetPath(m).StartsWith(a.Folder+"/")),"material swap times survive and every frame targets generated material "+binding.path+"/"+binding.propertyName);
            }
        }
        var ctrl=a.Root.GetComponent<Animator>().runtimeAnimatorController;
        Check(ctrl!=f.Root.GetComponent<Animator>().runtimeAnimatorController&&AssetDatabase.GetAssetPath(ctrl).StartsWith(a.Folder+"/"),"Animator controller graph is copied");
        Check(ctrl.animationClips.All(clip=>AssetDatabase.GetAssetPath(clip).StartsWith(a.Folder+"/")),"nested BlendTree/override graph contains only generated clips; "+string.Join(",",ctrl.animationClips.Select(clip=>AssetDatabase.GetAssetPath(clip))));
        string publicText=File.ReadAllText(a.PublicManifestPath);
        Check(!publicText.Contains("masterSeed")&&!publicText.Contains("runtimeKey")&&publicText.Contains("\"attributePolicy\": 2")&&publicText.Contains("\"sdkProcessed\": false"),"public manifest binds contextual policy and declares research limits without secrets");
        string privatePath=c.SavePrivate(),privateText=File.ReadAllText(privatePath);var animation=GuardAnimationContext.Capture(f.Root);
        try{File.WriteAllText(privatePath,privateText.Replace("\"schemaVersion\": "+GuardBuildContext.SchemaVersion,"\"schemaVersion\": 2"));Check(Rejects(()=>GuardBuildContext.LoadPrivate(c.BuildId)),"contextual policy cannot be downgraded into private schema 2");}
        finally{File.WriteAllText(privatePath,privateText);}
        try
        {
            File.WriteAllText(privatePath,privateText.Replace("\"schemaVersion\": "+GuardBuildContext.SchemaVersion,"\"schemaVersion\": 3"));
            using(var historical=GuardBuildContext.LoadPrivate(c.BuildId))Check(historical.TextureBindingCount==0&&historical.BindingCount==c.BindingCount,"historical mesh-only schema 3 remains readable without texture records");
        }
        finally{File.WriteAllText(privatePath,privateText);}
        using(var restored=GuardBuildContext.LoadPrivate(c.BuildId))
        {
            Check(Rejects(()=>restored.CreateCodec(MeshBindingIdentity.Capture(f.Root,source[0]))),"contextual private record rejects replay without animation analysis");
            for(int r=0;r<source.Length;r++)using(var codec=restored.CreateContextualCodec(MeshBindingIdentity.Capture(f.Root,source[r]),animation))
            {
                var mesh=source[r].GetComponent<MeshFilter>().sharedMesh;var plan=codec.Plan(mesh,.15f);var encoded=codec.Encode(mesh,plan,restored.RuntimeKey());
                try{Check(MeshBindingIdentity.ContentFingerprint(encoded)==MeshBindingIdentity.ContentFingerprint(generated[r].GetComponent<MeshFilter>().sharedMesh),"private schema 3 exactly reproduces contextual encoding "+r);Check(new[]{plan.Program.Attributes.FirstUvChannel,plan.Program.Attributes.SecondUvChannel}.OrderBy(x=>x).SequenceEqual(new[]{6,7}),"animated ID range 4–5 reserves both channels "+r);}
                finally{Object.DestroyImmediate(encoded);}
            }
        }
        for(int r=0;r<generated.Length;r++)
        {
            var mesh=generated[r].GetComponent<MeshFilter>().sharedMesh;var shader=generated[r].sharedMaterial.shader;var parsed=new LAGAdaptiveShaderDecoder(File.ReadAllText(AssetDatabase.GetAssetPath(shader)));
            float error=LAGAdaptiveShaderDecoder.MaxError(parsed.Decode(mesh,c.RuntimeKey()),source[r].GetComponent<MeshFilter>().sharedMesh.vertices);maxError=Mathf.Max(maxError,error);
            Check(error<1e-5f,"adaptive public-HLSL parser reconstructs binding "+r+" (observed limitation)");
            var other=new LAGAdaptiveShaderDecoder(File.ReadAllText(AssetDatabase.GetAssetPath(generated[1-r].sharedMaterial.shader)));
            float cross=LAGAdaptiveShaderDecoder.Rms(other.Decode(mesh,c.RuntimeKey()),source[r].GetComponent<MeshFilter>().sharedMesh.vertices);minCross=Mathf.Min(minCross,cross);Check(cross>.01f,"other renderer decoder cannot reconstruct this binding "+r);
        }
        string marker=a.Folder+"/do-not-overwrite.txt";File.WriteAllText(marker,"Owned marker");
        Check(Rejects(()=>GuardShaderForge.Prepare(f.Root,c,a.Folder))&&File.ReadAllText(marker)=="Owned marker","existing output is rejected without deleting its contents");
    }
    static void Clear(GameObject root)
    {foreach(var r in root.GetComponentsInChildren<MeshRenderer>(true)){r.SetPropertyBlock(null);for(int s=0;s<r.sharedMaterials.Length;s++)r.SetPropertyBlock(null,s);}}
    static AnimationClip KeyClip(AnimationClip source,int[] key,AssetBundle bundle,List<Object> transient)
    {
        // Test-only key curves share Unity's animation property sheet with the original color/ID curves.
        // These transient clips never enter the generated prefab, a saved asset, a bundle or a report.
        var clip=Object.Instantiate(source);clip.hideFlags=HideFlags.DontSave;transient.Add(clip);
        foreach(var binding in AnimationUtility.GetObjectReferenceCurveBindings(source))
        {
            var frames=AnimationUtility.GetObjectReferenceCurve(source,binding);
            for(int i=0;i<frames.Length;i++)
            {string path=AssetDatabase.GetAssetPath(frames[i].value);frames[i].value=bundle.LoadAsset<Material>(path.ToLowerInvariant());if(!frames[i].value)throw new Exception("Native material missing "+path);}
            AnimationUtility.SetObjectReferenceCurve(clip,binding,frames);
        }
        foreach(string path in AnimationUtility.GetObjectReferenceCurveBindings(source).Select(b=>b.path).Distinct())
            for(int i=0;i<4;i++)AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(path,typeof(MeshRenderer),"material."+GuardShaders.Property(i)),AnimationCurve.Constant(0,1,key[i]));
        return clip;
    }
    static void KeyController(GameObject root,int[] key,Dictionary<string,AnimationClip> templates,AssetBundle bundle,List<Object> transient)
    {
        var animator=root.GetComponent<Animator>();var original=animator.runtimeAnimatorController;
        var input=new List<KeyValuePair<AnimationClip,AnimationClip>>();
        if(original is AnimatorOverrideController overrides){overrides.GetOverrides(input);original=overrides.runtimeAnimatorController;}
        else input.AddRange(original.animationClips.Select(c=>new KeyValuePair<AnimationClip,AnimationClip>(c,c)));
        var copy=new AnimatorOverrideController(original){hideFlags=HideFlags.DontSave};transient.Add(copy);
        copy.ApplyOverrides(input.Select(p=>new KeyValuePair<AnimationClip,AnimationClip>(p.Key,KeyClip(templates[(p.Value?p.Value:p.Key).name],key,bundle,transient))).ToArray());
        animator.runtimeAnimatorController=copy;
    }
    static Color[] Render(Camera camera,string path)
    {
        var rt=new RenderTexture(384,256,24,RenderTextureFormat.ARGB32);var tex=new Texture2D(384,256,TextureFormat.RGBA32,false);var old=RenderTexture.active;
        try{camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,384,256),0,0);tex.Apply();if(path!=null)File.WriteAllBytes(path,tex.EncodeToPNG());return tex.GetPixels();}
        finally{RenderTexture.active=old;camera.targetTexture=null;Object.DestroyImmediate(tex);Object.DestroyImmediate(rt);}
    }
    static float Difference(Color[] a,Color[] b)=>a.Zip(b,(x,y)=>Mathf.Abs(x.r-y.r)+Mathf.Abs(x.g-y.g)+Mathf.Abs(x.b-y.b)).Average()/3;
    static void Native(Fixture[] fixtures,GuardBuildContext[] contexts,GuardShaderArtifact[] artifacts)
    {
        var builds=artifacts.Select((a,i)=>new AssetBundleBuild {assetBundleName="context-"+i+".bundle",assetNames=new[]{a.PrefabPath,a.PublicManifestPath}.Concat(a.ShaderAssetPaths).Concat(AssetDatabase.FindAssets("t:Material",new[]{a.Folder}).Select(AssetDatabase.GUIDToAssetPath)).Concat(new[]{AssetDatabase.GetAssetPath(a.CopyOf(fixtures[i].First)),AssetDatabase.GetAssetPath(a.CopyOf(fixtures[i].Second)),AssetDatabase.GetAssetPath(a.Root.GetComponent<Animator>().runtimeAnimatorController)}).Distinct().ToArray()}).ToArray();
        string output=Output+"/linux-bundles";Directory.CreateDirectory(output);
        var oldApi=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneLinux64);bool oldAuto=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64);
        var cameraObject=new GameObject("OwnedForgeCamera");var camera=cameraObject.AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.04f,.05f,.06f);camera.orthographic=true;camera.orthographicSize=1.05f;camera.nearClipPlane=.01f;camera.farClipPlane=20;
        foreach(var a in artifacts)a.Root.SetActive(false);foreach(var f in fixtures)f.Root.SetActive(false);
        try
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,new[]{GraphicsDeviceType.Vulkan});LAGDiversityVariantFilter.Enabled=true;
            var manifest=BuildPipeline.BuildAssetBundles(output,builds,BuildAssetBundleOptions.ForceRebuildAssetBundle|BuildAssetBundleOptions.StrictMode,BuildTarget.StandaloneLinux64);
            Check(manifest&&manifest.GetAllAssetBundles().Length==2,"two native Linux Vulkan contextual bundles build successfully");
            for(int i=0;i<artifacts.Length;i++)
            {
                var a=artifacts[i];var f=fixtures[i];var bundle=AssetBundle.LoadFromFile(Path.GetFullPath(output+"/context-"+i+".bundle"));GameObject visible=null,reference=null;var transient=new List<Object>();
                try
                {
                    Check(bundle,"native contextual bundle loads "+i);
                    foreach(var path in a.ProviderAssetPaths)Check(bundle.LoadAsset<Shader>(path.ToLowerInvariant()),"native UsePass provider loads before facade "+i+"/"+Path.GetFileName(path));
                    foreach(var path in a.ShaderAssetPaths.Except(a.ProviderAssetPaths)){var shader=bundle.LoadAsset<Shader>(path.ToLowerInvariant());Check(shader&&shader.isSupported&&!ShaderUtil.ShaderHasError(shader),"native facade compiles and is supported "+i+"/"+Path.GetFileName(path));}
                    visible=Object.Instantiate(bundle.LoadAsset<GameObject>(a.PrefabPath.ToLowerInvariant()));reference=Object.Instantiate(f.Root);visible.SetActive(true);reference.SetActive(true);
                    foreach(var t in reference.GetComponentsInChildren<Transform>(true))t.gameObject.layer=28;
                    foreach(var t in visible.GetComponentsInChildren<Transform>(true))t.gameObject.layer=29;
                    var clip=bundle.LoadAsset<AnimationClip>(AssetDatabase.GetAssetPath(a.CopyOf(f.First)).ToLowerInvariant());Check(clip,"native contextual clip loads "+i);
                    // Bundles strip editor curve metadata. The editable generated template is copied,
                    // then every material reference is rebound to the actual native bundle object.
                    var templates=new[]{a.CopyOf(f.First),a.CopyOf(f.Second)}.ToDictionary(c=>c.name,c=>c);
                    var unlockedClip=KeyClip(a.CopyOf(f.First),contexts[i].RuntimeKey(),bundle,transient);var lockedClip=KeyClip(a.CopyOf(f.First),new int[4],bundle,transient);
                    for(int t=0;t<5;t++)
                    {
                        float time=new[]{0,.25f,.49f,.51f,.9f}[t];Clear(reference);Clear(visible);reference.SetActive(true);visible.SetActive(true);
                        f.First.SampleAnimation(reference,time);unlockedClip.SampleAnimation(visible,time);
                        Check(visible.GetComponentsInChildren<MeshRenderer>(true).All(r=>r.sharedMaterials.All(m=>m.shader.name.StartsWith("LinuxAvatarGuard/"))),"material swaps retain generated shaders in bundle "+i+" at "+time);
                        Check(Vector3.Distance(reference.transform.Find("Body").localPosition,visible.transform.Find("Body").localPosition)<1e-6f&&Vector3.Distance(reference.transform.Find("Hair").localScale,visible.transform.Find("Hair").localScale)<1e-6f,"sampled transform animation matches original "+i+" at "+time);
                        for(int view=0;view<2;view++)
                        {
                            camera.transform.position=view==0?new Vector3(-.8f,.65f,-3.2f):new Vector3(1.3f,.85f,-3.2f);camera.transform.LookAt(new Vector3(0,.2f,0));
                            string prefix=Output+"/context-"+i+"-time-"+t+"-view-"+view;
                            camera.cullingMask=1<<28;var original=Render(camera,prefix+"-original.png");camera.cullingMask=1<<29;unlockedClip.SampleAnimation(visible,time);var unlocked=Render(camera,prefix+"-unlocked.png");lockedClip.SampleAnimation(visible,time);var locked=Render(camera,prefix+"-locked.png");
                            float diff=Difference(original,unlocked),lockedDiff=Difference(original,locked);maxVisual=Mathf.Max(maxVisual,diff);minLocked=Mathf.Min(minLocked,lockedDiff);
                            Check(diff<.002f&&lockedDiff>.003f,"Vulkan original/unlocked/locked rendering matches contract "+i+" at "+time+" view "+view+"; diff="+diff+", locked="+lockedDiff);
                            clip.SampleAnimation(visible,time);var raw=Render(camera,null);
                            Check(Difference(raw,locked)<.0001f,"unmodified native bundle clip reproduces locked template curves "+i+" at "+time+" view "+view);
                        }
                    }
                    // Evaluate the real copied Animator graph, including override controllers, not just clip sampling.
                    Clear(reference);Clear(visible);reference.SetActive(true);visible.SetActive(true);
                    KeyController(visible,contexts[i].RuntimeKey(),templates,bundle,transient);
                    foreach(var root in new[]{reference,visible}){var animator=root.GetComponent<Animator>();animator.enabled=true;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.Rebind();animator.Update(0);animator.SetFloat("Blend",.35f);animator.Update(.3f);}
                    Check(new[]{reference,visible}.All(root=>root.GetComponent<Animator>().isInitialized&&root.GetComponent<Animator>().GetCurrentAnimatorStateInfo(0).normalizedTime>.25f),"both Animator graphs initialize and advance beyond a static sampled pose "+i);
                    float expectedX=-.72f+.3f*.2f*.545f*(i==0?1:-1);
                    Check(Mathf.Abs(reference.transform.Find("Body").localPosition.x-expectedX)<1e-4f&&Mathf.Abs(visible.transform.Find("Body").localPosition.x-expectedX)<1e-4f,"nested blend weights and reversed overrides produce the analytically expected pose "+i);
                    Check(Vector3.Distance(reference.transform.Find("Body").localPosition,visible.transform.Find("Body").localPosition)<1e-5f&&Vector3.Distance(reference.transform.Find("Hair").localPosition,visible.transform.Find("Hair").localPosition)<1e-5f,"real Animator/BlendTree graph evaluation matches source "+i);
                    Check(visible.GetComponentsInChildren<MeshRenderer>(true).All(r=>r.sharedMaterials.All(m=>m.shader.name.StartsWith("LinuxAvatarGuard/"))),"real Animator keeps contextual shaders after material switches "+i);
                    camera.cullingMask=1<<28;var animatorOriginal=Render(camera,Output+"/context-"+i+"-animator-original.png");camera.cullingMask=1<<29;var animatorUnlocked=Render(camera,Output+"/context-"+i+"-animator-unlocked.png");
                    float animatorDiff=Difference(animatorOriginal,animatorUnlocked);maxVisual=Mathf.Max(maxVisual,animatorDiff);Check(animatorDiff<.002f,"real Animator native Vulkan image matches source "+i+"; diff="+animatorDiff);
                }
                finally{if(visible)Object.DestroyImmediate(visible);if(reference)Object.DestroyImmediate(reference);foreach(var item in transient)if(item)Object.DestroyImmediate(item);if(bundle)bundle.Unload(true);}
            }
            string windowsOutput=Output+"/windows-bundles";Directory.CreateDirectory(windowsOutput);
            var windows=BuildPipeline.BuildAssetBundles(windowsOutput,builds,BuildAssetBundleOptions.ForceRebuildAssetBundle|BuildAssetBundleOptions.StrictMode,BuildTarget.StandaloneWindows64);
            Check(windows&&windows.GetAllAssetBundles().Length==2,"two Windows64 contextual bundles compile for D3D; runtime execution not claimed");
        }
        finally{LAGDiversityVariantFilter.Enabled=false;PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,oldAuto);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,oldApi);Object.DestroyImmediate(cameraObject);}
    }
    [Serializable] sealed class Report
    {
        public string[] checks;public string unity,graphics,scope;
        public int contexts,rendererBindings,linuxBundles,windowsBundles,images,generatedShaders,rejectedPrograms;
        public float maxAdaptiveError,maxUnlockedImageDifference,minLockedImageDifference,minCrossDecoderRms;
        public bool originalFilesUnchanged,sourceShadersUnchanged,finalSdkAvatarTested;
    }
    public static void Run()
    {
        LAGBindingValidation.RequireResearchProject();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);checks.Clear();rejectedPrograms=0;maxError=0;maxVisual=0;minLocked=float.PositiveInfinity;minCross=float.PositiveInfinity;
        if(AssetDatabase.IsValidFolder(Folder))AssetDatabase.DeleteAsset(Folder);AssetDatabase.CreateFolder("Assets","ShaderForgeFixture");Directory.CreateDirectory(Output);
        string old=Environment.GetEnvironmentVariable("XDG_DATA_HOME"),scratch=Path.Combine(Path.GetTempPath(),"lag-owned-forge-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);Environment.SetEnvironmentVariable("XDG_DATA_HOME",scratch);
        var fixtures=new List<Fixture>();var contexts=new List<GuardBuildContext>();var artifacts=new List<GuardShaderArtifact>();
        try
        {
            using(var negative=CreateFixture("negative"))NegativeChecks(negative);
            var first=CreateFixture("source");fixtures.Add(first);var sourceFiles=Snapshot(first.Assets);
            var second=new Fixture{Assets=first.Assets,Root=Object.Instantiate(first.Root),First=first.First,Second=first.Second,Base=first.Base,Override=first.Override,A=first.A,B=first.B};second.Root.GetComponent<Animator>().runtimeAnimatorController=first.Override;
            // Reset only this owned fixture's editor preview state; input MPBs remain rejected by the package.
            Clear(second.Root);foreach(var r in second.Root.GetComponentsInChildren<MeshRenderer>(true))r.sharedMaterials=new[]{first.A,first.A};fixtures.Add(second);
            string shaderDirectory=Path.GetDirectoryName(AssetDatabase.GetAssetPath(Shader.Find("lilToon")));var shaders=Snapshot(shaderDirectory);
            for(int i=0;i<fixtures.Count;i++)
            {
                string output="Assets/LinuxAvatarGuardGenerated/Research/owned-context-"+i;if(AssetDatabase.IsValidFolder(output))AssetDatabase.DeleteAsset(output);
                var context=Context(fixtures[i],"native-"+i);contexts.Add(context);var artifact=GuardShaderForge.Prepare(fixtures[i].Root,context,output);artifacts.Add(artifact);Structure(fixtures[i],context,artifact);
            }
            Check(artifacts[0].CopyOf(first.First)!=artifacts[1].CopyOf(first.First),"same source clip receives separate copies across artifact contexts");
            using(var replay=GuardBuildContext.LoadPrivate(contexts[0].BuildId))
                Check(Rejects(()=>replay.CreateContextualCodec(MeshBindingIdentity.Capture(second.Root,second.Root.transform.Find("Body").GetComponent<Renderer>()),GuardAnimationContext.Capture(second.Root))),"private replay rejects the same binding with a different controller context");
            Native(fixtures.ToArray(),contexts.ToArray(),artifacts.ToArray());
            Check(sourceFiles.All(p=>File.Exists(p.Key)&&Hash(File.ReadAllBytes(p.Key))==p.Value),"source meshes/materials/controllers/clips/metas remain byte identical");
            Check(shaders.All(p=>File.Exists(p.Key)&&Hash(File.ReadAllBytes(p.Key))==p.Value),"all original lilToon files remain byte identical");
            File.WriteAllText(Output+"/validation.json",JsonUtility.ToJson(new Report{checks=checks.ToArray(),unity=Application.unityVersion,graphics=SystemInfo.graphicsDeviceType.ToString(),
                scope="Owned rigid generic Animator fixtures in Unity only; stepped ID selectors with convex blending bounds; unlit mono rendering and test-only variant filter; no VRChat analysis or SDK processing",
                contexts=2,rendererBindings=4,linuxBundles=2,windowsBundles=2,images=Directory.GetFiles(Output,"*.png").Length,generatedShaders=artifacts.Sum(a=>a.ShaderAssetPaths.Count),rejectedPrograms=rejectedPrograms,maxAdaptiveError=maxError,maxUnlockedImageDifference=maxVisual,minLockedImageDifference=minLocked,minCrossDecoderRms=minCross,originalFilesUnchanged=true,sourceShadersUnchanged=true,finalSdkAvatarTested=false},true));
            Debug.Log("LAG_SHADER_FORGE_VALIDATION_SUCCESS "+checks.Count+" checks; two native bundles and "+Directory.GetFiles(Output,"*.png").Length+" images");
        }
        finally{foreach(var a in artifacts)a.Dispose();foreach(var c in contexts)c.Dispose();foreach(var f in fixtures)f.Dispose();Environment.SetEnvironmentVariable("XDG_DATA_HOME",old);if(Directory.Exists(scratch))Directory.Delete(scratch,true);}
    }
}
public sealed class LAGShaderForgeProbe : MonoBehaviour { }
public sealed class LAGShaderForgeFaultPostprocessor : AssetPostprocessor
{
    public static GameObject Root;
    public static string Prefix;
    public static bool Touched;
    static void OnPostprocessAllAssets(string[] imported,string[] deleted,string[] moved,string[] oldPaths)
    {
        if(!Root||Touched||Environment.GetEnvironmentVariable("LAG_BINDING_AUDIT_ALLOWED")!="synthetic-unity-only")return;
        if(imported.Any(p=>p.StartsWith(Prefix,StringComparison.Ordinal)&&p.EndsWith("mesh.asset",StringComparison.Ordinal)))
        {Touched=true;Root.AddComponent<LAGShaderForgeProbe>();}
    }
}
