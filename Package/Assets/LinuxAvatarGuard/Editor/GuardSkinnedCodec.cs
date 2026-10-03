// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LinuxAvatarGuard
{
    // Linear normal carrier commutes with linear-blend skinning while the input normal remains raw.
    // Morph position deltas include the encoded normal delta. No bindpose/weight/bone remapping.
    // Not the nonlinear rigid codec; an adaptive linear extractor remains a known limitation.
    public sealed class SkinnedLinearCodecV1 : IMeshCodec, IDisposable
    {
        public const string Id="skinned-linear-contextual";
        public const int Version=1;
        public string CodecId=>Id;
        public int CodecVersion=>Version;
        readonly byte[] programSeed,payloadSeed;
        readonly MeshBindingIdentity binding;
        readonly AttributeUsageAnalysis usage;
        readonly AttributeLayout layout;
        readonly int[] expectedKey;
        readonly float? recordedStrength;
        bool disposed;
        internal SkinnedLinearCodecV1(byte[] programSeed,byte[] payloadSeed,MeshBindingIdentity binding,AttributeUsageAnalysis usage,AttributeLayout layout,int[] key,float? strength)
        {
            this.programSeed=(byte[])programSeed.Clone();this.payloadSeed=(byte[])payloadSeed.Clone();this.binding=binding;this.usage=usage;
            this.layout=new AttributeLayout(layout.FirstUvChannel,layout.SecondUvChannel,2,false,layout.PolicyVersion);expectedKey=(int[])key.Clone();recordedStrength=strength;
        }
        static bool Finite(float f)=>!float.IsNaN(f)&&!float.IsInfinity(f);
        static void Vector(Vector3 v){if(!Finite(v.x)||!Finite(v.y)||!Finite(v.z))throw new InvalidOperationException("SkinGuard: datos no finitos.");}
        internal static Mesh RequireRenderer(GameObject root,SkinnedMeshRenderer skin)
        {
            if(!root||!skin||!skin.sharedMesh||skin.GetComponent<Cloth>()||skin.HasPropertyBlock()||!skin.rootBone||skin.bones.Length==0||skin.bones.Length>256)
                throw new InvalidOperationException("SkinGuard: renderer/huesos incompletos, Cloth o property block no revisados.");
            foreach(var bone in skin.bones.Concat(new[]{skin.rootBone}))
                if(!bone||(bone!=root.transform&&!bone.IsChildOf(root.transform)))throw new InvalidOperationException("SkinGuard: todos los huesos deben pertenecer a la raíz copiada.");
            if(skin.bones.Length!=skin.sharedMesh.bindposes.Length)throw new InvalidOperationException("SkinGuard: cantidad de bindposes/huesos incompatible.");
            ValidateSource(skin.sharedMesh);
            foreach(var t in skin.bones.Concat(new[]{skin.transform,root.transform}))
            {
                var matrix=t.localToWorldMatrix;for(int i=0;i<16;i++)if(!Finite(matrix[i]))throw new InvalidOperationException("SkinGuard: transform no finito en la pose de trabajo.");
                if(!Finite(matrix.determinant)||Mathf.Abs(matrix.determinant)<1e-8f)
                    throw new InvalidOperationException("SkinGuard: transform singular/no finito en la pose de trabajo.");
            }
            return skin.sharedMesh;
        }
        internal static void ValidateSource(Mesh source)
        {
            if(!source||!source.isReadable||source.vertexCount==0||source.normals.Length!=source.vertexCount||source.bindposes.Length==0||source.bindposes.Length>256)
                throw new InvalidOperationException("SkinGuard: malla legible con normales y bindposes requerida.");
            if(source.GetBonesPerVertex().Any(n=>n>4)||source.boneWeights.Length!=source.vertexCount)
                throw new InvalidOperationException("SkinGuard: solo una a cuatro influencias por vértice revisadas.");
            foreach(var b in source.boneWeights)
            {
                int[] ids={b.boneIndex0,b.boneIndex1,b.boneIndex2,b.boneIndex3};float[] weights={b.weight0,b.weight1,b.weight2,b.weight3};
                for(int i=0;i<4;i++)if(!Finite(weights[i])||weights[i]<0||(weights[i]>0&&(ids[i]<0||ids[i]>=source.bindposes.Length)))throw new InvalidOperationException("SkinGuard: pesos/índices de hueso inválidos.");
                if(Mathf.Abs(weights.Sum()-1)>1e-5f)throw new InvalidOperationException("SkinGuard: pesos normalizados requeridos; no se modifican automáticamente.");
            }
            foreach(var matrix in source.bindposes)
            {
                for(int i=0;i<16;i++)if(!Finite(matrix[i]))throw new InvalidOperationException("SkinGuard: bindpose no finita.");
                if(Mathf.Abs(matrix.determinant)<1e-8f||Mathf.Abs(matrix[3,0])+Mathf.Abs(matrix[3,1])+Mathf.Abs(matrix[3,2])+Mathf.Abs(matrix[3,3]-1)>1e-5f)throw new InvalidOperationException("SkinGuard: bindpose singular/no afín.");
            }
            foreach(var p in source.vertices){Vector(p);if(Mathf.Max(Mathf.Abs(p.x),Mathf.Abs(p.y),Mathf.Abs(p.z))>16)throw new InvalidOperationException("SkinGuard: posiciones fuera del rango revisado.");}
            foreach(var n in source.normals){Vector(n);if(n.sqrMagnitude<1e-10f)throw new InvalidOperationException("SkinGuard: normal nula sin carrier útil.");}
            foreach(var t in source.tangents)for(int i=0;i<4;i++)if(!Finite(t[i]))throw new InvalidOperationException("SkinGuard: tangente no finita.");
            foreach(var c in source.colors)for(int i=0;i<4;i++)if(!Finite(c[i]))throw new InvalidOperationException("SkinGuard: color no finito.");
            var uv=new List<Vector4>();for(int i=0;i<8;i++){source.GetUVs(i,uv);foreach(var v in uv)for(int j=0;j<4;j++)if(!Finite(v[j]))throw new InvalidOperationException("SkinGuard: UV no finita.");}
            Vector(source.bounds.center);Vector(source.bounds.extents);
            var names=new HashSet<string>(StringComparer.Ordinal);var dp=new Vector3[source.vertexCount];var dn=new Vector3[source.vertexCount];var dt=new Vector3[source.vertexCount];
            for(int shape=0;shape<source.blendShapeCount;shape++)
            {
                if(string.IsNullOrEmpty(source.GetBlendShapeName(shape))||!names.Add(source.GetBlendShapeName(shape)))throw new InvalidOperationException("SkinGuard: nombres de blendshape vacíos/duplicados.");
                float previous=float.NegativeInfinity;
                for(int level=0;level<source.GetBlendShapeFrameCount(shape);level++)
                {
                    float weight=source.GetBlendShapeFrameWeight(shape,level);if(!Finite(weight)||weight<=previous)throw new InvalidOperationException("SkinGuard: frames de blendshape no ordenados/finitos.");previous=weight;
                    source.GetBlendShapeFrameVertices(shape,level,dp,dn,dt);foreach(var array in new[]{dp,dn,dt})foreach(var v in array)Vector(v);
                }
            }
        }
        internal static string RendererFingerprint(GameObject root,SkinnedMeshRenderer skin)
        {
            RequireRenderer(root,skin);
            using(var stream=new MemoryStream())
            {
                using(var w=new BinaryWriter(stream,Encoding.UTF8,true))
                {
                    w.Write("LAG/skinned-renderer/v1");w.Write((int)skin.quality);w.Write(skin.updateWhenOffscreen);w.Write(skin.skinnedMotionVectors);w.Write(skin.bones.Length);
                    foreach(var bone in skin.bones.Concat(new[]{skin.rootBone,skin.transform}))
                    {string path=AnimationUtility.CalculateTransformPath(bone,root.transform);if(GuardAnimationContext.Resolve(root,path)!=bone)throw new InvalidOperationException("SkinGuard: ruta de hueso ambigua.");w.Write(path);var m=root.transform.worldToLocalMatrix*bone.localToWorldMatrix;for(int i=0;i<16;i++)w.Write(m[i]);}
                    // updateWhenOffscreen recomputes runtime bounds from each encoded pose.
                    // Only author-controlled static bounds can be a stable identity component.
                    if(!skin.updateWhenOffscreen)for(int i=0;i<3;i++){w.Write(skin.localBounds.center[i]);w.Write(skin.localBounds.extents[i]);}
                    w.Write(skin.sharedMesh.blendShapeCount);for(int i=0;i<skin.sharedMesh.blendShapeCount;i++){float f=skin.GetBlendShapeWeight(i);if(!Finite(f))throw new InvalidOperationException("SkinGuard: peso de blendshape no finito.");w.Write(f);}
                }
                return MeshBindingIdentity.Digest(stream.ToArray());
            }
        }
        void Alive()
        {
            if(disposed)throw new ObjectDisposedException(nameof(SkinnedLinearCodecV1));
            if(Application.platform!=RuntimePlatform.LinuxEditor||Application.unityVersion!="2022.3.22f1"||SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Vulkan||GraphicsSettings.currentRenderPipeline!=null)throw new InvalidOperationException("SkinGuard: solo Unity 2022.3.22f1 Linux/Vulkan/Built-in revisado.");
        }
        public CodecPlan Plan(Mesh source,float strength)
        {
            Alive();binding.Validate();usage.Validate();ValidateSource(source);
            if(source!=binding.Source||!binding.IsSkinned||!Finite(strength)||strength<=0||strength>.5f||(recordedStrength.HasValue&&strength!=recordedStrength.Value))throw new ArgumentException("SkinGuard: fuente/intensidad/contexto ajeno.");
            foreach(int c in new[]{layout.FirstUvChannel,layout.SecondUvChannel})if(source.HasVertexAttribute((VertexAttribute)((int)VertexAttribute.TexCoord0+c)))throw new InvalidOperationException("SkinGuard: carrier ocupado; no se sobrescribe.");
            Vector4 rows;using(var random=new TextureGuardCodecV1.TextureStream(programSeed))rows=new Vector4(random.Next(2)==0?-1:1,random.Next(2)==0?-.5f:.5f,random.Next(2)==0?-1:1,random.Next(2)==0?-.5f:.5f);
            return new CodecPlan(source,new CodecProgram(Id,Version,layout,rows),strength,MeshBindingIdentity.ContentFingerprint(source),this,binding.StableId,usage.Fingerprint);
        }
        public void Validate(Mesh source,CodecPlan plan)
        {
            Alive();binding.Validate();usage.Validate();ValidateSource(source);
            if(plan==null||plan.Owner!=this||plan.Source!=source||plan.BindingStableId!=binding.StableId||plan.Program.CodecId!=Id||MeshBindingIdentity.ContentFingerprint(source)!=plan.SourceFingerprint)throw new InvalidOperationException("SkinGuard: plan/fuente modificado o ajeno.");
        }
        public Mesh Encode(Mesh source,CodecPlan plan,int[] key)
        {
            Validate(source,plan);if(key==null||key.Length!=4||!key.SequenceEqual(expectedKey))throw new ArgumentException("SkinGuard: claves ajenas al contexto.");
            var p=source.vertices;var n=source.normals;var offsets=new float[p.Length];var first=new List<Vector2>();var second=new List<Vector2>();double missing=0;
            using(var random=new TextureGuardCodecV1.TextureStream(payloadSeed))for(int i=0;i<p.Length;i++)
            {
                var c=new Vector4(random.Next(256)/127.5f-1,random.Next(256)/127.5f-1,random.Next(256)/127.5f-1,random.Next(256)/127.5f-1)*plan.Strength;
                float offset=Vector4.Dot(Vector4.Scale(c,plan.Program.SkinningWeights),new Vector4(key[0],key[1],key[2],key[3])/255f);offsets[i]=offset;
                var original=p[i];p[i]+=n[i]*offset;Vector(p[i]);
                if((p[i]-n[i]*offset-original).magnitude>2e-5f)throw new InvalidOperationException("SkinGuard: round-trip numérico fuera del contrato.");
                missing+=(n[i]*offset).sqrMagnitude;
                first.Add(new Vector2(c.x,c.y));second.Add(new Vector2(c.z,c.w));
            }
            if(Math.Sqrt(missing/p.Length)<=1e-5)throw new InvalidOperationException("SkinGuard: contexto sin deformación keyless útil; usa otro contexto.");
            var copy=UnityEngine.Object.Instantiate(source);
            try
            {
                copy.name="ms_"+ProgramHash(plan).Substring(0,20);copy.vertices=p;copy.SetUVs(layout.FirstUvChannel,first);copy.SetUVs(layout.SecondUvChannel,second);copy.ClearBlendShapes();
                var dv=new Vector3[p.Length];var dn=new Vector3[p.Length];var dt=new Vector3[p.Length];
                for(int shape=0;shape<source.blendShapeCount;shape++)for(int level=0;level<source.GetBlendShapeFrameCount(shape);level++)
                {
                    source.GetBlendShapeFrameVertices(shape,level,dv,dn,dt);for(int i=0;i<p.Length;i++)
                    {
                        var original=dv[i];dv[i]+=dn[i]*offsets[i];Vector(dv[i]);
                        if((dv[i]-dn[i]*offsets[i]-original).magnitude>2e-5f)throw new InvalidOperationException("SkinGuard: round-trip de morph fuera del contrato.");
                    }
                    copy.AddBlendShapeFrame(source.GetBlendShapeName(shape),source.GetBlendShapeFrameWeight(shape,level),dv,dn,dt);
                }
                copy.bounds=source.bounds;Validate(source,plan);return copy;
            }
            catch{UnityEngine.Object.DestroyImmediate(copy);throw;}
            finally{Array.Clear(offsets,0,offsets.Length);}
        }
        public DecoderFragment EmitDecoder(CodecPlan plan)
        {
            Validate(plan?.Source,plan);var row=plan.Program.SkinningWeights;var h=new StringBuilder("\nHLSLINCLUDE\n#define LIL_REQUIRE_APP_NORMAL\n");
            h.Append("#define LIL_REQUIRE_APP_TEXCOORD").Append(layout.FirstUvChannel).Append("\n#define LIL_REQUIRE_APP_TEXCOORD").Append(layout.SecondUvChannel).Append("\n#define LIL_CUSTOM_PROPERTIES float _LAGKey0;float _LAGKey1;float _LAGKey2;float _LAGKey3;\n");
            h.AppendFormat(CultureInfo.InvariantCulture,"// LAG_SKIN_LINEAR_V1 {0} {1} {2} {3} {4} {5}\n",layout.FirstUvChannel,layout.SecondUvChannel,row.x,row.y,row.z,row.w);
            h.AppendFormat(CultureInfo.InvariantCulture,"#define LIL_CUSTOM_VERTEX_OS positionOS.xyz -= input.normalOS * dot(float4(input.uv{0},input.uv{1})*float4({2},{3},{4},{5}),clamp(floor(float4(_LAGKey0,_LAGKey1,_LAGKey2,_LAGKey3)+0.5),0.0,255.0)/255.0);\nENDHLSL\n",layout.FirstUvChannel,layout.SecondUvChannel,row.x,row.y,row.z,row.w);
            return new DecoderFragment(Id,Version,h.ToString(),"_LAGKey0","_LAGKey1","_LAGKey2","_LAGKey3");
        }
        public static string ProgramHash(CodecPlan plan)
        {
            if(plan==null||plan.Program.CodecId!=Id)throw new ArgumentException("SkinGuard plan requerido.");var p=plan.Program;
            using(var stream=new MemoryStream())
            {using(var w=new BinaryWriter(stream,Encoding.UTF8,true)){w.Write(Id);w.Write(Version);w.Write(p.Attributes.FirstUvChannel);w.Write(p.Attributes.SecondUvChannel);w.Write(p.Attributes.PolicyVersion);w.Write(plan.AttributeUsageHash);for(int i=0;i<4;i++)w.Write(p.SkinningWeights[i]);}return MeshBindingIdentity.Digest(stream.ToArray());}
        }
        public void Dispose(){if(disposed)return;disposed=true;Array.Clear(programSeed,0,programSeed.Length);Array.Clear(payloadSeed,0,payloadSeed.Length);Array.Clear(expectedKey,0,expectedKey.Length);}
    }
}
