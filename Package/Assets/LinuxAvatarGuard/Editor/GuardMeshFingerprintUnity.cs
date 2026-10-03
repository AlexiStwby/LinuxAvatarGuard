// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LinuxAvatarGuard
{
    [Serializable]
    public sealed class GuardMeshFingerprintPrivateBundle
    {
        public int schema = 1;
        public string state = "PrototypeCpuSelfVerified";
        public GuardFingerprintPrivateRecord identity;
        public GuardMeshFingerprintPrivateRecord mesh;
        public string sourceGuid, scope, sourceNativeHash;
        public long sourceLocalId;
    }

    [Serializable]
    public sealed class GuardMeshFingerprintPublicManifest
    {
        public int schema = 1, algorithmVersion = GuardMeshFingerprintV1.Version;
        public string state = "PrototypeCpuSelfVerified", algorithm = GuardMeshFingerprintV1.Id, buildId;
        public int markedMeshes = 1, markedTextures, vertices, uniquePositions, usableSymbols;
        public double maximumObjectDisplacement, maximumRelativeDisplacement;
        public bool verifierCalibrated, visualValidated, sdkProcessed;
    }

    public enum GuardMeshFingerprintCheckpoint { PrivateSaved, CopySaved, ManifestSaved }
    public sealed class GuardMeshFingerprintArtifact
    {
        public Mesh Copy { get; internal set; }
        public string Folder { get; internal set; }
        public string PrivatePath { get; internal set; }
        public GuardMeshFingerprintPublicManifest Summary { get; internal set; }
    }

    // Research only. Captures one persistent static mesh; never assigns it to an avatar/renderer.
    public sealed class GuardMeshFingerprintSource
    {
        public Mesh Mesh { get; }
        public string BindingId { get; }
        public string Guid { get; }
        public long LocalId { get; }
        public string Scope { get; }
        internal readonly string assetPath, nativeHash, otherHash, fileHash, metaHash;
        internal readonly float[] positions;
        GuardMeshFingerprintSource(Mesh mesh, string scope)
        {
            GuardMeshFingerprintUnity.ValidateContract(mesh);
            if (!GuardMeshFingerprintV1.Hex(scope, 64)) throw new ArgumentException("MeshFingerprint: scope hexadecimal de 64 caracteres requerido.");
            assetPath = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(assetPath) || assetPath.StartsWith("Assets/LinuxAvatarGuardGenerated/", StringComparison.Ordinal) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long id))
                throw new ArgumentException("MeshFingerprint: una fuente persistente original es requerida.");
            Mesh = mesh; Scope = scope; Guid = guid; LocalId = id;
            positions = GuardMeshFingerprintUnity.Pack(mesh.vertices);
            otherHash = GuardMeshFingerprintUnity.OtherHash(mesh, false);
            nativeHash = GuardMeshFingerprintUnity.NativeHash(mesh);
            string absolute = GuardMeshFingerprintUnity.Absolute(assetPath);
            GuardMeshFingerprintUnity.NoLinks(absolute); fileHash = GuardMeshFingerprintUnity.FileHash(absolute);
            GuardMeshFingerprintUnity.NoLinks(absolute + ".meta"); metaHash = GuardMeshFingerprintUnity.FileHash(absolute + ".meta");
            BindingId = GuardMeshFingerprintV1.Digest(Encoding.UTF8.GetBytes("LAG/mesh-fingerprint/unity-binding/v1\0" + guid + "\0" +
                id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + scope + "\0" + nativeHash));
        }
        public static GuardMeshFingerprintSource Capture(Mesh source, string scope) => new GuardMeshFingerprintSource(source, scope);
        public void ValidateUnchanged()
        {
            GuardMeshFingerprintUnity.ValidateContract(Mesh);
            if (AssetDatabase.GetAssetPath(Mesh) != assetPath || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(Mesh, out string guid, out long id) || guid != Guid || id != LocalId ||
                GuardMeshFingerprintUnity.NativeHash(Mesh) != nativeHash)
                throw new InvalidOperationException("MeshFingerprint: fuente modificada; los cambios del usuario se conservan.");
            string absolute = GuardMeshFingerprintUnity.Absolute(assetPath);
            GuardMeshFingerprintUnity.NoLinks(absolute); GuardMeshFingerprintUnity.NoLinks(absolute + ".meta");
            if (GuardMeshFingerprintUnity.FileHash(absolute) != fileHash || GuardMeshFingerprintUnity.FileHash(absolute + ".meta") != metaHash)
                throw new InvalidOperationException("MeshFingerprint: archivo fuente/meta modificado; se conserva.");
        }
    }

    public static class GuardMeshFingerprintUnity
    {
        const string OutputPrefix = "Assets/LinuxAvatarGuardGenerated/Research/FingerprintMesh/";
        static void RequireEditor()
        {
            if (Application.platform != RuntimePlatform.LinuxEditor || Application.unityVersion != "2022.3.22f1" ||
                SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan || GraphicsSettings.currentRenderPipeline != null ||
                EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("MeshFingerprint: requiere Unity 2022.3.22f1 Linux/Vulkan, Built-in, Edit Mode.");
        }
        internal static string Absolute(string assetPath)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string absolute = Path.GetFullPath(Path.Combine(root, assetPath));
            if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("MeshFingerprint: ruta fuera del proyecto.");
            return absolute;
        }
        internal static void NoLinks(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("MeshFingerprint: ruta con enlaces rechazada.");
            }
        }
        internal static string FileHash(string path)
        { using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        internal static float[] Pack(Vector3[] vertices)
        {
            var result = new float[vertices.Length * 3];
            for (int i = 0; i < vertices.Length; i++) { result[i * 3] = vertices[i].x; result[i * 3 + 1] = vertices[i].y; result[i * 3 + 2] = vertices[i].z; }
            return result;
        }
        static bool Finite(float value) => GuardMeshFingerprintOptions.Finite(value);
        static void Write(BinaryWriter writer, Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
        static void Write(BinaryWriter writer, Vector4 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }
        static void Write(BinaryWriter writer, Bounds value) { Write(writer, value.center); Write(writer, value.extents); }
        static void Validate(Bounds bounds)
        {
            if (!Finite(bounds.center.x) || !Finite(bounds.center.y) || !Finite(bounds.center.z) ||
                !Finite(bounds.extents.x) || !Finite(bounds.extents.y) || !Finite(bounds.extents.z) || bounds.extents.x < 0 || bounds.extents.y < 0 || bounds.extents.z < 0)
                throw new ArgumentException("MeshFingerprint: bounds inválidos.");
        }
        internal static void ValidateContract(Mesh mesh)
        {
            RequireEditor();
            if (mesh == null || !EditorUtility.IsPersistent(mesh) || EditorUtility.IsDirty(mesh) || !mesh.isReadable || mesh.blendShapeCount != 0 || mesh.bindposes.Length != 0 ||
                mesh.HasVertexAttribute(VertexAttribute.BlendWeight) || mesh.HasVertexAttribute(VertexAttribute.BlendIndices) ||
                mesh.vertexCount < GuardMeshFingerprintV1.MinimumSamples * GuardMeshFingerprintV1.MinimumSymbols || mesh.vertexCount > GuardMeshFingerprintV1.MaximumVertices ||
                mesh.subMeshCount < 1 || mesh.subMeshCount > 64)
                throw new ArgumentException("MeshFingerprint: piloto limitado a mesh estática, legible y guardada, sin skinning/blendshapes.");
            foreach (var attribute in mesh.GetVertexAttributes())
            {
                bool positionOrNormal = attribute.attribute == VertexAttribute.Position || attribute.attribute == VertexAttribute.Normal;
                bool tangent = attribute.attribute == VertexAttribute.Tangent, color = attribute.attribute == VertexAttribute.Color;
                bool uv = attribute.attribute >= VertexAttribute.TexCoord0 && attribute.attribute <= VertexAttribute.TexCoord7;
                if (!(positionOrNormal || tangent || color || uv) || (positionOrNormal && attribute.dimension != 3) || ((tangent || color) && attribute.dimension != 4) ||
                    (uv && (attribute.dimension < 1 || attribute.dimension > 4)) ||
                    !(attribute.format == VertexAttributeFormat.Float32 || (color && attribute.format == VertexAttributeFormat.UNorm8)))
                    throw new ArgumentException("MeshFingerprint: layout fuera del piloto.");
            }
            if (!mesh.HasVertexAttribute(VertexAttribute.Position) || !mesh.HasVertexAttribute(VertexAttribute.Normal)) throw new ArgumentException("MeshFingerprint: posiciones y normales requeridas.");
            var positions = mesh.vertices; Validate(mesh.bounds);
            foreach (var vertex in positions) if (!Finite(vertex.x) || !Finite(vertex.y) || !Finite(vertex.z) || !mesh.bounds.Contains(vertex)) throw new ArgumentException("MeshFingerprint: posiciones/bounds incompatibles.");
            foreach (var normal in mesh.normals) if (!Finite(normal.x) || !Finite(normal.y) || !Finite(normal.z) || Math.Abs(normal.sqrMagnitude - 1) > 0.004) throw new ArgumentException("MeshFingerprint: normales inválidas.");
            foreach (var tangent in mesh.tangents) if (!Finite(tangent.x) || !Finite(tangent.y) || !Finite(tangent.z) || !Finite(tangent.w) || Math.Abs(tangent.w) != 1 || Math.Abs(new Vector3(tangent.x, tangent.y, tangent.z).sqrMagnitude - 1) > 0.004) throw new ArgumentException("MeshFingerprint: tangentes inválidas.");
            var uvs = new List<Vector4>();
            for (int i = 0; i < 8; i++) { mesh.GetUVs(i, uvs); foreach (var uv in uvs) if (!Finite(uv.x) || !Finite(uv.y) || !Finite(uv.z) || !Finite(uv.w)) throw new ArgumentException("MeshFingerprint: UV no finita."); }
            foreach (var color in mesh.colors) if (!Finite(color.r) || !Finite(color.g) || !Finite(color.b) || !Finite(color.a)) throw new ArgumentException("MeshFingerprint: color no finito.");
            long total = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var sub = mesh.GetSubMesh(i); Validate(sub.bounds); total += sub.indexCount;
                if (sub.topology != MeshTopology.Triangles || sub.indexCount % 3 != 0 || total > 8000000) throw new ArgumentException("MeshFingerprint: topología fuera del piloto.");
                foreach (int index in mesh.GetIndices(i, true)) if (index < 0 || index >= positions.Length || !sub.bounds.Contains(positions[index])) throw new ArgumentException("MeshFingerprint: índice/bounds de submesh inválidos.");
            }
        }
        // Semantic attributes and layout, not a promise of byte-identical GPU/native buffers.
        internal static string OtherHash(Mesh mesh, bool bounds)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write("LAG/mesh-fingerprint/unity-other/v1"); writer.Write(mesh.vertexCount); writer.Write((int)mesh.indexFormat);
                    var attributes = mesh.GetVertexAttributes(); writer.Write(attributes.Length);
                    foreach (var a in attributes) { writer.Write((int)a.attribute); writer.Write((int)a.format); writer.Write(a.dimension); writer.Write(a.stream); }
                    var normals = mesh.normals; writer.Write(normals.Length); foreach (var n in normals) Write(writer, n);
                    var tangents = mesh.tangents; writer.Write(tangents.Length); foreach (var t in tangents) Write(writer, t);
                    var uvs = new List<Vector4>(); for (int i = 0; i < 8; i++) { mesh.GetUVs(i, uvs); writer.Write(uvs.Count); foreach (var uv in uvs) Write(writer, uv); }
                    var colors = mesh.colors; writer.Write(colors.Length); foreach (var c in colors) { writer.Write(c.r); writer.Write(c.g); writer.Write(c.b); writer.Write(c.a); }
                    writer.Write(mesh.subMeshCount);
                    for (int i = 0; i < mesh.subMeshCount; i++)
                    {
                        var s = mesh.GetSubMesh(i); writer.Write(s.indexStart); writer.Write(s.indexCount); writer.Write((int)s.topology); writer.Write(s.baseVertex); writer.Write(s.firstVertex); writer.Write(s.vertexCount);
                        var indices = mesh.GetIndices(i, false); writer.Write(indices.Length); foreach (int index in indices) writer.Write(index); if (bounds) Write(writer, s.bounds);
                    }
                    if (bounds) Write(writer, mesh.bounds);
                }
                return GuardMeshFingerprintV1.Digest(stream.ToArray());
            }
        }
        internal static string NativeHash(Mesh mesh) => GuardMeshFingerprintV1.Digest(Encoding.UTF8.GetBytes("LAG/mesh-fingerprint/unity-native/v1\0" + mesh.name + "\0" + GuardMeshFingerprintV1.PositionHash(Pack(mesh.vertices)) + "\0" + OtherHash(mesh, true)));
        static void ValidateTriangles(Mesh source, Vector3[] marked)
        {
            var original = source.vertices; const double cosine = 0.9999904807207345; // 0.25 degrees.
            for (int sub = 0; sub < source.subMeshCount; sub++)
            {
                var indices = source.GetIndices(sub, true);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    var before = Vector3.Cross(original[b] - original[a], original[c] - original[a]);
                    var after = Vector3.Cross(marked[b] - marked[a], marked[c] - marked[a]);
                    double lengthBefore = before.magnitude, lengthAfter = after.magnitude;
                    if (!(lengthBefore > 0) || !(lengthAfter > 0) || lengthAfter / lengthBefore < 0.99 || lengthAfter / lengthBefore > 1.01 ||
                        Vector3.Dot(before, after) / (lengthBefore * lengthAfter) < cosine)
                        throw new InvalidOperationException("MeshFingerprint: triángulo degenerado o deformación local fuera del piloto.");
                }
            }
        }
        static void AuditCopy(Mesh copy, GuardMeshFingerprintSource source, GuardMeshFingerprintEncoding encoding, GuardMeshFingerprintObserver observer)
        {
            if (copy == null || !copy.isReadable || copy.blendShapeCount != 0 || copy.bindposes.Length != 0)
                throw new InvalidOperationException("MeshFingerprint: copia fuera del contrato estático.");
            var vertices = copy.vertices; var packed = Pack(vertices);
            if (OtherHash(copy, false) != source.otherHash || GuardMeshFingerprintV1.PositionHash(packed) != encoding.PrivateRecord.markedPositionHash)
                throw new InvalidOperationException("MeshFingerprint: copia no conserva los atributos/topología esperados.");
            Validate(copy.bounds); foreach (var vertex in vertices) if (!copy.bounds.Contains(vertex)) throw new InvalidOperationException("MeshFingerprint: bounds de copia insuficientes.");
            for (int i = 0; i < copy.subMeshCount; i++) { var sub = copy.GetSubMesh(i); Validate(sub.bounds); foreach (int index in copy.GetIndices(i, true)) if (!sub.bounds.Contains(vertices[index])) throw new InvalidOperationException("MeshFingerprint: bounds de submesh insuficientes."); }
            var result = observer.Observe(packed);
            if (result.UsableSymbols < GuardMeshFingerprintV1.MinimumSymbols || result.MatchedSymbols != result.UsableSymbols) throw new InvalidOperationException("MeshFingerprint: marca perdida en copia Unity.");
        }
        static void CreateParents(string parent)
        {
            string current = "Assets";
            foreach (string segment in parent.Substring("Assets/".Length).Split('/'))
            {
                string next = current + "/" + segment; NoLinks(Absolute(next));
                if (!AssetDatabase.IsValidFolder(next))
                { if (File.Exists(Absolute(next)) || File.Exists(Absolute(next) + ".meta") || Directory.Exists(Absolute(next))) throw new IOException("MeshFingerprint: ruta padre ya ocupada."); if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, segment))) throw new IOException("MeshFingerprint: crear directorio padre."); }
                current = next;
            }
        }
        sealed class OwnedFolder
        {
            readonly string folder, guid;
            Dictionary<string, string> files;
            Mesh trackedCopy;
            string copyHash;
            public OwnedFolder(string folder, string guid) { this.folder = folder; this.guid = guid; Record(); }
            Dictionary<string, string> Inventory()
            {
                string absolute = Absolute(folder); NoLinks(absolute); NoLinks(absolute + ".meta");
                if (!Directory.Exists(absolute) || Directory.GetDirectories(absolute).Length != 0) throw new IOException("MeshFingerprint: carpeta generada sustituida; se conserva.");
                var result = new Dictionary<string, string> { { absolute + ".meta", FileHash(absolute + ".meta") } };
                foreach (string file in Directory.GetFiles(absolute)) { NoLinks(file); result.Add(file, FileHash(file)); } return result;
            }
            public void Record()
            {
                var now = Inventory();
                foreach (string path in now.Keys)
                    if (path != Absolute(folder) + ".meta" && Path.GetFileName(path) != "copy.asset" && Path.GetFileName(path) != "copy.asset.meta" &&
                        Path.GetFileName(path) != "fingerprint-manifest.json" && Path.GetFileName(path) != "fingerprint-manifest.json.meta")
                        throw new IOException("MeshFingerprint: archivo inesperado; se conserva carpeta.");
                files = now;
            }
            public void TrackCopy(Mesh copy) { trackedCopy = copy; copyHash = NativeHash(copy); }
            public void ValidateUnchanged()
            {
                if (trackedCopy != null && (EditorUtility.IsDirty(trackedCopy) || trackedCopy.blendShapeCount != 0 || trackedCopy.bindposes.Length != 0 || NativeHash(trackedCopy) != copyHash))
                    throw new IOException("MeshFingerprint: edición de copia Unity; se conserva carpeta.");
                if (AssetDatabase.AssetPathToGUID(folder) != guid) throw new IOException("MeshFingerprint: identidad de carpeta modificada; se conserva.");
                var now = Inventory();
                if (now.Count != files.Count) throw new IOException("MeshFingerprint: archivos añadidos/retirados; se conserva carpeta.");
                foreach (var item in files) if (!now.TryGetValue(item.Key, out string value) || value != item.Value) throw new IOException("MeshFingerprint: archivo generado editado; se conserva carpeta.");
            }
            public void Rollback() { ValidateUnchanged(); if (!AssetDatabase.DeleteAsset(folder)) throw new IOException("MeshFingerprint: rollback de carpeta falló."); }
        }
        public static GuardMeshFingerprintArtifact Prepare(GuardMeshFingerprintSource source, GuardFingerprintContext context, string outputFolder,
            string absolutePrivatePath, GuardMeshFingerprintOptions options = null, Action<GuardMeshFingerprintCheckpoint> checkpoint = null)
        {
            RequireEditor(); if (source == null || context == null) throw new ArgumentNullException(); source.ValidateUnchanged();
            if (!Regex.IsMatch(outputFolder ?? "", @"\A" + OutputPrefix + @"[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)) throw new ArgumentException("MeshFingerprint: salida Research/FingerprintMesh/<nombre> requerida.");
            string absolute = Absolute(outputFolder); NoLinks(absolute);
            if (Directory.Exists(absolute) || File.Exists(absolute) || File.Exists(absolute + ".meta")) throw new IOException("MeshFingerprint: salida nueva requerida.");
            GuardFingerprintPrivateStore.GuardFingerprintPrivateLease lease = null; OwnedFolder owned = null; Mesh copy = null;
            try
            {
                GuardMeshFingerprintEncoding encoding;
                using (var plan = GuardMeshFingerprintV1.Plan(source.positions, context, source.BindingId, options)) encoding = plan.Encode(source.positions);
                var vertices = new Vector3[encoding.Positions.Length / 3]; for (int i = 0; i < vertices.Length; i++) vertices[i] = new Vector3(encoding.Positions[i * 3], encoding.Positions[i * 3 + 1], encoding.Positions[i * 3 + 2]);
                ValidateTriangles(source.Mesh, vertices);
                var privateBundle = new GuardMeshFingerprintPrivateBundle { identity = context.ExportPrivateRecord(), mesh = encoding.PrivateRecord, sourceGuid = source.Guid,
                    sourceLocalId = source.LocalId, scope = source.Scope, sourceNativeHash = source.nativeHash };
                lease = GuardFingerprintPrivateStore.Create(absolutePrivatePath, JsonUtility.ToJson(privateBundle, true)); checkpoint?.Invoke(GuardMeshFingerprintCheckpoint.PrivateSaved); source.ValidateUnchanged();
                copy = UnityEngine.Object.Instantiate(source.Mesh); copy.name = "fp_" + System.Guid.NewGuid().ToString("N"); copy.vertices = vertices;
                float margin = (float)(encoding.MaximumDisplacement + 1e-6);
                var bounds = source.Mesh.bounds; bounds.Expand(2 * margin); copy.bounds = bounds;
                for (int i = 0; i < copy.subMeshCount; i++) { var sub = source.Mesh.GetSubMesh(i); bounds = sub.bounds; bounds.Expand(2 * margin); sub.bounds = bounds; copy.SetSubMesh(i, sub, MeshUpdateFlags.DontRecalculateBounds); }
                using (var observer = GuardMeshFingerprintV1.CreateObserver(context, encoding.PrivateRecord))
                {
                    AuditCopy(copy, source, encoding, observer); CreateParents(OutputPrefix.TrimEnd('/')); source.ValidateUnchanged();
                    string guid = AssetDatabase.CreateFolder(OutputPrefix.TrimEnd('/'), Path.GetFileName(outputFolder));
                    if (string.IsNullOrEmpty(guid)) throw new IOException("MeshFingerprint: crear carpeta nueva."); owned = new OwnedFolder(outputFolder, guid);
                    string meshPath = outputFolder + "/copy.asset"; AssetDatabase.CreateAsset(copy, meshPath); owned.Record(); AssetDatabase.SaveAssetIfDirty(copy); owned.Record();
                    AssetDatabase.ImportAsset(meshPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate); owned.Record();
                    copy = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath); AuditCopy(copy, source, encoding, observer); owned.TrackCopy(copy);
                    checkpoint?.Invoke(GuardMeshFingerprintCheckpoint.CopySaved); owned.ValidateUnchanged(); source.ValidateUnchanged(); AuditCopy(copy, source, encoding, observer);
                    var summary = new GuardMeshFingerprintPublicManifest { buildId = context.BuildId, vertices = vertices.Length, uniquePositions = encoding.SelfObservation.UniquePositions,
                        usableSymbols = encoding.SelfObservation.UsableSymbols, maximumObjectDisplacement = encoding.MaximumDisplacement, maximumRelativeDisplacement = encoding.MaximumRelativeDisplacement };
                    string manifestPath = outputFolder + "/fingerprint-manifest.json";
                    using (var stream = new FileStream(Absolute(manifestPath), FileMode.CreateNew, FileAccess.Write, FileShare.None)) using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(JsonUtility.ToJson(summary, true));
                    owned.Record(); AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceSynchronousImport); owned.Record();
                    checkpoint?.Invoke(GuardMeshFingerprintCheckpoint.ManifestSaved); owned.ValidateUnchanged(); source.ValidateUnchanged(); AuditCopy(copy, source, encoding, observer);
                    lease.Commit(); lease.Dispose(); lease = null;
                    return new GuardMeshFingerprintArtifact { Copy = copy, Folder = outputFolder, PrivatePath = absolutePrivatePath, Summary = summary };
                }
            }
            catch (Exception failure)
            {
                var failures = new List<Exception> { failure };
                try { owned?.Rollback(); } catch (Exception cleanup) { failures.Add(cleanup); }
                try { lease?.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); } lease = null;
                if (failures.Count > 1) throw new AggregateException("MeshFingerprint: preparación falló y se conservaron archivos que no se pueden retirar con seguridad.", failures);
                throw;
            }
            finally { if (copy != null && !EditorUtility.IsPersistent(copy)) UnityEngine.Object.DestroyImmediate(copy); }
        }
        public static GuardMeshFingerprintPrivateBundle ReadPrivateBundle(string absolutePath, string expectedBuildId, GuardMeshFingerprintSource source)
        {
            RequireEditor(); if (source == null) throw new ArgumentNullException(nameof(source)); source.ValidateUnchanged();
            var record = JsonUtility.FromJson<GuardMeshFingerprintPrivateBundle>(GuardFingerprintPrivateStore.Read(absolutePath));
            if (record == null || record.schema != 1 || record.state != "PrototypeCpuSelfVerified" || record.mesh == null || record.mesh.bindingId != source.BindingId ||
                record.sourceGuid != source.Guid || record.sourceLocalId != source.LocalId || record.scope != source.Scope || record.sourceNativeHash != source.nativeHash)
                throw new ArgumentException("MeshFingerprint: respaldo de otra fuente/binding.");
            using (var context = GuardFingerprintContext.RestorePrivateRecord(record.identity, expectedBuildId))
            using (GuardMeshFingerprintV1.RestorePlan(source.positions, context, record.mesh)) { }
            return record;
        }
    }
}
