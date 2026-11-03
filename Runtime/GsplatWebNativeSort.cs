// SPDX-License-Identifier: MIT
using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    /// <summary>A session owns native scratch and pins immutable CPU inputs until workers stop using them.</summary>
    internal sealed class GsplatWebNativeSort : IDisposable
    {
        // ABI values match the native GS_* states.
        internal enum SortStatus
        {
            Idle = 0,
            Queued = 1,
            Running = 2,
            Complete = 3,
            Cancelled = 4,
            Invalid = 5
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
        [DllImport("__Internal")] static extern int GsplatWebSort_Seed(IntPtr context, IntPtr selection, uint count);
        [DllImport("__Internal")] static extern int GsplatWebSort_Start(IntPtr context, IntPtr centers,
            uint centerCount, IntPtr selection, uint count, float m20, float m21, float m22, float m23);

        // Native workers persist for the page lifetime; changing the requested count needs a reload.
        static int s_workers;
        IntPtr m_context;
        GCHandle m_centersPin;
        GCHandle m_selectionPin;
        readonly int m_centerCount;
        readonly int m_uploadBytes;
        bool m_running;
        internal SortStatus Status => m_running ? GsplatWebSort_Status(m_context) : SortStatus.Idle;
        internal bool Running => m_running;

        GsplatWebNativeSort(Vector3[] centers, int outputCapacity)
        {
            m_centerCount = centers.Length;
            m_uploadBytes = checked(outputCapacity * sizeof(uint));
            m_centersPin = GCHandle.Alloc(centers, GCHandleType.Pinned);
            try
            {
                m_context = GsplatWebSort_Create(checked((uint)outputCapacity));
                if (m_context == IntPtr.Zero)
                    throw new OutOfMemoryException("Native radix scratch allocation failed (about 16 bytes per output slot).");
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
                    if (GsplatWebSort_ApiVersion() != 1)
                        throw new NotSupportedException("Native sorter ABI mismatch.");
                    s_workers = GsplatWebSort_Initialize(Mathf.Clamp(GsplatSettings.Instance.NativeWebSortWorkers, 1, 8));
                    if (s_workers == 0)
                    {
                        reason = "pthread/shared-memory support or precreated workers are unavailable";
                        return null;
                    }
                    Debug.Log($"[GsplatSorter] Native WebGPU CPU radix-sort pool ready: {s_workers} worker(s). GPU sorting remains disabled.");
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

        internal bool TryStart(uint[] selection, int count, float m20, float m21, float m22, float m23)
        {
            if (m_running) return false;
            // No managed input copies: these arrays must not be mutated while the request runs.
            try
            {
                if (selection != null) m_selectionPin = GCHandle.Alloc(selection, GCHandleType.Pinned);
                IntPtr selected = m_selectionPin.IsAllocated ? m_selectionPin.AddrOfPinnedObject() : IntPtr.Zero;
                m_running = GsplatWebSort_Start(m_context, m_centersPin.AddrOfPinnedObject(),
                    (uint)m_centerCount, selected, (uint)count, m20, m21, m22, m23) != 0;
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
        internal void UploadOrderTexture(Texture2D texture)
        {
            // WASM is little-endian: uint index bytes match the existing RGBA8 order shader.
            // No managed uint[] -> Color32[] loop or duplicate managed output allocation.
            // LoadRawTextureData still copies into Unity texture storage; this is not GPU zero-copy.
            // Keep the texture readable because subsequent sort results update it again.
            texture.LoadRawTextureData(GsplatWebSort_Output(m_context), m_uploadBytes);
            texture.Apply(false, false);
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
        internal SortStatus Status => SortStatus.Idle;
        internal static GsplatWebNativeSort TryCreate(Vector3[] centers, int capacity, out string reason)
        { reason = null; return null; }
        internal bool TryStart(uint[] selection, int count, float a, float b, float c, float d) => false;
        internal void CancelAndWait() { }
        internal void Seed(uint[] selection, int count) { }
        internal void UploadOrderTexture(Texture2D texture) { }
        public void Dispose() { }
#endif
    }
}
