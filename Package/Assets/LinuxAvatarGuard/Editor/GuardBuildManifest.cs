// SPDX-License-Identifier: MIT
#if UNITY_EDITOR && LAG_VRCSDK
using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace LinuxAvatarGuard
{
    public enum ProtectionBuildStatus { Legacy = 0, Preparing = 1, AssetsReady = 2, Ready = 3, Failed = 4 }
    public enum PreparationCheckpoint { AssetsBuilt, ProfileRegistered, SceneSaved }

    [Serializable] public sealed class GuardSecurityManifest
    {
        public int schemaVersion = 1;
        public string lagVersion = GuardText.Version;
        public string buildId, codecId = LegacyLinearCodecV1.Id;
        public int codecVersion = LegacyLinearCodecV1.Version;
        public string status;
        public int rendererCount, protectedMeshes, protectedTextures, syncedBits = 32;
        public int[] payloadUvChannels = { 6, 7 };
        public bool experimentalSkinning;
        public int metadataGuardVersion, renamedMetadataAssets, renamedMetadataFiles;
        public string metadataPolicy;
    }

    public static class GuardBuildManifest
    {
        public static GuardSecurityManifest Read(string path) =>
            JsonUtility.FromJson<GuardSecurityManifest>(File.ReadAllText(path));
        public static void Write(string path, GuardSecurityManifest manifest)
        {
            if (!Regex.IsMatch(manifest.buildId ?? "", "^[0-9a-f]{32}$")) throw new InvalidOperationException("BuildID inválido.");
            File.WriteAllText(path, JsonUtility.ToJson(manifest, true));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }
        public static void Complete(GuardBuildResult result, GuardProfile profile)
        {
            var manifest = Read(result.manifestPath);
            if (manifest.buildId != result.buildId || profile.buildId != result.buildId)
                throw new InvalidOperationException("El manifest no corresponde a esta preparación.");
            manifest.status = ProtectionBuildStatus.Ready.ToString();
            Write(result.manifestPath, manifest);
            // Keep private build metadata next to format=1 key.json, without changing its OSC schema.
            GuardProfiles.WritePrivate(Path.Combine(profile.toolsPath, "private-manifest.json"), JsonUtility.ToJson(manifest, true));
            profile.status = ProtectionBuildStatus.Ready;
            GuardProfiles.Save(profile);
        }
    }
}
#endif
