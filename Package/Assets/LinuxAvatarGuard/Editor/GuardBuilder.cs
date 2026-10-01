// SPDX-License-Identifier: MIT
#if UNITY_EDITOR && LAG_VRCSDK
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace LinuxAvatarGuard
{
    [Serializable] public sealed class GuardKeyFile
    {
        public int format = 1;
        public string buildId;
        public string avatarId = "";
        public string[] parameters;
        public int[] keys;
    }
    public sealed class GuardBuildResult
    {
        public string prefabPath, keyPath;
        public int renderers;
    }
    public static class GuardBuilder
    {
        [DllImport("libc", SetLastError = true)] static extern int chmod(string pathname, uint mode);
        public static void RequireEnvironment()
        {
            if (Application.platform != RuntimePlatform.LinuxEditor) throw new InvalidOperationException("Preparación exclusiva de Unity Editor para Linux.");
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan) throw new InvalidOperationException("Inicia Unity con -force-vulkan. No se cambia Project Settings automáticamente.");
            if (GraphicsSettings.currentRenderPipeline != null) throw new InvalidOperationException("Esta versión requiere Built-in Render Pipeline.");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) throw new InvalidOperationException("Sal de Play Mode y espera a que termine la compilación.");
        }
        public static List<Renderer> Validate(GameObject source, bool allowExperimentalSkinning)
        {
            RequireEnvironment();
            if (!source || !source.GetComponent<VRCAvatarDescriptor>()) throw new InvalidOperationException("Selecciona la raíz con VRC Avatar Descriptor.");
            if (source.transform.parent) throw new InvalidOperationException("La raíz del avatar debe estar en el nivel superior.");
            if (source.GetComponentsInChildren<Component>(true).Any(c => !c)) throw new InvalidOperationException("El avatar contiene scripts ausentes.");
            foreach (var behaviour in source.GetComponentsInChildren<MonoBehaviour>(true))
            {
                string type = behaviour.GetType().FullName;
                if (type.StartsWith("nadena.dev.modular_avatar") || type.Contains("AvatarOptimizer") || type.Contains("VRCFury"))
                    throw new InvalidOperationException("Primero hornea MA/AAO/VRCFury en una copia mediante su flujo de exportación. Esta versión protege un avatar final para evitar que un optimizador elimine los UV de protección.");
            }
            if (source.GetComponentsInChildren<Component>(true).Any(c => c.GetType().FullName == "UnityEngine.Cloth")) throw new InvalidOperationException("Cloth simularía la geometría ofuscada antes del shader. No se soporta en esta versión.");
            if (source.GetComponentsInChildren<MeshCollider>(true).Any(c => c.sharedMesh)) throw new InvalidOperationException("MeshCollider expondría geometría original. Usa colliders primitivos en una copia.");
            var list = source.GetComponentsInChildren<Renderer>(true).ToList();
            if (list.Count == 0) throw new InvalidOperationException("Avatar sin renderers.");
            foreach (var renderer in list)
            {
                Mesh mesh;
                if (renderer is SkinnedMeshRenderer skin)
                {
                    if (!allowExperimentalSkinning) throw new InvalidOperationException("La protección de mallas skinned requiere activar el modo experimental y validar poses. La normal puede normalizarse durante el skinning.");
                    if (skin.bones.Any(b => b && (b.lossyScale - Vector3.one).sqrMagnitude > 0.00001f)) throw new InvalidOperationException("Huesos escalados: no soportados por el decodificador.");
                    mesh = skin.sharedMesh;
                }
                else if (renderer is MeshRenderer) mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                else throw new InvalidOperationException("Renderer no soportado: " + renderer.GetType().Name);
                if (!mesh) throw new InvalidOperationException("Renderer sin malla: " + renderer.name);
                if (renderer.sharedMaterials.Length != mesh.subMeshCount) throw new InvalidOperationException("El número de materiales debe coincidir con los submeshes: " + renderer.name);
                foreach (var material in renderer.sharedMaterials)
                    if (!material || !GuardShaders.Supports(material.shader) || ShaderUtil.ShaderHasError(material.shader)) throw new InvalidOperationException("Todos los submeshes deben usar una variante estándar lilToon soportada: " + renderer.name);
                // Probe all mesh restrictions before allocating output assets.
                var probe = GuardMesh.Encode(mesh, new[] { 123, 157, 211, 239 }, 0.01f);
                Object.DestroyImmediate(probe);
            }
            // Reject mesh replacement, shader-key conflicts and changing normal-dependent geometry.
            foreach (var animator in source.GetComponentsInChildren<Animator>(true))
            {
                if (!animator.runtimeAnimatorController) continue;
                CheckClips(animator.runtimeAnimatorController.animationClips);
            }
            var descriptor = source.GetComponent<VRCAvatarDescriptor>();
            foreach (var layer in descriptor.baseAnimationLayers.Concat(descriptor.specialAnimationLayers))
                if (layer.animatorController) CheckClips(layer.animatorController.animationClips);
            var expression = descriptor.customExpressions ? descriptor.expressionParameters : DefaultParameters();
            if (expression && expression.CalcTotalCost() + 32 > VRCExpressionParameters.MAX_PARAMETER_COST) throw new InvalidOperationException("Se necesitan 32 bits libres de parámetros sincronizados.");
            return list;
        }
        static VRCExpressionParameters DefaultParameters()
        {
            const string path = "Packages/com.vrchat.avatars/Samples/AV3 Demo Assets/Expressions Menu/DefaultExpressionParameters.asset";
            var defaults = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(path);
            if (!defaults) throw new InvalidOperationException("No se encontraron los parámetros predeterminados del SDK. Configura expresiones personalizadas en una copia antes de protegerla.");
            return defaults;
        }
        static void CheckClips(IEnumerable<AnimationClip> clips)
        {
            foreach (var clip in clips.Distinct())
            {
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (b.propertyName.Contains("m_Mesh")) throw new InvalidOperationException("Animación que cambia mallas no soportada: " + clip.name);
                    foreach (var frame in AnimationUtility.GetObjectReferenceCurve(clip, b))
                        if (frame.value is Material m && !GuardShaders.Supports(m.shader)) throw new InvalidOperationException("Material animado sin lilToon compatible: " + clip.name);
                }
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                    if (b.propertyName.Contains("_LAGKey") || b.propertyName.EndsWith("m_LocalScale.x") || b.propertyName.EndsWith("m_LocalScale.y") || b.propertyName.EndsWith("m_LocalScale.z"))
                        throw new InvalidOperationException("Animación de escala/clave incompatible: " + clip.name);
            }
        }
        public static GuardBuildResult Build(GameObject source, float strength, bool experimental)
        {
            var originalRenderers = Validate(source, experimental);
            string id = Guid.NewGuid().ToString("N");
            string parent = "Assets/LinuxAvatarGuardGenerated";
            if (!AssetDatabase.IsValidFolder(parent)) AssetDatabase.CreateFolder("Assets", "LinuxAvatarGuardGenerated");
            string folder = parent + "/" + id;
            AssetDatabase.CreateFolder(parent, id);
            string assets = folder + "/Assets"; AssetDatabase.CreateFolder(folder, "Assets");
            string shaderFolder = folder + "/Shaders"; AssetDatabase.CreateFolder(folder, "Shaders");
            GameObject copy = null;
            GameObject anchor = null;
            Scene scratch = default;
            string keyPath = Path.GetFullPath("Library/LinuxAvatarGuard/" + id + ".json");
            try
            {
                var key = GuardMesh.NewKey();
                // Instantiate directly into a preview scene; never dirty the user's working scene.
                scratch = EditorSceneManager.NewPreviewScene();
                anchor = EditorUtility.CreateGameObjectWithHideFlags("Linux Avatar Guard scratch", HideFlags.HideAndDontSave);
                SceneManager.MoveGameObjectToScene(anchor, scratch);
                copy = Object.Instantiate(source, anchor.transform, false);
                copy.transform.SetParent(null, true);
                copy.name = source.name + "_LAG";
                if (PrefabUtility.IsPartOfPrefabInstance(copy)) PrefabUtility.UnpackPrefabInstance(copy, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                var remapper = new GuardAssets(assets, new GuardShaders(shaderFolder, id));
                var encoded = new Dictionary<Mesh, Mesh>();
                foreach (var r in copy.GetComponentsInChildren<Renderer>(true))
                {
                    var original = r is SkinnedMeshRenderer sk ? sk.sharedMesh : r.GetComponent<MeshFilter>().sharedMesh;
                    if (!encoded.TryGetValue(original, out var mesh))
                    {
                        mesh = GuardMesh.Encode(original, key, strength);
                        AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath(assets + "/mesh.asset"));
                        encoded.Add(original, mesh); remapper.Register(original, mesh);
                    }
                    if (r is SkinnedMeshRenderer skin) skin.sharedMesh = mesh;
                    else r.GetComponent<MeshFilter>().sharedMesh = mesh;
                }
                foreach (var c in copy.GetComponentsInChildren<Component>(true)) remapper.Remap(c);
                var descriptor = copy.GetComponent<VRCAvatarDescriptor>();
                string[] parameters = Enumerable.Range(0, 4).Select(i => "LAG_" + id.Substring(0, 8) + "_" + i).ToArray();
                AddDrivers(copy, descriptor, parameters, assets);
                // Keep private keys OUTSIDE Assets/Packages, and lock permissions before writing.
                Directory.CreateDirectory(Path.GetDirectoryName(keyPath));
                if (chmod(Path.GetDirectoryName(keyPath), 448) != 0) throw new IOException("No se pudo asegurar el directorio de claves.");
                using (var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (chmod(keyPath, 384) != 0) throw new IOException("No se pudo restringir la clave a su propietario.");
                    using (var writer = new StreamWriter(stream)) writer.Write(JsonUtility.ToJson(new GuardKeyFile { buildId = id, parameters = parameters, keys = key }, true));
                }
                var dependencies = EditorUtility.CollectDependencies(new Object[] { copy });
                foreach (var dependency in dependencies)
                    if (dependency is Mesh unprotected && !encoded.Values.Contains(unprotected)) throw new InvalidOperationException("Dependencia de malla original detectada. Se cancela para evitar una falsa protección: " + unprotected.name);
                string prefabPath = folder + "/" + copy.name.Replace('/', '_') + ".prefab";
                if (!PrefabUtility.SaveAsPrefabAsset(copy, prefabPath)) throw new IOException("No se pudo guardar el prefab.");
                // Only save assets under this generated directory; no global SaveAssets call.
                foreach (var path in AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith(folder + "/")))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    if (asset) AssetDatabase.SaveAssetIfDirty(asset);
                }
                File.WriteAllText(folder + "/build-report.json", "{\"format\":1,\"buildId\":\"" + id + "\",\"rendererCount\":" + originalRenderers.Count + ",\"experimentalSkinning\":" + experimental.ToString().ToLowerInvariant() + ",\"syncedBits\":32,\"graphics\":\"Vulkan\"}");
                AssetDatabase.ImportAsset(folder + "/build-report.json");
                return new GuardBuildResult { prefabPath = prefabPath, keyPath = keyPath, renderers = originalRenderers.Count };
            }
            catch
            {
                AssetDatabase.DeleteAsset(folder);
                if (File.Exists(keyPath)) File.Delete(keyPath);
                throw;
            }
            finally
            {
                if (copy) Object.DestroyImmediate(copy);
                if (anchor) Object.DestroyImmediate(anchor);
                if (scratch.IsValid()) EditorSceneManager.ClosePreviewScene(scratch);
            }
        }
        static void AddDrivers(GameObject root, VRCAvatarDescriptor descriptor, string[] names, string folder)
        {
            var layers = descriptor.baseAnimationLayers;
            int fx = Array.FindIndex(layers, l => l.type == VRCAvatarDescriptor.AnimLayerType.FX);
            if (fx < 0) throw new InvalidOperationException("Descriptor sin capa FX.");
            AnimatorController controller;
            if (layers[fx].isDefault || !layers[fx].animatorController)
                controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            else
                controller = layers[fx].animatorController as AnimatorController;
            if (!controller) throw new InvalidOperationException("FX debe ser AnimatorController, no OverrideController.");
            // Preserve the SDK default parameter set before enabling custom expressions.
            if (!descriptor.customExpressions)
            {
                var defaults = Object.Instantiate(DefaultParameters());
                defaults.name = "Parameters";
                AssetDatabase.CreateAsset(defaults, folder + "/DefaultParameters.asset");
                descriptor.expressionParameters = defaults;
                descriptor.expressionsMenu = null;
            }
            descriptor.customExpressions = true;
            if (!descriptor.expressionsMenu)
            {
                var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                menu.controls = new List<VRCExpressionsMenu.Control>();
                AssetDatabase.CreateAsset(menu, folder + "/ExpressionsMenu.asset");
                descriptor.expressionsMenu = menu;
            }
            var expressions = descriptor.expressionParameters;
            if (!expressions)
            {
                expressions = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                expressions.parameters = Array.Empty<VRCExpressionParameters.Parameter>();
                AssetDatabase.CreateAsset(expressions, folder + "/Parameters.asset");
                descriptor.expressionParameters = expressions;
            }
            expressions.parameters = (expressions.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>()).Concat(names.Select(n => new VRCExpressionParameters.Parameter {
                name = n, valueType = VRCExpressionParameters.ValueType.Int, defaultValue = 0, saved = false, networkSynced = true
            })).ToArray();
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int index = 0; index < 4; index++)
            {
                if (controller.parameters.Any(p => p.name == names[index])) throw new InvalidOperationException("Colisión de parámetros.");
                controller.AddParameter(names[index], AnimatorControllerParameterType.Int);
                controller.AddLayer("LAG " + index);
                var all = controller.layers; var layer = all[all.Length - 1]; layer.defaultWeight = 1; controller.layers = all;
                var machine = layer.stateMachine;
                for (int value = 0; value <= 255; value++)
                {
                    var clip = new AnimationClip { name = "LAG_" + index + "_" + value };
                    AssetDatabase.AddObjectToAsset(clip, controller);
                    foreach (var r in renderers)
                        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(r.transform, root.transform), r.GetType(), "material." + GuardShaders.Property(index)), AnimationCurve.Constant(0, 1f / 60, value));
                    var state = machine.AddState(value.ToString()); state.motion = clip; state.writeDefaultValues = false;
                    if (value == 0) machine.defaultState = state;
                    var transition = machine.AddAnyStateTransition(state);
                    transition.hasExitTime = false; transition.duration = 0; transition.canTransitionToSelf = false;
                    transition.AddCondition(AnimatorConditionMode.Equals, value, names[index]);
                }
            }
            layers[fx].isDefault = false; layers[fx].animatorController = controller; descriptor.baseAnimationLayers = layers;
            descriptor.customizeAnimationLayers = true;
            EditorUtility.SetDirty(controller); EditorUtility.SetDirty(expressions);
        }
    }
}
#endif
