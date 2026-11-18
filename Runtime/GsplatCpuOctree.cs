// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gsplat
{
    internal sealed class GsplatCpuOctree
    {
        internal struct Node
        {
            public Bounds Bounds;
            public float SupportRadius;
            public int Start, Count, FirstChild, ChildCount;
        }

        readonly Vector3[] m_centers;
        readonly Func<uint, float> m_radius;
        readonly int m_leafCapacity, m_maxDepth;
        readonly List<Node> m_nodes = new();
        readonly List<int> m_leaves = new();
        internal readonly uint[] Indices;
        internal Node[] Nodes { get; }
        internal int[] Leaves { get; }

        internal GsplatCpuOctree(Vector3[] centers, uint[] selected, int count,
            Func<uint, float> radius, int leafCapacity, int maxDepth)
        {
            if (count <= 0 || (selected == null && count > centers.Length) ||
                (selected != null && count > selected.Length))
                throw new ArgumentOutOfRangeException(nameof(count));
            m_centers = centers;
            m_radius = radius;
            m_leafCapacity = Math.Max(128, leafCapacity);
            m_maxDepth = Math.Max(1, maxDepth);
            Indices = new uint[count];
            for (int i = 0; i < count; ++i)
            {
                uint id = selected == null ? (uint)i : selected[i];
                if (id >= centers.Length || !Finite(centers[id]))
                    throw new ArgumentException("CPU spatial sorting requires finite centers and valid source IDs.");
                Indices[i] = id;
            }
            m_nodes.Add(default);
            Build(0, 0, count, 0);
            Nodes = m_nodes.ToArray();
            Leaves = m_leaves.ToArray();
            m_nodes.Clear();
            m_nodes.TrimExcess();
            m_leaves.Clear();
            m_leaves.TrimExcess();
        }

        void Build(int nodeIndex, int start, int count, int depth)
        {
            Vector3 min = m_centers[Indices[start]], max = min;
            for (int i = start + 1; i < start + count; ++i)
            {
                var p = m_centers[Indices[i]];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var node = new Node { Start = start, Count = count };
            node.Bounds.SetMinMax(min, max);
            if (count <= m_leafCapacity)
            {
                for (int i = start; i < start + count; ++i)
                {
                    float radius = m_radius(Indices[i]);
                    if (!Finite(radius) || radius < 0)
                        throw new ArgumentException("CPU spatial sorting requires finite, nonnegative Gaussian support radii.");
                    node.SupportRadius = Math.Max(node.SupportRadius, radius);
                }
                m_nodes[nodeIndex] = node;
                m_leaves.Add(nodeIndex);
                return;
            }

            // Degenerate/depth-capped cells still need bounded visibility leaves. Their index
            // blocks may overlap spatially; selected IDs still enter one global sort.
            if (depth >= m_maxDepth || (max - min).sqrMagnitude < 1e-16f)
            {
                node.ChildCount = (count + m_leafCapacity - 1) / m_leafCapacity;
                node.FirstChild = m_nodes.Count;
                for (int i = 0; i < node.ChildCount; ++i) m_nodes.Add(default);
                for (int i = 0; i < node.ChildCount; ++i)
                {
                    int offset = i * m_leafCapacity;
                    Build(node.FirstChild + i, start + offset, Math.Min(m_leafCapacity, count - offset), depth + 1);
                    node.SupportRadius = Math.Max(node.SupportRadius, m_nodes[node.FirstChild + i].SupportRadius);
                }
            }
            else
            {
                Partition(start, count, node.Bounds.center, out var starts, out var counts);
                node.FirstChild = m_nodes.Count;
                for (int i = 0; i < 8; ++i)
                    if (counts[i] != 0) { ++node.ChildCount; m_nodes.Add(default); }
                int child = node.FirstChild;
                for (int i = 0; i < 8; ++i)
                {
                    if (counts[i] == 0) continue;
                    Build(child, starts[i], counts[i], depth + 1);
                    node.SupportRadius = Math.Max(node.SupportRadius, m_nodes[child++].SupportRadius);
                }
            }
            m_nodes[nodeIndex] = node;
        }

        void Partition(int start, int count, Vector3 center, out int[] starts, out int[] counts)
        {
            starts = new int[8];
            counts = new int[8];
            var next = new int[8];
            for (int i = start; i < start + count; ++i) ++counts[Octant(m_centers[Indices[i]], center)];
            int cursor = start;
            for (int i = 0; i < 8; ++i)
            {
                starts[i] = next[i] = cursor;
                cursor += counts[i];
            }
            for (int bucket = 0; bucket < 8; ++bucket)
            {
                int end = starts[bucket] + counts[bucket];
                while (next[bucket] < end)
                {
                    int index = next[bucket];
                    int target = Octant(m_centers[Indices[index]], center);
                    if (target == bucket) { ++next[bucket]; continue; }
                    int destination = next[target]++;
                    (Indices[index], Indices[destination]) = (Indices[destination], Indices[index]);
                }
            }
        }

        static int Octant(Vector3 point, Vector3 center) =>
            (point.x >= center.x ? 1 : 0) | (point.y >= center.y ? 2 : 0) | (point.z >= center.z ? 4 : 0);

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

        internal static Bounds TransformBounds(Node node, Matrix4x4 matrix, float scaleFactor)
        {
            // Center-only cells are not safe culling bounds for extended Gaussian kernels.
            var e = node.Bounds.extents + Vector3.one * (node.SupportRadius * Math.Max(1, Math.Abs(scaleFactor)));
            var extent = new Vector3(
                Math.Abs(matrix.m00) * e.x + Math.Abs(matrix.m01) * e.y + Math.Abs(matrix.m02) * e.z,
                Math.Abs(matrix.m10) * e.x + Math.Abs(matrix.m11) * e.y + Math.Abs(matrix.m12) * e.z,
                Math.Abs(matrix.m20) * e.x + Math.Abs(matrix.m21) * e.y + Math.Abs(matrix.m22) * e.z);
            return new Bounds(matrix.MultiplyPoint3x4(node.Bounds.center), extent * 2);
        }
    }
}
