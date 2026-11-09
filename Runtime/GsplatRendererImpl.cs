// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Vector3 = UnityEngine.Vector3;

namespace Gsplat
{
    public class GsplatRendererImpl
    {
        public uint SplatCount { get; private set; }

        MaterialPropertyBlock m_propertyBlock;
        GsplatAsset m_gsplatAsset;
        public uint m_remainingCount = 0;
        public Bounds m_bounds;

        public GsplatResource GsplatResource;
        public GraphicsBuffer OrderBuffer { get; private set; }
        public Texture2D OrderTexture { get; private set; }
        public GraphicsBuffer CutoutsBuffer { get; private set; }
        public GraphicsBuffer OrderSizeBuffer { get; private set; }
        public GraphicsBuffer BoundsBuffer { get; private set; }
        public ISorterResource SorterResource { get; private set; }

        static readonly int k_orderBuffer = Shader.PropertyToID("_OrderBuffer");
        static readonly int k_orderTexture = Shader.PropertyToID("_OrderTexture");
        static readonly int k_gsplatTextureWidth = Shader.PropertyToID("_GsplatTextureWidth");
        static readonly int k_matrixM = Shader.PropertyToID("_MATRIX_M");
        static readonly int k_splatInstanceSize = Shader.PropertyToID("_SplatInstanceSize");
        static readonly int k_splatCount = Shader.PropertyToID("_SplatCount");
        static readonly int k_gammaToLinear = Shader.PropertyToID("_GammaToLinear");
        static readonly int k_shDegree = Shader.PropertyToID("_SHDegree");
        static readonly int k_brightness = Shader.PropertyToID("_Brightness");
        static readonly int k_scaleFactor = Shader.PropertyToID("_ScaleFactor");
        static readonly int k_antialiased = Shader.PropertyToID("_Antialiased");
        static readonly int k_relightMap = Shader.PropertyToID("_GsplatRelightMap");
        static readonly int k_relightScreen = Shader.PropertyToID("_GsplatRelightScreen");
        static readonly int k_relightParams = Shader.PropertyToID("_GsplatRelightParams");
        static readonly int k_volumeEnabled = Shader.PropertyToID("_GsplatVolumeEnabled");
        static readonly int k_volumeDensity = Shader.PropertyToID("_GsplatVolumeDensity");
        static readonly int k_volumeBoundsMin = Shader.PropertyToID("_GsplatVolumeBoundsMin");
        static readonly int k_volumeBoundsMax = Shader.PropertyToID("_GsplatVolumeBoundsMax");
        static readonly int k_volumeBoundsInvSize =
            Shader.PropertyToID("_GsplatVolumeBoundsInvSize");
        static readonly int k_volumeLightingParams =
            Shader.PropertyToID("_GsplatVolumeLightingParams");
        static readonly int k_volumeOcclusionParams =
            Shader.PropertyToID("_GsplatVolumeOcclusionParams");
        static readonly int k_volumeAmbientColor =
            Shader.PropertyToID("_GsplatVolumeAmbientColor");
        static readonly int k_volumeLightCount =
            Shader.PropertyToID("_GsplatVolumeLightCount");
        static readonly int k_volumeLightColors =
            Shader.PropertyToID("_GsplatVolumeLightColors");
        static readonly int k_volumeLightPositions =
            Shader.PropertyToID("_GsplatVolumeLightPositions");
        static readonly int k_volumeLightDirections =
            Shader.PropertyToID("_GsplatVolumeLightDirections");
        static readonly int k_volumeLightSpotParams =
            Shader.PropertyToID("_GsplatVolumeLightSpotParams");
        static readonly int k_volumeNormalMatrix =
            Shader.PropertyToID("_GsplatVolumeNormalMatrix");
        static readonly int k_volumeWorldToLocal =
            Shader.PropertyToID("_GsplatVolumeWorldToLocal");
        static readonly Vector4[] s_emptyVolumeLightVectors =
            { Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero };
        static readonly Vector4[] s_emptyVolumeLightPositions =
        {
            new(0f, 0f, 0f, 3f), new(0f, 0f, 0f, 3f),
            new(0f, 0f, 0f, 3f), new(0f, 0f, 0f, 3f)
        };
        static readonly int k_skinningEnabled = Shader.PropertyToID("_GsplatSkinningEnabled");
        static readonly int k_skinWeights = Shader.PropertyToID("_GsplatSkinWeights");
        static readonly int k_skinReferenceRows = Shader.PropertyToID("_GsplatSkinReferenceRows");
        static readonly int k_skinBoneRows = Shader.PropertyToID("_GsplatSkinBoneRows");
        static readonly int k_skinIndexTexture = Shader.PropertyToID("_GsplatSkinIndexTexture");
        static readonly int k_skinWeightTexture = Shader.PropertyToID("_GsplatSkinWeightTexture");
        static readonly int k_skinReferenceTexture = Shader.PropertyToID("_GsplatSkinReferenceTexture");
        static readonly int k_skinBoneTexture = Shader.PropertyToID("_GsplatSkinBoneTexture");
        static readonly int k_skinIndexTextureWidth = Shader.PropertyToID("_GsplatSkinIndexTextureWidth");
        static readonly int k_skinWeightTextureWidth = Shader.PropertyToID("_GsplatSkinWeightTextureWidth");
        static readonly int k_skinReferenceTextureWidth = Shader.PropertyToID("_GsplatSkinReferenceTextureWidth");
        static readonly int k_skinBoneTextureWidth = Shader.PropertyToID("_GsplatSkinBoneTextureWidth");

        GraphicsBuffer m_dummySkinWeightBuffer;
        GraphicsBuffer m_dummySkinReferenceRowBuffer;
        GraphicsBuffer m_dummySkinBoneRowBuffer;

        uint m_framesBeforeRecomputeSort = 0;
        uint m_sortsBeforeRecomputeCutouts = 0;
        public bool ComputeSortRequired = true;
        public bool ComputeCutoutsRequired = true;
        Dictionary<Camera, (Vector3, Vector3)> m_prevCamTransforms;

        GsplatCutout.ShaderData[] m_cutoutsData;
        uint m_prevSplatCount;
        uint[] m_activeSourceIds;

        public GsplatRendererImpl(uint splatCount)
        {
            SplatCount = splatCount;
            m_prevCamTransforms = new Dictionary<Camera, (Vector3, Vector3)>();
            CreateResources(splatCount);
            CreatePropertyBlock();
        }

        public void RecreateResources(uint splatCount)
        {
            if (SplatCount == splatCount)
                return;
            Dispose();
            SplatCount = splatCount;
            CreateResources(splatCount);
            CreatePropertyBlock();
        }

        public void ComputeDepth(CommandBuffer cmd, Matrix4x4 matrixMv,
            GsplatSkinningSource rig = null)
        {
            ComputeShader shader = m_gsplatAsset.GsplatMaterial.CalcDepthShader;
            if (rig && rig.IsActivelyAnimating && !rig.UsesVertexTextures)
            {
                cmd.SetComputeIntParam(shader, k_skinningEnabled, 1);
                cmd.SetComputeBufferParam(shader, 0, k_skinWeights, rig.SkinWeightBuffer);
                cmd.SetComputeBufferParam(shader, 0, k_skinReferenceRows,
                    rig.ReferenceInverseBuffer);
                cmd.SetComputeBufferParam(shader, 0, k_skinBoneRows, rig.BoneRowBuffer);
            }
            else
            {
                // Compute shader state is shared between renderers, so explicitly clear the flag
                // and provide valid SRVs after an animated renderer has dispatched with these
                // same shader assets. D3D12 validates declared resources even on disabled paths.
                EnsureDummySkinningBuffers();
                cmd.SetComputeIntParam(shader, k_skinningEnabled, 0);
                cmd.SetComputeBufferParam(shader, 0, k_skinWeights, m_dummySkinWeightBuffer);
                cmd.SetComputeBufferParam(shader, 0, k_skinReferenceRows,
                    m_dummySkinReferenceRowBuffer);
                cmd.SetComputeBufferParam(shader, 0, k_skinBoneRows, m_dummySkinBoneRowBuffer);
            }
            m_gsplatAsset.ComputeDepth(cmd, matrixMv, m_remainingCount, SorterResource, GsplatResource);
        }

        public void SetActiveSourceIds(uint[] sourceIds)
        {
            m_activeSourceIds = sourceIds ?? throw new ArgumentNullException(nameof(sourceIds));
            GsplatSorter.Instance.SetActiveSourceIds(SorterResource, sourceIds);
            m_cutoutsData = Array.Empty<GsplatCutout.ShaderData>();
            UpdateActiveRangeCount();
            ComputeSortRequired = true;
            ComputeCutoutsRequired = false;
            ForceRefresh();
        }

        public void ClearActiveSourceIds()
        {
            m_activeSourceIds = null;
            GsplatSorter.Instance.ClearActiveSourceIds(SorterResource);
            m_cutoutsData = Array.Empty<GsplatCutout.ShaderData>();
            m_remainingCount = GsplatResource?.UploadedCount ?? 0;
            ComputeSortRequired = true;
            ComputeCutoutsRequired = true;
            ForceRefresh();
        }

        public void PrepareActiveRanges()
        {
            if (m_activeSourceIds == null)
                return;
            UpdateActiveRangeCount();
            ComputeCutoutsRequired = false;
            m_bounds = m_gsplatAsset ? m_gsplatAsset.Bounds : default;
        }

        void UpdateActiveRangeCount()
        {
            var uploadedCount = GsplatResource?.UploadedCount ?? 0;
            var residentCount = CountResidentSourceIds(m_activeSourceIds, uploadedCount);
            if (residentCount == m_remainingCount)
                return;
            m_remainingCount = residentCount;
            ComputeSortRequired = true;
            m_framesBeforeRecomputeSort = 0;
        }

        static uint CountResidentSourceIds(uint[] sourceIds, uint uploadedCount)
        {
            var lo = 0;
            var hi = sourceIds.Length;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                if (sourceIds[mid] < uploadedCount)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return (uint)lo;
        }

        Bounds ExtractBounds()
        {
            uint[] boundsData = new uint[6];
            BoundsBuffer.GetData(boundsData);

            Bounds bounds = default;
            Vector3 bmin = new(GsplatUtils.SortableUintToFloat(boundsData[0]),
                GsplatUtils.SortableUintToFloat(boundsData[1]), GsplatUtils.SortableUintToFloat(boundsData[2]));
            Vector3 bmax = new(GsplatUtils.SortableUintToFloat(boundsData[3]),
                GsplatUtils.SortableUintToFloat(boundsData[4]), GsplatUtils.SortableUintToFloat(boundsData[5]));
            bounds.SetMinMax(bmin, bmax);

            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f, 0.1f, 0.1f);
            return bounds;
        }

        uint ExtractOrderSize(GraphicsBuffer orderBuffer)
        {
            GraphicsBuffer.CopyCount(orderBuffer, OrderSizeBuffer, 0);
            uint[] count = new uint[1];
            OrderSizeBuffer.GetData(count);
            return count[0];
        }

        public void DispatchInitOrder(GsplatCutout[] cutouts, Matrix4x4 matrixWorld, bool cutoutsUpdateBounds)
        {
            if (cutouts.Length == 0)
            {
                if (m_cutoutsData.Length > 0)
                    SorterResource.Initialized = false;
                m_cutoutsData = Array.Empty<GsplatCutout.ShaderData>();
                m_remainingCount = GsplatResource.UploadedCount;
                m_bounds = m_gsplatAsset.Bounds;
                return;
            }

            if (!ComputeCutoutsRequired)
                return;

            SorterResource.Initialized = true;

            var cutoutsUnchanged = m_cutoutsData.Length == cutouts.Length;
            var updatedCutoutsData = new GsplatCutout.ShaderData[cutouts.Length];
            for (int i = 0; i != cutouts.Length; i++)
            {
                updatedCutoutsData[i] = cutouts[i].GetShaderData(matrixWorld);
                if (cutoutsUnchanged)
                    if (updatedCutoutsData[i].matrix != m_cutoutsData[i].matrix ||
                        updatedCutoutsData[i].typeAndFlags != m_cutoutsData[i].typeAndFlags)
                        cutoutsUnchanged = false;
            }

            if (cutoutsUnchanged && m_prevSplatCount == GsplatResource.UploadedCount)
                return;

            m_prevSplatCount = GsplatResource.UploadedCount;
            m_cutoutsData = updatedCutoutsData;
            CutoutsBuffer = m_gsplatAsset.UpdateCutoutsBuffer(CutoutsBuffer, m_cutoutsData);
            if (cutoutsUpdateBounds)
                m_gsplatAsset.UpdateBoundsBuffer(BoundsBuffer);
            m_gsplatAsset.InitOrder(SorterResource, GsplatResource, cutoutsUpdateBounds);
            m_remainingCount = ExtractOrderSize(SorterResource.OrderBuffer);
            m_bounds = cutoutsUpdateBounds ? ExtractBounds() : m_gsplatAsset.Bounds;
        }

        public void BindGsplatAsset(GsplatAsset gsplatAsset, bool asyncUpload = false)
        {
            Debug.Assert(!m_gsplatAsset);
            // Capacity can be reused for a same-count asset, but its prior permutation and
            // cutout membership are asset-specific and must never survive the rebind.
            SorterResource.Initialized = false;
            m_cutoutsData = Array.Empty<GsplatCutout.ShaderData>();
            m_prevSplatCount = uint.MaxValue;
            m_remainingCount = 0;
            ComputeSortRequired = true;
            ComputeCutoutsRequired = true;
            ForceRefresh();

            m_gsplatAsset = gsplatAsset;
            GsplatResource = GsplatResourceManager.Get(gsplatAsset);
            gsplatAsset.SetupMaterialPropertyBlock(m_propertyBlock, GsplatResource);
            if (asyncUpload)
            {
                var uploadTask = gsplatAsset.UploadDataAsync(GsplatResource);
                if (GsplatResource.TryOwnUploadObservation())
                    _ = ObserveAsyncUpload(uploadTask);
            }
            else
                gsplatAsset.UploadData(GsplatResource);
            GsplatSorter.Instance.BindSorterAsset(SorterResource, gsplatAsset);
        }

        static async Task ObserveAsyncUpload(Task uploadTask)
        {
            try
            {
                // BindGsplatAsset is called from MonoBehaviour.Update. Deliberately retain
                // Unity's synchronization context so any failure is logged on the main thread.
                await uploadTask;
            }
            catch (OperationCanceledException)
            {
                // Releasing the last shared-resource reference intentionally cancels upload.
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        public void ReleaseGsplatAsset()
        {
            if (m_activeSourceIds != null)
            {
                GsplatSorter.Instance.ClearActiveSourceIds(SorterResource);
                m_activeSourceIds = null;
            }
            GsplatSorter.Instance.UnbindSorterAsset(SorterResource);
            GsplatResourceManager.Release(m_gsplatAsset);
            GsplatResource = null;
            m_gsplatAsset = null;
        }

        /// <summary>
        /// Updates the draw count for the portable CPU-sort path without dispatching the
        /// compute-only cutout/order initialization pass.
        /// </summary>
        public void PrepareCpuFallback()
        {
            m_remainingCount = GsplatResource?.UploadedCount ?? 0;
            m_bounds = m_gsplatAsset ? m_gsplatAsset.Bounds : default;
        }

        void CreateResources(uint splatCount)
        {
            try
            {
                // Ensure the settings asset has initialized the sorter before choosing a backend.
                // GsplatRendererImpl can otherwise be created before GsplatSettings.OnEnable runs.
                _ = GsplatSettings.Instance;
                bool useVertexDataTextures = GsplatUtils.UseVertexDataTextures;
                if (GsplatUtils.UseOrderBuffer || !useVertexDataTextures)
                {
                    // WebGPU keeps splat data in textures but stores draw order in a
                    // StructuredBuffer; the desktop CPU path also uses a buffer.
                    var orderTarget = GsplatSorter.Instance.CpuFallbackEnabled
                        ? GraphicsBuffer.Target.Structured
                        : GraphicsBuffer.Target.Append;
                    OrderBuffer = new GraphicsBuffer(orderTarget, (int)splatCount, sizeof(uint));
                }
                else
                {
                    OrderTexture = GsplatUtils.CreateVertexDataTexture((int)splatCount,
                        GraphicsFormat.R8G8B8A8_UNorm, "Gsplat.Order.Texture");
                }

                SorterResource = GsplatSorter.Instance.CreateSorterResource(splatCount, OrderBuffer, OrderTexture);
                m_cutoutsData = Array.Empty<GsplatCutout.ShaderData>();
                CutoutsBuffer = null;
                if (!GsplatSorter.Instance.CpuFallbackEnabled)
                {
                    OrderSizeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, sizeof(uint));
                    BoundsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 6, sizeof(uint));
                }
            }
            catch
            {
                SorterResource?.Dispose();
                SorterResource = null;
                OrderBuffer?.Dispose();
                OrderBuffer = null;
                GsplatUtils.DestroyRuntimeTexture(OrderTexture);
                OrderTexture = null;
                OrderSizeBuffer?.Dispose();
                OrderSizeBuffer = null;
                BoundsBuffer?.Dispose();
                BoundsBuffer = null;
                throw;
            }
        }

        void CreatePropertyBlock()
        {
            m_propertyBlock ??= new MaterialPropertyBlock();
            if (OrderTexture)
            {
                m_propertyBlock.SetTexture(k_orderTexture, OrderTexture);
                m_propertyBlock.SetInteger(k_gsplatTextureWidth, OrderTexture.width);
            }
            else
            {
                m_propertyBlock.SetBuffer(k_orderBuffer, OrderBuffer);
            }
        }

        void EnsureDummySkinningBuffers()
        {
            if (m_dummySkinWeightBuffer != null && m_dummySkinReferenceRowBuffer != null &&
                m_dummySkinBoneRowBuffer != null)
                return;

            var identityWeight = new GsplatRigWeight { BoneIndex0 = 0, Weight0 = 1f };
            m_dummySkinWeightBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                sizeof(uint) * 4 + sizeof(float) * 4)
            {
                name = "Gsplat.DisabledSkinWeight"
            };
            m_dummySkinWeightBuffer.SetData(new[] { identityWeight });

            Matrix4x4 identity = Matrix4x4.identity;
            var identityRows = new[]
            {
                identity.GetRow(0), identity.GetRow(1), identity.GetRow(2), identity.GetRow(3)
            };
            m_dummySkinReferenceRowBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, 3, sizeof(float) * 4)
            {
                name = "Gsplat.DisabledSkinReferenceRows"
            };
            m_dummySkinReferenceRowBuffer.SetData(new[]
            {
                identityRows[0], identityRows[1], identityRows[2]
            });
            m_dummySkinBoneRowBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4,
                sizeof(float) * 4)
            {
                name = "Gsplat.DisabledSkinBoneRows"
            };
            m_dummySkinBoneRowBuffer.SetData(identityRows);
        }

        public void Dispose()
        {
            ReleaseGsplatAsset();
            m_activeSourceIds = null;
            OrderBuffer?.Dispose();
            OrderBuffer = null;
            GsplatUtils.DestroyRuntimeTexture(OrderTexture);
            OrderTexture = null;
            SorterResource?.Dispose();
            SorterResource = null;
            CutoutsBuffer?.Dispose();
            CutoutsBuffer = null;
            OrderSizeBuffer?.Dispose();
            OrderSizeBuffer = null;
            BoundsBuffer?.Dispose();
            BoundsBuffer = null;
            m_dummySkinWeightBuffer?.Dispose();
            m_dummySkinWeightBuffer = null;
            m_dummySkinReferenceRowBuffer?.Dispose();
            m_dummySkinReferenceRowBuffer = null;
            m_dummySkinBoneRowBuffer?.Dispose();
            m_dummySkinBoneRowBuffer = null;
        }

        public void ForceRefresh()
        {
            m_framesBeforeRecomputeSort = 0;
            m_sortsBeforeRecomputeCutouts = 0;
        }

        public void RefreshOnCameraMove()
        {
            foreach (var cam in Camera.allCameras)
            {
                if (m_prevCamTransforms.TryGetValue(cam, out (Vector3, Vector3) prevCamTransform))
                {
                    (Vector3 prevCamPos, Vector3 prevCamRot) = prevCamTransform;

                    if ((cam.transform.position - prevCamPos).magnitude >
                        GsplatSettings.Instance.CameraTranslationRefreshTreshold
                        || (cam.transform.eulerAngles - prevCamRot).magnitude >
                        GsplatSettings.Instance.CameraRotationRefreshTreshold)
                    {
                        m_prevCamTransforms[cam] = (cam.transform.position, cam.transform.eulerAngles);
                        ForceRefresh();
                    }
                }
                else
                {
                    m_prevCamTransforms.Add(cam, (cam.transform.position, cam.transform.eulerAngles));
                    ForceRefresh();
                }
            }
        }

        public void EvaluateRefreshRequired(GsplatRenderer.GsplatSortMode mode, uint sortRefreshRate,
            uint cutoutsRefreshRate)
        {
            if (mode == GsplatRenderer.GsplatSortMode.Always)
            {
                sortRefreshRate = 0;
                cutoutsRefreshRate = 0;
            }

            if (mode == GsplatRenderer.GsplatSortMode.SortEveryNFrames)
            {
                cutoutsRefreshRate = 0;
            }

            RefreshOnCameraMove();

            ComputeSortRequired = false;
            ComputeCutoutsRequired = false;

            if (m_framesBeforeRecomputeSort == 0)
            {
                m_framesBeforeRecomputeSort = sortRefreshRate;
                ComputeSortRequired = true;
                if (m_sortsBeforeRecomputeCutouts == 0)
                {
                    m_sortsBeforeRecomputeCutouts = cutoutsRefreshRate;
                    ComputeCutoutsRequired = true;
                }
                else
                    m_sortsBeforeRecomputeCutouts -= 1;
            }
            else
                m_framesBeforeRecomputeSort -= 1;
        }

        /// <summary>
        /// Render the splats.
        /// </summary>
        /// <param name="transform">Object transform.</param>
        /// <param name="layer">Layer used for rendering.</param>
        /// <param name="gammaToLinear">Covert color space from Gamma to Linear.</param>
        /// <param name="shDegree">Order of SH coefficients used for rendering. The final value is capped by the SHBands property.</param>
        /// <param name="brightness">Brightness color scaling.</param>
        /// <param name="scaleFactor">Splats uv scaling factor, reduce splat size while trying to keep visual fidelity.</param>
        /// <param name="renderOrder">Manual render order placement of the gsplat. The final value is capped by the maximum render order setting.</param>
        public void Render(Transform transform, int layer, bool gammaToLinear = false, int shDegree = 3,
            float brightness = 1.0f, float scaleFactor = 1.0f, uint renderOrder = 0,
            GsplatRelighting relighting = null, float relightingBlend = 1f,
            float relightingBrightness = 2f, float relightingBackground = 1f,
            GsplatSkinningSource rig = null, GsplatVolumeLighting volumeLighting = null)
        {
            uint drawCount = GsplatSorter.Instance.GetCpuDrawCount(SorterResource, m_remainingCount);
            if (drawCount == 0)
                return;

            m_propertyBlock.SetInteger(k_splatCount, (int)drawCount);
            m_propertyBlock.SetInteger(k_gammaToLinear, gammaToLinear ? 1 : 0);
            m_propertyBlock.SetInteger(k_splatInstanceSize, (int)GsplatSettings.Instance.SplatInstanceSize);
            m_propertyBlock.SetInteger(k_shDegree, Math.Min(m_gsplatAsset.SHBands, shDegree));
            m_propertyBlock.SetFloat(k_brightness, brightness);
            m_propertyBlock.SetFloat(k_scaleFactor, scaleFactor);
            m_propertyBlock.SetInteger(k_antialiased, m_gsplatAsset.Antialiased ? 1 : 0);
            m_propertyBlock.SetMatrix(k_matrixM, transform.localToWorldMatrix);
            if (rig && rig.IsActivelyAnimating)
            {
                m_propertyBlock.SetInteger(k_skinningEnabled, 1);
                if (rig.UsesVertexTextures)
                {
                    m_propertyBlock.SetTexture(k_skinIndexTexture, rig.SkinIndexTexture);
                    m_propertyBlock.SetTexture(k_skinWeightTexture, rig.SkinWeightTexture);
                    m_propertyBlock.SetTexture(k_skinReferenceTexture,
                        rig.ReferenceInverseTexture);
                    m_propertyBlock.SetTexture(k_skinBoneTexture, rig.BoneMatrixTexture);
                    m_propertyBlock.SetInteger(k_skinIndexTextureWidth,
                        rig.SkinIndexTexture.width);
                    m_propertyBlock.SetInteger(k_skinWeightTextureWidth,
                        rig.SkinWeightTexture.width);
                    m_propertyBlock.SetInteger(k_skinReferenceTextureWidth,
                        rig.ReferenceInverseTexture.width);
                    m_propertyBlock.SetInteger(k_skinBoneTextureWidth,
                        rig.BoneMatrixTexture.width);
                }
                else
                {
                    m_propertyBlock.SetBuffer(k_skinWeights, rig.SkinWeightBuffer);
                    m_propertyBlock.SetBuffer(k_skinReferenceRows,
                        rig.ReferenceInverseBuffer);
                    m_propertyBlock.SetBuffer(k_skinBoneRows, rig.BoneRowBuffer);
                }
            }
            else
            {
                m_propertyBlock.SetInteger(k_skinningEnabled, 0);
                if (GsplatUtils.UseVertexDataTextures)
                {
                    // Portable shader variants still require valid sampled resources even when
                    // skinning is disabled.
                    m_propertyBlock.SetTexture(k_skinIndexTexture, Texture2D.blackTexture);
                    m_propertyBlock.SetTexture(k_skinWeightTexture, Texture2D.blackTexture);
                    m_propertyBlock.SetTexture(k_skinReferenceTexture, Texture2D.blackTexture);
                    m_propertyBlock.SetTexture(k_skinBoneTexture, Texture2D.blackTexture);
                    m_propertyBlock.SetInteger(k_skinIndexTextureWidth, 1);
                    m_propertyBlock.SetInteger(k_skinWeightTextureWidth, 1);
                    m_propertyBlock.SetInteger(k_skinReferenceTextureWidth, 1);
                    m_propertyBlock.SetInteger(k_skinBoneTextureWidth, 1);
                }
                else
                {
                    EnsureDummySkinningBuffers();
                    m_propertyBlock.SetBuffer(k_skinWeights, m_dummySkinWeightBuffer);
                    m_propertyBlock.SetBuffer(k_skinReferenceRows,
                        m_dummySkinReferenceRowBuffer);
                    m_propertyBlock.SetBuffer(k_skinBoneRows, m_dummySkinBoneRowBuffer);
                }
            }
            if (volumeLighting && volumeLighting.TryGetShaderData(out var volumeData))
            {
                m_propertyBlock.SetInteger(k_volumeEnabled, 1);
                m_propertyBlock.SetTexture(k_volumeDensity, volumeData.OpacityField);
                m_propertyBlock.SetVector(k_volumeBoundsMin, volumeData.BoundsMin);
                m_propertyBlock.SetVector(k_volumeBoundsMax, volumeData.BoundsMax);
                m_propertyBlock.SetVector(k_volumeBoundsInvSize, volumeData.BoundsInvSize);
                m_propertyBlock.SetVector(k_volumeLightingParams, volumeData.LightingParams);
                m_propertyBlock.SetVector(k_volumeOcclusionParams,
                    volumeData.OcclusionParams);
                m_propertyBlock.SetVector(k_volumeAmbientColor, volumeData.AmbientColor);
                m_propertyBlock.SetInteger(k_volumeLightCount, volumeData.LightCount);
                m_propertyBlock.SetVectorArray(k_volumeLightColors, volumeData.LightColors);
                m_propertyBlock.SetVectorArray(k_volumeLightPositions, volumeData.LightPositions);
                m_propertyBlock.SetVectorArray(k_volumeLightDirections, volumeData.LightDirections);
                m_propertyBlock.SetVectorArray(k_volumeLightSpotParams,
                    volumeData.LightSpotParams);
                m_propertyBlock.SetMatrix(k_volumeNormalMatrix, volumeData.NormalMatrix);
                m_propertyBlock.SetMatrix(k_volumeWorldToLocal, volumeData.WorldToLocal);
            }
            else
            {
                m_propertyBlock.SetInteger(k_volumeEnabled, 0);
                m_propertyBlock.SetTexture(k_volumeDensity,
                    GsplatVolumeLighting.EmptyOpacityField);
                m_propertyBlock.SetVector(k_volumeBoundsMin, Vector4.zero);
                m_propertyBlock.SetVector(k_volumeBoundsMax, Vector4.one);
                m_propertyBlock.SetVector(k_volumeBoundsInvSize, Vector4.one);
                m_propertyBlock.SetVector(k_volumeLightingParams, Vector4.zero);
                m_propertyBlock.SetVector(k_volumeOcclusionParams, Vector4.zero);
                m_propertyBlock.SetVector(k_volumeAmbientColor, Vector4.zero);
                m_propertyBlock.SetInteger(k_volumeLightCount, 0);
                m_propertyBlock.SetVectorArray(k_volumeLightColors,
                    s_emptyVolumeLightVectors);
                m_propertyBlock.SetVectorArray(k_volumeLightPositions,
                    s_emptyVolumeLightPositions);
                m_propertyBlock.SetVectorArray(k_volumeLightDirections,
                    s_emptyVolumeLightVectors);
                m_propertyBlock.SetVectorArray(k_volumeLightSpotParams,
                    s_emptyVolumeLightVectors);
                m_propertyBlock.SetMatrix(k_volumeNormalMatrix, Matrix4x4.identity);
                m_propertyBlock.SetMatrix(k_volumeWorldToLocal, Matrix4x4.identity);
            }
            if (relighting && relighting.IsReady)
            {
                m_propertyBlock.SetTexture(k_relightMap, relighting.RelightingTexture);
                m_propertyBlock.SetVector(k_relightScreen, relighting.SourceScreen);
                m_propertyBlock.SetVector(k_relightParams, new Vector4(
                    relightingBlend,
                    relightingBrightness,
                    relightingBackground,
                    1f));
            }
            else
            {
                // Always bind a valid texture because some mobile drivers validate sampler
                // bindings even when the dynamic relighting branch is disabled.
                m_propertyBlock.SetTexture(k_relightMap, Texture2D.blackTexture);
                m_propertyBlock.SetVector(k_relightScreen, Vector4.zero);
                m_propertyBlock.SetVector(k_relightParams, Vector4.zero);
            }

            uint order = Math.Clamp(renderOrder, 0, GsplatSettings.Instance.MaxRenderOrder - 1);
            var rp = new RenderParams(m_gsplatAsset.Materials[order])
            {
                worldBounds = GsplatUtils.CalcWorldBounds(m_bounds, transform),
                matProps = m_propertyBlock,
                layer = layer
            };

            Graphics.RenderMeshPrimitives(rp, GsplatSettings.Instance.Mesh, 0,
                Mathf.CeilToInt(drawCount / (float)GsplatSettings.Instance.SplatInstanceSize));
        }
    }
}
