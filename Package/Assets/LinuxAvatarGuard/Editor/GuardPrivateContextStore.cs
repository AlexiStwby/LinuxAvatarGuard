// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace LinuxAvatarGuard
{
    // Linux file-descriptor operations keep all traversal/create/read/link operations from following symlinks.
    // A hostile process running as the same user can still read memory/files; this is not a local-user sandbox.
    static class GuardPrivateContextStore
    {
        const int ReadOnly = 0, WriteOnly = 1, CreateNew = 0x40 | 0x80, DirectoryFlag = 0x10000, NoFollow = 0x20000, CloseExec = 0x80000, NonBlock = 0x800;
        const uint FolderMode = 448, FileMode = 384; // 0700 / 0600, from the first create operation.
        [DllImport("libc", SetLastError = true)] static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] static extern int openat(int fd, string name, int flags, uint mode);
        [DllImport("libc", SetLastError = true)] static extern int mkdirat(int fd, string name, uint mode);
        [DllImport("libc", SetLastError = true)] static extern int fchmod(int fd, uint mode);
        [DllImport("libc", SetLastError = true)] static extern int fsync(int fd);
        [DllImport("libc", SetLastError = true)] static extern int linkat(int oldfd, string oldname, int newfd, string newname, int flags);
        [DllImport("libc", SetLastError = true)] static extern int unlinkat(int fd, string name, int flags);
        [DllImport("libc", SetLastError = true)] static extern long write(int fd, byte[] bytes, UIntPtr count);
        [DllImport("libc", SetLastError = true)] static extern long read(int fd, byte[] bytes, UIntPtr count);
        [DllImport("libc")] static extern int close(int fd);
        [DllImport("libc")] static extern uint geteuid();
        // Linux UAPI statx is a fixed 256-byte layout, including reserved space (x64/arm64).
        [StructLayout(LayoutKind.Explicit, Size = 256)] struct Statx
        {
            [FieldOffset(0)] public uint mask;
            [FieldOffset(16)] public uint links;
            [FieldOffset(20)] public uint uid;
            [FieldOffset(28)] public ushort mode;
        }
        [DllImport("libc", SetLastError = true)] static extern int statx(int fd, string path, int flags, uint mask, out Statx result);
        static IOException Error(string operation) => new IOException("Contexto privado: " + operation + " (errno " + Marshal.GetLastWin32Error() + ").");
        static void RequirePrivate(int fd, bool directory, bool checkMode)
        {
            if (statx(fd, "", 0x1000, 0xf, out var info) != 0) throw Error("verificar permisos del descriptor");
            int type = directory ? 0x4000 : 0x8000;
            if ((info.mask & 0xf) != 0xf || info.uid != geteuid() || (info.mode & 0xf000) != type ||
                (!directory && info.links != 1) || (checkMode && (info.mode & 0xfff) != (directory ? FolderMode : FileMode)))
                throw new IOException("El contexto requiere dueño actual, permisos privados y archivos regulares sin otros hard links.");
        }
        static string Root()
        {
            if (Application.platform != RuntimePlatform.LinuxEditor) throw new InvalidOperationException("El contexto privado requiere Linux Editor.");
            string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string data = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "share");
            var path = Path.GetFullPath(Path.Combine(data, "linux-avatar-guard", "prototype-builds"));
            string project = Path.GetDirectoryName(Application.dataPath);
            if (path == project || path.StartsWith(project + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("El contexto privado debe quedar fuera del proyecto Unity.");
            for (var parent = new DirectoryInfo(path); parent != null; parent = parent.Parent)
                if (Directory.Exists(Path.Combine(parent.FullName, ".git")) || File.Exists(Path.Combine(parent.FullName, ".git")))
                    throw new InvalidOperationException("El contexto privado debe quedar fuera de un repositorio Git.");
            return path;
        }
        static int OpenFolder(string root, bool create)
        {
            int fd = open("/", ReadOnly | DirectoryFlag | NoFollow | CloseExec);
            if (fd < 0) throw Error("abrir raíz");
            try
            {
                var parts = root.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (create && i >= parts.Length - 2 && mkdirat(fd, parts[i], FolderMode) != 0 && Marshal.GetLastWin32Error() != 17)
                        throw Error("crear directorio"); // EEXIST is checked by the no-follow open below.
                    int next = openat(fd, parts[i], ReadOnly | DirectoryFlag | NoFollow | CloseExec, 0);
                    if (next < 0) throw Error("abrir directorio sin enlaces");
                    close(fd); fd = next;
                    if (i >= parts.Length - 2)
                    {
                        RequirePrivate(fd, true, !create);
                        if (create && fchmod(fd, FolderMode) != 0) throw Error("asegurar directorio");
                    }
                }
                int result = fd; fd = -1; return result;
            }
            finally { if (fd >= 0) close(fd); }
        }
        static string Name(string id)
        { if (!MeshKeyDerivation.IsHex(id, 32)) throw new ArgumentException("BuildID inválido."); return id + ".lagprivate"; }
        public static string Create(string id, string json)
        {
            var name = Name(id); var root = Root(); var temp = ".context-" + Guid.NewGuid().ToString("N");
            var bytes = Encoding.UTF8.GetBytes(json); int folder = -1, fd = -1;
            bool linked = false;
            try
            {
                if (bytes.Length > 1024 * 1024) throw new IOException("Contexto privado demasiado grande.");
                folder = OpenFolder(root, true);
                fd = openat(folder, temp, WriteOnly | CreateNew | NoFollow | CloseExec, FileMode);
                if (fd < 0) throw Error("crear archivo privado");
                if (fchmod(fd, FileMode) != 0) throw Error("asegurar archivo privado");
                RequirePrivate(fd, false, true);
                int offset = 0;
                while (offset < bytes.Length)
                {
                    var chunk = new byte[bytes.Length - offset]; Array.Copy(bytes, offset, chunk, 0, chunk.Length);
                    long count;
                    try { count = write(fd, chunk, (UIntPtr)chunk.Length); }
                    finally { Array.Clear(chunk, 0, chunk.Length); }
                    if (count < 0 && Marshal.GetLastWin32Error() == 4) continue;
                    if (count <= 0) throw Error("escribir archivo privado"); offset += (int)count;
                }
                if (fsync(fd) != 0) throw Error("sincronizar archivo privado"); close(fd); fd = -1;
                // Atomic, create-only publication: a reused BuildID never overwrites an existing file/link.
                if (linkat(folder, temp, folder, name, 0) != 0) throw Error("publicar archivo privado sin reemplazar");
                linked = true;
                if (unlinkat(folder, temp, 0) != 0 || fsync(folder) != 0) throw Error("sincronizar directorio privado");
                return Path.Combine(root, name);
            }
            catch
            { if (linked && folder >= 0) { unlinkat(folder, name, 0); fsync(folder); } throw; }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
                if (fd >= 0) close(fd);
                if (folder >= 0) { unlinkat(folder, temp, 0); close(folder); }
            }
        }
        public static string Read(string id)
        {
            var name = Name(id); int folder = -1, fd = -1; var chunk = new byte[4096];
            using (var stream = new MemoryStream())
            try
            {
                folder = OpenFolder(Root(), false); fd = openat(folder, name, ReadOnly | NoFollow | CloseExec | NonBlock, 0);
                if (fd < 0) throw Error("leer archivo sin enlaces");
                RequirePrivate(fd, false, true);
                while (true)
                {
                    long count = read(fd, chunk, (UIntPtr)chunk.Length);
                    if (count < 0 && Marshal.GetLastWin32Error() == 4) continue;
                    if (count < 0) throw Error("leer archivo privado"); if (count == 0) break;
                    if (stream.Length + count > 1024 * 1024) throw new IOException("Contexto privado demasiado grande.");
                    stream.Write(chunk, 0, (int)count);
                }
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            }
            finally
            {
                Array.Clear(chunk, 0, chunk.Length); Array.Clear(stream.GetBuffer(), 0, (int)stream.Length);
                if (fd >= 0) close(fd); if (folder >= 0) close(folder);
            }
        }
    }
}
