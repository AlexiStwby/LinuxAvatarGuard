// SPDX-License-Identifier: MIT
// Run only in a disposable project. All geometry, textures and runtime values are owned fixtures.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using LinuxAvatarGuard;
using Object = UnityEngine.Object;

public static class LAGStaticPrototypeValidation
{
    static readonly List<string> checks = new List<string>();
    static readonly int[] key = { 83, 127, 191, 239 }; // Public fixture values.
    static void Check(bool value, string description)
    { if (!value) throw new Exception("STATIC_PROTOTYPE_FAILED: " + description); checks.Add(description); }
    static bool Rejects(Action action)
    { try { action(); return false; } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } }
    static byte[] Seed(int value) => Enumerable.Range(0, 32).Select(i => (byte)(i + value * 7)).ToArray();
    static string Hash(string path)
    { using (var h = SHA256.Create()) return Convert.ToBase64String(h.ComputeHash(File.ReadAllBytes(path))); }
    // Independent double-precision interpreter: never calls CodecInstruction.Apply or a CPU mesh decoder in the package.
    static Vector3[] Decode(Mesh mesh, CodecPlan plan, int[] runtime)
    {
        var first = new List<Vector2>(); var second = new List<Vector2>(); mesh.GetUVs(6, first); mesh.GetUVs(7, second);
        var result = mesh.vertices;
        for (int i = 0; i < result.Length; i++)
        {
            var p = new[] { (double)result[i].x, result[i].y, result[i].z };
            var c = new[] { (double)first[i].x, first[i].y, second[i].x, second[i].y };
            foreach (var instruction in plan.Program.Instructions.Reverse())
            {
                int a = instruction.Axis, b = instruction.OtherAxis;
                switch (instruction.Kind)
                {
                    case CodecOperationKind.AxisSwap: double swap = p[a]; p[a] = p[b]; p[b] = swap; break;
                    case CodecOperationKind.AxisFlip: p[a] = -p[a]; break;
                    case CodecOperationKind.TriangularShear: p[a] -= instruction.Factor * p[b]; break;
                    case CodecOperationKind.BoundedBend: p[a] -= instruction.Factor * p[b] / (1 + Math.Abs(p[b])); break;
                    case CodecOperationKind.KeyedVectorOffset:
                        var rows = new[] { instruction.RowX, instruction.RowY, instruction.RowZ };
                        for (int axis = 0; axis < 3; axis++) for (int j = 0; j < 4; j++)
                            p[axis] -= rows[axis][j] * c[j] * runtime[j] / 255.0;
                        break;
                    default: throw new Exception("Unknown public IR opcode in independent interpreter");
                }
            }
            result[i] = new Vector3((float)p[0], (float)p[1], (float)p[2]);
        }
        return result;
    }
    static float MaxError(Vector3[] a, Vector3[] b) => a.Zip(b, (x, y) => (x - y).magnitude).Max();
    static float Rms(Vector3[] a, Vector3[] b) => Mathf.Sqrt(a.Zip(b, (x, y) => (x - y).sqrMagnitude).Average());
    static bool Same(Mesh a, Mesh b) => a.vertices.SequenceEqual(b.vertices) && a.uv7.SequenceEqual(b.uv7) && a.uv8.SequenceEqual(b.uv8);
    static Mesh Fixture()
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var mesh = Object.Instantiate(sphere.GetComponent<MeshFilter>().sharedMesh); Object.DestroyImmediate(sphere);
        mesh.name = "OwnedAsymmetricStaticFixture";
        mesh.vertices = mesh.vertices.Select(p => new Vector3(p.x * (1.2f + .3f * p.y), p.y * 1.3f, p.z * .75f + .1f * p.x * p.x)).ToArray();
        mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
        mesh.colors = mesh.vertices.Select(p => new Color(.5f + p.x * .3f, .6f, .7f, 1)).ToArray();
        mesh.SetUVs(5, mesh.vertices.Select(p => new Vector4(p.x, p.y, p.z, 1)).ToList());
        return mesh;
    }
    static void SetKey(Renderer renderer, int[] values)
    { var block = new MaterialPropertyBlock(); for (int i = 0; i < 4; i++) block.SetFloat(GuardShaders.Property(i), values[i]); renderer.SetPropertyBlock(block); }
    static Color[] Render(Camera camera, string path)
    {
        var rt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
        var image = new Texture2D(256, 256, TextureFormat.RGBA32, false);
        var previous = RenderTexture.active;
        try
        {
            camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
            image.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); image.Apply();
            File.WriteAllBytes(path, image.EncodeToPNG()); return image.GetPixels();
        }
        finally { RenderTexture.active = previous; camera.targetTexture = null; Object.DestroyImmediate(image); Object.DestroyImmediate(rt); }
    }
    static float Difference(Color[] a, Color[] b) => a.Zip(b, (x, y) => Mathf.Abs(x.r-y.r)+Mathf.Abs(x.g-y.g)+Mathf.Abs(x.b-y.b)).Average()/3;
    public static void Run()
    {
        if (Application.platform != RuntimePlatform.LinuxEditor || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan || GraphicsSettings.currentRenderPipeline != null)
            throw new InvalidOperationException("Static prototype validation requires Linux Unity, Vulkan and Built-in Render Pipeline.");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        checks.Clear();
        var allocated = new List<Object>();
        Mesh source = Fixture(); allocated.Add(source);
        Mesh other = Object.Instantiate(source); allocated.Add(other);
        var original = source.vertices;
        float worstRoundTrip = 0, crossProgramRms = 0, legacyDecoderRms = 0, worstVisual = 0, missingVisual = 0, wrongVisual = 0;
        var publicHashes = new HashSet<string>(); var canonicalDecoderHashes = new HashSet<string>();
        var fixedSeed = Seed(0);
        try
        {
            using (var codec = new StaticPolymorphicCodecV1(fixedSeed))
            using (var second = new StaticPolymorphicCodecV1(Seed(1)))
            {
                var plan = codec.Plan(source, .15f); var otherPlan = second.Plan(source, .15f);
                Check(plan.Program.SchemaVersion == 2 && plan.Program.CodecId == StaticPolymorphicCodecV1.Id && plan.Program.Instructions.Count == 8,
                    "prototype has its own codec/version and typed IR schema");
                Check(plan.Program.Instructions.Count(i => i.Kind == CodecOperationKind.BoundedBend) == 2 &&
                      plan.Program.Instructions.Count(i => i.Kind == CodecOperationKind.KeyedVectorOffset) == 2 &&
                      !plan.Program.Attributes.RequiresUnitNormals, "program contains bounded nonlinear operations and three-axis keyed offsets");
                Check(Rejects(() => CodecInstruction.Swap(1,1)) && Rejects(() => CodecInstruction.Flip(3)) &&
                      Rejects(() => CodecInstruction.Bend(0,0,.1f)) && Rejects(() => CodecInstruction.Shear(0,1,float.NaN)) &&
                      Rejects(() => CodecInstruction.Shear(0,1,.3f)) && Rejects(() => CodecInstruction.Offset(Vector4.one,Vector4.one,Vector4.one)),
                      "IR rejects degenerate axes, nonfinite factors, excessive shear and rank-deficient offset rows");
                var encoded = codec.Encode(source, plan, key); allocated.Add(encoded);
                var repeated = codec.Encode(source, plan, key); allocated.Add(repeated);
                Check(Same(encoded,repeated) && StaticPolymorphicCodecV1.ProgramHash(plan) == StaticPolymorphicCodecV1.ProgramHash(codec.Plan(source,.15f)),
                    "fixed seed reproduces the exact program, positions and payload");
                using (var sameSeed = new StaticPolymorphicCodecV1(Seed(0)))
                {
                    var alternate = sameSeed.Encode(source, sameSeed.Plan(source,.15f),key); allocated.Add(alternate);
                    Check(Same(encoded,alternate), "determinism survives a new codec instance");
                }
                fixedSeed[0] ^= 0xff;
                var afterMutation = codec.Encode(source,plan,key); allocated.Add(afterMutation);
                Check(Same(encoded,afterMutation), "codec copies the caller seed and never depends on later mutations");
                Check(encoded.triangles.SequenceEqual(source.triangles) && encoded.normals.SequenceEqual(source.normals) &&
                      encoded.tangents.SequenceEqual(source.tangents) && encoded.colors.SequenceEqual(source.colors) &&
                      encoded.uv.SequenceEqual(source.uv) && encoded.bounds == source.bounds,
                      "encoding preserves topology, shading attributes, colors, UV0 and original bounds");
                var extraSource = new List<Vector4>(); var extraEncoded = new List<Vector4>(); source.GetUVs(5,extraSource); encoded.GetUVs(5,extraEncoded);
                Check(extraSource.SequenceEqual(extraEncoded), "four-component existing high UV data remains intact");
                Check(MaxError(Decode(encoded,plan,key),original) < 1e-5f, "independent double interpreter reconstructs the fixture within 10 micrometres");
                Check(Rms(Decode(encoded,plan,new[]{0,0,0,0}),original) > .02f && Rms(Decode(encoded,plan,new[]{1,2,3,4}),original) > .02f,
                    "absent and incorrect runtime values fail to reconstruct the fixture");
                crossProgramRms = Rms(Decode(encoded,otherPlan,key),original);
                Check(crossProgramRms > .02f, "a different seed's program fails to reconstruct this encoded fixture");
                var a = encoded.uv7; var b = encoded.uv8;
                var legacy = encoded.vertices.Select((p,i) => p-source.normals[i]*((a[i].x*key[0]+a[i].y*key[1]+b[i].x*key[2]+b[i].y*key[3])/255f)).ToArray();
                legacyDecoderRms = Rms(legacy,original);
                Check(legacyDecoderRms > .02f, "legacy normal-offset formula fails on the prototype with the same runtime values");
                for (int i = 0; i < 16; i++) using (var sample = new StaticPolymorphicCodecV1(Seed(i)))
                {
                    var samplePlan = sample.Plan(source,.15f); var mesh = sample.Encode(source,samplePlan,key);
                    try
                    {
                        worstRoundTrip = Mathf.Max(worstRoundTrip,MaxError(Decode(mesh,samplePlan,key),original));
                        publicHashes.Add(StaticPolymorphicCodecV1.ProgramHash(samplePlan));
                        using (var sha = SHA256.Create()) canonicalDecoderHashes.Add(Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sample.EmitDecoder(samplePlan).Injection))));
                    }
                    finally { Object.DestroyImmediate(mesh); }
                }
                Check(worstRoundTrip < 1e-5f && publicHashes.Count == 16 && canonicalDecoderHashes.Count == 16,
                    "16 controlled seeds produce distinct IR and decoder bodies with bounded independent round-trip error");
                foreach (float scale in new[] { .001f, .05f, 20f })
                {
                    var scaled = Object.Instantiate(source);
                    try
                    {
                        scaled.vertices = scaled.vertices.Select(p=>p*scale).ToArray(); scaled.RecalculateBounds();
                        var scaledPlan = codec.Plan(scaled,.15f); var scaledEncoded = codec.Encode(scaled,scaledPlan,key);
                        try
                        {
                            float error = MaxError(Decode(scaledEncoded,scaledPlan,key),scaled.vertices);
                            worstRoundTrip = Mathf.Max(worstRoundTrip,error);
                            Check(error < 1e-5f, "absolute round-trip budget holds for fixture scale " + scale.ToString(CultureInfo.InvariantCulture));
                        }
                        finally { Object.DestroyImmediate(scaledEncoded); }
                    }
                    finally { Object.DestroyImmediate(scaled); }
                }
                foreach (int boundary in new[] { 1,127,128,254,255 })
                {
                    var values = new[] {boundary,boundary,boundary,boundary}; var mesh = codec.Encode(source,plan,values);
                    try { Check(MaxError(Decode(mesh,plan,values),original)<1e-5f, "runtime byte boundary reconstructs correctly: "+boundary); }
                    finally { Object.DestroyImmediate(mesh); }
                }
                Check(Rejects(() => codec.Encode(source,plan,new[]{0,0,0,0})) && Rejects(() => codec.Encode(source,plan,new[]{-1,2,3,4})) &&
                      Rejects(() => codec.Encode(source,plan,new[]{256,2,3,4})), "invalid runtime configuration and an all-zero unlock configuration are rejected");
                Check(Rejects(() => codec.Encode(other,plan,key)) && Rejects(() => second.Encode(source,plan,key)), "plans cannot cross source meshes or codec owners");
                other.vertices = other.vertices.Select(p => p*1.01f).ToArray(); var changedPlan = codec.Plan(other,.1f);
                var changed = other.vertices; changed[0] += Vector3.one*.001f; other.vertices = changed;
                Check(Rejects(() => codec.Encode(other,changedPlan,key)) && Rejects(() => codec.EmitDecoder(changedPlan)), "source mutations invalidate both encoding and shader emission");
                other.SetUVs(6,Enumerable.Repeat(Vector2.one,other.vertexCount).ToList());
                Check(Rejects(() => codec.Plan(other,.1f)) && other.uv7.All(v => v==Vector2.one), "occupied UV carriers are rejected and preserved");
                other.SetUVs(6,new List<Vector2>()); other.bindposes = new[]{Matrix4x4.identity};
                Check(Rejects(() => codec.Plan(other,.1f)), "bindpose data is rejected before prototype encoding");
                other.bindposes = Array.Empty<Matrix4x4>(); other.boneWeights = Enumerable.Repeat(new BoneWeight{weight0=1},other.vertexCount).ToArray();
                Check(Rejects(() => codec.Plan(other,.1f)), "bone weights are rejected even without bindposes");
                var shape = Object.Instantiate(source); allocated.Add(shape);
                shape.AddBlendShapeFrame("OwnedTest",100,new Vector3[shape.vertexCount],new Vector3[shape.vertexCount],new Vector3[shape.vertexCount]);
                Check(Rejects(() => codec.Plan(shape,.1f)), "all blendshapes remain outside the static prototype contract");
                var skin = new GameObject("OwnedSkinnedRejection"); allocated.Add(skin);
                Check(Rejects(() => StaticPolymorphicCodecV1.RequireStaticRenderer(skin.AddComponent<SkinnedMeshRenderer>())), "SkinnedMeshRenderer is rejected explicitly");
                var invalid = Object.Instantiate(source); allocated.Add(invalid); var invalidVertices = invalid.vertices; invalidVertices[0].x = float.NaN; invalid.vertices = invalidVertices;
                Check(Rejects(() => codec.Plan(invalid,.1f)), "nonfinite positions are rejected");
                invalidVertices[0].x = 17; invalid.vertices = invalidVertices;
                Check(Rejects(() => codec.Plan(invalid,.1f)), "positions outside the declared 16 metre component range are rejected");
                Check(Rejects(() => new StaticPolymorphicCodecV1(new byte[31])) && Rejects(() => codec.Plan(source,0)) && Rejects(() => codec.Plan(source,float.NaN)),
                    "invalid seeds and strengths fail before encoding");
                var retired = new StaticPolymorphicCodecV1(Seed(0)); var retiredPlan = retired.Plan(source,.1f); retired.Dispose();
                Check(Rejects(()=>retired.Plan(source,.1f)) && Rejects(()=>retired.Encode(source,retiredPlan,key)), "disposed seed owners cannot create or execute plans");
                var savedCulture = CultureInfo.CurrentCulture;
                try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES"); Check(
                        codec.EmitDecoder(plan).Injection == DecoderForCulture(codec,plan,"en-US"), "HLSL numeric emission is independent of decimal locale"); }
                finally { CultureInfo.CurrentCulture = savedCulture; }
                Check(source.vertices.SequenceEqual(original) && source.uv7.Length==0 && source.uv8.Length==0, "all experiments preserve the source mesh");
                using (var random = StaticPolymorphicCodecV1.CreateRandom())
                    Check(random.Plan(source,.1f).Program.Instructions.Count==8, "explicit random prototype creation uses a private local seed");
                string folder = "Assets/StaticCodecPrototype";
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                AssetDatabase.CreateFolder("Assets","StaticCodecPrototype"); AssetDatabase.CreateFolder(folder,"Shaders");
                AssetDatabase.CreateAsset(source,folder+"/original.asset");
                var texture = new Texture2D(16,16,TextureFormat.RGBA32,false);
                texture.SetPixels(Enumerable.Range(0,256).Select(i => (i/16/4+i%16/4)%2==0 ? new Color(.2f,.7f,.9f) : new Color(.9f,.3f,.2f)).ToArray()); texture.Apply();
                AssetDatabase.CreateAsset(texture,folder+"/owned-texture.asset");
                var originalMaterial = new Material(Shader.Find("lilToon")); originalMaterial.SetFloat("_AsUnlit",1); originalMaterial.SetTexture("_MainTex",texture);
                AssetDatabase.CreateAsset(originalMaterial,folder+"/original.mat");
                var generator = new GuardShaders(folder+"/Shaders",Guid.NewGuid().ToString("N"),codec.EmitDecoder(plan));
                var shader = generator.Copy(Shader.Find("lilToon"));
                // Representative additional UsePass/outline/alpha paths; full feature matrix is a later stage.
                foreach (var name in new[]{"Hidden/lilToonCutoutOutline","Hidden/lilToonTransparent"})
                {
                    var variant = new Material(generator.Copy(Shader.Find(name)));
                    try { for(int pass=0;pass<variant.passCount;pass++) ShaderUtil.CompilePass(variant,pass,true); }
                    finally { Object.DestroyImmediate(variant); }
                }
                var protectedMaterial = new Material(originalMaterial); protectedMaterial.shader = shader;
                for(int pass=0;pass<protectedMaterial.passCount;pass++) ShaderUtil.CompilePass(protectedMaterial,pass,true);
                AssetDatabase.CreateAsset(protectedMaterial,folder+"/protected.mat"); AssetDatabase.CreateAsset(encoded,folder+"/protected.asset");
                var obj = new GameObject("OwnedStaticPrototype"); allocated.Add(obj);
                var filter = obj.AddComponent<MeshFilter>(); var renderer = obj.AddComponent<MeshRenderer>();
                filter.sharedMesh = encoded; renderer.sharedMaterial = protectedMaterial;
                Check(StaticPolymorphicCodecV1.RequireStaticRenderer(renderer)==encoded, "research renderer gate accepts MeshRenderer with its own mesh");
                PrefabUtility.SaveAsPrefabAsset(obj,folder+"/owned-prototype.prefab");
                AssetDatabase.SaveAssets();
                var sourceHash = Hash(folder+"/original.asset"); var materialHash = Hash(folder+"/original.mat"); var textureHash = Hash(folder+"/owned-texture.asset");
                var cameraObject = new GameObject("OwnedStaticCamera"); allocated.Add(cameraObject);
                var camera = cameraObject.AddComponent<Camera>(); camera.enabled=false; camera.orthographic=true; camera.orthographicSize=.9f;
                camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.04f,.05f,.07f);
                string output = "../evidence/static-prototype"; Directory.CreateDirectory(output);
                for (int view=0;view<3;view++)
                {
                    camera.transform.position=Quaternion.Euler(0,view*120,0)*new Vector3(0,0,-3); camera.transform.LookAt(Vector3.zero);
                    renderer.SetPropertyBlock(null); filter.sharedMesh=source; renderer.sharedMaterial=originalMaterial;
                    var reference=Render(camera,output+"/view-"+view+"-original.png");
                    filter.sharedMesh=encoded; renderer.sharedMaterial=protectedMaterial; SetKey(renderer,key);
                    var unlocked=Render(camera,output+"/view-"+view+"-unlocked.png");
                    SetKey(renderer,new[]{0,0,0,0}); var locked=Render(camera,output+"/view-"+view+"-locked.png");
                    SetKey(renderer,new[]{1,2,3,4}); var wrong=Render(camera,output+"/view-"+view+"-wrong.png");
                    worstVisual=Mathf.Max(worstVisual,Difference(reference,unlocked));
                    missingVisual=Mathf.Max(missingVisual,Difference(reference,locked)); wrongVisual=Mathf.Max(wrongVisual,Difference(reference,wrong));
                    Check(Difference(reference,unlocked)<.001f && Difference(reference,locked)>.01f && Difference(reference,wrong)>.01f,
                        "real Vulkan/lilToon render matches when unlocked and differs for absent/wrong values at view "+view);
                }
                Check(protectedMaterial.GetFloat("_LAGKey0")==0 && protectedMaterial.GetFloat("_LAGKey1")==0 &&
                      protectedMaterial.GetFloat("_LAGKey2")==0 && protectedMaterial.GetFloat("_LAGKey3")==0,
                      "unlock values remain in a transient property block and are absent from the saved material");
                Check(Hash(folder+"/original.asset")==sourceHash && Hash(folder+"/original.mat")==materialHash && Hash(folder+"/owned-texture.asset")==textureHash,
                    "source asset, original material and texture files remain byte-identical after GPU tests");
                var shaders=AssetDatabase.GetAllAssetPaths().Where(p=>p.StartsWith(folder+"/Shaders/")&&p.EndsWith(".shader")).ToArray();
                Check(shaders.All(p=>!ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(p))), "generated prototype and UsePass shaders compile without errors");
                Directory.CreateDirectory(output+"/windows-bundle");
                var bundle=BuildPipeline.BuildAssetBundles(output+"/windows-bundle",new[]{new AssetBundleBuild{assetBundleName="static-prototype.bundle",assetNames=new[]{folder+"/owned-prototype.prefab"}}},
                    BuildAssetBundleOptions.ForceRebuildAssetBundle|BuildAssetBundleOptions.StrictMode,BuildTarget.StandaloneWindows64);
                Check(bundle!=null, "Windows64 fixture bundle builds with the experimental decoder");
                File.WriteAllText(output+"/validation.json",JsonUtility.ToJson(new Report {checks=checks.ToArray(),seedSamples=16,uniquePrograms=publicHashes.Count,
                    uniqueDecoderBodies=canonicalDecoderHashes.Count,worstRoundTrip=worstRoundTrip,crossProgramRms=crossProgramRms,legacyDecoderRms=legacyDecoderRms,
                    worstUnlockedImageDifference=worstVisual,missingImageDifference=missingVisual,wrongImageDifference=wrongVisual,generatedShaders=shaders.Length,
                    windowsBundleBuilt=true,graphics=SystemInfo.graphicsDeviceType.ToString(),fixture="owned asymmetric static Unity sphere"},true));
                Debug.Log("LAG_STATIC_PROTOTYPE_SUCCESS "+checks.Count+" checks; "+shaders.Length+" shaders; Windows64 fixture bundle built");
            }
        }
        finally { foreach(var item in allocated.AsEnumerable().Reverse()) if(item && !AssetDatabase.Contains(item)) Object.DestroyImmediate(item); }
    }
    static string DecoderForCulture(StaticPolymorphicCodecV1 codec, CodecPlan plan, string name)
    { var saved=CultureInfo.CurrentCulture; try {CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(name); return codec.EmitDecoder(plan).Injection;} finally {CultureInfo.CurrentCulture=saved;} }
    [Serializable] sealed class Report
    {
        public string[] checks; public int seedSamples,uniquePrograms,uniqueDecoderBodies,generatedShaders;
        public float worstRoundTrip,crossProgramRms,legacyDecoderRms,worstUnlockedImageDifference,missingImageDifference,wrongImageDifference;
        public bool windowsBundleBuilt; public string graphics,fixture;
    }
#if LAG_VRCSDK
    public static void RunFullRegression() { LAGImplementationValidation.Run(); Run(); }
#endif
}
