// SPDX-License-Identifier: MIT
// 100 owned static prefab builds with two bindings each; not VRChat/SDK uploads or a performance certification.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using LinuxAvatarGuard;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

public static class LAGDiversityValidation
{
    const string Folder="Assets/CodecDiversityFixture", Output="../evidence/codec-diversity";
    const int Builds=100;
    const string FixtureSourceGuid="8e8d42bc2e173b915a7f9adefa841695"; // Owned fixture identity; keep controlled seeds reproducible after a clean run.
    // The original candidate 30 had adjacent opposite offsets (measured keyless RMS ~1.93e-8).
    // Keep its rejection as a regression; slot 30 uses public fixture input 100 to obtain 100 accepted builds.
    static int FixtureInput(int slot) => slot==30?100:slot;
    static readonly List<string> checks=new List<string>();
    static void Check(bool condition,string description)
    {if(!condition) throw new Exception("DIVERSITY_VALIDATION_FAILED: "+description); checks.Add(description);}
    static byte[] Digest(byte[] bytes) {using(var sha=SHA256.Create()) return sha.ComputeHash(bytes);}
    static string Hash(byte[] bytes) => BitConverter.ToString(Digest(bytes)).Replace("-","").ToLowerInvariant();
    static string HashText(string text) => Hash(Encoding.UTF8.GetBytes(text));
    static string FileHash(string path) => Hash(File.ReadAllBytes(path));
    static string MeshHash(Mesh mesh)
    {
        using(var stream=new MemoryStream())
        {
            using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))
            {
                foreach(var v in mesh.vertices) {writer.Write(v.x);writer.Write(v.y);writer.Write(v.z);}
                foreach(var v in mesh.uv7) {writer.Write(v.x);writer.Write(v.y);}
                foreach(var v in mesh.uv8) {writer.Write(v.x);writer.Write(v.y);}
            }
            return Hash(stream.ToArray());
        }
    }
    static void Key(Renderer renderer,int[] values)
    {var block=new MaterialPropertyBlock(); for(int i=0;i<4;i++) block.SetFloat(GuardShaders.Property(i),values[i]); renderer.SetPropertyBlock(block);}
    static Color[] Render(Camera camera,string path)
    {
        var rt=new RenderTexture(256,256,24,RenderTextureFormat.ARGB32); var image=new Texture2D(256,256,TextureFormat.RGBA32,false);
        var previous=RenderTexture.active;
        try
        {camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,256,256),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());return image.GetPixels();}
        finally {RenderTexture.active=previous;camera.targetTexture=null;Object.DestroyImmediate(image);Object.DestroyImmediate(rt);}
    }
    static float Difference(Color[] a,Color[] b) => a.Zip(b,(x,y)=>Mathf.Abs(x.r-y.r)+Mathf.Abs(x.g-y.g)+Mathf.Abs(x.b-y.b)).Average()/3;
    [Serializable] sealed class PublicBinding {public string bindingId,programHash;public int[] payloadUvChannels={6,7};}
    [Serializable] sealed class PublicManifest
    {
        public int schemaVersion=1,codecVersion=StaticPolymorphicCodecV1.Version,derivationVersion=MeshKeyDerivation.Version;
        public string codecId=StaticPolymorphicCodecV1.Id,buildId,status="ResearchFixtureOnly";
        public bool onlySyntheticUnity=true; public int protectedMeshes=2,protectedTextures=0; public PublicBinding[] bindings;
    }
    sealed class Sample
    {
        public string id,prefab;public int[] key;public string[] shaders;public string[] meshHashes;public string publicManifest;
        public double generationSeconds;public long bundleBytes;public string bundleHash;
    }
    [Serializable] sealed class BuildResult
    {public string buildId,bundleHash;public long bundleBytes;public double generationSeconds;public bool adaptiveExtracted;}
    [Serializable] sealed class Report
    {
        public string[] checks;public string fixture,graphics,scope;public int buildCount,bindingCount,linuxBundles,windowsSampleBundles,generatedShaders;
        public int uniquePrograms,uniqueDecoderBodies,uniqueConstants,uniqueSequences,uniqueLayouts,uniqueEncodedMeshes,uniqueBundleHashes;
        public int adaptiveRecoveredBindings,crossBuildAttempts,crossMeshAttempts;
        public float worstAdaptiveMaxError,minCrossBuildRms,minCrossMeshRms,minMissingKeyRms,minWrongKeyRms,worstUnlockedImageDifference,minLockedImageDifference;
        public double totalGenerationSeconds,bundleBuildSeconds;public long totalBundleBytes;public BuildResult[] builds;
        public bool originalFilesUnchanged,privateContextAbsentFromBundles,finalSdkAvatarBundleTested;
        public bool resumedExistingFixtures;public int fixtureVariantsBeforeFilter,fixtureVariantsAfterFilter;
    }
    public static void Run() => RunInternal(false, true);
    public static void ResumeGeneratedFixtures() => RunInternal(true, true);
    public static void ReplayBuiltFixtures() => RunInternal(true, false);
    static void RunInternal(bool resume, bool rebuild)
    {
        LAGBindingValidation.RequireResearchProject();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);checks.Clear();
        var oldXdg=Environment.GetEnvironmentVariable("XDG_DATA_HOME");var privateRoot=Path.Combine(Path.GetTempPath(),"lag-diversity-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(privateRoot);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME",privateRoot);
        GameObject sourceRoot=null;var samples=new List<Sample>();var bundleBuilds=new List<AssetBundleBuild>();
        var programs=new HashSet<string>();var bodies=new HashSet<string>();var constants=new HashSet<string>();var sequences=new HashSet<string>();var meshes=new HashSet<string>();
        float worstError=0,minCrossBuild=float.MaxValue,minCrossMesh=float.MaxValue,minMissing=float.MaxValue,minWrong=float.MaxValue,worstVisual=0,minLocked=float.MaxValue;
        int recovered=0,crossBuild=0,crossMesh=0;double bundleSeconds=0;LAGAdaptiveShaderDecoder buildZeroDecoder=null;
        try
        {
            Mesh source;Material originalMaterial;
            if(resume)
            {
                source=AssetDatabase.LoadAssetAtPath<Mesh>(Folder+"/original.asset");originalMaterial=AssetDatabase.LoadAssetAtPath<Material>(Folder+"/original.mat");
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(Folder+"/original.prefab");
                var expected=LAGBindingValidation.Fixture();
                try {Check(source && originalMaterial && originalMaterial.shader.name=="lilToon" && originalMaterial.GetFloat("_AsUnlit")==1 && prefab &&
                    MeshBindingIdentity.ContentFingerprint(source)==MeshBindingIdentity.ContentFingerprint(expected),"resume only the exact owned generated source fixture");}
                finally {Object.DestroyImmediate(expected);}
                sourceRoot=(GameObject)PrefabUtility.InstantiatePrefab(prefab);
            }
            else
            {
                if(AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);AssetDatabase.CreateFolder("Assets","CodecDiversityFixture");
                source=LAGBindingValidation.Fixture();AssetDatabase.CreateAsset(source,Folder+"/original.asset");
                var sourceMeta=Folder+"/original.asset.meta";
                File.WriteAllText(sourceMeta,System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(sourceMeta),@"(?m)^guid: [0-9a-f]{32}$","guid: "+FixtureSourceGuid));
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                source=AssetDatabase.LoadAssetAtPath<Mesh>(Folder+"/original.asset");
                var texture=new Texture2D(16,16,TextureFormat.RGBA32,false);texture.SetPixels(Enumerable.Range(0,256).Select(i=>(i/64+i%16/4)%2==0?new Color(.2f,.7f,.9f):new Color(.9f,.3f,.2f)).ToArray());texture.Apply();AssetDatabase.CreateAsset(texture,Folder+"/original-texture.asset");
                originalMaterial=new Material(Shader.Find("lilToon"));originalMaterial.SetFloat("_AsUnlit",1);originalMaterial.SetTexture("_MainTex",texture);AssetDatabase.CreateAsset(originalMaterial,Folder+"/original.mat");
                sourceRoot=new GameObject("OwnedTwoBindingFixture");
                for(int i=0;i<2;i++)
                {var obj=new GameObject(i==0?"Body":"Hair");obj.transform.SetParent(sourceRoot.transform,false);obj.transform.localPosition=new Vector3(i==0?-.7f:.7f,0,0);obj.AddComponent<MeshFilter>().sharedMesh=source;obj.AddComponent<MeshRenderer>().sharedMaterial=originalMaterial;}
                PrefabUtility.SaveAsPrefabAsset(sourceRoot,Folder+"/original.prefab");AssetDatabase.SaveAssets();
            }
            var renderers=sourceRoot.GetComponentsInChildren<MeshRenderer>();var bindings=renderers.Select(r=>MeshBindingIdentity.Capture(sourceRoot,r)).ToArray();
            var originalPositions=source.vertices;var protectedSourcePaths=new[]{Folder+"/original.asset",Folder+"/original.mat",Folder+"/original-texture.asset",Folder+"/original.prefab"};
            var sourceHashes=protectedSourcePaths.Select(FileHash).ToArray();sourceRoot.SetActive(false);Directory.CreateDirectory(Output);
            var rejectedSeed=Digest(Encoding.UTF8.GetBytes("LAG/owned-diversity-fixture/v1/seed/30"));
            var rejectedId=HashText("LAG/owned-diversity-fixture/v1/build/30").Substring(0,32);
            try
            {
                using(var rejected=GuardBuildContext.CreateReproducible(rejectedId,rejectedSeed)) using(var bad=rejected.CreateCodec(bindings[0]))
                    Check(LAGBindingValidation.Rejects(()=>bad.Plan(source,.15f)),"known keyless fixture candidate 30 is rejected before mesh/shader generation");
            }
            finally {Array.Clear(rejectedSeed,0,rejectedSeed.Length);}
            for(int index=0;index<Builds;index++)
            {
                // Fixed public fixture inputs allow controlled reproduction; default user contexts use CSPRNG.
                var seed=Digest(Encoding.UTF8.GetBytes("LAG/owned-diversity-fixture/v1/seed/"+FixtureInput(index)));
                var id=HashText("LAG/owned-diversity-fixture/v1/build/"+FixtureInput(index)).Substring(0,32);
                var watch=Stopwatch.StartNew();var target=Folder+"/build-"+index.ToString("D3");
                bool useExisting=resume && (index!=30 || (File.Exists(target+"/public-manifest.json") && JsonUtility.FromJson<PublicManifest>(File.ReadAllText(target+"/public-manifest.json")).buildId==id));
                if(!useExisting) {if(AssetDatabase.IsValidFolder(target)) AssetDatabase.DeleteAsset(target);AssetDatabase.CreateFolder(Folder,"build-"+index.ToString("D3"));}
                var copy=Object.Instantiate(sourceRoot);copy.SetActive(false);
                try
                {
                    using(var context=GuardBuildContext.CreateReproducible(id,seed))
                    {
                        var copyRenderers=copy.GetComponentsInChildren<MeshRenderer>(true);var sample=new Sample{id=id,key=context.RuntimeKey(),prefab=target+"/fixture.prefab",shaders=new string[2],meshHashes=new string[2],publicManifest=target+"/public-manifest.json"};
                        var manifest=new PublicManifest{buildId=id,bindings=new PublicBinding[2]};
                        for(int b=0;b<2;b++) using(var codec=context.CreateCodec(bindings[b]))
                        {
                            var plan=codec.Plan(source,.15f);var encoded=codec.Encode(source,plan,sample.key);context.RecordPlan(bindings[b],plan);
                            string bindingFolder=target+"/binding-"+b;Material mat;Shader shader;
                            if(useExisting)
                            {
                                var saved=AssetDatabase.LoadAssetAtPath<Mesh>(bindingFolder+"/encoded.asset");
                                try {Check(saved && MeshHash(saved)==MeshHash(encoded),"build "+index+" binding "+b+" saved encoding matches current private-context codec");}
                                finally {Object.DestroyImmediate(encoded);}
                                encoded=saved;mat=AssetDatabase.LoadAssetAtPath<Material>(bindingFolder+"/protected.mat");shader=mat?mat.shader:null;
                                Check(shader && shader.name=="LinuxAvatarGuard/"+id+"/"+bindings[b].StableId+"/lilToon","build "+index+" binding "+b+" retains its correct generated shader namespace");
                            }
                            else
                            {
                                AssetDatabase.CreateFolder(target,"binding-"+b);AssetDatabase.CreateFolder(bindingFolder,"Shaders");AssetDatabase.CreateAsset(encoded,bindingFolder+"/encoded.asset");
                                var generator=new GuardShaders(bindingFolder+"/Shaders",id+"/"+bindings[b].StableId,codec.EmitDecoder(plan));shader=generator.Copy(originalMaterial.shader);
                                mat=new Material(originalMaterial){shader=shader};AssetDatabase.CreateAsset(mat,bindingFolder+"/protected.mat");
                            }
                            copyRenderers[b].GetComponent<MeshFilter>().sharedMesh=encoded;copyRenderers[b].sharedMaterial=mat;
                            for(int pass=0;pass<mat.passCount;pass++) ShaderUtil.CompilePass(mat,pass,true);
                            Check(!ShaderUtil.ShaderHasError(shader), "build "+index+" binding "+b+" shader compiles");
                            Check(Enumerable.Range(0,4).All(k=>mat.GetFloat(GuardShaders.Property(k))==0), "build "+index+" binding "+b+" saved material contains zero unlock values");
                            sample.shaders[b]=AssetDatabase.GetAssetPath(shader);sample.meshHashes[b]=MeshHash(encoded);meshes.Add(sample.meshHashes[b]);
                            var parser=new LAGAdaptiveShaderDecoder(File.ReadAllText(sample.shaders[b]));bodies.Add(HashText(parser.Body));constants.Add(HashText(parser.Constants));sequences.Add(parser.Sequence);
                            string program=StaticPolymorphicCodecV1.ProgramHash(plan);programs.Add(program);manifest.bindings[b]=new PublicBinding{bindingId=bindings[b].StableId,programHash=program};
                        }
                        if(!useExisting) {PrefabUtility.SaveAsPrefabAsset(copy,sample.prefab);File.WriteAllText(sample.publicManifest,JsonUtility.ToJson(manifest,true));AssetDatabase.ImportAsset(sample.publicManifest,ImportAssetOptions.ForceSynchronousImport);}
                        else Check(File.ReadAllText(sample.publicManifest)==JsonUtility.ToJson(manifest,true),"build "+index+" saved manifest exactly matches reproduced context/identity/program");
                        var privatePath=context.SavePrivate();var dependencies=AssetDatabase.GetDependencies(new[]{sample.prefab,sample.publicManifest},true);
                        Check(!dependencies.Contains(Folder+"/original.asset") && !dependencies.Contains(Folder+"/original.mat") && !dependencies.Contains(Folder+"/original.prefab") &&
                              dependencies.All(p=>!p.EndsWith(".lagprivate") && !p.Contains("key.json")) && !dependencies.Contains(privatePath), "build "+index+" dependency inventory excludes original geometry/material/prefab and private context");
                        var publicText=File.ReadAllText(sample.publicManifest);
                        Check(!publicText.Contains(Convert.ToBase64String(seed)) && !publicText.Contains("masterSeed") && !publicText.Contains(privateRoot), "build "+index+" public manifest excludes master seed and private path");
                        using(var restored=GuardBuildContext.LoadPrivate(id)) using(var replay=restored.CreateCodec(bindings[0]))
                        {
                            var mesh=replay.Encode(source,replay.Plan(source,.15f),restored.RuntimeKey());
                            try {Check(MeshHash(mesh)==sample.meshHashes[0], "build "+index+" private restore reproduces mesh");} finally {Object.DestroyImmediate(mesh);}
                        }
                        // UsePass name references are not returned by AssetDatabase.GetDependencies: include providers explicitly.
                        var passShaders=AssetDatabase.GetAllAssetPaths().Where(p=>p.StartsWith(target+"/",StringComparison.Ordinal)&&p.EndsWith(".shader")).ToArray();
                        samples.Add(sample);bundleBuilds.Add(new AssetBundleBuild{assetBundleName="owned-build-"+index.ToString("D3")+".bundle",assetNames=new[]{sample.prefab,sample.publicManifest}.Concat(passShaders).ToArray()});
                    }
                }
                finally {Array.Clear(seed,0,seed.Length);Object.DestroyImmediate(copy);}
                watch.Stop();samples[index].generationSeconds=resume?-1:watch.Elapsed.TotalSeconds;
                if((index+1)%10==0) Debug.Log("LAG_DIVERSITY_PROGRESS "+(resume?"verified existing ":"generated ")+(index+1)+"/"+Builds);
            }
            AssetDatabase.SaveAssets();Directory.CreateDirectory(Output+"/linux-bundles");var bundleWatch=Stopwatch.StartNew();
            LAGDiversityVariantFilter.Before=0;LAGDiversityVariantFilter.After=0;LAGDiversityVariantFilter.Enabled=true;
            // Own test player target only; restoration in finally leaves the validation project's settings intact.
            bool oldAuto=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64);var oldApis=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneLinux64);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,new[]{GraphicsDeviceType.Vulkan});
            try
            {
            if(rebuild)
            {
                var compiled=BuildPipeline.BuildAssetBundles(Output+"/linux-bundles",bundleBuilds.ToArray(),BuildAssetBundleOptions.ForceRebuildAssetBundle|BuildAssetBundleOptions.StrictMode,BuildTarget.StandaloneLinux64);
                bundleWatch.Stop();bundleSeconds=bundleWatch.Elapsed.TotalSeconds;
                Check(compiled!=null && compiled.GetAllAssetBundles().Length==Builds,"100 Linux fixture AssetBundles are actually built");
                Debug.Log("LAG_DIVERSITY_LINUX_BUNDLES_BUILT 100");
            }
            else
            {
                bundleWatch.Stop();bundleSeconds=-1;
                Check(bundleBuilds.All(b=>File.Exists(Path.GetFullPath(Output+"/linux-bundles/"+b.assetBundleName))),"replay reuses all 100 previously built Linux fixture bundles");
            }
            var cameraObject=new GameObject("OwnedDiversityCamera");var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;camera.orthographicSize=1.5f;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.04f,.05f,.07f);
            try
            {
                for(int index=0;index<Builds;index++)
                {
                    var sample=samples[index];var path=Output+"/linux-bundles/owned-build-"+index.ToString("D3")+".bundle";sample.bundleHash=FileHash(path);sample.bundleBytes=new FileInfo(path).Length;
                    var bundle=AssetBundle.LoadFromFile(Path.GetFullPath(path));GameObject instance=null;
                    try
                    {
                        Check(bundle!=null,"build "+index+" bundle reopens in native Linux Unity");
                        var names=bundle.GetAllAssetNames();Check(names.Contains(sample.prefab.ToLowerInvariant()) && names.Contains(sample.publicManifest.ToLowerInvariant()) &&
                            !names.Contains((Folder+"/original.asset").ToLowerInvariant()) && names.All(n=>!n.EndsWith(".lagprivate") && !n.Contains("key.json")),"build "+index+" bundle asset inventory excludes original mesh and private context");
                        // Load providers before their facade shaders/materials, for Unity's UsePass name lookup.
                        foreach(var name in names.Where(n=>n.EndsWith(".shader")).OrderByDescending(n=>n,StringComparer.Ordinal))
                            Check(bundle.LoadAsset<Shader>(name)!=null,"build "+index+" loads explicit UsePass shader asset");
                        var prefab=bundle.LoadAsset<GameObject>(sample.prefab);Check(prefab!=null,"build "+index+" loads its protected prefab");
                        var rs=prefab.GetComponentsInChildren<MeshRenderer>(true);Check(rs.Length==2,"build "+index+" retains two renderer bindings");
                        Check(rs.All(r=>r.sharedMaterial.shader.isSupported),"build "+index+" bundle shaders are supported on native Vulkan");
                        var decoder=new LAGAdaptiveShaderDecoder(File.ReadAllText(sample.shaders[0]));
                        if(index==0) buildZeroDecoder=decoder;
                        for(int b=0;b<2;b++)
                        {
                            var mesh=rs[b].GetComponent<MeshFilter>().sharedMesh;var adaptive=new LAGAdaptiveShaderDecoder(File.ReadAllText(sample.shaders[b]));
                            Check(MeshHash(mesh)==sample.meshHashes[b],"build "+index+" binding "+b+" bundle preserves exact encoded mesh");
                            float error=LAGAdaptiveShaderDecoder.MaxError(adaptive.Decode(mesh,sample.key),originalPositions);worstError=Mathf.Max(worstError,error);
                            Check(error<1e-5f,"build "+index+" binding "+b+" adaptive HLSL decoder reconstructs bundle mesh");recovered++;
                            minMissing=Mathf.Min(minMissing,LAGAdaptiveShaderDecoder.Rms(adaptive.Decode(mesh,new int[4]),originalPositions));
                            minWrong=Mathf.Min(minWrong,LAGAdaptiveShaderDecoder.Rms(adaptive.Decode(mesh,new[]{1,2,3,4}),originalPositions));
                        }
                        float cross=LAGAdaptiveShaderDecoder.Rms(decoder.Decode(rs[1].GetComponent<MeshFilter>().sharedMesh,sample.key),originalPositions);minCrossMesh=Mathf.Min(minCrossMesh,cross);crossMesh++;
                        Check(cross>.02f,"build "+index+" Body decoder fails on Hair with the correct shared runtime values");
                        if(index>0)
                        {cross=LAGAdaptiveShaderDecoder.Rms(buildZeroDecoder.Decode(rs[0].GetComponent<MeshFilter>().sharedMesh,sample.key),originalPositions);minCrossBuild=Mathf.Min(minCrossBuild,cross);crossBuild++;Check(cross>.02f,"build "+index+" rejects unadapted build zero decoder with this build's correct runtime values");}
                        if(new[]{0,33,66,99}.Contains(index))
                        {
                            instance=Object.Instantiate(prefab);instance.SetActive(true);var visible=instance.GetComponentsInChildren<MeshRenderer>(true);
                            for(int view=0;view<3;view++)
                            {
                                camera.transform.position=Quaternion.Euler(0,view*120,0)*new Vector3(0,0,-4);camera.transform.LookAt(Vector3.zero);
                                instance.SetActive(false);sourceRoot.SetActive(true);var reference=Render(camera,Output+"/sample-"+index+"-view-"+view+"-original.png");sourceRoot.SetActive(false);instance.SetActive(true);
                                foreach(var r in visible) Key(r,sample.key);var unlocked=Render(camera,Output+"/sample-"+index+"-view-"+view+"-unlocked.png");
                                foreach(var r in visible) Key(r,new int[4]);var locked=Render(camera,Output+"/sample-"+index+"-view-"+view+"-locked.png");
                                float difference=Difference(reference,unlocked);worstVisual=Mathf.Max(worstVisual,difference);minLocked=Mathf.Min(minLocked,Difference(reference,locked));
                                Check(difference<.002f && Difference(reference,locked)>.005f,"bundle build "+index+" Vulkan render preserves unlocked appearance and deforms without values at view "+view);
                            }
                        }
                    }
                    finally {if(instance) Object.DestroyImmediate(instance);if(bundle) bundle.Unload(true);}
                    if((index+1)%10==0) Debug.Log("LAG_DIVERSITY_PROGRESS replayed "+(index+1)+"/"+Builds);
                }
            }
            finally {Object.DestroyImmediate(cameraObject);}
            Check(programs.Count==Builds*2 && bodies.Count==Builds*2 && meshes.Count==Builds*2,"200 binding programs, decoder bodies and encoded meshes are unique");
            Check(minMissing>.02f && minWrong>.02f && recovered==Builds*2,"absent/wrong values fail while adaptive shader parsing recovers every binding");
            var shaderPaths=AssetDatabase.GetAllAssetPaths().Where(p=>p.StartsWith(Folder+"/build-")&&p.EndsWith(".shader")).ToArray();
            Check(shaderPaths.All(p=>!ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(p))),"all generated shaders remain error free after bundle replay");
            Check(protectedSourcePaths.Select(FileHash).SequenceEqual(sourceHashes),"original source mesh/material/texture/prefab files remain byte identical");
            Directory.CreateDirectory(Output+"/windows-samples");var selected=bundleBuilds.Where((b,i)=>new[]{0,33,66,99}.Contains(i)).ToArray();
            var windows=BuildPipeline.BuildAssetBundles(Output+"/windows-samples",selected,BuildAssetBundleOptions.ForceRebuildAssetBundle|BuildAssetBundleOptions.StrictMode,BuildTarget.StandaloneWindows64);
            Check(windows!=null && windows.GetAllAssetBundles().Length==4,"four sampled Windows64 fixture bundles build successfully");
            File.WriteAllText(Output+"/validation.json",JsonUtility.ToJson(new Report{checks=checks.ToArray(),fixture="owned two-renderer asymmetric static surface; public deterministic fixture seeds",
                graphics=SystemInfo.graphicsDeviceType.ToString(),scope="100 static synthetic prefab builds, not processed VRChat avatars; adaptive attack uses generated HLSL source and provided public fixture runtime values; unlit mono variant subset",
                buildCount=Builds,bindingCount=Builds*2,linuxBundles=100,windowsSampleBundles=4,generatedShaders=shaderPaths.Length,uniquePrograms=programs.Count,uniqueDecoderBodies=bodies.Count,
                uniqueConstants=constants.Count,uniqueSequences=sequences.Count,uniqueLayouts=1,uniqueEncodedMeshes=meshes.Count,uniqueBundleHashes=samples.Select(s=>s.bundleHash).Distinct().Count(),
                adaptiveRecoveredBindings=recovered,crossBuildAttempts=crossBuild,crossMeshAttempts=crossMesh,worstAdaptiveMaxError=worstError,minCrossBuildRms=minCrossBuild,minCrossMeshRms=minCrossMesh,
                minMissingKeyRms=minMissing,minWrongKeyRms=minWrong,worstUnlockedImageDifference=worstVisual,minLockedImageDifference=minLocked,totalGenerationSeconds=resume?-1:samples.Sum(s=>s.generationSeconds),bundleBuildSeconds=bundleSeconds,
                totalBundleBytes=samples.Sum(s=>s.bundleBytes),originalFilesUnchanged=true,privateContextAbsentFromBundles=true,finalSdkAvatarBundleTested=false,
                resumedExistingFixtures=resume,fixtureVariantsBeforeFilter=LAGDiversityVariantFilter.Before,fixtureVariantsAfterFilter=LAGDiversityVariantFilter.After,
                builds=samples.Select(s=>new BuildResult{buildId=s.id,bundleHash=s.bundleHash,bundleBytes=s.bundleBytes,generationSeconds=s.generationSeconds,adaptiveExtracted=true}).ToArray()},true));
            Debug.Log("LAG_DIVERSITY_VALIDATION_SUCCESS "+checks.Count+" checks; 100 Linux bundles, 4 Windows samples, 200 adaptive recoveries");
            }
            finally {LAGDiversityVariantFilter.Enabled=false;PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,oldApis);PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,oldAuto);}
        }
        finally
        {
            if(sourceRoot) Object.DestroyImmediate(sourceRoot);Environment.SetEnvironmentVariable("XDG_DATA_HOME",oldXdg);
            if(Directory.Exists(privateRoot)) Directory.Delete(privateRoot,true);
            foreach(var sample in samples) Array.Clear(sample.key,0,sample.key.Length);
        }
    }
}
