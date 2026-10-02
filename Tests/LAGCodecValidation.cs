// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using LinuxAvatarGuard;
using Object = UnityEngine.Object;

public static class LAGCodecValidation
{
    static readonly List<string> checks = new List<string>();
    static void Check(bool value, string description)
    {
        if (!value) throw new Exception("CODEC_CONTRACT_FAILED: " + description);
        checks.Add(description);
    }
    static bool Rejects(Action action)
    { try { action(); return false; } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } }
    public static void Run()
    {
        checks.Clear();
        var primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var source = Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
        Object.DestroyImmediate(primitive);
        var other = Object.Instantiate(source);
        var created = new List<Mesh> { source, other };
        try
        {
            var vertices = source.vertices; var normals = source.normals;
            var topology = source.triangles; var uv = source.uv; var tangents = source.tangents;
            source.AddBlendShapeFrame("PositionOnly", 100, vertices.Select(v => v * .01f).ToArray(), new Vector3[vertices.Length], new Vector3[vertices.Length]);
            IMeshCodec codec = MeshCodecs.Legacy;
            var plan = codec.Plan(source, .15f);
            codec.Validate(source, plan);
            Check(plan.Program.CodecId == "legacy-linear" && plan.Program.CodecVersion == 1 && plan.Program.SchemaVersion == 1,
                "legacy plan identifies its codec and schema separately");
            Check(plan.Program.Operations.SequenceEqual(new[] { CodecOperationKind.LegacyNormalOffset }) &&
                plan.Program.Attributes.FirstUvChannel == 6 && plan.Program.Attributes.SecondUvChannel == 7 &&
                plan.Program.Attributes.ComponentsPerChannel == 2 && plan.Program.Attributes.RequiresUnitNormals,
                "typed legacy program declares unchanged normal and UV transport");
            var decoder = codec.EmitDecoder(plan);
            var goldenPath = AssetDatabase.FindAssets("legacy-decoder-v1 t:TextAsset").Select(AssetDatabase.GUIDToAssetPath)
                .Single(p => p.EndsWith("/legacy-decoder-v1.txt", StringComparison.Ordinal));
            Check(decoder.Injection == File.ReadAllText(goldenPath) && GuardShaders.Injection == decoder.Injection,
                "decoder exactly matches the pre-refactor golden fixture");
            Check(decoder.RuntimeProperties.SequenceEqual(new[] { "_LAGKey0", "_LAGKey1", "_LAGKey2", "_LAGKey3" }),
                "all four legacy material property names stay compatible");
            float worstRoundTrip = 0, worstFormula = 0;
            foreach (var value in new[] { 0, 1, 127, 128, 254, 255 })
            {
                var key = new[] { value, value, value, value };
                var encoded = codec.Encode(source, plan, key); created.Add(encoded);
                var positions = encoded.vertices;
                var a = new List<Vector2>(); var b = new List<Vector2>(); encoded.GetUVs(6, a); encoded.GetUVs(7, b);
                for (int i = 0; i < positions.Length; i++)
                {
                    // Independent reference to the old protocol, including byte-boundary runtime inputs.
                    float offset = (a[i].x * value + a[i].y * value + b[i].x * value + b[i].y * value) / 255f;
                    worstFormula = Mathf.Max(worstFormula, (positions[i] - (vertices[i] + normals[i] * offset)).magnitude);
                    worstRoundTrip = Mathf.Max(worstRoundTrip, (positions[i] - normals[i] * offset - vertices[i]).magnitude);
                }
                Check(encoded != source && encoded.triangles.SequenceEqual(topology) && encoded.normals.SequenceEqual(normals) &&
                    encoded.uv.SequenceEqual(uv) && encoded.tangents.SequenceEqual(tangents) && encoded.bounds == source.bounds &&
                    encoded.blendShapeCount == 1 && encoded.GetBlendShapeName(0) == "PositionOnly",
                    "legacy preserves source attributes at runtime byte boundary " + value);
            }
            Check(worstFormula < 1e-7f && worstRoundTrip < 1e-6f, "adapter output follows the legacy protocol with bounded float error");
            Check(Rejects(() => codec.Encode(other, plan, new[] { 1, 2, 3, 4 })), "a source plan cannot be applied to another mesh");
            Check(Rejects(() => codec.EmitDecoder(null)), "missing decoder plan is rejected");
            Check(Rejects(() => codec.Plan(source, float.NaN)) && Rejects(() => codec.Plan(source, .51f)) && Rejects(() => codec.Plan(source, 0)),
                "invalid codec strengths fail before output allocation");
            Check(Rejects(() => codec.Encode(source, plan, new[] { -1, 0, 0, 0 })) && Rejects(() => codec.Encode(source, plan, new[] { 256, 0, 0, 0 })),
                "out of range runtime values remain rejected");
            other.SetUVs(6, Enumerable.Repeat(Vector2.one, other.vertexCount).ToList());
            Check(Rejects(() => codec.Validate(other, codec.Plan(other, .1f))), "validation reserves occupied channels without erasing them");
            Check(other.uv7.Length == other.vertexCount && other.uv7.All(v => v == Vector2.one), "validation leaves conflicting UV data untouched");
            Check(source.vertices.SequenceEqual(vertices) && source.normals.SequenceEqual(normals) && source.triangles.SequenceEqual(topology),
                "plans and encoding never mutate source geometry");
            Directory.CreateDirectory("../evidence");
            File.WriteAllText("../evidence/codec-validation.json", JsonUtility.ToJson(new Report {
                checks = checks.ToArray(), worstRoundTrip = worstRoundTrip, worstFormula = worstFormula, runtimeBoundaryCases = 6, goldenDecoderUnchanged = true }, true));
            Debug.Log("LAG_CODEC_SUCCESS " + checks.Count + " checks");
        }
        finally { foreach (var mesh in created) if (mesh) Object.DestroyImmediate(mesh); }
    }
    [Serializable] sealed class Report
    { public string[] checks; public float worstRoundTrip, worstFormula; public int runtimeBoundaryCases; public bool goldenDecoderUnchanged; }
}
