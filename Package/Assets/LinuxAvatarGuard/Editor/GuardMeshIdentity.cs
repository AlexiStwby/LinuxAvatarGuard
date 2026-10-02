// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace LinuxAvatarGuard
{
    // Captured before cloning/encoding. IDs deliberately change when source content or the binding changes.
    public sealed class MeshBindingIdentity
    {
        public const int SchemaVersion = 1;
        public string StableId { get; }
        public string SourceGuid { get; }
        public long SourceLocalFileId { get; }
        public string ContentHash { get; }
        readonly GameObject root;
        readonly Renderer renderer;
        readonly Mesh source;
        internal Mesh Source => source;
        internal GameObject Root => root;
        internal Renderer Renderer => renderer;
        MeshBindingIdentity(GameObject root, Renderer renderer, Mesh source, string guid, long fileId, string content, string id)
        {
            this.root = root; this.renderer = renderer; this.source = source;
            SourceGuid = guid; SourceLocalFileId = fileId; ContentHash = content; StableId = id;
        }
        public static MeshBindingIdentity Capture(GameObject root, Renderer renderer)
        {
            if (!root || !renderer || (renderer.transform != root.transform && !renderer.transform.IsChildOf(root.transform)))
                throw new ArgumentException("El renderer debe pertenecer a la raíz de trabajo.");
            var mesh = StaticPolymorphicCodecV1.RequireStaticRenderer(renderer);
            StaticPolymorphicCodecV1.ValidateSource(mesh, .1f, false);
            if (!AssetDatabase.Contains(mesh) || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long fileId) ||
                !MeshKeyDerivation.IsHex(guid, 32) || fileId == 0)
                throw new InvalidOperationException("Guarda la malla fuente como asset antes de capturar su identidad estable.");
            var content = ContentFingerprint(mesh);
            var segments = new List<Transform>();
            for (var node = renderer.transform; node != root.transform; node = node.parent) segments.Add(node);
            segments.Reverse();
            using (var stream = new MemoryStream())
            {
                using (var output = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    output.Write("LAG/mesh-binding/v1"); output.Write(SchemaVersion);
                    output.Write(guid); output.Write(fileId); output.Write(content);
                    output.Write(segments.Count);
                    foreach (var segment in segments) { output.Write(segment.name); output.Write(segment.GetSiblingIndex()); }
                    output.Write(renderer.GetType().FullName);
                    output.Write(Array.IndexOf(renderer.GetComponents<Renderer>(), renderer));
                }
                return new MeshBindingIdentity(root, renderer, mesh, guid, fileId, content, Digest(stream.ToArray()));
            }
        }
        public void Validate()
        {
            if (!root || !renderer || !source) throw new InvalidOperationException("La fuente o el binding ya no existen.");
            var current = Capture(root, renderer);
            if (current.source != source || current.StableId != StableId)
                throw new InvalidOperationException("La malla o su binding cambiaron después de capturar la identidad.");
        }
        internal static string Digest(byte[] bytes)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        // Preserve the Stage 4 content contract byte-for-byte. Only static meshes pass Capture/Plan.
        public static string ContentFingerprint(Mesh mesh)
        {
            if (!mesh || !mesh.isReadable) throw new ArgumentException("Se requiere una malla legible.");
            using (var stream = new MemoryStream())
            {
                using (var output = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    output.Write("LAG/static-source/v1"); output.Write((int)mesh.indexFormat); output.Write(mesh.vertexCount);
                    foreach (var attribute in mesh.GetVertexAttributes())
                    { output.Write((int)attribute.attribute); output.Write((int)attribute.format); output.Write(attribute.dimension); output.Write(attribute.stream); }
                    output.Write(-1);
                    foreach (var v in mesh.vertices) { output.Write(v.x); output.Write(v.y); output.Write(v.z); }
                    foreach (var v in mesh.normals) { output.Write(v.x); output.Write(v.y); output.Write(v.z); }
                    output.Write(mesh.tangents.Length); foreach (var v in mesh.tangents) for (int j = 0; j < 4; j++) output.Write(v[j]);
                    var uv = new List<Vector4>();
                    for (int channel = 0; channel < 8; channel++)
                    { mesh.GetUVs(channel, uv); output.Write(uv.Count); foreach (var v in uv) for (int j = 0; j < 4; j++) output.Write(v[j]); }
                    output.Write(mesh.colors.Length); foreach (var v in mesh.colors) for (int j = 0; j < 4; j++) output.Write(v[j]);
                    output.Write(mesh.subMeshCount);
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    { output.Write((int)mesh.GetTopology(sub)); var indices = mesh.GetIndices(sub); output.Write(indices.Length); foreach (int index in indices) output.Write(index); }
                    for (int j = 0; j < 3; j++) { output.Write(mesh.bounds.center[j]); output.Write(mesh.bounds.extents[j]); }
                }
                return Digest(stream.ToArray());
            }
        }
    }
}
