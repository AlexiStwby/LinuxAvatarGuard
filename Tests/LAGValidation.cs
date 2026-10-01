#if UNITY_EDITOR && LAG_VRCSDK
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using LinuxAvatarGuard;
using Object = UnityEngine.Object;

public static class LAGValidation
{
    static readonly List<string> checks = new List<string>();
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); checks.Add(name); }
    static string Hash(string path) { using (var h = SHA256.Create()) return Convert.ToBase64String(h.ComputeHash(File.ReadAllBytes(path))); }
    static GameObject Fixture(bool skinned)
    {
        var root = new GameObject(skinned ? "SkinFixture" : "StaticFixture");
        root.AddComponent<Animator>();
        var d = root.AddComponent<VRCAvatarDescriptor>();
        d.customizeAnimationLayers = true; d.customExpressions = true;
        d.baseAnimationLayers = new[] {new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = false, animatorController = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/Fixture/FX.controller") }};
        d.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
        d.expressionParameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>("Assets/Fixture/Parameters.asset");
        var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Fixture/mesh.asset");
        Renderer r;
        if (skinned)
        {
            var bone = new GameObject("Bone"); bone.transform.SetParent(root.transform, false);
            var m = Object.Instantiate(mesh); m.name = "SkinnedSphere";
            m.bindposes = new[] {Matrix4x4.identity};
            m.boneWeights = Enumerable.Range(0, m.vertexCount).Select(i => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
            AssetDatabase.CreateAsset(m, "Assets/Fixture/skinmesh.asset");
            var s = body.AddComponent<SkinnedMeshRenderer>(); s.sharedMesh = m; s.bones = new[] {bone.transform}; s.rootBone = bone.transform; s.localBounds = m.bounds;
            r = s;
        }
        else
        {
            body.AddComponent<MeshFilter>().sharedMesh = mesh; r = body.AddComponent<MeshRenderer>();
        }
        r.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Fixture/original.mat");
        return root;
    }
    static Color[] Render(GameObject avatar, string name)
    {
        var cameraObject = new GameObject("ValidationCamera");
        var camera = cameraObject.AddComponent<Camera>();
        camera.transform.position = new Vector3(0, 0, -2);
        camera.transform.LookAt(Vector3.zero);
        camera.orthographic = true; camera.orthographicSize = 0.9f;
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.05f, 0.07f, 0.1f, 1);
        var target = new RenderTexture(192, 192, 24, RenderTextureFormat.ARGB32);
        camera.targetTexture = target;
        camera.Render();
        var previous = RenderTexture.active; RenderTexture.active = target;
        var image = new Texture2D(192, 192, TextureFormat.RGBA32, false);
        image.ReadPixels(new Rect(0,0,192,192),0,0); image.Apply();
        Directory.CreateDirectory("../evidence"); File.WriteAllBytes("../evidence/"+name+".png", image.EncodeToPNG());
        var pixels = image.GetPixels();
        RenderTexture.active = previous; camera.targetTexture = null;
        Object.DestroyImmediate(image); Object.DestroyImmediate(target); Object.DestroyImmediate(cameraObject);
        return pixels;
    }
    static float Difference(Color[] a, Color[] b) => a.Zip(b, (x,y) => Mathf.Abs(x.r-y.r)+Mathf.Abs(x.g-y.g)+Mathf.Abs(x.b-y.b)).Sum()/(a.Length*3);
    static void SetKey(GameObject root, int[] key)
    {
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
                for (int i=0;i<4;i++) m.SetFloat(GuardShaders.Property(i),key[i]);
    }
    public static void Run()
    {
        try
        {
            GuardBuilder.RequireEnvironment();
            Check(Shader.Find("lilToon") != null && !ShaderUtil.ShaderHasError(Shader.Find("lilToon")), "lilToon baseline resolves");
            if (AssetDatabase.IsValidFolder("Assets/Fixture")) AssetDatabase.DeleteAsset("Assets/Fixture");
            AssetDatabase.CreateFolder("Assets", "Fixture");
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var mesh = Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh); Object.DestroyImmediate(primitive);
            // A position-only blendshape is preserved and supported.
            mesh.AddBlendShapeFrame("PositionOnly",100,mesh.vertices.Select(p=>p*0.04f).ToArray(),new Vector3[mesh.vertexCount],new Vector3[mesh.vertexCount]);
            AssetDatabase.CreateAsset(mesh,"Assets/Fixture/mesh.asset");
            var originalMaterial = new Material(Shader.Find("lilToon")); originalMaterial.SetFloat("_AsUnlit",1); originalMaterial.SetColor("_Color",new Color(.7f,.25f,.5f,1));
            AssetDatabase.CreateAsset(originalMaterial,"Assets/Fixture/original.mat");
            var swapMaterial = new Material(originalMaterial); swapMaterial.SetColor("_Color",Color.cyan); AssetDatabase.CreateAsset(swapMaterial,"Assets/Fixture/swap.mat");
            var clip = new AnimationClip();
            AnimationUtility.SetObjectReferenceCurve(clip,EditorCurveBinding.PPtrCurve("Body",typeof(MeshRenderer),"m_Materials.Array.data[0]"),new[]{new ObjectReferenceKeyframe{time=0,value=swapMaterial}});
            AssetDatabase.CreateAsset(clip,"Assets/Fixture/Swap.anim");
            var fx=AnimatorController.CreateAnimatorControllerAtPath("Assets/Fixture/FX.controller");
            fx.AddParameter("ExistingToggle", AnimatorControllerParameterType.Bool); fx.layers[0].stateMachine.AddState("Existing").motion=clip;
            var expressions=ScriptableObject.CreateInstance<VRCExpressionParameters>(); expressions.parameters=new[]{new VRCExpressionParameters.Parameter{name="ExistingToggle",valueType=VRCExpressionParameters.ValueType.Bool,saved=true,networkSynced=true}};
            AssetDatabase.CreateAsset(expressions,"Assets/Fixture/Parameters.asset");
            AssetDatabase.SaveAssets();
            var hashes=Directory.GetFiles("Assets/Fixture").ToDictionary(p=>p,Hash);
            var source=Fixture(false); var before=EditorJsonUtility.ToJson(source.GetComponent<VRCAvatarDescriptor>());
            var build=GuardBuilder.Build(source,0.15f,false);
            Check(hashes.All(p=>Hash(p.Key)==p.Value),"all original dependency files byte-identical after build");
            Check(EditorJsonUtility.ToJson(source.GetComponent<VRCAvatarDescriptor>())==before,"original descriptor untouched");
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(build.prefabPath);
            var protectedAvatar=Object.Instantiate(prefab); source.SetActive(false);
            var protectedMesh=protectedAvatar.GetComponentInChildren<MeshFilter>().sharedMesh;
            Check(protectedMesh!=mesh && protectedMesh.vertexCount==mesh.vertexCount,"independent encoded mesh preserves vertex count");
            Check(protectedMesh.triangles.SequenceEqual(mesh.triangles) && protectedMesh.normals.SequenceEqual(mesh.normals) && protectedMesh.tangents.SequenceEqual(mesh.tangents),"topology normals and tangents preserved");
            Check(protectedMesh.blendShapeCount==1 && protectedMesh.GetBlendShapeName(0)=="PositionOnly","blendshapes preserved");
            var keyFile=JsonUtility.FromJson<GuardKeyFile>(File.ReadAllText(build.keyPath));
            Check(!build.keyPath.Contains("/Assets/") && !build.keyPath.Contains("/Packages/"),"key outside exported assets");
            var d=protectedAvatar.GetComponent<VRCAvatarDescriptor>();
            Check(d.expressionParameters!=expressions && d.expressionParameters.CalcTotalCost()==33,"original expression parameters retained plus 32 bits");
            Check(d.expressionParameters.parameters.Skip(1).All(p=>p.defaultValue==0&&!p.saved&&p.networkSynced),"keys absent from defaults and saved state");
            var generatedFx=(AnimatorController)d.baseAnimationLayers[0].animatorController;
            Check(generatedFx!=fx && generatedFx.layers.Length==fx.layers.Length+4,"independent FX controller retains existing layers");
            Check(generatedFx.layers.Skip(1).All(l=>l.stateMachine.states.Length==256&&l.defaultWeight==1),"four byte drivers generated for all values");
            var mappedClip=(AnimationClip)generatedFx.layers[0].stateMachine.states[0].state.motion;
            var mappedSwap=(Material)AnimationUtility.GetObjectReferenceCurve(mappedClip,AnimationUtility.GetObjectReferenceCurveBindings(mappedClip)[0])[0].value;
            Check(mappedClip!=clip&&mappedSwap!=swapMaterial&&mappedSwap.shader.name.StartsWith("LinuxAvatarGuard/"),"animated material swaps remapped on copied clip");
            Check(generatedFx.layers.Skip(1).All(l=>l.stateMachine.defaultState.motion is AnimationClip ac && AnimationUtility.GetEditorCurve(ac,AnimationUtility.GetCurveBindings(ac)[0]).keys[0].value==0),"all key drivers default to locked");
            // Shader compilation and render checks validate real Vulkan, not only C# arithmetic.
            foreach(var material in protectedAvatar.GetComponentsInChildren<Renderer>().SelectMany(r=>r.sharedMaterials))
                for(int pass=0;pass<material.passCount;pass++) ShaderUtil.CompilePass(material,pass,true);
            source.SetActive(true); protectedAvatar.SetActive(false); var original=Render(source,"original");
            source.SetActive(false); protectedAvatar.SetActive(true); var locked=Render(protectedAvatar,"locked");
            SetKey(protectedAvatar,keyFile.keys); var unlocked=Render(protectedAvatar,"unlocked");
            float lockedDifference=Difference(original,locked), unlockedDifference=Difference(original,unlocked);
            Check(lockedDifference>0.01f,"locked mesh visibly differs from original");
            Check(unlockedDifference<0.001f,"Vulkan unlocked static render matches original");
            Check(AssetDatabase.GetAllAssetPaths().Where(p=>p.StartsWith(Path.GetDirectoryName(build.prefabPath)+"/Shaders/")&&p.EndsWith(".shader")).All(p=>!ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(p))),"generated main and UsePass shaders have no compilation errors");
            // Evaluate the real FX controller, not just shader uniforms.
            SetKey(protectedAvatar,new[]{0,0,0,0});
            var animated=protectedAvatar.GetComponent<Animator>(); animated.runtimeAnimatorController=generatedFx;
            animated.cullingMode=AnimatorCullingMode.AlwaysAnimate; animated.Rebind();
            for(int i=0;i<4;i++) animated.SetInteger(keyFile.parameters[i],keyFile.keys[i]);
            animated.Update(.1f); animated.Update(.1f);
            var block=new MaterialPropertyBlock(); var animatedRenderer=protectedAvatar.GetComponentInChildren<Renderer>(); animatedRenderer.GetPropertyBlock(block);
            Check(Enumerable.Range(0,4).All(i=>Mathf.Abs(block.GetFloat(GuardShaders.Property(i))-keyFile.keys[i])<.01f || Mathf.Abs(animatedRenderer.sharedMaterial.GetFloat(GuardShaders.Property(i))-keyFile.keys[i])<.01f),"FX integer parameters drive real renderer keys");
            protectedAvatar.SetActive(false); source.SetActive(true);source.GetComponentInChildren<Renderer>().sharedMaterial=swapMaterial; var animatedOriginal=Render(source,"fx-original");
            source.SetActive(false);protectedAvatar.SetActive(true);for(int i=0;i<4;i++) animated.SetInteger(keyFile.parameters[i],keyFile.keys[i]);animated.Update(.1f);animated.Update(.1f);var animatedUnlocked=Render(protectedAvatar,"fx-unlocked");
            Check(Difference(animatedOriginal,animatedUnlocked)<.001f,"FX unlock and animated material swap render correctly");
            Object.DestroyImmediate(protectedAvatar); Object.DestroyImmediate(source);
            // Safety rejection tests.
            var key=new[]{100,150,200,250};
            var occupied=Object.Instantiate(mesh); occupied.SetUVs(6,Enumerable.Repeat(Vector2.one,occupied.vertexCount).ToList());
            bool rejected=false; try{GuardMesh.Encode(occupied,key,.1f);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"occupied protection UV channel rejected"); Object.DestroyImmediate(occupied);
            var normalsBlend=Object.Instantiate(mesh); normalsBlend.AddBlendShapeFrame("ChangingNormals",100,new Vector3[mesh.vertexCount],Enumerable.Repeat(Vector3.up*.01f,mesh.vertexCount).ToArray(),new Vector3[mesh.vertexCount]);
            rejected=false; try{GuardMesh.Encode(normalsBlend,key,.1f);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"normal-changing blendshape rejected without stripping"); Object.DestroyImmediate(normalsBlend);
            var skinned=Fixture(true);
            rejected=false;try{GuardBuilder.Validate(skinned,false);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"skinned meshes require explicit experimental opt-in");
            var skinBuild=GuardBuilder.Build(skinned,.1f,true); var skinCopy=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(skinBuild.prefabPath));
            var skinKey=JsonUtility.FromJson<GuardKeyFile>(File.ReadAllText(skinBuild.keyPath)).keys;
            skinned.transform.Find("Bone").localRotation=Quaternion.Euler(15,30,10);skinCopy.transform.Find("Bone").localRotation=Quaternion.Euler(15,30,10);
            skinned.GetComponentInChildren<SkinnedMeshRenderer>().SetBlendShapeWeight(0,75); skinCopy.GetComponentInChildren<SkinnedMeshRenderer>().SetBlendShapeWeight(0,75);
            skinCopy.SetActive(false);var skinOriginal=Render(skinned,"skin-original");skinned.SetActive(false);skinCopy.SetActive(true);SetKey(skinCopy,skinKey);var skinUnlocked=Render(skinCopy,"skin-unlocked");
            float skinDifference=Difference(skinOriginal,skinUnlocked);
            Check(skinDifference<.001f,"single-bone posed skinned render with position-only blendshape matches");
            // Add a second bone with mixed weights; pose both and measure actual GPU skinning.
            var skinRenderer=skinned.GetComponentInChildren<SkinnedMeshRenderer>();
            var second=new GameObject("Bone2");second.transform.SetParent(skinned.transform,false);
            var secondCopy=new GameObject("Bone2");secondCopy.transform.SetParent(skinCopy.transform,false);
            var originalSkin=Object.Instantiate(skinRenderer.sharedMesh);var encodedSkin=Object.Instantiate(skinCopy.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh);
            var weights=originalSkin.vertices.Select(p=>{float w=Mathf.Clamp01(.5f+p.y*.7f);return new BoneWeight{boneIndex0=0,weight0=w,boneIndex1=1,weight1=1-w};}).ToArray();
            originalSkin.bindposes=new[]{Matrix4x4.identity,Matrix4x4.identity};encodedSkin.bindposes=originalSkin.bindposes;
            originalSkin.boneWeights=weights;encodedSkin.boneWeights=weights;
            skinRenderer.sharedMesh=originalSkin;skinRenderer.bones=new[]{skinned.transform.Find("Bone"),second.transform};
            var encodedRenderer=skinCopy.GetComponentInChildren<SkinnedMeshRenderer>();encodedRenderer.sharedMesh=encodedSkin;encodedRenderer.bones=new[]{skinCopy.transform.Find("Bone"),secondCopy.transform};
            second.transform.localRotation=Quaternion.Euler(-35,10,25);secondCopy.transform.localRotation=second.transform.localRotation;
            second.transform.localPosition=new Vector3(.1f,.05f,0);secondCopy.transform.localPosition=second.transform.localPosition;
            skinRenderer.SetBlendShapeWeight(0,75);encodedRenderer.SetBlendShapeWeight(0,75);
            skinCopy.SetActive(false);skinned.SetActive(true);var multiOriginal=Render(skinned,"multi-original");skinned.SetActive(false);skinCopy.SetActive(true);var multiUnlocked=Render(skinCopy,"multi-unlocked");
            float multiDifference=Difference(multiOriginal,multiUnlocked);
            Check(multiDifference<.001f,"two-bone mixed-weight posed render matches within tolerance");
            Object.DestroyImmediate(skinned);Object.DestroyImmediate(skinCopy);Object.DestroyImmediate(originalSkin);Object.DestroyImmediate(encodedSkin);
            var defaultRoot=Fixture(false);var defaultDescriptor=defaultRoot.GetComponent<VRCAvatarDescriptor>();defaultDescriptor.customExpressions=false;defaultDescriptor.expressionParameters=null;
            var defaultBuild=GuardBuilder.Build(defaultRoot,.1f,false);var defaultCopy=AssetDatabase.LoadAssetAtPath<GameObject>(defaultBuild.prefabPath).GetComponent<VRCAvatarDescriptor>();
            Check(defaultCopy.customExpressions&&defaultCopy.expressionsMenu!=null,"default-expression avatars expose custom key parameters and menu");
            Check(new[]{"VRCEmote","VRCFaceBlendH","VRCFaceBlendV"}.All(n=>defaultCopy.expressionParameters.parameters.Any(p=>p.name==n)),"SDK default expression parameters preserved");
            Check(!defaultDescriptor.customExpressions&&defaultDescriptor.expressionParameters==null,"default-expression original descriptor remains untouched");Object.DestroyImmediate(defaultRoot);
            File.WriteAllText("../evidence/validation.json",JsonUtility.ToJson(new Report { unity=Application.unityVersion,graphics=SystemInfo.graphicsDeviceType.ToString(),gpu=SystemInfo.graphicsDeviceName,checks=checks.ToArray(),lockedDifference=lockedDifference,unlockedDifference=unlockedDifference,skinDifference=skinDifference,multiDifference=multiDifference},true));
            Debug.Log("LAG_VALIDATION_SUCCESS "+checks.Count+" checks");
        }
        catch(Exception e) { File.WriteAllText("../validation-failure.txt",e.ToString());throw; }
    }
    [Serializable] class Report {public string unity,graphics,gpu;public string[] checks; public float lockedDifference,unlockedDifference,skinDifference,multiDifference;}
}
#endif
