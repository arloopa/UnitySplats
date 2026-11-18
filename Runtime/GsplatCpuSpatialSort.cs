// SPDX-License-Identifier: MIT
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Gsplat
{
    // CPU-only view culling plus one global far-bucket/near-exact order. Culling chooses
    // which leaves enter the sort; per-leaf orders are never concatenated, so cell boundaries
    // cannot create order seams. A previous complete order stays drawable while the next job
    // runs. The near cutoff is a fixed camera-space distance, independent of scene extent and
    // point count, so it behaves like an LOD distance rather than a hidden percentage.
    internal sealed class GsplatCpuSpatialSort : IDisposable
    {
        internal const bool DefaultCullToFrustum = true;
        internal const int DefaultLeafCapacity = 512;
        internal const int DefaultMaxDepth = 10;

        readonly GsplatCpuSpatialSelection m_spatialSelection;
        readonly GsplatCpuHybridSort m_hybrid;
        GsplatWebNativeSort m_native;
        CancellationTokenSource m_cancel;
        Task m_task;

        bool m_working, m_disposed, m_orthographic, m_hasPublished;
        bool m_allowCulling, m_jobCullToFrustum, m_lastCullToFrustum, m_forceSort;
        Matrix4x4 m_localToView, m_projection;
        Matrix4x4 m_lastView, m_lastMatrix, m_lastProjection;
        float m_lastScale, m_pixelHeight, m_lastPixelHeight, m_projectionScale, m_lastProjectionScale;
        bool m_lastOrthographic;
        Vector3 m_viewPosition;
        Matrix4x4 m_jobView, m_jobMatrix, m_jobProjection, m_jobLocalToView;
        float m_jobScale, m_jobPixelHeight, m_jobProjectionScale;
        bool m_jobOrthographic, m_jobMeasured;
        int m_jobCount, m_jobs, m_sortedPoints, m_updates, m_uploads, m_lastNearPoints, m_lastJobCount;
        uint m_jobNearKey, m_lastNearKey;
        double m_lastCullMs, m_cullSum, m_cullMax, m_lastUploadMs, m_currentUpdateMs;
        int m_lastUploadBytes, m_culls;
        double m_jobAt, m_workerStartedAt, m_workerFinishedAt, m_lastKeys, m_lastWork, m_lastCollect, m_lastJobAge;
        // Change of the local-to-view depth axis since the job snapshot, including model
        // orientation. Both vectors must be expressed in the same local coordinate system.
        double m_lastPublishAngle, m_publishAngleMax;
        double m_logAt = GsplatWebSortDiagnostics.NowMs, m_updateSum, m_updateMax, m_uploadSum, m_uploadMax;

        internal readonly bool NativeRequested = GsplatWebNativeSort.Requested;
        internal uint[] Output => m_hybrid?.Result;
        internal readonly int LeafCapacity, MaxDepth;
        internal bool Ready => m_hasPublished;
        internal bool CullingEnabled => m_allowCulling;
        internal int VisibleCount { get; private set; }
        internal string Execution => m_native != null ? "native-hybrid" : "managed-hybrid";

        internal GsplatCpuSpatialSort(Vector3[] centers, uint[] selected, int count,
            GsplatAsset asset, int outputCapacity, int leafCapacity, int maxDepth,
            bool cullToFrustum = true)
        {
            double started = GsplatWebSortDiagnostics.NowMs;
            LeafCapacity = Math.Clamp(leafCapacity, 128, 16384);
            MaxDepth = Math.Clamp(maxDepth, 1, 16);
            if (outputCapacity < count)
                throw new ArgumentOutOfRangeException(nameof(outputCapacity));
            if (centers == null) throw new ArgumentNullException(nameof(centers));
            // Reject unavailable Web execution before the synchronous O(N) tree build.
            m_native = GsplatWebNativeSort.TryCreate(centers, outputCapacity, out var reason);
            if (m_native == null && !GsplatUtils.SupportsBackgroundCpuSort)
                throw new NotSupportedException(reason ??
                    "CPU hybrid sorting requires native Web workers or background CPU threads.");
            try
            {
                m_spatialSelection = new GsplatCpuSpatialSelection(centers, selected, count, asset,
                    LeafCapacity, MaxDepth, cullToFrustum);
                if (m_native == null)
                    m_hybrid = new GsplatCpuHybridSort(centers, outputCapacity);
            }
            catch
            {
                // Failed source validation/tree allocation must not retain a native session or pins.
                m_native?.Dispose();
                m_native = null;
                throw;
            }
            if (m_native == null && reason != null)
                Debug.LogWarning($"[GsplatSorter] CPU hybrid sorting uses managed execution: {reason}", asset);
            string mode = m_spatialSelection.CullToFrustum ? "cull + global" : "global";
            string detail = m_spatialSelection.CullToFrustum
                ? $", {m_spatialSelection.LeafCount:N0} leaves; visibility-culled order"
                : "; culling disabled, every source ID is included";
            string algorithm = NearKey(GsplatSettings.Instance.CpuNearExactDistance) == 0
                ? "exact 32-bit"
                : "near-exact/far-bucket hybrid";
            if (GsplatSettings.Instance.ShowDebugLog)
                Debug.Log($"[GsplatSorter] CPU {mode} {algorithm} sorting active: " +
                    $"{count:N0} points{detail}; {Execution}; build " +
                    $"{GsplatWebSortDiagnostics.NowMs - started:F1} ms. Uploads follow job completion; " +
                    "GPU sorting is unchanged.", asset);
        }

        internal bool Update(Camera camera, Matrix4x4 matrix, float scaleFactor, GsplatSettings settings,
            bool allowCulling = true)
        {
            if (m_disposed) return false;
            double started = settings.ShowDebugLog ? GsplatWebSortDiagnostics.NowMs : 0;
            uint nearKey = NearKey(settings.CpuNearExactDistance);
            for (int i = 0; i < 16; ++i)
                if (!GsplatCpuOctree.Finite(matrix[i])) throw new ArgumentException("Non-finite spatial-sort transform.");
            if (!GsplatCpuOctree.Finite(scaleFactor) || !GsplatCpuOctree.Finite(camera.transform.position))
                throw new ArgumentException("Non-finite spatial-sort camera or scale.");
            var view = camera.worldToCameraMatrix;
            m_localToView = view * matrix;
            for (int i = 0; i < 16; ++i)
                if (!GsplatCpuOctree.Finite(m_localToView[i]))
                    throw new ArgumentException("Non-finite spatial-sort view transform.");
            m_viewPosition = camera.transform.position;
            m_pixelHeight = Mathf.Max(1, camera.pixelHeight);
            m_projection = camera.projectionMatrix;
            m_projectionScale = Mathf.Max(0.0001f, Math.Abs(m_projection.m11));
            m_orthographic = camera.orthographic;

            m_allowCulling = allowCulling && m_spatialSelection.CullToFrustum;
            if (!m_allowCulling && ((m_hasPublished && m_lastCullToFrustum) ||
                (m_working && m_jobCullToFrustum)))
            {
                // Hard visibility-mode transition only: a culled order cannot serve another
                // camera. Drain once, publish all IDs, then sort them after this upload.
                CancelJob();
                SeedFullOrder();
                RecordUpdate(started, settings);
                return true;
            }

            if (m_working)
            {
                bool published = CollectJob();
                RecordUpdate(started, settings);
                return published;
            }
            // No upload pacing: completed output is uploaded before replacement work starts
            // in the same CPU tick. Refresh is limited by sort time and CPU tick cadence.
            if (SameInputs(view, matrix, scaleFactor, nearKey))
            {
                RecordUpdate(started, settings);
                return false;
            }
            bool changed = CullAndStart(camera, view, matrix, scaleFactor, nearKey, settings);
            RecordUpdate(started, settings);
            return changed;
        }

        // Call after Update in the same CPU tick, with the same camera/model/settings snapshot;
        // this continues using the local-to-view/projection/culling state cached by Update.
        // Any nonempty result published by Update must already have been uploaded. Starting
        // sooner can overwrite the one native/managed output buffer before it is consumed.
        internal void TryStartNext(Camera camera, Matrix4x4 matrix, float scaleFactor, GsplatSettings settings)
        {
            if (m_disposed || m_working) return;
            uint nearKey = NearKey(settings.CpuNearExactDistance);
            var view = camera.worldToCameraMatrix;
            if (SameInputs(view, matrix, scaleFactor, nearKey)) return;
            double started = settings.ShowDebugLog ? GsplatWebSortDiagnostics.NowMs : 0;
            CullAndStart(camera, view, matrix, scaleFactor, nearKey, settings);
            // If culling publishes an empty result, no GPU upload is needed; VisibleCount is zero.
            RecordUpdate(started, settings, continuation: true);
        }

        void SeedFullOrder()
        {
            int count = m_spatialSelection.SourceCount;
            uint[] source = m_spatialSelection.SourceIds;
            if (m_native != null)
                m_native.Seed(source, count);
            else if (source != null)
                Array.Copy(source, m_hybrid.Result, count);
            else
                for (int i = 0; i < count; ++i) m_hybrid.Result[i] = (uint)i;
            VisibleCount = m_jobCount = m_lastJobCount = count;
            m_lastNearPoints = 0;
            m_lastKeys = m_lastWork = m_lastCollect = m_lastJobAge = m_lastPublishAngle = 0;
            m_lastCullMs = 0;
            m_lastCullToFrustum = false;
            m_hasPublished = m_forceSort = true;
        }

        bool CullAndStart(Camera camera, Matrix4x4 view, Matrix4x4 matrix, float scaleFactor,
            uint nearKey, GsplatSettings settings)
        {
            if (m_allowCulling) m_spatialSelection.UpdateBounds(matrix, scaleFactor);
            double cullStarted = settings.ShowDebugLog && m_allowCulling
                ? GsplatWebSortDiagnostics.NowMs : 0;
            int cursor = m_spatialSelection.Collect(camera, view, m_viewPosition, m_pixelHeight,
                m_projectionScale, m_orthographic, m_allowCulling);
            if (cullStarted > 0)
            {
                double cullMs = GsplatWebSortDiagnostics.NowMs - cullStarted;
                m_lastCullMs = cullMs;
                m_cullSum += cullMs;
                m_cullMax = Math.Max(m_cullMax, cullMs);
                ++m_culls;
            }
            uint[] selection = m_spatialSelection.Selection;

            m_jobView = view;
            m_jobLocalToView = m_localToView;
            m_jobCullToFrustum = m_allowCulling;
            m_jobMatrix = matrix;
            m_jobProjection = m_projection;
            m_jobScale = scaleFactor;
            m_jobPixelHeight = m_pixelHeight;
            m_jobProjectionScale = m_projectionScale;
            m_jobOrthographic = m_orthographic;
            m_jobCount = cursor;
            m_jobNearKey = nearKey;
            m_jobMeasured = settings.ShowDebugLog;
            m_jobAt = GsplatWebSortDiagnostics.NowMs;
            m_forceSort = false;

            if (cursor == 0)
            {
                bool changed = VisibleCount != 0;
                VisibleCount = 0;
                m_lastJobCount = 0;
                m_lastNearPoints = 0;
                m_lastJobAge = 0;
                PublishJobInputs();
                return changed;
            }

            if (m_native != null)
            {
                bool started = m_jobNearKey == 0
                    ? m_native.TryStart(selection, cursor, m_localToView.m20, m_localToView.m21,
                        m_localToView.m22, m_localToView.m23, m_jobMeasured)
                    : m_native.TryStartHybrid(selection, cursor, m_localToView.m20, m_localToView.m21,
                        m_localToView.m22, m_localToView.m23, m_jobNearKey, m_jobMeasured);
                if (!started)
                    throw new InvalidOperationException("Native sort submission failed.");
            }
            else
            {
                m_cancel = new CancellationTokenSource();
                var token = m_cancel.Token;
                // The main thread updates current-view fields while this task is queued or
                // running. Capture the job inputs, not the mutable current-view state.
                var jobLocalToView = m_jobLocalToView;
                int jobCount = cursor;
                uint jobNearKey = nearKey;
                m_workerStartedAt = m_workerFinishedAt = 0;
                try
                {
                    m_task = Task.Run(() =>
                    {
                        m_workerStartedAt = GsplatWebSortDiagnostics.NowMs;
                        m_hybrid.Sort(selection, jobCount, jobNearKey, jobLocalToView, token);
                        m_workerFinishedAt = GsplatWebSortDiagnostics.NowMs;
                    }, token);
                }
                catch (Exception exception)
                {
                    m_cancel.Dispose();
                    m_cancel = null;
                    throw new InvalidOperationException("CPU hybrid job submission failed.", exception);
                }
            }
            m_working = true;
            return false;
        }

        // Returns true when the completed job published a new draw order.
        bool CollectJob()
        {
            if (m_native != null)
            {
                var state = m_native.Status;
                if (state is GsplatWebNativeSort.SortStatus.Queued or GsplatWebNativeSort.SortStatus.Running)
                    return false;
                m_native.CancelAndWait();
                if (state != GsplatWebNativeSort.SortStatus.Complete)
                {
                    m_working = false;
                    throw new InvalidOperationException($"Native hybrid sort ended with {state}.");
                }
                if (m_jobMeasured && m_native.TryGetTimings(out var timings))
                {
                    m_lastKeys = timings.KeysMs;
                    m_lastWork = timings.RadixMs;
                    m_lastCollect = timings.CollectMs;
                }
                if (m_jobMeasured)
                    m_lastNearPoints = m_jobCount - m_native.NearStart;
                // Native output remains in the native session for a direct upload.
            }
            else
            {
                if (m_task == null) throw new InvalidOperationException("CPU hybrid job disappeared.");
                if (!m_task.IsCompleted) return false;
                try
                {
                    m_task.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    m_task = null;
                    m_working = false;
                    m_cancel?.Dispose();
                    m_cancel = null;
                    throw;
                }
                catch (Exception exception)
                {
                    m_task = null;
                    m_working = false;
                    m_cancel?.Dispose();
                    m_cancel = null;
                    throw new InvalidOperationException("CPU hybrid sort failed.", exception);
                }
                m_task = null;
                if (m_jobMeasured)
                {
                    m_lastKeys = 0;
                    m_lastWork = m_workerFinishedAt - m_workerStartedAt;
                    m_lastCollect = GsplatWebSortDiagnostics.NowMs - m_workerFinishedAt;
                }
                // Result is uploaded before another job can reuse its storage. The old
                // drawable order already lives on the GPU; no CPU publication copy is needed.
                m_lastNearPoints = m_hybrid.NearCount;
            }

            VisibleCount = m_jobCount;
            m_sortedPoints += m_jobCount;
            ++m_jobs;
            m_lastJobCount = m_jobCount;
            m_lastJobAge = GsplatWebSortDiagnostics.NowMs - m_jobAt;
            if (m_jobMeasured)
            {
                m_lastPublishAngle = ViewAngleDegrees(m_jobLocalToView, m_localToView);
                m_publishAngleMax = Math.Max(m_publishAngleMax, m_lastPublishAngle);
            }
            m_working = false;
            m_cancel?.Dispose();
            m_cancel = null;
            PublishJobInputs();
            return true;
        }

        // Depth order follows the view-space forward axis, so this is the rotation that can
        // invalidate the published order.
        static double ViewAngleDegrees(Matrix4x4 from, Matrix4x4 to)
        {
            var a = new Vector3(from.m20, from.m21, from.m22);
            var b = new Vector3(to.m20, to.m21, to.m22);
            return Vector3.Angle(a, b);
        }

        bool SameInputs(Matrix4x4 view, Matrix4x4 matrix, float scaleFactor, uint nearKey)
        {
            return m_hasPublished && !m_working && !m_forceSort &&
                m_lastCullToFrustum == m_allowCulling &&
                m_lastScale == scaleFactor && m_lastMatrix.Equals(matrix) &&
                m_lastView.Equals(view) && m_lastProjection.Equals(m_projection) &&
                m_lastPixelHeight == m_pixelHeight && m_lastProjectionScale == m_projectionScale &&
                m_lastOrthographic == m_orthographic && m_lastNearKey == nearKey;
        }

        void PublishJobInputs()
        {
            m_lastView = m_jobView;
            m_lastCullToFrustum = m_jobCullToFrustum;
            m_lastMatrix = m_jobMatrix;
            m_lastProjection = m_jobProjection;
            m_lastScale = m_jobScale;
            m_lastPixelHeight = m_jobPixelHeight;
            m_lastProjectionScale = m_jobProjectionScale;
            m_lastOrthographic = m_jobOrthographic;
            m_lastNearKey = m_jobNearKey;
            m_hasPublished = true;
        }

        void RecordUpdate(double started, GsplatSettings settings, bool continuation = false)
        {
            if (!settings.ShowDebugLog) return;
            double elapsed = GsplatWebSortDiagnostics.NowMs - started;
            m_updateSum += elapsed;
            m_currentUpdateMs = continuation ? m_currentUpdateMs + elapsed : elapsed;
            m_updateMax = Math.Max(m_updateMax, m_currentUpdateMs);
            if (!continuation) ++m_updates;
        }

        internal static uint NearKey(float distance)
        {
            // Unity view-space Z is negative in front of the camera, so a positive camera-space
            // distance becomes a negative depth. A non-positive or invalid value means "no
            // cutoff": key 0 places every point in the exact suffix, which is also the fail-safe
            // choice when a stored setting is corrupted.
            if (!(distance > 0) || float.IsNaN(distance) || float.IsInfinity(distance))
                return 0;
            return GsplatCpuDepth.DepthKey(-distance);
        }

        internal bool HasNativeOutput => m_native != null;

        // Uploads the native buffer directly, avoiding a managed copy of the whole output.
        // Returns the number of bytes written to the texture.
        internal int UploadNativeOrder(Texture2D texture)
        {
            if (m_native == null || texture == null || m_jobCount <= 0) return 0;
            m_native.UploadOrderTexture(texture);
            return m_native.UploadBytes;
        }

        internal int UploadNativeOrder(GraphicsBuffer buffer)
        {
            if (m_native == null || buffer == null || m_jobCount <= 0) return 0;
            m_native.UploadOrderBuffer(buffer, m_jobCount);
            return m_jobCount * sizeof(uint);
        }

        internal void RecordUpload(double elapsed, int bytes, bool collectStats)
        {
            elapsed = Math.Max(0, elapsed);
            // Diagnostic only: the newest upload cost, not a moving average, and never a
            // scheduling input. Job cadence follows sort completion.
            m_lastUploadMs = elapsed;
            m_lastUploadBytes = Math.Max(0, bytes);
            if (!collectStats) return;
            m_uploadSum += elapsed;
            m_uploadMax = Math.Max(m_uploadMax, elapsed);
            ++m_uploads;
        }

        internal void Report(GsplatAsset asset, bool enabled)
        {
            double now = GsplatWebSortDiagnostics.NowMs;
            if (!enabled || now - m_logAt < 1000) return;
            Debug.Log($"[GsplatCpuSpatialTiming] {asset.name}: backend={Execution}, " +
                $"cull={(m_allowCulling ? "on" : "off")}, " +
                $"visibleLeaves={m_spatialSelection.VisibleLeafCount}/" +
                $"{m_spatialSelection.LeafCount}, visiblePoints(last)={m_lastJobCount}, " +
                $"nearPoints(last)={m_lastNearPoints}, draw={VisibleCount}, " +
                $"jobs={m_jobs}, applyHz={m_jobs * 1000.0 / Math.Max(1, now - m_logAt):F2}, " +
                $"sortedPoints={m_sortedPoints}, " +
                $"updateMs(avg/max)={m_updateSum / Math.Max(1, m_updates):F2}/{m_updateMax:F2}, " +
                $"cullMs(last/avg/max)={m_lastCullMs:F2}/{m_cullSum / Math.Max(1, m_culls):F2}/{m_cullMax:F2}, " +
                $"uploadCalls={m_uploads}, jobAgeMs(last)={m_lastJobAge:F2}, " +
                $"publishAngleDeg(last/max)={m_lastPublishAngle:F1}/{m_publishAngleMax:F1}, " +
                $"keysMs(last)={m_lastKeys:F2}, " +
                $"workMs(last)={m_lastWork:F2}, collectMs(last)={m_lastCollect:F2}, " +
                $"uploadMiB(last)={m_lastUploadBytes / (1024.0 * 1024.0):F2}, " +
                $"uploadMs(last)={m_lastUploadMs:F2}, " +
                $"uploadCallMs(avg/max)={m_uploadSum / Math.Max(1, m_uploads):F2}/{m_uploadMax:F2}",
                asset);
            m_logAt = now;
            m_updateSum = m_updateMax = m_uploadSum = m_uploadMax = 0;
            m_publishAngleMax = 0;
            m_cullSum = m_cullMax = 0;
            m_updates = m_uploads = m_jobs = m_sortedPoints = m_culls = 0;
        }

        void CancelJob()
        {
            m_cancel?.Cancel();
            m_native?.CancelAndWait();
            try { m_task?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            finally
            {
                m_task = null;
                m_cancel?.Dispose();
                m_cancel = null;
                m_working = false;
            }
        }

        public void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            try { CancelJob(); }
            catch (Exception exception) { Debug.LogException(exception); }
            finally
            {
                m_native?.Dispose();
                m_native = null;
            }
        }
    }
}
