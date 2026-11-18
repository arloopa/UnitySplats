// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Gsplat
{
    internal sealed class GsplatWebSortDiagnostics
    {
        enum Metric { Pending, Queue, Keys, Radix, Collect, Copy, ApplyCall, Age, AxisDelta, Count }
        static readonly double s_timestampToMs = 1000.0 / Stopwatch.Frequency;
        static readonly string[] s_names = { "pending", "queue", "keys", "radix", "collect", "copy", "applyCall", "age", "axisDeltaDeg" };
        readonly double[] m_sums = new double[(int)Metric.Count];
        readonly double[] m_maxima = new double[(int)Metric.Count];
        double m_windowStartedAt = NowMs;
        int m_samples;

        internal static double NowMs => Stopwatch.GetTimestamp() * s_timestampToMs;

        internal void Record(double pendingMs, GsplatWebNativeSort.NativeTimings native,
            GsplatWebNativeSort.UploadTimings upload, double ageMs, float axisDelta,
            int count, int uploadBytes, UnityEngine.Object asset, int resourceId)
        {
            Add(Metric.Pending, pendingMs);
            Add(Metric.Queue, native.QueueMs);
            Add(Metric.Keys, native.KeysMs);
            Add(Metric.Radix, native.RadixMs);
            Add(Metric.Collect, native.CollectMs);
            Add(Metric.Copy, upload.CopyMs);
            Add(Metric.ApplyCall, upload.ApplyCallMs);
            Add(Metric.Age, ageMs);
            Add(Metric.AxisDelta, axisDelta);
            ++m_samples;

            double now = NowMs;
            double elapsed = now - m_windowStartedAt;
            if (elapsed < 1000.0)
                return;

            var text = new StringBuilder(512);
            text.Append("[GsplatSorter][WebSortTiming] asset='").Append(asset ? asset.name : "<none>")
                .Append("' resource=").Append(resourceId)
                .Append(" workers=").Append(GsplatWebNativeSort.WorkerCount)
                .Append(" points(last)=").Append(count)
                .Append(" uploadMiB=").Append(Format(uploadBytes / 1048576.0))
                .Append(" windowSec=").Append(Format(elapsed / 1000.0))
                .Append(" applied=").Append(m_samples)
                .Append(" appliedHz=").Append(Format(m_samples * 1000.0 / elapsed))
                .Append(" | avg/max (ms except axisDeltaDeg): ");
            for (int i = 0; i < (int)Metric.Count; ++i)
            {
                if (i > 0) text.Append("; ");
                text.Append(s_names[i]).Append('=').Append(Format(m_sums[i] / m_samples))
                    .Append('/').Append(Format(m_maxima[i]));
            }
            UnityEngine.Debug.Log(text.ToString(), asset);
            Array.Clear(m_sums, 0, m_sums.Length);
            Array.Clear(m_maxima, 0, m_maxima.Length);
            m_samples = 0;
            m_windowStartedAt = NowMs;
        }

        void Add(Metric metric, double value)
        {
            int index = (int)metric;
            value = Math.Max(0, value);
            m_sums[index] += value;
            m_maxima[index] = Math.Max(m_maxima[index], value);
        }

        static string Format(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    }
}
