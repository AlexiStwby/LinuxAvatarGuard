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
        public const int SchemaVersion = 1;
        public string BuildId { get; }
        public string CreatedUtc { get; }
        readonly byte[] masterSeed;
        readonly Dictionary<string, BindingRecord> bindings = new Dictionary<string, BindingRecord>();
        readonly bool restored;
        bool disposed;
        public int BindingCount => bindings.Count;
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
        public StaticPolymorphicCodecV1 CreateCodec(MeshBindingIdentity binding)
        {
            Alive(); if (binding == null) throw new ArgumentNullException(nameof(binding)); binding.Validate();
            if (restored && !bindings.ContainsKey(binding.StableId)) throw new InvalidOperationException("El binding no pertenece al contexto privado restaurado.");
            if (bindings.TryGetValue(binding.StableId, out var record) &&
                (record.sourceGuid != binding.SourceGuid || record.localFileId != binding.SourceLocalFileId || record.contentHash != binding.ContentHash))
                throw new InvalidOperationException("La metadata privada no corresponde a la fuente.");
            byte[] program = null, payload = null;
            int[] runtime = null;
            try
            {
                program = MeshKeyDerivation.Derive(masterSeed, BuildId, binding, MeshDerivationPurpose.Program);
                payload = MeshKeyDerivation.Derive(masterSeed, BuildId, binding, MeshDerivationPurpose.Payload);
                runtime = RuntimeKey();
                var codec = new StaticPolymorphicCodecV1(program, payload, binding, record?.strength, runtime);
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
        public void RecordPlan(MeshBindingIdentity binding, CodecPlan plan)
        {
            Alive(); if (binding == null || plan == null || plan.BindingStableId != binding.StableId || plan.Source != binding.Source)
                throw new ArgumentException("El plan no corresponde al binding.");
            binding.Validate();
            // Reproduce the expected program instead of trusting a caller's program/hash.
            using (var expected = CreateCodec(binding))
            {
                var hash = StaticPolymorphicCodecV1.ProgramHash(plan);
                if (hash != StaticPolymorphicCodecV1.ProgramHash(expected.Plan(binding.Source, plan.Strength)))
                    throw new InvalidOperationException("El programa no pertenece a este contexto.");
                var record = new BindingRecord { stableId = binding.StableId, contentHash = binding.ContentHash,
                    sourceGuid = binding.SourceGuid, localFileId = binding.SourceLocalFileId, programHash = hash, strength = plan.Strength };
                if (bindings.TryGetValue(binding.StableId, out var old) && JsonUtility.ToJson(old) != JsonUtility.ToJson(record))
                    throw new InvalidOperationException("Un binding ya registrado no puede cambiar dentro del mismo BuildID.");
                bindings[binding.StableId] = record;
            }
        }
        public string SavePrivate()
        {
            Alive(); if (bindings.Count == 0) throw new InvalidOperationException("Registra los planes antes de guardar el contexto privado.");
            var dto = new PrivateContext { schemaVersion = SchemaVersion, derivationVersion = MeshKeyDerivation.Version, bindingSchema = MeshBindingIdentity.SchemaVersion,
                codecId = StaticPolymorphicCodecV1.Id, codecVersion = StaticPolymorphicCodecV1.Version,
                buildId = BuildId, masterSeedBase64 = Convert.ToBase64String(masterSeed), createdUtc = CreatedUtc,
                bindings = bindings.Values.OrderBy(b => b.stableId, StringComparer.Ordinal).ToArray() };
            try { return GuardPrivateContextStore.Create(BuildId, JsonUtility.ToJson(dto, true)); }
            finally { dto.masterSeedBase64 = null; } // Managed JSON/string copies are not guaranteed to be erased.
        }
        public static GuardBuildContext LoadPrivate(string buildId)
        {
            var dto = JsonUtility.FromJson<PrivateContext>(GuardPrivateContextStore.Read(buildId));
            if (dto == null || dto.schemaVersion != SchemaVersion || dto.derivationVersion != MeshKeyDerivation.Version ||
                dto.codecId != StaticPolymorphicCodecV1.Id || dto.codecVersion != StaticPolymorphicCodecV1.Version || dto.buildId != buildId ||
                dto.bindingSchema != MeshBindingIdentity.SchemaVersion || dto.bindings == null || dto.bindings.Length == 0 || dto.bindings.Length > 4096)
                throw new InvalidOperationException("Schema/codec/contexto privado incompatible.");
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
                    context.bindings.Add(b.stableId, b);
                }
                return context;
            }
            catch { context?.Dispose(); throw; }
            finally { if (seed != null) Array.Clear(seed, 0, seed.Length); dto.masterSeedBase64 = null; }
        }
        public void Dispose() { if (!disposed) { Array.Clear(masterSeed, 0, masterSeed.Length); disposed = true; } }
        [Serializable] sealed class BindingRecord
        { public string stableId, contentHash, sourceGuid, programHash; public long localFileId; public float strength; }
        // No public JSON/export method; this DTO is only passed to the private store.
        [Serializable] sealed class PrivateContext
        {
            public int schemaVersion, derivationVersion, bindingSchema, codecVersion;
            public string codecId;
            public string buildId, createdUtc, masterSeedBase64; public BindingRecord[] bindings;
        }
    }
}
