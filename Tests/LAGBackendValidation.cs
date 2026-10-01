#if UNITY_EDITOR && LAG_VRCSDK
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using LinuxAvatarGuard;
using Object=UnityEngine.Object;
public static class LAGBackendValidation
{
    public static void Run()
    {
        GuardBuilder.RequireEnvironment();
        string folder="Assets/BackendValidation";
        if(AssetDatabase.IsValidFolder(folder))AssetDatabase.DeleteAsset(folder);
        AssetDatabase.CreateFolder("Assets","BackendValidation");
        string[] names={"lilToon","Hidden/lilToonOutline","Hidden/lilToonCutout","Hidden/lilToonCutoutOutline","Hidden/lilToonTransparent","Hidden/lilToonTransparentOutline","Hidden/lilToonOnePassTransparent","Hidden/lilToonOnePassTransparentOutline","Hidden/lilToonTwoPassTransparent","Hidden/lilToonTwoPassTransparentOutline"};
        var generator=new GuardShaders(folder,Guid.NewGuid().ToString("N"));
        foreach(string name in names)
        {
            var shader=generator.Copy(Shader.Find(name));
            var material=new Material(shader);
            for(int pass=0;pass<material.passCount;pass++)ShaderUtil.CompilePass(material,pass,true);
            Object.DestroyImmediate(material);
        }
        var shaderPaths=AssetDatabase.GetAllAssetPaths().Where(p=>p.StartsWith(folder+"/")&&p.EndsWith(".shader")).ToArray();
        foreach(string path in shaderPaths)
        {
            var shader=AssetDatabase.LoadAssetAtPath<Shader>(path);
            if(ShaderUtil.ShaderHasError(shader)) throw new Exception("Shader error: "+path+" "+string.Join(";",ShaderUtil.GetShaderMessages(shader).Select(m=>m.message)));
        }
        // Verify the platform VRChat actually accepts, while retaining Vulkan in the Editor.
        var report=Directory.GetFiles("Assets/LinuxAvatarGuardGenerated","build-report.json",SearchOption.AllDirectories).First(p=>File.ReadAllText(p).Contains("\"experimentalSkinning\":false"));
        string prefab=Directory.GetFiles(Path.GetDirectoryName(report),"*.prefab").Single();
        Directory.CreateDirectory("../evidence/windows-bundle");
        var result=BuildPipeline.BuildAssetBundles("../evidence/windows-bundle",new[]{new AssetBundleBuild{assetBundleName="lag-validation.bundle",assetNames=new[]{prefab}}},BuildAssetBundleOptions.ForceRebuildAssetBundle|BuildAssetBundleOptions.StrictMode,BuildTarget.StandaloneWindows64);
        if(!result)throw new Exception("Windows bundle build failed");
        File.WriteAllText("../evidence/backend-validation.json","{\"standardVariants\":10,\"generatedShaders\":"+shaderPaths.Length+",\"vulkanCompilation\":true,\"windowsBundleBuilt\":true}");
        Debug.Log("LAG_BACKEND_SUCCESS "+shaderPaths.Length+" shaders; Windows64 bundle built");
    }
}
#endif
