// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Gsplat.Tests
{
    public class GsplatCpuSpatialSortTests
    {
        [Test]
        public void DisablingSpatialSortRestoresFullIdentityOrderAndDrawCount()
        {
            var settings = GsplatSettings.Instance;
            bool previous = settings.EnableCpuSortOptimization;
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            var texture = new Texture2D(2, 1, TextureFormat.RGBA32, false, true);
            ISorterResource resource = null;
            try
            {
                settings.EnableCpuSortOptimization = true;
                asset.SplatCount = 2;
                asset.Positions = new[] { Vector3.zero, Vector3.one };
                asset.Scales = new[] { Vector3.one, Vector3.one };
                var type = typeof(GsplatSorter).GetNestedType("Resource", BindingFlags.NonPublic);
                resource = (ISorterResource)Activator.CreateInstance(type, new object[] { 2u, null, false, texture });
                type.GetMethod("BindCpuCenters").Invoke(resource, new object[] { asset, asset.Positions });
                var spatial = new GsplatCpuSpatialSort(asset.Positions, null, 2, asset, 2, 256, 10);
                type.GetField("m_spatialSort", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(resource, spatial);
                texture.LoadRawTextureData(new byte[] { 1, 0, 0, 0, 1, 0, 0, 0 });
                texture.Apply(false, false);
                Assert.AreEqual(0u, GsplatSorter.Instance.GetCpuDrawCount(resource, 2));
                settings.EnableCpuSortOptimization = false;
                Assert.IsFalse((bool)type.GetMethod("TryUpdateCpuSpatial").Invoke(resource, new object[] { null, null }));
                Assert.AreEqual(2u, GsplatSorter.Instance.GetCpuDrawCount(resource, 2));
                CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 1, 0, 0, 0 }, texture.GetRawTextureData());
                Assert.IsTrue(resource.Initialized);
            }
            finally
            {
                resource?.Dispose();
                settings.EnableCpuSortOptimization = previous;
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void GpuResourceRejectsSpatialWorkBeforeAccessingCpuState()
        {
            var type = typeof(GsplatSorter).GetNestedType("Resource", BindingFlags.NonPublic);
            var resource = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            type.GetField("m_usesGpuSort", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(resource, true);
            Assert.IsFalse((bool)type.GetMethod("TryUpdateCpuSpatial").Invoke(resource, new object[] { null, null }));
            Assert.IsNull(type.GetField("m_spatialSort", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(resource));
        }

        [Test]
        public void ViewDepthKeysOrderByCameraDepthNotRadius()
        {
            var view = Matrix4x4.Scale(new Vector3(1, 1, -1));
            Assert.Less(GsplatCpuDepth.ViewDepthKey(new Vector3(0, 0, 12), view),
                GsplatCpuDepth.ViewDepthKey(new Vector3(6, 0, 10), view));
            Assert.AreEqual(GsplatCpuDepth.ViewDepthKey(new Vector3(0, 0, 10), view),
                GsplatCpuDepth.ViewDepthKey(new Vector3(6, 0, 10), view));
        }

        [TestCase(false, 10)]
        [TestCase(false, 1)]
        [TestCase(true, 10)]
        public void OctreePartitionsEverySelectedIdIntoBoundedLeaves(bool degenerate, int depth)
        {
            var points = Enumerable.Range(0, 10003).Select(i => degenerate ? Vector3.one :
                new Vector3(i % 97, i % 43, i % 257)).ToArray();
            var selected = Enumerable.Range(0, 4001).Select(i => (uint)(i * 2)).Reverse().ToArray();
            var tree = new GsplatCpuOctree(points, selected, selected.Length, _ => 2, 256, depth);
            CollectionAssert.AreEquivalent(selected, tree.Indices);
            CollectionAssert.AreEqual(selected, Enumerable.Range(0, 4001).Select(i => (uint)(i * 2)).Reverse());
            int cursor = 0;
            foreach (int index in tree.Leaves.OrderBy(i => tree.Nodes[i].Start))
            {
                var node = tree.Nodes[index];
                Assert.AreEqual(cursor, node.Start);
                Assert.That(node.Count, Is.InRange(1, 256));
                Assert.AreEqual(2, node.SupportRadius);
                cursor += node.Count;
            }
            Assert.AreEqual(selected.Length, cursor);
        }

        [Test]
        public void ExtendedGaussianBoundsSurviveNonuniformTransforms()
        {
            var node = new GsplatCpuOctree.Node { Bounds = new Bounds(new Vector3(2, 3, 4), Vector3.one), SupportRadius = 4 };
            var matrix = Matrix4x4.TRS(new Vector3(3, -2, 5), Quaternion.Euler(30, 20, 10), new Vector3(2, -3, 0.5f));
            var bounds = GsplatCpuOctree.TransformBounds(node, matrix, 1);
            for (int i = 0; i < 8; ++i)
            {
                var corner = node.Bounds.center + new Vector3((i & 1) == 0 ? -4.5f : 4.5f,
                    (i & 2) == 0 ? -4.5f : 4.5f, (i & 4) == 0 ? -4.5f : 4.5f);
                Assert.LessOrEqual((bounds.ClosestPoint(matrix.MultiplyPoint3x4(corner)) - matrix.MultiplyPoint3x4(corner)).magnitude, 0.0001f);
            }
        }

        static GsplatAssetUncompressed CreateSeparatedAsset(out uint[] selected)
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            asset.SplatCount = 2048;
            asset.Positions = Enumerable.Range(0, 2048)
                // Avoid eye-plane-crossing padding: such bounds are deliberately retained.
                .Select(i => new Vector3(i < 1024 ? 0 : 100, 0, 10)).ToArray();
            asset.Scales = Enumerable.Repeat(Vector3.one * 0.001f, 2048).ToArray();
            selected = Enumerable.Range(0, 1024).Select(i => (uint)((1023 - i) * 2 + 1)).ToArray();
            return asset;
        }

        [Test]
        public void RuntimeCullingModeSelectsOriginalSparseSourceArray()
        {
            var asset = CreateSeparatedAsset(out var ids);
            var cameraObject = new GameObject("CPU selection mode test");
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                camera.pixelRect = new Rect(0, 0, 512, 512);
                var selection = new GsplatCpuSpatialSelection(asset.Positions, ids, ids.Length, asset, 128, 10, true);
                selection.UpdateBounds(Matrix4x4.identity, 1);
                var view = camera.worldToCameraMatrix;
                float projectionScale = Math.Abs(camera.projectionMatrix.m11);
                int culled = selection.Collect(camera, view, camera.transform.position, 512, projectionScale, false);
                Assert.AreEqual(512, culled);
                Assert.IsFalse(ReferenceEquals(ids, selection.Selection));
                CollectionAssert.AreEquivalent(ids.Where(id => asset.Positions[id].x == 0), selection.Selection.Take(culled));
                Assert.AreEqual(1024, selection.Collect(camera, view, camera.transform.position, 512,
                    projectionScale, false, allowCulling: false));
                Assert.AreSame(ids, selection.Selection);
                Assert.AreEqual(512, selection.Collect(camera, view, camera.transform.position, 512,
                    projectionScale, false, allowCulling: true));
                CollectionAssert.AreEqual(Enumerable.Range(0, 1024).Select(i => (uint)((1023 - i) * 2 + 1)), ids);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void MultiCameraTransitionSeedsEverySourceIdBeforeReplacement()
        {
            var asset = CreateSeparatedAsset(out var ids);
            var cameraObject = new GameObject("CPU visibility transition test");
            GsplatCpuSpatialSort spatial = null;
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                camera.pixelRect = new Rect(0, 0, 512, 512);
                var settings = GsplatSettings.Instance;
                spatial = new GsplatCpuSpatialSort(asset.Positions, ids, ids.Length, asset, ids.Length, 128, 10);
                Assert.IsFalse(spatial.Update(camera, Matrix4x4.identity, 1, settings));
                // Transition while the culled job is still owned by the scheduler (running or complete).
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.identity, 1, settings, allowCulling: false));
                Assert.IsTrue(spatial.Ready);
                Assert.IsFalse(spatial.CullingEnabled);
                Assert.AreEqual(ids.Length, spatial.VisibleCount);
                CollectionAssert.AreEqual(ids, spatial.Output.Take(ids.Length));
                Assert.IsNull(typeof(GsplatCpuSpatialSort).GetField("m_task", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(spatial), "The all-ID seed must be consumed before replacement starts.");
                var uploadedSeed = spatial.Output.Take(spatial.VisibleCount).ToArray();
                spatial.TryStartNext(camera, Matrix4x4.identity, 1, settings);
                WaitForManagedJob(spatial);
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.identity, 1, settings, allowCulling: false));
                Assert.AreEqual(ids.Length, spatial.VisibleCount);
                CollectionAssert.AreEqual(ids, spatial.Output.Take(ids.Length));
                CollectionAssert.AreEqual(ids, uploadedSeed);
                // Returning to a single camera must be an input change, not a SameInputs hit.
                Assert.IsFalse(spatial.Update(camera, Matrix4x4.identity, 1, settings, allowCulling: true));
                Assert.AreEqual(ids.Length, spatial.VisibleCount, "Keep the complete old order while reculling.");
                WaitForManagedJob(spatial);
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.identity, 1, settings, allowCulling: true));
                Assert.AreEqual(512, spatial.VisibleCount);
            }
            finally
            {
                spatial?.Dispose();
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void CollectUploadStartReplacesInSameTickWithoutOverwritingPublishedOutput()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            var cameraObject = new GameObject("CPU collect upload start test");
            GsplatCpuSpatialSort spatial = null;
            try
            {
                asset.SplatCount = 3;
                asset.Positions = new[] { new Vector3(0, 0, 5), new Vector3(0, 0, 10), new Vector3(0, 0, 15) };
                var camera = cameraObject.AddComponent<Camera>();
                var settings = GsplatSettings.Instance;
                spatial = new GsplatCpuSpatialSort(asset.Positions, null, 3, asset, 3, 128, 10, false);
                spatial.Update(camera, Matrix4x4.identity, 1, settings);
                WaitForManagedJob(spatial);
                camera.transform.rotation = Quaternion.Euler(0, 180, 0);
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.identity, 1, settings));
                var taskField = typeof(GsplatCpuSpatialSort).GetField("m_task", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNull(taskField.GetValue(spatial));
                CollectionAssert.AreEqual(new uint[] { 2, 1, 0 }, spatial.Output);
                var uploaded = spatial.Output.ToArray();
                spatial.TryStartNext(camera, Matrix4x4.identity, 1, settings);
                Assert.NotNull(taskField.GetValue(spatial), "Replacement starts in the same CPU tick, after upload.");
                WaitForManagedJob(spatial);
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.identity, 1, settings));
                CollectionAssert.AreEqual(new uint[] { 0, 1, 2 }, spatial.Output);
                CollectionAssert.AreEqual(new uint[] { 2, 1, 0 }, uploaded);
                spatial.TryStartNext(camera, Matrix4x4.identity, 1, settings);
                Assert.IsNull(taskField.GetValue(spatial), "Unchanged published inputs must not resubmit.");
            }
            finally
            {
                spatial?.Dispose();
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void PublishAngleComparesJobAndCurrentLocalDepthAxes()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            var cameraObject = new GameObject("CPU depth axis metric test");
            var settings = GsplatSettings.Instance;
            bool previousLog = settings.ShowDebugLog;
            GsplatCpuSpatialSort spatial = null;
            try
            {
                settings.ShowDebugLog = true;
                asset.SplatCount = 2;
                asset.Positions = new[] { new Vector3(0, 0, 5), new Vector3(0, 0, 10) };
                var camera = cameraObject.AddComponent<Camera>();
                spatial = new GsplatCpuSpatialSort(asset.Positions, null, 2, asset, 2, 128, 10, false);
                var matrix = Matrix4x4.Rotate(Quaternion.Euler(0, 90, 0));
                spatial.Update(camera, matrix, 1, settings);
                var jobMatrix = typeof(GsplatCpuSpatialSort).GetField("m_jobLocalToView", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.AreEqual(camera.worldToCameraMatrix * matrix, (Matrix4x4)jobMatrix.GetValue(spatial));
                WaitForManagedJob(spatial);
                Assert.IsTrue(spatial.Update(camera, matrix, 1, settings));
                var angleField = typeof(GsplatCpuSpatialSort).GetField("m_lastPublishAngle", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That((double)angleField.GetValue(spatial), Is.EqualTo(0).Within(0.01));
                matrix = Matrix4x4.Rotate(Quaternion.Euler(0, 120, 0));
                spatial.Update(camera, matrix, 1, settings);
                WaitForManagedJob(spatial);
                matrix = Matrix4x4.Rotate(Quaternion.Euler(0, 150, 0));
                Assert.IsTrue(spatial.Update(camera, matrix, 1, settings));
                Assert.That((double)angleField.GetValue(spatial), Is.EqualTo(30).Within(0.01));
            }
            finally
            {
                spatial?.Dispose();
                settings.ShowDebugLog = previousLog;
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void OnlyDistinctLayerCompatibleCamerasDisableResourceCulling()
        {
            var a = new GameObject("CPU camera observation A");
            var b = new GameObject("CPU camera observation B");
            ISorterResource resource = null;
            try
            {
                var type = typeof(GsplatSorter).GetNestedType("Resource", BindingFlags.NonPublic);
                resource = (ISorterResource)Activator.CreateInstance(type, new object[] { 2u, null, false, null });
                var observe = type.GetMethod("ObserveCpuCamera");
                var allow = type.GetMethod("AllowCpuSpatialCulling");
                var first = a.AddComponent<Camera>();
                var second = b.AddComponent<Camera>();
                first.cullingMask = 1 << 3;
                second.cullingMask = 1 << 4;
                observe.Invoke(resource, new object[] { first, 3 });
                observe.Invoke(resource, new object[] { first, 3 });
                Assert.IsTrue((bool)allow.Invoke(resource, new object[] { first, 3 }));
                observe.Invoke(resource, new object[] { second, 3 });
                Assert.IsTrue((bool)allow.Invoke(resource, new object[] { first, 3 }));
                Assert.IsFalse((bool)allow.Invoke(resource, new object[] { second, 3 }), "A sort camera outside the renderer layer cannot supply its culled visibility.");
                second.cullingMask = 1 << 3;
                observe.Invoke(resource, new object[] { second, 3 });
                Assert.IsFalse((bool)allow.Invoke(resource, new object[] { first, 3 }));
                type.GetField("m_cpuMultiCameraFrame", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(resource, Time.frameCount - 2);
                Assert.IsTrue((bool)allow.Invoke(resource, new object[] { first, 3 }));
            }
            finally
            {
                resource?.Dispose();
                UnityEngine.Object.DestroyImmediate(a);
                UnityEngine.Object.DestroyImmediate(b);
            }
        }

        static void WaitForManagedJob(GsplatCpuSpatialSort spatial)
        {
            var field = typeof(GsplatCpuSpatialSort).GetField("m_task", BindingFlags.Instance | BindingFlags.NonPublic);
            var task = (System.Threading.Tasks.Task)field.GetValue(spatial);
            task?.GetAwaiter().GetResult();
            var hybrid = (GsplatCpuHybridSort)typeof(GsplatCpuSpatialSort)
                .GetField("m_hybrid", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(spatial);
            Assert.AreSame(hybrid.Result, spatial.Output, "Publication must reuse completed sorter storage.");
        }

        [Test]
        public void ManagedHybridSortOrdersGloballyByDepth()
        {
            var centers = new[] { new Vector3(0, 0, 5), new Vector3(0, 0, 10), new Vector3(0, 0, 15) };
            var localToView = Matrix4x4.Scale(new Vector3(1, 1, -1));
            var sorter = new GsplatCpuHybridSort(centers, 3);
            var selection = new uint[] { 2, 0, 1 };
            sorter.Sort(selection, selection.Length, 0u, localToView, CancellationToken.None);
            CollectionAssert.AreEqual(new uint[] { 2, 1, 0 }, sorter.Result.Take(3));
            sorter.Sort(null, centers.Length, 0u, localToView, CancellationToken.None);
            CollectionAssert.AreEqual(new uint[] { 2, 1, 0 }, sorter.Result.Take(3));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Assert.Throws<OperationCanceledException>(() => sorter.Sort(selection, selection.Length, 0u,
                localToView, canceled.Token));
        }

        [Test]
        public void ManagedHybridSortMakesNearBoundaryBucketExact()
        {
            // IDs 1 and 3 share one 18-bit bucket around depth 30; selection order is the
            // reverse of their depth order. The exact suffix must repair that bucket while
            // the far prefix remains globally ordered.
            var centers = new[]
            {
                new Vector3(0, 0, -10), new Vector3(0, 0, -30.02f),
                new Vector3(0, 0, -20), new Vector3(0, 0, -30)
            };
            var localToView = Matrix4x4.Scale(new Vector3(1, 1, -1));
            var sorter = new GsplatCpuHybridSort(centers, centers.Length);
            var selection = new uint[] { 1, 3, 0, 2 };
            uint nearKey = GsplatCpuDepth.DepthKey(26f);
            sorter.Sort(selection, selection.Length, nearKey, localToView, CancellationToken.None);
            CollectionAssert.AreEqual(new uint[] { 0, 2, 3, 1 }, sorter.Result.Take(selection.Length));
            Assert.AreEqual(2, sorter.NearCount);

            // UINT32_MAX means no exact suffix; the boundary bucket keeps selection order.
            sorter.Sort(selection, selection.Length, uint.MaxValue, localToView, CancellationToken.None);
            CollectionAssert.AreEqual(new uint[] { 0, 2, 1, 3 }, sorter.Result.Take(selection.Length));
            Assert.AreEqual(0, sorter.NearCount);
        }

        [TestCase("ascending")]
        [TestCase("descending")]
        [TestCase("organpipe")]
        [TestCase("random")]
        [TestCase("ties")]
        [TestCase("equal")]
        public void ManagedExactSortIsStableForLargeStructuredInputs(string pattern)
        {
            const int count = 100000;
            var random = new System.Random(177);
            var centers = new Vector3[count];
            for (int i = 0; i < count; ++i)
            {
                float depth = pattern switch
                {
                    "ascending" => i,
                    "descending" => count - i,
                    "organpipe" => Math.Min(i, count - 1 - i),
                    "random" => (float)random.NextDouble() * count,
                    "ties" => i % 32,
                    _ => 1
                };
                centers[i] = new Vector3(0, 0, -1 - depth);
            }
            var expected = Enumerable.Range(0, count)
                .OrderBy(i => GsplatCpuDepth.DepthKey(centers[i].z)).Select(i => (uint)i).ToArray();
            var sorter = new GsplatCpuHybridSort(centers, count);
            sorter.Sort(null, count, 0u, Matrix4x4.identity, CancellationToken.None);
            CollectionAssert.AreEqual(expected, sorter.Result);
            Assert.AreEqual(count, sorter.NearCount);
        }

        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        [TestCase(16, false)]
        [TestCase(16, true)]
        [TestCase(17, false)]
        [TestCase(17, true)]
        [TestCase(4097, false)]
        [TestCase(4097, true)]
        public void ManagedHybridSortPreservesInputTiesAcrossModesAndBufferReuse(int count, bool selected)
        {
            var random = new System.Random(177);
            var centers = Enumerable.Range(0, count * 2 + 3)
                .Select(_ => new Vector3(0, 0, -1 - random.Next(150))).ToArray();
            // Sparse, reverse source IDs distinguish stable input order from a source-ID tie break.
            var selection = selected ? Enumerable.Range(0, count).Select(i => (uint)((count - 1 - i) * 2)).ToArray() : null;
            var sorter = new GsplatCpuHybridSort(centers, count + 7);
            var output = sorter.Result;
            for (int repetition = 0; repetition < 2; ++repetition)
            foreach (uint nearKey in new[] { 0u, GsplatCpuDepth.DepthKey(-40), uint.MaxValue })
            {
                uint threshold = (nearKey >> 14) << 14;
                var input = selection ?? Enumerable.Range(0, count).Select(i => (uint)i).ToArray();
                var expected = input.OrderBy(id =>
                {
                    uint key = GsplatCpuDepth.DepthKey(centers[id].z);
                    return nearKey != uint.MaxValue && key >= threshold ? key : key & 0xffffc000u;
                }).ToArray();
                sorter.Sort(selection, count, nearKey, Matrix4x4.identity, CancellationToken.None);
                CollectionAssert.AreEqual(expected, sorter.Result.Take(count));
                Assert.AreSame(output, sorter.Result);
                Assert.AreEqual(input.Count(id => nearKey != uint.MaxValue &&
                    GsplatCpuDepth.DepthKey(centers[id].z) >= threshold), sorter.NearCount);
            }
        }

        [Test]
        public void ManagedHybridSortValidatesCountsAndCanReuseBuffersAfterCancellation()
        {
            var centers = Enumerable.Range(0, 37).Select(i => new Vector3(0, 0, -i - 1)).ToArray();
            var sorter = new GsplatCpuHybridSort(centers, 17);
            Assert.Throws<ArgumentOutOfRangeException>(() => sorter.Sort(null, -1, 0u,
                Matrix4x4.identity, CancellationToken.None));
            Assert.Throws<ArgumentOutOfRangeException>(() => sorter.Sort(null, 18, 0u,
                Matrix4x4.identity, CancellationToken.None));
            Assert.Throws<ArgumentOutOfRangeException>(() => sorter.Sort(new uint[2], 3, 0u,
                Matrix4x4.identity, CancellationToken.None));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => sorter.Sort(null, 17, 0u,
                Matrix4x4.identity, cancelled.Token));
            sorter.Sort(null, 17, 0u, Matrix4x4.identity, CancellationToken.None);
            CollectionAssert.AreEqual(Enumerable.Range(0, 17).Reverse().Select(i => (uint)i), sorter.Result);
            sorter.Sort(null, 0, 0u, Matrix4x4.identity, CancellationToken.None);
            Assert.AreEqual(0, sorter.NearCount);
            sorter.Sort(null, 3, 0u, Matrix4x4.identity, CancellationToken.None);
            CollectionAssert.AreEqual(new uint[] { 2, 1, 0 }, sorter.Result.Take(3));
        }

        [Test]
        public void NearExactKeyIsAFixedCameraSpaceDistance()
        {
            Assert.AreEqual(GsplatCpuDepth.DepthKey(-20f), GsplatCpuSpatialSort.NearKey(20f));
            Assert.AreEqual(0u, GsplatCpuSpatialSort.NearKey(0f));
            Assert.AreEqual(0u, GsplatCpuSpatialSort.NearKey(-1f));
            Assert.AreEqual(0u, GsplatCpuSpatialSort.NearKey(float.NaN));
            Assert.AreEqual(0u, GsplatCpuSpatialSort.NearKey(float.PositiveInfinity));
        }

        [Test]
        public void DisabledCullingSortsEveryPointRegardlessOfCamera()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            var cameraObject = new GameObject("CPU no-cull bucket test camera");
            GsplatCpuSpatialSort spatial = null;
            try
            {
                asset.SplatCount = 3;
                asset.Positions = new[] { new Vector3(0, 0, 5), new Vector3(0, 0, 10), new Vector3(0, 0, 15) };
                asset.Scales = new[] { Vector3.one * 0.01f, Vector3.one * 0.01f, Vector3.one * 0.01f };
                var camera = cameraObject.AddComponent<Camera>();
                camera.pixelRect = new Rect(0, 0, 512, 512);
                var settings = GsplatSettings.Instance;
                spatial = new GsplatCpuSpatialSort(asset.Positions, null, 3, asset, 4, 256, 10,
                    cullToFrustum: false);

                spatial.Update(camera, Matrix4x4.identity, 1, settings);
                WaitForManagedJob(spatial);
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.identity, 1, settings));
                Assert.IsTrue(spatial.Ready);
                Assert.AreEqual(3, spatial.VisibleCount);
                CollectionAssert.AreEqual(new uint[] { 2, 1, 0 }, spatial.Output.Take(3));

                Assert.IsFalse(spatial.Update(camera, Matrix4x4.identity, 1, settings));

                camera.transform.rotation = Quaternion.Euler(0, 180, 0);
                spatial.Update(camera, Matrix4x4.identity, 1, settings);
                WaitForManagedJob(spatial);
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.identity, 1, settings));
                Assert.AreEqual(3, spatial.VisibleCount, "Culling is disabled, so rotation must not remove points.");
                CollectionAssert.AreEquivalent(new uint[] { 0, 1, 2 }, spatial.Output.Take(3));
            }
            finally
            {
                spatial?.Dispose();
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void CullingAndHybridOrderPublishFarToNearWithoutASeam()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            var cameraObject = new GameObject("CPU bucket test camera");
            GsplatCpuSpatialSort spatial = null;
            try
            {
                asset.SplatCount = 3;
                asset.Positions = new[] { new Vector3(0, 0, 5), new Vector3(0, 0, 10), new Vector3(0, 0, 15) };
                asset.Scales = new[] { Vector3.one * 0.01f, Vector3.one * 0.01f, Vector3.one * 0.01f };
                var camera = cameraObject.AddComponent<Camera>();
                camera.pixelRect = new Rect(0, 0, 512, 512);
                var settings = GsplatSettings.Instance;
                spatial = new GsplatCpuSpatialSort(asset.Positions, null, 3, asset, 4, 256, 10);

                spatial.Update(camera, Matrix4x4.identity, 1, settings);
                WaitForManagedJob(spatial);
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.identity, 1, settings));
                Assert.IsTrue(spatial.Ready);
                Assert.AreEqual(3, spatial.VisibleCount);
                CollectionAssert.AreEqual(new uint[] { 2, 1, 0 }, spatial.Output.Take(3));

                Assert.IsFalse(spatial.Update(camera, Matrix4x4.identity, 1, settings));
                var taskField = typeof(GsplatCpuSpatialSort).GetField("m_task", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNull(taskField.GetValue(spatial));

                camera.transform.position = new Vector3(1, 0, 0);
                Assert.IsFalse(spatial.Update(camera, Matrix4x4.identity, 1, settings));
                Assert.AreEqual(3, spatial.VisibleCount, "The previous order must remain drawable while a job runs.");
                WaitForManagedJob(spatial);
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.identity, 1, settings));
                camera.transform.position = Vector3.zero;
                spatial.Update(camera, Matrix4x4.identity, 1, settings);
                WaitForManagedJob(spatial);
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.identity, 1, settings));

                camera.transform.rotation = Quaternion.Euler(0, 180, 0);
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.identity, 1, settings));
                Assert.AreEqual(0, spatial.VisibleCount);
                Assert.IsTrue(spatial.Ready);

                camera.transform.rotation = Quaternion.identity;
                spatial.Update(camera, Matrix4x4.identity, 1, settings);
                WaitForManagedJob(spatial);
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.identity, 1, settings));
                Assert.AreEqual(3, spatial.VisibleCount);
                CollectionAssert.AreEqual(new uint[] { 2, 1, 0 }, spatial.Output.Take(3));

                spatial.Update(camera, Matrix4x4.Translate(new Vector3(0, 0, 100)), 1, settings);
                WaitForManagedJob(spatial);
                Assert.IsTrue(spatial.Update(camera, Matrix4x4.Translate(new Vector3(0, 0, 100)), 1, settings));
                Assert.AreEqual(3, spatial.VisibleCount);
            }
            finally
            {
                spatial?.Dispose();
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }
    }
}
