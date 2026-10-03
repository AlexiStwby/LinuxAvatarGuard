// SPDX-License-Identifier: MIT
// Own procedural images/meshes only; never inspect an imported avatar or VRChat process.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LinuxAvatarGuard;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class LAGTextureValidation
{
    const string Folder = "Assets/TextureFixture";
    static string output;
    static readonly List<string> checks = new List<string>();
    static readonly List<Visual> visuals = new List<Visual>();
    static int uniquePrograms, adaptiveCases, maxPixelError, imageCount;
    static float maxMean, maxChannel, minMissing, minWrong;
    static string Hash(byte[] bytes) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    static byte[] Seed(string scope) { using (var h = SHA256.Create()) return h.ComputeHash(Encoding.UTF8.GetBytes("LAG/owned-texture-test/v1/" + scope)); }
    static void Check(bool ok, string description) { if (!ok) throw new Exception("TEXTURE_VALIDATION_FAILED: " + description); checks.Add(description); }
    static bool Rejects(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } }
    static Dictionary<string,string> Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Hash(File.ReadAllBytes(p)));
    static bool Unchanged(Dictionary<string,string> before) => before.All(p => File.Exists(p.Key) && Hash(File.ReadAllBytes(p.Key)) == p.Value);
    static Color32[] Pixels(int size)
    {
        var result = new Color32[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            int tile = x / (size/4) + 4 * (y / (size/4));
            // Smooth areas + tile boundaries + high-frequency, asymmetric marks.
            bool mark = (x + 3*y) % 19 < 3 || (x - size/3)*(x - size/3) + (y - size/2)*(y - size/2) < size*size/65;
            result[x + size*y] = new Color32((byte)(mark ? 238 : 20 + x*173/size),
                (byte)(mark ? 37 + tile*7 : 21 + y*195/size), (byte)(mark ? 45 + x*117/size : (tile*29 + x + 2*y) % 221 + 15), 255);
        }
        return result;
    }
    static Texture2D Source(string name, bool srgb = true, FilterMode filter = FilterMode.Bilinear,
        TextureWrapMode u = TextureWrapMode.Repeat, TextureWrapMode v = TextureWrapMode.Repeat, int size = 64,
        TextureFormat format = TextureFormat.RGBA32, bool mips = false, bool opaque = true)
    {
        var texture = new Texture2D(size, size, format, mips, !srgb) {name = name, filterMode = filter, wrapModeU = u, wrapModeV = v, anisoLevel = 0};
        var pixels = Pixels(size); if (!opaque) pixels[7].a = 30;
        texture.SetPixels32(pixels); texture.Apply(mips, false);
        AssetDatabase.CreateAsset(texture, Folder + "/" + name + ".asset"); AssetDatabase.SaveAssetIfDirty(texture);
        return texture;
    }
    static Material SourceMaterial(Texture2D source, string name)
    {
        var material = new Material(Shader.Find("lilToon")) {name = name};
        material.SetTexture("_MainTex", source); material.SetColor("_Color", Color.white); material.SetFloat("_AsUnlit", 1); material.SetFloat("_Cull", 0);
        AssetDatabase.CreateAsset(material, Folder + "/" + name + ".mat"); AssetDatabase.SaveAssetIfDirty(material);
        return material;
    }
    static Color32[] ReadPayload(Texture2D encoded)
    {
        var request = AsyncGPUReadback.Request(encoded, 0, TextureFormat.RGBA32); request.WaitForCompletion();
        if (request.hasError) throw new Exception("Owned texture GPU readback failed");
        return request.GetData<Color32>().ToArray();
    }
    // Independent adaptive extractor: parse emitted HLSL constants, not the codec's CPU methods or typed instructions.
    static Color32[] Extract(Color32[] payload, int size, string hlsl, int[] key)
    {
        var matches = Regex.Matches(hlsl, @"if \(tile == (\d+)u\) \{ atlas = (\d+)u; op = (\d+)u; order = (\d+)u; salts = uint3\((\d+)u,(\d+)u,(\d+)u\); \}");
        if (matches.Count != 16) throw new Exception("Independent extractor did not find sixteen tiles");
        var rows = matches.Cast<Match>().ToDictionary(m => int.Parse(m.Groups[1].Value), m => Enumerable.Range(2,6).Select(i => int.Parse(m.Groups[i].Value)).ToArray());
        string[] orders = {"RGB","RBG","GRB","GBR","BRG","BGR"};
        var decoded = new Color32[payload.Length]; int side = size/4;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            int tile = x/side + 4*(y/side); var row = rows[tile]; int a = x%side, b = y%side;
            if ((row[1]&1) == 1) { int t = a; a = b; b = t; }
            if ((row[1]&2) == 2) a = side - a - 1;
            if ((row[1]&4) == 4) b = side - b - 1;
            var pixel = payload[(row[0]%4*side+a) + size*(row[0]/4*side+b)];
            byte[] values = {(byte)(pixel.r ^ row[3] ^ key[tile%4]), (byte)(pixel.g ^ row[4] ^ key[(tile+1)%4]), (byte)(pixel.b ^ row[5] ^ key[(tile+2)%4])};
            string order = orders[row[2]];
            decoded[x+size*y] = new Color32(values[order.IndexOf('R')],values[order.IndexOf('G')],values[order.IndexOf('B')],255);
        }
        return decoded;
    }
    static int ByteError(Color32[] a, Color32[] b) => a.Zip(b, (x,y) => new[] {Math.Abs(x.r-y.r),Math.Abs(x.g-y.g),Math.Abs(x.b-y.b),Math.Abs(x.a-y.a)}.Max()).Max();
    static float ByteMean(Color32[] a, Color32[] b) => a.Zip(b, (x,y) => (Math.Abs(x.r-y.r)+Math.Abs(x.g-y.g)+Math.Abs(x.b-y.b))/765f).Average();
    static void SavePixels(Color32[] pixels, int size, string path)
    {
        var texture = new Texture2D(size,size,TextureFormat.RGBA32,false,true);
        try { texture.SetPixels32(pixels); texture.Apply(); File.WriteAllBytes(path,texture.EncodeToPNG()); }
        finally { Object.DestroyImmediate(texture); }
    }
    static void CodecChecks()
    {
        var source = Source("codec-source"); var before = Snapshot(Folder); var hashes = new HashSet<string>();
        var orientations = new HashSet<int>(); var orders = new HashSet<int>(); int[] key = {73,141,219,38};
        using (var codec = new TextureGuardCodecV1(Seed("program"), "body/slot0"))
        {
            var plan = codec.Plan(source); var text = codec.EmitDecoder(plan).Functions;
            var encoded = codec.Encode(source,plan,key);
            try
            {
                var payload = ReadPayload(encoded); var original = source.GetPixels32();
                Check(!encoded.isReadable && encoded.mipmapCount == 1 && !GraphicsFormatUtility.IsSRGBFormat(encoded.graphicsFormat),"payload is linear RGBA32, single mip and CPU unreadable");
                Check(plan.Program.SourceSRGB==source.isDataSRGB,"program records authored color space independently of the GPU view");
                Check(ByteError(Extract(payload,source.width,text,key),original) == 0,"independent HLSL extractor exactly recovers the GPU bytes");
                Check(ByteMean(payload,original) > .1f && ByteMean(Extract(payload,source.width,text,new int[4]),original) > .1f,"raw payload and missing-key reconstruction differ visibly from source");
                for (int lane = 0; lane < 4; lane++)
                { var wrong = (int[])key.Clone(); wrong[lane]++; Check(ByteError(Extract(payload,source.width,text,wrong),original)>0,"every runtime key lane changes recovered pixels " + lane); }
                var repeated = codec.Encode(source,codec.Plan(source),key);
                try { Check(ByteError(payload,ReadPayload(repeated)) == 0 && TextureGuardCodecV1.ProgramHash(plan) == TextureGuardCodecV1.ProgramHash(codec.Plan(source)),"same seed/scope/source reproduces program and payload exactly"); }
                finally { Object.DestroyImmediate(repeated); }
                SavePixels(original,source.width,output+"/texture-original.png"); SavePixels(payload,source.width,output+"/texture-encoded.png");
                SavePixels(Extract(payload,source.width,text,key),source.width,output+"/texture-recovered.png");
                using (var other = new TextureGuardCodecV1(Seed("program"), "body/slot1"))
                {
                    var otherPlan = other.Plan(source);
                    Check(TextureGuardCodecV1.ProgramHash(plan) != TextureGuardCodecV1.ProgramHash(otherPlan),"material scope changes program");
                    Check(ByteMean(Extract(payload,source.width,other.EmitDecoder(otherPlan).Functions,key),original) > .1f,"other material's program does not recover payload");
                    Check(Rejects(()=>other.Encode(source,plan,key)),"plan owner is enforced across codecs");
                }
                var culture = CultureInfo.CurrentCulture;
                try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES"); Check(codec.EmitDecoder(plan).Functions == text,"HLSL generation is locale independent"); }
                finally { CultureInfo.CurrentCulture = culture; }
                foreach (var invalid in new[] {new[]{0,1,2,3},new[]{256,1,2,3},new[]{1,2,3},null})
                    Check(Rejects(()=>codec.Encode(source,plan,invalid)),"invalid encoding key rejects before allocation");
                var wrap = source.wrapModeU; source.wrapModeU = TextureWrapMode.Clamp;
                Check(Rejects(()=>codec.Encode(source,plan,key)),"sampling mutation invalidates captured plan");
                source.wrapModeU = wrap; AssetDatabase.SaveAssetIfDirty(source);
                var p = source.GetPixels32(); p[0].r ^= 16; source.SetPixels32(p); source.Apply(false); AssetDatabase.SaveAssetIfDirty(source);
                Check(Rejects(()=>codec.EmitDecoder(plan)),"pixel mutation invalidates shader generation even after save");
                source.SetPixels32(original); source.Apply(false); AssetDatabase.SaveAssetIfDirty(source);
            }
            finally { Object.DestroyImmediate(encoded); }
        }
        Check(Unchanged(before),"codec experiments restore the source file byte-identically");
        for (int i = 0; i < 32; i++) using (var codec = new TextureGuardCodecV1(Seed("diversity-"+i), "body/slot0"))
        {
            var plan = codec.Plan(source); hashes.Add(TextureGuardCodecV1.ProgramHash(plan));
            foreach (var tile in plan.Program.Tiles) { orientations.Add(tile.Orientation); orders.Add(tile.ChannelOrder); }
            Check(plan.Program.Tiles.Select(t=>t.Destination).Distinct().Count() == 16,"tile mapping is bijective " + i);
            var encoded = codec.Encode(source,plan,key);
            try
            {
                int error = ByteError(Extract(ReadPayload(encoded),source.width,codec.EmitDecoder(plan).Functions,key),source.GetPixels32());
                maxPixelError = Math.Max(maxPixelError,error); adaptiveCases++;
                Check(error == 0,"independent extraction recovers diversified program " + i);
            }
            finally { Object.DestroyImmediate(encoded); }
        }
        uniquePrograms = hashes.Count;
        Check(hashes.Count == 32 && orientations.Count == 8 && orders.Count == 6,"32 seeds cover all orientations/RGB orders and produce distinct programs");
        using (var codec = new TextureGuardCodecV1(Seed("program"),"body/slot0"))
        {
            var copy = Source("same-pixels-different-identity");
            Check(TextureGuardCodecV1.ProgramHash(codec.Plan(source)) != TextureGuardCodecV1.ProgramHash(codec.Plan(copy)),"persistent texture identity separates equal image content");
        }
        Check(Rejects(()=>new TextureGuardCodecV1(new byte[31],"x"))&&Rejects(()=>new TextureGuardCodecV1(Seed("x"),"")),"invalid seed and scope reject");
        var retired = new TextureGuardCodecV1(Seed("x"),"x"); retired.Dispose();
        bool disposed = false; try { retired.Plan(source); } catch (ObjectDisposedException) { disposed = true; }
        Check(disposed,"disposed seed owner cannot create plans");
    }
    static void NegativeChecks()
    {
        using (var codec = new TextureGuardCodecV1(Seed("negative"),"negative"))
        {
            Check(Rejects(()=>codec.Plan(Source("has-mips",mips:true))),"mipmap sources reject");
            Check(Rejects(()=>codec.Plan(Source("has-alpha",opaque:false))),"non-opaque texels reject");
            Check(Rejects(()=>codec.Plan(Source("wrong-format",format:TextureFormat.RGB24))),"non-RGBA32 sources reject");
            Check(Rejects(()=>codec.Plan(Source("trilinear",filter:FilterMode.Trilinear))),"trilinear sources reject");
            Check(Rejects(()=>codec.Plan(Source("mirror",u:TextureWrapMode.Mirror))),"unreviewed wrap mode rejects");
            Check(Rejects(()=>codec.Plan(Source("nonpower",size:20))),"non-power-of-two source rejects");
            var source = Source("negative-source"); var material = SourceMaterial(source,"negative-material");
            source.anisoLevel = 4; AssetDatabase.SaveAssetIfDirty(source);
            Check(Rejects(()=>codec.Plan(source)),"anisotropic source rejects"); source.anisoLevel = 0; AssetDatabase.SaveAssetIfDirty(source);
            var transient = new Texture2D(64,64,TextureFormat.RGBA32,false);
            try { Check(Rejects(()=>codec.Plan(transient)),"transient source rejects"); } finally { Object.DestroyImmediate(transient); }
            var opaque = Source("non-readable-source"); opaque.Apply(false,true); AssetDatabase.SaveAssetIfDirty(opaque);
            Check(Rejects(()=>codec.Plan(opaque)),"unreadable source rejects without changing importer");
            string folder = "Assets/LinuxAvatarGuardGenerated/Research/texture-negative";
            if (Directory.Exists(folder)) AssetDatabase.DeleteAsset(folder);
            Check(Rejects(()=>GuardTextureForge.Prepare(material,codec,"Assets/../outside",new[]{1,2,3,4})),"output traversal rejects");
            Check(Rejects(()=>GuardTextureForge.Prepare(material,codec,folder+"\n",new[]{1,2,3,4})),"output names with a trailing newline reject");
            material.SetTexture("_MainColorAdjustMask",source); AssetDatabase.SaveAssetIfDirty(material);
            Check(Rejects(()=>GuardTextureForge.Prepare(material,codec,folder,new[]{1,2,3,4}))&&!Directory.Exists(folder),"unprotected duplicate albedo reference rejects before output");
            material.SetTexture("_MainColorAdjustMask",null); AssetDatabase.SaveAssetIfDirty(material);
            var originalShader = material.shader; material.shader = Shader.Find("Hidden/lilToonCutout"); AssetDatabase.SaveAssetIfDirty(material);
            Check(Rejects(()=>GuardTextureForge.Prepare(material,codec,folder,new[]{1,2,3,4}))&&!Directory.Exists(folder),"cutout material rejects before output");
            material.shader = originalShader; AssetDatabase.SaveAssetIfDirty(material);
            var before = Snapshot(Folder); LAGTextureFaultPostprocessor.Material = material; LAGTextureFaultPostprocessor.Prefix = folder+"/"; LAGTextureFaultPostprocessor.Touched = false;
            try { Check(Rejects(()=>GuardTextureForge.Prepare(material,codec,folder,new[]{1,2,3,4}))&&LAGTextureFaultPostprocessor.Touched&&!Directory.Exists(folder),"late source mutation rolls back the new generated folder"); }
            finally { LAGTextureFaultPostprocessor.Material = null; material.SetTextureScale("_MainTex",Vector2.one); AssetDatabase.SaveAssetIfDirty(material); }
            Check(Unchanged(before),"late rollback leaves all source files byte-identical");
            LAGTextureFaultPostprocessor.Material = material; LAGTextureFaultPostprocessor.Prefix = folder+"/";
            LAGTextureFaultPostprocessor.Touched = false; LAGTextureFaultPostprocessor.TamperGeneratedKey = true;
            try { Check(Rejects(()=>GuardTextureForge.Prepare(material,codec,folder,new[]{1,2,3,4}))&&LAGTextureFaultPostprocessor.Touched&&!Directory.Exists(folder),"late nonzero serialized key mutation rejects and rolls back output"); }
            finally { LAGTextureFaultPostprocessor.Material = null; LAGTextureFaultPostprocessor.TamperGeneratedKey = false; }
            Check(Unchanged(before),"generated-key mutation does not change any source file");
            Directory.CreateDirectory(folder); File.WriteAllText(folder+"/sentinel.txt","preserve me"); AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            try { Check(Rejects(()=>GuardTextureForge.Prepare(material,codec,folder,new[]{1,2,3,4}))&&File.ReadAllText(folder+"/sentinel.txt")=="preserve me","existing output is preserved on rejection"); }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
        // Non-destructive PNG importer path, including a settings mutation without reimporting.
        string png = Folder+"/owned-imported.png"; SavePixels(Pixels(64),64,png); AssetDatabase.ImportAsset(png,ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(png);
        importer.textureType = TextureImporterType.Default; importer.mipmapEnabled = false; importer.isReadable = true;
        importer.textureCompression = TextureImporterCompression.Uncompressed; importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.filterMode = FilterMode.Bilinear; importer.anisoLevel = 0; importer.sRGBTexture = true;
        var settings = importer.GetDefaultPlatformTextureSettings(); settings.format = TextureImporterFormat.RGBA32; importer.SetPlatformTextureSettings(settings); importer.SaveAndReimport();
        var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(png); string metadata = File.ReadAllText(png+".meta");
        using (var codec = new TextureGuardCodecV1(Seed("import"),"import"))
        {
            var plan = codec.Plan(imported); var encoded = codec.Encode(imported,plan,new[]{11,22,33,44}); Object.DestroyImmediate(encoded);
            Check(File.ReadAllText(png+".meta")==metadata,"PNG importer settings remain byte-identical during encoding");
            importer.sRGBTexture = false;
            Check(Rejects(()=>codec.Validate(imported,plan)),"unsaved importer mutation invalidates plan before reimport");
            importer.sRGBTexture = true;
        }
    }
    static Mesh Quad()
    {
        var mesh = new Mesh {name="OwnedTextureQuad",vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)},
            uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up},normals=Enumerable.Repeat(Vector3.back,4).ToArray(),triangles=new[]{0,2,1,0,3,2}};
        mesh.RecalculateBounds(); mesh.RecalculateTangents(); AssetDatabase.CreateAsset(mesh,Folder+"/quad.asset"); return mesh;
    }
    static Color[] Render(Camera camera, string path)
    {
        var rt = new RenderTexture(192,192,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Default) {antiAliasing=1};
        var image = new Texture2D(192,192,TextureFormat.RGBA32,false); var previous = RenderTexture.active;
        try { camera.targetTexture=rt; camera.Render(); RenderTexture.active=rt; image.ReadPixels(new Rect(0,0,192,192),0,0); image.Apply();
            if (path != null) { File.WriteAllBytes(path,image.EncodeToPNG()); imageCount++; } return image.GetPixels(); }
        finally { camera.targetTexture=null; RenderTexture.active=previous; Object.DestroyImmediate(image); Object.DestroyImmediate(rt); }
    }
    static void Key(Renderer renderer, int[] key)
    { var block = new MaterialPropertyBlock(); for (int i=0;i<4;i++) block.SetFloat(GuardShaders.Property(i),key[i]); renderer.SetPropertyBlock(block); }
    static float Mean(Color[] a, Color[] b) => a.Zip(b,(x,y)=>(Mathf.Abs(x.r-y.r)+Mathf.Abs(x.g-y.g)+Mathf.Abs(x.b-y.b))/3).Average();
    static float Maximum(Color[] a, Color[] b) => a.Zip(b,(x,y)=>Mathf.Max(Mathf.Abs(x.r-y.r),Mathf.Abs(x.g-y.g),Mathf.Abs(x.b-y.b))).Max();
    [Serializable] sealed class Visual { public string name; public bool srgb; public string filter,wrapU,wrapV; public int view; public float mean,max,missing,wrong; }
    sealed class Case { public Material Source; public Texture2D Texture; public GuardTextureArtifact Artifact; public int[] Key; public string Name; }
    static void Compare(Case sample, Material material, MeshRenderer renderer, Camera camera, int view, string prefix)
    {
        renderer.SetPropertyBlock(null); renderer.sharedMaterial=sample.Source; var original=Render(camera,prefix+"-original.png");
        renderer.sharedMaterial=material; Key(renderer,sample.Key); var unlocked=Render(camera,prefix+"-unlocked.png");
        Key(renderer,new int[4]); var missing=Render(camera,prefix+"-missing.png"); Key(renderer,new[]{1,2,3,4}); var wrong=Render(camera,prefix+"-wrong.png");
        float mean=Mean(original,unlocked),maximum=Maximum(original,unlocked),missingDiff=Mean(original,missing),wrongDiff=Mean(original,wrong);
        maxMean=Mathf.Max(maxMean,mean); maxChannel=Mathf.Max(maxChannel,maximum); minMissing=Mathf.Min(minMissing,missingDiff); minWrong=Mathf.Min(minWrong,wrongDiff);
        visuals.Add(new Visual {name=sample.Name,srgb=sample.Texture.isDataSRGB,filter=sample.Texture.filterMode.ToString(),
            wrapU=sample.Texture.wrapModeU.ToString(),wrapV=sample.Texture.wrapModeV.ToString(),view=view,mean=mean,max=maximum,missing=missingDiff,wrong=wrongDiff});
        Check(mean<=.0015f && maximum<=.012f && missingDiff>.08f && wrongDiff>.08f,
            "Vulkan lilToon parity/locked-key contract "+sample.Name+" view "+view+" mean="+mean+" max="+maximum+" missing="+missingDiff+" wrong="+wrongDiff);
    }
    static void VisualAndBundles()
    {
        var mesh=Quad(); var obj=new GameObject("OwnedTextureQuad"); obj.layer=29; obj.AddComponent<MeshFilter>().sharedMesh=mesh;
        var renderer=obj.AddComponent<MeshRenderer>(); var cameraObject=new GameObject("OwnedTextureCamera"); var camera=cameraObject.AddComponent<Camera>();
        camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=Color.black; camera.orthographic=true; camera.orthographicSize=1;
        camera.transform.position=new Vector3(0,0,-3); camera.transform.LookAt(Vector3.zero); camera.cullingMask=1<<29; camera.allowMSAA=false; camera.allowHDR=false;
        var samples=new List<Case>(); var beforeShaders=Snapshot("Packages/jp.lilxyzw.liltoon/Shader");
        var oldApi=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneLinux64); bool oldAuto=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64);
        try
        {
            int index=0;
            foreach(bool srgb in new[]{true,false}) foreach(var filter in new[]{FilterMode.Point,FilterMode.Bilinear})
            foreach(var u in new[]{TextureWrapMode.Repeat,TextureWrapMode.Clamp}) foreach(var v in new[]{TextureWrapMode.Repeat,TextureWrapMode.Clamp})
            {
                string name="case-"+index++; var texture=Source(name+"-source",srgb,filter,u,v); var source=SourceMaterial(texture,name+"-material");
                Check(texture.isDataSRGB==srgb,"fixture authored texture color space matches the requested test "+name);
                var key=new[]{73,141,219,38}; string folder="Assets/LinuxAvatarGuardGenerated/Research/texture-"+name;
                if (Directory.Exists(folder)) AssetDatabase.DeleteAsset(folder);
                GuardTextureArtifact artifact;
                using(var codec=new TextureGuardCodecV1(Seed(name),name)) artifact=GuardTextureForge.Prepare(source,codec,folder,key);
                Check(artifact.Material.GetTexture("_MainTex")==artifact.Texture && Enumerable.Range(0,4).All(i=>artifact.Material.GetFloat(GuardShaders.Property(i))==0),"generated material contains payload and only zero runtime values "+name);
                Check(artifact.ShaderAssetPaths.All(path=> {
                    string text=File.ReadAllText(path);
                    return Regex.Matches(text,@"(?m)^[ \t]*#include\s+""[^""]*/lil_common\.hlsl""").Count==Regex.Matches(text,"#ifndef LAG_TEXTURE_FUNCTIONS_INCLUDED").Count;
                }),"decoder hooks occur only after active common includes, preserving the commented lilToon workaround "+name);
                var sample=new Case {Source=source,Texture=texture,Artifact=artifact,Key=key,Name=name}; samples.Add(sample);
                var before=Snapshot(Folder);
                for(int view=0;view<3;view++)
                {
                    // View 0 spans full tiles; views 1/2 exercise negative UV, wrap borders, flips and non-integer tiling.
                    var scale=view==0?Vector2.one:view==1?new Vector2(2.25f,1.75f):new Vector2(-1.25f,.7f);
                    var offset=view==0?Vector2.zero:view==1?new Vector2(-.63f,-.38f):new Vector2(.97f,.89f);
                    source.SetTextureScale("_MainTex",scale); source.SetTextureOffset("_MainTex",offset);
                    artifact.Material.SetTextureScale("_MainTex",scale); artifact.Material.SetTextureOffset("_MainTex",offset);
                    Compare(sample,artifact.Material,renderer,camera,view,output+"/"+name+"-view-"+view);
                }
                source.SetTextureScale("_MainTex",Vector2.one); source.SetTextureOffset("_MainTex",Vector2.zero);
                artifact.Material.SetTextureScale("_MainTex",Vector2.one); artifact.Material.SetTextureOffset("_MainTex",Vector2.zero);
                AssetDatabase.SaveAssetIfDirty(source); AssetDatabase.SaveAssetIfDirty(artifact.Material);
                Check(Unchanged(before),"source textures/materials unchanged after all views "+name);
                // One provider family per material; no runtime component needed in the prefab.
                renderer.SetPropertyBlock(null); renderer.sharedMaterial=artifact.Material;
                PrefabUtility.SaveAsPrefabAsset(obj,folder+"/owned.prefab");
            }
            renderer.SetPropertyBlock(null);
            var assets=samples.SelectMany(s=>new[]{s.Artifact.Folder+"/owned.prefab",s.Artifact.Folder+"/public-manifest.json",AssetDatabase.GetAssetPath(s.Artifact.Material),AssetDatabase.GetAssetPath(s.Artifact.Texture)}.Concat(s.Artifact.ShaderAssetPaths)).Distinct().ToArray();
            var dependencies=AssetDatabase.GetDependencies(assets,true);
            Check(samples.All(s=>!dependencies.Contains(AssetDatabase.GetAssetPath(s.Texture))&&!dependencies.Contains(AssetDatabase.GetAssetPath(s.Source))),"complete authoring dependency graph excludes original albedos/source materials");
            var build=new[]{new AssetBundleBuild {assetBundleName="texture-pilot.bundle",assetNames=assets}};
            string linux=output+"/linux-bundle",windows=output+"/windows-bundle"; Directory.CreateDirectory(linux); Directory.CreateDirectory(windows);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,false); PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,new[]{GraphicsDeviceType.Vulkan}); LAGDiversityVariantFilter.Enabled=true;
            var manifest=BuildPipeline.BuildAssetBundles(linux,build,BuildAssetBundleOptions.ForceRebuildAssetBundle|BuildAssetBundleOptions.StrictMode,BuildTarget.StandaloneLinux64);
            Check(manifest,"native Linux Vulkan texture bundle builds");
            Check(samples.SelectMany(s=>s.Artifact.ShaderAssetPaths).All(path=>!ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(path))),"all facade/provider shaders compile without errors during the native build");
            var bundle=AssetBundle.LoadFromFile(Path.GetFullPath(linux+"/texture-pilot.bundle"));
            try
            {
                Check(bundle,"native texture bundle reopens"); var names=bundle.GetAllAssetNames();
                Check(samples.All(s=>!names.Contains(AssetDatabase.GetAssetPath(s.Texture).ToLowerInvariant())&&!names.Contains(AssetDatabase.GetAssetPath(s.Source).ToLowerInvariant())),"bundle excludes every original albedo and source material");
                var textures=bundle.LoadAllAssets<Texture2D>();
                Check(textures.Length==16&&textures.All(t=>!t.isReadable&&t.mipmapCount==1&&!GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat)),"bundle has sixteen encoded linear unreadable textures and no plaintext copies");
                foreach(var sample in samples)
                {
                    foreach(string path in sample.Artifact.ProviderAssetPaths) Check(bundle.LoadAsset<Shader>(path.ToLowerInvariant()),"native provider explicitly loads "+sample.Name);
                    var material=bundle.LoadAsset<Material>((sample.Artifact.Folder+"/material.mat").ToLowerInvariant());
                    var prefab=bundle.LoadAsset<GameObject>((sample.Artifact.Folder+"/owned.prefab").ToLowerInvariant());
                    Check(prefab&&prefab.GetComponent<MeshRenderer>().sharedMaterial==material&&textures.Contains(material.GetTexture("_MainTex")),"native prefab binds its encoded payload material "+sample.Name);
                    Check(material&&material.shader.isSupported&&Enumerable.Range(0,4).All(i=>material.GetFloat(GuardShaders.Property(i))==0),"native material supports Vulkan with serialized runtime values zero "+sample.Name);
                    Compare(sample,material,renderer,camera,0,output+"/native-"+sample.Name);
                    renderer.SetPropertyBlock(null); renderer.sharedMaterial=null;
                }
            }
            finally { if (bundle) bundle.Unload(true); }
            var windowsManifest=BuildPipeline.BuildAssetBundles(windows,build,BuildAssetBundleOptions.ForceRebuildAssetBundle|BuildAssetBundleOptions.StrictMode,BuildTarget.StandaloneWindows64);
            Check(windowsManifest,"Windows64 texture bundle compiles; D3D/client runtime not claimed");
            Check(samples.SelectMany(s=>s.Artifact.ShaderAssetPaths).All(path=>!ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(path))),"all facade/provider shaders compile without errors during Windows64 build");
            Check(Unchanged(beforeShaders),"all installed lilToon shader/include files remain byte-identical");
            File.WriteAllText(output+"/resources.json",JsonUtility.ToJson(new Resources {encodedTextures=16,encodedBytes=16*64*64*4,linuxBundleBytes=new FileInfo(linux+"/texture-pilot.bundle").Length,
                windowsBundleBytes=new FileInfo(windows+"/texture-pilot.bundle").Length,texelReadsPoint=1,texelReadsBilinear=4},true));
        }
        finally { LAGDiversityVariantFilter.Enabled=false; PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64,oldAuto); PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64,oldApi); Object.DestroyImmediate(obj); Object.DestroyImmediate(cameraObject); }
    }
    [Serializable] sealed class Resources {public int encodedTextures,encodedBytes,texelReadsPoint,texelReadsBilinear; public long linuxBundleBytes,windowsBundleBytes;}
    [Serializable] sealed class Report
    {
        public string[] checks; public string unity,graphics,gpu,colorSpace,scope;
        public int uniquePrograms,adaptiveCases,maxByteError,images,visualComparisons;
        public float maxMeanDifference,maxChannelDifference,minMissingDifference,minWrongDifference;
        public bool sourcePreserved=true,vrchatAnalyzed=false,productionIntegrated=false;
        public Visual[] visuals;
    }
    public static void Run()
    {
        LAGBindingValidation.RequireResearchProject(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        checks.Clear(); visuals.Clear(); uniquePrograms=adaptiveCases=maxPixelError=imageCount=0; maxMean=maxChannel=0; minMissing=minWrong=float.PositiveInfinity;
        output="../evidence/texture-guard/"+QualitySettings.activeColorSpace.ToString().ToLowerInvariant(); Directory.CreateDirectory(output);
        if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder); AssetDatabase.CreateFolder("Assets","TextureFixture");
        CodecChecks(); NegativeChecks(); VisualAndBundles();
        File.WriteAllText(output+"/validation.json",JsonUtility.ToJson(new Report {checks=checks.ToArray(),unity=Application.unityVersion,graphics=SystemInfo.graphicsDeviceType.ToString(),gpu=SystemInfo.graphicsDeviceName,
            colorSpace=QualitySettings.activeColorSpace.ToString(),scope="Owned opaque uncompressed single-mip albedo; mono unlit lilToon 2.3.4; not a VRChat/SDK avatar test",
            uniquePrograms=uniquePrograms,adaptiveCases=adaptiveCases,maxByteError=maxPixelError,images=imageCount+3,visualComparisons=visuals.Count,maxMeanDifference=maxMean,maxChannelDifference=maxChannel,
            minMissingDifference=minMissing,minWrongDifference=minWrong,visuals=visuals.ToArray()},true));
        Debug.Log("LAG_TEXTURE_VALIDATION_SUCCESS "+checks.Count+" checks; "+QualitySettings.activeColorSpace+"; "+visuals.Count+" visual comparisons; "+(imageCount+3)+" images");
    }
    public static void RunRegression()
    {
        LAGShaderForgeValidation.Run(); LAGAttributeValidation.Run(); LAGBindingValidation.Run(); LAGStaticPrototypeValidation.Run();
#if LAG_VRCSDK
        LAGImplementationValidation.Run();
#endif
        Debug.Log("LAG_TEXTURE_REGRESSION_SUCCESS");
    }
    public static void InspectOwnBundle()
    {
        LAGBindingValidation.RequireResearchProject();
        string root="../evidence/texture-guard/"+QualitySettings.activeColorSpace.ToString().ToLowerInvariant();
        var bundle=AssetBundle.LoadFromFile(Path.GetFullPath(root+"/linux-bundle/texture-pilot.bundle"));
        try
        {
            var textures=bundle.LoadAllAssets<Texture2D>();
            File.WriteAllLines(root+"/texture-inventory.txt",textures.Select(t=>t.name+" "+t.width+"x"+t.height+" "+t.graphicsFormat+" readable="+t.isReadable+" mips="+t.mipmapCount));
            Debug.Log("LAG_TEXTURE_OWN_BUNDLE_INVENTORY "+textures.Length+" textures");
        }
        finally { bundle.Unload(true); }
    }
}
public sealed class LAGTextureFaultPostprocessor : AssetPostprocessor
{
    public static Material Material;
    public static string Prefix;
    public static bool Touched;
    public static bool TamperGeneratedKey;
    static void OnPostprocessAllAssets(string[] imported,string[] deleted,string[] moved,string[] previous)
    {
        if (!Material || Touched || Environment.GetEnvironmentVariable("LAG_BINDING_AUDIT_ALLOWED") != "synthetic-unity-only") return;
        if (TamperGeneratedKey)
        {
            var path=imported.FirstOrDefault(p=>p.StartsWith(Prefix,StringComparison.Ordinal)&&p.EndsWith("/material.mat",StringComparison.Ordinal));
            if(path!=null) { Touched=true; var generated=AssetDatabase.LoadAssetAtPath<Material>(path); generated.SetFloat("_LAGKey0",17); EditorUtility.SetDirty(generated); }
        }
        else if (imported.Any(p=>p.StartsWith(Prefix,StringComparison.Ordinal)&&p.EndsWith(".asset",StringComparison.Ordinal)))
        { Touched=true; Material.SetTextureScale("_MainTex",new Vector2(2,2)); EditorUtility.SetDirty(Material); }
    }
}
