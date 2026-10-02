// SPDX-License-Identifier: MIT
// Destructive test fixtures are confined to an explicitly enabled disposable ValidationProject.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using LinuxAvatarGuard;
using Object = UnityEngine.Object;

public static class LAGBindingValidation
{
    const string Folder = "Assets/BindingCodecFixture";
    static readonly List<string> checks = new List<string>();
    public static readonly byte[] PublicSeed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    public const string PublicBuildId = "0123456789abcdef0123456789abcdef";
    [DllImport("libc", SetLastError = true)] static extern int symlink(string source, string link);
    [DllImport("libc", SetLastError = true)] static extern int link(string source, string target);
    [DllImport("libc", SetLastError = true)] static extern int chmod(string path, uint mode);
    [DllImport("libc", SetLastError = true)] static extern int mkfifo(string path, uint mode);
    static void Check(bool condition, string description)
    { if (!condition) throw new Exception("BINDING_VALIDATION_FAILED: " + description); checks.Add(description); }
    public static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } catch (IOException) { return true; }
    }
    public static void RequireResearchProject()
    {
        if (Environment.GetEnvironmentVariable("LAG_BINDING_AUDIT_ALLOWED") != "synthetic-unity-only" ||
            Path.GetFileName(Path.GetDirectoryName(Application.dataPath)) != "ValidationProject" ||
            Application.platform != RuntimePlatform.LinuxEditor || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan || GraphicsSettings.currentRenderPipeline != null)
            throw new InvalidOperationException("Run only in disposable ValidationProject, Linux/Vulkan/Built-in with explicit synthetic test flag.");
    }
    static byte[] Hex(string text) => Enumerable.Range(0, text.Length / 2).Select(i => Convert.ToByte(text.Substring(i * 2, 2), 16)).ToArray();
    public static Mesh Fixture()
    {
        // Owned, low-poly asymmetric surface. No imported avatar or commercial assets.
        const int around = 12, vertical = 8;
        var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var indices = new List<int>();
        for (int y = 0; y <= vertical; y++) for (int x = 0; x <= around; x++)
        {
            float a = x * 2 * Mathf.PI / around, b = .03f + y * (Mathf.PI - .06f) / vertical;
            float r = Mathf.Sin(b) * (.5f + .08f * Mathf.Cos(b));
            vertices.Add(new Vector3(r * Mathf.Cos(a), Mathf.Cos(b) * .65f, r * Mathf.Sin(a) * .75f + .06f * Mathf.Cos(a) * Mathf.Cos(a)));
            uv.Add(new Vector2((float)x / around, (float)y / vertical));
            if (x < around && y < vertical)
            { int n = y * (around + 1) + x; indices.AddRange(new[] { n, n + around + 1, n + 1, n + 1, n + around + 1, n + around + 2 }); }
        }
        var mesh = new Mesh { name = "OwnedBindingSurface", vertices = vertices.ToArray(), triangles = indices.ToArray(), uv = uv.ToArray() };
        mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds(); return mesh;
    }
    static MeshRenderer AddBinding(GameObject root, Mesh source, string name)
    {
        var obj = new GameObject(name); obj.transform.SetParent(root.transform, false);
        obj.AddComponent<MeshFilter>().sharedMesh = source; return obj.AddComponent<MeshRenderer>();
    }
    public static bool Same(Mesh a, Mesh b) => a.vertices.SequenceEqual(b.vertices) && a.uv7.SequenceEqual(b.uv7) && a.uv8.SequenceEqual(b.uv8);
    public static void Run()
    {
        RequireResearchProject(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); checks.Clear();
        var oldXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var privateRoot = Path.Combine(Path.GetTempPath(), "lag-binding-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(privateRoot);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", privateRoot);
        var allocated = new List<Object>();
        string savedPath = null;
        try
        {
            // Published RFC 5869 SHA-256 A.1/A.2/A.3, including multi-block output and absent salt/info.
            var ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
            Check(MeshKeyDerivation.HkdfSha256(ikm, Hex("000102030405060708090a0b0c"), Hex("f0f1f2f3f4f5f6f7f8f9"), 42)
                .SequenceEqual(Hex("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865")), "RFC 5869 SHA256 test vector A.1");
            Check(MeshKeyDerivation.HkdfSha256(Enumerable.Range(0, 80).Select(i => (byte)i).ToArray(),
                Enumerable.Range(0x60, 80).Select(i => (byte)i).ToArray(), Enumerable.Range(0xb0, 80).Select(i => (byte)i).ToArray(), 82)
                .SequenceEqual(Hex("b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71cc30c58179ec3e87c14c01d5c1f3434f1d87")), "RFC 5869 SHA256 test vector A.2");
            var emptyExpected = Hex("8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8");
            Check(MeshKeyDerivation.HkdfSha256(ikm, Array.Empty<byte>(), Array.Empty<byte>(), 42).SequenceEqual(emptyExpected) &&
                MeshKeyDerivation.HkdfSha256(ikm, null, null, 42).SequenceEqual(emptyExpected), "RFC 5869 SHA256 A.3 and omitted salt/info");
            Check(Rejects(() => MeshKeyDerivation.HkdfSha256(null,null,null,32)) && Rejects(() => MeshKeyDerivation.HkdfSha256(ikm,null,null,0)) &&
                Rejects(() => MeshKeyDerivation.HkdfSha256(ikm,null,null,8161)), "HKDF rejects null input and invalid output lengths");
            Check(MeshKeyDerivation.HkdfSha256(ikm,null,null,8160).Length == 8160, "HKDF permits RFC maximum output without counter overflow");
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", "BindingCodecFixture");
            var source = Fixture(); AssetDatabase.CreateAsset(source, Folder + "/source.asset");
            var subasset = Object.Instantiate(source); subasset.name = "OwnedSubasset"; AssetDatabase.AddObjectToAsset(subasset, source);
            var twinAsset = Object.Instantiate(source); AssetDatabase.CreateAsset(twinAsset, Folder + "/twin.asset"); AssetDatabase.SaveAssets();
            var root = new GameObject("OwnedBindingRoot"); allocated.Add(root);
            var body = AddBinding(root, source, "Duplicate/名前"); var hair = AddBinding(root, source, "Duplicate/名前");
            var otherAsset = AddBinding(root, twinAsset, "Other"); var sub = AddBinding(root, subasset, "Sub");
            var a = MeshBindingIdentity.Capture(root, body); var b = MeshBindingIdentity.Capture(root, hair);
            Check(a.ContentHash == b.ContentHash && a.StableId != b.StableId, "duplicate names and shared source mesh have distinct renderer binding IDs");
            Check(a.SourceGuid == MeshBindingIdentity.Capture(root,sub).SourceGuid && a.SourceLocalFileId != MeshBindingIdentity.Capture(root,sub).SourceLocalFileId,
                "GUID plus localFileID distinguishes subassets");
            Check(a.ContentHash == MeshBindingIdentity.Capture(root,otherAsset).ContentHash && a.SourceGuid != MeshBindingIdentity.Capture(root,otherAsset).SourceGuid,
                "identical content in separate source assets retains separate asset identity");
            Check(a.StableId == MeshBindingIdentity.Capture(root,body).StableId, "capture is stable across repeated queries");
            var clone = Object.Instantiate(root); allocated.Add(clone);
            Check(a.StableId == MeshBindingIdentity.Capture(clone,clone.GetComponentsInChildren<MeshRenderer>()[0]).StableId,
                "independent clone of the same root preserves relative binding identity");
            Check(!Rejects(() => {root.name="RenamedRoot"; a.Validate();}), "root instance name is excluded from binding identity");
            var foreign=new GameObject("ForeignRoot"); allocated.Add(foreign);
            Check(Rejects(() => MeshBindingIdentity.Capture(foreign,body)), "a foreign root cannot capture this renderer");
            var transient = Fixture(); allocated.Add(transient); var transientRenderer=AddBinding(root,transient,"Transient");
            Check(Rejects(() => MeshBindingIdentity.Capture(root,transientRenderer)), "transient meshes must be saved before binding capture");
            var p = MeshKeyDerivation.Derive(PublicSeed,PublicBuildId,a,MeshDerivationPurpose.Program);
            var q = MeshKeyDerivation.Derive(PublicSeed,PublicBuildId,a,MeshDerivationPurpose.Payload);
            Check(!p.SequenceEqual(q) && !p.SequenceEqual(MeshKeyDerivation.Derive(PublicSeed,PublicBuildId,b,MeshDerivationPurpose.Program)),
                "program, payload and other renderer use distinct derivation domains");
            Check(!p.SequenceEqual(MeshKeyDerivation.Derive(PublicSeed,"1123456789abcdef0123456789abcdef",a,MeshDerivationPurpose.Program)) &&
                !p.SequenceEqual(MeshKeyDerivation.Derive(PublicSeed.Reverse().ToArray(),PublicBuildId,a,MeshDerivationPurpose.Program)), "BuildID and master seed both affect derivation");
            Check(Rejects(() => MeshKeyDerivation.Derive(PublicSeed,"../bad",a,MeshDerivationPurpose.Program)) &&
                Rejects(() => MeshKeyDerivation.Derive(new byte[31],PublicBuildId,a,MeshDerivationPurpose.Program)) &&
                Rejects(() => MeshKeyDerivation.Derive(PublicSeed,PublicBuildId,a,(MeshDerivationPurpose)99)), "invalid IDs, master seeds and purposes fail closed");
            var culture=CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ja-JP"); Check(p.SequenceEqual(MeshKeyDerivation.Derive(PublicSeed,PublicBuildId,a,MeshDerivationPurpose.Program)), "identity and derivation are culture independent"); }
            finally {CultureInfo.CurrentCulture=culture;}
            using (var context = GuardBuildContext.CreateReproducible(PublicBuildId, PublicSeed))
            using (var first = context.CreateCodec(a)) using (var second = context.CreateCodec(b))
            {
                var planA=first.Plan(source,.15f); var planB=second.Plan(source,.15f); var key=context.RuntimeKey();
                Check(planA.BindingStableId==a.StableId && planB.BindingStableId==b.StableId && StaticPolymorphicCodecV1.ProgramHash(planA)!=StaticPolymorphicCodecV1.ProgramHash(planB),
                    "context plans carry binding IDs and separate programs on one shared source");
                var encoded=first.Encode(source,planA,key); allocated.Add(encoded); var encodedB=second.Encode(source,planB,key); allocated.Add(encodedB);
                Check(!Same(encoded,encodedB), "per-binding program/payload derivation produces different encoded meshes");
                Check(Rejects(() => context.SavePrivate()), "an empty/unregistered context cannot be persisted");
                context.RecordPlan(a,planA); context.RecordPlan(b,planB); savedPath=context.SavePrivate();
                Check(context.BindingCount==2 && File.Exists(savedPath) && !savedPath.StartsWith(Path.GetDirectoryName(Application.dataPath)+"/",StringComparison.Ordinal),
                    "versioned context is persisted outside Unity assets");
                string text=File.ReadAllText(savedPath);
                Check(Rejects(() => context.SavePrivate()) && File.ReadAllText(savedPath)==text, "reusing a BuildID cannot overwrite its private context");
                Check(!Directory.GetFiles(Path.GetDirectoryName(savedPath),".context-*").Any(), "successful and rejected creates leave no temporary secret files");
                using (var restored=GuardBuildContext.LoadPrivate(PublicBuildId)) using(var replay=restored.CreateCodec(a))
                {
                    var repeat=replay.Encode(source,replay.Plan(source,.15f),restored.RuntimeKey()); allocated.Add(repeat);
                    Check(Same(encoded,repeat) && key.SequenceEqual(restored.RuntimeKey()) && restored.BindingCount==2, "private restore reproduces exact program, payload, encoded positions and unlock values");
                    Check(Rejects(() => restored.CreateCodec(MeshBindingIdentity.Capture(root,otherAsset))), "restored contexts reject unregistered bindings");
                    Check(Rejects(() => replay.Plan(source,.2f)), "replayed bindings reject a changed recorded intensity");
                }
                using(var wrong=GuardBuildContext.CreateReproducible("2123456789abcdef0123456789abcdef",PublicSeed))
                    Check(Rejects(() => wrong.RecordPlan(a,planA)), "a foreign build cannot register another context's plan");
                Check(Rejects(() => first.Encode(twinAsset,planA,key)), "context codec rejects a different source object");
                string oldName=body.name; body.name="ChangedRenderer";
                Check(Rejects(() => first.Encode(source,planA,key)) && Rejects(() => first.EmitDecoder(planA)), "renderer rename invalidates encoding and emission"); body.name=oldName;
                hair.transform.SetSiblingIndex(0);
                Check(Rejects(() => a.Validate()), "sibling reordering invalidates captured binding identity"); body.transform.SetSiblingIndex(0);
                string move=AssetDatabase.MoveAsset(Folder+"/source.asset",Folder+"/renamed-source.asset");
                Check(move=="" && !Rejects(() => a.Validate()), "source asset rename preserves GUID/localFileID/content identity");
                var vertices=source.vertices; var changed=(Vector3[])vertices.Clone(); changed[0].x+=.001f; source.vertices=changed;
                Check(Rejects(() => first.EmitDecoder(planA)) && Rejects(() => a.Validate()), "source geometry mutations invalidate the binding plan"); source.vertices=vertices;
                Check(!Rejects(() => a.Validate()), "restoring unchanged source content restores its captured identity");
                // Store adversarial checks are restricted to known public fixture seed/context in the temp XDG directory.
                foreach(var field in new[]{"schemaVersion","derivationVersion","codecVersion","bindingSchema"})
                {
                    File.WriteAllText(savedPath,text.Replace("\""+field+"\": 1","\""+field+"\": 99"));
                    Check(Rejects(() => GuardBuildContext.LoadPrivate(PublicBuildId)), "private restore rejects incompatible "+field);
                }
                File.WriteAllText(savedPath,text.Replace("\"programHash\": \"","\"programHash\": \"0"));
                Check(Rejects(() => GuardBuildContext.LoadPrivate(PublicBuildId)), "malformed private program hashes are rejected");
                File.WriteAllText(savedPath,text.Replace(Convert.ToBase64String(PublicSeed),Convert.ToBase64String(PublicSeed.Reverse().ToArray())));
                Check(Rejects(() => {using(var bad=GuardBuildContext.LoadPrivate(PublicBuildId)) using(var unused=bad.CreateCodec(a)) {}}), "private seed tampering fails program reproduction validation");
                File.WriteAllText(savedPath,text);
                Check(chmod(savedPath,420)==0 && Rejects(() => GuardBuildContext.LoadPrivate(PublicBuildId)), "world-readable private files are refused"); chmod(savedPath,384);
                var hard=savedPath+".hard"; Check(link(savedPath,hard)==0 && Rejects(() => GuardBuildContext.LoadPrivate(PublicBuildId)), "hard-linked private files are refused"); File.Delete(hard);
                var backup=savedPath+".backup"; File.Move(savedPath,backup);
                Check(symlink(backup,savedPath)==0 && Rejects(() => GuardBuildContext.LoadPrivate(PublicBuildId)) && Rejects(() => context.SavePrivate()), "file symlinks cannot be read or overwritten"); File.Delete(savedPath);
                Check(mkfifo(savedPath,384)==0 && Rejects(() => GuardBuildContext.LoadPrivate(PublicBuildId)), "nonregular private files fail without blocking on FIFO"); File.Delete(savedPath); File.Move(backup,savedPath);
                var alias=privateRoot+"-alias"; Check(symlink(privateRoot,alias)==0,"create synthetic path alias for traversal check");
                Environment.SetEnvironmentVariable("XDG_DATA_HOME",alias);
                Check(Rejects(() => GuardBuildContext.LoadPrivate(PublicBuildId)), "private path traversal refuses symlink directory aliases"); File.Delete(alias);
                Environment.SetEnvironmentVariable("XDG_DATA_HOME",Application.dataPath);
                Check(Rejects(() => context.SavePrivate()), "a private data root inside Unity project is rejected before writing");
                Environment.SetEnvironmentVariable("XDG_DATA_HOME",Path.GetFullPath(".."));
                Check(Rejects(() => context.SavePrivate()), "a private data root inside this Git repository is rejected before writing");
                Environment.SetEnvironmentVariable("XDG_DATA_HOME",privateRoot);
            }
            var callerSeed=(byte[])PublicSeed.Clone();
            using(var copy=GuardBuildContext.CreateReproducible(PublicBuildId,callerSeed))
            { var before=copy.RuntimeKey(); callerSeed[0]^=255; Check(before.SequenceEqual(copy.RuntimeKey()), "context clones the caller master seed"); }
            using(var random=GuardBuildContext.CreateRandom()) Check(random.RuntimeKey().Length==4 && random.BuildId!=PublicBuildId,"CSPRNG default creates a new build context");
            var disposed=GuardBuildContext.CreateReproducible(PublicBuildId,PublicSeed); disposed.Dispose();
            Check(Rejects(() => disposed.RuntimeKey()) && Rejects(() => disposed.CreateCodec(a)), "disposed private contexts cannot derive or create codecs");
            string output="../evidence/binding-derivation"; Directory.CreateDirectory(output);
            File.WriteAllText(output+"/validation.json",JsonUtility.ToJson(new Report{checks=checks.ToArray(),graphics=SystemInfo.graphicsDeviceType.ToString(),
                fixture="owned static surface, public test seed only",privateStoreTested=true,sourcePreserved=true},true));
            Debug.Log("LAG_BINDING_VALIDATION_SUCCESS "+checks.Count+" checks");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME",oldXdg);
            if(Directory.Exists(privateRoot)) Directory.Delete(privateRoot,true);
            foreach(var item in allocated.AsEnumerable().Reverse()) if(item && !AssetDatabase.Contains(item)) Object.DestroyImmediate(item);
        }
    }
    [Serializable] sealed class Report {public string[] checks; public string graphics,fixture; public bool privateStoreTested,sourcePreserved;}
    public static void RunWithPrototypeRegression() { Run(); LAGStaticPrototypeValidation.Run(); }
}
