// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LinuxAvatarGuard;

// Existing Mono/libc only; isolated fixtures outside Git. Never reads avatar keys.
public static class LAGFingerprintPrivateStoreCpu
{
    [DllImport("libc", SetLastError = true)] static extern int chmod(string path, uint mode);
    [DllImport("libc", SetLastError = true)] static extern int symlink(string target, string path);
    [DllImport("libc", SetLastError = true)] static extern int link(string source, string path);
    [DllImport("libc", SetLastError = true)] static extern int mkfifo(string path, uint mode);
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static void Reject(Action action, string name)
    {
        bool rejected = false;
        try { action(); } catch (IOException) { rejected = true; } catch (ArgumentException) { rejected = true; } catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, name);
    }
    static string NewFolder(string path)
    { Directory.CreateDirectory(path); if (chmod(path, 448) != 0) throw new IOException("fixture chmod"); return path; }
    static void WritePrivate(string path, string text)
    { File.WriteAllText(path, text, new UTF8Encoding(false)); if (chmod(path, 384) != 0) throw new IOException("fixture chmod"); }
    public static int Main(string[] args)
    {
        string root = NewFolder(Path.Combine("/var/tmp", "lag-private-store-tests-" + Guid.NewGuid().ToString("N")));
        const string synthetic = "{\"synthetic\":true,\"message\":\"ASCII / Español / 日本語\"}";
        try
        {
            string live = Path.Combine(root, "live.lagprivate");
            using (var lease = GuardFingerprintPrivateStore.Create(live, synthetic))
            {
                Check(GuardFingerprintPrivateStore.Read(live) == synthetic, "UTF8 roundtrip");
                Reject(() => GuardFingerprintPrivateStore.Create(live, "other"), "no overwrite");
                Check(GuardFingerprintPrivateStore.Read(live) == synthetic, "existing preserved");
                lease.Commit();
            }
            Check(File.Exists(live), "commit persists");
            string rollback = Path.Combine(root, "rollback.lagprivate");
            using (GuardFingerprintPrivateStore.Create(rollback, synthetic)) { Check(File.Exists(rollback), "new pending exists"); }
            Check(!File.Exists(rollback), "rollback removes owned file");
            using (var lease = GuardFingerprintPrivateStore.Create(rollback, synthetic))
            { lease.Dispose(); Reject(lease.Commit, "disposed lease rejected"); lease.Dispose(); }
            string edit = Path.Combine(root, "edit.lagprivate"); var edited = GuardFingerprintPrivateStore.Create(edit, synthetic);
            WritePrivate(edit, "edited"); Reject(edited.Commit, "edited commit rejected"); Reject(edited.Dispose, "edited rollback rejected"); edited.Dispose();
            Check(File.ReadAllText(edit) == "edited", "user edit preserved");
            string replace = Path.Combine(root, "replace.lagprivate"); var replaced = GuardFingerprintPrivateStore.Create(replace, synthetic);
            File.Delete(replace); WritePrivate(replace, "replacement"); Reject(replaced.Commit, "replaced commit rejected"); Reject(replaced.Dispose, "replaced inode rejected"); replaced.Dispose();
            Check(File.ReadAllText(replace) == "replacement", "replacement preserved");
            using (GuardFingerprintPrivateStore.Create(rollback, synthetic)) { File.Delete(rollback); }
            Check(!File.Exists(rollback), "already removed rollback benign");
            string parent = NewFolder(Path.Combine(root, "parent"));
            var moved = GuardFingerprintPrivateStore.Create(Path.Combine(parent, "moved.lagprivate"), synthetic);
            Directory.Move(parent, parent + "-original"); NewFolder(parent);
            Reject(moved.Commit, "replaced parent commit rejected"); moved.Dispose();
            Check(Directory.GetFiles(parent + "-original").Length == 0, "rollback pinned to original parent");
            string symbolic = Path.Combine(root, "symlink.lagprivate"); Check(symlink(live, symbolic) == 0, "fixture symlink");
            Reject(() => GuardFingerprintPrivateStore.Read(symbolic), "no symlink read");
            Reject(() => GuardFingerprintPrivateStore.Create(symbolic, synthetic), "no symlink overwrite");
            Check(GuardFingerprintPrivateStore.Read(live) == synthetic, "symlink target preserved");
            string hard = Path.Combine(root, "hard.lagprivate"); Check(link(live, hard) == 0, "fixture hardlink");
            Reject(() => GuardFingerprintPrivateStore.Read(hard), "hardlink rejected"); File.Delete(hard);
            string fifo = Path.Combine(root, "fifo.lagprivate"); Check(mkfifo(fifo, 384) == 0, "fixture FIFO");
            Reject(() => GuardFingerprintPrivateStore.Read(fifo), "FIFO rejected without blocking");
            chmod(live, 420); Reject(() => GuardFingerprintPrivateStore.Read(live), "0644 rejected"); chmod(live, 384);
            chmod(root, 493); Reject(() => GuardFingerprintPrivateStore.Read(live), "0755 parent rejected"); chmod(root, 448);
            string bad = Path.Combine(root, "bad.lagprivate"); File.WriteAllBytes(bad, new byte[] { 255, 255 }); chmod(bad, 384);
            Reject(() => GuardFingerprintPrivateStore.Read(bad), "invalid UTF8 rejected");
            string oversized = Path.Combine(root, "oversized.lagprivate");
            Reject(() => GuardFingerprintPrivateStore.Create(oversized, new string('x', GuardFingerprintPrivateStore.MaximumBytes + 1)), "size limit create");
            Check(!File.Exists(oversized), "oversized create has no file");
            WritePrivate(oversized, new string('x', GuardFingerprintPrivateStore.MaximumBytes + 1));
            Reject(() => GuardFingerprintPrivateStore.Read(oversized), "size limit read");
            string boundary = Path.Combine(root, "boundary.lagprivate"); string limit = new string('x', GuardFingerprintPrivateStore.MaximumBytes);
            using (GuardFingerprintPrivateStore.Create(boundary, limit)) { Check(GuardFingerprintPrivateStore.Read(boundary) == limit, "1 MiB inclusive boundary"); }
            Reject(() => GuardFingerprintPrivateStore.Create("relative.lagprivate", synthetic), "absolute required");
            Reject(() => GuardFingerprintPrivateStore.Create(Path.Combine(root, "file.json"), synthetic), "private extension required");
            Reject(() => GuardFingerprintPrivateStore.Create(rollback, ""), "empty rejected");
            Reject(() => GuardFingerprintPrivateStore.Create(Path.Combine(root, "missing", "key.lagprivate"), synthetic), "missing parents rejected");
            Check(!Directory.Exists(Path.Combine(root, "missing")), "parents not auto created");
            foreach (string name in new[] { "Assets", "Packages", "git", "unity" })
            {
                string blocked = NewFolder(Path.Combine(root, name));
                if (name == "git") File.WriteAllText(Path.Combine(blocked, ".git"), "gitdir: elsewhere");
                if (name == "unity") Directory.CreateDirectory(Path.Combine(blocked, "ProjectSettings"));
                Reject(() => GuardFingerprintPrivateStore.Create(Path.Combine(blocked, "key.lagprivate"), synthetic), "unsafe parent " + name);
            }
            string alias = Path.Combine(root, "alias"); Check(symlink(root, alias) == 0, "fixture parent symlink");
            Reject(() => GuardFingerprintPrivateStore.Read(Path.Combine(alias, "live.lagprivate")), "parent symlink rejected");
            Check(Directory.GetFiles(root, ".lag-fingerprint-*").Length == 0, "temporary files cleaned");
            int descriptors = Directory.GetFiles("/proc/self/fd").Length;
            for (int i = 0; i < 32; i++) using (GuardFingerprintPrivateStore.Create(rollback, synthetic)) { GuardFingerprintPrivateStore.Read(rollback); }
            Check(Directory.GetFiles("/proc/self/fd").Length == descriptors, "success and rollback descriptor lifecycle");
            for (int i = 0; i < 16; i++) Reject(() => GuardFingerprintPrivateStore.Create(live, synthetic), "repeated existing rejection");
            Check(Directory.GetFiles("/proc/self/fd").Length == descriptors, "failure descriptor lifecycle");
            string report = "{\"schema\":1,\"checks\":" + checks + ",\"gpuUsed\":false,\"unityStarted\":false,\"syntheticOnly\":true,\"fixtureRemoved\":true}";
            if (args.Length == 1) File.WriteAllText(args[0], report + "\n", new UTF8Encoding(false));
            Console.WriteLine("LAG_FINGERPRINT_PRIVATE_STORE_CPU_SUCCESS " + checks + " checks"); return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
