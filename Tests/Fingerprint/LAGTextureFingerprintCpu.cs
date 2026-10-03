// SPDX-License-Identifier: MIT
// Standalone C# CPU host. All contexts and images are public synthetic fixtures.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using LinuxAvatarGuard;

[Serializable]
public sealed class LAGTextureFingerprintVector
{
    public int family, contextIndex, size;
    public string sourceFile, markedFile;
    public GuardTextureFingerprintPrivateRecord record;
    public double meanAbsoluteRgb, maximumRgb, psnrDb;
}
public static class LAGTextureFingerprintCpu
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static void Reject(Action action, string name)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } Check(rejected, name); }
    static byte[] Seed(string value) { using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(value)); }
    static GuardFingerprintContext Context(int index) => GuardFingerprintContext.CreateReproducible(index.ToString("x32"), Seed("stage16-fingerprint-" + index), Seed("stage16-carrier-" + index));
    static string Binding(int family) => BitConverter.ToString(Seed("stage16-binding-" + family)).Replace("-", "").ToLowerInvariant();
    public static byte[] Fixture(int size, int family)
    {
        var pixels = new byte[size * size * 4]; var random = new Random(22016 + family);
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            double xx = x / (double)(size - 1), yy = y / (double)(size - 1); double r, g, b;
            if (family == 0) { r = 0.22 + 0.48 * xx; g = 0.30 + 0.34 * yy; b = 0.45 + 0.12 * Math.Sin(xx * 14) * Math.Cos(yy * 11); }
            else if (family == 1) { double fabric = 0.065 * Math.Sin(xx * 105) * Math.Cos(yy * 112) + (random.NextDouble() * 2 - 1) * 0.042; r = 0.38 + fabric; g = 0.52 + fabric; b = 0.64 + fabric; }
            else { double patch = xx + yy > 0.85 && xx - yy < 0.33 ? 1 : 0; r = 0.19 + 0.50 * patch; g = 0.57 - 0.20 * patch + 0.08 * xx; b = 0.31 + 0.25 * (xx > 0.6 ? 1 : 0); }
            int i = (y * size + x) * 4; pixels[i] = (byte)Math.Round(255 * r); pixels[i + 1] = (byte)Math.Round(255 * g); pixels[i + 2] = (byte)Math.Round(255 * b); pixels[i + 3] = 255;
        }
        return pixels;
    }
    static void Save<T>(string path, T value) { using (var stream = File.Create(path)) new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); }
    static T Load<T>(string path) { using (var stream = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
    static GuardTextureFingerprintPrivateRecord Copy(GuardTextureFingerprintPrivateRecord record)
    {
        using (var stream = new MemoryStream()) { var serializer = new DataContractJsonSerializer(record.GetType()); serializer.WriteObject(stream, record); stream.Position = 0; return (GuardTextureFingerprintPrivateRecord)serializer.ReadObject(stream); }
    }
    static string Observation(GuardTextureFingerprintObservation o) => "{\"usableSymbols\":" + o.UsableSymbols + ",\"matchedSymbols\":" + o.MatchedSymbols + ",\"usableCarriers\":" + o.UsableCarriers + ",\"decision\":\"" + o.Decision + "\",\"confidence\":null}";
    static int Run(string folder)
    {
        var vectors = new List<LAGTextureFingerprintVector>(); var hashes = new HashSet<string>();
        for (int family = 0; family < 3; family++) for (int index = 0; index < 4; index++)
        {
            var source = Fixture(128, family); var before = (byte[])source.Clone();
            using (var context = Context(index)) using (var plan = GuardTextureFingerprintV1.Plan(source, 128, 128, context, Binding(family)))
            {
                var output = plan.Encode(source);
                Check(source.SequenceEqual(before), "source preserved"); Check(output.Pixels.Length == source.Length, "dimensions/length preserved");
                Check(Enumerable.Range(0, source.Length / 4).All(i => output.Pixels[i * 4 + 3] == 255), "alpha preserved");
                Check(output.SelfObservation.UsableSymbols >= 96 && output.SelfObservation.MatchedSymbols == output.SelfObservation.UsableSymbols, "native RGB8 self check");
                Check(output.MeanAbsoluteRgb <= 0.0015 && output.MaximumRgb <= 0.012 && output.PsnrDb >= 45, "quality budgets");
                Check(output.Pixels.SequenceEqual(plan.Encode(source).Pixels), "deterministic marking");
                using (var restoredContext = GuardFingerprintContext.RestorePrivateRecord(context.ExportPrivateRecord(), context.BuildId))
                using (var restored = GuardTextureFingerprintV1.RestorePlan(source, restoredContext, output.PrivateRecord))
                    Check(restored.Encode(source).Pixels.SequenceEqual(output.Pixels), "private record restores exactly");
                Check(hashes.Add(output.PrivateRecord.markedPixelHash), "different build/family byte hash");
                using (var observer = GuardTextureFingerprintV1.CreateObserver(context, output.PrivateRecord))
                {
                    var observed = observer.Observe(output.Pixels, 128, 128); Check(observed.Decision == GuardTextureFingerprintDecision.ResearchMatch, "independent observer native");
                    Check(!observed.Calibrated && observed.Confidence == null, "uncalibrated score");
                    Check(observer.Observe(source, 128, 128).Decision != GuardTextureFingerprintDecision.ResearchMatch, "unmarked source negative");
                    var tiny = new byte[] { 100, 100, 100, 255 }; Check(observer.Observe(tiny, 1, 1).Decision == GuardTextureFingerprintDecision.InconclusiveCoverage, "tiny abstention");
                }
                int vectorIndex = vectors.Count;
                string sourceName = "source-" + vectorIndex + ".rgba8.bin", markedName = "marked-" + vectorIndex + ".rgba8.bin";
                File.WriteAllBytes(Path.Combine(folder, sourceName), source); File.WriteAllBytes(Path.Combine(folder, markedName), output.Pixels);
                vectors.Add(new LAGTextureFingerprintVector { family = family, contextIndex = index, size = 128, sourceFile = sourceName, markedFile = markedName, record = output.PrivateRecord,
                    meanAbsoluteRgb = output.MeanAbsoluteRgb, maximumRgb = output.MaximumRgb, psnrDb = output.PsnrDb });
            }
        }
        for (int size = 256; size <= 1024; size *= 2)
        {
            var source = Fixture(size, 0); var before = (byte[])source.Clone();
            using (var context = Context(0)) using (var plan = GuardTextureFingerprintV1.Plan(source, size, size, context, Binding(0)))
            {
                var output = plan.Encode(source); Check(source.SequenceEqual(before), "larger source preserved");
                Check(output.Pixels.Length == source.Length && output.SelfObservation.MatchedSymbols == output.SelfObservation.UsableSymbols, "larger native self check");
                int i = vectors.Count; string sourceFile = "source-" + i + ".rgba8.bin", markedFile = "marked-" + i + ".rgba8.bin";
                File.WriteAllBytes(Path.Combine(folder, sourceFile), source); File.WriteAllBytes(Path.Combine(folder, markedFile), output.Pixels);
                vectors.Add(new LAGTextureFingerprintVector { family = 0, contextIndex = 0, size = size, sourceFile = sourceFile, markedFile = markedFile, record = output.PrivateRecord,
                    meanAbsoluteRgb = output.MeanAbsoluteRgb, maximumRgb = output.MaximumRgb, psnrDb = output.PsnrDb });
            }
        }
        var original = Fixture(128, 0); var record = vectors[0].record;
        using (var context = Context(0))
        {
            var tampering = new Action<GuardTextureFingerprintPrivateRecord>[] { r => r.schema++, r => r.algorithmVersion++, r => r.algorithm += "changed", r => r.state = "Ready",
                r => r.colorDomain = "LinearRgb", r => r.raster = "TopLeft", r => r.buildId = new string('a',32), r => r.bindingId = new string('b',64),
                r => r.sourcePixelHash = new string('a',64), r => r.markedPixelHash = new string('b',64), r => r.authentication = new string('c',64),
                r => r.sourceWidth = 256, r => r.sourceHeight = 256, r => r.usableSymbols--, r => r.step = 0.017,
                r => r.maximumMeanAbsoluteRgb = 0.0014, r => r.maximumRgb = 0.011, r => r.minimumPsnrDb = 46 };
            foreach (var change in tampering) { var altered = Copy(record); change(altered); Reject(() => { using (GuardTextureFingerprintV1.CreateObserver(context, altered)) { } }, "tampered record"); }
            using (var plan = GuardTextureFingerprintV1.Plan(original, 128, 128, context, Binding(0)))
            { original[0]++; byte changed = original[0]; Reject(() => plan.Encode(original), "late source mutation rejected"); Check(original[0] == changed, "caller change preserved"); original[0]--; }
            using (var observer = GuardTextureFingerprintV1.CreateObserver(context, record))
            {
                var transparent = (byte[])original.Clone(); transparent[3] = 254; Reject(() => observer.Observe(transparent, 128, 128), "alpha candidate rejected");
                Reject(() => observer.Observe(original, 128, 127), "candidate shape rejected");
                Reject(() => observer.Observe(original, 4096, 4096), "candidate resource envelope");
                observer.Dispose(); bool disposed = false; try { observer.Observe(original, 128, 128); } catch (ObjectDisposedException) { disposed = true; } Check(disposed, "observer disposal");
            }
            Reject(() => new GuardTextureFingerprintOptions(step: 0.019), "larger strength rejected");
            Reject(() => new GuardTextureFingerprintOptions(step: double.NaN), "NaN policy rejected");
            Reject(() => new GuardTextureFingerprintOptions(maximumMeanAbsoluteRgb: 0.002), "larger mean budget rejected");
            Reject(() => new GuardTextureFingerprintOptions(maximumRgb: 0.02), "larger maximum budget rejected");
            Reject(() => new GuardTextureFingerprintOptions(minimumPsnrDb: 44), "weaker PSNR rejected");
            Reject(() => { using (var plan = GuardTextureFingerprintV1.Plan(original, 128, 128, context, Binding(0), new GuardTextureFingerprintOptions(maximumRgb: 1e-10))) plan.Encode(original); }, "tight budget fails without forcing");
            var alpha = (byte[])original.Clone(); alpha[3] = 0;
            Reject(() => { using (GuardTextureFingerprintV1.Plan(alpha, 128, 128, context, Binding(0))) { } }, "transparent source rejected");
            Reject(() => { using (GuardTextureFingerprintV1.Plan(original, 64, 64, context, Binding(0))) { } }, "small source rejected");
            Reject(() => { using (GuardTextureFingerprintV1.Plan(original, 128, 128, context, "bad")) { } }, "bad binding rejected");
            Reject(() => { using (GuardTextureFingerprintV1.Plan(new byte[4 * 129 * 129], 129, 129, context, Binding(0))) { } }, "nonpower source rejected");
            var disposedPlan = GuardTextureFingerprintV1.Plan(original,128,128,context,Binding(0)); disposedPlan.Dispose(); disposedPlan.Dispose();
            bool rejected = false; try { disposedPlan.Encode(original); } catch (ObjectDisposedException) { rejected = true; } Check(rejected,"plan disposal");
        }
        using (var foreign = Context(1)) Reject(() => { using (GuardTextureFingerprintV1.CreateObserver(foreign, record)) { } }, "foreign identity rejected");
        Check(GuardTextureFingerprintV1.PixelHash(original, 128, 128) == record.sourcePixelHash, "final source unchanged");
        Save(Path.Combine(folder, "vectors.json"), vectors.ToArray());
        string report = "{\"schema\":1,\"checks\":" + checks + ",\"nativeVectors\":" + vectors.Count + ",\"diverseBuilds\":12,\"sourcePreserved\":true,\"gpuUsed\":false,\"unityStarted\":false,\"minimumPsnrDb\":" + vectors.Min(v=>v.psnrDb).ToString("R",CultureInfo.InvariantCulture) +
            ",\"maximumMeanAbsoluteRgb\":" + vectors.Max(v=>v.meanAbsoluteRgb).ToString("R",CultureInfo.InvariantCulture) + ",\"maximumRgb\":" + vectors.Max(v=>v.maximumRgb).ToString("R",CultureInfo.InvariantCulture) + "}";
        File.WriteAllText(Path.Combine(folder, "results.json"), report + "\n"); Console.WriteLine("TextureFingerprint CPU: " + checks + " checks, " + vectors.Count + " synthetic native vectors. No graphics."); return 0;
    }
    static int Queries(string folder, string archive, string reportPath)
    {
        var vectors = Load<LAGTextureFingerprintVector[]>(Path.Combine(folder, "vectors.json")); var results = new List<string>();
        using (var reader = new BinaryReader(File.OpenRead(archive)))
        {
            if (reader.ReadString() != "LAG-texture-cpu-queries-v1") throw new ArgumentException("query format"); int count = reader.ReadInt32(); if (count < 1 || count > 4096) throw new ArgumentException("query limit");
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString(); int vector = reader.ReadInt32(), width = reader.ReadInt32(), height = reader.ReadInt32(), bytes = reader.ReadInt32();
                if (name.Length > 96 || !name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-') || vector < 0 || vector >= vectors.Length || width < 1 || width > 2048 || height != width || bytes != width * height * 4) throw new ArgumentException("query envelope");
                byte[] rgba = reader.ReadBytes(bytes); if (rgba.Length != bytes) throw new IOException("truncated query"); var v = vectors[vector];
                using (var context = Context(v.contextIndex)) using (var observer = GuardTextureFingerprintV1.CreateObserver(context, v.record)) results.Add("{\"name\":\"" + name + "\",\"vector\":" + vector + ",\"observation\":" + Observation(observer.Observe(rgba, width, height)) + "}");
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new ArgumentException("query trailing data");
        }
        File.WriteAllText(reportPath, "[" + string.Join(",", results) + "]\n"); Console.WriteLine("TextureFingerprint C# observed " + results.Count + " transformed CPU candidates."); return 0;
    }
    public static int Main(string[] args)
    { try { return args.Length == 4 && args[0] == "--observe" ? Queries(args[1],args[2],args[3]) : Run(args[0]); } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
}
