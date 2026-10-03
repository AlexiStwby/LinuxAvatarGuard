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
            GuardBuildContext.SchemaVersion!=4||GuardShaderForge.Version!=2||GuardTextureBindingIdentity.SchemaVersion!=1)
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
                    foreach(int schema in new[]{1,2,3,4})
                    {
                        File.WriteAllText(path,json.Replace("\"schemaVersion\": 4","\"schemaVersion\": "+schema));
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
            Directory.CreateDirectory("../evidence/texture-integration");
            File.WriteAllText("../evidence/texture-integration/sdk-free.json","{\"compiledWithoutSdkOrLilToon\":true,\"schemasReproduced\":[1,2,3,4],\"privateMeshReproduction\":true,\"standaloneTextureApiExecuted\":true,\"contextualTextureApiAvailable\":true,\"contextualTextureForgeExecuted\":false,\"graphics\":\"Vulkan\"}");
            Debug.Log("LAG_TEXTURE_INTEGRATION_SDK_FREE_SUCCESS schemas 1/2/3/4 and standalone texture API");
        }
        finally{Object.DestroyImmediate(root);if(AssetDatabase.IsValidFolder(folder))AssetDatabase.DeleteAsset(folder);Environment.SetEnvironmentVariable("XDG_DATA_HOME",old);Directory.Delete(scratch,true);}
    }
}
