// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LinuxAvatarGuard
{
    [Serializable]
    public sealed class GuardTextureFingerprintPrivateBundle
    {
        public int schema = 1;
        public string state = "PrototypeCpuSelfVerified";
        public GuardFingerprintPrivateRecord identity;
        public GuardTextureFingerprintPrivateRecord texture;
        public string textureGuid, materialGuid, scope, sourceState;
        public long textureLocalId, materialLocalId;
    }
    [Serializable]
    public sealed class GuardTextureFingerprintPublicManifest
    {
        public int schema = 1, algorithmVersion = GuardTextureFingerprintV1.Version;
        public string algorithm = GuardTextureFingerprintV1.Id, state = "PrototypeCpuSelfVerified", colorDomain = GuardTextureFingerprintV1.ColorDomain, buildId;
        public int markedTextures = 1, markedMeshes, width, height, usableSymbols, mipCount = 1;
        public double meanAbsoluteRgb, maximumRgb, psnrDb;
        public bool verifierCalibrated, visualValidated, unityCompressionValidated, sdkProcessed;
    }
    public enum GuardTextureFingerprintCheckpoint { PrivateSaved, CopiesSaved, ManifestSaved }
    public sealed class GuardTextureFingerprintArtifact
    {
        public Texture2D Texture { get; internal set; }
        public Material Material { get; internal set; }
        public string Folder { get; internal set; }
        public string PrivatePath { get; internal set; }
        public GuardTextureFingerprintPublicManifest Summary { get; internal set; }
    }

    public sealed class GuardTextureFingerprintSource
    {
        public Material Material { get; }
        public Texture2D Texture { get; }
        public string BindingId { get; }
        public string Scope { get; }
        public string TextureGuid { get; }
        public string MaterialGuid { get; }
        public long TextureLocalId { get; }
        public long MaterialLocalId { get; }
        internal readonly string materialPath, texturePath, sourceState, materialProperties, frozenPixelHash;
        internal readonly Dictionary<string, string> originalFiles = new Dictionary<string, string>();
        internal readonly byte[] pixels;
        GuardTextureFingerprintSource(Material material, string scope)
        {
            GuardTextureFingerprintUnity.RequireSource(material);
            if (!GuardTextureFingerprintV1.Hex(scope, 64)) throw new ArgumentException("TextureFingerprint: scope privado hexadecimal de 64 caracteres requerido.");
            Material = material; Texture = (Texture2D)material.GetTexture("_MainTex"); Scope = scope;
            materialPath = AssetDatabase.GetAssetPath(material); texturePath = AssetDatabase.GetAssetPath(Texture);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string materialGuid, out long materialId); MaterialGuid = materialGuid; MaterialLocalId = materialId;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(Texture, out string textureGuid, out long textureId); TextureGuid = textureGuid; TextureLocalId = textureId;
            pixels = GuardTextureFingerprintUnity.ReadPixels(Texture);
            frozenPixelHash = GuardTextureFingerprintV1.PixelHash(pixels, Texture.width, Texture.height);
            sourceState = GuardTextureFingerprintUnity.SourceState(material); materialProperties = GuardTextureFingerprintUnity.MaterialProperties(material, Texture);
            foreach (string asset in new[] { materialPath, texturePath }) foreach (string suffix in new[] { "", ".meta" })
            { string path = GuardTextureFingerprintUnity.Absolute(asset) + suffix; GuardTextureFingerprintUnity.NoLinks(path); originalFiles[path] = GuardTextureFingerprintUnity.FileHash(path); }
            BindingId = GuardTextureFingerprintV1.Digest(Encoding.UTF8.GetBytes("LAG/texture-fingerprint/unity-binding/v1\0" + materialGuid + "\0" + materialId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "\0" + textureGuid + "\0" + textureId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + scope + "\0" + sourceState));
        }
        public static GuardTextureFingerprintSource Capture(Material source, string scope) => new GuardTextureFingerprintSource(source, scope);
        public void ValidateUnchanged()
        {
            GuardTextureFingerprintUnity.RequireSource(Material);
            if (Material.GetTexture("_MainTex") != Texture || AssetDatabase.GetAssetPath(Material) != materialPath || AssetDatabase.GetAssetPath(Texture) != texturePath ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(Material, out string mg, out long mi) || mg != MaterialGuid || mi != MaterialLocalId ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(Texture, out string tg, out long ti) || tg != TextureGuid || ti != TextureLocalId || GuardTextureFingerprintUnity.SourceState(Material) != sourceState ||
                GuardTextureFingerprintV1.PixelHash(pixels, Texture.width, Texture.height) != frozenPixelHash)
                throw new InvalidOperationException("TextureFingerprint: fuente/material cambiaron; se conservan los cambios del usuario.");
            foreach (var file in originalFiles) { GuardTextureFingerprintUnity.NoLinks(file.Key); if (GuardTextureFingerprintUnity.FileHash(file.Key) != file.Value) throw new IOException("TextureFingerprint: fuente/meta editada; se conserva."); }
        }
    }
    public static class GuardTextureFingerprintUnity
    {
        const string Prefix = "Assets/LinuxAvatarGuardGenerated/Research/FingerprintTexture/";
        internal static void RequireEditor()
        {
            if (Application.platform != RuntimePlatform.LinuxEditor || Application.unityVersion != "2022.3.22f1" || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan ||
                GraphicsSettings.currentRenderPipeline != null || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("TextureFingerprint: requiere Unity 2022.3.22f1 Linux/Vulkan Built-in, Edit Mode.");
        }
        internal static string Absolute(string path)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..")), result = Path.GetFullPath(Path.Combine(root, path));
            if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("TextureFingerprint: ruta fuera del proyecto."); return result;
        }
        internal static void NoLinks(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attrs; try { attrs = File.GetAttributes(current); } catch (FileNotFoundException) { continue; } catch (DirectoryNotFoundException) { continue; }
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw new IOException("TextureFingerprint: ruta con enlaces rechazada.");
            }
        }
        internal static string FileHash(string path)
        { using (var stream = File.OpenRead(path)) using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        static bool Wrap(TextureWrapMode mode) => mode == TextureWrapMode.Repeat || mode == TextureWrapMode.Clamp;
        internal static void RequireSource(Material material)
        {
            RequireEditor();
            if (material == null || !EditorUtility.IsPersistent(material) || EditorUtility.IsDirty(material) || material.shader == null || material.shader.name != "lilToon" ||
                material.shaderKeywords.Length != 0 || material.GetTag("RenderType", false) != "Opaque" || !(material.GetTexture("_MainTex") is Texture2D texture))
                throw new ArgumentException("TextureFingerprint: material persistente/guardado lilToon opaco sin keywords y con _MainTex requerido.");
            if (!EditorUtility.IsPersistent(texture) || EditorUtility.IsDirty(texture) || !texture.isReadable || !texture.isDataSRGB ||
                (texture.format != TextureFormat.RGBA32 && texture.format != TextureFormat.RGB24) || texture.mipmapCount != 1 || texture.streamingMipmaps ||
                texture.width < 128 || texture.width > 1024 || texture.height != texture.width || (texture.width & (texture.width - 1)) != 0 ||
                !Wrap(texture.wrapModeU) || !Wrap(texture.wrapModeV) || !Wrap(texture.wrapModeW) || texture.anisoLevel > 1 || texture.anisoLevel < 0 ||
                (texture.filterMode != FilterMode.Point && texture.filterMode != FilterMode.Bilinear) || !GuardTextureFingerprintV1.Finite(texture.mipMapBias))
                throw new ArgumentException("TextureFingerprint: albedo sRGB legible/guardado RGB24/RGBA32 de 128–1024, opaco, sin compresión/mips/streaming requerido.");
            foreach (UnityEngine.Object asset in new UnityEngine.Object[] { material, texture })
            {
                string path = AssetDatabase.GetAssetPath(asset);
                if (string.IsNullOrEmpty(path) || path.StartsWith("Assets/LinuxAvatarGuardGenerated/", StringComparison.Ordinal) || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long id) || !GuardTextureFingerprintV1.Hex(guid, 32) || id == 0)
                    throw new ArgumentException("TextureFingerprint: fuente persistente original requerida.");
            }
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));
            if (importer is TextureImporter ti && (ti.textureType != TextureImporterType.Default || !ti.sRGBTexture || ti.mipmapEnabled || ti.streamingMipmaps || ti.textureCompression != TextureImporterCompression.Uncompressed))
                throw new ArgumentException("TextureFingerprint: importador Default sRGB sin compresión/mips/streaming requerido; no se cambia automáticamente.");
            for (int i = 0; i < ShaderUtil.GetPropertyCount(material.shader); i++)
                if (ShaderUtil.GetPropertyType(material.shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    string property = ShaderUtil.GetPropertyName(material.shader, i);
                    if (property != "_MainTex" && material.GetTexture(property) == texture) throw new ArgumentException("TextureFingerprint: la fuente albedo está compartida con otra propiedad; se requiere revisión.");
                    var scale = material.GetTextureScale(property); var offset = material.GetTextureOffset(property);
                    if (!GuardTextureFingerprintV1.Finite(scale.x) || !GuardTextureFingerprintV1.Finite(scale.y) || !GuardTextureFingerprintV1.Finite(offset.x) || !GuardTextureFingerprintV1.Finite(offset.y)) throw new ArgumentException("TextureFingerprint: UV de material no finita.");
                }
            GuardTextureFingerprintV1.RequirePixels(ReadPixels(texture), texture.width, texture.height, true);
        }
        internal static byte[] ReadPixels(Texture2D texture)
        {
            var colors = texture.GetPixels32(0); var result = new byte[colors.Length * 4];
            for (int i = 0; i < colors.Length; i++) { result[i * 4] = colors[i].r; result[i * 4 + 1] = colors[i].g; result[i * 4 + 2] = colors[i].b; result[i * 4 + 3] = colors[i].a; } return result;
        }
        internal static string TextureState(Texture2D texture)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                { writer.Write("LAG/texture-fingerprint/unity-texture/v1"); writer.Write(texture.name); writer.Write((int)texture.hideFlags); writer.Write(texture.width); writer.Write(texture.height); writer.Write((int)texture.format); writer.Write((int)texture.graphicsFormat);
                    writer.Write(texture.isReadable); writer.Write(texture.isDataSRGB); writer.Write(texture.mipmapCount); writer.Write(texture.streamingMipmaps); writer.Write(texture.ignoreMipmapLimit);
                    writer.Write((int)texture.filterMode); writer.Write((int)texture.wrapModeU); writer.Write((int)texture.wrapModeV); writer.Write((int)texture.wrapModeW); writer.Write(texture.anisoLevel); writer.Write(texture.mipMapBias);
                    writer.Write(GuardTextureFingerprintV1.PixelHash(ReadPixels(texture), texture.width, texture.height)); }
                return GuardTextureFingerprintV1.Digest(stream.ToArray());
            }
        }
        internal static string SourceState(Material material)
        {
            var texture = (Texture2D)material.GetTexture("_MainTex");
            return GuardTextureFingerprintV1.Digest(Encoding.UTF8.GetBytes("LAG/texture-fingerprint/unity-source/v1\0" + EditorJsonUtility.ToJson(material) + "\0" + TextureState(texture) + "\0" +
                AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(material)) + "\0" + EditorJsonUtility.ToJson(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)))));
        }
        // Compare effective cloned material state with the same name, flags and original albedo reference.
        internal static string MaterialProperties(Material candidate, Texture2D originalTexture)
        {
            var clone = new Material(candidate) { name = "fp_canonical", hideFlags = HideFlags.None };
            try { clone.SetTexture("_MainTex", originalTexture); return GuardTextureFingerprintV1.Digest(Encoding.UTF8.GetBytes(EditorJsonUtility.ToJson(clone))); }
            finally { UnityEngine.Object.DestroyImmediate(clone); }
        }
        static void Audit(Texture2D texture, Material material, GuardTextureFingerprintSource source, GuardTextureFingerprintEncoding encoding, GuardTextureFingerprintObserver observer)
        {
            if (texture == null || material == null || material.shader != source.Material.shader || material.GetTexture("_MainTex") != texture ||
                texture.width != source.Texture.width || texture.height != source.Texture.height || texture.format != TextureFormat.RGBA32 || !texture.isReadable || !texture.isDataSRGB || texture.mipmapCount != 1 || texture.streamingMipmaps ||
                texture.filterMode != source.Texture.filterMode || texture.wrapModeU != source.Texture.wrapModeU || texture.wrapModeV != source.Texture.wrapModeV || texture.wrapModeW != source.Texture.wrapModeW ||
                texture.anisoLevel != source.Texture.anisoLevel || texture.mipMapBias != source.Texture.mipMapBias || texture.ignoreMipmapLimit != source.Texture.ignoreMipmapLimit || MaterialProperties(material, source.Texture) != source.materialProperties)
                throw new InvalidOperationException("TextureFingerprint: copia/material/layout no conservan el contrato.");
            byte[] pixels = ReadPixels(texture);
            if (GuardTextureFingerprintV1.PixelHash(pixels, texture.width, texture.height) != encoding.PrivateRecord.markedPixelHash) throw new InvalidOperationException("TextureFingerprint: píxeles alterados al importar.");
            var result = observer.Observe(pixels, texture.width, texture.height);
            if (result.UsableSymbols < GuardTextureFingerprintV1.MinimumSymbols || result.MatchedSymbols != result.UsableSymbols) throw new InvalidOperationException("TextureFingerprint: marca perdida en copia Unity.");
        }
        sealed class OwnedFolder
        {
            readonly string folder, guid;
            Dictionary<string, string> files;
            Texture2D texture;
            Material material;
            string textureState, materialState;
            public OwnedFolder(string folder, string guid) { this.folder = folder; this.guid = guid; Record(); }
            Dictionary<string, string> Inventory()
            {
                string path = Absolute(folder); NoLinks(path); NoLinks(path + ".meta");
                if (!Directory.Exists(path) || Directory.GetDirectories(path).Length != 0) throw new IOException("TextureFingerprint: carpeta nueva sustituida; se conserva.");
                var result = new Dictionary<string, string> { { path + ".meta", FileHash(path + ".meta") } };
                foreach (string file in Directory.GetFiles(path)) { NoLinks(file); result.Add(file, FileHash(file)); } return result;
            }
            public void Record()
            {
                var now = Inventory();
                foreach (string path in now.Keys)
                    if (path != Absolute(folder) + ".meta" && !Regex.IsMatch(Path.GetFileName(path), @"\A(?:copy\.asset|material\.mat|fingerprint-manifest\.json)(?:\.meta)?\z"))
                        throw new IOException("TextureFingerprint: archivo inesperado; se conserva carpeta.");
                files = now;
            }
            public void Track(Texture2D texture, Material material) { this.texture = texture; this.material = material; textureState = TextureState(texture); materialState = EditorJsonUtility.ToJson(material); }
            public void ValidateUnchanged()
            {
                if (texture != null && (EditorUtility.IsDirty(texture) || TextureState(texture) != textureState) || material != null && (EditorUtility.IsDirty(material) || EditorJsonUtility.ToJson(material) != materialState))
                    throw new IOException("TextureFingerprint: edición de copias Unity; se conserva carpeta.");
                if (AssetDatabase.AssetPathToGUID(folder) != guid) throw new IOException("TextureFingerprint: GUID de carpeta cambiado; se conserva.");
                var now = Inventory(); if (now.Count != files.Count) throw new IOException("TextureFingerprint: archivos añadidos/retirados; se conserva carpeta.");
                foreach (var file in files) if (!now.TryGetValue(file.Key, out string value) || value != file.Value) throw new IOException("TextureFingerprint: archivo editado/sustituido; se conserva carpeta.");
            }
            public void Rollback() { ValidateUnchanged(); if (!AssetDatabase.DeleteAsset(folder)) throw new IOException("TextureFingerprint: retirar carpeta nueva falló."); }
        }
        static void Parents()
        {
            string current = "Assets";
            foreach (string segment in Prefix.Substring("Assets/".Length).TrimEnd('/').Split('/'))
            {
                string next = current + "/" + segment; NoLinks(Absolute(next));
                if (!AssetDatabase.IsValidFolder(next))
                { if (Directory.Exists(Absolute(next)) || File.Exists(Absolute(next)) || File.Exists(Absolute(next) + ".meta")) throw new IOException("TextureFingerprint: ruta padre ocupada."); if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, segment))) throw new IOException("TextureFingerprint: crear padre falló."); }
                current = next;
            }
        }
        public static GuardTextureFingerprintArtifact Prepare(GuardTextureFingerprintSource source, GuardFingerprintContext context, string outputFolder, string absolutePrivatePath,
            GuardTextureFingerprintOptions options = null, Action<GuardTextureFingerprintCheckpoint> checkpoint = null)
        {
            RequireEditor(); if (source == null || context == null) throw new ArgumentNullException(); source.ValidateUnchanged();
            if (!Regex.IsMatch(outputFolder ?? "", @"\A" + Prefix + @"[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)) throw new ArgumentException("TextureFingerprint: salida Research/FingerprintTexture/<nombre> requerida.");
            string absolute = Absolute(outputFolder); NoLinks(absolute); if (Directory.Exists(absolute) || File.Exists(absolute) || File.Exists(absolute + ".meta")) throw new IOException("TextureFingerprint: salida nueva requerida.");
            GuardFingerprintPrivateStore.GuardFingerprintPrivateLease lease = null; OwnedFolder owned = null; Texture2D copy = null; Material material = null;
            try
            {
                GuardTextureFingerprintEncoding encoding;
                using (var plan = GuardTextureFingerprintV1.Plan(source.pixels, source.Texture.width, source.Texture.height, context, source.BindingId, options)) encoding = plan.Encode(source.pixels);
                var bundle = new GuardTextureFingerprintPrivateBundle { identity = context.ExportPrivateRecord(), texture = encoding.PrivateRecord, textureGuid = source.TextureGuid,
                    textureLocalId = source.TextureLocalId, materialGuid = source.MaterialGuid, materialLocalId = source.MaterialLocalId, scope = source.Scope, sourceState = source.sourceState };
                lease = GuardFingerprintPrivateStore.Create(absolutePrivatePath, JsonUtility.ToJson(bundle, true));
                ReadPrivateBundle(absolutePrivatePath, context.BuildId, source); // Typed JSON/context/encoding replay must succeed before assets.
                checkpoint?.Invoke(GuardTextureFingerprintCheckpoint.PrivateSaved); source.ValidateUnchanged();
                copy = new Texture2D(source.Texture.width, source.Texture.height, TextureFormat.RGBA32, false, false) { name = "fp_" + Guid.NewGuid().ToString("N"), filterMode = source.Texture.filterMode,
                    wrapModeU = source.Texture.wrapModeU, wrapModeV = source.Texture.wrapModeV, wrapModeW = source.Texture.wrapModeW, anisoLevel = source.Texture.anisoLevel, mipMapBias = source.Texture.mipMapBias, ignoreMipmapLimit = source.Texture.ignoreMipmapLimit };
                var colors = new Color32[encoding.Pixels.Length / 4]; for (int i = 0; i < colors.Length; i++) colors[i] = new Color32(encoding.Pixels[i * 4], encoding.Pixels[i * 4 + 1], encoding.Pixels[i * 4 + 2], encoding.Pixels[i * 4 + 3]);
                copy.SetPixels32(colors, 0); copy.Apply(false, false); // Future native execution uses GPU; never invoked by CPU harness.
                material = new Material(source.Material) { name = "fp_" + Guid.NewGuid().ToString("N") }; material.SetTexture("_MainTex", copy);
                using (var observer = GuardTextureFingerprintV1.CreateObserver(context, encoding.PrivateRecord))
                {
                    Audit(copy, material, source, encoding, observer); Parents(); source.ValidateUnchanged();
                    string guid = AssetDatabase.CreateFolder(Prefix.TrimEnd('/'), Path.GetFileName(outputFolder)); if (string.IsNullOrEmpty(guid)) throw new IOException("TextureFingerprint: crear carpeta nueva falló."); owned = new OwnedFolder(outputFolder, guid);
                    string texturePath = outputFolder + "/copy.asset", materialPath = outputFolder + "/material.mat";
                    AssetDatabase.CreateAsset(copy, texturePath); owned.Record(); AssetDatabase.SaveAssetIfDirty(copy); owned.Record();
                    AssetDatabase.CreateAsset(material, materialPath); owned.Record(); AssetDatabase.SaveAssetIfDirty(material); owned.Record();
                    AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate); owned.Record();
                    AssetDatabase.ImportAsset(materialPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate); owned.Record();
                    copy = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath); material = AssetDatabase.LoadAssetAtPath<Material>(materialPath); Audit(copy, material, source, encoding, observer); owned.Track(copy, material);
                    checkpoint?.Invoke(GuardTextureFingerprintCheckpoint.CopiesSaved); owned.ValidateUnchanged(); source.ValidateUnchanged(); Audit(copy, material, source, encoding, observer);
                    var summary = new GuardTextureFingerprintPublicManifest { buildId = context.BuildId, width = copy.width, height = copy.height, usableSymbols = encoding.SelfObservation.UsableSymbols,
                        meanAbsoluteRgb = encoding.MeanAbsoluteRgb, maximumRgb = encoding.MaximumRgb, psnrDb = encoding.PsnrDb };
                    string manifest = outputFolder + "/fingerprint-manifest.json";
                    using (var stream = new FileStream(Absolute(manifest), FileMode.CreateNew, FileAccess.Write, FileShare.None)) using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(JsonUtility.ToJson(summary, true));
                    owned.Record(); AssetDatabase.ImportAsset(manifest, ImportAssetOptions.ForceSynchronousImport); owned.Record();
                    checkpoint?.Invoke(GuardTextureFingerprintCheckpoint.ManifestSaved); owned.ValidateUnchanged(); source.ValidateUnchanged(); Audit(copy, material, source, encoding, observer);
                    lease.Commit(); lease.Dispose(); lease = null;
                    return new GuardTextureFingerprintArtifact { Texture = copy, Material = material, Folder = outputFolder, PrivatePath = absolutePrivatePath, Summary = summary };
                }
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure }; try { owned?.Rollback(); } catch (Exception cleanup) { errors.Add(cleanup); }
                try { lease?.Dispose(); } catch (Exception cleanup) { errors.Add(cleanup); } lease = null;
                if (errors.Count > 1) throw new AggregateException("TextureFingerprint: preparación falló; se preservan archivos que no pueden retirarse con seguridad.", errors); throw;
            }
            finally { if (material != null && !EditorUtility.IsPersistent(material)) UnityEngine.Object.DestroyImmediate(material); if (copy != null && !EditorUtility.IsPersistent(copy)) UnityEngine.Object.DestroyImmediate(copy); }
        }
        public static GuardTextureFingerprintPrivateBundle ReadPrivateBundle(string path, string expectedBuildId, GuardTextureFingerprintSource source)
        {
            RequireEditor(); if (source == null) throw new ArgumentNullException(nameof(source)); source.ValidateUnchanged();
            var record = JsonUtility.FromJson<GuardTextureFingerprintPrivateBundle>(GuardFingerprintPrivateStore.Read(path));
            if (record == null || record.schema != 1 || record.state != "PrototypeCpuSelfVerified" || record.texture == null || record.texture.bindingId != source.BindingId || record.textureGuid != source.TextureGuid ||
                record.textureLocalId != source.TextureLocalId || record.materialGuid != source.MaterialGuid || record.materialLocalId != source.MaterialLocalId || record.scope != source.Scope || record.sourceState != source.sourceState)
                throw new ArgumentException("TextureFingerprint: respaldo de otra fuente/binding.");
            using (var context = GuardFingerprintContext.RestorePrivateRecord(record.identity, expectedBuildId)) using (GuardTextureFingerprintV1.RestorePlan(source.pixels, context, record.texture)) { }
            return record;
        }
    }
}
