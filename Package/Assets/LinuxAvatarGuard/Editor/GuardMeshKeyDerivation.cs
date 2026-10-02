// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LinuxAvatarGuard
{
    public enum MeshDerivationPurpose { Program = 1, Payload = 2 }

    public static class MeshKeyDerivation
    {
        public const int Version = 1;
        internal static bool IsHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
        // RFC 5869, SHA-256 extract + expand. Exposed for published known-answer vectors, not a password KDF.
        public static byte[] HkdfSha256(byte[] ikm, byte[] salt, byte[] info, int length)
        {
            if (ikm == null || length < 1 || length > 255 * 32) throw new ArgumentException("HKDF input/length inválido.");
            info = info ?? Array.Empty<byte>();
            byte[] prk = null, block = Array.Empty<byte>(), input = null;
            var result = new byte[length];
            try
            {
                using (var extract = new HMACSHA256(salt == null || salt.Length == 0 ? new byte[32] : salt)) prk = extract.ComputeHash(ikm);
                using (var expand = new HMACSHA256(prk))
                    for (int written = 0, counter = 1; written < length; counter++)
                    {
                        input = new byte[block.Length + info.Length + 1];
                        Array.Copy(block, input, block.Length); Array.Copy(info, 0, input, block.Length, info.Length);
                        input[input.Length - 1] = (byte)counter;
                        Array.Clear(block, 0, block.Length); block = expand.ComputeHash(input);
                        Array.Clear(input, 0, input.Length); input = null;
                        int count = Math.Min(block.Length, length - written); Array.Copy(block, 0, result, written, count); written += count;
                    }
                return result;
            }
            catch { Array.Clear(result, 0, result.Length); throw; }
            finally
            {
                if (prk != null) Array.Clear(prk, 0, prk.Length);
                Array.Clear(block, 0, block.Length); if (input != null) Array.Clear(input, 0, input.Length);
            }
        }
        public static byte[] Derive(byte[] master, string buildId, MeshBindingIdentity binding, MeshDerivationPurpose purpose)
        {
            if (binding == null || !Enum.IsDefined(typeof(MeshDerivationPurpose), purpose)) throw new ArgumentException("Binding/dominio inválido.");
            binding.Validate();
            return DeriveContext(master, buildId, binding.StableId, (int)purpose, 32);
        }
        internal static byte[] DeriveContext(byte[] master, string buildId, string stableId, int purpose, int length)
        {
            if (master == null || master.Length != 32 || !IsHex(buildId, 32) || (!IsHex(stableId, 64) && stableId != "runtime-global"))
                throw new ArgumentException("Contexto de derivación inválido.");
            byte[] salt;
            using (var hash = SHA256.Create()) salt = hash.ComputeHash(Encoding.UTF8.GetBytes("LAG/build-salt/v1\0" + buildId));
            using (var stream = new MemoryStream())
            {
                using (var output = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    output.Write("LAG/binding-kdf/v1"); output.Write(Version);
                    output.Write(StaticPolymorphicCodecV1.Id); output.Write(StaticPolymorphicCodecV1.Version);
                    output.Write(buildId); output.Write(stableId); output.Write(purpose); output.Write(length);
                }
                return HkdfSha256(master, salt, stream.ToArray(), length);
            }
        }
    }
}
