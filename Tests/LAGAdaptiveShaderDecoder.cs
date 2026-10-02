// SPDX-License-Identifier: MIT
// Adversarial research helper. Reads generated HLSL, not CodecPlan/IR/seeds or package CPU helpers.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

public sealed class LAGAdaptiveShaderDecoder
{
    readonly List<Action<double[],double[]>> steps = new List<Action<double[],double[]>>();
    readonly int firstUv, secondUv;
    public string Body { get; }
    public string Sequence { get; }
    public string Constants { get; }
    static int Axis(string value) => "xyz".IndexOf(value[0]);
    static double Number(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    public LAGAdaptiveShaderDecoder(string shader)
    {
        var header = Regex.Match(shader,@"float3 LAGStaticDecode\(float3 p, float4 c, float4 k\) \{\s*k=clamp\(floor\(k\+0\.5\),0\.0,255\.0\)/255\.0;\s*(.*?)return p; \}",RegexOptions.Singleline);
        var carriers=Regex.Match(shader,@"LAGStaticDecode\(positionOS\.xyz,float4\(input\.uv(\d),input\.uv(\d)\)");
        if(!header.Success || !carriers.Success) throw new InvalidOperationException("Unsupported shader decoder/carrier contract in adaptive test.");
        firstUv=int.Parse(carriers.Groups[1].Value); secondUv=int.Parse(carriers.Groups[2].Value); Body=header.Groups[1].Value;
        var kinds=new List<string>(); var constants=new List<string>();
        foreach(string raw in Body.Split('\n'))
        {
            string line=raw.Trim(); if(line.Length==0) continue;
            var swap=Regex.Match(line,@"^\{ float t=p\.([xyz]); p\.\1=p\.([xyz]); p\.\2=t; \}$");
            var flip=Regex.Match(line,@"^p\.([xyz])=-p\.\1;$");
            var shear=Regex.Match(line,@"^p\.([xyz])-=\(([^)]+)\)\*p\.([xyz]);$");
            var bend=Regex.Match(line,@"^p\.([xyz])-=\(([^)]+)\)\*\(p\.([xyz])/\(1\.0\+abs\(p\.\3\)\)\);$");
            if(swap.Success)
            { int a=Axis(swap.Groups[1].Value),b=Axis(swap.Groups[2].Value); steps.Add((p,c)=>{double t=p[a];p[a]=p[b];p[b]=t;}); kinds.Add("swap"); }
            else if(flip.Success) {int a=Axis(flip.Groups[1].Value); steps.Add((p,c)=>p[a]=-p[a]); kinds.Add("flip");}
            else if(shear.Success)
            { int a=Axis(shear.Groups[1].Value),b=Axis(shear.Groups[3].Value); double f=Number(shear.Groups[2].Value); steps.Add((p,c)=>p[a]-=f*p[b]); kinds.Add("shear"); constants.Add(shear.Groups[2].Value); }
            else if(bend.Success)
            { int a=Axis(bend.Groups[1].Value),b=Axis(bend.Groups[3].Value); double f=Number(bend.Groups[2].Value); steps.Add((p,c)=>p[a]-=f*p[b]/(1+Math.Abs(p[b]))); kinds.Add("bend"); constants.Add(bend.Groups[2].Value); }
            else
            {
                var vectors=Regex.Matches(line,@"float4\(([^)]+)\)");
                if(!line.StartsWith("p-=float3(dot(",StringComparison.Ordinal) || vectors.Count!=3)
                    throw new InvalidOperationException("Unknown shader statement in adaptive test: "+line);
                var rows=vectors.Cast<Match>().Select(m=>m.Groups[1].Value.Split(',').Select(Number).ToArray()).ToArray();
                if(rows.Any(r=>r.Length!=4)) throw new InvalidOperationException("Invalid offset width in shader.");
                steps.Add((p,c)=>{for(int a=0;a<3;a++) for(int j=0;j<4;j++) p[a]-=rows[a][j]*c[j];}); kinds.Add("offset");
                constants.AddRange(vectors.Cast<Match>().Select(m=>m.Groups[1].Value));
            }
        }
        if(steps.Count!=8) throw new InvalidOperationException("Expected eight shader statements.");
        Sequence=string.Join(",",kinds); Constants=string.Join("|",constants);
    }
    public Vector3[] Decode(Mesh mesh,int[] runtime)
    {
        var first=new List<Vector2>(); var second=new List<Vector2>(); mesh.GetUVs(firstUv,first); mesh.GetUVs(secondUv,second);
        var vertices=mesh.vertices;
        if(runtime==null || runtime.Length!=4 || first.Count!=vertices.Length || second.Count!=vertices.Length) throw new ArgumentException("Invalid adaptive decoder inputs.");
        var result=new Vector3[vertices.Length];
        for(int i=0;i<vertices.Length;i++)
        {
            var p=new[]{(double)vertices[i].x,vertices[i].y,vertices[i].z};
            var c=new[]{first[i].x*runtime[0]/255.0,first[i].y*runtime[1]/255.0,second[i].x*runtime[2]/255.0,second[i].y*runtime[3]/255.0};
            foreach(var step in steps) step(p,c); result[i]=new Vector3((float)p[0],(float)p[1],(float)p[2]);
        }
        return result;
    }
    public static float MaxError(Vector3[] a,Vector3[] b) => a.Zip(b,(x,y)=>(x-y).magnitude).Max();
    public static float Rms(Vector3[] a,Vector3[] b) => Mathf.Sqrt(a.Zip(b,(x,y)=>(x-y).sqrMagnitude).Average());
}
