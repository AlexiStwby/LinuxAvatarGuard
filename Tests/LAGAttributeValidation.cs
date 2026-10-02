// SPDX-License-Identifier: MIT
// Own static fixtures only, in the explicitly enabled disposable Vulkan validation project.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using LinuxAvatarGuard;
using Object = UnityEngine.Object;

public static class LAGAttributeValidation
{
    const string Folder = "Assets/AttributeCodecFixture";
    const string Output = "../evidence/dynamic-attributes";
    static readonly List<string> checks = new List<string>();
    static int fixtureIndex;
    static int rejectedPrograms;
    static float worstError, worstVisual, minLocked = float.PositiveInfinity;
    static readonly HashSet<string> layouts = new HashSet<string>();
    static string Hash(byte[] bytes) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    static byte[] Seed(string value) { using (var h = SHA256.Create()) return h.ComputeHash(Encoding.UTF8.GetBytes("LAG/owned-attributes/v1/" + value)); }
    static void Check(bool value, string description) { if (!value) throw new Exception("ATTRIBUTE_VALIDATION_FAILED: " + description); checks.Add(description); }
    static bool Rejects(Action action) => LAGBindingValidation.Rejects(action);
    static string Layout(CodecPlan p) => p.Program.Attributes.FirstUvChannel + "," + p.Program.Attributes.SecondUvChannel;
    static Material Material(string shaderName, int selector = 8)
    {
        var shader = Shader.Find(shaderName); if (!shader) throw new Exception("Missing fixture shader " + shaderName);
        var mat = new Material(shader); mat.SetFloat("_AsUnlit", 1); mat.SetColor("_Color", new Color(.25f,.7f,.9f,1)); mat.SetFloat("_IDMaskFrom", selector);
        AssetDatabase.CreateAsset(mat, Folder + "/material-" + fixtureIndex++ + ".mat"); return mat;
    }
    static Mesh Mesh(int mask, int dimension, bool multiple = false)
    {
        var mesh = LAGBindingValidation.Fixture();
        for (int channel = 1; channel < 8; channel++)
        {
            if (channel >= 4 && (mask & (1 << (channel - 4))) == 0) continue;
            var values = mesh.vertices.Select(p => new Vector4(.2f+p.x*.1f,.3f+p.y*.1f,.4f+p.z*.1f,.9f)).ToList();
            if (dimension == 2) mesh.SetUVs(channel, values.Select(v => new Vector2(v.x,v.y)).ToList());
            else if (dimension == 3) mesh.SetUVs(channel, values.Select(v => new Vector3(v.x,v.y,v.z)).ToList()); else mesh.SetUVs(channel,values);
        }
        mesh.colors = mesh.vertices.Select(v => new Color(.5f+v.x*.1f,.6f,.8f,1)).ToArray();
        if (multiple) { var triangles = mesh.triangles; mesh.subMeshCount=2; mesh.SetTriangles(triangles.Take(triangles.Length/2).ToArray(),0); mesh.SetTriangles(triangles.Skip(triangles.Length/2).ToArray(),1); }
        string path=Folder+"/mesh-"+(fixtureIndex++)+".asset";
        AssetDatabase.CreateAsset(mesh,path);
        // Pin owned test asset identity, so accepted/rejected private programs reproduce on rerun.
        string meta=path+".meta";
        File.WriteAllText(meta,Regex.Replace(File.ReadAllText(meta),@"(?m)^guid: [0-9a-f]{32}$","guid: "+Hash(Seed(path)).Substring(0,32)));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);return AssetDatabase.LoadAssetAtPath<Mesh>(path);
    }
    static MeshRenderer Root(Mesh mesh, params Material[] mats)
    {
        var root = new GameObject("OwnedAttributeRoot"); var body = new GameObject("Body"); body.transform.SetParent(root.transform,false);
        body.AddComponent<MeshFilter>().sharedMesh=mesh; var renderer=body.AddComponent<MeshRenderer>(); renderer.sharedMaterials=mats; return renderer;
    }
    static MeshBindingIdentity Binding(MeshRenderer r) => MeshBindingIdentity.Capture(r.transform.parent.gameObject,r);
    static bool Preserved(Mesh source, Mesh encoded, CodecPlan plan)
    {
        if (!source.normals.SequenceEqual(encoded.normals) || !source.tangents.SequenceEqual(encoded.tangents) || !source.colors.SequenceEqual(encoded.colors) || source.bounds!=encoded.bounds) return false;
        for(int s=0;s<source.subMeshCount;s++) if(source.GetTopology(s)!=encoded.GetTopology(s) || !source.GetIndices(s).SequenceEqual(encoded.GetIndices(s))) return false;
        for(int channel=0;channel<8;channel++)
        {
            if(channel==plan.Program.Attributes.FirstUvChannel || channel==plan.Program.Attributes.SecondUvChannel) continue;
            var a=new List<Vector4>();var b=new List<Vector4>();source.GetUVs(channel,a);encoded.GetUVs(channel,b);if(!a.SequenceEqual(b))return false;
            var attr=(VertexAttribute)((int)VertexAttribute.TexCoord0+channel);
            if(source.HasVertexAttribute(attr)!=encoded.HasVertexAttribute(attr) || source.GetVertexAttributeDimension(attr)!=encoded.GetVertexAttributeDimension(attr) ||
                source.GetVertexAttributeFormat(attr)!=encoded.GetVertexAttributeFormat(attr))return false;
        }
        return true;
    }
    static void Key(Renderer r, int[] key) {var b=new MaterialPropertyBlock();for(int i=0;i<4;i++)b.SetFloat(GuardShaders.Property(i),key[i]);r.SetPropertyBlock(b);}
    static Color[] Render(Camera camera,string path)
    {
        var rt=new RenderTexture(256,256,24,RenderTextureFormat.ARGB32);var tex=new Texture2D(256,256,TextureFormat.RGBA32,false);var old=RenderTexture.active;
        try {camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,256,256),0,0);tex.Apply();File.WriteAllBytes(path,tex.EncodeToPNG());return tex.GetPixels();}
        finally {RenderTexture.active=old;camera.targetTexture=null;Object.DestroyImmediate(tex);Object.DestroyImmediate(rt);}
    }
    static float Difference(Color[] a,Color[] b) => a.Zip(b,(x,y)=>Mathf.Abs(x.r-y.r)+Mathf.Abs(x.g-y.g)+Mathf.Abs(x.b-y.b)).Average()/3;
    static void FunctionalChecks(Material mat)
    {
        for(int dimension=2;dimension<=4;dimension++) for(int mask=0;mask<16;mask++)
        {
            var source=Mesh(mask,dimension); var renderer=Root(source,mat); var binding=Binding(renderer);
            string file=AssetDatabase.GetAssetPath(source);var before=Hash(File.ReadAllBytes(file));var content=MeshBindingIdentity.ContentFingerprint(source);
            try
            {
                var usage=AttributeAllocator.Inspect(binding);var expected=Enumerable.Range(4,4).Where(c=>(mask&(1<<(c-4)))==0).ToArray();
                Check(usage.AvailableUvChannels.SequenceEqual(expected),"occupied mask "+mask+" / "+dimension+" components is reserved exactly");
                using(var context=GuardBuildContext.CreateReproducible(Hash(Seed("mask-id-"+mask+"-"+dimension)).Substring(0,32),Seed("mask-seed-"+mask+"-"+dimension)))
                {
                    if(expected.Length<2) Check(Rejects(()=>{using(var unused=context.CreateDynamicCodec(binding)) {}}),"insufficient carriers cancel for mask "+mask+" / "+dimension);
                    else using(var codec=context.CreateDynamicCodec(binding))
                    {
                        var plan=codec.Plan(source,.15f);using(var repeated=context.CreateDynamicCodec(binding))
                            Check(StaticPolymorphicCodecV1.ProgramHash(repeated.Plan(source,.15f))==StaticPolymorphicCodecV1.ProgramHash(plan),"allocation/program deterministic for mask "+mask+" / "+dimension);
                        Check(expected.Contains(plan.Program.Attributes.FirstUvChannel)&&expected.Contains(plan.Program.Attributes.SecondUvChannel)&&plan.Program.Attributes.FirstUvChannel!=plan.Program.Attributes.SecondUvChannel,"only distinct available carriers selected for mask "+mask+" / "+dimension);
                        var encoded=codec.Encode(source,plan,context.RuntimeKey());
                        try
                        {
                            var parsed=new LAGAdaptiveShaderDecoder(codec.EmitDecoder(plan).Injection);var error=LAGAdaptiveShaderDecoder.MaxError(parsed.Decode(encoded,context.RuntimeKey()),source.vertices);
                            worstError=Mathf.Max(worstError,error);Check(error<1e-5f,"independent HLSL parser reconstructs mask "+mask+" / "+dimension);
                            Check(Preserved(source,encoded,plan),"UV data/dimensions, normals, tangents, colors, indices and bounds preserved for mask "+mask+" / "+dimension);
                            foreach(int channel in new[]{plan.Program.Attributes.FirstUvChannel,plan.Program.Attributes.SecondUvChannel})
                            {var attr=(VertexAttribute)((int)VertexAttribute.TexCoord0+channel);Check(encoded.GetVertexAttributeDimension(attr)==2&&encoded.GetVertexAttributeFormat(attr)==VertexAttributeFormat.Float32,"payload carrier "+channel+" is Float32 x2");}
                        }
                        finally{Object.DestroyImmediate(encoded);}
                    }
                }
                Check(before==Hash(File.ReadAllBytes(file))&&content==MeshBindingIdentity.ContentFingerprint(source),"source remains byte/content identical for mask "+mask+" / "+dimension);
            }
            finally{Object.DestroyImmediate(renderer.transform.parent.gameObject);}
        }
        var mesh=Mesh(0,2);var r=Root(mesh,mat);var bnd=Binding(r);
        try
        {
            for(int selected=0;selected<=8;selected++)
            {
                mat.SetFloat("_IDMaskFrom",selected);var usage=AttributeAllocator.Inspect(bnd);
                Check(usage.AvailableUvChannels.SequenceEqual(Enumerable.Range(4,4).Where(c=>c!=selected)),"ID Mask selector "+selected+" reserves its carrier even with every mask disabled");
            }
            mat.SetFloat("_IDMaskFrom",8);
            var hd=MeshKeyDerivation.Derive(LAGBindingValidation.PublicSeed,LAGBindingValidation.PublicBuildId,bnd,MeshDerivationPurpose.AttributeLayout);
            Check(!hd.SequenceEqual(MeshKeyDerivation.Derive(LAGBindingValidation.PublicSeed,LAGBindingValidation.PublicBuildId,bnd,MeshDerivationPurpose.Program)),"HKDF attribute-layout domain is separated from program domain");Array.Clear(hd,0,hd.Length);
            using(var context=GuardBuildContext.CreateReproducible(LAGBindingValidation.PublicBuildId,LAGBindingValidation.PublicSeed))
            using(var codec=context.CreateDynamicCodec(bnd))
            {
                var plan=codec.Plan(mesh,.15f);context.RecordPlan(bnd,plan);var encoded=codec.Encode(mesh,plan,context.RuntimeKey());
                try
                {
                    string path=context.SavePrivate(),original=File.ReadAllText(path);
                    Check(original.Contains("\"schemaVersion\": "+GuardBuildContext.SchemaVersion)&&original.Contains("\"attributePolicy\": 1"),"current private schema explicitly records dynamic policy/layout");
                    using(var restored=GuardBuildContext.LoadPrivate(context.BuildId)) using(var repeated=restored.CreateCodec(bnd))
                    {
                        var p=repeated.Plan(mesh,.15f);var copy=repeated.Encode(mesh,p,restored.RuntimeKey());
                        try {Check(Layout(p)==Layout(plan)&&p.AttributeUsageHash==plan.AttributeUsageHash&&MeshBindingIdentity.ContentFingerprint(copy)==MeshBindingIdentity.ContentFingerprint(encoded),"disk restore exactly reproduces dynamic layout, usage hash and mesh");}
                        finally{Object.DestroyImmediate(copy);}
                    }
                    File.WriteAllText(path,original.Replace("\"schemaVersion\": "+GuardBuildContext.SchemaVersion,"\"schemaVersion\": 2"));
                    using(var old=GuardBuildContext.LoadPrivate(context.BuildId))using(var repeated=old.CreateCodec(bnd))
                        Check(StaticPolymorphicCodecV1.ProgramHash(repeated.Plan(mesh,.15f))==StaticPolymorphicCodecV1.ProgramHash(plan),"historical private schema 2 reproduces the same policy 1 program");
                    foreach(var change in new[]{original.Replace("\"attributePolicy\": 1","\"attributePolicy\": 99"),original.Replace("\"components\": 2","\"components\": 4"),Regex.Replace(original,@"""firstUv"": [4-7]","\"firstUv\": 0"),original.Replace("\"schemaVersion\": "+GuardBuildContext.SchemaVersion,"\"schemaVersion\": 1")})
                    {File.WriteAllText(path,change);Check(Rejects(()=>GuardBuildContext.LoadPrivate(context.BuildId)),"malformed/downgraded private allocation is rejected");}
                    File.WriteAllText(path,original.Replace(plan.AttributeUsageHash,Hash(Seed("tampered-usage"))));
                    Check(Rejects(()=>{using(var restored=GuardBuildContext.LoadPrivate(context.BuildId))using(var unused=restored.CreateCodec(bnd)) {}}),"tampered valid-length usage hash fails replay");File.WriteAllText(path,original);
                    Action<string,Action,Action> mutation=(name,change,restore)=>{try{change();Check(Rejects(()=>codec.Encode(mesh,plan,context.RuntimeKey()))&&Rejects(()=>codec.EmitDecoder(plan)),name+" invalidates encode and shader emission before writing");}finally{restore();}};
                    mutation("ID Mask property change",()=>mat.SetFloat("_IDMaskFrom",4),()=>mat.SetFloat("_IDMaskFrom",8));
                    mutation("fractional ID Mask selector",()=>mat.SetFloat("_IDMaskFrom",4.5f),()=>mat.SetFloat("_IDMaskFrom",8));
                    mutation("nonfinite ID Mask selector",()=>mat.SetFloat("_IDMaskFrom",float.NaN),()=>mat.SetFloat("_IDMaskFrom",8));
                    float cutoff=mat.GetFloat("_Cutoff");mutation("other material property change",()=>mat.SetFloat("_Cutoff",cutoff+.1f),()=>mat.SetFloat("_Cutoff",cutoff));
                    mutation("unknown material keyword",()=>mat.EnableKeyword("_ADD_PRECOMPUTED_VELOCITY"),()=>mat.DisableKeyword("_ADD_PRECOMPUTED_VELOCITY"));
                    mutation("global velocity keyword",()=>Shader.EnableKeyword("_ADD_PRECOMPUTED_VELOCITY"),()=>Shader.DisableKeyword("_ADD_PRECOMPUTED_VELOCITY"));
                    mutation("disabled shader pass",()=>mat.SetShaderPassEnabled("FORWARD",false),()=>mat.SetShaderPassEnabled("FORWARD",true));
                    var replacement=Material("lilToon");mutation("replacement material identity",()=>r.sharedMaterial=replacement,()=>r.sharedMaterial=mat);
                    mutation("property block override",()=>{var block=new MaterialPropertyBlock();block.SetFloat("_IDMaskFrom",4);r.SetPropertyBlock(block);},()=>r.SetPropertyBlock(null));
                    mutation("per-material property block override",()=>{var block=new MaterialPropertyBlock();block.SetFloat("_IDMaskFrom",4);r.SetPropertyBlock(block,0);},()=>r.SetPropertyBlock(null,0));
                    mutation("additional vertex stream",()=>r.additionalVertexStreams=mesh,()=>r.additionalVertexStreams=null);
                    mutation("enlighten vertex stream",()=>r.enlightenVertexStream=mesh,()=>r.enlightenVertexStream=null);
                    var oldFlags=GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                    mutation("static batching flag",()=>GameObjectUtility.SetStaticEditorFlags(r.gameObject,oldFlags|StaticEditorFlags.BatchingStatic),()=>GameObjectUtility.SetStaticEditorFlags(r.gameObject,oldFlags));
                    mutation("material batching override",()=>mat.SetOverrideTag("DisableBatching","False"),()=>mat.SetOverrideTag("DisableBatching",""));
                    int carrier=plan.Program.Attributes.FirstUvChannel;
                    mutation("new source data in allocated carrier",()=>mesh.SetUVs(carrier,mesh.vertices.Select(v=>new Vector4(v.x,v.y,v.z,1)).ToList()),()=>mesh.SetUVs(carrier,new List<Vector4>()));
                    var animator=r.transform.parent.gameObject.AddComponent<Animator>();animator.enabled=false;
                    Check(Rejects(()=>AttributeAllocator.Inspect(bnd)),"disabled Animator is outside static allocation scope");Object.DestroyImmediate(animator);
                    var animation=r.gameObject.AddComponent<Animation>();Check(Rejects(()=>AttributeAllocator.Inspect(bnd)),"legacy Animation is rejected");Object.DestroyImmediate(animation);
                    var script=r.gameObject.AddComponent<LAGAttributeProbe>();Check(Rejects(()=>AttributeAllocator.Inspect(bnd)),"unreviewed MonoBehaviour is rejected");Object.DestroyImmediate(script);
                    var ancestor=new GameObject("AnimatedAncestor");r.transform.parent.SetParent(ancestor.transform);ancestor.AddComponent<Animator>();
                    Check(Rejects(()=>AttributeAllocator.Inspect(bnd)),"animated ancestor is rejected");r.transform.parent.SetParent(null);Object.DestroyImmediate(ancestor);
                    Check(!Rejects(()=>codec.Validate(mesh,plan)),"restoring the source context restores its analysis without changing identity");
                }
                finally{Object.DestroyImmediate(encoded);}
            }
            using(var fixedContext=GuardBuildContext.CreateReproducible(Hash(Seed("schema1-id")).Substring(0,32),Seed("schema1-seed")))using(var fixedCodec=fixedContext.CreateCodec(bnd))
            {
                var plan=fixedCodec.Plan(mesh,.15f);fixedContext.RecordPlan(bnd,plan);var path=fixedContext.SavePrivate();var text=File.ReadAllText(path).Replace("\"schemaVersion\": "+GuardBuildContext.SchemaVersion,"\"schemaVersion\": 1");
                text=Regex.Replace(text,@"(?m)^\s*""(?:firstUv|secondUv|components|attributePolicy|usageHash)"":.*\n?","");text=Regex.Replace(text,@",\s*}","\n}");File.WriteAllText(path,text);
                using(var restored=GuardBuildContext.LoadPrivate(fixedContext.BuildId))using(var old=restored.CreateCodec(bnd))
                {var p=old.Plan(mesh,.15f);Check(p.Program.SchemaVersion==2&&Layout(p)=="6,7"&&p.Program.Attributes.PolicyVersion==0&&StaticPolymorphicCodecV1.ProgramHash(p)==StaticPolymorphicCodecV1.ProgramHash(plan),"historical schema 1 without allocation fields reproduces fixed UV6/UV7 and unchanged program hash");Check(Rejects(()=>{using(var unused=restored.CreateDynamicCodec(bnd)) {}}),"schema 1 cannot silently become dynamic");}
            }
            int accepted=0;
            for(int i=0;accepted<100&&i<1000;i++) using(var ctx=GuardBuildContext.CreateReproducible(Hash(Seed("diversity-id-"+i)).Substring(0,32),Seed("diversity-seed-"+i)))using(var codec=ctx.CreateDynamicCodec(bnd))
            {
                CodecPlan plan;
                try{plan=codec.Plan(mesh,.15f);}
                catch(InvalidOperationException e)when(e.Message.Contains("cancela sus offsets keyed"))
                {rejectedPrograms++;Check(true,"keyless diversity candidate "+i+" is rejected by the existing dependency guard");continue;}
                accepted++;layouts.Add(Layout(plan));Check(plan.Program.SchemaVersion==3&&plan.AttributeUsageHash!=null,"dynamic build "+i+" binds schema 3 program to attribute analysis");
            }
            Check(accepted==100,"one hundred accepted dynamic contexts are examined with rejected candidates recorded separately");
            Check(layouts.Count==12,"100 private fixture contexts exercise all twelve ordered layouts across four UV channels");
            var shaderPath=AssetDatabase.GetAssetPath(mat.shader);var includes=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(shaderPath)),"Includes");
            var sentinel=Path.Combine(includes,"LAG_unreviewed_hook.hlsl");try{File.WriteAllText(sentinel,"#define LIL_REQUIRE_APP_PREVPOS\n");Check(Rejects(()=>AttributeAllocator.Inspect(bnd)),"additional shader include/custom hook invalidates the pinned source contract");}finally{File.Delete(sentinel);}
            var common=Path.Combine(includes,"lil_common_appdata.hlsl");var originalBytes=File.ReadAllBytes(common);
            try{File.AppendAllText(common,"\n#define LIL_REQUIRE_APP_PREVEL\n");Check(Rejects(()=>AttributeAllocator.Inspect(bnd)),"modified existing appdata source is rejected even when the file count is unchanged");}finally{File.WriteAllBytes(common,originalBytes);}
            Check(Hash(File.ReadAllBytes(common))==Hash(originalBytes),"shader source mutation test restores exact original bytes");
            var unknown=new Material(Shader.Find("Unlit/Color"));AssetDatabase.CreateAsset(unknown,Folder+"/unknown.mat");r.sharedMaterial=unknown;Check(Rejects(()=>AttributeAllocator.Inspect(bnd)),"unknown shader is rejected");r.sharedMaterial=mat;
            r.sharedMaterials=new[]{mat,mat};Check(Rejects(()=>AttributeAllocator.Inspect(bnd)),"extra material slots are rejected");r.sharedMaterials=new Material[]{null};Check(Rejects(()=>AttributeAllocator.Inspect(bnd)),"empty material slots are rejected");r.sharedMaterial=mat;
        }
        finally{mat.SetFloat("_IDMaskFrom",8);Object.DestroyImmediate(r.transform.parent.gameObject);}
        var multi=Mesh(0,3,true);var m4=Material("lilToon",4);var m5=Material("Hidden/lilToonCutout",5);var multiple=Root(multi,m4,m5);
        try{Check(AttributeAllocator.Inspect(Binding(multiple)).AvailableUvChannels.SequenceEqual(new[]{6,7}),"all submesh materials contribute consumer reservations across variants");}
        finally{Object.DestroyImmediate(multiple.transform.parent.gameObject);}
    }
    [Serializable] sealed class Sample {public string prefab,bundle,firstShader;public string[] providers;public int firstUv,secondUv;[NonSerialized]public int[] key;[NonSerialized]public MeshRenderer original;[NonSerialized]public Mesh source;}
    static void BundleChecks(Material mat)
    {
        var samples=new List<Sample>();var builds=new List<AssetBundleBuild>();int shaderCount=0;
        string[] variants={"lilToon","Hidden/lilToonOutline","Hidden/lilToonCutout","Hidden/lilToonCutoutOutline","Hidden/lilToonTransparent","Hidden/lilToonTransparentOutline","Hidden/lilToonOnePassTransparent","Hidden/lilToonOnePassTransparentOutline","Hidden/lilToonTwoPassTransparent","Hidden/lilToonTwoPassTransparentOutline"};
        for(int a=4;a<=7;a++)for(int b=a+1;b<=7;b++)
        {
            int mask=15^(1<<(a-4))^(1<<(b-4));var source=Mesh(mask,4);var original=Root(source,mat);var binding=Binding(original);string folder=Folder+"/pair-"+a+"-"+b;Directory.CreateDirectory(folder);
            using(var context=GuardBuildContext.CreateReproducible(Hash(Seed("pair-id-"+a+"-"+b)).Substring(0,32),Seed("pair-seed-"+a+"-"+b)))using(var codec=context.CreateDynamicCodec(binding))
            {
                var plan=codec.Plan(source,.15f);var decoder=codec.EmitDecoder(plan);var encoded=codec.Encode(source,plan,context.RuntimeKey());AssetDatabase.CreateAsset(encoded,folder+"/encoded.asset");
                var forge=new GuardShaders(folder,context.BuildId,decoder);var shader=forge.Copy(mat.shader);
                foreach(var variant in variants) Check(forge.Copy(Shader.Find(variant))!=null,"UV pair "+a+"/"+b+" compiles reviewed shader variant "+variant);
                var generated=new Material(mat){shader=shader};AssetDatabase.CreateAsset(generated,folder+"/protected.mat");var copy=Object.Instantiate(original.transform.parent.gameObject);var rr=copy.GetComponentInChildren<MeshRenderer>();rr.GetComponent<MeshFilter>().sharedMesh=encoded;rr.sharedMaterial=generated;
                Check(AssetDatabase.GetAssetPath(shader)==folder+"/shader_1.shader","forge returns the current shader asset rather than a cached namesake for pair "+a+"/"+b);
                Check(string.Equals(generated.GetTag("DisableBatching",false,""),"True",StringComparison.OrdinalIgnoreCase),"prototype shader disables dynamic batching for pair "+a+"/"+b+"; tag="+generated.GetTag("DisableBatching",false,"<missing>"));
                string prefab=folder+"/fixture.prefab";PrefabUtility.SaveAsPrefabAsset(copy,prefab);Object.DestroyImmediate(copy);
                var shaderPaths=Directory.GetFiles(folder,"*.shader").Select(p=>p.Replace('\\','/')).ToArray();shaderCount+=shaderPaths.Length;
                Check(!AssetDatabase.GetDependencies(prefab,true).Contains(AssetDatabase.GetAssetPath(source)),"UV pair "+a+"/"+b+" excludes original mesh from prefab dependencies");
                var providers=shaderPaths.Where(p=>Regex.Match(File.ReadAllText(p),@"(?m)^Shader\s+""([^""]+)""").Groups[1].Value.Contains("/Hidden/ltspass_")).Select(p=>p.ToLowerInvariant()).ToArray();
                samples.Add(new Sample{prefab=prefab,bundle="uv-"+a+"-"+b+".bundle",firstShader=folder+"/shader_1.shader",providers=providers,firstUv=plan.Program.Attributes.FirstUvChannel,secondUv=plan.Program.Attributes.SecondUvChannel,key=context.RuntimeKey(),original=original,source=source});original.transform.parent.gameObject.SetActive(false);
                string manifest=folder+"/public-layout.json";string publicText=JsonUtility.ToJson(new PublicLayout{buildId=context.BuildId,firstUv=plan.Program.Attributes.FirstUvChannel,secondUv=plan.Program.Attributes.SecondUvChannel,programHash=StaticPolymorphicCodecV1.ProgramHash(plan)},true);
                Check(!publicText.Contains("masterSeed")&&!publicText.Contains("runtimeKey"),"public layout manifest excludes secrets for pair "+a+"/"+b);
                Check(Enumerable.Range(0,4).All(k=>generated.GetFloat(GuardShaders.Property(k))==0),"saved material has zero unlock values for pair "+a+"/"+b);
                File.WriteAllText(manifest,publicText);AssetDatabase.ImportAsset(manifest,ImportAssetOptions.ForceSynchronousImport);
                builds.Add(new AssetBundleBuild{assetBundleName="uv-"+a+"-"+b+".bundle",assetNames=new[]{prefab,manifest}.Concat(shaderPaths).ToArray()});
            }
        }
        var oldApis=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneLinux64);bool oldAuto=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64);
        var cameraObject=new GameObject("OwnedAttributeCamera");var camera=cameraObject.AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.035f,.05f,.075f,1);camera.orthographic=true;camera.orthographicSize=1.05f;
        try
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,new[]{GraphicsDeviceType.Vulkan});LAGDiversityVariantFilter.Enabled=true;
            Directory.CreateDirectory(Output+"/linux-bundles");var result=BuildPipeline.BuildAssetBundles(Output+"/linux-bundles",builds.ToArray(),BuildAssetBundleOptions.ForceRebuildAssetBundle|BuildAssetBundleOptions.StrictMode,BuildTarget.StandaloneLinux64);
            Check(result!=null&&result.GetAllAssetBundles().Length==6,"six actual Vulkan AssetBundles cover all unordered UV carrier pairs");
            foreach(var s in samples)
            {
                var bundle=AssetBundle.LoadFromFile(Path.GetFullPath(Output+"/linux-bundles/"+s.bundle));GameObject instance=null;
                try
                {
                    Check(bundle!=null,"native bundle reopens for carriers "+s.firstUv+"/"+s.secondUv);
                    var names=bundle.GetAllAssetNames();
                    foreach(var name in s.providers.Concat(names.Where(n=>n.EndsWith(".shader")&&!s.providers.Contains(n))))
                    {var shader=bundle.LoadAsset<Shader>(name);Check(shader!=null&&shader.isSupported&&!ShaderUtil.ShaderHasError(shader),"native shader/UsePass provider supports Vulkan: "+name);}
                    var publicManifest=names.Single(n=>n.EndsWith("/public-layout.json"));
                    Check(bundle.LoadAsset<TextAsset>(publicManifest)!=null&&!names.Any(n=>n.Contains("lagprivate")||n.Contains("original.asset")),"native bundle includes public layout and excludes private context for "+s.bundle);
                    var prefab=bundle.LoadAsset<GameObject>(s.prefab);instance=Object.Instantiate(prefab);var renderer=instance.GetComponentInChildren<MeshRenderer>();
                    Check(renderer.sharedMaterial.shader.isSupported&&!ShaderUtil.ShaderHasError(renderer.sharedMaterial.shader),"native bundle shader supports Vulkan for "+s.bundle);
                    Check(string.Equals(renderer.sharedMaterial.GetTag("DisableBatching",false,""),"True",StringComparison.OrdinalIgnoreCase),"native shader preserves the batching guard for "+s.bundle);
                    var encoded=renderer.GetComponent<MeshFilter>().sharedMesh;var extractor=new LAGAdaptiveShaderDecoder(File.ReadAllText(s.firstShader));var error=LAGAdaptiveShaderDecoder.MaxError(extractor.Decode(encoded,s.key),s.source.vertices);worstError=Mathf.Max(worstError,error);
                    Check(error<1e-5f,"adaptive shader-source extraction still recovers dynamic carriers from "+s.bundle);
                    Check(LAGAdaptiveShaderDecoder.Rms(extractor.Decode(encoded,new int[4]),s.source.vertices)>.02f,"absent runtime values deform "+s.bundle);
                    for(int view=0;view<3;view++)
                    {
                        float angle=view*60*Mathf.Deg2Rad;camera.transform.position=new Vector3(Mathf.Sin(angle)*2.8f,.18f,Mathf.Cos(angle)*2.8f);camera.transform.LookAt(Vector3.zero);string prefix=Output+"/uv-"+s.firstUv+"-"+s.secondUv+"-view-"+view;
                        instance.SetActive(false);s.original.transform.parent.gameObject.SetActive(true);var reference=Render(camera,prefix+"-original.png");s.original.transform.parent.gameObject.SetActive(false);instance.SetActive(true);
                        Key(renderer,s.key);var unlocked=Render(camera,prefix+"-unlocked.png");Key(renderer,new int[4]);var locked=Render(camera,prefix+"-locked.png");float diff=Difference(reference,unlocked),lockedDiff=Difference(reference,locked);worstVisual=Mathf.Max(worstVisual,diff);minLocked=Mathf.Min(minLocked,lockedDiff);
                        Check(diff<.002f&&lockedDiff>.005f,"native Vulkan unlocked/locked rendering for "+s.bundle+" view "+view);
                    }
                }
                finally{if(instance)Object.DestroyImmediate(instance);if(bundle)bundle.Unload(true);}
            }
            Check(shaderCount>=60,"ten shader variants generated for each of six carrier pairs");
        }
        finally{LAGDiversityVariantFilter.Enabled=false;PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,oldApis);PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,oldAuto);Object.DestroyImmediate(cameraObject);foreach(var s in samples){Object.DestroyImmediate(s.original.transform.parent.gameObject);Array.Clear(s.key,0,s.key.Length);}}
    }
    [Serializable] sealed class PublicLayout {public int schemaVersion=1,policyVersion=1,components=2,programSchemaVersion=3,codecVersion=1,firstUv,secondUv;public string codecId=StaticPolymorphicCodecV1.Id,format="Float32",buildId,programHash;public string status="ResearchStaticFixtureOnly";}
    [Serializable] sealed class Report {public string[] checks,orderedLayouts;public string graphics,unity,shaderVersion,reviewedShaderDigest,scope;public int dynamicContexts,rejectedPrograms,linuxBundles,views,images;public float worstAdaptiveError,worstUnlockedImageDifference,minLockedImageDifference;public bool originalUnchanged,finalSdkAvatarTested;}
    public static void Run()
    {
        LAGBindingValidation.RequireResearchProject();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);checks.Clear();layouts.Clear();fixtureIndex=0;rejectedPrograms=0;worstError=0;worstVisual=0;minLocked=float.PositiveInfinity;
        if(AssetDatabase.IsValidFolder(Folder))AssetDatabase.DeleteAsset(Folder);AssetDatabase.CreateFolder("Assets","AttributeCodecFixture");Directory.CreateDirectory(Output);
        string oldXdg=Environment.GetEnvironmentVariable("XDG_DATA_HOME"),privateRoot=Path.Combine(Path.GetTempPath(),"lag-attributes-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(privateRoot);Environment.SetEnvironmentVariable("XDG_DATA_HOME",privateRoot);
        try
        {
            var mat=Material("lilToon");AssetDatabase.SaveAssets();string materialPath=AssetDatabase.GetAssetPath(mat);string materialHash=Hash(File.ReadAllBytes(materialPath));
            FunctionalChecks(mat);BundleChecks(mat);
            Check(Hash(File.ReadAllBytes(materialPath))==materialHash,"original material file remains byte identical after all operations");
            File.WriteAllText(Output+"/validation.json",JsonUtility.ToJson(new Report{checks=checks.ToArray(),orderedLayouts=layouts.OrderBy(v=>v).ToArray(),graphics=SystemInfo.graphicsDeviceType.ToString(),unity=Application.unityVersion,shaderVersion="2.3.4",reviewedShaderDigest=AttributeAllocator.ReviewedShaderDigest,
                scope="Owned static Unity fixtures; UV indices 4–7 only; no animation/skinning/custom shaders/material keywords; unlit mono renders and test-only variant filter; no VRChat inspection",
                dynamicContexts=100,rejectedPrograms=rejectedPrograms,linuxBundles=6,views=18,images=54,worstAdaptiveError=worstError,worstUnlockedImageDifference=worstVisual,minLockedImageDifference=minLocked,originalUnchanged=true,finalSdkAvatarTested=false},true));
            Debug.Log("LAG_ATTRIBUTE_VALIDATION_SUCCESS "+checks.Count+" checks; twelve ordered layouts, six native Vulkan bundles and 54 images");
        }
        finally{Environment.SetEnvironmentVariable("XDG_DATA_HOME",oldXdg);if(Directory.Exists(privateRoot))Directory.Delete(privateRoot,true);}
    }
}

public sealed class LAGAttributeProbe : MonoBehaviour { }
