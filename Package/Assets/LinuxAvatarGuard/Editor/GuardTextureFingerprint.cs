// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LinuxAvatarGuard
{
    public sealed class GuardTextureFingerprintOptions
    {
        public double Step { get; }
        public double MaximumMeanAbsoluteRgb { get; }
        public double MaximumRgb { get; }
        public double MinimumPsnrDb { get; }
        public GuardTextureFingerprintOptions(double step = 0.018, double maximumMeanAbsoluteRgb = 0.0015, double maximumRgb = 0.012, double minimumPsnrDb = 45)
        {
            if (!GuardTextureFingerprintV1.Finite(step) || step < 0.004 || step > 0.018 ||
                !GuardTextureFingerprintV1.Finite(maximumMeanAbsoluteRgb) || maximumMeanAbsoluteRgb <= 0 || maximumMeanAbsoluteRgb > 0.0015 ||
                !GuardTextureFingerprintV1.Finite(maximumRgb) || maximumRgb <= 0 || maximumRgb > 0.012 ||
                !GuardTextureFingerprintV1.Finite(minimumPsnrDb) || minimumPsnrDb < 45 || minimumPsnrDb > 100)
                throw new ArgumentException("TextureFingerprint: presupuesto fuera del contrato del piloto.");
            Step = step; MaximumMeanAbsoluteRgb = maximumMeanAbsoluteRgb; MaximumRgb = maximumRgb; MinimumPsnrDb = minimumPsnrDb;
        }
    }
    [Serializable]
    public sealed class GuardTextureFingerprintPrivateRecord
    {
        public int schema = 1, algorithmVersion = GuardTextureFingerprintV1.Version;
        public string algorithm = GuardTextureFingerprintV1.Id, state = "CpuSelfVerified", colorDomain = GuardTextureFingerprintV1.ColorDomain, raster = GuardTextureFingerprintV1.Raster;
        public string buildId, bindingId, sourcePixelHash, markedPixelHash, authentication;
        public int sourceWidth, sourceHeight, usableSymbols;
        public double step, maximumMeanAbsoluteRgb, maximumRgb, minimumPsnrDb;
    }
    public enum GuardTextureFingerprintDecision { ResearchMatch, NoResearchMatch, InconclusiveCoverage }
    public sealed class GuardTextureFingerprintObservation
    {
        public int UsableSymbols { get; internal set; }
        public int MatchedSymbols { get; internal set; }
        public int UsableCarriers { get; internal set; }
        public double Coverage => UsableSymbols / 128.0;
        public double BitAgreement => UsableSymbols == 0 ? 0 : MatchedSymbols / (double)UsableSymbols;
        public double? Confidence => null;
        public bool Calibrated => false;
        public GuardTextureFingerprintDecision Decision => UsableSymbols < GuardTextureFingerprintV1.MinimumSymbols ? GuardTextureFingerprintDecision.InconclusiveCoverage :
            BitAgreement >= 109 / 128.0 ? GuardTextureFingerprintDecision.ResearchMatch : GuardTextureFingerprintDecision.NoResearchMatch;
    }
    public sealed class GuardTextureFingerprintEncoding
    {
        public byte[] Pixels { get; internal set; }
        public GuardTextureFingerprintPrivateRecord PrivateRecord { get; internal set; }
        public GuardTextureFingerprintObservation SelfObservation { get; internal set; }
        public double MeanAbsoluteRgb { get; internal set; }
        public double MaximumRgb { get; internal set; }
        public double PsnrDb { get; internal set; }
    }

    // CPU-only opaque encoded-sRGB RGBA8, with unchanged dimensions/alpha. No Unity/IO/runtime keys.
    public static class GuardTextureFingerprintV1
    {
        public const string Id = "DctRgb8TextureFingerprintV1", ColorDomain = "EncodedSrgbRgb8V1", Raster = "BottomLeftRowMajorRgba8V1";
        public const int Version = 1, Frame = 128, Symbols = 128, CarriersPerSymbol = 3, MinimumSymbols = 96;
        const double BoundaryMargin = 0.06;
        static readonly double[,] basis = Basis();
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        internal static bool Hex(string text, int length)
        { if (text == null || text.Length != length) return false; foreach (char c in text) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false; return true; }
        internal static string Digest(byte[] bytes)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal static void RequirePixels(byte[] pixels, int width, int height, bool authoring)
        {
            int min = authoring ? 128 : 1, max = authoring ? 1024 : 2048;
            if (width < min || height != width || width > max || (authoring && (width & (width - 1)) != 0) || pixels == null || pixels.Length != width * height * 4)
                throw new ArgumentException("TextureFingerprint: RGBA8 cuadrado/tamaño fuera del contrato.");
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 255) throw new ArgumentException("TextureFingerprint: solo albedo completamente opaco.");
        }
        public static string PixelHash(byte[] pixels, int width, int height)
        {
            RequirePixels(pixels, width, height, false);
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                { writer.Write("LAG/texture-fingerprint/pixels/v1"); writer.Write(ColorDomain); writer.Write(Raster); writer.Write(width); writer.Write(height); writer.Write(pixels.Length); writer.Write(pixels); }
                return Digest(stream.ToArray());
            }
        }
        static double[,] Basis()
        {
            var result = new double[Frame, Frame];
            for (int u = 0; u < Frame; u++) for (int x = 0; x < Frame; x++) result[u, x] = Math.Cos(Math.PI * (x + 0.5) * u / Frame) * Math.Sqrt((u == 0 ? 1.0 : 2.0) / Frame);
            return result;
        }
        internal static double[] CanonicalLuma(byte[] pixels, int size)
        {
            var result = new double[Frame * Frame];
            if (size >= Frame)
            {
                // Exact normalized footprint average, including noninteger candidate resize ratios.
                double scale = size / (double)Frame;
                for (int y = 0; y < Frame; y++) for (int x = 0; x < Frame; x++)
                {
                    double left = x * scale, right = (x + 1) * scale, bottom = y * scale, top = (y + 1) * scale, sum = 0;
                    for (int yy = (int)Math.Floor(bottom); yy < Math.Ceiling(top); yy++) for (int xx = (int)Math.Floor(left); xx < Math.Ceiling(right); xx++)
                    {
                        double weight = (Math.Min(right, xx + 1) - Math.Max(left, xx)) * (Math.Min(top, yy + 1) - Math.Max(bottom, yy));
                        int offset = (Math.Min(size - 1, yy) * size + Math.Min(size - 1, xx)) * 4;
                        sum += weight * Luma(pixels, offset);
                    }
                    result[y * Frame + x] = sum / (scale * scale);
                }
            }
            else
            {
                for (int y = 0; y < Frame; y++) for (int x = 0; x < Frame; x++)
                {
                    double px = Math.Max(0, Math.Min(size - 1, (x + 0.5) * size / Frame - 0.5)), py = Math.Max(0, Math.Min(size - 1, (y + 0.5) * size / Frame - 0.5));
                    int x0 = (int)px, y0 = (int)py, x1 = Math.Min(size - 1, x0 + 1), y1 = Math.Min(size - 1, y0 + 1); double dx = px - x0, dy = py - y0;
                    result[y * Frame + x] = (1 - dy) * ((1 - dx) * Luma(pixels, (y0 * size + x0) * 4) + dx * Luma(pixels, (y0 * size + x1) * 4)) +
                        dy * ((1 - dx) * Luma(pixels, (y1 * size + x0) * 4) + dx * Luma(pixels, (y1 * size + x1) * 4));
                }
            }
            return result;
        }
        static double Luma(byte[] pixels, int offset) => (0.2126 * pixels[offset] + 0.7152 * pixels[offset + 1] + 0.0722 * pixels[offset + 2]) / 255;
        internal static double[] Transform(double[] values, bool inverse)
        {
            var temp = new double[Frame * Frame]; var result = new double[temp.Length];
            for (int y = 0; y < Frame; y++) for (int x = 0; x < Frame; x++)
            {
                double sum = 0; for (int k = 0; k < Frame; k++) sum += (inverse ? basis[k, y] : basis[y, k]) * values[k * Frame + x]; temp[y * Frame + x] = sum;
            }
            for (int y = 0; y < Frame; y++) for (int x = 0; x < Frame; x++)
            {
                double sum = 0; for (int k = 0; k < Frame; k++) sum += temp[y * Frame + k] * (inverse ? basis[k, x] : basis[x, k]); result[y * Frame + x] = sum;
            }
            return result;
        }
        internal sealed class Pattern : IDisposable
        {
            public readonly int[] Coordinates;
            public readonly double[] Dither;
            public readonly byte[] Codeword;
            public Pattern(byte[] seed, byte[] codeword, double step)
            {
                Codeword = (byte[])codeword.Clone(); Dither = new double[Symbols * CarriersPerSymbol];
                var pool = new System.Collections.Generic.List<int>(); for (int u = 0; u <= 34; u++) for (int v = 0; v <= 34; v++) if (u + v >= 3 && u + v <= 34) pool.Add(u * Frame + v);
                var shuffled = pool.ToArray(); byte[] block = null; int offset = 32; uint counter = 0;
                byte[] header = Encoding.UTF8.GetBytes("LAG/texture-fingerprint/dct-rgb8/v1/layout\0"); var input = new byte[header.Length + 4]; Buffer.BlockCopy(header, 0, input, 0, header.Length);
                using (var hmac = new HMACSHA256(seed))
                try
                {
                    for (int i = shuffled.Length - 1; i > 0; i--)
                    {
                        ulong limit = (1UL << 32) - (1UL << 32) % (uint)(i + 1); uint value;
                        do
                        {
                            if (offset == 32)
                            { if (block != null) Array.Clear(block, 0, block.Length); for (int b = 0; b < 4; b++) input[header.Length + b] = (byte)(counter >> (24 - 8 * b)); block = hmac.ComputeHash(input); counter++; offset = 0; }
                            value = ((uint)block[offset] << 24) | ((uint)block[offset + 1] << 16) | ((uint)block[offset + 2] << 8) | block[offset + 3]; offset += 4;
                        } while (value >= limit);
                        int j = (int)(value % (uint)(i + 1)), old = shuffled[i]; shuffled[i] = shuffled[j]; shuffled[j] = old;
                    }
                    Coordinates = new int[Dither.Length]; Array.Copy(shuffled, Coordinates, Coordinates.Length);
                    header = Encoding.UTF8.GetBytes("LAG/texture-fingerprint/dct-rgb8/v1/dither\0"); Array.Clear(input, 0, input.Length); input = new byte[header.Length + 4]; Buffer.BlockCopy(header, 0, input, 0, header.Length);
                    for (int i = 0; i < Dither.Length; i++)
                    {
                        for (int b = 0; b < 4; b++) input[header.Length + b] = (byte)(i >> (24 - 8 * b)); byte[] bytes = hmac.ComputeHash(input);
                        try { ulong value = 0; for (int b = 0; b < 8; b++) value = (value << 8) | bytes[b]; Dither[i] = (value >> 11) * (1.0 / 9007199254740992.0) * 2 * step; }
                        finally { Array.Clear(bytes, 0, bytes.Length); }
                    }
                }
                finally { Array.Clear(shuffled, 0, shuffled.Length); Array.Clear(input, 0, input.Length); if (block != null) Array.Clear(block, 0, block.Length); }
            }
            public int Bit(int symbol) => (Codeword[symbol / 8] >> (7 - symbol % 8)) & 1;
            public void Dispose() { Array.Clear(Coordinates, 0, Coordinates.Length); Array.Clear(Dither, 0, Dither.Length); Array.Clear(Codeword, 0, Codeword.Length); }
        }
        internal static GuardTextureFingerprintObservation Observe(byte[] pixels, int size, Pattern pattern, double step)
        {
            var result = new GuardTextureFingerprintObservation(); if (size < 32) return result;
            var spectrum = Transform(CanonicalLuma(pixels, size), false);
            for (int symbol = 0; symbol < Symbols; symbol++)
            {
                int count = 0, votes = 0;
                for (int j = 0; j < CarriersPerSymbol; j++)
                {
                    int index = symbol * CarriersPerSymbol + j; double value = (spectrum[pattern.Coordinates[index]] - pattern.Dither[index]) / step;
                    if (Math.Abs(value - Math.Floor(value) - 0.5) < BoundaryMargin) continue;
                    count++; votes += (int)((long)Math.Round(value, MidpointRounding.ToEven) & 1);
                }
                result.UsableCarriers += count;
                if (count >= 2 && votes * 2 != count) { result.UsableSymbols++; if ((votes * 2 > count ? 1 : 0) == pattern.Bit(symbol)) result.MatchedSymbols++; }
            }
            return result;
        }
        internal static string Authenticate(GuardTextureFingerprintPrivateRecord r, byte[] seed)
        {
            using (var stream = new MemoryStream())
            {
                using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
                { w.Write("LAG/texture-fingerprint/record-auth/v1"); w.Write(r.schema); w.Write(r.algorithmVersion); w.Write(r.algorithm); w.Write(r.state); w.Write(r.colorDomain); w.Write(r.raster);
                    w.Write(r.buildId); w.Write(r.bindingId); w.Write(r.sourcePixelHash); w.Write(r.markedPixelHash); w.Write(r.sourceWidth); w.Write(r.sourceHeight); w.Write(r.usableSymbols);
                    w.Write(r.step); w.Write(r.maximumMeanAbsoluteRgb); w.Write(r.maximumRgb); w.Write(r.minimumPsnrDb); }
                using (var hmac = new HMACSHA256(seed)) { byte[] bytes = hmac.ComputeHash(stream.ToArray()); try { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); } finally { Array.Clear(bytes, 0, bytes.Length); } }
            }
        }
        internal static GuardTextureFingerprintOptions ValidateRecord(GuardFingerprintContext context, GuardTextureFingerprintPrivateRecord r, byte[] seed)
        {
            if (context == null || r == null || r.schema != 1 || r.algorithmVersion != Version || r.algorithm != Id || r.state != "CpuSelfVerified" || r.colorDomain != ColorDomain || r.raster != Raster ||
                r.buildId != context.BuildId || !Hex(r.bindingId, 64) || !Hex(r.sourcePixelHash, 64) || !Hex(r.markedPixelHash, 64) || !Hex(r.authentication, 64) ||
                r.sourceWidth < 128 || r.sourceWidth > 1024 || r.sourceHeight != r.sourceWidth || (r.sourceWidth & (r.sourceWidth - 1)) != 0 || r.usableSymbols < MinimumSymbols || r.usableSymbols > Symbols)
                throw new ArgumentException("TextureFingerprint: registro/color/raster/versionado incompatible.");
            var options = new GuardTextureFingerprintOptions(r.step, r.maximumMeanAbsoluteRgb, r.maximumRgb, r.minimumPsnrDb);
            string expected = Authenticate(r, seed); int diff = 0; for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ r.authentication[i];
            if (diff != 0) throw new InvalidOperationException("TextureFingerprint: registro modificado o contexto ajeno."); return options;
        }
        public static GuardTextureFingerprintPlan Plan(byte[] rgba, int width, int height, GuardFingerprintContext context, string bindingId, GuardTextureFingerprintOptions options = null) =>
            new GuardTextureFingerprintPlan(rgba, width, height, context, bindingId, options ?? new GuardTextureFingerprintOptions());
        public static GuardTextureFingerprintPlan RestorePlan(byte[] rgba, GuardFingerprintContext context, GuardTextureFingerprintPrivateRecord r)
        {
            if (context == null || r == null) throw new ArgumentNullException(); byte[] seed = context.DeriveCarrierSeed(GuardFingerprintChannel.Texture, r.bindingId);
            try
            {
                var options = ValidateRecord(context, r, seed);
                if (PixelHash(rgba, r.sourceWidth, r.sourceHeight) != r.sourcePixelHash) throw new InvalidOperationException("TextureFingerprint: fuente original modificada.");
                var plan = Plan(rgba, r.sourceWidth, r.sourceHeight, context, r.bindingId, options);
                try { if (plan.Encode(rgba).PrivateRecord.authentication != r.authentication) throw new InvalidOperationException("TextureFingerprint: reproducción distinta al registro."); return plan; }
                catch { plan.Dispose(); throw; }
            }
            finally { Array.Clear(seed, 0, seed.Length); }
        }
        public static GuardTextureFingerprintObserver CreateObserver(GuardFingerprintContext context, GuardTextureFingerprintPrivateRecord r) => new GuardTextureFingerprintObserver(context, r);
    }
    public sealed class GuardTextureFingerprintPlan : IDisposable
    {
        readonly byte[] original, seed;
        readonly GuardTextureFingerprintV1.Pattern pattern;
        readonly string buildId, bindingId, sourceHash;
        readonly int size;
        readonly GuardTextureFingerprintOptions options;
        bool disposed;
        internal GuardTextureFingerprintPlan(byte[] rgba, int width, int height, GuardFingerprintContext context, string bindingId, GuardTextureFingerprintOptions options)
        {
            GuardTextureFingerprintV1.RequirePixels(rgba, width, height, true); if (context == null) throw new ArgumentNullException(nameof(context));
            original = (byte[])rgba.Clone(); size = width; this.options = options; sourceHash = GuardTextureFingerprintV1.PixelHash(original, size, size); buildId = context.BuildId; this.bindingId = bindingId;
            byte[] codeword = null;
            try { seed = context.DeriveCarrierSeed(GuardFingerprintChannel.Texture, bindingId); codeword = context.DeriveCodeword(GuardFingerprintChannel.Texture, bindingId); pattern = new GuardTextureFingerprintV1.Pattern(seed, codeword, options.Step); }
            catch { Dispose(); throw; } finally { if (codeword != null) Array.Clear(codeword, 0, codeword.Length); }
        }
        public GuardTextureFingerprintEncoding Encode(byte[] rgba)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GuardTextureFingerprintPlan));
            if (GuardTextureFingerprintV1.PixelHash(rgba, size, size) != sourceHash) throw new InvalidOperationException("TextureFingerprint: fuente modificada después de planificar.");
            var frame = GuardTextureFingerprintV1.CanonicalLuma(original, size); var spectrum = GuardTextureFingerprintV1.Transform(frame, false);
            for (int i = 0; i < pattern.Coordinates.Length; i++)
            { int coordinate = pattern.Coordinates[i], bit = pattern.Bit(i / 3); double value = spectrum[coordinate]; spectrum[coordinate] = options.Step * (2 * Math.Round(((value - pattern.Dither[i]) / options.Step - bit) / 2, MidpointRounding.ToEven) + bit) + pattern.Dither[i]; }
            var reconstructed = GuardTextureFingerprintV1.Transform(spectrum, true); var result = (byte[])original.Clone(); int scale = size / GuardTextureFingerprintV1.Frame;
            double abs = 0, squares = 0, largest = 0; bool changed = false;
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                int frameIndex = y / scale * GuardTextureFingerprintV1.Frame + x / scale; double delta = reconstructed[frameIndex] - frame[frameIndex]; int offset = (y * size + x) * 4;
                for (int c = 0; c < 3; c++)
                { result[offset + c] = (byte)Math.Round(Math.Max(0, Math.Min(255, original[offset + c] + 255 * delta)), MidpointRounding.ToEven); double difference = Math.Abs(result[offset + c] - original[offset + c]) / 255.0; changed |= difference > 0; abs += difference; squares += difference * difference; largest = Math.Max(largest, difference); }
            }
            double mean = abs / (size * size * 3), mse = squares / (size * size * 3), psnr = mse == 0 ? double.PositiveInfinity : -10 * Math.Log10(mse);
            if (!changed || mean > options.MaximumMeanAbsoluteRgb || largest > options.MaximumRgb || psnr < options.MinimumPsnrDb)
                throw new InvalidOperationException("TextureFingerprint: RGB8 excede el presupuesto o no transporta cambio.");
            var self = GuardTextureFingerprintV1.Observe(result, size, pattern, options.Step);
            if (self.UsableSymbols < GuardTextureFingerprintV1.MinimumSymbols || self.MatchedSymbols != self.UsableSymbols)
                throw new InvalidOperationException("TextureFingerprint: autoverificación RGB8 insuficiente; se conserva la fuente.");
            if (GuardTextureFingerprintV1.PixelHash(rgba, size, size) != sourceHash) throw new InvalidOperationException("TextureFingerprint: mutación durante encoding.");
            var record = new GuardTextureFingerprintPrivateRecord { buildId = buildId, bindingId = bindingId, sourcePixelHash = sourceHash, markedPixelHash = GuardTextureFingerprintV1.PixelHash(result, size, size),
                sourceWidth = size, sourceHeight = size, usableSymbols = self.UsableSymbols, step = options.Step, maximumMeanAbsoluteRgb = options.MaximumMeanAbsoluteRgb, maximumRgb = options.MaximumRgb, minimumPsnrDb = options.MinimumPsnrDb };
            record.authentication = GuardTextureFingerprintV1.Authenticate(record, seed);
            return new GuardTextureFingerprintEncoding { Pixels = result, PrivateRecord = record, SelfObservation = self, MeanAbsoluteRgb = mean, MaximumRgb = largest, PsnrDb = psnr };
        }
        public void Dispose() { if (disposed) return; disposed = true; if (seed != null) Array.Clear(seed, 0, seed.Length); pattern?.Dispose(); }
    }
    public sealed class GuardTextureFingerprintObserver : IDisposable
    {
        readonly GuardTextureFingerprintV1.Pattern pattern;
        readonly double step;
        bool disposed;
        internal GuardTextureFingerprintObserver(GuardFingerprintContext context, GuardTextureFingerprintPrivateRecord record)
        {
            if (context == null || record == null) throw new ArgumentNullException(); byte[] seed = context.DeriveCarrierSeed(GuardFingerprintChannel.Texture, record.bindingId), codeword = null;
            try { step = GuardTextureFingerprintV1.ValidateRecord(context, record, seed).Step; codeword = context.DeriveCodeword(GuardFingerprintChannel.Texture, record.bindingId); pattern = new GuardTextureFingerprintV1.Pattern(seed, codeword, step); }
            finally { Array.Clear(seed, 0, seed.Length); if (codeword != null) Array.Clear(codeword, 0, codeword.Length); }
        }
        public GuardTextureFingerprintObservation Observe(byte[] rgba, int width, int height)
        { if (disposed) throw new ObjectDisposedException(nameof(GuardTextureFingerprintObserver)); GuardTextureFingerprintV1.RequirePixels(rgba, width, height, false); return GuardTextureFingerprintV1.Observe(rgba, width, pattern, step); }
        public void Dispose() { if (disposed) return; disposed = true; pattern.Dispose(); }
    }
}
