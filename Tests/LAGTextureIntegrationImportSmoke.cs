// SPDX-License-Identifier: MIT
// Import/API smoke only. Contextual texture reproduction requires the reviewed lilToon sources.
using System;
using System.IO;
using System.Linq;
using LinuxAvatarGuard;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class LAGTextureIntegrationImportSmoke
{
    public static void Run()
    {
        if(Environment.GetEnvironmentVariable("LAG_TEXTURE_IMPORT_ALLOWED")!="owned-api-smoke"||
            Path.GetFileName(Path.GetDirectoryName(Application.dataPath))!="ImportValidationProject"||
            Application.platform!=RuntimePlatform.LinuxEditor||SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Vulkan)
            throw new InvalidOperationException("Only the explicitly enabled disposable SDK-free import project is allowed.");
        if(typeof(GuardWindow).Assembly.GetType("LinuxAvatarGuard.GuardBuilder")!=null||Shader.Find("lilToon")||
            GuardBuildContext.SchemaVersion!=5||GuardShaderForge.Version!=3||GuardTextureBindingIdentity.SchemaVersion!=1)
            throw new Exception("Unexpected SDK/lilToon or unavailable contextual API versions.");
        const string folder="Assets/TextureIntegrationApiSmoke";
        string old=Environment.GetEnvironmentVariable("XDG_DATA_HOME"),scratch=Path.Combine(Path.GetTempPath(),"lag-texture-import-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);Environment.SetEnvironmentVariable("XDG_DATA_HOME",scratch);
        var root=GameObject.CreatePrimitive(PrimitiveType.Sphere);
        try
        {
            if(AssetDatabase.IsValidFolder(folder))AssetDatabase.DeleteAsset(folder);AssetDatabase.CreateFolder("Assets","TextureIntegrationApiSmoke");
            var mesh=Object.Instantiate(root.GetComponent<MeshFilter>().sharedMesh);AssetDatabase.CreateAsset(mesh,folder+"/mesh.asset");root.GetComponent<MeshFilter>().sharedMesh=mesh;
            var binding=MeshBindingIdentity.Capture(root,root.GetComponent<Renderer>());bool reproduced=false;
            for(int candidate=0;candidate<256&&!reproduced;candidate++)using(var context=GuardBuildContext.CreateRandom())using(var codec=context.CreateCodec(binding))
            {
                CodecPlan plan;
                try{plan=codec.Plan(mesh,.1f);}
                catch(InvalidOperationException e)when(e.Message.Contains("cancela sus offsets keyed")||e.Message.Contains("no supera el error de reconstrucción sin clave")){continue;}
                var encoded=codec.Encode(mesh,plan,context.RuntimeKey());
                try
                {
                    context.RecordPlan(binding,plan);string path=context.SavePrivate(),json=File.ReadAllText(path);
                    foreach(int schema in new[]{1,2,3,4,5})
                    {
                        File.WriteAllText(path,json.Replace("\"schemaVersion\": 5","\"schemaVersion\": "+schema));
                        using(var restored=GuardBuildContext.LoadPrivate(context.BuildId))using(var replay=restored.CreateCodec(binding))
                        {
                            var copy=replay.Encode(mesh,replay.Plan(mesh,.1f),restored.RuntimeKey());
                            try{if(!copy.vertices.SequenceEqual(encoded.vertices)||restored.TextureBindingCount!=0)throw new Exception("Historical private mesh reproduction failed.");}
                            finally{Object.DestroyImmediate(copy);}
                        }
                    }
                    reproduced=true;
                }
                finally{Object.DestroyImmediate(encoded);}
            }
            if(!reproduced)throw new Exception("No admissible own mesh context for SDK-free smoke.");
            var source=new Texture2D(16,16,TextureFormat.RGBA32,false,false){filterMode=FilterMode.Point,anisoLevel=0};
            source.SetPixels32(Enumerable.Range(0,256).Select(i=>new Color32((byte)i,(byte)(255-i),71,255)).ToArray());source.Apply(false);AssetDatabase.CreateAsset(source,folder+"/texture.asset");AssetDatabase.SaveAssetIfDirty(source);
            using(var codec=new TextureGuardCodecV1(new byte[32],"owned-api-smoke"))
            {
                var plan=codec.Plan(source);var encoded=codec.Encode(source,plan,new[]{31,62,93,124});
                try{if(encoded.isReadable||plan.BindingStableId!=null||!codec.EmitDecoder(plan).Functions.Contains("_MainTex.Load"))throw new Exception("Standalone texture API changed unexpectedly.");}
                finally{Object.DestroyImmediate(encoded);}
            }
            var mipSource=new Texture2D(16,16,TextureFormat.RGBA32,true,false){filterMode=FilterMode.Trilinear,anisoLevel=0};
            for(int level=0;level<mipSource.mipmapCount;level++){int size=Math.Max(1,16>>level);mipSource.SetPixels32(Enumerable.Repeat(new Color32((byte)(level*41),123,211,255),size*size).ToArray(),level);}mipSource.Apply(false);AssetDatabase.CreateAsset(mipSource,folder+"/mips.asset");AssetDatabase.SaveAssetIfDirty(mipSource);
            using(var codec=new TextureGuardCodecV2(new byte[32],"owned-mip-api-smoke"))
            {var plan=codec.Plan(mipSource);var encoded=codec.Encode(mipSource,plan,new[]{0,62,93,124});try{if(encoded.mipmapCount!=5||encoded.isReadable||!encoded.ignoreMipmapLimit||plan.Program.Mips[4].Grid!=1||!codec.EmitDecoder(plan).Functions.Contains("CalculateLevelOfDetail"))throw new Exception("Mip API contract failed.");}finally{Object.DestroyImmediate(encoded);}}
            Directory.CreateDirectory("../evidence/mip-skin");
            File.WriteAllText("../evidence/mip-skin/sdk-free.json","{\"compiledWithoutSdkOrLilToon\":true,\"schemasReproduced\":[1,2,3,4,5],\"privateMeshReproduction\":true,\"standaloneTextureApiExecuted\":true,\"textureV2ApiExecuted\":true,\"skinningApiAvailable\":true,\"contextualTextureApiAvailable\":true,\"contextualTextureForgeExecuted\":false,\"graphics\":\"Vulkan\"}");
            Debug.Log("LAG_TEXTURE_INTEGRATION_SDK_FREE_SUCCESS schemas 1/2/3/4/5 and texture V1/V2 APIs");
        }
        finally{Object.DestroyImmediate(root);if(AssetDatabase.IsValidFolder(folder))AssetDatabase.DeleteAsset(folder);Environment.SetEnvironmentVariable("XDG_DATA_HOME",old);Directory.Delete(scratch,true);}
    }
}
