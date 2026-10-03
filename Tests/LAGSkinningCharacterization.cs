// SPDX-License-Identifier: MIT
// Owned test geometry only. Establish the skinning space/order before enabling a codec.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;
using LinuxAvatarGuard;

public static class LAGSkinningCharacterization
{
    const string Folder="Assets/SkinningCharacterFixture";
    public static Matrix4x4 PublicFrame => Matrix4x4.TRS(new Vector3(.37f,-.28f,.19f),Quaternion.Euler(31,67,-23),Vector3.one);
    public static Mesh Reframe(Mesh original,Matrix4x4 frame)
    {
        var copy=Object.Instantiate(original);
        copy.vertices=original.vertices.Select(frame.MultiplyPoint3x4).ToArray();
        copy.normals=original.normals.Select(frame.MultiplyVector).ToArray();
        copy.tangents=original.tangents.Select(t=>{var v=frame.MultiplyVector(new Vector3(t.x,t.y,t.z));return new Vector4(v.x,v.y,v.z,t.w);}).ToArray();
        copy.bindposes=original.bindposes.Select(b=>b*frame.inverse).ToArray();
        copy.ClearBlendShapes();
        var dp=new Vector3[original.vertexCount];var dn=new Vector3[original.vertexCount];var dt=new Vector3[original.vertexCount];
        for(int shape=0;shape<original.blendShapeCount;shape++)for(int level=0;level<original.GetBlendShapeFrameCount(shape);level++)
        {
            original.GetBlendShapeFrameVertices(shape,level,dp,dn,dt);
            copy.AddBlendShapeFrame(original.GetBlendShapeName(shape),original.GetBlendShapeFrameWeight(shape,level),dp.Select(frame.MultiplyVector).ToArray(),dn.Select(frame.MultiplyVector).ToArray(),dt.Select(frame.MultiplyVector).ToArray());
        }
        copy.RecalculateBounds();return copy;
    }
    public static Mesh Mesh(Transform renderer,Transform[] bones)
    {
        var mesh=LAGBindingValidation.Fixture();mesh.RecalculateTangents();
        mesh.boneWeights=mesh.vertices.Select(p=>{float w=Mathf.Clamp01((p.y+.65f)/1.3f);return new BoneWeight{boneIndex0=0,boneIndex1=1,weight0=1-w,weight1=w};}).ToArray();
        mesh.bindposes=bones.Select(b=>b.worldToLocalMatrix*renderer.localToWorldMatrix).ToArray();
        for(int shape=0;shape<3;shape++)for(int level=1;level<=2;level++)
        {
            float amount=level*.045f;
            var dv=mesh.vertices.Select((p,i)=>shape==0?new Vector3(amount*Mathf.Sin(p.y*3),amount*p.x,0):shape==1?new Vector3(0,0,amount*(p.y+.7f)):new Vector3(amount*p.x,0,-amount*Mathf.Cos(p.y*4))).ToArray();
            var dn=mesh.normals.Select(n=>new Vector3(n.z,n.x,-n.y)*amount*.6f).ToArray();
            var dt=mesh.normals.Select(n=>new Vector3(-n.y,n.z,n.x)*amount*.4f).ToArray();
            mesh.AddBlendShapeFrame(new[]{"Smile","LookLeft","VisemeAA"}[shape],level*50,dv,dn,dt);
        }
        return mesh;
    }
    public static void Pose(Transform[] bones,SkinnedMeshRenderer renderer,int index)
    {
        bones[0].localPosition=new Vector3(.1f*Mathf.Sin(index),-.2f,0);
        bones[1].localPosition=new Vector3(0,.45f+.05f*Mathf.Cos(index),.025f*index);
        bones[0].localRotation=Quaternion.Euler(index*13,index*-7,index*17);
        bones[1].localRotation=Quaternion.Euler(index*-19,index*23,index*-11);
        bones[0].localScale=index%3==2?new Vector3(1.15f,.85f,1.05f):Vector3.one;
        bones[1].localScale=index%3==1?new Vector3(.8f,1.2f,.95f):Vector3.one;
        for(int shape=0;shape<renderer.sharedMesh.blendShapeCount;shape++)renderer.SetBlendShapeWeight(shape,(index*(23+shape*11))%101);
    }
    public static Color[] Render(Camera camera,string path)
    {
        var rt=RenderTexture.GetTemporary(384,256,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Default);
        var prior=RenderTexture.active;var image=new Texture2D(384,256,TextureFormat.RGBA32,false,false);
        try{camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,384,256),0,0);image.Apply(false);if(path!=null)File.WriteAllBytes(path,image.EncodeToPNG());return image.GetPixels();}
        finally{camera.targetTexture=null;RenderTexture.active=prior;RenderTexture.ReleaseTemporary(rt);Object.DestroyImmediate(image);}
    }
    static float Error(Vector3[] a,Vector3[] b)=>a.Zip(b,(x,y)=>(x-y).magnitude).Max();
    [Serializable] public sealed class Sample{public int pose;public float maxPosition,maxNormal,maxTangent,rmsPosition,meanImageError,legacyNormalOffsetError,decodedLinearCpuError,decodedLinearImageError,minRawNormalLength,maxRawNormalLength;}
    [Serializable] public sealed class Report{public string unity,graphics,gpu;public bool ownedFixtureOnly=true,vrchatAnalyzed=false,bakeIsCpuReference=true,productionEnabled=false;public Sample[] samples;public string limitation="CPU and Editor renders only: normal-linear encoding compensates blendshape normal deltas. GPU-skinning player/native bundles still need validation; bindpose compensation remains an observed keyless recovery route.";}
    public static void Run()
    {
        LAGBindingValidation.RequireResearchProject();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        if(AssetDatabase.IsValidFolder(Folder))AssetDatabase.DeleteAsset(Folder);AssetDatabase.CreateFolder("Assets","SkinningCharacterFixture");
        string output="../evidence/mip-skin/characterization";Directory.CreateDirectory(output);
        var root=new GameObject("OwnedSkinRoot");var cameraObj=new GameObject("OwnedSkinCamera");
        var originals=new List<Object>();
        try
        {
            var bone0=new GameObject("Bone0").transform;bone0.SetParent(root.transform,false);bone0.localPosition=new Vector3(0,-.2f,0);
            var bone1=new GameObject("Bone1").transform;bone1.SetParent(bone0,false);bone1.localPosition=new Vector3(0,.45f,0);var bones=new[]{bone0,bone1};
            var sourceObj=new GameObject("Original");sourceObj.transform.SetParent(root.transform,false);var original=sourceObj.AddComponent<SkinnedMeshRenderer>();original.bones=bones;original.rootBone=bone0;original.updateWhenOffscreen=true;original.quality=SkinQuality.Bone4;
            var source=Mesh(sourceObj.transform,bones);AssetDatabase.CreateAsset(source,Folder+"/original.asset");original.sharedMesh=source;
            var copyObj=new GameObject("Encoded");copyObj.transform.SetParent(root.transform,false);var copy=copyObj.AddComponent<SkinnedMeshRenderer>();copy.bones=bones;copy.rootBone=bone0;copy.updateWhenOffscreen=true;copy.quality=SkinQuality.Bone4;
            var encoded=Reframe(source,PublicFrame);AssetDatabase.CreateAsset(encoded,Folder+"/encoded.asset");copy.sharedMesh=encoded;
            var legacy=Object.Instantiate(source);legacy.vertices=source.vertices.Zip(source.normals,(p,n)=>p+n*.13f).ToArray();legacy.ClearBlendShapes();
            legacy.SetUVs(6,Enumerable.Repeat(new Vector2(.13f,0),source.vertexCount).ToList());legacy.SetUVs(7,Enumerable.Repeat(Vector2.zero,source.vertexCount).ToList());
            var dv=new Vector3[source.vertexCount];var dn=new Vector3[source.vertexCount];var dt=new Vector3[source.vertexCount];
            for(int shape=0;shape<source.blendShapeCount;shape++)for(int frame=0;frame<source.GetBlendShapeFrameCount(shape);frame++)
            {source.GetBlendShapeFrameVertices(shape,frame,dv,dn,dt);legacy.AddBlendShapeFrame(source.GetBlendShapeName(shape),source.GetBlendShapeFrameWeight(shape,frame),dv.Zip(dn,(p,n)=>p+n*.13f).ToArray(),dn,dt);}
            originals.Add(legacy);
            Directory.CreateDirectory("Assets/LinuxAvatarGuardGenerated/Research/skin-character-shaders");
            var shaders=new GuardShaders("Assets/LinuxAvatarGuardGenerated/Research/skin-character-shaders","OwnedSkinCharacter",LegacyLinearCodecV1.Decoder);
            var legacyObj=new GameObject("LegacyProbe");legacyObj.transform.SetParent(root.transform,false);var probe=legacyObj.AddComponent<SkinnedMeshRenderer>();probe.sharedMesh=legacy;probe.bones=bones;probe.rootBone=bone0;probe.updateWhenOffscreen=true;probe.quality=SkinQuality.Bone4;probe.enabled=false;
            var material=new Material(Shader.Find("lilToon"));material.SetFloat("_AsUnlit",1);material.SetColor("_Color",new Color(.3f,.8f,.65f,1));originals.Add(material);original.sharedMaterial=copy.sharedMaterial=material;
            var linearMaterial=new Material(material){shader=shaders.Copy(material.shader)};linearMaterial.SetFloat("_LAGKey0",255);originals.Add(linearMaterial);probe.sharedMaterial=linearMaterial;
            var camera=cameraObj.AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.03f,.04f,.05f);camera.orthographic=true;camera.orthographicSize=1.3f;camera.nearClipPlane=.1f;camera.farClipPlane=30;camera.allowHDR=false;camera.allowMSAA=false;camera.transform.position=new Vector3(0,0,-4);
            var a=new Mesh();var b=new Mesh();var c=new Mesh();originals.AddRange(new Object[]{a,b,c});var samples=new List<Sample>();
            for(int pose=0;pose<8;pose++)
            {
                Pose(bones,original,pose);for(int shape=0;shape<3;shape++)copy.SetBlendShapeWeight(shape,original.GetBlendShapeWeight(shape));
                original.BakeMesh(a,false);copy.BakeMesh(b,false);
                var row=new Sample{pose=pose,maxPosition=Error(a.vertices,b.vertices),maxNormal=Error(a.normals,b.normals),maxTangent=Error(a.tangents.Select(t=>new Vector3(t.x,t.y,t.z)).ToArray(),b.tangents.Select(t=>new Vector3(t.x,t.y,t.z)).ToArray()),rmsPosition=(float)Math.Sqrt(a.vertices.Zip(b.vertices,(p,q)=>(p-q).sqrMagnitude).Average())};
                original.enabled=true;copy.enabled=false;var reference=Render(camera,output+"/pose-"+pose+"-original.png");original.enabled=false;copy.enabled=true;var result=Render(camera,output+"/pose-"+pose+"-compensated.png");
                row.meanImageError=reference.Zip(result,(p,q)=>(Mathf.Abs(p.r-q.r)+Mathf.Abs(p.g-q.g)+Mathf.Abs(p.b-q.b))/3).Average();
                copy.enabled=false;probe.enabled=true;for(int shape=0;shape<3;shape++)probe.SetBlendShapeWeight(shape,original.GetBlendShapeWeight(shape));probe.BakeMesh(c,false);
                row.decodedLinearCpuError=Error(a.vertices,c.vertices.Zip(c.normals,(p,n)=>p-n*.13f).ToArray());
                row.minRawNormalLength=c.normals.Min(n=>n.magnitude);row.maxRawNormalLength=c.normals.Max(n=>n.magnitude);
                var linear=Render(camera,output+"/pose-"+pose+"-linear-decoded.png");probe.enabled=false;
                row.decodedLinearImageError=reference.Zip(linear,(p,q)=>(Mathf.Abs(p.r-q.r)+Mathf.Abs(p.g-q.g)+Mathf.Abs(p.b-q.b))/3).Average();
                // Record the zero-morph CPU observation without assuming normalization.
                for(int shape=0;shape<3;shape++)probe.SetBlendShapeWeight(shape,0);
                for(int shape=0;shape<3;shape++)original.SetBlendShapeWeight(shape,0);original.BakeMesh(a,false);probe.BakeMesh(c,false);
                row.legacyNormalOffsetError=Error(a.vertices,c.vertices.Zip(c.normals,(p,n)=>p-n*.13f).ToArray());samples.Add(row);
                if(row.maxPosition>2e-5f||row.maxNormal>2e-5f||row.maxTangent>2e-5f||row.meanImageError>.0015f||row.decodedLinearCpuError>2e-5f||row.decodedLinearImageError>.0015f)throw new Exception("SKINNING_CHARACTERIZATION_FAILED pose "+pose+" max "+row.maxPosition+" normal "+row.maxNormal+" image "+row.meanImageError);
            }

            File.WriteAllText(output+"/report.json",JsonUtility.ToJson(new Report{unity=Application.unityVersion,graphics=SystemInfo.graphicsDeviceType.ToString(),gpu=SystemInfo.graphicsDeviceName,samples=samples.ToArray()},true));
            Debug.Log("LAG_SKINNING_CHARACTERIZATION_SUCCESS 8 mixed-weight poses with position/normal/tangent blendshapes and nonuniform bone scales");
        }
        finally{foreach(var obj in originals)if(obj)Object.DestroyImmediate(obj);Object.DestroyImmediate(root);Object.DestroyImmediate(cameraObj);}
    }
}
