// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace LinuxAvatarGuard
{
    // Research context, not a GuardProfile and never an avatar upload readiness certificate.
    public sealed class GuardBuildContext : IDisposable
    {
        public const int SchemaVersion = 5;
        public string BuildId { get; }
        public string CreatedUtc { get; }
        readonly byte[] masterSeed;
        readonly Dictionary<string, BindingRecord> bindings = new Dictionary<string, BindingRecord>();
        readonly Dictionary<string, TextureRecord> textures = new Dictionary<string, TextureRecord>();
        readonly bool restored;
        bool disposed;
        public int BindingCount => bindings.Count;
        public int TextureBindingCount => textures.Count;
        public int SkinningBindingCount => bindings.Values.Count(b => b.skinned);
        GuardBuildContext(string buildId, byte[] seed, string created, bool restored)
        {
            if (!MeshKeyDerivation.IsHex(buildId, 32) || seed == null || seed.Length != 32)
                throw new ArgumentException("BuildID/seed privado inválido.");
            if (!DateTime.TryParseExact(created, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) || date.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Timestamp privado inválido.");
            BuildId = buildId; masterSeed = (byte[])seed.Clone(); CreatedUtc = created; this.restored = restored;
        }
        public static GuardBuildContext CreateRandom()
        {
            var seed = new byte[32];
            try { using (var random = RandomNumberGenerator.Create()) random.GetBytes(seed); return CreateReproducible(Guid.NewGuid().ToString("N"), seed); }
            finally { Array.Clear(seed, 0, seed.Length); }
        }
        // Explicit reproducibility requires both BuildID and seed; production research builds use CreateRandom.
        public static GuardBuildContext CreateReproducible(string buildId, byte[] masterSeed) =>
            new GuardBuildContext(buildId, masterSeed, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), false);
        void Alive() { if (disposed) throw new ObjectDisposedException(nameof(GuardBuildContext)); }
        // Recorded bindings reproduce their policy; contextual records require the same animation analysis.
        public StaticPolymorphicCodecV1 CreateCodec(MeshBindingIdentity binding) => CreateCodecCore(binding, false);
        public StaticPolymorphicCodecV1 CreateDynamicCodec(MeshBindingIdentity binding) => CreateCodecCore(binding, true);
        public StaticPolymorphicCodecV1 CreateContextualCodec(MeshBindingIdentity binding, GuardAnimationContext animation)
        {if(animation==null)throw new ArgumentNullException(nameof(animation));return CreateCodecCore(binding,true,animation);}
        public SkinnedLinearCodecV1 CreateSkinningCodec(MeshBindingIdentity binding, GuardAnimationContext animation)
        {
            Alive();if(binding==null||animation==null||!binding.IsSkinned||!animation.SkinningEnabled||binding.Root!=animation.Root)throw new ArgumentException("SkinGuard: binding/análisis opt-in requeridos.");
            binding.Validate();animation.Validate();
            if(restored&&!bindings.ContainsKey(binding.StableId))throw new InvalidOperationException("SkinGuard: binding ajeno al respaldo privado.");
            bindings.TryGetValue(binding.StableId,out var record);
            if(record!=null&&(!record.skinned||record.skinningVersion!=SkinnedLinearCodecV1.Version||record.skinningHash!=binding.SkinningFingerprint||record.sourceGuid!=binding.SourceGuid||record.localFileId!=binding.SourceLocalFileId||record.contentHash!=binding.ContentHash))throw new InvalidOperationException("SkinGuard: registro/política de fuente incompatible.");
            byte[] program=null,payload=null,allocation=null;int[] runtime=null;
            try
            {
                program=DeriveTyped("LAG/skinning/v1/program",binding.StableId);payload=DeriveTyped("LAG/skinning/v1/payload",binding.StableId);allocation=DeriveTyped("LAG/skinning/v1/layout",binding.StableId);runtime=RuntimeKey();
                var usage=AttributeAllocator.Inspect(binding,animation);var layout=AttributeAllocator.Allocate(usage,allocation);
                var codec=new SkinnedLinearCodecV1(program,payload,binding,usage,layout,runtime,record?.strength);
                try{if(record!=null&&(record.usageHash!=usage.Fingerprint||record.firstUv!=layout.FirstUvChannel||record.secondUv!=layout.SecondUvChannel||record.programHash!=SkinnedLinearCodecV1.ProgramHash(codec.Plan(binding.Source,record.strength))))throw new InvalidOperationException("SkinGuard: programa privado ya no se reproduce.");return codec;}
                catch{codec.Dispose();throw;}
            }
            finally{foreach(var bytes in new[]{program,payload,allocation})if(bytes!=null)Array.Clear(bytes,0,bytes.Length);if(runtime!=null)Array.Clear(runtime,0,runtime.Length);}
        }
        byte[] DeriveTyped(string domain,string identity)
        {using(var hash=SHA256.Create()){var salt=hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(domain+"/salt\0"+BuildId));try{return MeshKeyDerivation.HkdfSha256(masterSeed,salt,System.Text.Encoding.UTF8.GetBytes(domain+"\0"+BuildId+"\0"+identity),32);}finally{Array.Clear(salt,0,salt.Length);}}}
        public TextureGuardCodecV2 CreateMipTextureCodec(GuardTextureBindingIdentity binding)
        {
            Alive();if(binding==null||!binding.MipmapsEnabled)throw new ArgumentException("TextureGuard V2: binding mip opt-in requerido.");binding.Validate();
            if(restored&&(!bindings.ContainsKey(binding.MeshBindingId)||!textures.ContainsKey(binding.StableId)))throw new InvalidOperationException("TextureGuard V2: binding ajeno al respaldo privado.");
            byte[] derived=null;int[] runtime=null;
            try
            {
                derived=DeriveTyped("LAG/texture-binding-kdf/v2/"+TextureGuardCodecV2.Id,binding.StableId);runtime=RuntimeKey();var codec=new TextureGuardCodecV2(derived,binding,runtime);
                try{if(textures.TryGetValue(binding.StableId,out var record)&&JsonUtility.ToJson(record)!=JsonUtility.ToJson(TextureRecordOf(binding,codec.Plan(binding.Source))))throw new InvalidOperationException("TextureGuard V2: programa privado incompatible.");return codec;}
                catch{codec.Dispose();throw;}
            }
            finally{if(derived!=null)Array.Clear(derived,0,derived.Length);if(runtime!=null)Array.Clear(runtime,0,runtime.Length);}
        }
        public TextureGuardCodecV1 CreateTextureCodec(GuardTextureBindingIdentity binding)
        {
            Alive(); if (binding == null) throw new ArgumentNullException(nameof(binding)); binding.Validate();
            if(binding.MipmapsEnabled)throw new InvalidOperationException("El binding V2 requiere CreateMipTextureCodec.");
            if (restored && (!bindings.ContainsKey(binding.MeshBindingId) || !textures.ContainsKey(binding.StableId)))
                throw new InvalidOperationException("El binding de textura no pertenece al contexto privado restaurado.");
            byte[] salt = null, derived = null; int[] runtime = null;
            try
            {
                // A separate typed HKDF domain; historical mesh/runtime derivation stays byte-identical.
                using (var hash = SHA256.Create()) salt = hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes("LAG/texture-build-salt/v1\0" + BuildId));
                var info = System.Text.Encoding.UTF8.GetBytes("LAG/texture-binding-kdf/v1\0" + TextureGuardCodecV1.Id + "\0" +
                    TextureGuardCodecV1.Version + "\0" + BuildId + "\0" + binding.StableId);
                derived = MeshKeyDerivation.HkdfSha256(masterSeed, salt, info, 32); runtime = RuntimeKey();
                var codec = new TextureGuardCodecV1(derived, binding, runtime);
                try
                {
                    if (textures.TryGetValue(binding.StableId, out var record) && JsonUtility.ToJson(record) !=
                        JsonUtility.ToJson(TextureRecordOf(binding, codec.Plan(binding.Source))))
                        throw new InvalidOperationException("El registro privado de textura no corresponde al programa reproducido.");
                    return codec;
                }
                catch { codec.Dispose(); throw; }
            }
            finally
            {
                if (salt != null) Array.Clear(salt, 0, salt.Length); if (derived != null) Array.Clear(derived, 0, derived.Length);
                if (runtime != null) Array.Clear(runtime, 0, runtime.Length);
            }
        }
        StaticPolymorphicCodecV1 CreateCodecCore(MeshBindingIdentity binding, bool dynamicAttributes, GuardAnimationContext animation=null)
        {
            Alive(); if (binding == null) throw new ArgumentNullException(nameof(binding)); binding.Validate();
            if(binding.IsSkinned)throw new InvalidOperationException("El binding skinned requiere CreateSkinningCodec; no admite el codec rígido.");
            if (restored && !bindings.ContainsKey(binding.StableId)) throw new InvalidOperationException("El binding no pertenece al contexto privado restaurado.");
            if (bindings.TryGetValue(binding.StableId, out var record) &&
                (record.skinned || record.sourceGuid != binding.SourceGuid || record.localFileId != binding.SourceLocalFileId || record.contentHash != binding.ContentHash))
                throw new InvalidOperationException("La metadata privada no corresponde a la fuente.");
            if (record != null && dynamicAttributes && record.attributePolicy == 0)
                throw new InvalidOperationException("Un binding privado fijo no puede reinterpretarse como dinámico.");
            dynamicAttributes |= record != null && record.attributePolicy != 0;
            if(record!=null && ((record.attributePolicy==AttributeAllocator.ContextualPolicyVersion)!=(animation!=null)))
                throw new InvalidOperationException("El registro contextual requiere su análisis de animación; no puede cambiar de política.");
            byte[] program = null, payload = null, layoutSeed = null;
            int[] runtime = null;
            try
            {
                program = MeshKeyDerivation.Derive(masterSeed, BuildId, binding, MeshDerivationPurpose.Program);
                payload = MeshKeyDerivation.Derive(masterSeed, BuildId, binding, MeshDerivationPurpose.Payload);
                runtime = RuntimeKey();
                AttributeUsageAnalysis usage = null; AttributeLayout layout = null;
                if (dynamicAttributes)
                {
                    usage = AttributeAllocator.Inspect(binding,animation);
                    layoutSeed = MeshKeyDerivation.Derive(masterSeed, BuildId, binding, MeshDerivationPurpose.AttributeLayout);
                    layout = AttributeAllocator.Allocate(usage, layoutSeed);
                    if (record != null && (record.usageHash != usage.Fingerprint || record.firstUv != layout.FirstUvChannel || record.secondUv != layout.SecondUvChannel))
                        throw new InvalidOperationException("El layout/uso de materiales privado ya no corresponde al binding.");
                }
                var codec = new StaticPolymorphicCodecV1(program, payload, binding, record?.strength, runtime, layout, usage);
                try
                {
                    if (record != null && StaticPolymorphicCodecV1.ProgramHash(codec.Plan(binding.Source, record.strength)) != record.programHash)
                        throw new InvalidOperationException("El hash privado no corresponde al programa reproducido.");
                    return codec;
                }
                catch { codec.Dispose(); throw; }
            }
            finally
            {
                if (program != null) Array.Clear(program, 0, program.Length); if (payload != null) Array.Clear(payload, 0, payload.Length);
                if (layoutSeed != null) Array.Clear(layoutSeed, 0, layoutSeed.Length);
                if (runtime != null) Array.Clear(runtime, 0, runtime.Length);
            }
        }
        // Four synchronized bytes remain shared by this research build and observable at runtime.
        public int[] RuntimeKey()
        {
            Alive(); var bytes = MeshKeyDerivation.DeriveContext(masterSeed, BuildId, "runtime-global", 3, 4);
            try
            {
                if (bytes.All(b => b == 0)) throw new InvalidOperationException("Valores de desbloqueo cero: crea un contexto nuevo.");
                return bytes.Select(b => (int)b).ToArray();
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        public void RecordPlan(MeshBindingIdentity binding, CodecPlan plan, GuardAnimationContext animation=null)
        {
            Alive(); if (binding == null || plan == null || plan.BindingStableId != binding.StableId || plan.Source != binding.Source)
                throw new ArgumentException("El plan no corresponde al binding.");
            binding.Validate();
            if(binding.IsSkinned)
            {
                using(var expected=CreateSkinningCodec(binding,animation))
                {
                    string hash=SkinnedLinearCodecV1.ProgramHash(plan);
                    if(hash!=SkinnedLinearCodecV1.ProgramHash(expected.Plan(binding.Source,plan.Strength)))throw new InvalidOperationException("SkinGuard: plan de otro contexto.");
                    var skin=new BindingRecord{stableId=binding.StableId,contentHash=binding.ContentHash,sourceGuid=binding.SourceGuid,localFileId=binding.SourceLocalFileId,programHash=hash,strength=plan.Strength,
                        firstUv=plan.Program.Attributes.FirstUvChannel,secondUv=plan.Program.Attributes.SecondUvChannel,components=2,attributePolicy=AttributeAllocator.ContextualPolicyVersion,usageHash=plan.AttributeUsageHash,skinned=true,skinningVersion=SkinnedLinearCodecV1.Version,skinningHash=binding.SkinningFingerprint};
                    if(bindings.TryGetValue(binding.StableId,out var old)&&JsonUtility.ToJson(old)!=JsonUtility.ToJson(skin))throw new InvalidOperationException("SkinGuard: binding registrado modificado.");bindings[binding.StableId]=skin;return;
                }
            }
            // Reproduce the expected program instead of trusting a caller's program/hash.
            using (var expected = CreateCodecCore(binding, plan.Program.Attributes.PolicyVersion != 0,animation))
            {
                var hash = StaticPolymorphicCodecV1.ProgramHash(plan);
                if (hash != StaticPolymorphicCodecV1.ProgramHash(expected.Plan(binding.Source, plan.Strength)))
                    throw new InvalidOperationException("El programa no pertenece a este contexto.");
                var record = new BindingRecord { stableId = binding.StableId, contentHash = binding.ContentHash,
                    sourceGuid = binding.SourceGuid, localFileId = binding.SourceLocalFileId, programHash = hash, strength = plan.Strength,
                    firstUv = plan.Program.Attributes.FirstUvChannel, secondUv = plan.Program.Attributes.SecondUvChannel,
                    components = plan.Program.Attributes.ComponentsPerChannel, attributePolicy = plan.Program.Attributes.PolicyVersion, usageHash = plan.AttributeUsageHash };
                if (bindings.TryGetValue(binding.StableId, out var old) && JsonUtility.ToJson(old) != JsonUtility.ToJson(record))
                    throw new InvalidOperationException("Un binding ya registrado no puede cambiar dentro del mismo BuildID.");
                bindings[binding.StableId] = record;
            }
        }
        public void RecordTexturePlan(GuardTextureBindingIdentity binding, TexturePlan plan)
        {
            Alive();
            if (binding == null || plan == null || plan.BindingStableId != binding.StableId || plan.Source != binding.Source ||
                !bindings.TryGetValue(binding.MeshBindingId, out var mesh) || mesh.attributePolicy != AttributeAllocator.ContextualPolicyVersion)
                throw new ArgumentException("El plan de textura requiere su binding de malla contextual registrado.");
            binding.Validate();
            var expected = binding.MipmapsEnabled ? (ITextureCodec)CreateMipTextureCodec(binding) : CreateTextureCodec(binding);
            try
            {
                var record = TextureRecordOf(binding, plan);
                if (JsonUtility.ToJson(record) != JsonUtility.ToJson(TextureRecordOf(binding, expected.Plan(binding.Source))))
                    throw new InvalidOperationException("El programa de textura no pertenece a este contexto.");
                if (textures.TryGetValue(binding.StableId, out var old) && JsonUtility.ToJson(old) != JsonUtility.ToJson(record))
                    throw new InvalidOperationException("Un binding de textura registrado no puede cambiar dentro del mismo BuildID.");
                textures[binding.StableId] = record;
            }
            finally{((IDisposable)expected).Dispose();}
        }
        static TextureRecord TextureRecordOf(GuardTextureBindingIdentity binding, TexturePlan plan) => new TextureRecord {
            stableId = binding.StableId, meshBindingId = binding.MeshBindingId, slot = binding.Slot, property = binding.Property,
            sourceGuid = binding.SourceGuid, sourceLocalFileId = binding.SourceLocalFileId,
            materialGuid = binding.MaterialGuid, materialLocalFileId = binding.MaterialLocalFileId,
            sourceFingerprint = plan.SourceFingerprint, contextFingerprint = binding.ContextFingerprint, programHash = TextureGuardCodecV1.ProgramHash(plan),
            mipCodec=binding.MipmapsEnabled,mipCount=binding.MipmapsEnabled?plan.Program.MipCount:0
        };
        internal void RecordPlans(IEnumerable<Tuple<MeshBindingIdentity,CodecPlan>> plans,GuardAnimationContext animation,
            IEnumerable<Tuple<GuardTextureBindingIdentity,TexturePlan>> texturePlans = null)
        {
            var before=new Dictionary<string,BindingRecord>(bindings);
            var beforeTextures=new Dictionary<string,TextureRecord>(textures);
            try
            {foreach(var p in plans)RecordPlan(p.Item1,p.Item2,animation);if(texturePlans!=null)foreach(var p in texturePlans)RecordTexturePlan(p.Item1,p.Item2);}
            catch
            {bindings.Clear();foreach(var p in before)bindings.Add(p.Key,p.Value);textures.Clear();foreach(var p in beforeTextures)textures.Add(p.Key,p.Value);throw;}
        }
        public string SavePrivate()
        {
            Alive(); if (bindings.Count == 0) throw new InvalidOperationException("Registra los planes antes de guardar el contexto privado.");
            var dto = new PrivateContext { schemaVersion = SchemaVersion, derivationVersion = MeshKeyDerivation.Version, bindingSchema = MeshBindingIdentity.SchemaVersion,
                codecId = StaticPolymorphicCodecV1.Id, codecVersion = StaticPolymorphicCodecV1.Version,
                buildId = BuildId, masterSeedBase64 = Convert.ToBase64String(masterSeed), createdUtc = CreatedUtc,
                bindings = bindings.Values.OrderBy(b => b.stableId, StringComparer.Ordinal).ToArray(),
                textureBindingSchema = textures.Count == 0 ? 0 : textures.Values.Any(t=>t.mipCodec)?GuardTextureBindingIdentity.MipSchemaVersion:GuardTextureBindingIdentity.SchemaVersion,
                textureCodecId = textures.Count == 0 ? null : textures.Values.Any(t=>t.mipCodec)?TextureGuardCodecV2.Id:TextureGuardCodecV1.Id,
                textureCodecVersion = textures.Count == 0 ? 0 : textures.Values.Any(t=>t.mipCodec)?TextureGuardCodecV2.Version:TextureGuardCodecV1.Version,
                textures = textures.Values.OrderBy(t => t.stableId, StringComparer.Ordinal).ToArray() };
            try { return GuardPrivateContextStore.Create(BuildId, JsonUtility.ToJson(dto, true)); }
            finally { dto.masterSeedBase64 = null; } // Managed JSON/string copies are not guaranteed to be erased.
        }
        public static GuardBuildContext LoadPrivate(string buildId)
        {
            var dto = JsonUtility.FromJson<PrivateContext>(GuardPrivateContextStore.Read(buildId));
            if (dto == null || dto.schemaVersion < 1 || dto.schemaVersion > SchemaVersion || dto.derivationVersion != MeshKeyDerivation.Version ||
                dto.codecId != StaticPolymorphicCodecV1.Id || dto.codecVersion != StaticPolymorphicCodecV1.Version || dto.buildId != buildId ||
                dto.bindingSchema != MeshBindingIdentity.SchemaVersion || dto.bindings == null || dto.bindings.Length == 0 || dto.bindings.Length > 4096)
                throw new InvalidOperationException("Schema/codec/contexto privado incompatible.");
            bool hasTextures = dto.textures != null && dto.textures.Length > 0;
            bool hasMipTextures=hasTextures&&dto.textures.Any(t=>t!=null&&t.mipCodec);
            if (hasTextures ? dto.schemaVersion < (hasMipTextures?5:4) || dto.textures.Length > 4096 ||
                dto.textureBindingSchema != (hasMipTextures?GuardTextureBindingIdentity.MipSchemaVersion:GuardTextureBindingIdentity.SchemaVersion) ||
                dto.textureCodecId != (hasMipTextures?TextureGuardCodecV2.Id:TextureGuardCodecV1.Id) || dto.textureCodecVersion != (hasMipTextures?TextureGuardCodecV2.Version:TextureGuardCodecV1.Version) :
                dto.textureBindingSchema != 0 || !string.IsNullOrEmpty(dto.textureCodecId) || dto.textureCodecVersion != 0)
                throw new InvalidOperationException("Schema/codec de textura privado incompatible; no puede degradarse a un schema anterior.");
            byte[] seed = null; GuardBuildContext context = null;
            try
            {
                seed = Convert.FromBase64String(dto.masterSeedBase64 ?? "");
                context = new GuardBuildContext(dto.buildId, seed, dto.createdUtc, true);
                foreach (var b in dto.bindings)
                {
                    if (b == null || !MeshKeyDerivation.IsHex(b.stableId, 64) || !MeshKeyDerivation.IsHex(b.contentHash, 64) ||
                        !MeshKeyDerivation.IsHex(b.sourceGuid, 32) || b.localFileId == 0 || !MeshKeyDerivation.IsHex(b.programHash, 64) ||
                        float.IsNaN(b.strength) || float.IsInfinity(b.strength) || b.strength <= 0 || b.strength > .5f || context.bindings.ContainsKey(b.stableId))
                        throw new InvalidOperationException("Registro de binding privado inválido/duplicado.");
                    if (dto.schemaVersion == 1)
                    {
                        // Historical schema 1 omitted allocation fields and always used channels 6/7.
                        if (b.attributePolicy != 0 || !string.IsNullOrEmpty(b.usageHash) ||
                            !((b.firstUv == 0 && b.secondUv == 0 && b.components == 0) || (b.firstUv == 6 && b.secondUv == 7 && b.components == 2)))
                            throw new InvalidOperationException("Schema 1 no puede contener asignación dinámica.");
                        b.firstUv = 6; b.secondUv = 7; b.components = 2;
                    }
                    if (b.components != 2 || (b.attributePolicy == 0 ? b.firstUv != 6 || b.secondUv != 7 || !string.IsNullOrEmpty(b.usageHash) :
                        (b.attributePolicy != AttributeAllocator.PolicyVersion && (b.attributePolicy != AttributeAllocator.ContextualPolicyVersion || dto.schemaVersion<3)) || b.firstUv < 4 || b.firstUv > 7 || b.secondUv < 4 || b.secondUv > 7 ||
                        b.firstUv == b.secondUv || !MeshKeyDerivation.IsHex(b.usageHash, 64)))
                        throw new InvalidOperationException("Layout/política de atributos privada incompatible.");
                    if(b.skinned ? dto.schemaVersion<5||b.skinningVersion!=SkinnedLinearCodecV1.Version||b.attributePolicy!=AttributeAllocator.ContextualPolicyVersion||!MeshKeyDerivation.IsHex(b.skinningHash,64) :
                        b.skinningVersion!=0||!string.IsNullOrEmpty(b.skinningHash))throw new InvalidOperationException("Schema/política de skinning incompatible; no admite degradación.");
                    context.bindings.Add(b.stableId, b);
                }
                foreach (var t in dto.textures ?? Array.Empty<TextureRecord>())
                {
                    if (t == null || !MeshKeyDerivation.IsHex(t.stableId, 64) || !MeshKeyDerivation.IsHex(t.meshBindingId, 64) ||
                        !context.bindings.TryGetValue(t.meshBindingId, out var parent) || parent.attributePolicy != AttributeAllocator.ContextualPolicyVersion ||
                        t.slot < 0 || t.slot > 255 || t.property != "_MainTex" || !MeshKeyDerivation.IsHex(t.sourceGuid, 32) || t.sourceLocalFileId == 0 ||
                        !MeshKeyDerivation.IsHex(t.materialGuid, 32) || t.materialLocalFileId == 0 || !MeshKeyDerivation.IsHex(t.sourceFingerprint, 64) ||
                        !MeshKeyDerivation.IsHex(t.contextFingerprint, 64) || !MeshKeyDerivation.IsHex(t.programHash, 64) || context.textures.ContainsKey(t.stableId))
                        throw new InvalidOperationException("Registro de textura privado inválido/duplicado.");
                    if(t.mipCodec ? dto.schemaVersion<5||t.mipCount<1||t.mipCount>11 : t.mipCount!=0)throw new InvalidOperationException("Registro de mipmaps privado incompatible.");
                    context.textures.Add(t.stableId, t);
                }
                return context;
            }
            catch { context?.Dispose(); throw; }
            finally { if (seed != null) Array.Clear(seed, 0, seed.Length); dto.masterSeedBase64 = null; }
        }
        public void Dispose() { if (!disposed) { Array.Clear(masterSeed, 0, masterSeed.Length); disposed = true; } }
        [Serializable] sealed class BindingRecord
        {
            public string stableId, contentHash, sourceGuid, programHash; public long localFileId; public float strength;
            public int firstUv, secondUv, components, attributePolicy; public string usageHash;
            public bool skinned;public int skinningVersion;public string skinningHash;
        }
        [Serializable] sealed class TextureRecord
        {
            public string stableId, meshBindingId, property, sourceGuid, materialGuid, sourceFingerprint, contextFingerprint, programHash;
            public int slot;
            public bool mipCodec;public int mipCount;
            public long sourceLocalFileId, materialLocalFileId;
        }
        // No public JSON/export method; this DTO is only passed to the private store.
        [Serializable] sealed class PrivateContext
        {
            public int schemaVersion, derivationVersion, bindingSchema, codecVersion;
            public string codecId;
            public string buildId, createdUtc, masterSeedBase64; public BindingRecord[] bindings;
            public int textureBindingSchema, textureCodecVersion;
            public string textureCodecId;
            public TextureRecord[] textures;
        }
    }
}
