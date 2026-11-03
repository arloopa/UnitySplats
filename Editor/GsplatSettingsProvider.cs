// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Gsplat.Editor
{
    public class GsplatSettingsProvider : SettingsProvider
    {
        SerializedObject m_gsplatSettings;

        public GsplatSettingsProvider(string path, SettingsScope scopes = SettingsScope.Project,
            IEnumerable<string> keywords = null) : base(path, scopes, keywords)
        {
        }

        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement)
        {
            m_gsplatSettings = new SerializedObject(GsplatSettings.Instance);
        }

        public override void OnGUI(string searchContext)
        {
            m_gsplatSettings.Update();
            EditorGUILayout.PropertyField(m_gsplatSettings.FindProperty(nameof(GsplatSettings.ComputeShader)));
            EditorGUILayout.PropertyField(m_gsplatSettings.FindProperty(nameof(GsplatSettings.SplatInstanceSize)));
            EditorGUILayout.PropertyField(m_gsplatSettings.FindProperty(nameof(GsplatSettings.UploadBatchSize)));
            EditorGUILayout.PropertyField(m_gsplatSettings.FindProperty(nameof(GsplatSettings.MaxRenderOrder)));
            EditorGUILayout.PropertyField(
                m_gsplatSettings.FindProperty(nameof(GsplatSettings.CameraTranslationRefreshTreshold)));
            EditorGUILayout.PropertyField(
                m_gsplatSettings.FindProperty(nameof(GsplatSettings.CameraRotationRefreshTreshold)));
            EditorGUILayout.PropertyField(m_gsplatSettings.FindProperty(nameof(GsplatSettings.DisplayBoundingBoxes)));
            EditorGUILayout.PropertyField(m_gsplatSettings.FindProperty(nameof(GsplatSettings.ShowImportErrors)));
            EditorGUILayout.PropertyField(m_gsplatSettings.FindProperty(nameof(GsplatSettings.Materials)));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Native Web Sorting", EditorStyles.boldLabel);
            var nativeSort = m_gsplatSettings.FindProperty(nameof(GsplatSettings.EnableNativeWebSort));
            EditorGUILayout.PropertyField(nativeSort);
            using (new EditorGUI.DisabledScope(!nativeSort.boolValue))
                EditorGUILayout.PropertyField(m_gsplatSettings.FindProperty(nameof(GsplatSettings.NativeWebSortWorkers)));
            if (nativeSort.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "Requires a Unity 6000+ WebGPU player, native C/C++ multithreading in Player Settings, " +
                    "shared memory/cross-origin isolation and available precreated workers. Otherwise Web uses " +
                    "main-thread CPU sorting. CPU-skinned models also use the existing fallback. " +
                    "Confirm 'Native WebGPU radix sorting active' in the player log. " +
                    "The pool persists until the page closes; worker-count changes require a page reload.",
                    MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Cross-Renderer Sorting", EditorStyles.boldLabel);
            var enableGlobalSortProp = m_gsplatSettings.FindProperty(nameof(GsplatSettings.EnableGlobalSort));
            EditorGUILayout.PropertyField(enableGlobalSortProp,
                new GUIContent("Enable Cross-Renderer Sorting"));
            if (enableGlobalSortProp.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "Cross-renderer sorting requires the GPU radix path and every active renderer " +
                    "to use Spark compression. " +
                    "If any active renderer uses an uncompressed asset, global sort is disabled " +
                    "for the whole scene and all renderers fall back to per-renderer rendering.",
                    MessageType.Info);
                if (!GsplatSorter.Instance.GpuSortEnabled)
                {
                    EditorGUILayout.HelpBox(
                        "The current graphics API is using the portable CPU sorter. True " +
                        "cross-renderer merging is unavailable on this path. Prefer Vulkan, " +
                        "Direct3D 12, or Metal on supported hardware.",
                        MessageType.Warning);
                }
                EditorGUILayout.PropertyField(m_gsplatSettings.FindProperty(nameof(GsplatSettings.GlobalMaterial)));
            }

            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Reset", GUILayout.Width(60)))
                ResetToDefaults();
            GUILayout.EndHorizontal();
            if (m_gsplatSettings.ApplyModifiedProperties())
                GsplatSorter.Instance.MarkGlobalBuffersDirty();
        }

        void ResetToDefaults()
        {
            Undo.RecordObject(GsplatSettings.Instance, "Reset Gsplat Settings");
            GsplatSettings.Instance.Reset();
            EditorUtility.SetDirty(GsplatSettings.Instance);
            m_gsplatSettings = new SerializedObject(GsplatSettings.Instance);
        }

        [SettingsProvider]
        public static SettingsProvider CreateGsplatSettingsProvider()
        {
            var provider = new GsplatSettingsProvider("Project/Gsplat");
            return provider;
        }
    }
}
