// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace LinuxAvatarGuard
{
    // One renderer/slot/material/albedo occurrence, including materials introduced by clips.
    // Content or animation changes intentionally invalidate its identity and private replay.
    public sealed class GuardTextureBindingIdentity
    {
        public const int SchemaVersion = 1;
        public string StableId { get; }
        public string MeshBindingId => MeshBinding.StableId;
        public int Slot { get; }
        public string Property => "_MainTex";
        public string SourceGuid { get; }
        public long SourceLocalFileId { get; }
        public string MaterialGuid { get; }
        public long MaterialLocalFileId { get; }
        public string SourceFingerprint { get; }
        public string ContextFingerprint { get; }
        internal Texture2D Source { get; }
        internal Material Material { get; }
        internal MeshBindingIdentity MeshBinding { get; }
        readonly GuardAnimationContext animation;
        readonly string materialState;

        GuardTextureBindingIdentity(MeshBindingIdentity mesh, GuardAnimationContext animation, int slot, Material material)
        {
            MeshBinding = mesh; this.animation = animation; Slot = slot; Material = material;
            materialState = GuardTextureForge.MaterialState(material);
            Source = (Texture2D)material.GetTexture(Property);
            SourceFingerprint = TextureGuardCodecV1.Fingerprint(Source, true);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(Source, out string textureGuid, out long textureId);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string materialGuid, out long materialId);
            SourceGuid = textureGuid; SourceLocalFileId = textureId; MaterialGuid = materialGuid; MaterialLocalFileId = materialId;
            using (var stream = new MemoryStream())
            {
                using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    w.Write("LAG/texture-context/v1"); w.Write(SchemaVersion);
                    w.Write(mesh.StableId); w.Write(animation.Fingerprint); w.Write(slot); w.Write(Property);
                    w.Write(GuardAnimationContext.Identity(material)); w.Write(materialState);
                    w.Write(GuardAnimationContext.Identity(Source)); w.Write(SourceFingerprint);
                }
                ContextFingerprint = MeshBindingIdentity.Digest(stream.ToArray());
            }
            StableId = MeshBindingIdentity.Digest(Encoding.UTF8.GetBytes("LAG/texture-binding/v1\0" + ContextFingerprint));
        }
        public static GuardTextureBindingIdentity Capture(MeshBindingIdentity mesh, GuardAnimationContext animation, int slot, Material material)
        {
            if (mesh == null || animation == null || !material) throw new ArgumentNullException("Binding/contexto/material");
            mesh.Validate(); animation.Validate();
            if (animation.Root != mesh.Root || slot < 0 || slot >= mesh.Renderer.sharedMaterials.Length ||
                !animation.Materials(mesh.Renderer, slot).Contains(material))
                throw new InvalidOperationException("TextureGuard: material/slot ajeno al binding y sus clips revisados.");
            return new GuardTextureBindingIdentity(mesh, animation, slot, material);
        }
        public void Validate()
        {
            MeshBinding.Validate(); animation.Validate();
            if (!Material || Material.GetTexture(Property) != Source || GuardTextureForge.MaterialState(Material) != materialState ||
                TextureGuardCodecV1.Fingerprint(Source, true) != SourceFingerprint)
                throw new InvalidOperationException("TextureGuard: cambiaron la fuente, material, importador o contexto del binding.");
        }
    }
}
