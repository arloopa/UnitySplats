// Copyright (c) 2026 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Gsplat.Formats;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat.Tests
{
    public class GsplatAsyncUploadTests
    {
        static FieldInfo PrivateField<T>(string name) => typeof(T).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic);

        sealed class FakeResource : GsplatResource
        {
            public int DisposeCount { get; private set; }

            public override void Dispose()
            {
                if (!BeginDispose())
                    return;
                DisposeCount++;
            }
        }

        sealed class FakeAsset : GsplatAsset
        {
            public readonly TaskCompletionSource<bool> Started = new();
            public readonly TaskCompletionSource<bool> Resume = new();
            public FakeResource LastResource { get; private set; }

            public override CompressionMode Compression => CompressionMode.Uncompressed;
            public override void Allocate() { }
            public override void LoadFromPly(string path, ProgressCallback progressCallback = null,
                SourceCoordinates sourceCoordinates = SourceCoordinates.RUF) { }
            public override void LoadFromDecoded(GsplatDecodedData data, ProgressCallback progressCallback = null) { }

            public override GsplatResource CreateResource() => LastResource = new FakeResource();

            protected override void _UploadData(GsplatResource resource)
            {
                resource.UploadedCount = 2;
            }

            protected override async Task _UploadDataAsync(GsplatResource resource,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                resource.UploadedCount = 1;
                Started.TrySetResult(true);
                await Resume.Task;
                cancellationToken.ThrowIfCancellationRequested();
                resource.UploadedCount = 2;
            }

            public override void SetupMaterialPropertyBlock(MaterialPropertyBlock propertyBlock,
                GsplatResource resource) { }
            public override void ComputeDepth(CommandBuffer cmd, Matrix4x4 matrixMv,
                ISorterResource sorterResource, GsplatResource resource) { }
            public override void InitOrder(ISorterResource sorterResource, GsplatResource resource,
                bool updateBounds) { }
        }

        [Test]
        public async Task LastSharedReleaseCancelsOwnedUploadBeforeDisposal()
        {
            var asset = ScriptableObject.CreateInstance<FakeAsset>();
            var releases = 0;
            try
            {
                var first = GsplatResourceManager.Get(asset);
                var second = GsplatResourceManager.Get(asset);
                Assert.AreSame(first, second, "Shared assets must share one resource and upload task.");

                var upload = asset.UploadDataAsync(first);
                Assert.AreSame(upload, first.UploadTask);
                Assert.AreSame(upload, asset.UploadDataAsync(first),
                    "Repeated async requests must return the resource-owned task.");
                await asset.Started.Task;
                Assert.AreEqual(1u, first.UploadedCount);

                GsplatResourceManager.Release(asset);
                releases++;
                Assert.IsFalse(first.Disposed, "The first shared release must not cancel or dispose the resource.");
                Assert.AreEqual(0, asset.LastResource.DisposeCount);

                GsplatResourceManager.Release(asset);
                releases++;
                Assert.IsTrue(first.Disposed, "The last release must mark the resource disposed before buffers release.");
                Assert.AreEqual(1, asset.LastResource.DisposeCount);

                asset.Resume.TrySetResult(true);
                try
                {
                    await upload;
                    Assert.Fail("The resource-owned upload task should be canceled by the last release.");
                }
                catch (OperationCanceledException)
                {
                    // Expected: the resume point observes cancellation before any more work.
                }
                Assert.AreEqual(1u, first.UploadedCount,
                    "Cancellation must not advance UploadedCount past the last complete batch.");

                first.Dispose();
                Assert.AreEqual(1, asset.LastResource.DisposeCount, "Resource disposal must be idempotent.");
            }
            finally
            {
                asset.Resume.TrySetResult(true);
                while (releases < 2)
                {
                    GsplatResourceManager.Release(asset);
                    releases++;
                }
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void ClearingRendererAssetReleasesCachedResource()
        {
            var asset = ScriptableObject.CreateInstance<FakeAsset>();
            var gameObject = new GameObject("GsplatAssetReleaseTest");
            try
            {
                var resource = GsplatResourceManager.Get(asset);
#pragma warning disable SYSLIB0050 // Test avoids allocating GraphicsBuffers in GsplatRendererImpl's constructor.
                var implementation = (GsplatRendererImpl)FormatterServices.GetUninitializedObject(
                    typeof(GsplatRendererImpl));
#pragma warning restore SYSLIB0050
                PrivateField<GsplatRendererImpl>("m_gsplatAsset").SetValue(implementation, asset);
                implementation.GsplatResource = resource;

                var renderer = gameObject.AddComponent<GsplatRenderer>();
                renderer.GsplatAsset = null;
                PrivateField<GsplatRenderer>("m_prevAsset").SetValue(renderer, asset);
                PrivateField<GsplatRenderer>("m_renderer").SetValue(renderer, implementation);

                renderer.Update();

                Assert.AreEqual(1, asset.LastResource.DisposeCount,
                    "Clearing GsplatAsset must release the resource cached for the previous asset.");
                Assert.IsNull(implementation.GsplatResource);
                Assert.IsNull(PrivateField<GsplatRenderer>("m_prevAsset").GetValue(renderer));
                Assert.IsNull(PrivateField<GsplatRenderer>("m_renderer").GetValue(renderer),
                    "Clearing GsplatAsset must also release the renderer's size-dependent GPU buffers.");
            }
            finally
            {
                var renderer = gameObject.GetComponent<GsplatRenderer>();
                if (renderer)
                    PrivateField<GsplatRenderer>("m_renderer").SetValue(renderer, null);
                if (asset.LastResource is { DisposeCount: 0 })
                    GsplatResourceManager.Release(asset);
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void DisposingGlobalBuffersInvalidatesMergedDrawCounts()
        {
            var renderer = new GsplatGlobalRenderer();
            PrivateField<GsplatGlobalRenderer>("m_totalSplatCount").SetValue(renderer, 23u);
            PrivateField<GsplatGlobalRenderer>("m_totalRemainingCount").SetValue(renderer, 17u);

            renderer.DisposeGlobalBuffers();

            Assert.AreEqual(0u,
                PrivateField<GsplatGlobalRenderer>("m_totalSplatCount").GetValue(renderer));
            Assert.AreEqual(0u,
                PrivateField<GsplatGlobalRenderer>("m_totalRemainingCount").GetValue(renderer),
                "A rebuilt order buffer must not inherit the previous merge's valid-entry count.");
        }

        [Test]
        public void ActiveRangesNormalizeToExactSortedUnionAndClearRestoresDenseMode()
        {
            var asset = ScriptableObject.CreateInstance<FakeAsset>();
            asset.SplatCount = 20;
            var gameObject = new GameObject("GsplatActiveRangeTest");
            try
            {
                var renderer = gameObject.AddComponent<GsplatRenderer>();
                renderer.GsplatAsset = asset;
                renderer.SetActiveRanges(new[]
                {
                    new GsplatActiveRange(15, 2),
                    new GsplatActiveRange(4, 3),
                    new GsplatActiveRange(2, 3), // overlaps/abuts [4, 7)
                    new GsplatActiveRange(10, 2),
                    new GsplatActiveRange(16, 2), // overlaps [15, 17)
                    new GsplatActiveRange(12, 0),
                });

                Assert.IsTrue(renderer.HasActiveRanges);
                Assert.AreEqual(10u, renderer.ActiveSplatCount);
                Assert.AreEqual(3, renderer.ActiveRanges.Count);
                Assert.AreEqual(new GsplatActiveRange(2, 5), renderer.ActiveRanges[0]);
                Assert.AreEqual(new GsplatActiveRange(10, 2), renderer.ActiveRanges[1]);
                Assert.AreEqual(new GsplatActiveRange(15, 3), renderer.ActiveRanges[2]);

                var ids = (uint[])PrivateField<GsplatRenderer>("m_activeSourceIds").GetValue(renderer);
                CollectionAssert.AreEqual(new uint[] { 2, 3, 4, 5, 6, 10, 11, 15, 16, 17 }, ids);

                renderer.SetActiveRanges(Array.Empty<GsplatActiveRange>());
                Assert.IsTrue(renderer.HasActiveRanges, "An empty explicit selection must differ from clear/full.");
                Assert.AreEqual(0u, renderer.ActiveSplatCount);

                renderer.ClearActiveRanges();
                Assert.IsFalse(renderer.HasActiveRanges);
                Assert.AreEqual(asset.SplatCount, renderer.ActiveSplatCount);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void ActiveRangesRejectOverflowAndOutOfBounds()
        {
            var asset = ScriptableObject.CreateInstance<FakeAsset>();
            asset.SplatCount = 20;
            var gameObject = new GameObject("GsplatActiveRangeValidationTest");
            try
            {
                var renderer = gameObject.AddComponent<GsplatRenderer>();
                renderer.GsplatAsset = asset;
                Assert.Throws<ArgumentOutOfRangeException>(() => renderer.SetActiveRanges(
                    new[] { new GsplatActiveRange(19, 2) }));
                Assert.Throws<ArgumentOutOfRangeException>(() => renderer.SetActiveRanges(
                    new[] { new GsplatActiveRange(21, 0) }));

                asset.SplatCount = uint.MaxValue;
                Assert.Throws<ArgumentOutOfRangeException>(() => renderer.SetActiveRanges(
                    new[] { new GsplatActiveRange(uint.MaxValue, 2) }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void ResidentActiveCountIntersectsUploadedPrefixAcrossGaps()
        {
            var method = typeof(GsplatRendererImpl).GetMethod("CountResidentSourceIds",
                BindingFlags.Static | BindingFlags.NonPublic);
            var ids = new uint[] { 2, 3, 8, 9, 15 };

            Assert.AreEqual(0u, method.Invoke(null, new object[] { ids, 2u }));
            Assert.AreEqual(2u, method.Invoke(null, new object[] { ids, 8u }));
            Assert.AreEqual(4u, method.Invoke(null, new object[] { ids, 15u }));
            Assert.AreEqual(5u, method.Invoke(null, new object[] { ids, 16u }));
        }

        [Test]
        public void CpuFallbackSortReadsAndEmitsActualSelectedSourceIds()
        {
            var sorterType = typeof(GsplatSorter);
            var requestType = sorterType.GetNestedType("CpuSortRequest", BindingFlags.NonPublic);
            var workspaceType = sorterType.GetNestedType("CpuSortWorkspace", BindingFlags.NonPublic);
            var requestConstructor = requestType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(Vector3[]), typeof(uint[]), typeof(int), typeof(int), typeof(Matrix4x4) }, null);
            var workspace = Activator.CreateInstance(workspaceType, true);
            workspaceType.GetMethod("EnsureCapacity").Invoke(workspace, new object[] { 3 });

            var centers = new Vector3[6];
            centers[1] = new Vector3(0, 0, 5);
            centers[3] = new Vector3(0, 0, -2);
            centers[5] = new Vector3(0, 0, 1);
            var selectedIds = new uint[] { 1, 3, 5 };
            var request = requestConstructor.Invoke(new object[]
            {
                centers, selectedIds, selectedIds.Length, 7, Matrix4x4.identity
            });

            sorterType.GetMethod("SortCpu", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
                new[] { request, workspace, CancellationToken.None });
            var order = (uint[])workspaceType.GetField("Order").GetValue(workspace);
            CollectionAssert.AreEqual(new uint[] { 3, 5, 1 }, new[] { order[0], order[1], order[2] });
        }

        [Test]
        public void CpuFallbackSortPreservesNearbyDepthOrderAcrossLargeRanges()
        {
            var sorterType = typeof(GsplatSorter);
            var requestType = sorterType.GetNestedType("CpuSortRequest", BindingFlags.NonPublic);
            var workspaceType = sorterType.GetNestedType("CpuSortWorkspace", BindingFlags.NonPublic);
            var requestConstructor = requestType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(Vector3[]), typeof(uint[]), typeof(int), typeof(int), typeof(Matrix4x4) }, null);
            var workspace = Activator.CreateInstance(workspaceType, true);
            workspaceType.GetMethod("EnsureCapacity").Invoke(workspace, new object[] { 4 });

            // The old 16-bit normalized sorter collapsed -11 and -10 into one bucket because
            // of the distant outlier, then incorrectly preserved their near-before-far PLY order.
            var centers = new[]
            {
                new Vector3(0, 0, -10),
                new Vector3(0, 0, -11),
                new Vector3(0, 0, -1_000_000_000),
                new Vector3(0, 0, -1)
            };
            var request = requestConstructor.Invoke(new object[]
            {
                centers, null, centers.Length, 8, Matrix4x4.identity
            });

            sorterType.GetMethod("SortCpu", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
                new[] { request, workspace, CancellationToken.None });
            var order = (uint[])workspaceType.GetField("Order").GetValue(workspace);
            CollectionAssert.AreEqual(new uint[] { 2, 1, 0, 3 },
                new[] { order[0], order[1], order[2], order[3] });
        }

        [Test]
        public void CpuFallbackSortIsStableForEqualViewDepths()
        {
            var sorterType = typeof(GsplatSorter);
            var requestType = sorterType.GetNestedType("CpuSortRequest", BindingFlags.NonPublic);
            var workspaceType = sorterType.GetNestedType("CpuSortWorkspace", BindingFlags.NonPublic);
            var requestConstructor = requestType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(Vector3[]), typeof(uint[]), typeof(int), typeof(int), typeof(Matrix4x4) }, null);
            var workspace = Activator.CreateInstance(workspaceType, true);
            workspaceType.GetMethod("EnsureCapacity").Invoke(workspace, new object[] { 5 });

            // Interleave three equal-depth splats with keys on either side. An unstable in-place
            // partition changes the equal group from 0,2,4 to 0,4,2 at this camera angle.
            var centers = new[]
            {
                new Vector3(0, 0, -10),
                new Vector3(0, 0, 100),
                new Vector3(0, 0, -10),
                new Vector3(0, 0, -100),
                new Vector3(0, 0, -10)
            };
            var request = requestConstructor.Invoke(new object[]
            {
                centers, null, centers.Length, 9, Matrix4x4.identity
            });

            sorterType.GetMethod("SortCpu", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
                new[] { request, workspace, CancellationToken.None });
            var order = (uint[])workspaceType.GetField("Order").GetValue(workspace);
            CollectionAssert.AreEqual(new uint[] { 3, 0, 2, 4, 1 },
                new[] { order[0], order[1], order[2], order[3], order[4] });
        }

        [Test]
        public void CpuFallbackDoesNotResortAnUnchangedView()
        {
            var settings = GsplatSettings.Instance;
            bool previousOptimization = settings.EnableCpuSortOptimization;
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            var texture = new Texture2D(2, 1, TextureFormat.RGBA32, false, true);
            ISorterResource resource = null;
            try
            {
                settings.EnableCpuSortOptimization = false;
                asset.SplatCount = 2;
                asset.Positions = new[] { Vector3.zero, Vector3.one };
                var type = typeof(GsplatSorter).GetNestedType("Resource", BindingFlags.NonPublic);
                resource = (ISorterResource)Activator.CreateInstance(type,
                    new object[] { 2u, null, false, texture });
                type.GetMethod("BindCpuCenters").Invoke(resource, new object[] { asset, asset.Positions });
                var queue = type.GetMethod("QueueCpuSort");
                var pending = type.GetField("m_hasPendingCpuRequest",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var view = Matrix4x4.Translate(new Vector3(0, 0, -5));
                var movedView = Matrix4x4.Translate(new Vector3(0, 0, -6));

                // Sort Mode "Always" requests a sort every frame; only the first one for a view
                // should be queued.
                queue.Invoke(resource, new object[] { view, 2u, false, true, null });
                Assert.IsTrue((bool)pending.GetValue(resource));
                pending.SetValue(resource, false);

                queue.Invoke(resource, new object[] { view, 2u, false, true, null });
                Assert.IsFalse((bool)pending.GetValue(resource), "An unchanged view must not resort.");

                queue.Invoke(resource, new object[] { movedView, 2u, false, true, null });
                Assert.IsTrue((bool)pending.GetValue(resource), "A moved camera must resort.");
                pending.SetValue(resource, false);

                queue.Invoke(resource, new object[] { movedView, 2u, true, true, null });
                Assert.IsTrue((bool)pending.GetValue(resource), "A forced sort must always queue.");
            }
            finally
            {
                resource?.Dispose();
                settings.EnableCpuSortOptimization = previousOptimization;
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void BuiltInNoDrawablePathClearsCommandsFromPreviousFrame()
        {
            GsplatSorter sorter = GsplatSorter.Instance;
            FieldInfo commandBufferField = PrivateField<GsplatSorter>("m_commandBuffer");
            var previous = (CommandBuffer)commandBufferField.GetValue(sorter);
            var commandBuffer = new CommandBuffer();
            var cameraObject = new GameObject("Gsplat command-buffer regression camera");
            try
            {
                commandBuffer.SetGlobalFloat("_GsplatStaleCommandRegression", 1f);
                Assert.That(commandBuffer.sizeInBytes, Is.GreaterThan(0));
                commandBufferField.SetValue(sorter, commandBuffer);

                var camera = cameraObject.AddComponent<Camera>();
                typeof(GsplatSorter).GetMethod("OnPreCullCamera",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(sorter, new object[] { camera });

                Assert.That(commandBuffer.sizeInBytes, Is.EqualTo(0),
                    "An empty/inactive frame must not retain dispatches referencing an unloaded LOD.");
            }
            finally
            {
                commandBufferField.SetValue(sorter, previous);
                commandBuffer.Dispose();
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }
    }
}
