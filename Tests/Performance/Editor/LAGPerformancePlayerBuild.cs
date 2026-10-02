// SPDX-License-Identifier: MIT
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace LinuxAvatarGuard.Performance
{
    // This SDK-free player contains only the probe. Fixture assets arrive in separate native bundles.
    public static class PlayerBuild
    {
        public static void Run()
        {
            if (Environment.GetEnvironmentVariable("LAG_PERFORMANCE_ALLOWED") != "owned-fixtures-only" ||
                Path.GetFileName(Path.GetDirectoryName(Application.dataPath)) != "PerformancePlayerProject" ||
                Application.platform != RuntimePlatform.LinuxEditor || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
                throw new InvalidOperationException("Only the explicitly enabled disposable Linux/Vulkan performance project is allowed.");
            string output = Environment.GetEnvironmentVariable("LAG_PERFORMANCE_OUTPUT");
            if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output)) throw new ArgumentException("Absolute output directory required.");
            Directory.CreateDirectory(output);
            PlayerSettings.companyName = "LinuxAvatarGuardResearch";
            PlayerSettings.productName = "LinuxAvatarGuardPerformanceFixture";
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.enableFrameTimingStats = true;
            PlayerSettings.defaultScreenWidth = 1920; PlayerSettings.defaultScreenHeight = 1080;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = false; PlayerSettings.runInBackground = true;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
            QualitySettings.vSyncCount = 0; QualitySettings.antiAliasing = 0;
            QualitySettings.shadows = ShadowQuality.Disable;
            GraphicsSettings.renderPipelineAsset = null;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string mode=Environment.GetEnvironmentVariable("LAG_PERFORMANCE_MODE")??"isolated";
            if(mode=="isolated")new GameObject("OwnedPerformanceProbe").AddComponent<Probe>();
            else if(mode=="interleaved")new GameObject("OwnedPerformanceProbe").AddComponent<InterleavedProbe>();
            else throw new ArgumentException("Unknown own-player performance mode.");
            EditorSceneManager.SaveScene(scene, "Assets/owned-performance.unity");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { "Assets/owned-performance.unity" }, target = BuildTarget.StandaloneLinux64,
                locationPathName = Path.Combine(output, "player", "lag-performance.x86_64"), options = BuildOptions.StrictMode
            });
            File.WriteAllText(Path.Combine(output, "player-build.json"), JsonUtility.ToJson(new PlayerBuildResult {
                success = report.summary.result == BuildResult.Succeeded, unity = Application.unityVersion,
                seconds = report.summary.totalTime.TotalSeconds, bytes = (long)report.summary.totalSize,
                errors = (int)report.summary.totalErrors, warnings = (int)report.summary.totalWarnings,
                development = false, frameTimingStats = true, sdkIncluded = false
            }, true));
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("Performance player build failed: " + report.summary.result);
            Debug.Log("LAG_PERFORMANCE_PLAYER_BUILD_SUCCESS");
        }
        [Serializable] sealed class PlayerBuildResult
        {
            public string unity; public double seconds; public long bytes;
            public int errors, warnings; public bool success, development, frameTimingStats, sdkIncluded;
        }
    }
}
