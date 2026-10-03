// SPDX-License-Identifier: MIT
#if UNITY_EDITOR && LAG_VRCSDK
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;
namespace LinuxAvatarGuard
{
    public static class GuardSetup
    {
        public static GameObject AvatarRoot(GameObject selected)
        {
            if (!selected)
                return null;
            var d = selected.GetComponentInParent<VRCAvatarDescriptor>();
            return d ? d.gameObject : selected;
        }
        public static GuardProfile Prepare(GameObject selected, float strength = .1f) => Prepare(selected, strength, null);
        // A checkpoint observer also lets integration tests inject failures at completed boundaries.
        public static GuardProfile Prepare(GameObject selected, float strength, Action<PreparationCheckpoint> checkpoint)
            => PrepareCore(selected, strength, checkpoint, false);
        public static GuardProfile PrepareWithMetadata(GameObject selected, float strength = .1f, Action<PreparationCheckpoint> checkpoint = null)
            => PrepareCore(selected, strength, checkpoint, true);
        static GuardProfile PrepareCore(GameObject selected, float strength, Action<PreparationCheckpoint> checkpoint, bool metadata)
        {
            var source = AvatarRoot(selected);
            if (GuardProfiles.BuildId(source) != null)
                throw new InvalidOperationException("Esta copia ya está protegida. Selecciona tu avatar de " +
                                                    "trabajo para generar otra versión.");
            if (Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Any(s => s.isLoaded && string.IsNullOrEmpty(s.path) && !EditorSceneManager.IsPreviewScene(s)))
                throw new InvalidOperationException("Guarda primero las escenas sin nombre. Los cambios pendientes se conservan.");
            var snapshot = GuardProfiles.RegistrySnapshot();
            var dataRoot = GuardProfiles.DataRoot;
            bool skinned = source && source.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 0;
            var result = metadata ? GuardBuilder.BuildWithMetadata(source, strength, skinned) : GuardBuilder.Build(source, strength, skinned);
            var privateFolder = Path.Combine(dataRoot, result.buildId);
            bool ownsRegistration = false;
            GuardProfile profile = null;
            Scene scene = default;
            var previous = SceneManager.GetActiveScene();
            try
            {
                if (Directory.Exists(privateFolder) || GuardProfiles.All().Any(p => p.buildId == result.buildId))
                    throw new InvalidOperationException("Ya existe un respaldo para este BuildID; se conserva.");
                ownsRegistration = true;
                checkpoint?.Invoke(PreparationCheckpoint.AssetsBuilt);
                profile = GuardProfiles.Register(result, source.name);
                checkpoint?.Invoke(PreparationCheckpoint.ProfileRegistered);
                scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Additive);
                SceneManager.SetActiveScene(scene);
                var root = (GameObject)PrefabUtility.InstantiatePrefab(
                    AssetDatabase.LoadAssetAtPath<GameObject>(profile.prefabPath), scene);
                root.name = source.name + "_LAG";
                var camera = scene.GetRootGameObjects()
                                 .SelectMany(g => g.GetComponentsInChildren<Camera>())
                                 .FirstOrDefault();
                var rs = source.GetComponentsInChildren<Renderer>(true);
                var b = rs[0].bounds;
                foreach (var r in rs.Skip(1))
                    b.Encapsulate(r.bounds);
                if (camera)
                {
                    camera.transform.position =
                        b.center + Vector3.forward * Mathf.Max(3, b.size.magnitude * 2);
                    camera.transform.LookAt(b.center);
                    camera.orthographic = true;
                    camera.orthographicSize = Mathf.Max(.1f, Mathf.Max(b.extents.x, b.extents.y) * 1.2f);
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = new Color(.055f, .065f, .085f);
                }
                profile.scenePath = Path.GetDirectoryName(profile.prefabPath).Replace('\\', '/') +
                                    "/AvatarGuard-Upload.unity";
                if (!EditorSceneManager.SaveScene(scene, profile.scenePath))
                    throw new IOException("No se pudo guardar la escena de subida.");
                checkpoint?.Invoke(PreparationCheckpoint.SceneSaved);
                GuardBuildManifest.Complete(result, profile);
                return profile;
            }
            catch (Exception failure)
            {
                // Close the generated scene before deleting its assets. The user's active scene is restored.
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                try
                {
                    if (ownsRegistration)
                    {
                        GuardProfiles.RestoreRegistry(snapshot);
                        if (Directory.Exists(privateFolder)) Directory.Delete(privateFolder, true);
                    }
                    var folder = Path.GetDirectoryName(result.prefabPath).Replace('\\', '/');
                    if (folder != "Assets/LinuxAvatarGuardGenerated/" + result.buildId)
                        throw new InvalidOperationException("Ruta de rollback inválida.");
                    AssetDatabase.DeleteAsset(folder);
                    if (File.Exists(result.keyPath)) File.Delete(result.keyPath);
                    if (!string.IsNullOrEmpty(result.metadataMapPath) && File.Exists(result.metadataMapPath)) File.Delete(result.metadataMapPath);
                    var failed = new GuardSecurityManifest { buildId = result.buildId, status = ProtectionBuildStatus.Failed.ToString() };
                    GuardProfiles.WritePrivate(Path.GetFullPath("UserSettings/LinuxAvatarGuardFailures/" + result.buildId + ".json"), JsonUtility.ToJson(failed, true));
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException("Falló la preparación y no se pudo completar su rollback.", failure, rollbackFailure);
                }
                throw;
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded)
                    SceneManager.SetActiveScene(previous);
                if (scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }
        public static void OpenScene(GuardProfile p)
        {
            GuardProfiles.RequirePrepared(p);
            if (string.IsNullOrEmpty(p.scenePath))
                throw new InvalidOperationException(
                    "Este perfil no tiene una escena preparada; instancia su prefab en tu escena de subida.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            EditorSceneManager.OpenScene(p.scenePath);
            var root = Object.FindObjectsOfType<VRCAvatarDescriptor>().FirstOrDefault(
                d => GuardProfiles.BuildId(d.gameObject) == p.buildId);
            if (root)
            {
                Selection.activeGameObject = root.gameObject;
                SceneView.lastActiveSceneView?.FrameSelected();
            }
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
        }
        public static string PublishedId(GuardProfile p)
        {
            foreach (var d in Object.FindObjectsOfType<VRCAvatarDescriptor>())
                if (GuardProfiles.BuildId(d.gameObject) == p.buildId)
                {
                    var id = d.GetComponent<PipelineManager>()?.blueprintId;
                    if (GuardProfiles.ValidAvatarId(id))
                        return id;
                }
            return null;
        }
        public static GuardProfile ImportExisting(GameObject selected)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Sal de Play Mode antes de reconocer una copia guardada.");
            var root = AvatarRoot(selected);
            var id = GuardProfiles.BuildId(root);
            if (id == null)
                throw new InvalidOperationException("Selecciona una copia protegida de Linux Avatar Guard.");
            var generatedManifest = "Assets/LinuxAvatarGuardGenerated/" + id + "/security-manifest.json";
            if (File.Exists(generatedManifest) && GuardBuildManifest.Read(generatedManifest).status != ProtectionBuildStatus.Ready.ToString())
                throw new InvalidOperationException("Preparación incompleta. Genera una copia nueva.");
            var key = Path.GetFullPath("Library/LinuxAvatarGuard/" + id + ".json");
            if (!File.Exists(key))
                key = EditorUtility.OpenFilePanel(GuardText.Text("Seleccionar respaldo privado de esta copia"), "", "json");
            if (string.IsNullOrEmpty(key))
                return null;
            if (JsonUtility.FromJson<GuardKeyFile>(File.ReadAllText(key)).buildId != id)
                throw new InvalidOperationException(
                    "La clave seleccionada corresponde a otra copia del avatar.");
            var prefab = PrefabUtility.IsPartOfPrefabAsset(root)
                             ? AssetDatabase.GetAssetPath(root)
                             : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            var metadataMap = File.Exists(key + ".metadata.json") ? key + ".metadata.json" :
                Path.Combine(Path.GetDirectoryName(key), "metadata-map.json");
            var p = GuardProfiles.Register(new GuardBuildResult { prefabPath = prefab, keyPath = key,
                                          metadataMapPath = File.Exists(metadataMap) ? metadataMap : null },
                                           root.name);
            if (!PrefabUtility.IsPartOfPrefabAsset(root))
                p.scenePath = root.scene.path;
            var pipeline = root.GetComponent<PipelineManager>();
            if (pipeline && GuardProfiles.ValidAvatarId(pipeline.blueprintId))
                GuardProfiles.Link(p, pipeline.blueprintId);
            GuardProfiles.Save(p);
            return p;
        }
        public static void Preview(GuardProfile p, bool unlocked)
        {
            GuardProfiles.RequirePrepared(p);
            var root = Object.FindObjectsOfType<VRCAvatarDescriptor>()
                           .FirstOrDefault(d => GuardProfiles.BuildId(d.gameObject) == p.buildId)
                           ?.gameObject;
            if (!root)
                throw new InvalidOperationException("Abre la escena de la copia protegida para verla.");
            var key = GuardProfiles.Key(p);
            if (Application.isPlaying)
            {
                var gm = Object.FindObjectsOfType<MonoBehaviour>().FirstOrDefault(
                    b => b && b.GetType().FullName == "BlackStartX.GestureManager.GestureManager");
                var module = gm?.GetType().GetField("Module")?.GetValue(gm);
                var parameters =
                    module?.GetType().GetField("Params")?.GetValue(module) as System.Collections.IDictionary;
                if (parameters == null)
                    throw new InvalidOperationException(
                        "Selecciona el avatar en Gesture Manager antes de probarlo en Play Mode.");
                var simulated = module.GetType().GetField("Avatar")?.GetValue(module) as GameObject;
                if (simulated && simulated != root)
                    throw new InvalidOperationException("Gesture Manager está simulando otro avatar.");
                for (int i = 0; i < 4; i++)
                {
                    var parameter = parameters[key.parameters[i]];
                    var set = parameter.GetType().GetMethods().First(
                        m => m.Name == "Set" && m.GetParameters().Length == 3 &&
                             m.GetParameters()[1].ParameterType == typeof(int));
                    set.Invoke(parameter, new object[] { module, unlocked ? key.keys[i] : 0, null });
                }
            }
            else
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!unlocked)
                    {
                        renderer.SetPropertyBlock(null);
                        continue;
                    }
                    var block = new MaterialPropertyBlock();
                    for (int i = 0; i < 4; i++)
                        block.SetFloat(GuardShaders.Property(i), key.keys[i]);
                    renderer.SetPropertyBlock(block);
                }
            SceneView.RepaintAll();
        }
    }
}
#endif
