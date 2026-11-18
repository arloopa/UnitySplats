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
            EditorGUILayout.PropertyField(m_gsplatSettings.FindProperty(nameof(GsplatSettings.Materials)));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Native Web Sorting", EditorStyles.boldLabel);
            var nativeSort = m_gsplatSettings.FindProperty(nameof(GsplatSettings.EnableNativeWebSort));
            EditorGUILayout.PropertyField(nativeSort);
            if (nativeSort.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "Requires a Unity 6000+ WebGPU player, native C/C++ multithreading in Player Settings, " +
                    "shared memory/cross-origin isolation and available precreated workers. Otherwise Web uses " +
                    "main-thread CPU sorting. CPU-skinned models also use the existing fallback. " +
                    "Confirm 'Native WebGPU radix sorting active' in the player log. " +
                    "The worker count is fixed at startup, and the pool persists until the page closes.",
                    MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("CPU Sort Optimization", EditorStyles.boldLabel);
            var spatialSort = m_gsplatSettings.FindProperty(nameof(GsplatSettings.EnableCpuSortOptimization));
            EditorGUILayout.PropertyField(spatialSort, new GUIContent("Enable Cpu Sort Optimization"));
            if (spatialSort.boolValue)
            {
                EditorGUILayout.PropertyField(
                    m_gsplatSettings.FindProperty(nameof(GsplatSettings.CpuNearExactDistance)),
                    new GUIContent("Near Exact Distance (units)"));
                EditorGUILayout.HelpBox(
                    "CPU fallback only; does not select or modify GPU sorting. Reduces the fallback sorter's work using " +
                    "spatial culling and a near-exact/far-bucket view-depth order. Points within the near exact distance " +
                    "are sorted exactly; farther points use 18-bit buckets. The distance is absolute camera-space distance, " +
                    "independent of model depth span and point count; 0 sorts all selected points exactly. The previous completed order stays drawable while the " +
                    "next job runs; uploads follow job completion. Requires fully uploaded static centers; CPU-skinned models " +
                    "use the existing sorter. Web requires native Web sorting and available workers; otherwise the feature " +
                    "falls back to global CPU view-Z sorting. Culling margins, leaf size and bucket width are " +
                    "fixed internal values. Enabling this adds CPU memory for the " +
                    "culling tree, selection buffer and sorter scratch, scaling with splat count.", MessageType.Info);
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
            EditorGUILayout.PropertyField(m_gsplatSettings.FindProperty(nameof(GsplatSettings.ShowImportErrors)));
            EditorGUILayout.PropertyField(m_gsplatSettings.FindProperty(nameof(GsplatSettings.ShowDebugLog)));

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
