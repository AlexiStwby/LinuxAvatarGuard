// SPDX-License-Identifier: MIT
// Standalone CPU host: no Unity assemblies, editor, graphics or avatar data.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LinuxAvatarGuard;

public static class LAGMeshFingerprintCpu
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static void Reject(Action action, string name)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, name);
    }
    static byte[] Seed(string name) { using (var hash = SHA256.Create()) return hash.ComputeHash(Encoding.UTF8.GetBytes(name)); }
    static GuardFingerprintContext Context(int index) => GuardFingerprintContext.CreateReproducible(index.ToString("x32"), Seed("stage15-fingerprint-" + index), Seed("stage15-carrier-" + index));
    // Every radial band has independent directions plus exact antipodes. Six anchors stay outside the carriers.
    public static float[] Fixture(int fixtureSeed = 17015)
    {
        var random = new Random(fixtureSeed); var positions = new List<float>();
        for (int band = 0; band < 128; band++)
            for (int sample = 0; sample < 16; sample++)
            {
                double radius = 0.1 + 0.88 * (band + 0.2 + 0.5 * sample / 16) / 128;
                double z = random.NextDouble() * 2 - 1, phi = random.NextDouble() * Math.PI * 2, xy = Math.Sqrt(1 - z * z);
                float x = (float)(radius * xy * Math.Cos(phi)), y = (float)(radius * xy * Math.Sin(phi)), zz = (float)(radius * z);
                positions.AddRange(new[] { x, y, zz, -x, -y, -zz });
            }
        positions.AddRange(new float[] { 1, 0, 0, -1, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 1, 0, 0, -1 });
        return positions.ToArray();
    }
    static float[] Shuffle(float[] points)
    {
        var indices = Enumerable.Range(0, points.Length / 3).ToArray(); var random = new Random(17015);
        for (int i = indices.Length - 1; i > 0; i--) { int j = random.Next(i + 1), old = indices[i]; indices[i] = indices[j]; indices[j] = old; }
        var result = new float[points.Length];
        for (int i = 0; i < indices.Length; i++) Array.Copy(points, indices[i] * 3, result, i * 3, 3);
        return result;
    }
    static float[] Duplicates(float[] points)
    {
        var result = new List<float>(points);
        // Deliberately biased multiplicities. The frame must not be a mean over duplicate vertices.
        for (int i = 0; i < 1200; i++) for (int j = 0; j < 3; j++) result.Add(points[(i < 900 ? 5 : i) * 3 + j]);
        return result.ToArray();
    }
    static float[] Similarity(float[] points)
    {
        var result = new float[points.Length]; double c = Math.Cos(0.93), s = Math.Sin(0.93);
        for (int i = 0; i < points.Length; i += 3)
        {
            result[i] = (float)((c * points[i] - s * points[i + 1]) * 2.37 + 1.2);
            result[i + 1] = (float)((s * points[i] + c * points[i + 1]) * 2.37 - 0.4);
            result[i + 2] = (float)(points[i + 2] * 2.37 + 2);
        }
        return result;
    }
    static GuardMeshFingerprintPrivateRecord Copy(GuardMeshFingerprintPrivateRecord r) => new GuardMeshFingerprintPrivateRecord {
        schema = r.schema, algorithm = r.algorithm, algorithmVersion = r.algorithmVersion, state = r.state, buildId = r.buildId, bindingId = r.bindingId,
        sourcePositionHash = r.sourcePositionHash, markedPositionHash = r.markedPositionHash, authentication = r.authentication,
        relativeStep = r.relativeStep, maximumRelativeDisplacement = r.maximumRelativeDisplacement, maximumObjectDisplacement = r.maximumObjectDisplacement,
        sourceVertices = r.sourceVertices, sourceUniquePositions = r.sourceUniquePositions, usableSymbols = r.usableSymbols };
    static void Binary(string path, float[] data) { using (var writer = new BinaryWriter(File.Create(path))) foreach (float value in data) writer.Write(value); }
    static string Json(GuardMeshFingerprintObservation o) => "{\"usableSymbols\":" + o.UsableSymbols + ",\"matchedSymbols\":" + o.MatchedSymbols +
        ",\"uniquePositions\":" + o.UniquePositions + ",\"bitAgreement\":" + o.BitAgreement.ToString("R", CultureInfo.InvariantCulture) + ",\"decision\":\"" + o.Decision + "\",\"confidence\":null}";
    public static int Main(string[] args)
    {
        try
        {
            string outputFolder = args[0], binding = new string('b', 64);
            var original = Fixture(); var originalCopy = (float[])original.Clone(); string originalHash = GuardMeshFingerprintV1.PositionHash(original);
            var observations = new Dictionary<string, GuardMeshFingerprintObservation>();
            var outputHashes = new HashSet<string>(); double maximumDisplacement = 0, maximumRelative = 0;
            using (var context = Context(0))
            using (var plan = GuardMeshFingerprintV1.Plan(original, context, binding))
            {
                var encoded = plan.Encode(original);
                Check(original.SequenceEqual(originalCopy), "source array preserved");
                Check(encoded.Positions.Length == original.Length, "vertex count preserved");
                Check(encoded.SelfObservation.UsableSymbols == 128 && encoded.SelfObservation.MatchedSymbols == 128, "native Float32 self check");
                Check(encoded.PrivateRecord.state == "CpuSelfVerified", "limited state");
                Check(encoded.MaximumDisplacement <= 5e-5 && encoded.MaximumRelativeDisplacement <= 1e-5, "measured budgets");
                Check(!encoded.Positions.SequenceEqual(original), "actual positional marking");
                Check(encoded.Positions.SequenceEqual(plan.Encode(original).Positions), "deterministic encoding");
                using (var restoredContext = GuardFingerprintContext.RestorePrivateRecord(context.ExportPrivateRecord(), context.BuildId))
                using (var restoredPlan = GuardMeshFingerprintV1.RestorePlan(original, restoredContext, encoded.PrivateRecord))
                    Check(encoded.Positions.SequenceEqual(restoredPlan.Encode(original).Positions), "exact restoration");
                using (var observer = GuardMeshFingerprintV1.CreateObserver(context, encoded.PrivateRecord))
                {
                    observations["native"] = observer.Observe(encoded.Positions);
                    observations["reorder"] = observer.Observe(Shuffle(encoded.Positions));
                    observations["duplicate_seams"] = observer.Observe(Duplicates(encoded.Positions));
                    observations["similarity_float32"] = observer.Observe(Similarity(encoded.Positions));
                    observations["reflection"] = observer.Observe(encoded.Positions.Select(v => -v).ToArray());
                    observations["unmarked_same_source"] = observer.Observe(original);
                    foreach (string name in new[] { "native", "reorder", "duplicate_seams", "similarity_float32", "reflection" })
                        Check(observations[name].Decision == GuardMeshFingerprintDecision.ResearchMatch, name);
                    Check(observations["duplicate_seams"].UniquePositions == observations["native"].UniquePositions, "duplicates do not bias normalization");
                    Check(observations["unmarked_same_source"].Decision != GuardMeshFingerprintDecision.ResearchMatch, "unmarked base is not build attribution");
                    var sparse = new float[120]; Array.Copy(encoded.Positions, sparse, sparse.Length);
                    Check(observer.Observe(sparse).Decision == GuardMeshFingerprintDecision.InconclusiveCoverage, "sparse candidate abstention");
                    Check(observer.Observe(new float[3]).Decision == GuardMeshFingerprintDecision.InconclusiveCoverage, "degenerate candidate abstention");
                    var nonuniform = (float[])encoded.Positions.Clone(); for (int i = 1; i < nonuniform.Length; i += 3) nonuniform[i] *= 1.02f;
                    observations["nonuniform_scale"] = observer.Observe(nonuniform);
                    Check(observations["nonuniform_scale"].Decision != GuardMeshFingerprintDecision.ResearchMatch, "nonuniform scale weakness documented");
                    var noisy = (float[])encoded.Positions.Clone(); var random = new Random(44);
                    for (int i = 0; i < noisy.Length; i++) noisy[i] += (float)((random.NextDouble() * 2 - 1) * 4e-5);
                    observations["noise_uniform_4e-5"] = observer.Observe(noisy);
                    Check(observations["noise_uniform_4e-5"].Decision != GuardMeshFingerprintDecision.ResearchMatch, "larger noise weakness documented");
                    Check(observations.Values.All(o => !o.Calibrated && o.Confidence == null), "no probability from bit scores");
                    var invalid = (float[])encoded.Positions.Clone(); invalid[0] = float.NaN;
                    Reject(() => observer.Observe(invalid), "nonfinite suspect rejected");
                }
                using (var duplicatePlan = GuardMeshFingerprintV1.Plan(Duplicates(original), context, binding))
                {
                    var duplicateEncoding = duplicatePlan.Encode(Duplicates(original));
                    Check(duplicateEncoding.Positions.Take(original.Length).SequenceEqual(encoded.Positions), "duplicate-safe embedding");
                    Check(duplicateEncoding.PrivateRecord.sourceUniquePositions == original.Length / 3, "unique-count contract");
                    bool equalSeams = true;
                    for (int i = 0; i < 900; i++) for (int j = 0; j < 3; j++)
                        equalSeams &= duplicateEncoding.Positions[original.Length + i * 3 + j] == encoded.Positions[5 * 3 + j];
                    Check(equalSeams, "coincident seams receive identical displacement");
                }
                // Authenticate all persisted policies/data, without requiring the original source to observe a suspect.
                foreach (Action<GuardMeshFingerprintPrivateRecord> tamper in new Action<GuardMeshFingerprintPrivateRecord>[] {
                    r => r.schema++, r => r.algorithmVersion++, r => r.algorithm = "other", r => r.state = "Ready", r => r.buildId = new string('c',32),
                    r => r.bindingId = new string('c',64), r => r.sourcePositionHash = new string('c',64), r => r.markedPositionHash = new string('c',64),
                    r => r.authentication = new string('0',64), r => r.relativeStep /= 2, r => r.maximumObjectDisplacement /= 2,
                    r => r.maximumRelativeDisplacement /= 2, r => r.sourceVertices++, r => r.sourceUniquePositions--, r => r.usableSymbols-- })
                {
                    var changed = Copy(encoded.PrivateRecord); tamper(changed);
                    Reject(() => { using (var forbidden = GuardMeshFingerprintV1.CreateObserver(context, changed)) { } }, "tampered private record rejected");
                }
                using (var different = Context(1))
                    Reject(() => { using (var forbidden = GuardMeshFingerprintV1.CreateObserver(different, encoded.PrivateRecord)) { } }, "foreign build rejected");
                original[0] += 0.01f;
                Reject(() => plan.Encode(original), "mutated source rejected");
                Check(original[0] != originalCopy[0], "caller mutation preserved"); original[0] = originalCopy[0];
                var changedSource = (float[])original.Clone(); changedSource[1] += 0.01f;
                Reject(() => { using (var forbidden = GuardMeshFingerprintV1.RestorePlan(changedSource, context, encoded.PrivateRecord)) { } }, "restore source mismatch");
                var flat = new float[original.Length]; for (int i = 0; i < flat.Length; i += 3) flat[i] = i / (float)flat.Length;
                Reject(() => { using (var forbidden = GuardMeshFingerprintV1.Plan(flat.Take(900).ToArray(), context, binding)) { } }, "source minimum unique gate");
                Reject(() => { using (var tiny = GuardMeshFingerprintV1.Plan(original, context, binding, new GuardMeshFingerprintOptions(maximumObjectDisplacement: 1e-10))) tiny.Encode(original); }, "strict object budget enforced");
                var large = original.Select(v => v * 8).ToArray();
                Reject(() => { using (var oversized = GuardMeshFingerprintV1.Plan(large, context, binding)) oversized.Encode(large); }, "large source budget enforced");
                Reject(() => new GuardMeshFingerprintOptions(relativeStep: 1e-30), "step numeric envelope");
                Reject(() => new GuardMeshFingerprintOptions(maximumRelativeDisplacement: 1e-3), "cannot force a larger budget");
                Reject(() => new GuardMeshFingerprintOptions(maximumObjectDisplacement: double.NaN), "invalid budget");
                var bad = (float[])original.Clone(); bad[0] = float.PositiveInfinity;
                Reject(() => { using (var forbidden = GuardMeshFingerprintV1.Plan(bad, context, binding)) { } }, "nonfinite authoring rejected");
                Reject(() => { using (var forbidden = GuardMeshFingerprintV1.Plan(new float[4], context, binding)) { } }, "malformed XYZ rejected");
                Reject(() => { using (var forbidden = GuardMeshFingerprintV1.Plan(original, context, "bad")) { } }, "invalid binding rejected");
                var narrow = (float[])original.Clone();
                for (int i = 0; i < narrow.Length; i += 3) { double radius = Math.Sqrt(narrow[i]*narrow[i]+narrow[i+1]*narrow[i+1]+narrow[i+2]*narrow[i+2]); for (int j = 0; j < 3; j++) narrow[i+j] = (float)(narrow[i+j]/radius); }
                Reject(() => { using (var forbidden = GuardMeshFingerprintV1.Plan(narrow, context, binding)) { } }, "insufficient bands rejected without marking");
                Binary(Path.Combine(outputFolder, "source.xyz32.bin"), original);
                Binary(Path.Combine(outputFolder, "marked.xyz32.bin"), encoded.Positions);
                maximumDisplacement = encoded.MaximumDisplacement; maximumRelative = encoded.MaximumRelativeDisplacement;
            }
            for (int index = 0; index < 32; index++)
            using (var context = Context(index))
            using (var plan = GuardMeshFingerprintV1.Plan(original, context, binding))
            {
                var encoded = plan.Encode(original);
                Check(encoded.SelfObservation.UsableSymbols == 128 && encoded.SelfObservation.MatchedSymbols == 128, "diverse Float32 self check");
                Check(outputHashes.Add(encoded.PrivateRecord.markedPositionHash), "build diversity");
                maximumDisplacement = Math.Max(maximumDisplacement, encoded.MaximumDisplacement); maximumRelative = Math.Max(maximumRelative, encoded.MaximumRelativeDisplacement);
            }
            var deadContext = Context(44); var deadPlan = GuardMeshFingerprintV1.Plan(original, deadContext, binding); deadPlan.Dispose(); deadPlan.Dispose();
            bool disposed = false; try { deadPlan.Encode(original); } catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, "disposed plan rejects encoding"); deadContext.Dispose();
            Check(GuardMeshFingerprintV1.PositionHash(original) == originalHash, "final fixture source preserved");
            string observationJson = string.Join(",", observations.Select(p => "\"" + p.Key + "\":" + Json(p.Value)));
            string report = "{\"schema\":1,\"checks\":" + checks + ",\"diverseBuilds\":32,\"sourceVertices\":" + original.Length / 3 +
                ",\"sourcePositionHash\":\"" + originalHash + "\",\"markedPositionHash\":\"" + GuardMeshFingerprintV1.PositionHash(ReadBinary(Path.Combine(outputFolder, "marked.xyz32.bin"))) +
                "\",\"sourcePreserved\":true,\"gpuUsed\":false,\"unityStarted\":false,\"maximumObjectDisplacement\":" + maximumDisplacement.ToString("R",CultureInfo.InvariantCulture) +
                ",\"maximumRelativeDisplacement\":" + maximumRelative.ToString("R",CultureInfo.InvariantCulture) + ",\"observations\":{" + observationJson + "}}";
            File.WriteAllText(Path.Combine(outputFolder, "results.json"), report + "\n");
            Console.WriteLine("MeshFingerprint CPU: " + checks + " checks passed, 32 deterministic fixture builds. No graphics API.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static float[] ReadBinary(string path)
    {
        using (var reader = new BinaryReader(File.OpenRead(path)))
        { var result = new float[reader.BaseStream.Length / 4]; for (int i = 0; i < result.Length; i++) result[i] = reader.ReadSingle(); return result; }
    }
}
