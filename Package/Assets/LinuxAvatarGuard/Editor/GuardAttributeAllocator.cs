// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Rendering;

namespace LinuxAvatarGuard
{
    public sealed class AttributeUsageAnalysis
    {
        public string Fingerprint { get; }
        public ReadOnlyCollection<int> AvailableUvChannels { get; }
        public ReadOnlyCollection<string> Reservations { get; }
        readonly MeshBindingIdentity binding;
        readonly GuardAnimationContext animation;
        internal int Policy => animation==null ? AttributeAllocator.PolicyVersion : AttributeAllocator.ContextualPolicyVersion;
        internal AttributeUsageAnalysis(MeshBindingIdentity binding, string hash, int[] channels, string[] reservations, GuardAnimationContext animation=null)
        { this.binding = binding; this.animation=animation; Fingerprint = hash; AvailableUvChannels = Array.AsReadOnly(channels); Reservations = Array.AsReadOnly(reservations); }
        public void Validate()
        {
            if (AttributeAllocator.Inspect(binding,animation).Fingerprint != Fingerprint)
                throw new InvalidOperationException("Cambió el uso de atributos/materiales/shaders después del análisis. Crea un plan nuevo.");
        }
    }

    // Positive, version-pinned contract. This is deliberately not a general HLSL semantic analyzer.
    public static class AttributeAllocator
    {
        public const int PolicyVersion = 1;
        public const int ContextualPolicyVersion = 2;
        public const string ReviewedShaderDigest = "46fda1de8dedeb5f57c58ab069a4c66b6937b867cb55dec54f73c987d344d892";
        static readonly Dictionary<string, string> shaders = new Dictionary<string, string> {
            { "lilToon", "lts.shader" }, { "Hidden/lilToonOutline", "lts_o.shader" },
            { "Hidden/lilToonCutout", "lts_cutout.shader" }, { "Hidden/lilToonCutoutOutline", "lts_cutout_o.shader" },
            { "Hidden/lilToonTransparent", "lts_trans.shader" }, { "Hidden/lilToonTransparentOutline", "lts_trans_o.shader" },
            { "Hidden/lilToonOnePassTransparent", "lts_onetrans.shader" }, { "Hidden/lilToonOnePassTransparentOutline", "lts_onetrans_o.shader" },
            { "Hidden/lilToonTwoPassTransparent", "lts_twotrans.shader" }, { "Hidden/lilToonTwoPassTransparentOutline", "lts_twotrans_o.shader" }
        };
        static readonly string[] forbiddenKeywords = { "_ADD_PRECOMPUTED_VELOCITY", "LIL_REQUIRE_APP_PREVPOS", "LIL_REQUIRE_APP_PREVEL", "LIL_PASS_MOTIONVECTOR_INCLUDED" };
        static void RequireEnvironment()
        {
            if (Application.platform != RuntimePlatform.LinuxEditor || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan ||
                GraphicsSettings.currentRenderPipeline != null || Application.unityVersion != "2022.3.22f1")
                throw new InvalidOperationException("Allocator experimental: solo Unity 2022.3.22f1, Linux, Vulkan y Built-in revisados.");
            if (forbiddenKeywords.Any(Shader.IsKeywordEnabled))
                throw new InvalidOperationException("Keyword global de posición/velocidad previa incompatible con carriers UV4/UV5.");
        }
        static void RequireStaticContext(GameObject root, Renderer renderer, GuardAnimationContext animation)
        {
            var meshRenderer = (MeshRenderer)renderer;
            if (meshRenderer.additionalVertexStreams || meshRenderer.enlightenVertexStream || renderer.isPartOfStaticBatch)
                throw new InvalidOperationException("Vertex streams/batching adicionales no analizados: se cancela la asignación.");
            // Even disabled controllers/scripts can later change materials or their semantic properties.
            for (var node = root.transform; node; node = node.parent)
                RequireUnanimated(node.gameObject,animation);
            foreach (var node in root.GetComponentsInChildren<Transform>(true)) RequireUnanimated(node.gameObject,animation);
            if (renderer.HasPropertyBlock()) throw new InvalidOperationException("MaterialPropertyBlock no analizado: se cancela la asignación.");
        }
        static void RequireUnanimated(GameObject obj, GuardAnimationContext animation)
        {
            if ((GameObjectUtility.GetStaticEditorFlags(obj) & StaticEditorFlags.BatchingStatic) != 0)
                throw new InvalidOperationException("Static batching no conserva el contrato de posiciones del codec experimental.");
            if ((obj.GetComponent<Animator>() && (animation==null || obj.GetComponent<Animator>()!=animation.Animator)) || obj.GetComponent<Animation>() || obj.GetComponent<PlayableDirector>() || obj.GetComponents<MonoBehaviour>().Length != 0)
                throw new InvalidOperationException("El allocator estático no analiza Animator, Animation, Timeline ni scripts. Integración contextual pendiente.");
        }
        internal static string ReviewSources(Shader shader)
        {
            if (!shader || !shaders.TryGetValue(shader.name, out var filename))
                throw new InvalidOperationException("Shader/variante sin contrato de atributos revisado.");
            var path = AssetDatabase.GetAssetPath(shader);
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            if (package == null || package.name != "jp.lilxyzw.liltoon" || package.version != "2.3.4")
                throw new InvalidOperationException("Se requiere el paquete lilToon 2.3.4 revisado; no se presume compatibilidad de forks/versiones.");
            var directory = Path.Combine(package.resolvedPath, "Shader");
            if (Path.GetFullPath(path) != Path.GetFullPath(Path.Combine(directory, filename)))
                throw new InvalidOperationException("El nombre del shader no acredita su procedencia.");
            var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Where(f => new[] { ".shader", ".hlsl", ".cginc" }.Contains(Path.GetExtension(f)))
                .OrderBy(f => f.Substring(directory.Length + 1).Replace('\\', '/'), StringComparer.Ordinal).ToArray();
            var manifest = new StringBuilder();
            foreach (var file in files)
            {
                var normalized = File.ReadAllText(file).Replace("\r\n", "\n").Replace("\r", "\n");
                manifest.Append(file.Substring(directory.Length + 1).Replace('\\', '/')).Append('\0')
                    .Append(MeshBindingIdentity.Digest(Encoding.UTF8.GetBytes(normalized))).Append('\n');
            }
            var digest = MeshBindingIdentity.Digest(Encoding.UTF8.GetBytes(manifest.ToString()));
            if (files.Length != 101 || digest != ReviewedShaderDigest)
                throw new InvalidOperationException("Las fuentes/pases/includes lilToon cambiaron o incluyen hooks sin revisar: " + files.Length + " archivos; SHA-256 " + digest);
            if (!shader.isSupported || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Shader original sin soporte/compilación correcta.");
            return ReviewedShaderDigest;
        }
        static void ObjectIdentity(BinaryWriter output, UnityEngine.Object obj)
        {
            output.Write(obj != null);
            if (!obj) return;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long id) || !MeshKeyDerivation.IsHex(guid, 32) || id == 0)
                throw new InvalidOperationException("Material/textura sin identidad persistente: guarda el asset antes del análisis.");
            output.Write(guid); output.Write(id);
        }
        static void WriteFloat(BinaryWriter output, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("Propiedad de material no finita.");
            output.Write(value);
        }
        static void SnapshotMaterial(BinaryWriter output, Material material)
        {
            ObjectIdentity(output, material); ObjectIdentity(output, material.shader);
            output.Write(material.shader.name); output.Write(material.renderQueue); output.Write(material.enableInstancing);
            output.Write(material.doubleSidedGI); output.Write((int)material.globalIlluminationFlags);
            if (!string.IsNullOrEmpty(material.GetTag("DisableBatching", false, "")))
                throw new InvalidOperationException("Override de batching no revisado: se cancela la asignación.");
            // Standard, non-Multi lilToon uses property branches. Unknown local keywords are unsupported.
            if (material.shaderKeywords.Length != 0) throw new InvalidOperationException("Keywords de material no revisadas: se cancela la asignación.");
            for (int pass = 0; pass < material.shader.passCount; pass++)
            { var name = material.GetPassName(pass); output.Write(name); output.Write(material.GetShaderPassEnabled(name)); }
            output.Write(-1);
            for (int i = 0; i < ShaderUtil.GetPropertyCount(material.shader); i++)
            {
                var name = ShaderUtil.GetPropertyName(material.shader, i); var type = ShaderUtil.GetPropertyType(material.shader, i);
                output.Write(name); output.Write((int)type);
                switch (type)
                {
                    case ShaderUtil.ShaderPropertyType.Color:
                        var c = material.GetColor(name); for (int j = 0; j < 4; j++) WriteFloat(output, c[j]); break;
                    case ShaderUtil.ShaderPropertyType.Vector:
                        var v = material.GetVector(name); for (int j = 0; j < 4; j++) WriteFloat(output, v[j]); break;
                    case ShaderUtil.ShaderPropertyType.Float: case ShaderUtil.ShaderPropertyType.Range:
                        WriteFloat(output, material.GetFloat(name)); break;
                    case ShaderUtil.ShaderPropertyType.Int:
                        output.Write(material.GetInteger(name)); break;
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        ObjectIdentity(output, material.GetTexture(name));
                        var scale = material.GetTextureScale(name); var offset = material.GetTextureOffset(name);
                        WriteFloat(output, scale.x); WriteFloat(output, scale.y); WriteFloat(output, offset.x); WriteFloat(output, offset.y); break;
                    default: throw new InvalidOperationException("Tipo de propiedad shader sin analizar.");
                }
            }
        }
        public static AttributeUsageAnalysis Inspect(MeshBindingIdentity binding) => Inspect(binding,null);
        internal static AttributeUsageAnalysis Inspect(MeshBindingIdentity binding, GuardAnimationContext animation)
        {
            RequireEnvironment();
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            if(animation!=null){if(animation.Root!=binding.Root)throw new ArgumentException("Contexto de animación de otra raíz.");animation.Validate();}
            binding.Validate(); RequireStaticContext(binding.Root, binding.Renderer,animation);
            var mesh = binding.Source; var materials = binding.Renderer.sharedMaterials;
            if (materials.Length == 0 || materials.Length != mesh.subMeshCount || materials.Any(m => !m))
                throw new InvalidOperationException("Se requiere un material persistente por submesh, sin slots vacíos/adicionales.");
            if(animation!=null)materials=animation.Materials(binding.Renderer);
            var available = new HashSet<int>(new[] { 4, 5, 6, 7 });
            var reserved = new List<string> { "UV0–3: lilToon shading/decals/AudioLink; colors/tangents: preserved" };
            foreach (int channel in available.ToArray())
                if (mesh.HasVertexAttribute((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel)))
                { available.Remove(channel); reserved.Add("UV" + channel + ": existing vertex attribute (any dimension/format)"); }
            using (var stream = new MemoryStream())
            {
                using (var output = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    output.Write(animation==null ? "LAG/attribute-policy/v1" : "LAG/attribute-policy/v2"); output.Write(animation==null ? PolicyVersion : ContextualPolicyVersion); output.Write(binding.StableId);
                    if(animation!=null)output.Write(animation.Fingerprint);
                    output.Write(Application.unityVersion); output.Write(materials.Length);
                    foreach (var material in materials)
                    {
                        output.Write(ReviewSources(material.shader));
                        if (!material.HasProperty("_IDMaskFrom")) throw new InvalidOperationException("Contrato ID Mask ausente.");
                        float selector = material.GetFloat("_IDMaskFrom");
                        if (float.IsNaN(selector) || selector != Mathf.Floor(selector) || selector < 0 || selector > 8)
                            throw new InvalidOperationException("Selector ID Mask no soportado.");
                        if (selector >= 4 && selector <= 7)
                        { available.Remove((int)selector); reserved.Add("UV" + (int)selector + ": ID Mask consumer, including disabled masks"); }
                        SnapshotMaterial(output, material);
                    }
                    foreach (int channel in available.OrderBy(c => c)) output.Write(channel);
                    if(animation!=null)foreach(int channel in animation.ReservedSelectors(binding.Renderer))
                        if(channel>=4&&channel<=7){available.Remove(channel);reserved.Add("UV"+channel+": animated ID Mask/blend range");}
                    if(animation!=null){output.Write(-1);foreach(int channel in available.OrderBy(c=>c))output.Write(channel);}
                }
                return new AttributeUsageAnalysis(binding, MeshBindingIdentity.Digest(stream.ToArray()), available.OrderBy(c => c).ToArray(), reserved.ToArray(),animation);
            }
        }
        // Layout seed is supplied only by the private context's separate HKDF purpose (4).
        internal static AttributeLayout Allocate(AttributeUsageAnalysis usage, byte[] layoutSeed)
        {
            if (usage == null || layoutSeed == null || layoutSeed.Length != 32) throw new ArgumentException("Análisis/seed de atributos inválido.");
            if (usage.AvailableUvChannels.Count < 2) throw new InvalidOperationException("No hay dos carriers UV demostrablemente libres; se cancela sin modificar la fuente.");
            var ordered = usage.AvailableUvChannels.OrderBy(channel => {
                var bytes = new byte[layoutSeed.Length + 1]; Array.Copy(layoutSeed, bytes, layoutSeed.Length); bytes[bytes.Length - 1] = (byte)channel;
                try { return MeshBindingIdentity.Digest(bytes); } finally { Array.Clear(bytes, 0, bytes.Length); }
            }, StringComparer.Ordinal).ThenBy(c => c).ToArray();
            return new AttributeLayout(ordered[0], ordered[1], 2, false, usage.Policy);
        }
    }
}
