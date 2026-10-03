// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LinuxAvatarGuard
{
    [Serializable] public sealed class GuardMetadataBlendshapes
    {
        public string rendererPath;
        public string[] names = Array.Empty<string>();
    }
    // Functional names require an explicit, closed-world opt-in. No heuristic selects them.
    public sealed class GuardMetadataOptions
    {
        public bool RenameAssetLabels = true, CleanAnimatorEditorMetadata = true, RenameAnimatorLayers;
        public string[] InternalObjectPaths = Array.Empty<string>();
        public string[] InternalParameters = Array.Empty<string>();
        public GuardMetadataBlendshapes[] InternalBlendshapes = Array.Empty<GuardMetadataBlendshapes>();
    }
    public enum GuardMetadataCheckpoint { AssetsCopied, NamesRemapped, PrefabSaved }
    [Serializable] public sealed class GuardMetadataName
    {
        public string kind, identity, original, renamed;
    }
    [Serializable] public sealed class GuardMetadataSummary
    {
        public int schemaVersion = 1, metadataGuardVersion = GuardMetadataGuard.Version;
        public string buildId, status, policy;
        public int assets, assetFiles, objects, blendshapes, parameters, animatorLayers, editorGraphs;
        public bool functionalNamesPreserved, geometryEncoded = false, sdkProcessed = false;
    }
    [Serializable] sealed class GuardMetadataPrivateMap
    {
        public int schemaVersion = 1;
        public string buildId;
        public GuardMetadataName[] names;
    }
    public sealed class GuardMetadataArtifact : IDisposable
    {
        public GameObject Root { get; }
        public string Folder { get; }
        public string PrefabPath => Folder + "/metadata.prefab";
        public string PublicManifestPath => Folder + "/metadata-manifest.json";
        public GuardMetadataSummary Summary { get; }
        public string PrivateMappingJson { get; }
        readonly Dictionary<Object, Object> copies;
        readonly Scene preview;
        internal GuardMetadataArtifact(GameObject root, string folder, GuardMetadataSummary summary,
            string privateJson, Dictionary<Object, Object> copies, Scene preview)
        { Root = root; Folder = folder; Summary = summary; PrivateMappingJson = privateJson; this.copies = copies; this.preview = preview; }
        public T CopyOf<T>(T source) where T : Object => copies.TryGetValue(source, out var copy) ?
            (T)copy : throw new ArgumentException("MetadataGuard: asset fuera de la copia.");
        public void SavePrivateMapping(string absolutePath) => GuardMetadataGuard.WritePrivateMap(absolutePath, PrivateMappingJson, Summary.buildId);
        public void Dispose() { if (Root) Object.DestroyImmediate(Root); if (preview.IsValid() && preview.isLoaded) EditorSceneManager.ClosePreviewScene(preview); }
    }
    public static class GuardMetadataGuard
    {
        public const int Version = 1;
        [DllImport("libc", SetLastError = true)] static extern int chmod(string path, uint mode);
        static readonly HashSet<string> ReservedParameters = new HashSet<string>(StringComparer.Ordinal) {
            "IsLocal", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight",
            "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright", "Grounded", "Seated",
            "AFK", "TrackingType", "VRMode", "MuteSelf", "InStation", "Earmuffs", "AvatarVersion", "ScaleModified",
            "ScaleFactor", "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent", "IsOnFriendsList"
        };
        static string Hash(byte[] bytes) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static string Identity(Object value)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long id) || string.IsNullOrEmpty(guid))
                throw new InvalidOperationException("MetadataGuard: guarda primero las dependencias de autoría.");
            return guid + "/" + id;
        }
        static bool VrcAsset(Object value) => value is ScriptableObject &&
            (value.GetType().Namespace ?? "").StartsWith("VRC.SDK3.Avatars.ScriptableObjects", StringComparison.Ordinal);
        static bool LabelAsset(Object value) => value is Mesh || value is Material || value is Motion ||
            value is RuntimeAnimatorController || value is AvatarMask;
        static bool CopiedAsset(Object value) => LabelAsset(value) || VrcAsset(value);
        static bool GraphAsset(Object value) => CopiedAsset(value) || value is AnimatorState || value is AnimatorStateMachine ||
            value is AnimatorTransitionBase || value is StateMachineBehaviour;
        static bool CoreComponent(Component c) => c is Transform || c is Animator || c is MeshFilter ||
            c is MeshRenderer || c is SkinnedMeshRenderer;
        static bool KnownAvatarComponent(Component c)
        {
            string type = c.GetType().FullName;
            return CoreComponent(c) || c is Collider || c is Rigidbody ||
                type == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor" || type == "VRC.Core.PipelineManager" ||
                type == "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone" ||
                type == "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider" ||
                type == "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender" ||
                type == "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver";
        }
        static void EnvironmentCheck()
        {
            if (Application.platform != RuntimePlatform.LinuxEditor || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan ||
                GraphicsSettings.currentRenderPipeline != null || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("MetadataGuard requiere Linux Editor, Vulkan, Built-in y Edit Mode sin compilación pendiente.");
        }
        static void NoLinks(string path)
        {
            for (string p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            {
                try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("MetadataGuard: no se permiten enlaces simbólicos en la salida."); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        static void NewOutput(string folder)
        {
            if (folder == null || !Regex.IsMatch(folder, @"\AAssets/LinuxAvatarGuardGenerated/Research/[A-Za-z0-9_-]{1,64}\z"))
                throw new ArgumentException("MetadataGuard: usa una carpeta nueva bajo Assets/LinuxAvatarGuardGenerated/Research/.");
            NoLinks(folder);
            if (Directory.Exists(folder) || File.Exists(folder) || File.Exists(folder + ".meta"))
                throw new IOException("MetadataGuard: se conserva la salida existente.");
        }
        static string MapJson(string buildId, List<GuardMetadataName> names) => JsonUtility.ToJson(new GuardMetadataPrivateMap {
            buildId = buildId, names = names.OrderBy(n => n.kind, StringComparer.Ordinal).ThenBy(n => n.identity, StringComparer.Ordinal).ToArray()
        }, true);
        // Keep reversal information outside every Assets/Packages tree, without changing OSC keys.
        public static void WritePrivateMap(string absolutePath, string json, string expectedBuildId = null)
        {
            if (!Path.IsPathRooted(absolutePath)) throw new ArgumentException("MetadataGuard: el respaldo necesita una ruta absoluta.");
            string path = Path.GetFullPath(absolutePath);
            if (path.Split(Path.DirectorySeparatorChar).Any(p => p == "Assets" || p == "Packages"))
                throw new InvalidOperationException("MetadataGuard: el mapa privado no pertenece a Assets/Packages.");
            NoLinks(path);
            var map = JsonUtility.FromJson<GuardMetadataPrivateMap>(json);
            if (map == null || map.schemaVersion != 1 || !Regex.IsMatch(map.buildId ?? "", @"\A[0-9a-f]{32}\z") || map.names == null)
                throw new ArgumentException("MetadataGuard: mapa privado inválido.");
            if (expectedBuildId != null && map.buildId != expectedBuildId)
                throw new InvalidOperationException("MetadataGuard: el mapa pertenece a otra generación.");
            if (File.Exists(path) && File.ReadAllText(path) != json) throw new IOException("MetadataGuard: se conserva el respaldo existente.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (chmod(Path.GetDirectoryName(path), 448) != 0) throw new IOException("MetadataGuard: permisos del directorio privado.");
            if (File.Exists(path) && chmod(path, 384) != 0) throw new IOException("MetadataGuard: permisos del mapa existente.");
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (chmod(temp, 384) != 0) throw new IOException("MetadataGuard: permisos del mapa privado.");
                    using (var writer = new StreamWriter(file)) writer.Write(json);
                }
                if (File.Exists(path)) File.Delete(temp); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        sealed class Names : IDisposable
        {
            readonly byte[] seed;
            readonly string buildId;
            readonly Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            readonly HashSet<string> allocated = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<GuardMetadataName> Records = new List<GuardMetadataName>();
            public Names(string id, byte[] supplied)
            {
                if (!Regex.IsMatch(id ?? "", @"\A[0-9a-f]{32}\z")) throw new ArgumentException("MetadataGuard: BuildID inválido.");
                buildId = id;
                if (supplied != null && supplied.Length != 32) throw new ArgumentException("MetadataGuard: seed de 32 bytes requerido.");
                seed = supplied == null ? new byte[32] : (byte[])supplied.Clone();
                if (supplied == null) using (var random = RandomNumberGenerator.Create()) random.GetBytes(seed);
            }
            public void Reserve(IEnumerable<string> existing) { foreach (var name in existing) allocated.Add(name); }
            public string Alias(string kind, string identity, string original)
            {
                string key = kind + "\0" + identity;
                if (aliases.TryGetValue(key, out var name)) return name;
                using (var h = new HMACSHA256(seed)) name = "n_" + BitConverter.ToString(h.ComputeHash(
                    Encoding.UTF8.GetBytes("LAG/metadata/v1\0" + buildId + "\0" + key))).Replace("-", "").ToLowerInvariant().Substring(0, 32);
                if (!allocated.Add(name)) throw new InvalidOperationException("MetadataGuard: colisión de nombres; se cancela la copia.");
                aliases.Add(key, name); Records.Add(new GuardMetadataName { kind = kind, identity = identity, original = original, renamed = name });
                return name;
            }
            public void Dispose() { Array.Clear(seed, 0, seed.Length); }
        }
        sealed class Snapshot
        {
            readonly GameObject root;
            readonly string hierarchy;
            readonly Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.Ordinal);
            readonly Dictionary<Object, string> assets = new Dictionary<Object, string>();
            readonly Dictionary<Object, string> components = new Dictionary<Object, string>();
            static string AssetHash(Object value) => value is Mesh mesh ? MeshBindingIdentity.ContentFingerprint(mesh) : Hash(Encoding.UTF8.GetBytes(EditorJsonUtility.ToJson(value)));
            static string Hierarchy(GameObject root) => string.Join("\n", root.GetComponentsInChildren<Transform>(true).SelectMany(t =>
                new[] { EditorJsonUtility.ToJson(t.gameObject) }.Concat(t.GetComponents<Component>().Select(c => c ? EditorJsonUtility.ToJson(c) : "missing"))));
            public Snapshot(GameObject value, bool requireClean = true)
            {
                root = value; hierarchy = Hierarchy(root);
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                { components[t.gameObject] = EditorJsonUtility.ToJson(t.gameObject); foreach (var c in t.GetComponents<Component>().Where(c => c)) components[c] = EditorJsonUtility.ToJson(c); }
                var dependencies = EditorUtility.CollectDependencies(new Object[] { root }).Where(o => o).ToArray();
                foreach (var dependency in dependencies.Concat(dependencies.OfType<AnimatorController>().SelectMany(c =>
                    AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(c)))).Where(o => o).Distinct())
                {
                    if (!dependency) continue;
                    string path = AssetDatabase.GetAssetPath(dependency);
                    // Native clones include live edits. File-copied graphs must match their saved files.
                    if (requireClean && GraphAsset(dependency) && !(dependency is Mesh || dependency is Material || dependency is AnimationClip ||
                        dependency is AvatarMask || dependency is AnimatorOverrideController) && EditorUtility.IsDirty(dependency))
                        throw new InvalidOperationException("MetadataGuard: guarda primero el asset modificado " + dependency.GetType().Name + " en " + AssetDatabase.GetAssetPath(dependency) + ".");
                    if (GraphAsset(dependency)) assets[dependency] = AssetHash(dependency);
                    foreach (string p in new[] { path, path + ".meta" })
                        if (!string.IsNullOrEmpty(path) && File.Exists(p) && !files.ContainsKey(p)) files.Add(p, Hash(File.ReadAllBytes(p)));
                }
            }
            public void Validate()
            {
                if (!root) throw new InvalidOperationException("MetadataGuard: raíz retirada durante la preparación.");
                if (Hierarchy(root) != hierarchy)
                {
                    var changed = components.FirstOrDefault(p => !p.Key || EditorJsonUtility.ToJson(p.Key) != p.Value);
                    throw new InvalidOperationException("MetadataGuard: jerarquía modificada durante la preparación: " + (changed.Key ? changed.Key.GetType().Name : "objetos/componentes añadidos o retirados") + ".");
                }
                var changedFile = files.FirstOrDefault(p => !File.Exists(p.Key) || Hash(File.ReadAllBytes(p.Key)) != p.Value);
                if (changedFile.Key != null) throw new InvalidOperationException("MetadataGuard: archivo fuente modificado durante la preparación: " + changedFile.Key);
                var changedAsset = assets.FirstOrDefault(p => !p.Key || AssetHash(p.Key) != p.Value);
                if (!ReferenceEquals(changedAsset.Key, null)) throw new InvalidOperationException("MetadataGuard: asset en memoria modificado durante la preparación: " + changedAsset.Key.GetType().Name + " " + AssetDatabase.GetAssetPath(changedAsset.Key));
            }
        }
        sealed class Plan
        {
            public readonly Dictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<Mesh, Dictionary<string, string>> Shapes = new Dictionary<Mesh, Dictionary<string, string>>();
            public readonly List<AnimatorController> Controllers = new List<AnimatorController>();
            public bool Functional, LabelsSafe, RenameLayers;
            public Plan(GameObject root, GuardMetadataOptions options, Names names)
            {
                if (options.InternalObjectPaths == null || options.InternalParameters == null || options.InternalBlendshapes == null)
                    throw new ArgumentException("MetadataGuard: selecciones nulas.");
                Functional = options.InternalObjectPaths.Length > 0 || options.InternalParameters.Length > 0 || options.InternalBlendshapes.Length > 0 || options.RenameAnimatorLayers;
                RenameLayers = options.RenameAnimatorLayers;
                var components = root.GetComponentsInChildren<Component>(true);
                if (components.Any(c => !c)) throw new InvalidOperationException("MetadataGuard: hay componentes ausentes.");
                if (components.Any(c => !KnownAvatarComponent(c)))
                    throw new InvalidOperationException("MetadataGuard: integración no revisada; se conserva la entrada sin generar una copia.");
                var direct = EditorUtility.CollectDependencies(new Object[] { root }).Where(o => o).ToArray();
                // Unity's runtime dependency walk can omit editor-only BlendTrees/behaviours.
                var deps = direct.Concat(direct.OfType<AnimatorController>().SelectMany(c =>
                    AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(c)))).Where(o => o).Distinct().ToArray();
                Controllers.AddRange(deps.OfType<AnimatorController>().Distinct());
                bool events = deps.OfType<AnimationClip>().Any(c => AnimationUtility.GetAnimationEvents(c).Length > 0);
                bool behaviours = deps.OfType<StateMachineBehaviour>().Any() || deps.OfType<AnimatorState>().Any(s => s.behaviours.Length > 0) ||
                    deps.OfType<AnimatorStateMachine>().Any(s => s.behaviours.Length > 0);
                LabelsSafe = components.All(KnownAvatarComponent) && !events && !behaviours;
                names.Reserve(deps.Select(o => o.name).Concat(Controllers.SelectMany(c => c.layers.Select(l => l.name))));
                if (Functional)
                {
                    if (components.Any(c => !CoreComponent(c)) || deps.Any(VrcAsset) || events || behaviours ||
                        components.OfType<Animator>().Any(a => a.avatar) || components.OfType<Animator>().Count() > 1 ||
                        components.OfType<Animator>().Any(a => a.transform != root.transform))
                        throw new InvalidOperationException("MetadataGuard: nombres funcionales solo en una raíz genérica revisada sin SDK, Avatar, scripts, behaviours ni eventos externos.");
                    ValidateHierarchy(root);
                }
                if (!Functional) { Paths.Add("", ""); return; }
                names.Reserve(root.GetComponentsInChildren<Transform>(true).Select(t => t.name)
                    .Concat(Controllers.SelectMany(c => c.parameters.Select(p => p.name)))
                    .Concat(root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(s => s.sharedMesh).SelectMany(s =>
                        Enumerable.Range(0, s.sharedMesh.blendShapeCount).Select(s.sharedMesh.GetBlendShapeName))));
                var selected = new HashSet<string>(options.InternalObjectPaths, StringComparer.Ordinal);
                if (selected.Count != options.InternalObjectPaths.Length) throw new ArgumentException("MetadataGuard: rutas duplicadas.");
                foreach (string path in selected)
                    if (string.IsNullOrEmpty(path) || !root.transform.Find(path)) throw new ArgumentException("MetadataGuard: objeto interno no encontrado.");
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    string path = AnimationUtility.CalculateTransformPath(t, root.transform);
                    if (t == root.transform) { Paths.Add(path, path); continue; }
                    string parent = AnimationUtility.CalculateTransformPath(t.parent, root.transform);
                    string node = selected.Contains(path) ? names.Alias("object", path, t.name) : t.name;
                    Paths.Add(path, string.IsNullOrEmpty(Paths[parent]) ? node : Paths[parent] + "/" + node);
                }
                var parameterSet = new HashSet<string>(options.InternalParameters, StringComparer.Ordinal);
                if (parameterSet.Count != options.InternalParameters.Length) throw new ArgumentException("MetadataGuard: parámetros duplicados.");
                foreach (string parameter in parameterSet)
                {
                    if (string.IsNullOrEmpty(parameter) || ReservedParameters.Contains(parameter) || parameter.StartsWith("LAG_", StringComparison.Ordinal) ||
                        Regex.IsMatch(parameter, @"\A(?:VRC|FT[/_]|vrc\.|OSC[/_])") || Controllers.Count != 1 || !Controllers[0].parameters.Any(p => p.name == parameter))
                        throw new InvalidOperationException("MetadataGuard: parámetro reservado, externo o no demostrado interno.");
                    Parameters.Add(parameter, names.Alias("parameter", Identity(Controllers[0]) + "/" + parameter, parameter));
                }
                foreach (var selection in options.InternalBlendshapes)
                {
                    var t = selection == null || string.IsNullOrEmpty(selection.rendererPath) ? null : root.transform.Find(selection.rendererPath);
                    var skin = t ? t.GetComponent<SkinnedMeshRenderer>() : null;
                    if (!skin || !skin.sharedMesh || selection.names == null || selection.names.Length == 0 || selection.names.Distinct().Count() != selection.names.Length)
                        throw new ArgumentException("MetadataGuard: selección de blendshape inválida.");
                    if (!Shapes.TryGetValue(skin.sharedMesh, out var map)) Shapes.Add(skin.sharedMesh, map = new Dictionary<string, string>(StringComparer.Ordinal));
                    foreach (string shape in selection.names)
                    {
                        if (string.IsNullOrEmpty(shape) || skin.sharedMesh.GetBlendShapeIndex(shape) < 0 || Regex.IsMatch(shape, @"\A(?:vrc[._]|viseme|Viseme|eyeBlink|EyeBlink)"))
                            throw new InvalidOperationException("MetadataGuard: blendshape reservado o ausente.");
                        map[shape] = names.Alias("blendshape", Identity(skin.sharedMesh) + "/" + shape, shape);
                    }
                }
                if (Shapes.Count > 0 && deps.OfType<AnimationClip>().Any(c => AnimationUtility.GetObjectReferenceCurveBindings(c).Any(b => b.propertyName.Contains("m_Mesh"))))
                    throw new InvalidOperationException("MetadataGuard: no se renombran morphs con reemplazos de malla animados.");
            }
            static void ValidateHierarchy(GameObject root)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (string.IsNullOrEmpty(t.name) || t.name.Contains("/") || t.Cast<Transform>().GroupBy(c => c.name).Any(g => g.Count() > 1))
                        throw new InvalidOperationException("MetadataGuard: rutas ambiguas; se conservan los nombres.");
            }
            public string Parameter(string name) => name != null && Parameters.TryGetValue(name, out var renamed) ? renamed : name;
        }
        sealed class Copier
        {
            readonly string folder;
            readonly Names names;
            readonly bool labelNames;
            readonly Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<Object, Object> Copies = new Dictionary<Object, Object>();
            public Copier(string folder, Names names, bool labels) { this.folder = folder; this.names = names; labelNames = labels; }
            public Object Copy(Object original)
            {
                if (!original) return original;
                if (!CopiedAsset(original)) return Copies.TryGetValue(original, out var local) ? local : original;
                if (Copies.TryGetValue(original, out var existing)) return existing;
                string source = AssetDatabase.GetAssetPath(original), identity = Identity(original);
                string token = names.Alias("asset-file", identity, source);
                Object result;
                if (original is Mesh || original is Material || original is AnimationClip || original is AvatarMask || original is AnimatorOverrideController)
                {
                    result = Object.Instantiate(original); result.hideFlags = HideFlags.None;
                    Copies.Add(original, result);
                    if (labelNames && LabelAsset(original)) result.name = names.Alias("asset", identity, original.name);
                    string extension = original is Material ? ".mat" : original is AnimationClip ? ".anim" : original is AnimatorOverrideController ? ".overrideController" : original is AvatarMask ? ".mask" : ".asset";
                    AssetDatabase.CreateAsset(result, folder + "/" + token + extension);
                    if (original is AnimatorOverrideController aoc)
                    {
                        var copied = (AnimatorOverrideController)result;
                        copied.runtimeAnimatorController = (RuntimeAnimatorController)Copy(aoc.runtimeAnimatorController);
                        var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(); aoc.GetOverrides(pairs);
                        copied.ApplyOverrides(pairs.Select(p => new KeyValuePair<AnimationClip, AnimationClip>((AnimationClip)Copy(p.Key), (AnimationClip)Copy(p.Value))).ToList());
                    }
                    Remap(result); return result;
                }
                if (!files.TryGetValue(source, out var target))
                {
                    target = folder + "/" + token + Path.GetExtension(source);
                    files.Add(source, target);
                    var originals = AssetDatabase.LoadAllAssetsAtPath(source);
                    File.Copy(source, target); AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceSynchronousImport);
                    var copies = AssetDatabase.LoadAllAssetsAtPath(target);
                    foreach (var item in originals.Where(i => i))
                    {
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(item, out string guid, out long localId);
                        var copy = copies.SingleOrDefault(c => c && c.GetType() == item.GetType() && LocalId(c) == localId);
                        if (!copy) throw new InvalidOperationException("MetadataGuard: subasset sin correspondencia.");
                        Copies[item] = copy;
                        if (labelNames && LabelAsset(item)) copy.name = names.Alias("asset", Identity(item), item.name);
                    }
                    foreach (var item in copies.Where(i => i)) Remap(item);
                }
                if (!Copies.TryGetValue(original, out result)) throw new InvalidOperationException("MetadataGuard: asset sin copia.");
                return result;
            }
            static long LocalId(Object value) { AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long id); return id; }
            public void Remap(Object value)
            {
                var so = new SerializedObject(value); var p = so.GetIterator();
                while (p.Next(true))
                {
                    if (p.propertyType != SerializedPropertyType.ObjectReference || p.name == "m_Script") continue;
                    var old = p.objectReferenceValue;
                    if (old && !AssetDatabase.GetAssetPath(old).StartsWith(folder + "/", StringComparison.Ordinal)) p.objectReferenceValue = Copy(old);
                }
                so.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(value);
            }
        }
        static void RenameShapes(Mesh mesh, Dictionary<string, string> names)
        {
            var frames = new List<Tuple<string, float, Vector3[], Vector3[], Vector3[]>>();
            for (int s = 0; s < mesh.blendShapeCount; s++) for (int f = 0; f < mesh.GetBlendShapeFrameCount(s); f++)
            {
                var v = new Vector3[mesh.vertexCount]; var n = new Vector3[mesh.vertexCount]; var t = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(s, f, v, n, t);
                string name = mesh.GetBlendShapeName(s);
                frames.Add(Tuple.Create(names.TryGetValue(name, out var renamed) ? renamed : name, mesh.GetBlendShapeFrameWeight(s, f), v, n, t));
            }
            mesh.ClearBlendShapes(); foreach (var f in frames) mesh.AddBlendShapeFrame(f.Item1, f.Item2, f.Item3, f.Item4, f.Item5);
            EditorUtility.SetDirty(mesh);
        }
        static EditorCurveBinding Binding(GameObject source, Plan plan, EditorCurveBinding binding)
        {
            string originalPath = binding.path;
            if (plan.Paths.TryGetValue(originalPath, out var path)) binding.path = path;
            else if (plan.Functional) throw new InvalidOperationException("MetadataGuard: binding fuera de la jerarquía revisada.");
            if (binding.type == typeof(Animator) && originalPath == "") binding.propertyName = plan.Parameter(binding.propertyName);
            if (binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
            {
                var skin = (string.IsNullOrEmpty(originalPath) ? source.transform : source.transform.Find(originalPath))?.GetComponent<SkinnedMeshRenderer>();
                if (!skin || !skin.sharedMesh) throw new InvalidOperationException("MetadataGuard: binding de morph sin renderer.");
                string shape = binding.propertyName.Substring(11);
                if (skin.sharedMesh.GetBlendShapeIndex(shape) < 0) throw new InvalidOperationException("MetadataGuard: binding de morph ausente.");
                if (plan.Shapes.TryGetValue(skin.sharedMesh, out var shapes) && shapes.TryGetValue(shape, out var renamed)) binding.propertyName = "blendShape." + renamed;
            }
            return binding;
        }
        static void RemapClip(GameObject source, Plan plan, AnimationClip original, AnimationClip copy, Copier copier)
        {
            if (!plan.Functional) return;
            var floats = AnimationUtility.GetCurveBindings(original);
            foreach (var b in AnimationUtility.GetCurveBindings(copy)) AnimationUtility.SetEditorCurve(copy, b, null);
            foreach (var b in floats) AnimationUtility.SetEditorCurve(copy, Binding(source, plan, b), AnimationUtility.GetEditorCurve(original, b));
            var objects = AnimationUtility.GetObjectReferenceCurveBindings(original);
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(copy)) AnimationUtility.SetObjectReferenceCurve(copy, b, null);
            foreach (var b in objects)
            {
                var frames = AnimationUtility.GetObjectReferenceCurve(original, b);
                for (int i = 0; i < frames.Length; i++) frames[i].value = copier.Copy(frames[i].value);
                AnimationUtility.SetObjectReferenceCurve(copy, Binding(source, plan, b), frames);
            }
            EditorUtility.SetDirty(copy);
        }
        static void Controller(AnimatorController controller, string sourceIdentity, Plan plan, Names names, bool clean, GuardMetadataSummary summary)
        {
            var parameters = controller.parameters;
            foreach (var p in parameters) p.name = plan.Parameter(p.name);
            controller.parameters = parameters;
            // Assign the full layer array back; changing a getter result does not persist it.
            var layers = controller.layers;
            if (plan.RenameLayers) foreach (var layer in layers)
            {
                string original = layer.name; layer.name = names.Alias("layer", sourceIdentity + "/" + original, original);
                if (layer.stateMachine && layer.stateMachine.name == original) layer.stateMachine.name = layer.name;
                summary.animatorLayers++;
            }
            controller.layers = layers;
            var seen = new HashSet<AnimatorStateMachine>();
            foreach (var layer in layers) Machine(layer.stateMachine, plan, clean, seen, summary);
            EditorUtility.SetDirty(controller);
        }
        static void Transition(AnimatorTransitionBase transition, Plan plan)
        {
            var conditions = transition.conditions;
            for (int i = 0; i < conditions.Length; i++) conditions[i].parameter = plan.Parameter(conditions[i].parameter);
            transition.conditions = conditions; EditorUtility.SetDirty(transition);
        }
        static void Machine(AnimatorStateMachine machine, Plan plan, bool clean, HashSet<AnimatorStateMachine> seen, GuardMetadataSummary summary)
        {
            if (!machine || !seen.Add(machine)) return;
            foreach (var transition in machine.anyStateTransitions.Cast<AnimatorTransitionBase>().Concat(machine.entryTransitions)) Transition(transition, plan);
            var states = machine.states;
            for (int i = 0; i < states.Length; i++)
            {
                var state = states[i].state;
                state.speedParameter = plan.Parameter(state.speedParameter); state.timeParameter = plan.Parameter(state.timeParameter);
                state.cycleOffsetParameter = plan.Parameter(state.cycleOffsetParameter); state.mirrorParameter = plan.Parameter(state.mirrorParameter);
                foreach (var transition in state.transitions) Transition(transition, plan);
                if (clean) states[i].position = Vector3.zero;
                EditorUtility.SetDirty(state);
            }
            var children = machine.stateMachines;
            for (int i = 0; i < children.Length; i++)
            {
                foreach (var transition in machine.GetStateMachineTransitions(children[i].stateMachine)) Transition(transition, plan);
                Machine(children[i].stateMachine, plan, clean, seen, summary);
                if (clean) children[i].position = Vector3.zero;
            }
            if (clean)
            {
                machine.states = states; machine.stateMachines = children;
                machine.anyStatePosition = machine.entryPosition = machine.exitPosition = machine.parentStateMachinePosition = Vector3.zero;
                summary.editorGraphs++;
            }
            EditorUtility.SetDirty(machine);
        }
        static void SaveFolder(string folder)
        {
            foreach (string path in AssetDatabase.FindAssets("", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Distinct())
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path).Where(o => o)) AssetDatabase.SaveAssetIfDirty(obj);
        }
        public static GuardMetadataArtifact Prepare(GameObject source, string buildId, string outputFolder,
            GuardMetadataOptions options = null, byte[] seed = null, Action<GuardMetadataCheckpoint> checkpoint = null)
        {
            EnvironmentCheck(); NewOutput(outputFolder);
            if (!source || source.transform.parent) throw new ArgumentException("MetadataGuard: selecciona una raíz independiente.");
            options = options ?? new GuardMetadataOptions();
            using (var names = new Names(buildId, seed))
            {
                var snapshot = new Snapshot(source); var plan = new Plan(source, options, names);
                var summary = new GuardMetadataSummary { buildId = buildId, status = "Preparing", policy = plan.Functional ? "explicit-generic-internals" : "conservative-asset-labels", functionalNamesPreserved = !plan.Functional };
                GameObject copy = null, anchor = null; Scene preview = default; bool owned = false;
                try
                {
                    Directory.CreateDirectory(outputFolder + "/Assets"); owned = true;
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    preview = EditorSceneManager.NewPreviewScene();
                    anchor = EditorUtility.CreateGameObjectWithHideFlags("MetadataGuard scratch", HideFlags.HideAndDontSave);
                    SceneManager.MoveGameObjectToScene(anchor, preview);
                    copy = Object.Instantiate(source, anchor.transform, false); copy.transform.SetParent(null, true);
                    Object.DestroyImmediate(anchor); anchor = null;
                    copy.name = source.name; copy.SetActive(false);
                    if (PrefabUtility.IsPartOfPrefabInstance(copy)) PrefabUtility.UnpackPrefabInstance(copy, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    var copier = new Copier(outputFolder + "/Assets", names, options.RenameAssetLabels && plan.LabelsSafe);
                    foreach (var component in copy.GetComponentsInChildren<Component>(true)) copier.Remap(component);
                    checkpoint?.Invoke(GuardMetadataCheckpoint.AssetsCopied); snapshot.Validate();
                    foreach (var pair in plan.Shapes) RenameShapes((Mesh)copier.Copies[pair.Key], pair.Value);
                    foreach (var pair in copier.Copies.ToArray()) if (pair.Key is AnimationClip clip) RemapClip(source, plan, clip, (AnimationClip)pair.Value, copier);
                    foreach (var pair in copier.Copies.ToArray()) if (pair.Key is AvatarMask mask && plan.Functional)
                    {
                        var copied = (AvatarMask)pair.Value;
                        for (int i = 0; i < mask.transformCount; i++)
                        {
                            string path = mask.GetTransformPath(i);
                            if (!plan.Paths.TryGetValue(path, out var renamed)) throw new InvalidOperationException("MetadataGuard: AvatarMask fuera de la jerarquía revisada.");
                            copied.SetTransformPath(i, renamed);
                        }
                        EditorUtility.SetDirty(copied);
                    }
                    foreach (var controller in plan.Controllers) Controller((AnimatorController)copier.Copies[controller], Identity(controller), plan, names,
                        options.CleanAnimatorEditorMetadata && plan.LabelsSafe, summary);
                    foreach (var tree in copier.Copies.Values.OfType<BlendTree>())
                    {
                        tree.blendParameter = plan.Parameter(tree.blendParameter); tree.blendParameterY = plan.Parameter(tree.blendParameterY);
                        var children = tree.children;
                        for (int i = 0; i < children.Length; i++) children[i].directBlendParameter = plan.Parameter(children[i].directBlendParameter);
                        tree.children = children; EditorUtility.SetDirty(tree);
                    }
                    foreach (var originalSkin in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        string path = AnimationUtility.CalculateTransformPath(originalSkin.transform, source.transform);
                        var skin = (path == "" ? copy.transform : copy.transform.Find(path)).GetComponent<SkinnedMeshRenderer>();
                        skin.localBounds = originalSkin.localBounds;
                        if (originalSkin.sharedMesh) for (int i = 0; i < originalSkin.sharedMesh.blendShapeCount; i++) skin.SetBlendShapeWeight(i, originalSkin.GetBlendShapeWeight(i));
                    }
                    // Rename descendants from deepest to shallowest so original paths remain resolvable.
                    foreach (var pair in plan.Paths.OrderByDescending(p => p.Key.Count(c => c == '/')))
                        if (pair.Key != pair.Value && pair.Key != "") copy.transform.Find(pair.Key).name = pair.Value.Split('/').Last();
                    summary.assets = names.Records.Count(n => n.kind == "asset"); summary.assetFiles = names.Records.Count(n => n.kind == "asset-file"); summary.objects = names.Records.Count(n => n.kind == "object");
                    summary.blendshapes = names.Records.Count(n => n.kind == "blendshape"); summary.parameters = plan.Parameters.Count;
                    checkpoint?.Invoke(GuardMetadataCheckpoint.NamesRemapped); snapshot.Validate();
                    copy.SetActive(source.activeSelf); SaveFolder(outputFolder);
                    var outputSnapshot = new Snapshot(copy, false);
                    var prefab = PrefabUtility.SaveAsPrefabAsset(copy, outputFolder + "/metadata.prefab");
                    if (!prefab) throw new IOException("MetadataGuard: prefab no guardado.");
                    var prefabSnapshot = new Snapshot(prefab, false);
                    checkpoint?.Invoke(GuardMetadataCheckpoint.PrefabSaved); snapshot.Validate(); outputSnapshot.Validate(); prefabSnapshot.Validate();
                    foreach (var dependency in EditorUtility.CollectDependencies(new Object[] { copy }))
                        if (dependency && CopiedAsset(dependency) && !AssetDatabase.GetAssetPath(dependency).StartsWith(outputFolder + "/", StringComparison.Ordinal))
                            throw new InvalidOperationException("MetadataGuard: referencia de autoría sin copiar.");
                    summary.status = "Ready";
                    File.WriteAllText(outputFolder + "/metadata-manifest.json", JsonUtility.ToJson(summary, true));
                    AssetDatabase.ImportAsset(outputFolder + "/metadata-manifest.json", ImportAssetOptions.ForceSynchronousImport); snapshot.Validate(); outputSnapshot.Validate(); prefabSnapshot.Validate();
                    return new GuardMetadataArtifact(copy, outputFolder, summary, MapJson(buildId, names.Records), copier.Copies, preview);
                }
                catch
                {
                    if (copy) Object.DestroyImmediate(copy);
                    if (anchor) Object.DestroyImmediate(anchor);
                    if (preview.IsValid() && preview.isLoaded) EditorSceneManager.ClosePreviewScene(preview);
                    if (owned && !AssetDatabase.DeleteAsset(outputFolder) && Directory.Exists(outputFolder))
                        throw new IOException("MetadataGuard: no se pudo retirar la salida parcial.");
                    throw;
                }
            }
        }
        // Builder-only: every mutable object and file is already owned by its transaction.
        internal static GuardMetadataSummary ApplyOwnedLabels(GameObject copy, string folder, string buildId, out string privateJson)
        {
            using (var names = new Names(buildId, null))
            {
                var plan = new Plan(copy, new GuardMetadataOptions(), names);
                var summary = new GuardMetadataSummary { buildId = buildId, status = "Ready", policy = "conservative-asset-labels", functionalNamesPreserved = true };
                foreach (string path in AssetDatabase.FindAssets("", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Distinct().ToArray())
                {
                    var main = AssetDatabase.LoadMainAssetAtPath(path);
                    if (!main || !CopiedAsset(main)) continue;
                    string token = names.Alias("asset-file", Identity(main), path);
                    if (plan.LabelsSafe) foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path).Where(LabelAsset))
                    { obj.name = names.Alias("asset", Identity(obj), obj.name); EditorUtility.SetDirty(obj); }
                    string target = Path.GetDirectoryName(path).Replace('\\', '/') + "/" + token + Path.GetExtension(path);
                    foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path).Where(o => o)) AssetDatabase.SaveAssetIfDirty(obj);
                    string error = AssetDatabase.MoveAsset(path, target);
                    if (!string.IsNullOrEmpty(error)) throw new IOException("MetadataGuard: " + error);
                }
                summary.assets = names.Records.Count(n => n.kind == "asset"); summary.assetFiles = names.Records.Count(n => n.kind == "asset-file"); privateJson = MapJson(buildId, names.Records); return summary;
            }
        }
    }
}
