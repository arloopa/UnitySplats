// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gsplat
{
    // Octree-backed visibility selection for the CPU spatial sorter. The tree, world bounds
    // and frustum traversal live here; the sorter only consumes the compacted source-ID list.
    // Keeping this separate leaves the sorter focused on job scheduling and output.
    internal sealed class GsplatCpuSpatialSelection
    {
        readonly GsplatCpuOctree m_tree; // null when culling is disabled
        readonly uint[] m_selection;     // null when culling is disabled
        readonly Bounds[] m_worldBounds;
        readonly uint[] m_sourceIds;
        readonly int m_sourceCount;
        readonly bool m_cullToFrustum;
        readonly Plane[] m_planes = new Plane[6];
        readonly List<int> m_visible = new(), m_stack = new();
        bool m_haveMatrix, m_collectedWithCulling;
        Matrix4x4 m_matrix;
        float m_scaleFactor;

        internal bool CullToFrustum => m_cullToFrustum;
        internal uint[] Selection => m_collectedWithCulling ? m_selection : m_sourceIds;
        internal uint[] SourceIds => m_sourceIds;
        internal int SourceCount => m_sourceCount;
        internal int VisibleLeafCount => m_visible.Count;
        internal int LeafCount => m_tree?.Leaves.Length ?? 0;

        internal GsplatCpuSpatialSelection(Vector3[] centers, uint[] selected, int count,
            GsplatAsset asset, int leafCapacity, int maxDepth, bool cullToFrustum)
        {
            m_cullToFrustum = m_collectedWithCulling = cullToFrustum;
            m_sourceIds = selected;
            m_sourceCount = count;
            if (cullToFrustum)
            {
                m_tree = new GsplatCpuOctree(centers, selected, count, CreateRadiusReader(asset),
                    leafCapacity, maxDepth);
                m_selection = new uint[count];
                m_worldBounds = new Bounds[m_tree.Nodes.Length];
            }
            else
            {
                ValidateSource(centers, selected, count);
                m_tree = null;
                m_selection = null;
                m_worldBounds = Array.Empty<Bounds>();
            }
        }

        internal void UpdateBounds(Matrix4x4 matrix, float scaleFactor)
        {
            if (!m_cullToFrustum) return;
            if (!m_haveMatrix || !matrix.Equals(m_matrix))
            {
                m_matrix = matrix;
                m_haveMatrix = false;
            }
            if (!m_haveMatrix || scaleFactor != m_scaleFactor)
            {
                for (int i = 0; i < m_worldBounds.Length; ++i)
                {
                    var bounds = GsplatCpuOctree.TransformBounds(m_tree.Nodes[i], matrix, scaleFactor);
                    if (!GsplatCpuOctree.Finite(bounds.center) || !GsplatCpuOctree.Finite(bounds.extents))
                        throw new ArgumentException("Spatial-sort bounds overflowed the finite world range.");
                    m_worldBounds[i] = bounds;
                }
                m_haveMatrix = true;
                m_scaleFactor = scaleFactor;
            }
        }

        // Returns the number of IDs in Selection for this camera. With culling disabled the
        // selection is the caller's original ID array and count.
        internal int Collect(Camera camera, Matrix4x4 view, Vector3 viewPosition, float pixelHeight,
            float projectionScale, bool orthographic, bool allowCulling = true)
        {
            m_collectedWithCulling = m_cullToFrustum && allowCulling;
            if (!m_collectedWithCulling)
            {
                m_visible.Clear();
                return m_sourceCount;
            }
            CollectVisible(camera, view, viewPosition, pixelHeight, projectionScale, orthographic);
            int cursor = 0;
            foreach (int index in m_visible)
            {
                var node = m_tree.Nodes[index];
                if (node.Count <= 0 || node.Start < 0 || node.Count > m_tree.Indices.Length - node.Start ||
                    node.Count > m_selection.Length - cursor)
                    throw new InvalidOperationException("Spatial culling produced an invalid selection range.");
                Array.Copy(m_tree.Indices, node.Start, m_selection, cursor, node.Count);
                cursor += node.Count;
            }
            return cursor;
        }

        void CollectVisible(Camera camera, Matrix4x4 view, Vector3 position, float pixelHeight,
            float projectionScale, bool orthographic)
        {
            GeometryUtility.CalculateFrustumPlanes(camera, m_planes);
            m_visible.Clear();
            m_stack.Clear();
            m_stack.Add(0);
            float pixelScale = 16f / pixelHeight;
            while (m_stack.Count != 0)
            {
                int last = m_stack.Count - 1, index = m_stack[last];
                m_stack.RemoveAt(last);
                var bounds = m_worldBounds[index];
                // Include the shader's pixel-space blur/minimum footprint, not just 3D support.
                float distance = (bounds.center - position).magnitude + bounds.extents.magnitude;
                float padding = pixelScale / Math.Max(0.0001f, projectionScale) * (orthographic ? 1 : distance);
                bounds.Expand(padding * 2);
                bool outside = false;
                // The shader clamps clip Z: only side planes may reject nodes. Its covariance
                // projection is linearized; inflate off-axis cells and retain eye-plane crossings.
                var viewCenter = view.MultiplyPoint3x4(bounds.center);
                float radius = bounds.extents.magnitude;
                float nearestDepth = -viewCenter.z - radius;
                if (nearestDepth > 0)
                {
                    if (!orthographic)
                        bounds.Expand(2 * radius * (Math.Abs(viewCenter.x) + Math.Abs(viewCenter.y) + radius) / nearestDepth);
                    for (int plane = 0; plane < 4; ++plane)
                    {
                        var normal = m_planes[plane].normal;
                        var extent = bounds.extents;
                        float support = Math.Abs(normal.x) * extent.x + Math.Abs(normal.y) * extent.y + Math.Abs(normal.z) * extent.z;
                        if (m_planes[plane].GetDistanceToPoint(bounds.center) < -support) { outside = true; break; }
                    }
                }
                else if (viewCenter.z > radius) outside = true;
                if (outside) continue;
                var node = m_tree.Nodes[index];
                if (node.ChildCount != 0)
                {
                    for (int i = 0; i < node.ChildCount; ++i) m_stack.Add(node.FirstChild + i);
                }
                else
                {
                    m_visible.Add(index);
                }
            }
        }

        static void ValidateSource(Vector3[] centers, uint[] selected, int count)
        {
            if (centers == null) throw new ArgumentNullException(nameof(centers));
            int available = selected?.Length ?? centers.Length;
            if (count <= 0 || count > available)
                throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < count; ++i)
            {
                uint id = selected == null ? (uint)i : selected[i];
                if (id >= centers.Length || !GsplatCpuOctree.Finite(centers[id]))
                    throw new ArgumentException("CPU spatial sorting requires finite centers and valid source IDs.");
            }
        }

        static Func<uint, float> CreateRadiusReader(GsplatAsset asset)
        {
            if (asset is GsplatAssetUncompressed raw && raw.Scales?.Length == asset.SplatCount)
                return id =>
                {
                    Vector3 scale = raw.Scales[id];
                    if (!GsplatCpuOctree.Finite(scale)) throw new ArgumentException("Non-finite Gaussian scale.");
                    return 4 * Math.Max(Math.Abs(scale.x), Math.Max(Math.Abs(scale.y), Math.Abs(scale.z)));
                };
            if (asset is GsplatAssetSpark spark && spark.PackedSplats?.Length == asset.SplatCount)
            {
                var radii = new float[256];
                for (int i = 1; i < radii.Length; ++i) radii[i] = 4 * Mathf.Exp(-12f + (i - 1) * (21f / 254f));
                return id =>
                {
                    uint packed = spark.PackedSplats[id].w;
                    int code = Math.Max((int)(packed & 255), Math.Max((int)((packed >> 8) & 255), (int)((packed >> 16) & 255)));
                    return radii[code];
                };
            }
            throw new NotSupportedException("CPU spatial sorting requires uncompressed or Spark Gaussian scale data.");
        }
    }
}
