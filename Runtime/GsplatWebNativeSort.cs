// SPDX-License-Identifier: MIT
using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    /// <summary>A session owns native scratch and pins immutable CPU inputs until workers stop using them.</summary>
    internal sealed class GsplatWebNativeSort : IDisposable
    {
        // ABI values match the native GS_* states.
        // This is a C#/native contract handshake, not the package release version.
        const int k_NativeApiVersion = 2;
        internal enum SortStatus
        {
            Idle = 0,
            Queued = 1,
            Running = 2,
            Complete = 3,
            Cancelled = 4,
            Invalid = 5
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeTimings
        {
            public double QueueMs, KeysMs, RadixMs, CollectMs;
        }

        internal readonly struct UploadTimings
        {
            internal readonly double CopyMs, ApplyCallMs;
            internal UploadTimings(double copyMs, double applyCallMs)
            {
                CopyMs = copyMs;
                ApplyCallMs = applyCallMs;
            }
        }

        // This is an opt-in gate, not a capability verdict. Native initialization also checks
        // shared WASM memory and actual precreated workers; failure keeps the CPU fallback.
        internal static bool Requested
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR && UNITY_6000_0_OR_NEWER
                var settings = GsplatSettings.Instance;
                return settings && settings.EnableNativeWebSort &&
                       SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU;
#else
                return false;
#endif
            }
        }
#if UNITY_WEBGL && !UNITY_EDITOR && UNITY_6000_0_OR_NEWER
        [DllImport("__Internal")] static extern int GsplatWebSort_ApiVersion();
        [DllImport("__Internal")] static extern int GsplatWebSort_Initialize(int requested);
        [DllImport("__Internal")] static extern IntPtr GsplatWebSort_Create(uint capacity);
        [DllImport("__Internal")] static extern void GsplatWebSort_Destroy(IntPtr context);
        [DllImport("__Internal")] static extern void GsplatWebSort_CancelAndWait(IntPtr context);
        [DllImport("__Internal")] static extern SortStatus GsplatWebSort_Status(IntPtr context);
        [DllImport("__Internal")] static extern IntPtr GsplatWebSort_Output(IntPtr context);
        [DllImport("__Internal")] static extern uint GsplatWebSort_NearStart(IntPtr context);
        [DllImport("__Internal")] static extern void GsplatWebSort_SetTimingEnabled(IntPtr context, int enabled);
        [DllImport("__Internal")] static extern int GsplatWebSort_GetTimings(IntPtr context, out NativeTimings timings);
        [DllImport("__Internal")] static extern int GsplatWebSort_Seed(IntPtr context, IntPtr selection, uint count);
        [DllImport("__Internal")] static extern int GsplatWebSort_Start(IntPtr context, IntPtr centers,
            uint centerCount, IntPtr selection, uint count, float m20, float m21, float m22, float m23);
        [DllImport("__Internal")] static extern int GsplatWebSort_StartHybrid(IntPtr context, IntPtr centers,
            uint centerCount, IntPtr selection, uint count, float m20, float m21, float m22, float m23,
            uint nearKey);

        // Native workers persist for the page lifetime; the requested count is fixed policy,
        // not a project setting. The native side clamps it to precreated Unity workers.
        const int k_RequestedWorkers = 4;
        static int s_workers;
        IntPtr m_context;
        GCHandle m_centersPin;
        GCHandle m_selectionPin;
        readonly int m_centerCount;
        readonly int m_uploadBytes;
        bool m_running;
        internal SortStatus Status => m_running ? GsplatWebSort_Status(m_context) : SortStatus.Idle;
        internal bool Running => m_running;
        internal static int WorkerCount => s_workers;
        internal int UploadBytes => m_uploadBytes;
        // Start of the exact suffix, NOT its size. The consumer subtracts this from job count.
        internal int NearStart => m_context != IntPtr.Zero && !m_running
            ? checked((int)GsplatWebSort_NearStart(m_context))
            : 0;

        GsplatWebNativeSort(Vector3[] centers, int outputCapacity)
        {
            m_centerCount = centers.Length;
            m_uploadBytes = checked(outputCapacity * sizeof(uint));
            m_centersPin = GCHandle.Alloc(centers, GCHandleType.Pinned);
            try
            {
                m_context = GsplatWebSort_Create(checked((uint)outputCapacity));
                if (m_context == IntPtr.Zero)
                    throw new OutOfMemoryException("Native sort scratch allocation failed (about 16 bytes per output slot).");
            }
            catch
            {
                m_centersPin.Free();
                throw;
            }
        }

        internal static GsplatWebNativeSort TryCreate(Vector3[] centers, int outputCapacity, out string reason)
        {
            reason = null;
            if (!Requested || centers.Length == 0) return null;
            try
            {
                if (s_workers == 0)
                {
                    if (GsplatWebSort_ApiVersion() != k_NativeApiVersion)
                        throw new NotSupportedException("Native sorter ABI mismatch.");
                    s_workers = GsplatWebSort_Initialize(k_RequestedWorkers);
                    if (s_workers == 0)
                    {
                        reason = "pthread/shared-memory support or precreated workers are unavailable";
                        return null;
                    }
                    if (GsplatSettings.Instance.ShowDebugLog)
                        Debug.Log($"[GsplatSorter] Native WebGPU CPU sort pool ready: {s_workers} worker(s). GPU sorting remains disabled.");
                }
                return new GsplatWebNativeSort(centers, outputCapacity);
            }
            catch (Exception exception) when (exception is DllNotFoundException ||
                exception is EntryPointNotFoundException || exception is NotSupportedException ||
                exception is OutOfMemoryException || exception is OverflowException)
            {
                reason = exception.Message;
                return null;
            }
        }

        internal bool TryStart(uint[] selection, int count, float m20, float m21, float m22, float m23, bool collectTimings = false)
        {
            if (m_running) return false;
            // No managed input copies: these arrays must not be mutated while the request runs.
            try
            {
                if (selection != null) m_selectionPin = GCHandle.Alloc(selection, GCHandleType.Pinned);
                IntPtr selected = m_selectionPin.IsAllocated ? m_selectionPin.AddrOfPinnedObject() : IntPtr.Zero;
                GsplatWebSort_SetTimingEnabled(m_context, collectTimings ? 1 : 0);
                m_running = GsplatWebSort_Start(m_context, m_centersPin.AddrOfPinnedObject(),
                    (uint)m_centerCount, selected, (uint)count, m20, m21, m22, m23) != 0;
                return m_running;
            }
            finally
            {
                if (!m_running && m_selectionPin.IsAllocated) m_selectionPin.Free();
            }
        }
        internal bool TryStartHybrid(uint[] selection, int count, float m20, float m21, float m22, float m23,
            uint nearKey, bool collectTimings = false)
        {
            if (m_running) return false;
            try
            {
                if (selection != null) m_selectionPin = GCHandle.Alloc(selection, GCHandleType.Pinned);
                IntPtr selected = m_selectionPin.IsAllocated ? m_selectionPin.AddrOfPinnedObject() : IntPtr.Zero;
                GsplatWebSort_SetTimingEnabled(m_context, collectTimings ? 1 : 0);
                m_running = GsplatWebSort_StartHybrid(m_context, m_centersPin.AddrOfPinnedObject(),
                    (uint)m_centerCount, selected, (uint)count, m20, m21, m22, m23, nearKey) != 0;
                return m_running;
            }
            finally
            {
                if (!m_running && m_selectionPin.IsAllocated) m_selectionPin.Free();
            }
        }
        internal void CancelAndWait()
        {
            if (m_running)
            {
                // Safe also for a queued/running job; the native side ends all input access.
                GsplatWebSort_CancelAndWait(m_context);
                m_running = false;
            }
            if (m_selectionPin.IsAllocated) m_selectionPin.Free();
        }
        internal void Seed(uint[] selection, int count)
        {
            CancelAndWait();
            GCHandle pin = default;
            try
            {
                if (selection != null && selection.Length > 0) pin = GCHandle.Alloc(selection, GCHandleType.Pinned);
                if (GsplatWebSort_Seed(m_context, pin.IsAllocated ? pin.AddrOfPinnedObject() : IntPtr.Zero, (uint)count) == 0)
                    throw new InvalidOperationException("Cannot initialize native draw order.");
            }
            finally { if (pin.IsAllocated) pin.Free(); }
        }
        internal bool TryGetTimings(out NativeTimings timings) =>
            GsplatWebSort_GetTimings(m_context, out timings) != 0;

        internal UploadTimings UploadOrderTexture(Texture2D texture, bool collectTimings = false)
        {
            if (m_running || texture == null)
                throw new InvalidOperationException("Drain native work before uploading its output.");
            // WASM is little-endian: uint index bytes match the existing RGBA8 order shader.
            // No managed uint[] -> Color32[] loop or duplicate managed output allocation.
            // LoadRawTextureData requires the complete texture payload, so the full capacity is
            // copied even when culling reduced the visible draw count. The WebGPU buffer path
            // instead uploads only the visible prefix.
            double startedAt = collectTimings ? GsplatWebSortDiagnostics.NowMs : 0;
            texture.LoadRawTextureData(GsplatWebSort_Output(m_context), m_uploadBytes);
            double copiedAt = collectTimings ? GsplatWebSortDiagnostics.NowMs : 0;
            texture.Apply(false, false);
            return collectTimings
                ? new UploadTimings(copiedAt - startedAt, GsplatWebSortDiagnostics.NowMs - copiedAt)
                : default;
        }

        internal unsafe UploadTimings UploadOrderBuffer(GraphicsBuffer buffer, int count, bool collectTimings = false)
        {
            if (m_running || buffer == null || count <= 0 || count > buffer.count ||
                (long)count * sizeof(uint) > m_uploadBytes)
                throw new InvalidOperationException("Invalid native order-buffer upload or running job.");
            double startedAt = collectTimings ? GsplatWebSortDiagnostics.NowMs : 0;
            // The native output is already a contiguous uint array. Wrap the visible prefix in
            // a NativeArray view and let GraphicsBuffer upload only that range.
            var data = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<uint>(
                (void*)GsplatWebSort_Output(m_context), count, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = AtomicSafetyHandle.Create();
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref data, safety);
            try
            {
                buffer.SetData(data, 0, 0, count);
            }
            finally
            {
                AtomicSafetyHandle.Release(safety);
            }
#else
            buffer.SetData(data, 0, 0, count);
#endif
            // No CPU repacking copy; this measures the SetData call, not GPU completion.
            return collectTimings
                ? new UploadTimings(0, GsplatWebSortDiagnostics.NowMs - startedAt)
                : default;
        }
        public void Dispose()
        {
            if (m_context == IntPtr.Zero) return;
            CancelAndWait();
            GsplatWebSort_Destroy(m_context);
            m_context = IntPtr.Zero;
            if (m_centersPin.IsAllocated) m_centersPin.Free();
        }
#else
        internal bool Running => false;
        internal static int WorkerCount => 0;
        internal int UploadBytes => 0;
        internal int NearStart => 0;
        internal SortStatus Status => SortStatus.Idle;
        internal static GsplatWebNativeSort TryCreate(Vector3[] centers, int capacity, out string reason)
        { reason = null; return null; }
        internal bool TryStart(uint[] selection, int count, float a, float b, float c, float d, bool collectTimings = false) => false;
        internal bool TryStartHybrid(uint[] selection, int count, float a, float b, float c, float d,
            uint nearKey, bool collectTimings = false) => false;
        internal void CancelAndWait() { }
        internal void Seed(uint[] selection, int count) { }
        internal bool TryGetTimings(out NativeTimings timings) { timings = default; return false; }
        internal UploadTimings UploadOrderTexture(Texture2D texture, bool collectTimings = false) => default;
        internal UploadTimings UploadOrderBuffer(GraphicsBuffer buffer, int count, bool collectTimings = false) => default;
        public void Dispose() { }
#endif
    }
}
