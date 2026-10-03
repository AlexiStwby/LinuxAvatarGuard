// SPDX-License-Identifier: MIT
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LinuxAvatarGuard
{
    public enum GuardFingerprintChannel { Mesh, Texture }

    // Identity foundation only. No mesh/texture has been marked by creating this record.
    [Serializable]
    public sealed class GuardFingerprintPrivateRecord
    {
        public int schema = 1;
        public string recordType = "LAG-Fingerprint-Identity-v1";
        public string state = "IdentityOnly";
        public string buildId, createdUtc, fingerprintBase64, carrierSeedBase64;
    }

    [Serializable]
    public sealed class GuardFingerprintSummary
    {
        public int schema = 1;
        public string state = "IdentityOnly";
        public string buildId;
        public int markedMeshes, markedTextures;
        public bool verifierCalibrated;
    }

    // Pure managed research API: no Unity calls, graphics API, file IO or runtime keys.
    // Caller-owned returned secrets must stay in private local storage, never in Unity assets.
    public sealed class GuardFingerprintContext : IDisposable
    {
        public const int SchemaVersion = 1;
        public const int FingerprintBits = 256;
        public const int ComponentCodewordBits = 128;
        public string BuildId { get; }
        public string CreatedUtc { get; }
        readonly byte[] fingerprint, carrierSeed;
        bool disposed;

        GuardFingerprintContext(string buildId, byte[] fingerprint, byte[] carrierSeed, string createdUtc)
        {
            if (!Hex(buildId, 32)) throw new ArgumentException("Fingerprint: BuildID hexadecimal de 32 caracteres requerido.");
            if (fingerprint == null || fingerprint.Length != 32 || carrierSeed == null || carrierSeed.Length != 32)
                throw new ArgumentException("Fingerprint: identidad y seed independientes de 32 bytes requeridas.");
            if (!DateTime.TryParseExact(createdUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) || date.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Fingerprint: timestamp UTC inválido.");
            BuildId = buildId; CreatedUtc = createdUtc;
            this.fingerprint = (byte[])fingerprint.Clone(); this.carrierSeed = (byte[])carrierSeed.Clone();
        }

        public static GuardFingerprintContext CreateRandom(string buildId)
        {
            var identity = new byte[32]; var seed = new byte[32];
            try
            {
                using (var random = RandomNumberGenerator.Create()) { random.GetBytes(identity); random.GetBytes(seed); }
                return CreateReproducible(buildId, identity, seed);
            }
            finally { Array.Clear(identity, 0, identity.Length); Array.Clear(seed, 0, seed.Length); }
        }

        // Explicit reproduction for research/restoration. Never use fixed example secrets in a build.
        public static GuardFingerprintContext CreateReproducible(string buildId, byte[] fingerprint, byte[] carrierSeed) =>
            new GuardFingerprintContext(buildId, fingerprint, carrierSeed, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        void Alive() { if (disposed) throw new ObjectDisposedException(nameof(GuardFingerprintContext)); }
        static bool Hex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
        static string Channel(GuardFingerprintChannel channel)
        {
            switch (channel)
            {
                case GuardFingerprintChannel.Mesh: return "mesh";
                case GuardFingerprintChannel.Texture: return "texture";
                default: throw new ArgumentOutOfRangeException(nameof(channel));
            }
        }
        byte[] Derive(string purpose, GuardFingerprintChannel channel, string bindingId, int length)
        {
            Alive();
            if (!Hex(bindingId, 64)) throw new ArgumentException("Fingerprint: binding privado hexadecimal de 64 caracteres requerido.");
            var header = Encoding.UTF8.GetBytes("LAG/fingerprint/v1/" + purpose + "\0" + Channel(channel) + "\0" + BuildId + "\0" + bindingId + "\0");
            var input = new byte[header.Length + fingerprint.Length]; byte[] full = null;
            try
            {
                Buffer.BlockCopy(header, 0, input, 0, header.Length);
                Buffer.BlockCopy(fingerprint, 0, input, header.Length, fingerprint.Length);
                using (var hmac = new HMACSHA256(carrierSeed)) full = hmac.ComputeHash(input);
                var result = new byte[length]; Buffer.BlockCopy(full, 0, result, 0, length); return result;
            }
            finally { Array.Clear(input, 0, input.Length); if (full != null) Array.Clear(full, 0, full.Length); }
        }

        public byte[] DeriveCarrierSeed(GuardFingerprintChannel channel, string bindingId) => Derive("carrier", channel, bindingId, 32);
        public byte[] DeriveCodeword(GuardFingerprintChannel channel, string bindingId) => Derive("codeword", channel, bindingId, ComponentCodewordBits / 8);

        // Private DTO contains secrets. This API deliberately does not serialize or save it automatically.
        public GuardFingerprintPrivateRecord ExportPrivateRecord()
        {
            Alive();
            return new GuardFingerprintPrivateRecord { buildId = BuildId, createdUtc = CreatedUtc,
                fingerprintBase64 = Convert.ToBase64String(fingerprint), carrierSeedBase64 = Convert.ToBase64String(carrierSeed) };
        }
        static byte[] Decode(string value)
        {
            if (value == null || value.Length != 44) throw new ArgumentException("Fingerprint: secreto Base64 privado inválido.");
            byte[] bytes = Convert.FromBase64String(value);
            if (bytes.Length == 32 && Convert.ToBase64String(bytes) == value) return bytes;
            Array.Clear(bytes, 0, bytes.Length); throw new ArgumentException("Fingerprint: secreto Base64 no canónico.");
        }
        public static GuardFingerprintContext RestorePrivateRecord(GuardFingerprintPrivateRecord record, string expectedBuildId)
        {
            if (record == null || record.schema != SchemaVersion || record.recordType != "LAG-Fingerprint-Identity-v1" ||
                record.state != "IdentityOnly" || !Hex(expectedBuildId, 32) || record.buildId != expectedBuildId)
                throw new ArgumentException("Fingerprint: registro privado, versión o BuildID incompatible.");
            byte[] identity = null, seed = null;
            try
            {
                identity = Decode(record.fingerprintBase64); seed = Decode(record.carrierSeedBase64);
                return new GuardFingerprintContext(record.buildId, identity, seed, record.createdUtc);
            }
            finally { if (identity != null) Array.Clear(identity, 0, identity.Length); if (seed != null) Array.Clear(seed, 0, seed.Length); }
        }
        public GuardFingerprintSummary PublicSummary()
        {
            Alive(); return new GuardFingerprintSummary { buildId = BuildId };
        }
        public override string ToString() => "Fingerprint IdentityOnly (" + BuildId + ")";
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            Array.Clear(fingerprint, 0, fingerprint.Length); Array.Clear(carrierSeed, 0, carrierSeed.Length);
        }
    }
}
