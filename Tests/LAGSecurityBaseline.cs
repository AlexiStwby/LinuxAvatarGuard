// SPDX-License-Identifier: MIT
// Controlled legacy-codec audit on synthetic meshes only. No avatars or private user keys.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using LinuxAvatarGuard;
using Object = UnityEngine.Object;

public static class LAGSecurityBaseline
{
    static readonly List<string> checks = new List<string>();
    static void Check(bool value, string name)
    {
        if (!value) throw new Exception("SECURITY_BASELINE_FAILED: " + name);
        checks.Add(name);
    }
    static Mesh Fixture(float scale)
    {
        const int edge = 6;
        var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var triangles = new List<int>();
        for (int y = 0; y < edge; y++) for (int x = 0; x < edge; x++)
        {
            vertices.Add(new Vector3(x * scale / edge, y * scale / edge, 0));
            uv.Add(new Vector2(x / (float)(edge - 1), y / (float)(edge - 1)));
            if (x < edge - 1 && y < edge - 1)
            {
                int i = y * edge + x;
                triangles.AddRange(new[] { i, i + edge, i + 1, i + 1, i + edge, i + edge + 1 });
            }
        }
        var mesh = new Mesh { name = "SyntheticAuditPlane", vertices = vertices.ToArray(), triangles = triangles.ToArray() };
        mesh.normals = Enumerable.Repeat(Vector3.forward, vertices.Count).ToArray();
        mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), vertices.Count).ToArray();
        mesh.colors = Enumerable.Repeat(Color.white, vertices.Count).ToArray();
        mesh.SetUVs(0, uv); mesh.SetUVs(1, uv);
        mesh.bindposes = new[] { Matrix4x4.identity };
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, vertices.Count).ToArray();
        mesh.AddBlendShapeFrame("PositionOnly", 100, Enumerable.Repeat(Vector3.up * .01f, vertices.Count).ToArray(), new Vector3[vertices.Count], new Vector3[vertices.Count]);
        mesh.RecalculateBounds();
        return mesh;
    }
    // Independent implementation of the public decoder; deliberately does not call GuardMesh.Offset.
    static Vector3[] ObservedParameterDecoder(Mesh encoded, int[] observed)
    {
        var a = new List<Vector2>(); var b = new List<Vector2>();
        encoded.GetUVs(6, a); encoded.GetUVs(7, b);
        var positions = encoded.vertices; var normals = encoded.normals;
        for (int i = 0; i < positions.Length; i++)
        {
            float displacement = (a[i].x * observed[0] + a[i].y * observed[1] + b[i].x * observed[2] + b[i].y * observed[3]) / 255f;
            positions[i] -= normals[i] * displacement;
        }
        return positions;
    }
    static float Rms(Vector3[] a, Vector3[] b) => Mathf.Sqrt(a.Zip(b, (x, y) => (x - y).sqrMagnitude).Sum() / a.Length);
    static int[] KnownPlaintextRecovery(Mesh original, Mesh encoded)
    {
        var a = new List<Vector2>(); var b = new List<Vector2>();
        encoded.GetUVs(6, a); encoded.GetUVs(7, b);
        var plain = original.vertices; var protectedPositions = encoded.vertices; var normals = encoded.normals;
        var augmented = new double[4, 5];
        for (int i = 0; i < plain.Length; i++)
        {
            var row = new double[] { a[i].x, a[i].y, b[i].x, b[i].y };
            double target = Vector3.Dot(protectedPositions[i] - plain[i], normals[i]) * 255;
            for (int x = 0; x < 4; x++)
            {
                for (int y = 0; y < 4; y++) augmented[x, y] += row[x] * row[y];
                augmented[x, 4] += row[x] * target;
            }
        }
        for (int p = 0; p < 4; p++)
        {
            int pivot = p;
            for (int r = p + 1; r < 4; r++) if (Math.Abs(augmented[r, p]) > Math.Abs(augmented[pivot, p])) pivot = r;
            if (Math.Abs(augmented[pivot, p]) < 1e-12) throw new Exception("Degenerate synthetic coefficient matrix");
            for (int c = p; c < 5; c++) { double t = augmented[p, c]; augmented[p, c] = augmented[pivot, c]; augmented[pivot, c] = t; }
            double divisor = augmented[p, p];
            for (int c = p; c < 5; c++) augmented[p, c] /= divisor;
            for (int r = 0; r < 4; r++) if (r != p)
            {
                double factor = augmented[r, p];
                for (int c = p; c < 5; c++) augmented[r, c] -= factor * augmented[p, c];
            }
        }
        return Enumerable.Range(0, 4).Select(i => (int)Math.Round(augmented[i, 4])).ToArray();
    }
    static string PayloadHash(Mesh mesh)
    {
        var a = new List<Vector2>(); var b = new List<Vector2>(); mesh.GetUVs(6, a); mesh.GetUVs(7, b);
        using (var bytes = new MemoryStream())
        {
            using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
                for (int i = 0; i < a.Count; i++) { writer.Write(a[i].x); writer.Write(a[i].y); writer.Write(b[i].x); writer.Write(b[i].y); }
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(bytes.ToArray()));
        }
    }
    public static void Run()
    {
        checks.Clear();
        var objects = new List<Mesh>();
        try
        {
            var source = Fixture(1); var other = Fixture(2); objects.Add(source); objects.Add(other);
            var before = source.vertices; var key = new[] { 83, 127, 191, 239 };
            var protectedMesh = GuardMesh.Encode(source, key, .15f); objects.Add(protectedMesh);
            float decodedError = Rms(before, ObservedParameterDecoder(protectedMesh, key));
            float missingError = Rms(before, ObservedParameterDecoder(protectedMesh, new[] { 0, 0, 0, 0 }));
            float wrongError = Rms(before, ObservedParameterDecoder(protectedMesh, new[] { 240, 38, 77, 61 }));
            Check(source.vertices.SequenceEqual(before), "encoding preserves the synthetic source");
            Check(protectedMesh != source && protectedMesh.triangles.SequenceEqual(source.triangles), "independent output retains topology");
            Check(protectedMesh.normals.SequenceEqual(source.normals) && protectedMesh.tangents.SequenceEqual(source.tangents), "normals and tangents preserved");
            Check(protectedMesh.uv.SequenceEqual(source.uv) && protectedMesh.uv2.SequenceEqual(source.uv2) && protectedMesh.colors.SequenceEqual(source.colors), "existing UVs and colors preserved");
            Check(protectedMesh.bindposes.SequenceEqual(source.bindposes) && protectedMesh.boneWeights.SequenceEqual(source.boneWeights), "bindposes and weights preserved");
            Check(protectedMesh.blendShapeCount == source.blendShapeCount && protectedMesh.GetBlendShapeName(0) == source.GetBlendShapeName(0), "blendshape count and names preserved");
            Check(protectedMesh.bounds == source.bounds, "source bounds preserved");
            Check(decodedError < 1e-6, "KNOWN WEAKNESS: an independent decoder reconstructs with observable parameters");
            Check(missingError > .01f && wrongError > .01f, "zero and distant wrong keys fail on the synthetic fixture");
            var encodedOther = GuardMesh.Encode(other, key, .15f); objects.Add(encodedOther);
            Check(Rms(other.vertices, ObservedParameterDecoder(encodedOther, key)) < 1e-6, "KNOWN WEAKNESS: the same formula and runtime key reconstruct another mesh");
            var recovered = KnownPlaintextRecovery(source, protectedMesh);
            Check(recovered.SequenceEqual(key) && Rms(before, ObservedParameterDecoder(protectedMesh, recovered)) < 1e-6, "KNOWN WEAKNESS: matching plaintext and encoded vertices recover the four runtime bytes");
            var hashes = new HashSet<string>(); float worstError = 0;
            for (int i = 0; i < 100; i++)
            {
                var randomKey = GuardMesh.NewKey(); var encoded = GuardMesh.Encode(source, randomKey, .15f);
                try { hashes.Add(PayloadHash(encoded)); worstError = Mathf.Max(worstError, Rms(before, ObservedParameterDecoder(encoded, randomKey))); }
                finally { Object.DestroyImmediate(encoded); }
            }
            Check(hashes.Count == 100 && worstError < 1e-6, "KNOWN WEAKNESS: 100 unique payloads are all reconstructed by one unchanged decoder");
            var sameKeyAgain = GuardMesh.Encode(source, key, .15f); objects.Add(sameKeyAgain);
            Check(PayloadHash(protectedMesh) != PayloadHash(sameKeyAgain), "same mesh and key are not reproducible without a seed");
            float tangentLeak = Rms(before.Select(p => new Vector3(p.x, p.y, 0)).ToArray(), protectedMesh.vertices.Select(p => new Vector3(p.x, p.y, 0)).ToArray());
            Check(tangentLeak < 1e-6, "KNOWN WEAKNESS: displacement along normals retains tangential geometry");
            Directory.CreateDirectory("../evidence");
            File.WriteAllText("../evidence/security-baseline.json", JsonUtility.ToJson(new Report { version = GuardText.Version, checks = checks.ToArray(), decodedRms = decodedError, missingRms = missingError, wrongRms = wrongError, independentDecoderCount = 1, sampledBuilds = 100, uniquePayloads = hashes.Count, worstDecodedRms = worstError, knownPlaintextRecovery = true }, true));
            Debug.Log("LAG_SECURITY_BASELINE_SUCCESS " + checks.Count + " checks; known weaknesses reproduced");
        }
        finally { foreach (var mesh in objects) if (mesh) Object.DestroyImmediate(mesh); }
    }
#if LAG_VRCSDK
    public static void RunAudit()
    {
        // This entry point is for a disposable validation project, never the user's working scene.
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
        LAGValidation.Run();
        Run();
    }
#endif
    [Serializable] sealed class Report
    {
        public string version; public string[] checks;
        public float decodedRms, missingRms, wrongRms, worstDecodedRms;
        public int independentDecoderCount, sampledBuilds, uniquePayloads;
        public bool knownPlaintextRecovery;
    }
}
