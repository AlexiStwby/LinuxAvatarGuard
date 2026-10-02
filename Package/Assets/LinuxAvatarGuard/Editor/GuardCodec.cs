// SPDX-License-Identifier: MIT
using System;
using System.Collections.ObjectModel;
using UnityEngine;

namespace LinuxAvatarGuard
{
    // Editor-only contracts. Runtime secrets are arguments to Encode, never part of a plan or IR.
    public interface IMeshCodec
    {
        string CodecId { get; }
        int CodecVersion { get; }
        CodecPlan Plan(Mesh source, float strength);
        void Validate(Mesh source, CodecPlan plan);
        Mesh Encode(Mesh source, CodecPlan plan, int[] runtimeKey);
        DecoderFragment EmitDecoder(CodecPlan plan);
    }

    public enum CodecOperationKind
    {
        LegacyNormalOffset = 1, AxisSwap = 2, AxisFlip = 3,
        TriangularShear = 4, BoundedBend = 5, KeyedVectorOffset = 6
    }

    public sealed class AttributeLayout
    {
        public int FirstUvChannel { get; }
        public int SecondUvChannel { get; }
        public int ComponentsPerChannel { get; }
        public bool RequiresUnitNormals { get; }
        public int PolicyVersion { get; }
        internal AttributeLayout(int first, int second, int components, bool normals, int policyVersion = 0)
        {
            FirstUvChannel = first; SecondUvChannel = second;
            ComponentsPerChannel = components; RequiresUnitNormals = normals; PolicyVersion = policyVersion;
        }
    }

    public sealed class CodecProgram
    {
        public string CodecId { get; }
        public int CodecVersion { get; }
        public int SchemaVersion { get; }
        public ReadOnlyCollection<CodecOperationKind> Operations { get; }
        public ReadOnlyCollection<CodecInstruction> Instructions { get; }
        public AttributeLayout Attributes { get; }
        internal CodecProgram(string id, int version, AttributeLayout attributes, params CodecOperationKind[] operations)
        {
            CodecId = id; CodecVersion = version; SchemaVersion = 1; Attributes = attributes;
            Operations = Array.AsReadOnly((CodecOperationKind[])operations.Clone());
            Instructions = Array.AsReadOnly(Array.Empty<CodecInstruction>());
        }
        internal CodecProgram(string id, int version, int schema, AttributeLayout attributes, CodecInstruction[] instructions)
        {
            CodecId = id; CodecVersion = version; SchemaVersion = schema; Attributes = attributes;
            var copy = (CodecInstruction[])instructions.Clone();
            Instructions = Array.AsReadOnly(copy);
            Operations = Array.AsReadOnly(Array.ConvertAll(copy, instruction => instruction.Kind));
        }
    }

    public sealed class CodecPlan
    {
        public CodecProgram Program { get; }
        public float Strength { get; }
        public string BindingStableId { get; }
        public string AttributeUsageHash { get; }
        // Legacy/standalone plans leave BindingStableId null. Context-created plans also validate the captured binding.
        internal Mesh Source { get; }
        internal string SourceFingerprint { get; }
        internal object Owner { get; }
        internal CodecPlan(Mesh source, CodecProgram program, float strength, string fingerprint = null, object owner = null, string bindingId = null, string attributeUsageHash = null)
        { Source = source; Program = program; Strength = strength; SourceFingerprint = fingerprint; Owner = owner; BindingStableId = bindingId; AttributeUsageHash = attributeUsageHash; }
    }

    public sealed class DecoderFragment
    {
        public string CodecId { get; }
        public int CodecVersion { get; }
        public string Injection { get; }
        public ReadOnlyCollection<string> RuntimeProperties { get; }
        internal DecoderFragment(string id, int version, string injection, params string[] properties)
        {
            CodecId = id; CodecVersion = version; Injection = injection;
            RuntimeProperties = Array.AsReadOnly((string[])properties.Clone());
        }
    }

    public sealed class LegacyLinearCodecV1 : IMeshCodec
    {
        public const string Id = "legacy-linear";
        public const int Version = 1;
        public string CodecId => Id;
        public int CodecVersion => Version;
        static readonly CodecProgram program = new CodecProgram(Id, Version,
            new AttributeLayout(6, 7, 2, true), CodecOperationKind.LegacyNormalOffset);
        public static readonly DecoderFragment Decoder = new DecoderFragment(Id, Version, @"
        HLSLINCLUDE
        #define LIL_REQUIRE_APP_NORMAL
        #define LIL_REQUIRE_APP_TEXCOORD6
        #define LIL_REQUIRE_APP_TEXCOORD7
        #define LIL_CUSTOM_PROPERTIES float _LAGKey0; float _LAGKey1; float _LAGKey2; float _LAGKey3;
        #define LIL_CUSTOM_VERTEX_OS positionOS.xyz -= input.normalOS * dot(float4(input.uv6, input.uv7), clamp(floor(float4(_LAGKey0, _LAGKey1, _LAGKey2, _LAGKey3) + 0.5), 0.0, 255.0) / 255.0);
        ENDHLSL
", "_LAGKey0", "_LAGKey1", "_LAGKey2", "_LAGKey3");
        public CodecPlan Plan(Mesh source, float strength)
        {
            if (!source) throw new ArgumentNullException(nameof(source));
            if (float.IsNaN(strength) || float.IsInfinity(strength) || strength <= 0 || strength > .5f)
                throw new ArgumentException("Intensidad fuera de rango (0, 0.5].");
            return new CodecPlan(source, program, strength);
        }
        static void CheckPlan(Mesh source, CodecPlan plan)
        {
            if (plan == null || plan.Program != program || !plan.Source || plan.Source != source)
                throw new ArgumentException("El plan del codec no corresponde a esta malla.");
        }
        public void Validate(Mesh source, CodecPlan plan)
        {
            CheckPlan(source, plan);
            GuardMesh.ValidateInputs(source, new[] { 123, 157, 211, 239 }, plan.Strength);
        }
        public Mesh Encode(Mesh source, CodecPlan plan, int[] runtimeKey)
        {
            CheckPlan(source, plan);
            return GuardMesh.Encode(source, runtimeKey, plan.Strength);
        }
        public DecoderFragment EmitDecoder(CodecPlan plan)
        {
            CheckPlan(plan?.Source, plan);
            return Decoder;
        }
    }

    public static class MeshCodecs
    {
        public static IMeshCodec Legacy { get; } = new LegacyLinearCodecV1();
    }
}
