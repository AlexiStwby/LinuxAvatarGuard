// SPDX-License-Identifier: MIT
#if UNITY_EDITOR && LAG_VRCSDK
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
namespace LinuxAvatarGuard
{
    [Serializable]
    public sealed class GuardProfile
    {
        public string buildId, name, prefabPath, scenePath, keyPath, toolsPath;
        public ProtectionBuildStatus status;
    }
    [Serializable]
    sealed class GuardProfileList
    {
        public List<GuardProfile> profiles = new List<GuardProfile>();
    }
    [Serializable]
    public sealed class GuardOscStatus
    {
        public int pid;
        public string buildId, state, message;
    }
    public static class GuardProfiles
    {
        public const string Version = GuardText.Version;
        [DllImport("libc", SetLastError = true)]
        static extern int chmod(string pathname, uint mode);
        public static string DataRoot
        {
            get {
                var x = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                return Path.Combine(
                    !string.IsNullOrEmpty(x) && Path.IsPathRooted(x)
                        ? x
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                                       ".local", "share"),
                    "linux-avatar-guard");
            }
        }
        static string Registry => Path.GetFullPath("UserSettings/LinuxAvatarGuardProfiles.json");
        public static string ToolRoot
        {
            get {
                var script = AssetDatabase.FindAssets("GuardWindow t:MonoScript")
                                 .Select(AssetDatabase.GUIDToAssetPath)
                                 .First(p => p.EndsWith("/GuardWindow.cs", StringComparison.Ordinal));
                return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(script), "../Tools"));
            }
        }
        static void SecureFolder(string path)
        {
            if (Application.platform != RuntimePlatform.LinuxEditor)
                throw new InvalidOperationException("Esta función requiere Linux.");
            Directory.CreateDirectory(path);
            if (chmod(path, 448) != 0)
                throw new IOException("No se pudieron asegurar los permisos privados.");
        }
        public static void WritePrivate(string path, string text)
        {
            SecureFolder(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream =
                           new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (chmod(temp, 384) != 0)
                        throw new IOException("No se pudo asegurar el archivo privado.");
                    using (var writer = new StreamWriter(stream)) writer.Write(text);
                }
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
        public static List<GuardProfile> All() =>
            File.Exists(Registry)
                ? JsonUtility.FromJson<GuardProfileList>(File.ReadAllText(Registry))?.profiles ??
                      new List<GuardProfile>()
                : new List<GuardProfile>();
        public static void Save(GuardProfile p)
        {
            var list = All();
            list.RemoveAll(x => x.buildId == p.buildId);
            list.Add(p);
            WritePrivate(Registry, JsonUtility.ToJson(new GuardProfileList { profiles = list }, true));
        }
        internal static string RegistrySnapshot() => File.Exists(Registry) ? File.ReadAllText(Registry) : null;
        internal static void RestoreRegistry(string snapshot)
        {
            if (snapshot != null) WritePrivate(Registry, snapshot);
            else if (File.Exists(Registry)) File.Delete(Registry);
        }
        public static GuardProfile Register(GuardBuildResult build, string name)
        {
            var key = JsonUtility.FromJson<GuardKeyFile>(File.ReadAllText(build.keyPath));
            if (!Regex.IsMatch(key.buildId ?? "", "^[0-9a-f]{32}$"))
                throw new InvalidOperationException("Identificador de generación inválido.");
            SecureFolder(DataRoot);
            var folder = Path.Combine(DataRoot, key.buildId);
            SecureFolder(folder);
            var p = new GuardProfile { buildId = key.buildId, name = name, prefabPath = build.prefabPath,
                                       keyPath = Path.Combine(folder, "key.json"), toolsPath = folder,
                                       status = string.IsNullOrEmpty(build.buildId) ? ProtectionBuildStatus.Legacy : ProtectionBuildStatus.Preparing };
            WritePrivate(p.keyPath, JsonUtility.ToJson(key, true));
            if (!string.IsNullOrEmpty(build.manifestPath))
                WritePrivate(Path.Combine(folder, "private-manifest.json"), File.ReadAllText(build.manifestPath));
            RefreshTools(p);
            Save(p);
            return p;
        }
        static string Quote(string arg) => "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        public static void RefreshTools(GuardProfile p)
        {
            WritePrivate(Path.Combine(p.toolsPath, "lag_osc.py"),
                         File.ReadAllText(Path.Combine(ToolRoot, "lag_osc.py")));
            var launcher = Path.Combine(p.toolsPath, "Desbloquear.sh");
            WritePrivate(
                launcher,
                "#!/usr/bin/env bash\nset -euo pipefail\nlag_folder=$(cd -- \"$(dirname -- \"$0\")\" " +
                    "&& pwd)\nexec python3 -u \"$lag_folder/lag_osc.py\" \"$lag_folder/key.json\" --watch " +
                    "--status-file \"$lag_folder/status.json\"\n");
            if (chmod(launcher, 448) != 0)
                throw new IOException("No se pudo habilitar el lanzador.");
            var desktop =
                "[Desktop Entry]\nType=Application\nName=Linux Avatar Guard\nComment=Desbloquear el " +
                "avatar mediante OSC local\nTerminal=true\nExec=" + Quote(FindPython()) + " -u " +
                Quote(Path.Combine(p.toolsPath, "lag_osc.py")) + " " + Quote(p.keyPath) +
                " --watch --status-file " + Quote(Path.Combine(p.toolsPath, "status.json")) + "\n";
            WritePrivate(Path.Combine(p.toolsPath, "Desbloquear.desktop"), desktop.Replace("%", "%%"));
            chmod(Path.Combine(p.toolsPath, "Desbloquear.desktop"), 448);
        }
        public static bool ValidAvatarId(string id) => Regex.IsMatch(
            id ?? "", @"^avtr_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
        public static GuardKeyFile Key(GuardProfile p) =>
            JsonUtility.FromJson<GuardKeyFile>(File.ReadAllText(p.keyPath));
        public static void RequirePrepared(GuardProfile p)
        {
            if (p == null || (p.status != ProtectionBuildStatus.Legacy && p.status != ProtectionBuildStatus.Ready))
                throw new InvalidOperationException("Preparación incompleta. Genera una copia nueva.");
            var path = "Assets/LinuxAvatarGuardGenerated/" + p.buildId + "/security-manifest.json";
            if (File.Exists(path))
            {
                var manifest = GuardBuildManifest.Read(path);
                if (manifest.buildId != p.buildId || manifest.schemaVersion != 1 ||
                    manifest.codecId != LegacyLinearCodecV1.Id || manifest.codecVersion != LegacyLinearCodecV1.Version ||
                    manifest.status != ProtectionBuildStatus.Ready.ToString())
                    throw new InvalidOperationException("Preparación incompleta. Genera una copia nueva.");
            }
            else if (p.status != ProtectionBuildStatus.Legacy)
                throw new InvalidOperationException("Preparación incompleta. Genera una copia nueva.");
        }
        public static void Link(GuardProfile p, string id)
        {
            if (!ValidAvatarId(id))
                throw new InvalidOperationException("Se necesita el ID avtr_... de tu avatar publicado.");
            var key = Key(p);
            key.avatarId = id;
            WritePrivate(p.keyPath, JsonUtility.ToJson(key, true));
        }
        public static string BuildId(GameObject root)
        {
            if (!root)
                return null;
            var path = PrefabUtility.IsPartOfPrefabAsset(root)
                           ? AssetDatabase.GetAssetPath(root)
                           : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            var m = Regex.Match(path ?? "", @"(?:^|/)LinuxAvatarGuardGenerated/([0-9a-f]{32})/");
            if (m.Success) return m.Groups[1].Value;
            // Simulators unpack prefab instances in Play Mode; the generated shader keeps the build ID.
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.sharedMaterials)
                {
                    var shader = Regex.Match(material && material.shader ? material.shader.name : "", @"^LinuxAvatarGuard/([0-9a-f]{32})/");
                    if (shader.Success) return shader.Groups[1].Value;
                }
            return null;
        }
        public static string FindPython()
        {
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin")
                         .Split(Path.PathSeparator))
            {
                var path = Path.Combine(folder, "python3");
                if (File.Exists(path))
                    return path;
            }
            throw new InvalidOperationException(
                "Instala Python 3 con el gestor de tu distribución para iniciar OSC.");
        }
        public static GuardOscStatus Status(GuardProfile p)
        {
            var file = Path.Combine(p.toolsPath, "status.json");
            if (!File.Exists(file))
                return null;
            try
            {
                return JsonUtility.FromJson<GuardOscStatus>(File.ReadAllText(file));
            }
            catch
            {
                return null;
            }
        }
        static bool OwnedProcess(GuardProfile p, GuardOscStatus s)
        {
            if (s == null || s.pid <= 0 || s.buildId != p.buildId)
                return false;
            try
            {
                var args = File.ReadAllText("/proc/" + s.pid + "/cmdline").Split('\0');
                return args.Contains(Path.Combine(p.toolsPath, "lag_osc.py")) && args.Contains(p.keyPath);
            }
            catch
            {
                return false;
            }
        }
        public static bool Running(GuardProfile p) => OwnedProcess(p, Status(p));
        public static void Start(GuardProfile p)
        {
            RequirePrepared(p);
            if (!ValidAvatarId(Key(p).avatarId))
                throw new InvalidOperationException("Primero vincula el avatar publicado.");
            if (Running(p))
                return;
            RefreshTools(p);
            Process.Start(
                new ProcessStartInfo { FileName = FindPython(),
                                       Arguments = "-u " + Quote(Path.Combine(p.toolsPath, "lag_osc.py")) +
                                                   " " + Quote(p.keyPath) + " --watch --status-file " +
                                                   Quote(Path.Combine(p.toolsPath, "status.json")),
                                       UseShellExecute = false, CreateNoWindow = true });
        }
        public static void Stop(GuardProfile p)
        {
            var s = Status(p);
            if (OwnedProcess(p, s))
                Process.GetProcessById(s.pid).Kill();
        }
    }
}
#endif
