// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace LinuxAvatarGuard
{
    // Research opt-in only. GuardBuilder and the avatar wizard continue to use the legacy codec.
    public sealed class StaticPolymorphicCodecV1 : IMeshCodec, IDisposable
    {
        public const string Id = "static-polymorphic-prototype";
        public const int Version = 1;
        public string CodecId => Id;
        public int CodecVersion => Version;
        readonly byte[] programSeed, payloadSeed;
        readonly MeshBindingIdentity binding;
        readonly float? recordedStrength;
        readonly int[] expectedRuntimeKey;
        bool disposed;
        public StaticPolymorphicCodecV1(byte[] seed) : this(seed, seed, null) { }
        internal StaticPolymorphicCodecV1(byte[] programSeed, byte[] payloadSeed, MeshBindingIdentity binding, float? recordedStrength = null, int[] expectedRuntimeKey = null)
        {
            if (programSeed == null || programSeed.Length != 32 || payloadSeed == null || payloadSeed.Length != 32)
                throw new ArgumentException("Seed del prototipo inválido: se requieren 32 bytes.");
            this.programSeed = (byte[])programSeed.Clone(); this.payloadSeed = (byte[])payloadSeed.Clone(); this.binding = binding; this.recordedStrength = recordedStrength;
            this.expectedRuntimeKey = expectedRuntimeKey == null ? null : (int[])expectedRuntimeKey.Clone();
        }
        public static StaticPolymorphicCodecV1 CreateRandom()
        {
            var bytes = new byte[32];
            try { using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); return new StaticPolymorphicCodecV1(bytes); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        public static Mesh RequireStaticRenderer(Renderer renderer)
        {
            if (!(renderer is MeshRenderer) || !renderer.GetComponent<MeshFilter>() || !renderer.GetComponent<MeshFilter>().sharedMesh)
                throw new InvalidOperationException("Solo se admite MeshRenderer en el prototipo estático.");
            return renderer.GetComponent<MeshFilter>().sharedMesh;
        }
        void RequireAlive()
        {
            if (disposed) throw new ObjectDisposedException(nameof(StaticPolymorphicCodecV1));
            if (Application.platform != RuntimePlatform.LinuxEditor || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan || GraphicsSettings.currentRenderPipeline != null)
                throw new InvalidOperationException("El prototipo requiere Unity Editor Linux, Vulkan y Built-in Render Pipeline.");
        }
        public CodecPlan Plan(Mesh source, float strength)
        {
            RequireAlive();
            ValidateSource(source, strength);
            if (recordedStrength.HasValue && strength != recordedStrength.Value) throw new ArgumentException("La intensidad no coincide con el plan privado registrado.");
            if (binding != null) { binding.Validate(); if (binding.Source != source) throw new ArgumentException("La fuente no pertenece al binding."); }
            using (var random = new SeedStream(programSeed, "LAG/static-prototype/v1/program"))
            {
                Func<Tuple<int, int>> axes = () => { int a = random.Next(3), b = (a + 1 + random.Next(2)) % 3; return Tuple.Create(a, b); };
                Func<float> factor = () => (random.Next(2) == 0 ? -1 : 1) * (2 + random.Next(3)) / 16f;
                var instructions = new List<CodecInstruction>();
                var pair = axes(); instructions.Add(CodecInstruction.Swap(pair.Item1, pair.Item2));
                instructions.Add(CodecInstruction.Flip(random.Next(3)));
                for (int i = 0; i < 2; i++)
                {
                    pair = axes(); instructions.Add(CodecInstruction.Shear(pair.Item1, pair.Item2, factor()));
                    pair = axes(); instructions.Add(CodecInstruction.Bend(pair.Item1, pair.Item2, factor()));
                    // Signed/permuted Hadamard rows remain full rank and exactly representable.
                    var rows = new[] { new Vector4(1,1,1,1), new Vector4(1,-1,1,-1), new Vector4(1,1,-1,-1) };
                    var permutation = new[] { 0, 1, 2, 3 };
                    for (int j = 3; j > 0; j--) { int k = random.Next(j + 1); int t = permutation[j]; permutation[j] = permutation[k]; permutation[k] = t; }
                    var signs = Enumerable.Range(0, 4).Select(j => random.Next(2) == 0 ? -1f : 1f).ToArray();
                    for (int row = 0; row < 3; row++)
                    {
                        float rowSign = random.Next(2) == 0 ? -1 : 1;
                        rows[row] = new Vector4(rows[row][permutation[0]] * signs[0], rows[row][permutation[1]] * signs[1],
                            rows[row][permutation[2]] * signs[2], rows[row][permutation[3]] * signs[3]) * rowSign;
                    }
                    instructions.Add(CodecInstruction.Offset(rows[0], rows[1], rows[2]));
                }
                for (int i = instructions.Count - 1; i > 0; i--)
                { int j = random.Next(i + 1); var t = instructions[i]; instructions[i] = instructions[j]; instructions[j] = t; }
                ValidateKeyDependence(instructions);
                return new CodecPlan(source, new CodecProgram(Id, Version, 2, new AttributeLayout(6, 7, 2, false), instructions.ToArray()),
                    strength, MeshBindingIdentity.ContentFingerprint(source), this, binding?.StableId);
            }
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static void ValidateSource(Mesh source, float strength)
        {
            if (!source || !source.isReadable) throw new InvalidOperationException("La malla debe ser legible. No se cambia su importador automáticamente.");
            if (!Finite(strength) || strength <= 0 || strength > .5f) throw new ArgumentException("Intensidad fuera de rango (0, 0.5].");
            if (source.blendShapeCount != 0 || source.bindposes.Length != 0 || source.HasVertexAttribute(VertexAttribute.BlendWeight) || source.HasVertexAttribute(VertexAttribute.BlendIndices))
                throw new InvalidOperationException("El codec estático experimental no admite skinning ni blendshapes.");
            if (source.vertexCount == 0 || source.normals.Length != source.vertexCount) throw new InvalidOperationException("Malla vacía o sin normales.");
            var uv = new List<Vector4>();
            for (int channel = 0; channel < 8; channel++)
            {
                source.GetUVs(channel, uv);
                if (channel >= 6 && uv.Count != 0) throw new InvalidOperationException(channel == 6 ? "UV7 ocupado: se conserva el original y se cancela." : "UV8 ocupado: se conserva el original y se cancela.");
                foreach (var value in uv) for (int j = 0; j < 4; j++) if (!Finite(value[j])) throw new InvalidOperationException("La malla contiene datos no finitos o supera el rango admitido por el prototipo.");
            }
            foreach (var value in source.vertices) for (int j = 0; j < 3; j++)
                if (!Finite(value[j]) || Mathf.Abs(value[j]) > 16) throw new InvalidOperationException("La malla contiene datos no finitos o supera el rango admitido por el prototipo.");
            foreach (var value in source.normals) for (int j = 0; j < 3; j++)
                if (!Finite(value[j])) throw new InvalidOperationException("La malla contiene datos no finitos o supera el rango admitido por el prototipo.");
            foreach (var value in source.tangents) for (int j = 0; j < 4; j++)
                if (!Finite(value[j])) throw new InvalidOperationException("La malla contiene datos no finitos o supera el rango admitido por el prototipo.");
            foreach (var value in source.colors) for (int j = 0; j < 4; j++)
                if (!Finite(value[j])) throw new InvalidOperationException("La malla contiene datos no finitos o supera el rango admitido por el prototipo.");
            for (int j = 0; j < 3; j++)
                if (!Finite(source.bounds.center[j]) || !Finite(source.bounds.extents[j])) throw new InvalidOperationException("La malla contiene datos no finitos o supera el rango admitido por el prototipo.");
        }
        void CheckPlan(Mesh source, CodecPlan plan)
        {
            RequireAlive();
            if (plan == null || plan.Owner != this || !plan.Source || plan.Source != source || plan.Program.CodecId != Id)
                throw new ArgumentException("El plan no pertenece a este codec experimental.");
            ValidateSource(source, plan.Strength);
            if (MeshBindingIdentity.ContentFingerprint(source) != plan.SourceFingerprint) throw new InvalidOperationException("La malla cambió después de crear el plan.");
            if (binding != null) binding.Validate();
            foreach (var instruction in plan.Program.Instructions) instruction.Validate();
            ValidateKeyDependence(plan.Program.Instructions);
        }
        static void ValidateKeyDependence(IList<CodecInstruction> instructions)
        {
            for (int i = 1; i < instructions.Count; i++)
            {
                var a = instructions[i - 1]; var b = instructions[i];
                if (a.Kind == CodecOperationKind.KeyedVectorOffset && b.Kind == a.Kind &&
                    a.RowX + b.RowX == Vector4.zero && a.RowY + b.RowY == Vector4.zero && a.RowZ + b.RowZ == Vector4.zero)
                    throw new InvalidOperationException("El programa cancela sus offsets keyed: crea un contexto nuevo.");
            }
        }
        public void Validate(Mesh source, CodecPlan plan) => CheckPlan(source, plan);
        public Mesh Encode(Mesh source, CodecPlan plan, int[] runtimeKey)
        {
            CheckPlan(source, plan);
            if (runtimeKey == null || runtimeKey.Length != 4 || runtimeKey.Any(k => k < 0 || k > 255) || runtimeKey.All(k => k == 0))
                throw new ArgumentException("Clave del prototipo inválida: cuatro bytes y al menos uno distinto de cero.");
            if (expectedRuntimeKey != null && !runtimeKey.SequenceEqual(expectedRuntimeKey))
                throw new ArgumentException("Los valores de codificación no coinciden con el contexto privado.");
            var key = new Vector4(runtimeKey[0], runtimeKey[1], runtimeKey[2], runtimeKey[3]) / 255f;
            var positions = source.vertices;
            var first = new List<Vector2>(positions.Length); var second = new List<Vector2>(positions.Length);
            double missingSquaredError = 0;
            using (var random = new SeedStream(payloadSeed, "LAG/static-prototype/v1/payload"))
                for (int i = 0; i < positions.Length; i++)
                {
                    var payload = new Vector4(random.Next(256) / 127.5f - 1, random.Next(256) / 127.5f - 1,
                        random.Next(256) / 127.5f - 1, random.Next(256) / 127.5f - 1) * plan.Strength;
                    var decoded = positions[i];
                    foreach (var instruction in plan.Program.Instructions) positions[i] = instruction.Apply(positions[i], payload, key, false);
                    var roundTrip = positions[i];
                    foreach (var instruction in plan.Program.Instructions.Reverse()) roundTrip = instruction.Apply(roundTrip, payload, key, true);
                    var absent = positions[i];
                    foreach (var instruction in plan.Program.Instructions.Reverse()) absent = instruction.Apply(absent, payload, Vector4.zero, true);
                    missingSquaredError += (absent - decoded).sqrMagnitude;
                    if (!Finite(positions[i].x) || !Finite(positions[i].y) || !Finite(positions[i].z) || (roundTrip - decoded).magnitude > 1e-5f)
                        throw new InvalidOperationException("El prototipo supera su presupuesto de error geométrico.");
                    first.Add(new Vector2(payload.x, payload.y)); second.Add(new Vector2(payload.z, payload.w));
                }
            if (Math.Sqrt(missingSquaredError / positions.Length) <= 1e-5)
                throw new InvalidOperationException("La codificación no supera el error de reconstrucción sin clave: crea un contexto nuevo o aumenta la intensidad.");
            var result = UnityEngine.Object.Instantiate(source);
            try
            {
                result.name = source.name + "_LAG_StaticPrototype"; result.vertices = positions;
                result.SetUVs(6, first); result.SetUVs(7, second); result.bounds = source.bounds;
                return result;
            }
            catch { UnityEngine.Object.DestroyImmediate(result); throw; }
        }
        public DecoderFragment EmitDecoder(CodecPlan plan)
        {
            CheckPlan(plan?.Source, plan);
            var output = new StringBuilder("\nHLSLINCLUDE\n#define LIL_REQUIRE_APP_TEXCOORD6\n#define LIL_REQUIRE_APP_TEXCOORD7\n");
            output.Append("#define LIL_CUSTOM_PROPERTIES float _LAGKey0; float _LAGKey1; float _LAGKey2; float _LAGKey3;\n");
            output.Append("float3 LAGStaticDecode(float3 p, float4 c, float4 k) {\nk=clamp(floor(k+0.5),0.0,255.0)/255.0;\n");
            foreach (var instruction in plan.Program.Instructions.Reverse()) instruction.EmitInverse(output);
            output.Append("return p; }\n#define LIL_CUSTOM_VERTEX_OS positionOS.xyz = LAGStaticDecode(positionOS.xyz,float4(input.uv6,input.uv7),float4(_LAGKey0,_LAGKey1,_LAGKey2,_LAGKey3));\nENDHLSL\n");
            return new DecoderFragment(Id, Version, output.ToString(), "_LAGKey0", "_LAGKey1", "_LAGKey2", "_LAGKey3");
        }
        public static string ProgramHash(CodecPlan plan)
        {
            if (plan == null || plan.Program.CodecId != Id) throw new ArgumentException("Expected a static prototype plan.");
            using (var stream = new MemoryStream())
            {
                using (var output = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    output.Write(Id); output.Write(Version); output.Write(plan.Program.SchemaVersion);
                    output.Write(plan.Program.Attributes.FirstUvChannel); output.Write(plan.Program.Attributes.SecondUvChannel);
                    output.Write(plan.Program.Instructions.Count);
                    foreach (var instruction in plan.Program.Instructions) instruction.WriteCanonical(output);
                }
                return Digest(stream.ToArray());
            }
        }
        static string Digest(byte[] bytes)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public void Dispose()
        {
            if (!disposed)
            {
                Array.Clear(programSeed, 0, programSeed.Length); Array.Clear(payloadSeed, 0, payloadSeed.Length);
                if (expectedRuntimeKey != null) Array.Clear(expectedRuntimeKey, 0, expectedRuntimeKey.Length); disposed = true;
            }
        }
        // HMAC counter stream; GuardBuildContext supplies independent HKDF-derived binding seeds.
        sealed class SeedStream : IDisposable
        {
            readonly HMACSHA256 hmac;
            readonly byte[] label;
            byte[] block = Array.Empty<byte>(); int offset; uint counter;
            public SeedStream(byte[] seed, string domain) { hmac = new HMACSHA256(seed); label = Encoding.UTF8.GetBytes(domain); }
            byte Byte()
            {
                if (offset == block.Length)
                {
                    var input = new byte[label.Length + 4]; Array.Copy(label, input, label.Length);
                    for (int i = 0; i < 4; i++) input[label.Length + i] = (byte)(counter >> (8 * i));
                    counter++; Array.Clear(block, 0, block.Length); block = hmac.ComputeHash(input); offset = 0;
                }
                return block[offset++];
            }
            public int Next(int max)
            {
                if (max < 1 || max > 256) throw new ArgumentOutOfRangeException(nameof(max));
                int limit = 256 - 256 % max, value;
                do { value = Byte(); } while (value >= limit);
                return value % max;
            }
            public void Dispose() { Array.Clear(block, 0, block.Length); hmac.Dispose(); }
        }
    }
}
