// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LinuxAvatarGuard
{
    // Copy entire controller files, including embedded states and behaviours. Remap clips,
    // nested trees, override controllers, expression menus and material swap keyframes.
    public sealed class GuardAssets
    {
        readonly string folder;
        readonly GuardShaders shaders;
        readonly Dictionary<Object, Object> objects = new Dictionary<Object, Object>();
        readonly Dictionary<string, string> files = new Dictionary<string, string>();
        public GuardAssets(string folder, GuardShaders shaders) { this.folder = folder; this.shaders = shaders; }
        public void Register(Object original, Object copy) { objects[original] = copy; }
        public Object Copy(Object original)
        {
            if (!original) return original;
            if (objects.TryGetValue(original, out var copy)) return copy;
            if (original is Material material)
            {
                var m = new Material(material) { shader = shaders.Copy(material.shader), name = material.name + "_LAG" };
                m.renderQueue = material.renderQueue;
                for (int i = 0; i < 4; i++) m.SetFloat(GuardShaders.Property(i), 0);
                string output = AssetDatabase.GenerateUniqueAssetPath(folder + "/material.mat");
                AssetDatabase.CreateAsset(m, output);
                objects.Add(original, m);
                return m;
            }
            if (!(original is Motion) && !(original is RuntimeAnimatorController) && !(original is AvatarMask) &&
                !(original is ScriptableObject && original.GetType().Namespace != null && original.GetType().Namespace.StartsWith("VRC.SDK3.Avatars.ScriptableObjects"))) return original;
            string source = AssetDatabase.GetAssetPath(original);
            if (string.IsNullOrEmpty(source)) throw new InvalidOperationException("Dependencia de animación sin guardar: " + original.name);
            // Extract imported clips instead of copying a model file containing unprotected meshes.
            if (original is AnimationClip imported && !source.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            {
                var clip = Object.Instantiate(imported);
                string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + imported.name.Replace('/', '_') + ".anim");
                AssetDatabase.CreateAsset(clip, path); objects.Add(original, clip); Remap(clip); return clip;
            }
            if (!files.TryGetValue(source, out string target))
            {
                var originals = AssetDatabase.LoadAllAssetsAtPath(source);
                if (Array.Exists(originals, item => item && EditorUtility.IsDirty(item)))
                    throw new InvalidOperationException("Guarda primero los cambios de animación del avatar de trabajo: " + source);
                target = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileName(source));
                // CopyAsset can flush unrelated dirty assets. Import a new file with a fresh GUID instead.
                File.Copy(Path.GetFullPath(source), Path.GetFullPath(target));
                AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceSynchronousImport);
                files.Add(source, target);
                var copies = AssetDatabase.LoadAllAssetsAtPath(target);
                foreach (var item in originals)
                {
                    if (!item) continue;
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(item, out string guid, out long id);
                    foreach (var candidate in copies)
                    {
                        if (!candidate) continue;
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out string copiedGuid, out long copiedId);
                        if (id == copiedId && item.GetType() == candidate.GetType()) { objects[item] = candidate; break; }
                    }
                }
                // CopyAsset remaps local subassets already, but external dependencies still need copying.
                foreach (var item in copies) if (item) Remap(item);
            }
            if (!objects.TryGetValue(original, out copy)) throw new InvalidOperationException("No se pudo remapear un subasset: " + original.name);
            return copy;
        }
        public void Remap(Object obj)
        {
            var serialized = new SerializedObject(obj);
            var property = serialized.GetIterator();
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference || property.name == "m_Script") continue;
                var original = property.objectReferenceValue;
                if (original && !AssetDatabase.GetAssetPath(original).StartsWith(folder + "/", StringComparison.Ordinal))
                    property.objectReferenceValue = Copy(original);
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(obj);
        }
    }
}
