// SPDX-License-Identifier: MIT
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LinuxAvatarGuard
{
    public interface ITextureCodec
    {
        TexturePlan Plan(Texture2D source);
        void Validate(Texture2D source, TexturePlan plan);
        Texture2D Encode(Texture2D source, TexturePlan plan, int[] runtimeKey);
        TextureDecoderFragment EmitDecoder(TexturePlan plan);
    }

    public sealed class TextureTile
    {
        public int Destination { get; }
        public int Orientation { get; }
        public int ChannelOrder { get; }
        public int SaltR { get; }
        public int SaltG { get; }
        public int SaltB { get; }
        internal TextureTile(int destination, int orientation, int order, int r, int g, int b)
        { Destination = destination; Orientation = orientation; ChannelOrder = order; SaltR = r; SaltG = g; SaltB = b; }
    }

    // Public instructions contain salts and mapping, never seed or expected runtime values.
    public sealed class TextureProgram
    {
        public string CodecId => TextureGuardCodecV1.Id;
        public int CodecVersion => TextureGuardCodecV1.Version;
        public int SchemaVersion => 1;
        public int Size { get; }
        public bool SourceSRGB { get; }
        public FilterMode Filter { get; }
        public TextureWrapMode WrapU { get; }
        public TextureWrapMode WrapV { get; }
        public ReadOnlyCollection<TextureTile> Tiles { get; }
        internal TextureProgram(Texture2D source, TextureTile[] tiles)
        {
            Size = source.width; SourceSRGB = source.isDataSRGB;
            Filter = source.filterMode; WrapU = source.wrapModeU; WrapV = source.wrapModeV;
            Tiles = Array.AsReadOnly((TextureTile[])tiles.Clone());
        }
    }

    public sealed class TexturePlan
    {
        public TextureProgram Program { get; }
        public string SourceFingerprint { get; }
        public string BindingStableId { get; }
        internal Texture2D Source { get; }
        internal object Owner { get; }
        internal TexturePlan(Texture2D source, TextureProgram program, string fingerprint, object owner, string bindingId = null)
        { Source = source; Program = program; SourceFingerprint = fingerprint; Owner = owner; BindingStableId = bindingId; }
    }

    public sealed class TextureDecoderFragment
    {
        public string Functions { get; }
        public string MainOverride { get; }
        internal TextureDecoderFragment(string functions)
        {
            Functions = functions;
            MainOverride = "\nHLSLINCLUDE\n#define OVERRIDE_MAIN fd.col = LAG_TextureSample(fd.uvMain); LIL_APPLY_MAIN_TONECORRECTION fd.col *= _Color;\nENDHLSL\n";
        }
    }

    // Research API only: no runtime C# decoder and no changes to avatar preparation.
    public sealed class TextureGuardCodecV1 : ITextureCodec, IDisposable
    {
        public const string Id = "opaque-tile-texture-prototype";
        public const int Version = 1;
        public const int Grid = 4;
        readonly byte[] seed;
        readonly string scope;
        readonly GuardTextureBindingIdentity binding;
        readonly int[] expectedRuntimeKey;
        bool disposed;
        static readonly int[][] orders = {
            new[] {0,1,2}, new[] {0,2,1}, new[] {1,0,2}, new[] {1,2,0}, new[] {2,0,1}, new[] {2,1,0}
        };

        public TextureGuardCodecV1(byte[] seed, string scope)
        {
            if (seed == null || seed.Length != 32 || string.IsNullOrWhiteSpace(scope) || scope.Length > 512 || scope.Any(char.IsControl))
                throw new ArgumentException("TextureGuard: seed de 32 bytes y scope explícito válidos requeridos.");
            this.seed = (byte[])seed.Clone(); this.scope = scope;
        }
        // Contextual builds preserve the historical shared keys, including individual zero lanes.
        internal TextureGuardCodecV1(byte[] seed, GuardTextureBindingIdentity binding, int[] runtimeKey)
            : this(seed, "LAG/texture-binding/v1/" + binding.StableId)
        { this.binding = binding; expectedRuntimeKey = (int[])runtimeKey.Clone(); }
        void RequireAlive()
        {
            if (disposed) throw new ObjectDisposedException(nameof(TextureGuardCodecV1));
            if (Application.platform != RuntimePlatform.LinuxEditor || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan ||
                GraphicsSettings.currentRenderPipeline != null || Application.unityVersion != "2022.3.22f1")
                throw new InvalidOperationException("TextureGuard experimental: solo Unity 2022.3.22f1, Linux, Vulkan y Built-in revisados.");
        }
        internal static void RequireSource(Texture2D source, bool allowRgb24 = false)
        {
            if (!source || !source.isReadable || (source.format != TextureFormat.RGBA32 && !(allowRgb24 && source.format == TextureFormat.RGB24)) || source.mipmapCount != 1 || source.streamingMipmaps)
                throw new InvalidOperationException("TextureGuard: fuente legible RGBA32 (o RGB24 contextual), sin compresión, mipmaps ni streaming requerida; no se modifica el importador.");
            if (source.width != source.height || source.width < 16 || source.width > 1024 || (source.width & (source.width - 1)) != 0)
                throw new InvalidOperationException("TextureGuard: tamaño cuadrado power-of-two entre 16 y 1024 requerido.");
            if ((source.filterMode != FilterMode.Point && source.filterMode != FilterMode.Bilinear) || source.anisoLevel > 1 ||
                !SupportedWrap(source.wrapModeU) || !SupportedWrap(source.wrapModeV))
                throw new InvalidOperationException("TextureGuard: solo Point/Bilinear y Repeat/Clamp por eje, sin anisotropía.");
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId) ||
                !MeshKeyDerivation.IsHex(guid, 32) || localId == 0 || EditorUtility.IsDirty(source))
                throw new InvalidOperationException("TextureGuard: guarda la fuente persistente antes de analizarla.");
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source));
            if (importer is TextureImporter textureImporter &&
                (textureImporter.textureType != TextureImporterType.Default || textureImporter.mipmapEnabled || textureImporter.streamingMipmaps ||
                 textureImporter.textureCompression != TextureImporterCompression.Uncompressed))
                throw new InvalidOperationException("TextureGuard: importador Default, sin compresión/mipmaps/streaming requerido.");
            if (source.format == TextureFormat.RGBA32 && source.GetPixelData<Color32>(0).Any(p => p.a != 255))
                throw new InvalidOperationException("TextureGuard: solo albedo con todos los texels opacos; alfa transparente/cutout pendiente.");
        }
        static bool SupportedWrap(TextureWrapMode mode) => mode == TextureWrapMode.Clamp || mode == TextureWrapMode.Repeat;
        internal static string Fingerprint(Texture2D source, bool allowRgb24 = false)
        {
            RequireSource(source, allowRgb24);
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long id);
                    writer.Write("LAG/texture-source/v1"); writer.Write(guid); writer.Write(id);
                    writer.Write(source.width); writer.Write(source.height); writer.Write((int)source.format); writer.Write(source.isDataSRGB);
                    writer.Write((int)source.filterMode); writer.Write((int)source.wrapModeU); writer.Write((int)source.wrapModeV);
                    writer.Write(source.anisoLevel); writer.Write(source.mipMapBias);
                    string path = AssetDatabase.GetAssetPath(source);
                    writer.Write(AssetDatabase.GetAssetDependencyHash(path).ToString());
                    var importer = AssetImporter.GetAtPath(path);
                    writer.Write(importer ? EditorJsonUtility.ToJson(importer) : "");
                    writer.Write(source.GetRawTextureData<byte>().ToArray());
                }
                return MeshBindingIdentity.Digest(stream.ToArray());
            }
        }
        public TexturePlan Plan(Texture2D source)
        {
            RequireAlive();
            if (binding != null) { binding.Validate(); if (source != binding.Source) throw new InvalidOperationException("TextureGuard: fuente ajena al binding contextual."); }
            string fingerprint = Fingerprint(source, binding != null);
            byte[] key;
            using (var hmac = new HMACSHA256(seed)) key = hmac.ComputeHash(Encoding.UTF8.GetBytes("LAG/texture/v1/program\0" + fingerprint + "\0" + scope));
            try
            {
                using (var random = new TextureStream(key))
                {
                    var destinations = Enumerable.Range(0, Grid * Grid).ToArray();
                    for (int i = destinations.Length - 1; i > 0; i--)
                    { int j = random.Next(i + 1); int temp = destinations[i]; destinations[i] = destinations[j]; destinations[j] = temp; }
                    var tiles = destinations.Select(d => new TextureTile(d, random.Next(8), random.Next(6), random.Next(256), random.Next(256), random.Next(256))).ToArray();
                    return new TexturePlan(source, new TextureProgram(source, tiles), fingerprint, this, binding?.StableId);
                }
            }
            finally { Array.Clear(key, 0, key.Length); }
        }
        public void Validate(Texture2D source, TexturePlan plan)
        {
            RequireAlive();
            binding?.Validate();
            if (plan == null || plan.Owner != this || plan.Source != source || plan.BindingStableId != binding?.StableId || Fingerprint(source, binding != null) != plan.SourceFingerprint)
                throw new InvalidOperationException("TextureGuard: plan de otro codec/fuente o fuente/importador modificado.");
        }
        void RequireKey(int[] key)
        {
            int minimum = binding == null ? 1 : 0;
            if (key == null || key.Length != 4 || key.Any(k => k < minimum || k > 255) || key.All(k => k == 0) ||
                (expectedRuntimeKey != null && !key.SequenceEqual(expectedRuntimeKey)))
                throw new ArgumentException("TextureGuard: claves de encoding inválidas o ajenas al contexto.");
        }
        public Texture2D Encode(Texture2D source, TexturePlan plan, int[] runtimeKey)
        {
            Validate(source, plan); RequireKey(runtimeKey);
            int size = plan.Program.Size, tileSize = size / Grid;
            var pixels = source.format == TextureFormat.RGB24 ? source.GetPixels32(0) : source.GetPixelData<Color32>(0).ToArray();
            var encoded = new Color32[pixels.Length]; Texture2D result = null;
            try
            {
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                {
                    int tile = x / tileSize + Grid * (y / tileSize); var instruction = plan.Program.Tiles[tile];
                    int a = x % tileSize, b = y % tileSize;
                    if ((instruction.Orientation & 1) != 0) { int temp = a; a = b; b = temp; }
                    if ((instruction.Orientation & 2) != 0) a = tileSize - 1 - a;
                    if ((instruction.Orientation & 4) != 0) b = tileSize - 1 - b;
                    int destX = instruction.Destination % Grid * tileSize + a;
                    int destY = instruction.Destination / Grid * tileSize + b;
                    var pixel = pixels[x + y * size]; var order = orders[instruction.ChannelOrder];
                    encoded[destX + destY * size] = new Color32(
                        (byte)(Channel(pixel, order[0]) ^ instruction.SaltR ^ runtimeKey[tile % 4]),
                        (byte)(Channel(pixel, order[1]) ^ instruction.SaltG ^ runtimeKey[(tile + 1) % 4]),
                        (byte)(Channel(pixel, order[2]) ^ instruction.SaltB ^ runtimeKey[(tile + 2) % 4]), 255);
                }
                // Load reads these linear UNORM bytes. Preserve the shared sampler's source settings.
                result = new Texture2D(size, size, TextureFormat.RGBA32, false, true) {
                    name = "t_" + ProgramHash(plan).Substring(0, 20), filterMode = source.filterMode,
                    wrapModeU = source.wrapModeU, wrapModeV = source.wrapModeV, anisoLevel = 0, mipMapBias = source.mipMapBias
                };
                result.SetPixels32(encoded); result.Apply(false, true); Validate(source, plan);
                return result;
            }
            catch { if (result) UnityEngine.Object.DestroyImmediate(result); throw; }
            finally { Array.Clear(pixels, 0, pixels.Length); Array.Clear(encoded, 0, encoded.Length); }
        }
        static int Channel(Color32 pixel, int c) => c == 0 ? pixel.r : c == 1 ? pixel.g : pixel.b;
        public static string ProgramHash(TexturePlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var p = plan.Program;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(p.CodecId); writer.Write(p.CodecVersion); writer.Write(p.SchemaVersion);
                    writer.Write(p.Size); writer.Write(p.SourceSRGB); writer.Write((int)p.Filter); writer.Write((int)p.WrapU); writer.Write((int)p.WrapV);
                    foreach (var tile in p.Tiles) { writer.Write(tile.Destination); writer.Write(tile.Orientation); writer.Write(tile.ChannelOrder); writer.Write(tile.SaltR); writer.Write(tile.SaltG); writer.Write(tile.SaltB); }
                }
                return MeshBindingIdentity.Digest(stream.ToArray());
            }
        }
        public TextureDecoderFragment EmitDecoder(TexturePlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            Validate(plan.Source, plan);
            var p = plan.Program; var text = new StringBuilder();
            text.Append("\n#ifndef LAG_TEXTURE_FUNCTIONS_INCLUDED\n#define LAG_TEXTURE_FUNCTIONS_INCLUDED\n");
            text.Append("#if !defined(SHADER_API_VULKAN) && !defined(SHADER_API_D3D11)\n#error TextureGuard pilot requires Vulkan or D3D11\n#endif\n");
            text.Append("float4 LAG_TextureTexel(float2 pixel)\n{\n");
            text.AppendFormat(CultureInfo.InvariantCulture, "    float2 size = float2({0},{0});\n", p.Size);
            for (int axis = 0; axis < 2; axis++)
            {
                string c = axis == 0 ? "x" : "y"; var wrap = axis == 0 ? p.WrapU : p.WrapV;
                text.Append("    pixel.").Append(c).Append(wrap == TextureWrapMode.Repeat ? " -= floor(pixel." + c + " / size." + c + ") * size." + c + ";\n" : " = clamp(pixel." + c + ",0.0,size." + c + " - 1.0);\n");
            }
            text.AppendFormat(CultureInfo.InvariantCulture, "    uint side = {0}u;\n", p.Size / Grid);
            text.Append("    uint2 pos = (uint2)pixel; uint tile = pos.x / side + 4u * (pos.y / side);\n    uint atlas = 0u, op = 0u, order = 0u; uint3 salts = 0u;\n");
            for (int i = 0; i < p.Tiles.Count; i++)
            {
                var t = p.Tiles[i];
                text.AppendFormat(CultureInfo.InvariantCulture, "    if (tile == {0}u) {{ atlas = {1}u; op = {2}u; order = {3}u; salts = uint3({4}u,{5}u,{6}u); }}\n", i, t.Destination, t.Orientation, t.ChannelOrder, t.SaltR, t.SaltG, t.SaltB);
            }
            text.Append(@"    uint2 local = pos % side;
    if ((op & 1u) != 0u) local = local.yx;
    if ((op & 2u) != 0u) local.x = side - 1u - local.x;
    if ((op & 4u) != 0u) local.y = side - 1u - local.y;
    uint2 address = uint2(atlas % 4u, atlas / 4u) * side + local;
    uint4 key = (uint4)clamp(floor(float4(_LAGKey0,_LAGKey1,_LAGKey2,_LAGKey3) + 0.5),0.0,255.0);
    uint3 bytes = (uint3)floor(_MainTex.Load(int3(address,0)).rgb * 255.0 + 0.5);
    bytes ^= salts ^ uint3(key[tile % 4u],key[(tile + 1u) % 4u],key[(tile + 2u) % 4u]);
    float3 rgb = (float3)bytes / 255.0;
    if (order == 1u) rgb = rgb.xzy;
    if (order == 2u) rgb = rgb.yxz;
    if (order == 3u) rgb = rgb.zxy;
    if (order == 4u) rgb = rgb.yzx;
    if (order == 5u) rgb = rgb.zyx;
");
            if (p.SourceSRGB) text.Append(@"#if !defined(UNITY_COLORSPACE_GAMMA)
    rgb = float3(rgb.r <= 0.04045 ? rgb.r / 12.92 : pow((rgb.r + 0.055) / 1.055,2.4),
                 rgb.g <= 0.04045 ? rgb.g / 12.92 : pow((rgb.g + 0.055) / 1.055,2.4),
                 rgb.b <= 0.04045 ? rgb.b / 12.92 : pow((rgb.b + 0.055) / 1.055,2.4));
#endif
");
            text.Append("    return float4(rgb,1.0);\n}\nfloat4 LAG_TextureSample(float2 uv)\n{\n");
            text.AppendFormat(CultureInfo.InvariantCulture, "    float2 p = uv * float2({0},{0});\n", p.Size);
            if (p.Filter == FilterMode.Point) text.Append("    return LAG_TextureTexel(floor(p));\n");
            else text.Append(@"    p -= 0.5; float2 basePixel = floor(p); float2 weight = frac(p);
    float4 a = LAG_TextureTexel(basePixel);
    float4 b = LAG_TextureTexel(basePixel + float2(1,0));
    float4 c = LAG_TextureTexel(basePixel + float2(0,1));
    float4 d = LAG_TextureTexel(basePixel + float2(1,1));
    return lerp(lerp(a,b,weight.x),lerp(c,d,weight.x),weight.y);
");
            text.Append("}\n#endif\n");
            return new TextureDecoderFragment(text.ToString());
        }
        public void Dispose()
        { if (disposed) return; disposed = true; Array.Clear(seed, 0, seed.Length); if (expectedRuntimeKey != null) Array.Clear(expectedRuntimeKey, 0, expectedRuntimeKey.Length); }
        sealed class TextureStream : IDisposable
        {
            readonly HMACSHA256 hmac;
            byte[] block = Array.Empty<byte>(); int cursor; uint counter;
            public TextureStream(byte[] key) { hmac = new HMACSHA256(key); }
            uint NextUInt()
            {
                if (cursor == block.Length)
                {
                    Array.Clear(block, 0, block.Length);
                    block = hmac.ComputeHash(Encoding.UTF8.GetBytes("LAG/texture-stream/v1/" + (counter++).ToString(CultureInfo.InvariantCulture))); cursor = 0;
                }
                uint value = (uint)block[cursor] | (uint)block[cursor+1] << 8 | (uint)block[cursor+2] << 16 | (uint)block[cursor+3] << 24;
                cursor += 4; return value;
            }
            public int Next(int count)
            {
                uint bound = (uint)count, threshold = unchecked(0u - bound) % bound, value;
                do { value = NextUInt(); } while (value < threshold);
                return (int)(value % bound);
            }
            public void Dispose() { hmac.Dispose(); Array.Clear(block, 0, block.Length); }
        }
    }
}
