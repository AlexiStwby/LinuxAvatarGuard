// SPDX-License-Identifier: MIT
// Test-only: the owned unlit mono fixture does not exercise lighting, fog, shadow, stereo or instancing variants.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

public sealed class LAGDiversityVariantFilter : IPreprocessShaders
{
    public static bool Enabled;
    public static int Before,After;
    public int callbackOrder => int.MaxValue;
    static readonly HashSet<string> omitted=new HashSet<string>{"LIGHTMAP_ON","DIRLIGHTMAP_COMBINED","DYNAMICLIGHTMAP_ON","LIGHTMAP_SHADOW_MIXING","SHADOWS_SHADOWMASK",
        "STEREO_INSTANCING_ON","STEREO_MULTIVIEW_ON","UNITY_SINGLE_PASS_STEREO","INSTANCING_ON","FOG_LINEAR","FOG_EXP","FOG_EXP2","VERTEXLIGHT_ON","SHADOWS_SCREEN","SHADOWS_SOFT","SHADOWS_CUBE","POINT","POINT_COOKIE","DIRECTIONAL_COOKIE","SPOT"};
    public void OnProcessShader(Shader shader,ShaderSnippetData snippet,IList<ShaderCompilerData> data)
    {
        if(!Enabled || Environment.GetEnvironmentVariable("LAG_BINDING_AUDIT_ALLOWED")!="synthetic-unity-only" ||
            !shader.name.StartsWith("LinuxAvatarGuard/",StringComparison.Ordinal)) return;
        Before+=data.Count;
        var fallback=data.Where(d=>d.shaderCompilerPlatform==ShaderCompilerPlatform.Vulkan || d.shaderCompilerPlatform==ShaderCompilerPlatform.D3D).Take(1).ToArray();
        for(int i=data.Count-1;i>=0;i--)
        {
            var platform=data[i].shaderCompilerPlatform;
            if((platform!=ShaderCompilerPlatform.Vulkan && platform!=ShaderCompilerPlatform.D3D) ||
                data[i].shaderKeywordSet.GetShaderKeywords().Any(k=>omitted.Contains(k.name))) data.RemoveAt(i);
        }
        // A serialized pass must retain a program even when the test never renders that pass.
        if(data.Count==0 && fallback.Length==1) data.Add(fallback[0]);
        After+=data.Count;
    }
}
