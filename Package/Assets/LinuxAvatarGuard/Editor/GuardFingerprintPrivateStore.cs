// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LinuxAvatarGuard
{
    // Linux-only, create-only private blobs. JSON validation belongs to the typed caller.
    // All parent directories must already exist. Does not chmod existing directories or overwrite files.
    public static class GuardFingerprintPrivateStore
    {
        const int ReadOnly = 0, WriteOnly = 1, CreateNew = 0x40 | 0x80, DirectoryFlag = 0x10000, NoFollow = 0x20000, CloseExec = 0x80000, NonBlock = 0x800;
        const uint FolderMode = 448, FileMode = 384, RequiredStat = 0x30f;
        public const int MaximumBytes = 1024 * 1024;
        [DllImport("libc", SetLastError = true)] static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] static extern int openat(int fd, string name, int flags, uint mode);
        [DllImport("libc", SetLastError = true)] static extern int fchmod(int fd, uint mode);
        [DllImport("libc", SetLastError = true)] static extern int fsync(int fd);
        [DllImport("libc", SetLastError = true)] static extern int linkat(int oldfd, string oldname, int newfd, string newname, int flags);
        [DllImport("libc", SetLastError = true)] static extern int unlinkat(int fd, string name, int flags);
        [DllImport("libc", SetLastError = true)] static extern long write(int fd, byte[] bytes, UIntPtr count);
        [DllImport("libc", SetLastError = true)] static extern long read(int fd, [Out] byte[] bytes, UIntPtr count);
        [DllImport("libc")] static extern int close(int fd);
        [DllImport("libc")] static extern uint geteuid();
        [StructLayout(LayoutKind.Explicit, Size = 256)] struct Statx
        {
            [FieldOffset(0)] public uint mask;
            [FieldOffset(16)] public uint links;
            [FieldOffset(20)] public uint uid;
            [FieldOffset(28)] public ushort mode;
            [FieldOffset(32)] public ulong inode;
            [FieldOffset(40)] public ulong size;
            [FieldOffset(136)] public uint deviceMajor;
            [FieldOffset(140)] public uint deviceMinor;
        }
        [DllImport("libc", SetLastError = true)] static extern int statx(int fd, string path, int flags, uint mask, out Statx result);
        static IOException Error(string operation) => new IOException("Fingerprint privado: " + operation + " (errno " + Marshal.GetLastWin32Error() + ").");
        static Statx Require(int fd, bool directory)
        {
            if (statx(fd, "", 0x1000, RequiredStat, out var info) != 0) throw Error("verificar descriptor");
            if ((info.mask & RequiredStat) != RequiredStat || info.uid != geteuid() || (info.mode & 0xf000) != (directory ? 0x4000 : 0x8000) ||
                (info.mode & 0xfff) != (directory ? FolderMode : FileMode) || (!directory && (info.links != 1 || info.size > MaximumBytes)))
                throw new IOException("Fingerprint privado: dueño, permisos, tipo, tamaño o links incompatibles.");
            return info;
        }
        static string PathCheck(string absolutePath)
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix || !Directory.Exists("/proc/self")) throw new PlatformNotSupportedException("Fingerprint privado requiere Linux.");
            if (absolutePath == null || !Path.IsPathRooted(absolutePath)) throw new ArgumentException("Fingerprint privado: ruta absoluta requerida.");
            string path = Path.GetFullPath(absolutePath);
            if (!path.EndsWith(".lagprivate", StringComparison.Ordinal)) throw new ArgumentException("Fingerprint privado: extensión .lagprivate requerida.");
            foreach (var segment in path.Split('/')) if (segment == "Assets" || segment == "Packages") throw new ArgumentException("Fingerprint privado: salida fuera de Assets/Packages requerida.");
            for (string parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (File.Exists(Path.Combine(parent, ".git")) || Directory.Exists(Path.Combine(parent, ".git")) || Directory.Exists(Path.Combine(parent, "ProjectSettings")))
                    throw new ArgumentException("Fingerprint privado: salida fuera de Git/proyectos Unity requerida.");
            return path;
        }
        static int Folder(string path)
        {
            int fd = open("/", ReadOnly | DirectoryFlag | NoFollow | CloseExec);
            if (fd < 0) throw Error("abrir raíz");
            try
            {
                foreach (string part in Path.GetDirectoryName(path).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int next = openat(fd, part, ReadOnly | DirectoryFlag | NoFollow | CloseExec, 0);
                    if (next < 0) throw Error("abrir directorio sin enlaces"); close(fd); fd = next;
                }
                Require(fd, true); int result = fd; fd = -1; return result;
            }
            finally { if (fd >= 0) close(fd); }
        }
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static byte[] ReadBytes(int fd)
        {
            Require(fd, false); var chunk = new byte[4096];
            using (var stream = new MemoryStream())
            try
            {
                while (true)
                {
                    long count = read(fd, chunk, (UIntPtr)chunk.Length);
                    if (count < 0 && Marshal.GetLastWin32Error() == 4) continue;
                    if (count < 0) throw Error("leer archivo privado"); if (count == 0) break;
                    if (stream.Length + count > MaximumBytes) throw new IOException("Fingerprint privado: tamaño excesivo.");
                    stream.Write(chunk, 0, (int)count);
                }
                return stream.ToArray();
            }
            finally { Array.Clear(chunk, 0, chunk.Length); Array.Clear(stream.GetBuffer(), 0, (int)stream.Length); }
        }
        public static string Read(string absolutePath)
        {
            string path = PathCheck(absolutePath); int folder = -1, fd = -1; byte[] bytes = null;
            try
            {
                folder = Folder(path); fd = openat(folder, Path.GetFileName(path), ReadOnly | NoFollow | CloseExec | NonBlock, 0);
                if (fd < 0) throw Error("abrir archivo privado"); bytes = ReadBytes(fd); return new UTF8Encoding(false, true).GetString(bytes);
            }
            finally { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); if (fd >= 0) close(fd); if (folder >= 0) close(folder); }
        }
        public static GuardFingerprintPrivateLease Create(string absolutePath, string json)
        {
            string path = PathCheck(absolutePath); if (string.IsNullOrEmpty(json)) throw new ArgumentException("Fingerprint privado: registro vacío.");
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(json); int folder = -1, fd = -1; bool linked = false;
            string temp = ".lag-fingerprint-" + Guid.NewGuid().ToString("N"), name = Path.GetFileName(path);
            try
            {
                if (bytes.Length > MaximumBytes) throw new IOException("Fingerprint privado: tamaño excesivo.");
                folder = Folder(path); fd = openat(folder, temp, WriteOnly | CreateNew | NoFollow | CloseExec, FileMode);
                if (fd < 0) throw Error("crear archivo temporal");
                if (fchmod(fd, FileMode) != 0) throw Error("asegurar permisos"); Require(fd, false);
                int offset = 0;
                while (offset < bytes.Length)
                {
                    var chunk = new byte[Math.Min(4096, bytes.Length - offset)]; Buffer.BlockCopy(bytes, offset, chunk, 0, chunk.Length); long count;
                    try { count = write(fd, chunk, (UIntPtr)chunk.Length); } finally { Array.Clear(chunk, 0, chunk.Length); }
                    if (count < 0 && Marshal.GetLastWin32Error() == 4) continue;
                    if (count <= 0) throw Error("escribir registro"); offset += (int)count;
                }
                var info = Require(fd, false);
                if (fsync(fd) != 0) throw Error("sincronizar archivo");
                if (linkat(folder, temp, folder, name, 0) != 0) throw Error("publicar sin sobrescritura"); linked = true;
                if (unlinkat(folder, temp, 0) != 0 || fsync(folder) != 0) throw Error("sincronizar directorio");
                var lease = new GuardFingerprintPrivateLease(path, folder, fd, name, info.inode, Hash(bytes)); folder = -1; fd = -1; return lease;
            }
            catch { if (linked && folder >= 0) { unlinkat(folder, name, 0); fsync(folder); } throw; }
            finally { Array.Clear(bytes, 0, bytes.Length); if (fd >= 0) close(fd); if (folder >= 0) { unlinkat(folder, temp, 0); close(folder); } }
        }
        public sealed class GuardFingerprintPrivateLease : IDisposable
        {
            public string Path { get; }
            readonly string name, hash;
            readonly ulong inode;
            int folder, originalFile;
            bool committed;
            internal GuardFingerprintPrivateLease(string path, int fd, int originalFile, string name, ulong inode, string hash)
            { Path = path; folder = fd; this.originalFile = originalFile; this.name = name; this.inode = inode; this.hash = hash; }
            void Verify()
            {
                int currentFolder = Folder(PathCheck(Path));
                try
                {
                    var original = Require(folder, true); var current = Require(currentFolder, true);
                    if (original.inode != current.inode || original.deviceMajor != current.deviceMajor || original.deviceMinor != current.deviceMinor)
                        throw new IOException("Fingerprint privado: directorio sustituido; se conserva.");
                }
                finally { close(currentFolder); }
                int fd = openat(folder, name, ReadOnly | NoFollow | CloseExec | NonBlock, 0);
                if (fd < 0) throw Error("comprobar registro pendiente"); byte[] bytes = null;
                try
                {
                    if (Require(fd, false).inode != inode) throw new IOException("Fingerprint privado: archivo sustituido; se conserva.");
                    bytes = ReadBytes(fd); if (Hash(bytes) != hash) throw new IOException("Fingerprint privado: archivo editado; se conserva.");
                }
                finally { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); close(fd); }
            }
            public void Commit() { if (folder < 0) throw new ObjectDisposedException(nameof(GuardFingerprintPrivateLease)); Verify(); committed = true; }
            public void Dispose()
            {
                if (folder < 0) return;
                try
                {
                    if (!committed)
                    {
                        int fd = openat(folder, name, ReadOnly | NoFollow | CloseExec | NonBlock, 0);
                        if (fd < 0 && Marshal.GetLastWin32Error() == 2) return;
                        if (fd < 0) throw Error("comprobar rollback"); byte[] bytes = null;
                        try
                        {
                            if (Require(fd, false).inode != inode) throw new IOException("Fingerprint privado: archivo sustituido; se conserva.");
                            bytes = ReadBytes(fd); if (Hash(bytes) != hash) throw new IOException("Fingerprint privado: archivo editado; se conserva.");
                        }
                        finally { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); close(fd); }
                        if (unlinkat(folder, name, 0) != 0 || fsync(folder) != 0) throw Error("retirar registro nuevo");
                    }
                }
                finally { close(folder); close(originalFile); folder = -1; originalFile = -1; }
            }
        }
    }
}
