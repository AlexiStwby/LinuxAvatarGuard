// SPDX-License-Identifier: MIT
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace LinuxAvatarGuard
{
    public sealed class TextureMipProgram
    {
        public int Level { get; }
        public int Size { get; }
        public int Grid => Math.Min(4, Size);
        public ReadOnlyCollection<TextureTile> Tiles { get; }
        internal TextureMipProgram(int level,int size,TextureTile[] tiles)
        { Level=level;Size=size;Tiles=Array.AsReadOnly((TextureTile[])tiles.Clone()); }
    }

    // Explicit V2 research opt-in. Encode original mip bytes, never average XOR/permuted payloads.
    public sealed class TextureGuardCodecV2 : ITextureCodec, IDisposable
    {
        public const string Id="texture-polymorphic-mips";
        public const int Version=2;
        readonly byte[] seed;
        readonly string scope;
        readonly GuardTextureBindingIdentity binding;
        readonly int[] expectedKey;
        bool disposed;
        static readonly int[][] orders={new[]{0,1,2},new[]{0,2,1},new[]{1,0,2},new[]{1,2,0},new[]{2,0,1},new[]{2,1,0}};
        public TextureGuardCodecV2(byte[] seed,string scope)
        {
            if(seed==null||seed.Length!=32||string.IsNullOrEmpty(scope)||scope.Length>512||scope.Any(char.IsControl))throw new ArgumentException("TextureGuard V2: seed/scope inválidos.");
            this.seed=(byte[])seed.Clone();this.scope=scope;
        }
        internal TextureGuardCodecV2(byte[] seed,GuardTextureBindingIdentity binding,int[] key):this(seed,"LAG/texture-binding/v2/"+binding.StableId)
        {this.binding=binding;expectedKey=(int[])key.Clone();}
        void Alive()
        {
            if(disposed)throw new ObjectDisposedException(nameof(TextureGuardCodecV2));
            if(Application.platform!=RuntimePlatform.LinuxEditor||Application.unityVersion!="2022.3.22f1"||SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Vulkan||GraphicsSettings.currentRenderPipeline!=null)
                throw new InvalidOperationException("TextureGuard V2: solo Unity 2022.3.22f1 Linux/Vulkan/Built-in revisado.");
        }
        internal static string Fingerprint(Texture2D source)
        {
            string hash=TextureGuardCodecV1.Fingerprint(source,true,true);
            int complete=1+(int)Mathf.Log(source.width,2);
            if(source.mipmapCount!=1&&source.mipmapCount!=complete)throw new InvalidOperationException("TextureGuard V2: una pirámide completa o un solo mip requerida.");
            if(float.IsNaN(source.mipMapBias)||float.IsInfinity(source.mipMapBias)||Mathf.Abs(source.mipMapBias)>8)
                throw new InvalidOperationException("TextureGuard V2: mip bias finito entre -8 y 8 requerido.");
            return hash;
        }
        public TexturePlan Plan(Texture2D source)
        {
            Alive();binding?.Validate();if(binding!=null&&(source!=binding.Source||!binding.MipmapsEnabled))throw new InvalidOperationException("Fuente/política ajena al binding V2.");
            string fingerprint=Fingerprint(source);var mips=new TextureMipProgram[source.mipmapCount];
            for(int level=0;level<mips.Length;level++)
            {
                byte[] key;using(var hmac=new HMACSHA256(seed))key=hmac.ComputeHash(Encoding.UTF8.GetBytes("LAG/texture-mips/v2/program\0"+scope+"\0"+fingerprint+"\0"+level.ToString(CultureInfo.InvariantCulture)));
                try
                {
                    int size=Math.Max(1,source.width>>level),grid=Math.Min(4,size);var destination=Enumerable.Range(0,grid*grid).ToArray();
                    using(var random=new TextureGuardCodecV1.TextureStream(key))
                    {
                        for(int i=destination.Length-1;i>0;i--){int j=random.Next(i+1);int t=destination[i];destination[i]=destination[j];destination[j]=t;}
                        mips[level]=new TextureMipProgram(level,size,destination.Select(d=>new TextureTile(d,random.Next(8),random.Next(6),random.Next(256),random.Next(256),random.Next(256))).ToArray());
                    }
                }
                finally{Array.Clear(key,0,key.Length);}
            }
            return new TexturePlan(source,new TextureProgram(source,mips),fingerprint,this,binding?.StableId);
        }
        public void Validate(Texture2D source,TexturePlan plan)
        {
            Alive();binding?.Validate();
            if(plan==null||plan.Owner!=this||plan.Source!=source||!plan.Program.MipmapsEnabled||plan.BindingStableId!=binding?.StableId||Fingerprint(source)!=plan.SourceFingerprint)
                throw new InvalidOperationException("TextureGuard V2: plan/fuente ajeno o modificado.");
        }
        public Texture2D Encode(Texture2D source,TexturePlan plan,int[] key)
        {
            Validate(source,plan);
            if(key==null||key.Length!=4||key.Any(k=>k<0||k>255)||key.All(k=>k==0)||(expectedKey!=null&&!key.SequenceEqual(expectedKey)))throw new ArgumentException("TextureGuard V2: claves inválidas/ajenas al contexto.");
            Texture2D output=null;
            try
            {
                output=new Texture2D(source.width,source.height,TextureFormat.RGBA32,source.mipmapCount,true){name="tm_"+ProgramHash(plan).Substring(0,20),filterMode=source.filterMode,wrapModeU=source.wrapModeU,wrapModeV=source.wrapModeV,anisoLevel=0,mipMapBias=source.mipMapBias,ignoreMipmapLimit=true};
                foreach(var mip in plan.Program.Mips)
                {
                    var original=source.format==TextureFormat.RGB24?source.GetPixels32(mip.Level):source.GetPixelData<Color32>(mip.Level).ToArray();var encoded=new Color32[original.Length];
                    try
                    {
                        int side=mip.Size/mip.Grid;
                        for(int y=0;y<mip.Size;y++)for(int x=0;x<mip.Size;x++)
                        {
                            int tile=x/side+mip.Grid*(y/side),lx=x%side,ly=y%side;var instruction=mip.Tiles[tile];
                            if((instruction.Orientation&1)!=0){int t=lx;lx=ly;ly=t;}if((instruction.Orientation&2)!=0)lx=side-1-lx;if((instruction.Orientation&4)!=0)ly=side-1-ly;
                            int address=(instruction.Destination%mip.Grid*side+lx)+(instruction.Destination/mip.Grid*side+ly)*mip.Size;var pixel=original[x+y*mip.Size];var order=orders[instruction.ChannelOrder];
                            encoded[address]=new Color32((byte)(Channel(pixel,order[0])^instruction.SaltR^key[tile%4]),(byte)(Channel(pixel,order[1])^instruction.SaltG^key[(tile+1)%4]),(byte)(Channel(pixel,order[2])^instruction.SaltB^key[(tile+2)%4]),255);
                        }
                        output.SetPixels32(encoded,mip.Level);
                    }
                    finally{Array.Clear(original,0,original.Length);Array.Clear(encoded,0,encoded.Length);}
                }
                // This upload must NOT rebuild the independently encoded pyramid.
                output.Apply(false,true);Validate(source,plan);return output;
            }
            catch{if(output)UnityEngine.Object.DestroyImmediate(output);throw;}
        }
        static int Channel(Color32 p,int c)=>c==0?p.r:c==1?p.g:p.b;
        public static string ProgramHash(TexturePlan plan)
        {
            if(plan==null||!plan.Program.MipmapsEnabled)throw new ArgumentException("TextureGuard V2 plan requerido.");var p=plan.Program;
            using(var stream=new MemoryStream())
            {
                using(var w=new BinaryWriter(stream,Encoding.UTF8,true))
                {
                    w.Write(Id);w.Write(Version);w.Write(p.SchemaVersion);w.Write(p.Size);w.Write(p.SourceSRGB);w.Write((int)p.Filter);w.Write((int)p.WrapU);w.Write((int)p.WrapV);w.Write(p.MipBias);w.Write(p.MipCount);
                    foreach(var mip in p.Mips){w.Write(mip.Level);w.Write(mip.Size);w.Write(mip.Grid);foreach(var t in mip.Tiles){w.Write(t.Destination);w.Write(t.Orientation);w.Write(t.ChannelOrder);w.Write(t.SaltR);w.Write(t.SaltG);w.Write(t.SaltB);}}
                }
                return MeshBindingIdentity.Digest(stream.ToArray());
            }
        }
        public TextureDecoderFragment EmitDecoder(TexturePlan plan)
        {
            Validate(plan?.Source,plan);var p=plan.Program;var h=new StringBuilder("\n#ifndef LAG_TEXTURE_FUNCTIONS_INCLUDED\n#define LAG_TEXTURE_FUNCTIONS_INCLUDED\n#if !defined(SHADER_API_VULKAN) && !defined(SHADER_API_D3D11)\n#error TextureGuard V2 requires Vulkan or D3D11\n#endif\n");
            h.Append("float4 LAG_TextureTexel(float2 pixel,uint mip) {\nuint width,height,count; _MainTex.GetDimensions(mip,width,height,count);\nfloat2 size=float2(width,height);\n");
            for(int axis=0;axis<2;axis++){string c=axis==0?"x":"y";var wrap=axis==0?p.WrapU:p.WrapV;h.Append("pixel.").Append(c).Append(wrap==TextureWrapMode.Repeat?" -= floor(pixel."+c+"/size."+c+")*size."+c+";\n":" = clamp(pixel."+c+",0.0,size."+c+"-1.0);\n");}
            h.AppendFormat(CultureInfo.InvariantCulture,"uint sourceMip=(uint)round(log2({0}.0/float(width)));\n",p.Size);
            h.Append("uint grid=min(4u,width),side=width/grid;uint2 pos=(uint2)pixel;uint tile=pos.x/side+grid*(pos.y/side);\nuint atlas=0u,op=0u,order=0u;uint3 salts=0u;\n");
            foreach(var level in p.Mips)
            {
                h.AppendFormat(CultureInfo.InvariantCulture,"// LAG_MIP_V2 {0} {1} {2}\nif(sourceMip=={0}u) {{\n",level.Level,level.Size,level.Grid);
                for(int i=0;i<level.Tiles.Count;i++){var t=level.Tiles[i];h.AppendFormat(CultureInfo.InvariantCulture,"if(tile=={0}u) {{atlas={1}u;op={2}u;order={3}u;salts=uint3({4}u,{5}u,{6}u);}}\n",i,t.Destination,t.Orientation,t.ChannelOrder,t.SaltR,t.SaltG,t.SaltB);}
                h.Append("}\n");
            }
            h.Append(@"uint2 local=pos%side;
if((op&1u)!=0u)local=local.yx;if((op&2u)!=0u)local.x=side-1u-local.x;if((op&4u)!=0u)local.y=side-1u-local.y;
uint2 address=uint2(atlas%grid,atlas/grid)*side+local;
uint4 key=(uint4)clamp(floor(float4(_LAGKey0,_LAGKey1,_LAGKey2,_LAGKey3)+0.5),0.0,255.0);
uint3 bytes=(uint3)floor(_MainTex.Load(int3(address,mip)).rgb*255.0+0.5);
bytes^=salts^uint3(key[tile%4u],key[(tile+1u)%4u],key[(tile+2u)%4u]);
float3 rgb=(float3)bytes/255.0;
if(order==1u)rgb=rgb.xzy;if(order==2u)rgb=rgb.yxz;if(order==3u)rgb=rgb.zxy;if(order==4u)rgb=rgb.yzx;if(order==5u)rgb=rgb.zyx;
");
            if(p.SourceSRGB)h.Append("#if !defined(UNITY_COLORSPACE_GAMMA)\nrgb=float3(rgb.r<=0.04045?rgb.r/12.92:pow((rgb.r+0.055)/1.055,2.4),rgb.g<=0.04045?rgb.g/12.92:pow((rgb.g+0.055)/1.055,2.4),rgb.b<=0.04045?rgb.b/12.92:pow((rgb.b+0.055)/1.055,2.4));\n#endif\n");
            h.Append("return float4(rgb,1.0);\n}\nfloat4 LAG_TextureLevel(float2 uv,uint mip) {uint w,h,n;_MainTex.GetDimensions(mip,w,h,n);float2 p=uv*float2(w,h);\n");
            if(p.Filter==FilterMode.Point)h.Append("return LAG_TextureTexel(floor(p),mip);\n");
            else h.Append("p-=0.5;float2 b=floor(p),t=frac(p);return lerp(lerp(LAG_TextureTexel(b,mip),LAG_TextureTexel(b+float2(1,0),mip),t.x),lerp(LAG_TextureTexel(b+float2(0,1),mip),LAG_TextureTexel(b+float2(1,1),mip),t.x),t.y);\n");
            // Query the hardware LOD with original UV derivatives and the preserved sampler/bias.
            h.Append("}\nfloat4 LAG_TextureSample(float2 uv){uint w,h,n;_MainTex.GetDimensions(0,w,h,n);float lod=clamp(_MainTex.CalculateLevelOfDetail(sampler_MainTex,uv),0.0,float(n-1u));\n");
            if(p.Filter==FilterMode.Trilinear)h.Append("uint m=(uint)floor(lod);float f=frac(lod);float4 a=LAG_TextureLevel(uv,m);if(f<=0.0||m+1u>=n)return a;return lerp(a,LAG_TextureLevel(uv,m+1u),f);\n");
            else h.Append("return LAG_TextureLevel(uv,(uint)floor(lod+0.5));\n");
            h.Append("}\n#endif\n");return new TextureDecoderFragment(h.ToString());
        }
        public void Dispose(){if(disposed)return;disposed=true;Array.Clear(seed,0,seed.Length);if(expectedKey!=null)Array.Clear(expectedKey,0,expectedKey.Length);}
    }
}
