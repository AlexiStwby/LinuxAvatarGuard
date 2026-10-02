// SPDX-License-Identifier: MIT
// Stage 9: author/build ONLY owned fixtures in the disposable ValidationProject.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using LinuxAvatarGuard;
using Object = UnityEngine.Object;
using Debug = UnityEngine.Debug;

public static class LAGPerformanceValidation
{
    const string Folder = "Assets/PerformanceFixture";
    const string ForgeFolder = "Assets/LinuxAvatarGuardGenerated/Research/PerformanceFixture";
    static readonly List<string> checks = new List<string>();
    [Serializable] sealed class Manifest
    {
        public string scope = "owned-procedural-rigid-liltoon-fixture", unity, liltoon, graphics;
        public bool originalFilesUnchanged, sourceShadersUnchanged, sdkProcessed = false, skinningValidated = false;
        public int verticesPerInstance, trianglesPerInstance, renderersPerInstance;
        public int[] publicFixtureUnlockValues; public Variant[] variants; public string[] checks;
    }
    [Serializable] sealed class Variant
    {
        public string id, bundle, prefab; public string[] providers, shaders;
        public int meshes, materials, shaderAssets, variantsBeforeFilter, variantsAfterFilter;
        public long bundleBytes, sourceAssetBytes, vertexIndexBytes;
        public double prepareSeconds, bundleBuildSeconds;
    }
    static void Check(bool condition, string description)
    { if (!condition) throw new Exception("PERFORMANCE_FIXTURE_FAILED: " + description); checks.Add(description); }
    static string Hash(string path) { using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant(); }
    static Dictionary<string,string> Snapshot(string folder) => Directory.GetFiles(folder,"*",SearchOption.AllDirectories).Where(p=>!p.EndsWith(".meta")).ToDictionary(p=>p,Hash);
    static Mesh Mesh()
    {
        const int around = 128, vertical = 96;
        var vertices = new Vector3[(around+1)*(vertical+1)]; var uv = new Vector2[vertices.Length]; var indices = new List<int>();
        for(int y=0;y<=vertical;y++)for(int x=0;x<=around;x++)
        {
            int n=y*(around+1)+x; float a=x*2*Mathf.PI/around,b=.03f+y*(Mathf.PI-.06f)/vertical;
            float r=Mathf.Sin(b)*(.5f+.08f*Mathf.Cos(b));
            vertices[n]=new Vector3(r*Mathf.Cos(a),Mathf.Cos(b)*.65f,r*Mathf.Sin(a)*.75f+.06f*Mathf.Cos(a)*Mathf.Cos(a));
            uv[n]=new Vector2((float)x/around,(float)y/vertical);
            if(x<around&&y<vertical)indices.AddRange(new[]{n,n+around+1,n+1,n+1,n+around+1,n+around+2});
        }
        var mesh=new Mesh{name="OwnedDenseSurface",vertices=vertices,uv=uv,triangles=indices.ToArray()};
        mesh.RecalculateNormals();mesh.RecalculateTangents();mesh.RecalculateBounds();return mesh;
    }
    static GameObject Original()
    {
        var mesh=Mesh();AssetDatabase.CreateAsset(mesh,Folder+"/source.asset");
        var root=new GameObject("OwnedPerformanceRoot");root.SetActive(false);
        for(int i=0;i<2;i++)
        {
            var material=new Material(Shader.Find("lilToon")){name="OwnedMaterial"+i,enableInstancing=false};
            material.SetFloat("_AsUnlit",1);material.SetFloat("_Cull",2);material.SetColor("_Color",i==0?new Color(.25f,.65f,.9f,1):new Color(.8f,.3f,.5f,1));
            AssetDatabase.CreateAsset(material,Folder+"/material-"+i+".mat");AssetDatabase.SaveAssetIfDirty(material);
            var child=new GameObject(i==0?"Body":"Hair");child.transform.SetParent(root.transform,false);child.transform.localPosition=new Vector3(i==0?-.5f:.5f,0,0);
            child.AddComponent<MeshFilter>().sharedMesh=mesh;var renderer=child.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;
            renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
        }
        AssetDatabase.SaveAssetIfDirty(mesh);PrefabUtility.SaveAsPrefabAsset(root,Folder+"/original.prefab");return root;
    }
    static Variant Describe(string id,string prefab,IEnumerable<string> shaders,IEnumerable<string> providers,GameObject root,double seconds)
    {
        var meshes=root.GetComponentsInChildren<MeshFilter>(true).Select(f=>f.sharedMesh).Distinct().ToArray();
        var materials=root.GetComponentsInChildren<MeshRenderer>(true).SelectMany(r=>r.sharedMaterials).Distinct().ToArray();
        var dependencies=AssetDatabase.GetDependencies(prefab,true).Where(p=>p.StartsWith(Folder+"/")||p.StartsWith(ForgeFolder+"/")).Distinct();
        return new Variant{id=id,prefab=prefab.ToLowerInvariant(),bundle="bundles/"+id+".bundle",providers=providers.Select(p=>p.ToLowerInvariant()).ToArray(),shaders=shaders.Select(p=>p.ToLowerInvariant()).ToArray(),
            meshes=meshes.Length,materials=materials.Length,shaderAssets=shaders.Count(),prepareSeconds=seconds,
            sourceAssetBytes=dependencies.Where(File.Exists).Sum(p=>new FileInfo(p).Length),
            vertexIndexBytes=meshes.Sum(m=>Enumerable.Range(0,m.vertexBufferCount).Sum(b=>(long)m.GetVertexBufferStride(b)*m.vertexCount)+Enumerable.Range(0,m.subMeshCount).Sum(s=>(long)m.GetIndexCount(s)*(m.indexFormat==IndexFormat.UInt16?2:4)))};
    }
    public static void Run()
    {
        LAGBindingValidation.RequireResearchProject();checks.Clear();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        string output=Environment.GetEnvironmentVariable("LAG_PERFORMANCE_OUTPUT");
        if(string.IsNullOrEmpty(output)||!Path.IsPathRooted(output))throw new ArgumentException("Absolute evidence directory required.");
        Directory.CreateDirectory(Path.Combine(output,"bundles"));
        if(AssetDatabase.IsValidFolder(Folder))AssetDatabase.DeleteAsset(Folder);AssetDatabase.CreateFolder("Assets","PerformanceFixture");
        if(AssetDatabase.IsValidFolder(ForgeFolder))AssetDatabase.DeleteAsset(ForgeFolder);
        var original=Original();var originals=Snapshot(Folder);string liltoon=Path.GetDirectoryName(Path.GetDirectoryName(AssetDatabase.GetAssetPath(Shader.Find("lilToon"))));var lilFiles=Snapshot(liltoon);
        var objects=new List<Object>{original};var timer=new Stopwatch();GuardShaderArtifact artifact=null;
        var oldApi=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneLinux64);bool oldAuto=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64);
        using(var context=GuardBuildContext.CreateReproducible(LAGBindingValidation.PublicBuildId,LAGBindingValidation.PublicSeed))
        try
        {
            int[] key=context.RuntimeKey();var root=Object.Instantiate(original);objects.Add(root);
            AssetDatabase.CreateFolder(Folder,"legacy");AssetDatabase.CreateFolder(Folder+"/legacy","shaders");timer.Start();
            var shaders=new GuardShaders(Folder+"/legacy/shaders","owned-performance-legacy");
            var encoded=GuardMesh.Encode(original.GetComponentInChildren<MeshFilter>(true).sharedMesh,key,.15f);AssetDatabase.CreateAsset(encoded,Folder+"/legacy/mesh.asset");
            var materials=new Dictionary<Material,Material>();
            foreach(var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.GetComponent<MeshFilter>().sharedMesh=encoded;var source=renderer.sharedMaterial;
                if(!materials.TryGetValue(source,out var material)){material=new Material(source){shader=shaders.Copy(source.shader)};AssetDatabase.CreateAsset(material,Folder+"/legacy/material-"+materials.Count+".mat");materials.Add(source,material);AssetDatabase.SaveAssetIfDirty(material);}
                renderer.sharedMaterial=material;
            }
            AssetDatabase.SaveAssetIfDirty(encoded);PrefabUtility.SaveAsPrefabAsset(root,Folder+"/legacy/legacy.prefab");timer.Stop();double legacySeconds=timer.Elapsed.TotalSeconds;
            timer.Restart();artifact=GuardShaderForge.Prepare(original,context,ForgeFolder,.15f);timer.Stop();double forgeSeconds=timer.Elapsed.TotalSeconds;
            var originalPaths=new[]{AssetDatabase.GetAssetPath(Shader.Find("lilToon")),AssetDatabase.GetAssetPath(Shader.Find("Hidden/ltspass_opaque"))};
            Check(originalPaths.All(p=>!string.IsNullOrEmpty(p)),"original facade and exact UsePass provider are explicit bundle assets");
            var report=new Manifest{unity=Application.unityVersion,liltoon="2.3.4",graphics=SystemInfo.graphicsDeviceName,
                publicFixtureUnlockValues=key,verticesPerInstance=encoded.vertexCount*2,trianglesPerInstance=encoded.triangles.Length/3*2,renderersPerInstance=2};
            report.variants=new[]{Describe("original",Folder+"/original.prefab",originalPaths,originalPaths.Where(p=>File.ReadAllText(p).Contains("Shader \"Hidden/ltspass_")),original,0),
                Describe("legacy",Folder+"/legacy/legacy.prefab",shaders.GeneratedAssetPaths,shaders.ProviderAssetPaths,root,legacySeconds),
                Describe("forge",artifact.PrefabPath,artifact.ShaderAssetPaths,artifact.ProviderAssetPaths,artifact.Root,forgeSeconds)};
            Check(report.variants.Select(v=>v.meshes).SequenceEqual(new[]{1,1,2}),"shared source/legacy mesh is deduplicated; contextual mesh is per binding");
            foreach(var variant in report.variants)
            {
                foreach(string path in AssetDatabase.GetDependencies(variant.prefab,true).Where(p=>p.EndsWith(".mat")))
                {var mat=AssetDatabase.LoadAssetAtPath<Material>(path);for(int k=0;k<4;k++)Check(!mat.HasProperty(GuardShaders.Property(k))||mat.GetFloat(GuardShaders.Property(k))==0,"serialized fixture unlock value is zero "+variant.id+"/"+mat.name+"/"+k);}
            }
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,new[]{GraphicsDeviceType.Vulkan});
            foreach(var variant in report.variants)
            {
                PerformanceShaderFilter.Names=new HashSet<string>(variant.shaders.Select(p=>AssetDatabase.LoadAssetAtPath<Shader>(p)?.name).Where(n=>n!=null));
                PerformanceShaderFilter.Before=0;PerformanceShaderFilter.After=0;PerformanceShaderFilter.Enabled=true;timer.Restart();
                // Each native build has the same explicitly limited mono/unlit variant policy.
                var build=BuildPipeline.BuildAssetBundles(Path.Combine(output,"bundles"),new[]{new AssetBundleBuild{assetBundleName=variant.id+".bundle",
                    assetNames=new[]{variant.prefab}.Concat(variant.shaders).Concat(AssetDatabase.GetDependencies(variant.prefab,true).Where(p=>p.EndsWith(".mat"))).Distinct().ToArray()}},
                    BuildAssetBundleOptions.ForceRebuildAssetBundle|BuildAssetBundleOptions.StrictMode,BuildTarget.StandaloneLinux64);
                timer.Stop();Check(build&&File.Exists(Path.Combine(output,variant.bundle)),"native Linux Vulkan bundle builds "+variant.id);
                variant.bundleBuildSeconds=timer.Elapsed.TotalSeconds;variant.bundleBytes=new FileInfo(Path.Combine(output,variant.bundle)).Length;
                variant.variantsBeforeFilter=PerformanceShaderFilter.Before;variant.variantsAfterFilter=PerformanceShaderFilter.After;
                PerformanceShaderFilter.Enabled=false;
            }
            report.originalFilesUnchanged=originals.All(p=>File.Exists(p.Key)&&Hash(p.Key)==p.Value);report.sourceShadersUnchanged=lilFiles.All(p=>File.Exists(p.Key)&&Hash(p.Key)==p.Value);
            Check(report.originalFilesUnchanged&&report.sourceShadersUnchanged,"source prefab/mesh/materials and all lilToon source files remain byte-identical");
            report.checks=checks.ToArray();File.WriteAllText(Path.Combine(output,"fixtures.json"),JsonUtility.ToJson(report,true));
            Debug.Log("LAG_PERFORMANCE_FIXTURE_SUCCESS "+checks.Count+" checks");
        }
        finally{PerformanceShaderFilter.Enabled=false;LAGDiversityVariantFilter.Enabled=false;PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,oldAuto);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,oldApi);artifact?.Dispose();foreach(var obj in objects)if(obj)Object.DestroyImmediate(obj);}
        // All owned assets and reports are already saved, and cleanup has completed.
        // Explicit batch exit avoids waiting on unrelated editor-package background work.
        if(Application.isBatchMode)EditorApplication.Exit(0);
    }
}

// Research-only equivalent variant reduction for BOTH baseline and generated families.
public sealed class PerformanceShaderFilter : IPreprocessShaders
{
    public static bool Enabled;public static int Before,After;public static HashSet<string> Names=new HashSet<string>();
    public int callbackOrder=>int.MaxValue;
    static readonly HashSet<string> omitted=new HashSet<string>{"LIGHTMAP_ON","DIRLIGHTMAP_COMBINED","DYNAMICLIGHTMAP_ON","LIGHTMAP_SHADOW_MIXING","SHADOWS_SHADOWMASK","STEREO_INSTANCING_ON","STEREO_MULTIVIEW_ON","UNITY_SINGLE_PASS_STEREO","INSTANCING_ON","FOG_LINEAR","FOG_EXP","FOG_EXP2","VERTEXLIGHT_ON","SHADOWS_SCREEN","SHADOWS_SOFT","SHADOWS_CUBE","POINT","POINT_COOKIE","DIRECTIONAL_COOKIE","SPOT"};
    public void OnProcessShader(Shader shader,ShaderSnippetData snippet,IList<ShaderCompilerData> data)
    {
        if(!Enabled||Environment.GetEnvironmentVariable("LAG_BINDING_AUDIT_ALLOWED")!="synthetic-unity-only"||!Names.Contains(shader.name))return;
        Before+=data.Count;var fallback=data.Where(d=>d.shaderCompilerPlatform==ShaderCompilerPlatform.Vulkan).Take(1).ToArray();
        for(int i=data.Count-1;i>=0;i--)if(data[i].shaderCompilerPlatform!=ShaderCompilerPlatform.Vulkan||data[i].shaderKeywordSet.GetShaderKeywords().Any(k=>omitted.Contains(k.name)))data.RemoveAt(i);
        if(data.Count==0&&fallback.Length==1)data.Add(fallback[0]);After+=data.Count;
    }
}
