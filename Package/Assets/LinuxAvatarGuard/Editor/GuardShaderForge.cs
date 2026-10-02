// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LinuxAvatarGuard
{
    public sealed class GuardShaderArtifact : IDisposable
    {
        public GameObject Root { get; }
        public string Folder { get; }
        public string PrefabPath { get; }
        public string PublicManifestPath { get; }
        public ReadOnlyCollection<string> ShaderAssetPaths { get; }
        public ReadOnlyCollection<string> ProviderAssetPaths { get; }
        readonly Dictionary<AnimationClip,AnimationClip> clips;
        internal GuardShaderArtifact(GameObject root,string folder,string[] shaders,string[] providers,Dictionary<AnimationClip,AnimationClip> clips)
        {
            Root=root;Folder=folder;PrefabPath=folder+"/fixture.prefab";PublicManifestPath=folder+"/public-manifest.json";
            ShaderAssetPaths=Array.AsReadOnly(shaders);ProviderAssetPaths=Array.AsReadOnly(providers);
            this.clips=new Dictionary<AnimationClip,AnimationClip>(clips);
        }
        public AnimationClip CopyOf(AnimationClip source) => clips.TryGetValue(source,out var clip) ? clip : throw new ArgumentException("Clip ajeno al artefacto.");
        // Only the temporary scene instance is disposed; generated assets remain reviewable.
        public void Dispose() { if(Root)Object.DestroyImmediate(Root); }
    }

    // Explicit research API. The avatar wizard and GuardBuilder keep the proven legacy route.
    public static class GuardShaderForge
    {
        public const int Version=1;
        sealed class BindingWork : IDisposable
        {
            public Renderer Source;
            public MeshBindingIdentity Binding;
            public StaticPolymorphicCodecV1 Codec;
            public CodecPlan Plan;
            public Mesh Encoded;
            public GuardShaders Shaders;
            public string Path,Folder;
            public void Dispose(){Codec?.Dispose();if(Encoded&&!AssetDatabase.Contains(Encoded))Object.DestroyImmediate(Encoded);}
        }
        [Serializable] sealed class PublicBinding
        {
            public string bindingId,programHash,usageHash,mesh;
            public int firstUv,secondUv,attributePolicy;
        }
        [Serializable] sealed class PublicManifest
        {
            public int forgeVersion,codecVersion;
            public string buildId,codecId,animationHash,status;
            public bool sdkProcessed=false,skinningValidated=false,texturesProtected=false;
            public PublicBinding[] bindings;
        }
        static string Digest(string value)=>MeshBindingIdentity.Digest(Encoding.UTF8.GetBytes(value));
        static string MaterialKey(BindingWork w,int slot,Material material) => w.Binding.StableId+"/"+slot+"/"+GuardAnimationContext.Identity(material)+"/"+
            StaticPolymorphicCodecV1.ProgramHash(w.Plan)+"/"+w.Plan.Program.Attributes.FirstUvChannel+"/"+w.Plan.Program.Attributes.SecondUvChannel+"/"+GuardAnimationContext.Identity(material.shader);
        static void OutputPath(string folder)
        {
            if(folder==null || !Regex.IsMatch(folder,@"^Assets/LinuxAvatarGuardGenerated/Research/[A-Za-z0-9_-]{1,64}$"))
                throw new ArgumentException("La salida de investigación debe ser una carpeta nueva en Assets/LinuxAvatarGuardGenerated/Research/.");
            if(Directory.Exists(folder)||File.Exists(folder)||File.Exists(folder+".meta"))throw new InvalidOperationException("La carpeta de salida ya existe; no se sobrescribe.");
            for(string parent=Path.GetDirectoryName(Path.GetFullPath(folder));!string.IsNullOrEmpty(parent);parent=Path.GetDirectoryName(parent))
                if((Directory.Exists(parent)||File.Exists(parent))&&(File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidOperationException("La ruta de salida contiene un enlace simbólico.");
        }
        static void Components(GameObject root)
        {
            foreach(var c in root.GetComponentsInChildren<Component>(true))
                if(!c || !(c is Transform||c is MeshFilter||c is MeshRenderer||c is Animator))
                    throw new InvalidOperationException("ShaderForge contextual inicial: solo Transform, MeshFilter, MeshRenderer y Animator genérico revisados.");
            foreach(var t in root.GetComponentsInChildren<Transform>(true))
            {
                if(t.name.Contains("/"))throw new InvalidOperationException("Nombre con separador de ruta de animación.");
                if(t.GetComponents<MeshRenderer>().Length>1||t.GetComponents<MeshFilter>().Length>1)
                    throw new InvalidOperationException("Componentes de geometría duplicados.");
                var filter=t.GetComponent<MeshFilter>();
                if(filter && !t.GetComponent<MeshRenderer>())throw new InvalidOperationException("MeshFilter sin binding de renderer.");
            }
        }
        public static GuardShaderArtifact Prepare(GameObject sourceRoot,GuardBuildContext context,string outputFolder,float strength=.15f)
        {
            if(!sourceRoot||context==null)throw new ArgumentNullException("Fuente/contexto");
            OutputPath(outputFolder);Components(sourceRoot);
            var animation=GuardAnimationContext.Capture(sourceRoot);
            var work=new List<BindingWork>();GameObject copy=null;bool owned=false;
            var materials=new Dictionary<string,Material>();var clips=new Dictionary<AnimationClip,AnimationClip>();
            try
            {
                // Every renderer is analyzed and encoded before the first output asset is written.
                foreach(var renderer in sourceRoot.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var w=new BindingWork {Source=renderer,Binding=MeshBindingIdentity.Capture(sourceRoot,renderer),Path=AnimationUtility.CalculateTransformPath(renderer.transform,sourceRoot.transform)};
                    work.Add(w);w.Codec=context.CreateContextualCodec(w.Binding,animation);w.Plan=w.Codec.Plan(w.Binding.Source,strength);
                    w.Encoded=w.Codec.Encode(w.Binding.Source,w.Plan,context.RuntimeKey());
                }
                animation.Validate();
                Directory.CreateDirectory(outputFolder);owned=true;AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                copy=Object.Instantiate(sourceRoot);copy.name="LAG_"+context.BuildId;copy.SetActive(false);
                if(PrefabUtility.IsPartOfPrefabInstance(copy))PrefabUtility.UnpackPrefabInstance(copy,PrefabUnpackMode.Completely,InteractionMode.AutomatedAction);
                foreach(var w in work)
                {
                    w.Folder=outputFolder+"/b_"+w.Binding.StableId.Substring(0,32);Directory.CreateDirectory(w.Folder);
                    string program=StaticPolymorphicCodecV1.ProgramHash(w.Plan);
                    w.Shaders=new GuardShaders(w.Folder,context.BuildId+"/"+w.Binding.StableId.Substring(0,16)+program.Substring(0,16),w.Codec.EmitDecoder(w.Plan),true);
                    w.Encoded.name="m_"+program.Substring(0,32);AssetDatabase.CreateAsset(w.Encoded,w.Folder+"/mesh.asset");
                    var renderer=GuardAnimationContext.Resolve(copy,w.Path).GetComponent<MeshRenderer>();
                    renderer.GetComponent<MeshFilter>().sharedMesh=w.Encoded;
                    for(int slot=0;slot<w.Source.sharedMaterials.Length;slot++)foreach(var source in animation.Materials(w.Source,slot))
                    {
                        string key=MaterialKey(w,slot,source),token=Digest(key);
                        var material=new Material(source){name="m_"+token.Substring(0,32),shader=w.Shaders.Copy(source.shader),hideFlags=HideFlags.None};
                        material.renderQueue=source.renderQueue;
                        for(int i=0;i<4;i++)material.SetFloat(GuardShaders.Property(i),0);
                        AssetDatabase.CreateAsset(material,w.Folder+"/m_"+token+".mat");materials.Add(key,material);
                    }
                    renderer.sharedMaterials=w.Source.sharedMaterials.Select((m,s)=>materials[MaterialKey(w,s,m)]).ToArray();
                }
                Func<AnimationClip,AnimationClip> copyClip=original=>
                {
                    if(clips.TryGetValue(original,out var existing))return existing;
                    if(!animation.Clips.Contains(original))throw new InvalidOperationException("Clip fuera del contexto revisado.");
                    var clip=Object.Instantiate(original);clip.name="c_"+Digest(GuardAnimationContext.Identity(original)).Substring(0,32);clip.hideFlags=HideFlags.None;
                    foreach(var binding in AnimationUtility.GetObjectReferenceCurveBindings(original))
                    {
                        var sourceRenderer=animation.RendererAt(binding);var w=work.Single(b=>b.Source==sourceRenderer);int slot=GuardAnimationContext.MaterialSlot(binding);
                        var frames=AnimationUtility.GetObjectReferenceCurve(original,binding);
                        for(int i=0;i<frames.Length;i++)frames[i].value=materials[MaterialKey(w,slot,(Material)frames[i].value)];
                        AnimationUtility.SetObjectReferenceCurve(clip,binding,frames);
                    }
                    AssetDatabase.CreateAsset(clip,outputFolder+"/"+clip.name+".anim");clips.Add(original,clip);return clip;
                };
                // Build all clips, including overridden base clips; controller references share the same contextual map.
                foreach(var clip in animation.Clips)copyClip(clip);
                if(animation.Animator)
                {
                    var graph=new GuardAssets(outputFolder,work[0].Shaders,copyClip);
                    copy.GetComponent<Animator>().runtimeAnimatorController=(RuntimeAnimatorController)graph.Copy(animation.Controller);
                }
                foreach(var path in work.SelectMany(w=>w.Shaders.GeneratedAssetPaths))
                {
                    var shader=AssetDatabase.LoadAssetAtPath<Shader>(path);
                    if(!shader||!shader.isSupported||ShaderUtil.ShaderHasError(shader))throw new InvalidOperationException("Shader generado sin compilación/soporte Vulkan: "+path);
                }
                foreach(string path in AssetDatabase.FindAssets("",new[]{outputFolder}).Select(AssetDatabase.GUIDToAssetPath).Distinct())
                {
                    foreach(var asset in AssetDatabase.LoadAllAssetsAtPath(path))if(asset)AssetDatabase.SaveAssetIfDirty(asset);
                }
                copy.SetActive(sourceRoot.activeSelf);
                if(!PrefabUtility.SaveAsPrefabAsset(copy,outputFolder+"/fixture.prefab"))throw new InvalidOperationException("No se pudo guardar el prefab generado.");
                var manifest=new PublicManifest {forgeVersion=Version,codecVersion=StaticPolymorphicCodecV1.Version,codecId=StaticPolymorphicCodecV1.Id,
                    buildId=context.BuildId,animationHash=animation.Fingerprint,status="research-rigid-context-only",
                    bindings=work.Select(w=>new PublicBinding{bindingId=w.Binding.StableId,programHash=StaticPolymorphicCodecV1.ProgramHash(w.Plan),usageHash=w.Plan.AttributeUsageHash,
                        mesh=w.Folder+"/mesh.asset",firstUv=w.Plan.Program.Attributes.FirstUvChannel,secondUv=w.Plan.Program.Attributes.SecondUvChannel,attributePolicy=w.Plan.Program.Attributes.PolicyVersion}).ToArray()};
                File.WriteAllText(outputFolder+"/public-manifest.json",JsonUtility.ToJson(manifest,true));AssetDatabase.ImportAsset(outputFolder+"/public-manifest.json",ImportAssetOptions.ForceSynchronousImport);
                animation.Validate();foreach(var w in work)w.Codec.Validate(w.Binding.Source,w.Plan);
                var artifact=new GuardShaderArtifact(copy,outputFolder,work.SelectMany(w=>w.Shaders.GeneratedAssetPaths).Distinct().OrderBy(p=>p,StringComparer.Ordinal).ToArray(),
                    work.SelectMany(w=>w.Shaders.ProviderAssetPaths).Distinct().OrderBy(p=>p,StringComparer.Ordinal).ToArray(),clips);
                context.RecordPlans(work.Select(w=>Tuple.Create(w.Binding,w.Plan)),animation);
                return artifact;
            }
            catch
            {
                if(copy)Object.DestroyImmediate(copy);
                if(owned)
                {
                    AssetDatabase.DeleteAsset(outputFolder);
                    if(Directory.Exists(outputFolder))Directory.Delete(outputFolder,true);
                    if(File.Exists(outputFolder+".meta"))File.Delete(outputFolder+".meta");
                }
                throw;
            }
            finally{foreach(var w in work)w.Dispose();}
        }
    }
}
