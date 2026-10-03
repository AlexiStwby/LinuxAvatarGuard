// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LinuxAvatarGuard
{
    public sealed class GuardMeshFingerprintOptions
    {
        public double RelativeStep { get; }
        public double MaximumRelativeDisplacement { get; }
        public double MaximumObjectDisplacement { get; }
        public GuardMeshFingerprintOptions(double relativeStep = 1e-5, double maximumRelativeDisplacement = 1e-5, double maximumObjectDisplacement = 5e-5)
        {
            if (!Finite(relativeStep) || relativeStep < 1e-8 || relativeStep > 1e-5 ||
                !Finite(maximumRelativeDisplacement) || maximumRelativeDisplacement <= 0 || maximumRelativeDisplacement > 1e-5 ||
                !Finite(maximumObjectDisplacement) || maximumObjectDisplacement <= 0 || maximumObjectDisplacement > 5e-5)
                throw new ArgumentException("MeshFingerprint: presupuesto fuera del contrato del piloto.");
            RelativeStep = relativeStep; MaximumRelativeDisplacement = maximumRelativeDisplacement; MaximumObjectDisplacement = maximumObjectDisplacement;
        }
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    [Serializable]
    public sealed class GuardMeshFingerprintPrivateRecord
    {
        public int schema = 1, algorithmVersion = 1;
        public string algorithm = GuardMeshFingerprintV1.Id;
        public string state = "CpuSelfVerified";
        public string buildId, bindingId, sourcePositionHash, markedPositionHash, authentication;
        public double relativeStep, maximumRelativeDisplacement, maximumObjectDisplacement;
        public int sourceVertices, sourceUniquePositions, usableSymbols;
    }

    public enum GuardMeshFingerprintDecision { ResearchMatch, NoResearchMatch, InconclusiveCoverage }

    public sealed class GuardMeshFingerprintObservation
    {
        public int UniquePositions { get; internal set; }
        public int UsableSymbols { get; internal set; }
        public int MatchedSymbols { get; internal set; }
        public double Coverage => UsableSymbols / 128.0;
        public double BitAgreement => UsableSymbols == 0 ? 0 : MatchedSymbols / (double)UsableSymbols;
        public double? Confidence => null;
        public bool Calibrated => false;
        public GuardMeshFingerprintDecision Decision => UsableSymbols < GuardMeshFingerprintV1.MinimumSymbols ?
            GuardMeshFingerprintDecision.InconclusiveCoverage : BitAgreement >= 109 / 128.0 ?
            GuardMeshFingerprintDecision.ResearchMatch : GuardMeshFingerprintDecision.NoResearchMatch;
    }

    public sealed class GuardMeshFingerprintEncoding
    {
        public float[] Positions { get; internal set; }
        public GuardMeshFingerprintPrivateRecord PrivateRecord { get; internal set; }
        public GuardMeshFingerprintObservation SelfObservation { get; internal set; }
        public double MaximumDisplacement { get; internal set; }
        public double RmsDisplacement { get; internal set; }
        public double MaximumRelativeDisplacement { get; internal set; }
    }

    // Float32 XYZ arrays only. No Unity, graphics, IO, topology or runtime key dependency.
    public static class GuardMeshFingerprintV1
    {
        public const string Id = "RadialUniqueMeshFingerprintV1";
        public const int Version = 1, Symbols = 128, MinimumSymbols = 96, MinimumSamples = 4, MaximumVertices = 262144;
        internal const double BandMinimum = 0.1, BandWidth = 0.88;

        internal sealed class Geometry
        {
            public readonly float[] Positions;
            public readonly int[] Group;
            public readonly List<Point> Unique;
            public readonly double X, Y, Z, Radius, Diagonal;
            public Geometry(float[] positions, bool authoring)
            {
                if (positions == null || positions.Length % 3 != 0 || positions.Length < 3 || positions.Length / 3 > MaximumVertices)
                    throw new ArgumentException("MeshFingerprint: array XYZ/contador fuera del contrato.");
                double maximum = authoring ? 16 : 1e6;
                foreach (float value in positions)
                    if (!GuardMeshFingerprintOptions.Finite(value) || Math.Abs(value) > maximum)
                        throw new ArgumentException("MeshFingerprint: posición no finita o fuera de rango.");
                Positions = positions; int count = positions.Length / 3;
                var order = new int[count]; for (int i = 0; i < count; i++) order[i] = i;
                Array.Sort(order, (a, b) => Compare(positions, a, b));
                Group = new int[count]; Unique = new List<Point>(count);
                foreach (int index in order)
                {
                    var point = new Point(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]);
                    if (Unique.Count == 0 || !point.Same(Unique[Unique.Count - 1])) Unique.Add(point);
                    Group[index] = Unique.Count - 1;
                }
                if (authoring && Unique.Count < MinimumSymbols * MinimumSamples) throw new InvalidOperationException("MeshFingerprint: posiciones únicas insuficientes.");
                double sx = 0, sy = 0, sz = 0, cx = 0, cy = 0, cz = 0;
                double minX = double.PositiveInfinity, minY = minX, minZ = minX, maxX = double.NegativeInfinity, maxY = maxX, maxZ = maxX;
                foreach (var p in Unique)
                {
                    Sum(p.X, ref sx, ref cx); Sum(p.Y, ref sy, ref cy); Sum(p.Z, ref sz, ref cz);
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
                }
                X = sx / Unique.Count; Y = sy / Unique.Count; Z = sz / Unique.Count;
                foreach (var p in Unique) Radius = Math.Max(Radius, Distance(p.X - X, p.Y - Y, p.Z - Z));
                Diagonal = Distance(maxX - minX, maxY - minY, maxZ - minZ);
                if (authoring && (!(Radius > 0) || !(Diagonal > 0))) throw new InvalidOperationException("MeshFingerprint: geometría degenerada.");
            }
            static int Compare(float[] p, int a, int b)
            {
                for (int j = 0; j < 3; j++) { int value = p[a * 3 + j].CompareTo(p[b * 3 + j]); if (value != 0) return value; }
                return 0;
            }
            static void Sum(double value, ref double sum, ref double correction)
            { double adjusted = value - correction, next = sum + adjusted; correction = (next - sum) - adjusted; sum = next; }
            public double NormalizedRadius(Point p) => Distance(p.X - X, p.Y - Y, p.Z - Z) / Radius;
        }
        internal struct Point
        {
            public readonly float X, Y, Z;
            public Point(float x, float y, float z) { X = x; Y = y; Z = z; }
            public bool Same(Point other) => X == other.X && Y == other.Y && Z == other.Z;
        }
        internal static double Distance(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);
        internal static int Band(double radius) => (int)Math.Floor((radius - BandMinimum) / BandWidth * Symbols);
        internal static bool Hex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
        internal static string Digest(byte[] value)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
        public static string PositionHash(float[] positions)
        {
            if (positions == null || positions.Length % 3 != 0 || positions.Length / 3 > MaximumVertices) throw new ArgumentException("MeshFingerprint: XYZ inválido.");
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                { writer.Write("LAG/mesh-fingerprint/positions/v1"); writer.Write(positions.Length); foreach (float p in positions) writer.Write(p); }
                return Digest(stream.ToArray());
            }
        }
        internal static double[] Dithers(byte[] seed, double step)
        {
            var result = new double[Symbols]; var header = Encoding.UTF8.GetBytes("LAG/mesh-fingerprint/radial-unique/v1/dither\0");
            var input = new byte[header.Length + 4]; Buffer.BlockCopy(header, 0, input, 0, header.Length);
            using (var hmac = new HMACSHA256(seed))
            for (int i = 0; i < Symbols; i++)
            {
                input[header.Length + 3] = (byte)i;
                byte[] bytes = hmac.ComputeHash(input);
                try
                {
                    ulong value = 0; for (int j = 0; j < 8; j++) value = (value << 8) | bytes[j];
                    result[i] = (value >> 11) * (1.0 / 9007199254740992.0) * 2 * step;
                }
                finally { Array.Clear(bytes, 0, bytes.Length); }
            }
            Array.Clear(input, 0, input.Length); return result;
        }
        internal static int Bit(byte[] codeword, int symbol) => (codeword[symbol / 8] >> (7 - symbol % 8)) & 1;
        internal static GuardMeshFingerprintObservation Observe(Geometry geometry, byte[] codeword, double[] dither, double step)
        {
            if (!(geometry.Radius > 0)) return new GuardMeshFingerprintObservation { UniquePositions = geometry.Unique.Count };
            var counts = new int[Symbols]; var votes = new int[Symbols];
            foreach (var p in geometry.Unique)
            {
                double radius = geometry.NormalizedRadius(p); int band = Band(radius); if (band < 0 || band >= Symbols) continue;
                long quantized = (long)Math.Round((radius - dither[band]) / step, MidpointRounding.ToEven);
                counts[band]++; votes[band] += (int)(quantized & 1);
            }
            var result = new GuardMeshFingerprintObservation { UniquePositions = geometry.Unique.Count };
            for (int i = 0; i < Symbols; i++)
                if (counts[i] >= MinimumSamples && votes[i] * 2 != counts[i])
                { result.UsableSymbols++; if ((votes[i] * 2 > counts[i] ? 1 : 0) == Bit(codeword, i)) result.MatchedSymbols++; }
            return result;
        }
        internal static string Authenticate(GuardMeshFingerprintPrivateRecord record, byte[] seed)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write("LAG/mesh-fingerprint/record-auth/v1"); writer.Write(record.schema); writer.Write(record.algorithm); writer.Write(record.algorithmVersion); writer.Write(record.state);
                    writer.Write(record.buildId); writer.Write(record.bindingId); writer.Write(record.sourcePositionHash); writer.Write(record.markedPositionHash);
                    writer.Write(record.relativeStep); writer.Write(record.maximumRelativeDisplacement); writer.Write(record.maximumObjectDisplacement);
                    writer.Write(record.sourceVertices); writer.Write(record.sourceUniquePositions); writer.Write(record.usableSymbols);
                }
                using (var hmac = new HMACSHA256(seed)) { byte[] bytes = hmac.ComputeHash(stream.ToArray()); try { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); } finally { Array.Clear(bytes, 0, bytes.Length); } }
            }
        }
        internal static GuardMeshFingerprintOptions ValidateRecord(GuardFingerprintContext context, GuardMeshFingerprintPrivateRecord record, byte[] seed)
        {
            if (context == null || record == null || record.schema != 1 || record.algorithm != Id || record.algorithmVersion != Version || record.state != "CpuSelfVerified" ||
                record.buildId != context.BuildId || !Hex(record.bindingId, 64) || !Hex(record.sourcePositionHash, 64) || !Hex(record.markedPositionHash, 64) || !Hex(record.authentication, 64) ||
                record.sourceVertices < MinimumSamples * MinimumSymbols || record.sourceVertices > MaximumVertices || record.sourceUniquePositions < MinimumSamples * MinimumSymbols ||
                record.sourceUniquePositions > record.sourceVertices || record.usableSymbols < MinimumSymbols || record.usableSymbols > Symbols)
                throw new ArgumentException("MeshFingerprint: registro/versionado/identidad incompatible.");
            var options = new GuardMeshFingerprintOptions(record.relativeStep, record.maximumRelativeDisplacement, record.maximumObjectDisplacement);
            string expected = Authenticate(record, seed); int difference = 0;
            for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ record.authentication[i];
            if (difference != 0) throw new InvalidOperationException("MeshFingerprint: registro privado modificado o contexto ajeno.");
            return options;
        }
        public static GuardMeshFingerprintPlan Plan(float[] positions, GuardFingerprintContext context, string bindingId, GuardMeshFingerprintOptions options = null) =>
            new GuardMeshFingerprintPlan(positions, context, bindingId, options ?? new GuardMeshFingerprintOptions());
        public static GuardMeshFingerprintPlan RestorePlan(float[] positions, GuardFingerprintContext context, GuardMeshFingerprintPrivateRecord record)
        {
            if (context == null || record == null) throw new ArgumentNullException();
            byte[] seed = context.DeriveCarrierSeed(GuardFingerprintChannel.Mesh, record.bindingId);
            try
            {
                var options = ValidateRecord(context, record, seed);
                if (PositionHash(positions) != record.sourcePositionHash || positions.Length / 3 != record.sourceVertices)
                    throw new InvalidOperationException("MeshFingerprint: la fuente original ya no corresponde al respaldo.");
                var plan = Plan(positions, context, record.bindingId, options);
                try
                {
                    if (plan.UniquePositions != record.sourceUniquePositions) throw new InvalidOperationException("MeshFingerprint: las posiciones únicas cambiaron.");
                    var output = plan.Encode(positions);
                    if (output.PrivateRecord.authentication != record.authentication) throw new InvalidOperationException("MeshFingerprint: reproducción distinta al registro privado.");
                    return plan;
                }
                catch { plan.Dispose(); throw; }
            }
            finally { Array.Clear(seed, 0, seed.Length); }
        }
        public static GuardMeshFingerprintObserver CreateObserver(GuardFingerprintContext context, GuardMeshFingerprintPrivateRecord record) => new GuardMeshFingerprintObserver(context, record);
    }

    public sealed class GuardMeshFingerprintPlan : IDisposable
    {
        readonly string buildId, bindingId, sourceHash;
        readonly GuardMeshFingerprintV1.Geometry source;
        readonly GuardMeshFingerprintOptions options;
        readonly byte[] seed, codeword;
        readonly double[] dither;
        bool disposed;
        public int UniquePositions => source.Unique.Count;
        public int SourceVertices => source.Positions.Length / 3;
        public string SourcePositionHash => sourceHash;
        internal GuardMeshFingerprintPlan(float[] positions, GuardFingerprintContext context, string bindingId, GuardMeshFingerprintOptions options)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            source = new GuardMeshFingerprintV1.Geometry((float[])positions?.Clone(), true);
            this.options = options; sourceHash = GuardMeshFingerprintV1.PositionHash(source.Positions); buildId = context.BuildId; this.bindingId = bindingId;
            try
            {
                seed = context.DeriveCarrierSeed(GuardFingerprintChannel.Mesh, bindingId); codeword = context.DeriveCodeword(GuardFingerprintChannel.Mesh, bindingId);
                dither = GuardMeshFingerprintV1.Dithers(seed, options.RelativeStep);
                var counts = new int[128];
                foreach (var p in source.Unique) { int band = GuardMeshFingerprintV1.Band(source.NormalizedRadius(p)); if (band >= 0 && band < 128) counts[band]++; }
                int covered = 0; foreach (int count in counts) if (count >= GuardMeshFingerprintV1.MinimumSamples) covered++;
                if (covered < GuardMeshFingerprintV1.MinimumSymbols) throw new InvalidOperationException("MeshFingerprint: cobertura radial insuficiente; no se fuerza la detección.");
            }
            catch { Dispose(); throw; }
        }
        void Alive() { if (disposed) throw new ObjectDisposedException(nameof(GuardMeshFingerprintPlan)); }
        public GuardMeshFingerprintEncoding Encode(float[] positions)
        {
            Alive();
            if (GuardMeshFingerprintV1.PositionHash(positions) != sourceHash) throw new InvalidOperationException("MeshFingerprint: fuente modificada después de planificar.");
            var moved = new GuardMeshFingerprintV1.Point[source.Unique.Count];
            double largest = 0, squares = 0;
            for (int i = 0; i < moved.Length; i++)
            {
                var point = source.Unique[i]; double radius = source.NormalizedRadius(point); int band = GuardMeshFingerprintV1.Band(radius); moved[i] = point;
                if (band >= 0 && band < 128)
                {
                    int bit = GuardMeshFingerprintV1.Bit(codeword, band);
                    double target = options.RelativeStep * (2 * Math.Round(((radius - dither[band]) / options.RelativeStep - bit) / 2, MidpointRounding.ToEven) + bit) + dither[band];
                    if (target > 0 && GuardMeshFingerprintV1.Band(target) == band)
                    {
                        double ratio = target / radius;
                        moved[i] = new GuardMeshFingerprintV1.Point((float)(source.X + (point.X - source.X) * ratio), (float)(source.Y + (point.Y - source.Y) * ratio), (float)(source.Z + (point.Z - source.Z) * ratio));
                    }
                }
                double distance = GuardMeshFingerprintV1.Distance(moved[i].X - (double)point.X, moved[i].Y - (double)point.Y, moved[i].Z - (double)point.Z);
                largest = Math.Max(largest, distance);
                if (distance > options.MaximumObjectDisplacement || distance / source.Diagonal > options.MaximumRelativeDisplacement)
                    throw new InvalidOperationException("MeshFingerprint: la cuantización Float32 excede el presupuesto.");
            }
            var result = new float[positions.Length];
            for (int i = 0; i < source.Group.Length; i++)
            {
                var p = moved[source.Group[i]]; result[i * 3] = p.X; result[i * 3 + 1] = p.Y; result[i * 3 + 2] = p.Z;
                double distance = GuardMeshFingerprintV1.Distance(p.X - (double)source.Positions[i * 3], p.Y - (double)source.Positions[i * 3 + 1], p.Z - (double)source.Positions[i * 3 + 2]); squares += distance * distance;
            }
            var geometry = new GuardMeshFingerprintV1.Geometry(result, true);
            if (geometry.Unique.Count != source.Unique.Count) throw new InvalidOperationException("MeshFingerprint: la marca fusionaría posiciones distintas.");
            var observed = GuardMeshFingerprintV1.Observe(geometry, codeword, dither, options.RelativeStep);
            if (observed.UsableSymbols < GuardMeshFingerprintV1.MinimumSymbols || observed.MatchedSymbols != observed.UsableSymbols)
                throw new InvalidOperationException("MeshFingerprint: autoverificación insuficiente; se conserva la fuente.");
            if (GuardMeshFingerprintV1.PositionHash(positions) != sourceHash) throw new InvalidOperationException("MeshFingerprint: mutación durante encoding.");
            var record = new GuardMeshFingerprintPrivateRecord { buildId = buildId, bindingId = bindingId, sourcePositionHash = sourceHash,
                markedPositionHash = GuardMeshFingerprintV1.PositionHash(result), sourceVertices = SourceVertices, sourceUniquePositions = UniquePositions,
                usableSymbols = observed.UsableSymbols, relativeStep = options.RelativeStep, maximumRelativeDisplacement = options.MaximumRelativeDisplacement, maximumObjectDisplacement = options.MaximumObjectDisplacement };
            record.authentication = GuardMeshFingerprintV1.Authenticate(record, seed);
            return new GuardMeshFingerprintEncoding { Positions = result, PrivateRecord = record, SelfObservation = observed,
                MaximumDisplacement = largest, MaximumRelativeDisplacement = largest / source.Diagonal, RmsDisplacement = Math.Sqrt(squares / SourceVertices) };
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (seed != null) Array.Clear(seed, 0, seed.Length); if (codeword != null) Array.Clear(codeword, 0, codeword.Length); if (dither != null) Array.Clear(dither, 0, dither.Length);
        }
    }

    public sealed class GuardMeshFingerprintObserver : IDisposable
    {
        readonly byte[] codeword;
        readonly double[] dither;
        readonly double step;
        bool disposed;
        internal GuardMeshFingerprintObserver(GuardFingerprintContext context, GuardMeshFingerprintPrivateRecord record)
        {
            if (context == null || record == null) throw new ArgumentNullException();
            byte[] seed = context.DeriveCarrierSeed(GuardFingerprintChannel.Mesh, record.bindingId);
            try
            {
                var options = GuardMeshFingerprintV1.ValidateRecord(context, record, seed); step = options.RelativeStep;
                codeword = context.DeriveCodeword(GuardFingerprintChannel.Mesh, record.bindingId); dither = GuardMeshFingerprintV1.Dithers(seed, step);
            }
            catch { if (codeword != null) Array.Clear(codeword, 0, codeword.Length); throw; }
            finally { Array.Clear(seed, 0, seed.Length); }
        }
        public GuardMeshFingerprintObservation Observe(float[] candidatePositions)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GuardMeshFingerprintObserver));
            return GuardMeshFingerprintV1.Observe(new GuardMeshFingerprintV1.Geometry(candidatePositions, false), codeword, dither, step);
        }
        public void Dispose()
        { if (disposed) return; disposed = true; Array.Clear(codeword, 0, codeword.Length); Array.Clear(dither, 0, dither.Length); }
    }
}
