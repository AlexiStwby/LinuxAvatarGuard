// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LinuxAvatarGuard
{
    // Bounded generic Animator contract for rigid MeshRenderers; this does not enable skinned avatars.
    public sealed class GuardAnimationContext
    {
        public const int Version = 1;
        public string Fingerprint { get; }
        public ReadOnlyCollection<AnimationClip> Clips { get; }
        public bool TextureTransformsEnabled { get; }
        public bool SkinningEnabled { get; }
        internal GameObject Root { get; }
        internal Animator Animator { get; }
        internal RuntimeAnimatorController Controller => Animator ? Animator.runtimeAnimatorController : null;
        readonly Dictionary<Renderer, HashSet<Material>[]> materials;
        readonly Dictionary<Renderer, HashSet<int>> selectors;
        GuardAnimationContext(GameObject root, Animator animator, Dictionary<Renderer,HashSet<Material>[]> mats,
            Dictionary<Renderer,HashSet<int>> ids, AnimationClip[] clips, string hash, bool textureTransforms = false, bool skinning = false)
        { Root=root; Animator=animator; materials=mats; selectors=ids; Clips=Array.AsReadOnly(clips); Fingerprint=hash; TextureTransformsEnabled=textureTransforms; SkinningEnabled=skinning; }
        public void Validate()
        {
            if (!Root || Capture(Root,TextureTransformsEnabled,SkinningEnabled).Fingerprint!=Fingerprint) throw new InvalidOperationException("Cambió el contexto de animación/materiales después del análisis.");
        }
        internal Material[] Materials(Renderer renderer) => materials[renderer].SelectMany(m=>m).Distinct().OrderBy(Identity,StringComparer.Ordinal).ToArray();
        internal Material[] Materials(Renderer renderer,int slot) => materials[renderer][slot].OrderBy(Identity,StringComparer.Ordinal).ToArray();
        internal int[] ReservedSelectors(Renderer renderer) => selectors[renderer].OrderBy(i=>i).ToArray();
        internal static string Identity(Object obj)
        {
            if (!obj || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj,out string guid,out long id) || !MeshKeyDerivation.IsHex(guid,32) || id==0)
                throw new InvalidOperationException("Dependencia contextual sin identidad persistente.");
            return guid+":"+id;
        }
        internal static Transform Resolve(GameObject root,string path)
        {
            var node=root.transform;
            if(string.IsNullOrEmpty(path)) return node;
            foreach(var segment in path.Split('/'))
            {
                var matches=Enumerable.Range(0,node.childCount).Select(i=>node.GetChild(i)).Where(t=>t.name==segment).ToArray();
                if(segment.Length==0 || matches.Length!=1) throw new InvalidOperationException("Ruta de animación ausente/ambigua: "+path);
                node=matches[0];
            }
            return node;
        }
        internal Renderer RendererAt(EditorCurveBinding binding)
        {
            var renderer=Resolve(Root,binding.path).GetComponent<Renderer>();
            if(!renderer || !materials.ContainsKey(renderer) || (binding.type!=renderer.GetType()&&binding.type!=typeof(Renderer)))
                throw new InvalidOperationException("Binding de renderer sin contexto inequívoco.");
            return renderer;
        }
        internal static int MaterialSlot(EditorCurveBinding binding)
        {
            var match=Regex.Match(binding.propertyName,@"^m_Materials\.Array\.data\[(\d+)\]$");
            if(!typeof(Renderer).IsAssignableFrom(binding.type) || !match.Success || !int.TryParse(match.Groups[1].Value,out int slot))
                throw new InvalidOperationException("Solo se admiten referencias animadas a slots de materiales.");
            return slot;
        }
        static void CleanAsset(Object obj, HashSet<string> files)
        {
            Identity(obj);string path=AssetDatabase.GetAssetPath(obj);
            if(!File.Exists(path) || AssetDatabase.LoadAllAssetsAtPath(path).Any(a=>a&&EditorUtility.IsDirty(a)))
                throw new InvalidOperationException("Guarda las dependencias de animación antes del análisis: "+path);
            files.Add(path);
        }
        static void Controllers(RuntimeAnimatorController controller,HashSet<RuntimeAnimatorController> visited,HashSet<AnimationClip> clips,HashSet<string> files)
        {
            if(!controller || visited.Count>=32 || !visited.Add(controller)) throw new InvalidOperationException("Controller ausente/cíclico o grafo excesivo.");
            CleanAsset(controller,files);
            if(controller is AnimatorOverrideController overrides)
            {
                Controllers(overrides.runtimeAnimatorController,visited,clips,files);
                var pairs=new List<KeyValuePair<AnimationClip,AnimationClip>>();overrides.GetOverrides(pairs);
                foreach(var p in pairs){if(p.Key)clips.Add(p.Key);if(p.Value)clips.Add(p.Value);}
            }
            else if(controller is AnimatorController concrete)
            {
                foreach(var layer in concrete.layers)
                {
                    if(layer.blendingMode!=AnimatorLayerBlendingMode.Override || layer.syncedLayerIndex>=0 || !Finite(layer.defaultWeight) || layer.defaultWeight<0 || layer.defaultWeight>1)
                        throw new InvalidOperationException("Capas additive/synced/pesos no revisados.");
                    if(layer.avatarMask)CleanAsset(layer.avatarMask,files);
                }
                foreach(var asset in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(concrete)))
                {
                    if(asset is StateMachineBehaviour)throw new InvalidOperationException("StateMachineBehaviour no revisado.");
                    if(asset is BlendTree tree && tree.blendType!=BlendTreeType.Simple1D && tree.blendType!=BlendTreeType.SimpleDirectional2D)
                        throw new InvalidOperationException("Solo BlendTrees 1D y 2D simple con pesos convexos revisados.");
                    if(asset is AnimatorState state && state.motion && !(state.motion is AnimationClip) && !(state.motion is BlendTree))
                        throw new InvalidOperationException("Motion no revisado.");
                }
                foreach(var clip in concrete.animationClips)clips.Add(clip);
                foreach(string dependency in AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(concrete),true))
                    foreach(var asset in AssetDatabase.LoadAllAssetsAtPath(dependency))
                    {
                        if(asset is StateMachineBehaviour)throw new InvalidOperationException("StateMachineBehaviour externo no revisado.");
                        if(asset is BlendTree tree && tree.blendType!=BlendTreeType.Simple1D && tree.blendType!=BlendTreeType.SimpleDirectional2D)
                            throw new InvalidOperationException("BlendTree externo sin contrato de pesos convexos.");
                        if(asset is AnimatorState state && state.motion && !(state.motion is AnimationClip) && !(state.motion is BlendTree))
                            throw new InvalidOperationException("Motion externo no revisado.");
                        if(asset is Motion || asset is AvatarMask)CleanAsset(asset,files);
                    }
            }
            else throw new InvalidOperationException("Tipo de controller sin contrato contextual.");
        }
        static bool Finite(float f)=>!float.IsNaN(f)&&!float.IsInfinity(f);
        static void CurveValues(AnimationCurve curve)
        {
            if(curve==null || curve.length==0 || curve.length>65536)throw new InvalidOperationException("Curva vacía/excesiva.");
            foreach(var k in curve.keys)
                if(!Finite(k.time)||k.time<0||!Finite(k.value)||float.IsNaN(k.inTangent)||float.IsNaN(k.outTangent)||!Finite(k.inWeight)||!Finite(k.outWeight))
                    throw new InvalidOperationException("Curva con datos inválidos.");
        }
        static void SelectorCurve(AnimationCurve curve,HashSet<int> values)
        {
            var keys=curve.keys;
            foreach(var k in keys)
            {if(k.value<0||k.value>8||k.value!=Mathf.Floor(k.value))throw new InvalidOperationException("ID Mask animado fuera del contrato discreto 0–8.");values.Add((int)k.value);}
            for(int i=0;i<keys.Length-1;i++)
                if(!float.IsPositiveInfinity(keys[i].outTangent) &&
                    !(keys[i].value==keys[i+1].value&&keys[i].outTangent==0&&keys[i+1].inTangent==0))
                    throw new InvalidOperationException("Curva ID Mask interpolada/overshoot no revisado: usa claves constantes/discretas.");
        }
        static void MaterialCurve(Material material, string binding, bool textureTransforms)
        {
            string full=binding.Substring(9), property=Regex.Replace(full,@"\.[rgbaxyzw]$","");
            string component=full.Length==property.Length ? "" : full.Substring(property.Length+1);
            if(textureTransforms && property=="_MainTex_ST" && component.Length==1 && "xyzw".Contains(component) && material.HasProperty("_MainTex"))return;
            int index=-1;for(int i=0;i<ShaderUtil.GetPropertyCount(material.shader);i++)if(ShaderUtil.GetPropertyName(material.shader,i)==property){index=i;break;}
            if(index<0)throw new InvalidOperationException("Propiedad animada ausente en alguna variante/material posible.");
            var type=ShaderUtil.GetPropertyType(material.shader,index);
            bool allowed=(type==ShaderUtil.ShaderPropertyType.Float||type==ShaderUtil.ShaderPropertyType.Range||type==ShaderUtil.ShaderPropertyType.Int) ? component.Length==0 :
                type==ShaderUtil.ShaderPropertyType.Color ? component.Length==1&&"rgba".Contains(component) :
                type==ShaderUtil.ShaderPropertyType.Vector && component.Length==1&&"xyzw".Contains(component);
            if(!allowed)throw new InvalidOperationException("Tipo/componente de propiedad animada no revisado.");
        }
        public static GuardAnimationContext Capture(GameObject root, bool allowTextureTransforms = false, bool allowSkinning = false)
        {
            if(!root)throw new ArgumentNullException(nameof(root));
            var animators=root.GetComponentsInChildren<Animator>(true);
            if(animators.Length>1 || (animators.Length==1&&animators[0].gameObject!=root))throw new InvalidOperationException("Primera integración: un Animator en la raíz, sin animators anidados.");
            var animator=animators.SingleOrDefault();
            if(animator&&(animator.avatar||animator.applyRootMotion||!animator.runtimeAnimatorController))throw new InvalidOperationException("Solo Animator genérico sin Avatar/root motion y con controller persistente.");
            var mats=new Dictionary<Renderer,HashSet<Material>[]>();var ids=new Dictionary<Renderer,HashSet<int>>();
            foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh=allowSkinning && renderer is SkinnedMeshRenderer skin ? SkinnedLinearCodecV1.RequireRenderer(root,skin) : StaticPolymorphicCodecV1.RequireStaticRenderer(renderer);
                if(!(allowSkinning && renderer is SkinnedMeshRenderer))StaticPolymorphicCodecV1.ValidateSource(mesh,.1f,false);
                if(renderer.sharedMaterials.Length!=mesh.subMeshCount||renderer.sharedMaterials.Any(m=>!m))throw new InvalidOperationException("Materiales/submeshes contextuales incompletos.");
                mats.Add(renderer,renderer.sharedMaterials.Select(m=>new HashSet<Material>{m}).ToArray());ids.Add(renderer,new HashSet<int>());
            }
            if(mats.Count==0||mats.Count>128)throw new InvalidOperationException("Cantidad de renderers fuera del contrato contextual.");
            var clips=new HashSet<AnimationClip>();var files=new HashSet<string>();
            if(animator)Controllers(animator.runtimeAnimatorController,new HashSet<RuntimeAnimatorController>(),clips,files);
            if(clips.Count>256)throw new InvalidOperationException("Demasiados clips para el contrato contextual.");
            var context=new GuardAnimationContext(root,animator,mats,ids,clips.OrderBy(Identity,StringComparer.Ordinal).ToArray(),null,allowTextureTransforms,allowSkinning);
            foreach(var clip in context.Clips)
            {
                CleanAsset(clip,files);
                if(clip.legacy||!AssetDatabase.GetAssetPath(clip).EndsWith(".anim",StringComparison.Ordinal)||AnimationUtility.GetAnimationEvents(clip).Length!=0)
                    throw new InvalidOperationException("Clips importados/legacy/eventos fuera del contrato contextual inicial.");
                foreach(var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    int slot=MaterialSlot(binding);var r=context.RendererAt(binding);
                    if(slot>=mats[r].Length)throw new InvalidOperationException("Slot animado fuera de rango.");
                    foreach(var frame in AnimationUtility.GetObjectReferenceCurve(clip,binding))
                    {if(!Finite(frame.time)||frame.time<0||!(frame.value is Material material))throw new InvalidOperationException("Swap nulo/no-material inválido.");Identity(material);mats[r][slot].Add(material);}
                }
                foreach(var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    Resolve(root,binding.path);var curve=AnimationUtility.GetEditorCurve(clip,binding);CurveValues(curve);
                    if(binding.type==typeof(Transform)&&Regex.IsMatch(binding.propertyName,@"^((m_LocalPosition|m_LocalScale|localEulerAnglesRaw)\.[xyz]|m_LocalRotation\.[xyzw])$"))continue;
                    if(binding.type==typeof(GameObject)&&binding.propertyName=="m_IsActive")continue;
                    if(typeof(Renderer).IsAssignableFrom(binding.type))
                    {
                        var r=context.RendererAt(binding);
                        if(binding.propertyName=="m_Enabled")continue;
                        if(allowSkinning && r is SkinnedMeshRenderer skin && binding.propertyName.StartsWith("blendShape.",StringComparison.Ordinal))
                        {if(skin.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring(11))<0)throw new InvalidOperationException("SkinGuard: blendshape animado ausente.");continue;}
                        if(!binding.propertyName.StartsWith("material.",StringComparison.Ordinal))throw new InvalidOperationException("Propiedad animada de renderer no revisada.");
                        string property=Regex.Replace(binding.propertyName.Substring(9),@"\.[rgbaxyzw]$","");
                        if(property.StartsWith("_LAG",StringComparison.Ordinal))throw new InvalidOperationException("No se permiten claves/propiedades guard animadas de entrada.");
                        if(property=="_IDMaskFrom")SelectorCurve(curve,ids[r]);
                        // Check against every possible material after all object curves have been collected below.
                    }
                    else throw new InvalidOperationException("Binding de float no revisado.");
                }
            }
            // Collect all swaps first: a property clip can run against a material introduced by another clip.
            foreach(var clip in context.Clips)foreach(var binding in AnimationUtility.GetCurveBindings(clip))
                if(typeof(Renderer).IsAssignableFrom(binding.type)&&binding.propertyName.StartsWith("material.",StringComparison.Ordinal))
                {
                    var r=context.RendererAt(binding);
                    foreach(var material in context.Materials(r))MaterialCurve(material,binding.propertyName,allowTextureTransforms);
                }
            foreach(var renderer in mats.Keys)
            {
                bool animatedId=ids[renderer].Count>0;
                foreach(var material in context.Materials(renderer))
                {CleanAsset(material,files);if(!material.HasProperty("_IDMaskFrom"))throw new InvalidOperationException("Contrato ID Mask ausente.");float v=material.GetFloat("_IDMaskFrom");if(!Finite(v)||v!=Mathf.Floor(v)||v<0||v>8)throw new InvalidOperationException("ID Mask base inválido.");ids[renderer].Add((int)v);}
                if(animatedId)
                {
                    // Animator transitions/layers blend float properties, even if individual clips are stepped.
                    int min=ids[renderer].Min(),max=ids[renderer].Max();for(int v=min;v<=max;v++)ids[renderer].Add(v);
                }
            }
            using(var stream=new MemoryStream())
            {
                using(var w=new BinaryWriter(stream,Encoding.UTF8,true))
                {
                    w.Write("LAG/animation-context/v1");w.Write(Version);w.Write(animator!=null);
                    // The original policy retains its exact fingerprint for private schemas 1–3.
                    if(allowTextureTransforms)w.Write("LAG/texture-transform-policy/v1");
                    if(allowSkinning)w.Write("LAG/skinning-animation-policy/v1");
                    if(animator){w.Write(Identity(animator.runtimeAnimatorController));w.Write(animator.enabled);w.Write((int)animator.updateMode);w.Write((int)animator.cullingMode);}
                    foreach(string file in files.OrderBy(f=>f,StringComparer.Ordinal))
                    {w.Write(AssetDatabase.AssetPathToGUID(file));w.Write(MeshBindingIdentity.Digest(File.ReadAllBytes(file)));}
                    foreach(var r in mats.Keys.OrderBy(r=>AnimationUtility.CalculateTransformPath(r.transform,root.transform),StringComparer.Ordinal))
                    {w.Write(MeshBindingIdentity.Capture(root,r,allowSkinning).StableId);foreach(var slot in mats[r]){w.Write(slot.Count);foreach(var m in slot.OrderBy(Identity,StringComparer.Ordinal))w.Write(Identity(m));}foreach(int v in ids[r].OrderBy(v=>v))w.Write(v);w.Write(-1);}
                }
                return new GuardAnimationContext(root,animator,mats,ids,context.Clips.ToArray(),MeshBindingIdentity.Digest(stream.ToArray()),allowTextureTransforms,allowSkinning);
            }
        }
    }
}
