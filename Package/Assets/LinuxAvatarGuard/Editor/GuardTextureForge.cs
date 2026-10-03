// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LinuxAvatarGuard
{
    public sealed class GuardTextureArtifact
    {
        public Material Material { get; }
        public Texture2D Texture { get; }
        public string Folder { get; }
        public string[] ShaderAssetPaths { get; }
        public string[] ProviderAssetPaths { get; }
        public string ProgramHash { get; }
        internal GuardTextureArtifact(Material material, Texture2D texture, string folder, GuardShaders shaders, string hash)
        { Material = material; Texture = texture; Folder = folder; ShaderAssetPaths = shaders.GeneratedAssetPaths; ProviderAssetPaths = shaders.ProviderAssetPaths; ProgramHash = hash; }
    }

    // Stage 11: one material + its opaque albedo, not an avatar/animation pipeline.
    public static class GuardTextureForge
    {
        public const int Version = 1;
        [Serializable] sealed class PublicManifest
        {
            public int schemaVersion = 1, codecVersion = TextureGuardCodecV1.Version, texturesProtected = 1;
            public string codec = TextureGuardCodecV1.Id, status = "ResearchOnly", programHash;
            public bool sdkProcessed = false, mipmapsSupported = false, compressedPayloadSupported = false;
        }
        static string MaterialState(Material material)
        {
            if (!material || !material.shader || material.shader.name != "lilToon" || !AssetDatabase.Contains(material) || EditorUtility.IsDirty(material) || material.shaderKeywords.Length != 0)
                throw new InvalidOperationException("TextureGuard: material persistente/guardado lilToon opaco sin keywords requerido.");
            string digest = AttributeAllocator.ReviewSources(material.shader);
            if (!(material.GetTexture("_MainTex") is Texture2D main)) throw new InvalidOperationException("TextureGuard: albedo Texture2D requerido.");
            for (int i = 0; i < ShaderUtil.GetPropertyCount(material.shader); i++)
                if (ShaderUtil.GetPropertyType(material.shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    string name = ShaderUtil.GetPropertyName(material.shader, i);
                    if (name != "_MainTex" && material.GetTexture(name) == main)
                        throw new InvalidOperationException("TextureGuard: la fuente albedo también está referenciada por " + name + "; esa ruta no está protegida.");
                    var scale = material.GetTextureScale(name); var offset = material.GetTextureOffset(name);
                    if (new[] {scale.x, scale.y, offset.x, offset.y}.Any(x => float.IsNaN(x) || float.IsInfinity(x)))
                        throw new InvalidOperationException("TextureGuard: transformación de textura no finita.");
                }
            return MeshBindingIdentity.Digest(Encoding.UTF8.GetBytes(digest + "\0" + EditorJsonUtility.ToJson(material) + "\0" + AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(material))));
        }
        internal static void OutputPath(string folder)
        {
            if (folder == null || !Regex.IsMatch(folder, @"\AAssets/LinuxAvatarGuardGenerated/Research/[A-Za-z0-9_-]{1,64}\z"))
                throw new ArgumentException("TextureGuard: salida nueva en Assets/LinuxAvatarGuardGenerated/Research/ requerida.");
            if (Directory.Exists(folder) || File.Exists(folder) || File.Exists(folder + ".meta"))
                throw new InvalidOperationException("TextureGuard: la salida ya existe, no se sobrescribe.");
            for (string parent = Path.GetDirectoryName(Path.GetFullPath(folder)); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if ((Directory.Exists(parent) || File.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("TextureGuard: la ruta contiene un enlace simbólico.");
        }
        public static GuardTextureArtifact Prepare(Material source, TextureGuardCodecV1 codec, string folder, int[] runtimeKey)
        {
            if (codec == null) throw new ArgumentNullException(nameof(codec));
            OutputPath(folder); string state = MaterialState(source);
            var texture = (Texture2D)source.GetTexture("_MainTex"); var plan = codec.Plan(texture);
            Texture2D encoded = null; Material material = null; bool owned = false;
            try
            {
                // Validation/encoding happens before any output assets are written.
                encoded = codec.Encode(texture, plan, runtimeKey);
                var decoder = codec.EmitDecoder(plan); string hash = TextureGuardCodecV1.ProgramHash(plan);
                Directory.CreateDirectory(folder); owned = true; AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.CreateAsset(encoded, folder + "/t_" + hash.Substring(0, 20) + ".asset");
                Directory.CreateDirectory(folder + "/shaders");
                var keys = Enumerable.Range(0, 4).Select(GuardShaders.Property).ToArray();
                var declarations = new DecoderFragment(TextureGuardCodecV1.Id, TextureGuardCodecV1.Version,
                    "\nHLSLINCLUDE\n#define LIL_CUSTOM_PROPERTIES float _LAGKey0; float _LAGKey1; float _LAGKey2; float _LAGKey3;\nENDHLSL\n", keys);
                var shaders = new GuardShaders(folder + "/shaders", "tex_" + hash.Substring(0, 20), declarations, true, decoder);
                var shader = shaders.Copy(source.shader);
                material = new Material(source) { name = "m_" + hash.Substring(0, 20), shader = shader };
                material.SetTexture("_MainTex", encoded);
                foreach (string key in keys) material.SetFloat(key, 0);
                AssetDatabase.CreateAsset(material, folder + "/material.mat");
                foreach (string path in shaders.GeneratedAssetPaths)
                {
                    var generated = AssetDatabase.LoadAssetAtPath<Shader>(path);
                    if (!generated || !generated.isSupported || ShaderUtil.ShaderHasError(generated))
                        throw new InvalidOperationException("TextureGuard: shader/provider sin soporte o con errores: " + path);
                }
                File.WriteAllText(folder + "/public-manifest.json", JsonUtility.ToJson(new PublicManifest { programHash = hash }, true));
                AssetDatabase.ImportAsset(folder + "/public-manifest.json", ImportAssetOptions.ForceSynchronousImport);
                if (MaterialState(source) != state || source.GetTexture("_MainTex") != texture)
                    throw new InvalidOperationException("TextureGuard: el material fuente cambió durante la preparación.");
                codec.Validate(texture, plan);
                if (material.GetTexture("_MainTex") != encoded || material.shader != shader || keys.Any(k => material.GetFloat(k) != 0) ||
                    encoded.isReadable || encoded.isDataSRGB || encoded.mipmapCount != 1 || encoded.width != plan.Program.Size || encoded.height != plan.Program.Size ||
                    encoded.format != TextureFormat.RGBA32 || UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(encoded.graphicsFormat))
                    throw new InvalidOperationException("TextureGuard: el artefacto generado cambió durante la importación o conserva claves serializadas.");
                return new GuardTextureArtifact(material, encoded, folder, shaders, hash);
            }
            catch
            {
                if (material && !AssetDatabase.Contains(material)) Object.DestroyImmediate(material);
                if (encoded && !AssetDatabase.Contains(encoded)) Object.DestroyImmediate(encoded);
                if (owned)
                {
                    AssetDatabase.DeleteAsset(folder);
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                    if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
                }
                throw;
            }
        }
    }
}
