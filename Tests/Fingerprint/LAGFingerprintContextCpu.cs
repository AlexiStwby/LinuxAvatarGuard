// SPDX-License-Identifier: MIT
// Standalone managed host. Compile only this and GuardFingerprintContext.cs, without Unity assemblies.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LinuxAvatarGuard;

static class LAGFingerprintContextCpu
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static void Reject(Action action, string name)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; } catch (FormatException) { rejected = true; }
        Check(rejected, name);
    }
    static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
    static byte[] Reference(string purpose, string channel, string build, string binding, byte[] identity, byte[] seed, int length)
    {
        // Known-input test vector, independent construction of the versioned framing.
        using (var input = new MemoryStream())
        {
            byte[] head = Encoding.UTF8.GetBytes("LAG/fingerprint/v1/" + purpose + "\0" + channel + "\0" + build + "\0" + binding + "\0");
            input.Write(head, 0, head.Length); input.Write(identity, 0, identity.Length);
            using (var hmac = new HMACSHA256(seed)) return hmac.ComputeHash(input.ToArray()).Take(length).ToArray();
        }
    }
    public static int Main(string[] args)
    {
        try
        {
            string build = new string('a', 32), binding = new string('b', 64);
            byte[] identity = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            byte[] seed = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
            string meshCodewordVector = null, meshCarrierVector = null;
            using (var context = GuardFingerprintContext.CreateReproducible(build, identity, seed))
            {
                Check(GuardFingerprintContext.FingerprintBits == 256, "256 bit identity");
                Check(GuardFingerprintContext.ComponentCodewordBits == 128, "128 bit per-component codeword");
                var carrier = context.DeriveCarrierSeed(GuardFingerprintChannel.Mesh, binding);
                var codeword = context.DeriveCodeword(GuardFingerprintChannel.Mesh, binding);
                Check(carrier.Length == 32 && codeword.Length == 16, "lengths");
                Check(carrier.SequenceEqual(Reference("carrier", "mesh", build, binding, identity, seed, 32)), "carrier vector");
                Check(codeword.SequenceEqual(Reference("codeword", "mesh", build, binding, identity, seed, 16)), "codeword vector");
                Check(context.DeriveCodeword(GuardFingerprintChannel.Texture, binding).SequenceEqual(Reference("codeword", "texture", build, binding, identity, seed, 16)), "texture vector");
                Check(!carrier.Take(16).SequenceEqual(codeword), "purpose separation");
                Check(!carrier.SequenceEqual(context.DeriveCarrierSeed(GuardFingerprintChannel.Texture, binding)), "channel separation");
                Check(!codeword.SequenceEqual(context.DeriveCodeword(GuardFingerprintChannel.Mesh, new string('c', 64))), "binding separation");
                using (var other = GuardFingerprintContext.CreateReproducible(new string('c', 32), identity, seed))
                    Check(!codeword.SequenceEqual(other.DeriveCodeword(GuardFingerprintChannel.Mesh, binding)), "build separation");
                identity[0] ^= 255; seed[0] ^= 255;
                Check(codeword.SequenceEqual(context.DeriveCodeword(GuardFingerprintChannel.Mesh, binding)), "caller arrays isolated");
                var privateRecord = context.ExportPrivateRecord();
                Check(privateRecord.fingerprintBase64.Length == 44 && privateRecord.carrierSeedBase64.Length == 44, "private fields");
                using (var restored = GuardFingerprintContext.RestorePrivateRecord(privateRecord, build))
                {
                    Check(restored.CreatedUtc == context.CreatedUtc, "timestamp preserved");
                    Check(restored.DeriveCodeword(GuardFingerprintChannel.Mesh, binding).SequenceEqual(codeword), "restore codeword");
                    Check(restored.DeriveCarrierSeed(GuardFingerprintChannel.Mesh, binding).SequenceEqual(carrier), "restore carrier");
                }
                var publicRecord = context.PublicSummary();
                Check(publicRecord.state == "IdentityOnly" && publicRecord.markedMeshes == 0 && publicRecord.markedTextures == 0 && !publicRecord.verifierCalibrated, "no protection claim");
                var publicFields = typeof(GuardFingerprintSummary).GetFields().Select(f => f.Name).ToArray();
                Check(!publicFields.Any(n => n.Contains("Base64") || n.Contains("Seed") || n.Contains("fingerprint")), "public allowlist has no secrets");
                Check(!context.ToString().Contains(privateRecord.fingerprintBase64), "safe display");
                Reject(() => GuardFingerprintContext.RestorePrivateRecord(privateRecord, new string('c', 32)), "wrong expected build");
                privateRecord.schema++; Reject(() => GuardFingerprintContext.RestorePrivateRecord(privateRecord, build), "unknown schema"); privateRecord.schema--;
                privateRecord.state = "Marked"; Reject(() => GuardFingerprintContext.RestorePrivateRecord(privateRecord, build), "unknown state"); privateRecord.state = "IdentityOnly";
                privateRecord.recordType = "other"; Reject(() => GuardFingerprintContext.RestorePrivateRecord(privateRecord, build), "unknown type"); privateRecord.recordType = "LAG-Fingerprint-Identity-v1";
                string timestamp = privateRecord.createdUtc; privateRecord.createdUtc = "2026-10-03"; Reject(() => GuardFingerprintContext.RestorePrivateRecord(privateRecord, build), "non UTC date"); privateRecord.createdUtc = timestamp;
                privateRecord.fingerprintBase64 = new string('!', 44); Reject(() => GuardFingerprintContext.RestorePrivateRecord(privateRecord, build), "malformed Base64");
                Reject(() => context.DeriveCodeword(GuardFingerprintChannel.Mesh, "binding\0mesh"), "binding delimiter injection");
                Reject(() => context.DeriveCodeword((GuardFingerprintChannel)123, binding), "unknown channel");
                carrier[0] ^= 255; Check(!carrier.SequenceEqual(context.DeriveCarrierSeed(GuardFingerprintChannel.Mesh, binding)), "returned arrays isolated");
                meshCodewordVector = Hex(codeword); meshCarrierVector = Hex(context.DeriveCarrierSeed(GuardFingerprintChannel.Mesh, binding));
            }
            Reject(() => GuardFingerprintContext.CreateReproducible("bad", identity, seed), "invalid build");
            Reject(() => GuardFingerprintContext.CreateReproducible(new string('A', 32), identity, seed), "canonical lowercase");
            Reject(() => GuardFingerprintContext.CreateReproducible(build, new byte[16], seed), "short identity");
            Reject(() => GuardFingerprintContext.CreateReproducible(build, identity, new byte[16]), "short seed");
            using (var first = GuardFingerprintContext.CreateRandom(build))
            using (var second = GuardFingerprintContext.CreateRandom(build))
            {
                Check(first.ExportPrivateRecord().fingerprintBase64 != second.ExportPrivateRecord().fingerprintBase64, "random identities");
                Check(first.ExportPrivateRecord().carrierSeedBase64 != second.ExportPrivateRecord().carrierSeedBase64, "random seeds");
            }
            var dead = GuardFingerprintContext.CreateRandom(build); dead.Dispose(); dead.Dispose();
            bool disposed = false; try { dead.ExportPrivateRecord(); } catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, "disposed context rejects exports");
            if (args.Length == 1) File.WriteAllText(args[0], "{\"checks\":" + checks + ",\"graphicsUsed\":false,\"meshCodewordVector\":\"" + meshCodewordVector + "\",\"meshCarrierVector\":\"" + meshCarrierVector + "\"}");
            Console.WriteLine("Fingerprint CPU context: " + checks + " checks passed. Graphics API not used.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
