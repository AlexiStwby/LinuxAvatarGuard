// SPDX-License-Identifier: MIT
#if UNITY_EDITOR && LAG_VRCSDK
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using LinuxAvatarGuard;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

public static class LAGPreparationValidation
{
    static readonly List<string> checks = new List<string>();
    static void Check(bool value, string name)
    { if (!value) throw new Exception("PREPARATION_FAILED: " + name); checks.Add(name); }
    static string[] Directories(string path) => Directory.Exists(path) ? Directory.GetDirectories(path).OrderBy(p => p).ToArray() : new string[0];
    public static void Run()
    {
        checks.Clear();
        var registry = Path.GetFullPath("UserSettings/LinuxAvatarGuardProfiles.json");
        var originalRegistry = File.Exists(registry) ? File.ReadAllBytes(registry) : null;
        var previousDataRoot = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var sandbox = Path.GetFullPath("../private/preparation-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", sandbox);
        GameObject source = null;
        GuardProfile ready = null;
        try
        {
            source = new GameObject("SyntheticPreparationSource");
            source.AddComponent<Animator>();
            var descriptor = source.AddComponent<VRCAvatarDescriptor>();
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer {
                type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = true } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            var body = new GameObject("Body"); body.transform.SetParent(source.transform, false);
            body.AddComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Fixture/mesh.asset");
            body.AddComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Fixture/original.mat");
            var untitledBefore = Directories("Assets/LinuxAvatarGuardGenerated");
            bool untitledRejected = false;
            try { GuardSetup.Prepare(source, .1f); }
            catch (InvalidOperationException e) { untitledRejected = e.Message.StartsWith("Guarda primero las escenas sin nombre.", StringComparison.Ordinal); }
            Check(untitledRejected && Directories("Assets/LinuxAvatarGuardGenerated").SequenceEqual(untitledBefore),
                  "untitled scenes fail before output allocation and are never saved automatically");
            if (!EditorSceneManager.SaveScene(source.scene, "Assets/Fixture/PreparationWork.unity"))
                throw new IOException("Could not save the disposable working-scene fixture");
            EditorSceneManager.MarkSceneDirty(source.scene);
            var previousScene = SceneManager.GetActiveScene(); var wasDirty = previousScene.isDirty;
            var descriptorBefore = EditorJsonUtility.ToJson(source.GetComponent<VRCAvatarDescriptor>());
            var existing = new GuardProfile { buildId = Guid.NewGuid().ToString("N"), name = "SyntheticLegacyRecord", status = ProtectionBuildStatus.Legacy };
            GuardProfiles.Save(existing);
            var legacy = JsonUtility.FromJson<GuardProfile>("{\"buildId\":\"" + existing.buildId + "\",\"name\":\"OldFormatProfile\"}");
            GuardProfiles.RequirePrepared(legacy);
            Check(legacy.status == ProtectionBuildStatus.Legacy, "profiles without new state metadata retain legacy access");
            var registryBefore = File.ReadAllBytes(registry);
            var generatedBefore = Directories("Assets/LinuxAvatarGuardGenerated");
            var keyFilesBefore = Directory.GetFiles("Library/LinuxAvatarGuard").OrderBy(p => p).ToArray();
            foreach (var failAt in new[] { PreparationCheckpoint.AssetsBuilt, PreparationCheckpoint.ProfileRegistered, PreparationCheckpoint.SceneSaved })
            {
                string failedId = null;
                bool failed = false;
                try
                {
                    GuardSetup.Prepare(source, .1f, checkpoint => {
                        if (checkpoint == PreparationCheckpoint.AssetsBuilt)
                            failedId = Path.GetFileName(Directories("Assets/LinuxAvatarGuardGenerated").Except(generatedBefore).Single());
                        if (checkpoint == failAt) throw new IOException("Injected synthetic failure at " + failAt);
                    });
                }
                catch (IOException) { failed = true; }
                Check(failed && !string.IsNullOrEmpty(failedId), "fault injected at " + failAt);
                Check(Directories("Assets/LinuxAvatarGuardGenerated").SequenceEqual(generatedBefore) &&
                      Directory.GetFiles("Library/LinuxAvatarGuard").OrderBy(p => p).SequenceEqual(keyFilesBefore) &&
                      !Directory.Exists(Path.Combine(GuardProfiles.DataRoot, failedId)), "failed assets and private key rolled back at " + failAt);
                Check(File.ReadAllBytes(registry).SequenceEqual(registryBefore) && GuardProfiles.All().Any(p => p.buildId == existing.buildId),
                      "previous profile registry restored exactly at " + failAt);
                var failure = GuardBuildManifest.Read("UserSettings/LinuxAvatarGuardFailures/" + failedId + ".json");
                Check(failure.status == "Failed" && failure.buildId == failedId, "private failed status recorded at " + failAt);
                Check(SceneManager.GetActiveScene() == previousScene && previousScene.isDirty == wasDirty &&
                      EditorJsonUtility.ToJson(source.GetComponent<VRCAvatarDescriptor>()) == descriptorBefore,
                      "original scene and descriptor preserved at " + failAt);
            }
            ready = GuardSetup.Prepare(source, .1f);
            GuardProfiles.RequirePrepared(ready);
            var manifest = GuardBuildManifest.Read(Path.GetDirectoryName(ready.prefabPath) + "/security-manifest.json");
            Check(ready.status == ProtectionBuildStatus.Ready && manifest.status == "Ready" && File.Exists(ready.scenePath),
                  "successful preparation becomes Ready after saving its scene");
            Check(manifest.codecId == "legacy-linear" && manifest.codecVersion == 1 && manifest.schemaVersion == 1 && manifest.protectedTextures == 0 && manifest.protectedMeshes == 1,
                  "public manifest describes actual legacy protection without claiming texture protection");
            var key = GuardProfiles.Key(ready);
            Check(key.format == 1 && key.parameters.Length == 4 && key.keys.Length == 4,
                  "private OSC configuration retains format 1 and four runtime bytes");
            var privateManifest = GuardBuildManifest.Read(Path.Combine(ready.toolsPath, "private-manifest.json"));
            Check(privateManifest.status == "Ready" && privateManifest.buildId == ready.buildId,
                  "private metadata shares the ready status without changing key.json");
            var publicJson = File.ReadAllText(Path.GetDirectoryName(ready.prefabPath) + "/security-manifest.json");
            Check(!publicJson.Contains("\"keys\"") && !publicJson.Contains("keyPath") && !publicJson.Contains(sandbox),
                  "public manifest excludes runtime secrets and private paths");
            Check(SceneManager.GetActiveScene() == previousScene && previousScene.isDirty == wasDirty &&
                  EditorJsonUtility.ToJson(source.GetComponent<VRCAvatarDescriptor>()) == descriptorBefore,
                  "successful preparation preserves the original working scene");
            bool rejected = false;
            ready.status = ProtectionBuildStatus.Failed;
            try { GuardProfiles.RequirePrepared(ready); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "failed profiles cannot open as prepared avatars");
            ready.status = (ProtectionBuildStatus)99;
            rejected = false;
            try { GuardProfiles.RequirePrepared(ready); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "unknown preparation states are rejected");
            Directory.CreateDirectory("../evidence");
            File.WriteAllText("../evidence/preparation-validation.json", JsonUtility.ToJson(new Report { checks = checks.ToArray(), injectedFailures = 3 }, true));
            Debug.Log("LAG_PREPARATION_SUCCESS " + checks.Count + " checks");
        }
        finally
        {
            if (ready != null) AssetDatabase.DeleteAsset(Path.GetDirectoryName(ready.prefabPath).Replace('\\', '/'));
            if (ready != null && File.Exists("Library/LinuxAvatarGuard/" + ready.buildId + ".json")) File.Delete("Library/LinuxAvatarGuard/" + ready.buildId + ".json");
            if (source) Object.DestroyImmediate(source);
            if (originalRegistry != null) File.WriteAllBytes(registry, originalRegistry);
            else if (File.Exists(registry)) File.Delete(registry);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previousDataRoot);
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
        }
    }
    [Serializable] sealed class Report { public string[] checks; public int injectedFailures; }
}
#endif
