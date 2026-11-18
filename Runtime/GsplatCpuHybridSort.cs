// SPDX-License-Identifier: MIT
using System;
using System.Threading;
using UnityEngine;

namespace Gsplat
{
    /// <summary>
    /// Managed fallback for the near-exact/far-bucket order. Points nearer than the supplied
    /// camera-space cutoff use the exact view-depth key; farther points keep the 18-bit
    /// bucket order. Both ranges preserve input order on equal keys. The cutoff is an absolute
    /// camera-space distance, not a fraction of the visible scene depth or a point count.
    /// A null selection means the identity order 0..count-1, matching the native entry point.
    /// </summary>
    internal sealed class GsplatCpuHybridSort
    {
        internal const int BucketBits = 18;
        internal const int BucketCount = 1 << BucketBits;
        const int k_KeyShift = 32 - BucketBits;
        const int k_InsertionLimit = 16;

        readonly Vector3[] m_centers;
        // Keys stay indexed by input ordinal. Near sorting moves ordinals, not center data.
        readonly uint[] m_keys;
        readonly uint[] m_result;
        readonly uint[] m_scratch;
        // Counts become scatter cursors in place, then the first 256 slots serve near radix.
        readonly uint[] m_buckets = new uint[BucketCount];

        internal GsplatCpuHybridSort(Vector3[] centers, int capacity)
        {
            m_centers = centers;
            m_keys = new uint[capacity];
            m_result = new uint[capacity];
            m_scratch = new uint[capacity];
        }

        // Upload only after completion and before starting another job. The previous draw
        // order remains on the GPU, so no separate CPU publication copy is necessary.
        internal uint[] Result => m_result;
        internal int NearCount { get; private set; }

        internal void Sort(uint[] selection, int count, uint nearKey,
            Matrix4x4 localToView, CancellationToken cancellation)
        {
            int available = selection?.Length ?? m_centers.Length;
            if (count < 0 || count > available || count > m_keys.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            cancellation.ThrowIfCancellationRequested();
            if (count == 0)
            {
                NearCount = 0;
                return;
            }

            bool nearEnabled = nearKey != uint.MaxValue;
            uint nearThreshold = nearEnabled ? (nearKey >> k_KeyShift) << k_KeyShift : 0u;
            bool haveNear = false;
            // OR of XORs against the first near key: zero means all near keys are equal,
            // so stable input-order packing already gives the correct exact suffix.
            uint firstNearKey = 0, nearVariation = 0;
            Array.Clear(m_buckets, 0, m_buckets.Length);
            for (int i = 0; i < count; ++i)
            {
                if ((i & 65535) == 0) cancellation.ThrowIfCancellationRequested();
                uint id = selection == null ? (uint)i : selection[i];
                uint key = GsplatCpuDepth.ViewDepthKey(m_centers[id], localToView);
                m_keys[i] = key;
                if (!nearEnabled || key < nearThreshold)
                    ++m_buckets[(int)(key >> k_KeyShift)];
                else if (!haveNear)
                {
                    haveNear = true;
                    firstNearKey = key;
                }
                else
                    nearVariation |= firstNearKey ^ key;
            }

            uint total = 0;
            for (int bucket = 0; bucket < BucketCount; ++bucket)
            {
                uint amount = m_buckets[bucket];
                m_buckets[bucket] = total;
                total += amount;
            }

            // The boundary bucket joins the exact suffix, so concatenation cannot introduce
            // a depth seam. Only far points were counted: total is already the suffix start.
            int nearStart = (int)total, nearCursor = nearStart;
            NearCount = count - nearStart;
            for (int i = 0; i < count; ++i)
            {
                if ((i & 65535) == 0) cancellation.ThrowIfCancellationRequested();
                uint key = m_keys[i];
                uint id = selection == null ? (uint)i : selection[i];
                if (nearEnabled && key >= nearThreshold)
                    m_result[nearCursor++] = nearVariation == 0 ? id : (uint)i;
                else
                    m_result[m_buckets[(int)(key >> k_KeyShift)]++] = id;
            }

            // Empty and equal-key suffixes already have their stable final source IDs.
            if (nearVariation == 0) return;
            if (NearCount <= k_InsertionLimit)
            {
                SortSmallNear(selection, nearStart, count);
                return;
            }
            SortNearRadix(selection, nearStart, count, cancellation);
        }

        void SortSmallNear(uint[] selection, int begin, int end)
        {
            for (int i = begin + 1; i < end; ++i)
            {
                uint ordinal = m_result[i], key = m_keys[ordinal];
                int j = i - 1;
                while (j >= begin && m_keys[m_result[j]] > key)
                {
                    m_result[j + 1] = m_result[j];
                    --j;
                }
                m_result[j + 1] = ordinal;
            }
            if (selection != null)
                for (int i = begin; i < end; ++i) m_result[i] = selection[m_result[i]];
        }

        void SortNearRadix(uint[] selection, int begin, int end, CancellationToken cancellation)
        {
            uint[] source = m_result, destination = m_scratch;
            for (int pass = 0; pass < 4; ++pass)
            {
                int shift = pass * 8;
                Array.Clear(m_buckets, 0, 256);
                for (int i = begin; i < end; ++i)
                {
                    if ((i & 65535) == 0) cancellation.ThrowIfCancellationRequested();
                    ++m_buckets[(int)((m_keys[source[i]] >> shift) & 255)];
                }
                uint offset = (uint)begin;
                for (int bucket = 0; bucket < 256; ++bucket)
                {
                    uint amount = m_buckets[bucket];
                    m_buckets[bucket] = offset;
                    offset += amount;
                }
                if (pass == 3)
                {
                    // Map input ordinal to source ID only on the final scatter, which lands
                    // back in m_result. The far prefix never leaves that array.
                    for (int i = begin; i < end; ++i)
                    {
                        if ((i & 65535) == 0) cancellation.ThrowIfCancellationRequested();
                        uint ordinal = source[i];
                        destination[m_buckets[(int)((m_keys[ordinal] >> shift) & 255)]++] =
                            selection == null ? ordinal : selection[ordinal];
                    }
                }
                else
                {
                    for (int i = begin; i < end; ++i)
                    {
                        if ((i & 65535) == 0) cancellation.ThrowIfCancellationRequested();
                        uint ordinal = source[i];
                        destination[m_buckets[(int)((m_keys[ordinal] >> shift) & 255)]++] = ordinal;
                    }
                }
                var swap = source;
                source = destination;
                destination = swap;
            }
        }
    }
}
