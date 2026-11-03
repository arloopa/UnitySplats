// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace Gsplat
{
    public class GsplatSettings : ScriptableObject
    {
        const string k_gsplatSettingsResourcesPath = "GsplatSettings";

        const string k_gsplatSettingsPath =
            "Assets/Gsplat/Settings/Resources/" + k_gsplatSettingsResourcesPath + ".asset";

        static GsplatSettings s_instance;

        public static GsplatSettings Instance
        {
            get
            {
                if (s_instance)
                    return s_instance;

                var settings = Resources.Load<GsplatSettings>(k_gsplatSettingsResourcesPath);
#if UNITY_EDITOR
                if (!settings)
                {
                    var assetPath = Path.GetDirectoryName(k_gsplatSettingsPath);
                    if (!Directory.Exists(assetPath))
                        Directory.CreateDirectory(assetPath);

                    settings = CreateInstance<GsplatSettings>();
                    settings.Reset();
                    AssetDatabase.CreateAsset(settings, k_gsplatSettingsPath);
                    AssetDatabase.SaveAssets();
                }
                else
                {
                    Version previousVersion = settings.Version;
                    bool changed = false;
                    if (previousVersion < new Version("1.2.0"))
                    {
                        settings.Materials = DefaultMaterials;
                        settings.GlobalMaterial = DefaultGlobalMaterial;
                        settings.m_prevComputeShader = null;
                        changed = true;
                    }
                    else if (previousVersion < new Version("1.4.0"))
                    {
                        settings.GlobalMaterial = DefaultGlobalMaterial;
                        changed = true;
                    }

                    if (previousVersion < new Version("1.5.0"))
                    {
                        // Cross-renderer sorting used to be an obscure opt-in project setting.
                        // Enable it once on upgrade; subsequent user changes are preserved.
                        settings.EnableGlobalSort = true;
                        changed = true;
                    }

                    if (changed)
                    {
                        Debug.Log($"Updated GsplatSettings from version {previousVersion}.");
                        settings.Version = GsplatUtils.k_SettingsVersion;
                        settings.OnValidate();
                        EditorUtility.SetDirty(settings);
                        AssetDatabase.SaveAssets();
                    }
                }
#endif

                s_instance = settings;
                return s_instance;
            }
        }

        public ComputeShader ComputeShader;
        public GsplatGlobalMaterial GlobalMaterial;

        [Tooltip(
            "When enabled, 2+ active Gaussian splat renderers are merged into a single globally depth-sorted draw call.")]
        public bool EnableGlobalSort = true;

        [Tooltip("Use native background radix sorting only on Unity 6000+ WebGPU with pthread/shared-memory support. Otherwise use main-thread sorting. Does not enable Unity build threading settings.")]
        public bool EnableNativeWebSort = false;

        [Tooltip("Maximum native sorting workers requested at first use. Clamped to available precreated Unity workers; changes require a player reload.")]
        [Range(1, 8)] public int NativeWebSortWorkers = 4;

        public uint SplatInstanceSize;
        public uint UploadBatchSize;
        [Range(1, 20)] public uint MaxRenderOrder;
        public bool DisplayBoundingBoxes;

        [Tooltip(
            "If a camera moves more that this threshold, each GsplatRenderer compute sorting and cutouts regardless of refresh rate")]
        [Range(0.05f, 1f)]
        public float CameraTranslationRefreshTreshold;

        [Tooltip(
            "If a camera rotates more that this threshold, each GsplatRenderer compute sorting and cutouts refresh regardless of refresh rate")]
        [Range(0.2f, 30f)]
        public float CameraRotationRefreshTreshold;

        public bool ShowImportErrors;
        public GsplatMaterial[] Materials;
        public Mesh Mesh { get; private set; }

        public bool Valid => Materials?.Length != 0 && Mesh && SplatInstanceSize > 0;

        public Version Version
        {
            get => Version.Parse(m_version);
            set => m_version = value.ToString();
        }

        ComputeShader m_prevComputeShader;
        uint m_prevSplatInstanceSize;

        [HideInInspector] [SerializeField] string m_version = "1.5.0";

#if UNITY_EDITOR
        static ComputeShader DefaultComputeShader => AssetDatabase.LoadAssetAtPath<ComputeShader>(
            GsplatUtils.k_PackagePath + "Runtime/Shaders/Gsplat.compute");

        static GsplatGlobalMaterial DefaultGlobalMaterial => AssetDatabase.LoadAssetAtPath<GsplatGlobalMaterial>(
            GsplatUtils.k_PackagePath + "Runtime/Materials/GsplatGlobal.asset");

        static GsplatMaterial[] DefaultMaterials
        {
            get
            {
                var materials = new GsplatMaterial[Enum.GetValues(typeof(CompressionMode)).Length];
                materials[(int)CompressionMode.Uncompressed] =
                    AssetDatabase.LoadAssetAtPath<GsplatMaterial>(GsplatUtils.k_PackagePath +
                                                                  "Runtime/Materials/GsplatUncompressed.asset");
                materials[(int)CompressionMode.Spark] =
                    AssetDatabase.LoadAssetAtPath<GsplatMaterial>(GsplatUtils.k_PackagePath +
                                                                  "Runtime/Materials/GsplatSpark.asset");
                return materials;
            }
        }

        public void Reset()
        {
            Version = GsplatUtils.k_SettingsVersion;
            ComputeShader = DefaultComputeShader;
            GlobalMaterial = DefaultGlobalMaterial;
            Materials = DefaultMaterials;
            EnableGlobalSort = true;
            EnableNativeWebSort = false;
            NativeWebSortWorkers = 4;
            SplatInstanceSize = 128;
            UploadBatchSize = 100000;
            MaxRenderOrder = 1;
            DisplayBoundingBoxes = false;
            CameraTranslationRefreshTreshold = 0.2f;
            CameraRotationRefreshTreshold = 10;
            ShowImportErrors = true;

            m_prevComputeShader = null;
            m_prevSplatInstanceSize = 0;
            OnValidate();
        }
#endif

        void CreateMeshInstance()
        {
            var meshPositions = new Vector3[4 * SplatInstanceSize];
            var meshIndices = new int[6 * SplatInstanceSize];
            for (uint i = 0; i < SplatInstanceSize; ++i)
            {
                // Store the sub-instance index as an ordinary, exactly representable float.
                // Reinterpreting small uint values as floats creates subnormals, which mobile
                // GPUs are allowed to flush to zero at the vertex-input/shader boundary.
                var subInstance = (float)i;
                meshPositions[i * 4] = new Vector3(-1, -1, subInstance);
                meshPositions[i * 4 + 1] = new Vector3(1, -1, subInstance);
                meshPositions[i * 4 + 2] = new Vector3(-1, 1, subInstance);
                meshPositions[i * 4 + 3] = new Vector3(1, 1, subInstance);

                int b = (int)i * 4;
                Array.Copy(new[] { 0 + b, 1 + b, 2 + b, 1 + b, 3 + b, 2 + b }, 0, meshIndices, i * 6, 6);
            }

            Mesh = new Mesh
            {
                name = "GsplatMeshInstance",
                vertices = meshPositions,
                triangles = meshIndices,
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        void OnValidate()
        {
            if (ComputeShader != m_prevComputeShader)
            {
                GsplatSorter.Instance.InitSorter(ComputeShader);
                m_prevComputeShader = ComputeShader;
            }

            GsplatSorter.Instance.InitGlobal(GlobalMaterial);

            if (SplatInstanceSize != m_prevSplatInstanceSize)
            {
                DestroyImmediate(Mesh);
                CreateMeshInstance();
                m_prevSplatInstanceSize = SplatInstanceSize;
            }
#if UNITY_EDITOR
            foreach (var mat in Materials)
            {
                mat.Reset();
            }

            if (GlobalMaterial)
                GlobalMaterial.Reset();
#endif
        }

        void OnEnable()
        {
            GsplatSorter.Instance.InitSorter(ComputeShader);
            GsplatSorter.Instance.InitGlobal(GlobalMaterial);
            m_prevComputeShader = ComputeShader;

            CreateMeshInstance();
            m_prevSplatInstanceSize = SplatInstanceSize;
        }
    }
}
