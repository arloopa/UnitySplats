// SPDX-License-Identifier: MIT
// Standalone algorithm/lifetime tests; Native~ is not imported by Unity.
#include <assert.h>
#include <stdio.h>
#include "../../Runtime/Plugins/WebGL/GsplatWebNativeSort.c"

typedef struct { uint32_t key, ordinal, id; } expected_item;
static int compare_expected(const void *a, const void *b) {
    const expected_item *x = a, *y = b;
    if (x->key != y->key) return x->key < y->key ? -1 : 1;
    return (x->ordinal > y->ordinal) - (x->ordinal < y->ordinal);
}
static void wait_complete(gs_sort *s) {
    pthread_mutex_lock(&gs_mutex);
    while (atomic_load(&s->status) == GS_QUEUED || atomic_load(&s->status) == GS_RUNNING)
        pthread_cond_wait(&gs_done, &gs_mutex);
    pthread_mutex_unlock(&gs_mutex);
}
static void test_order(uint32_t count, int selected, int tied, int transformed) {
    const float row[4] = {transformed ? 0.25f : 0, transformed ? -0.5f : 0, transformed ? -0.75f : 1, transformed ? 1.25f : 0};
    float *centers = calloc((size_t)count * 3, sizeof(float));
    uint32_t *ids = malloc((size_t)count * 4);
    expected_item *expected = malloc((size_t)count * sizeof(expected_item));
    assert(centers && ids && expected);
    for (uint32_t i = 0; i < count; ++i) {
        centers[(size_t)i * 3] = tied ? 0 : (float)((int)(i % 17) - 8);
        centers[(size_t)i * 3 + 1] = tied ? 0 : (float)((int)(i % 7) - 3);
        centers[(size_t)i * 3 + 2] = tied ? 1.0f : (float)((int)((i * 1664525u + 1013904223u) % 4096) - 2048) * 0.125f;
        ids[i] = count - 1 - i;
    }
    if (!tied && count >= 8) {
        centers[2] = NAN; centers[5] = INFINITY; centers[8] = -INFINITY;
        centers[11] = -0.0f; centers[14] = 0.0f;
    }
    for (uint32_t i = 0; i < count; ++i) {
        uint32_t id = selected ? ids[i] : i;
        const float *p = centers + (size_t)id * 3;
        float depth = row[0] * p[0] + row[1] * p[1] + row[2] * p[2] + row[3];
        expected[i] = (expected_item){gs_depth_key(depth), i, id};
    }
    qsort(expected, count, sizeof(expected_item), compare_expected);
    gs_sort *s = GsplatWebSort_Create(count + 7);
    assert(s && GsplatWebSort_Seed(s, selected ? ids : NULL, count));
    GsplatWebSort_SetTimingEnabled(s, selected);
    assert(GsplatWebSort_Start(s, centers, count, selected ? ids : NULL, count, row[0], row[1], row[2], row[3]));
    wait_complete(s);
    assert(GsplatWebSort_Status(s) == GS_COMPLETE);
    gs_timings timings;
    assert(GsplatWebSort_GetTimings(s, &timings) == selected);
    if (selected) {
        assert(isfinite(timings.queue_ms) && timings.queue_ms >= 0);
        assert(isfinite(timings.keys_ms) && timings.keys_ms >= 0);
        assert(isfinite(timings.radix_ms) && timings.radix_ms >= 0);
        assert(isfinite(timings.collect_ms) && timings.collect_ms >= 0);
    }
    for (uint32_t i = 0; i < count; ++i) assert(GsplatWebSort_Output(s)[i] == expected[i].id);
    GsplatWebSort_Destroy(s);
    free(expected); free(ids); free(centers);
}
static void test_queue_and_cancel(void) {
    const uint32_t count = 100003;
    float *centers = calloc((size_t)count * 3, sizeof(float));
    assert(centers);
    gs_sort *a = GsplatWebSort_Create(count), *b = GsplatWebSort_Create(count), *c = GsplatWebSort_Create(count);
    assert(a && b && c);
    assert(GsplatWebSort_Start(a, centers, count, NULL, count, 0, 0, 1, 0));
    assert(GsplatWebSort_Start(b, centers, count, NULL, count, 0, 0, 1, 0));
    assert(GsplatWebSort_Start(c, centers, count, NULL, count, 0, 0, 1, 0));
    GsplatWebSort_CancelAndWait(b);
    GsplatWebSort_Destroy(b);
    wait_complete(a); wait_complete(c);
    assert(GsplatWebSort_Status(a) == GS_COMPLETE && GsplatWebSort_Status(c) == GS_COMPLETE);
    GsplatWebSort_Destroy(a); GsplatWebSort_Destroy(c);
    for (int i = 0; i < 100; ++i) {
        a = GsplatWebSort_Create(count);
        assert(a && GsplatWebSort_Start(a, centers, count, NULL, count, 0, 0, 1, 0));
        GsplatWebSort_Destroy(a); // Queued/running cancellation must end all input/output accesses.
    }
    a = GsplatWebSort_Create(1);
    uint32_t invalid_id = count;
    assert(a && GsplatWebSort_Start(a, centers, count, &invalid_id, 1, 0, 0, 1, 0));
    wait_complete(a);
    assert(GsplatWebSort_Status(a) == GS_INVALID);
    GsplatWebSort_Destroy(a);
    free(centers);
}
static void test_buckets(int tied) {
    const uint32_t count = 100003;
    const float row[4] = {0.25f, -0.5f, -0.75f, 1.25f};
    float *centers = calloc((size_t)count * 3, sizeof(float));
    uint32_t *ids = malloc((size_t)count * sizeof(uint32_t));
    expected_item *expected = malloc((size_t)count * sizeof(expected_item));
    uint8_t *seen = calloc(count, 1);
    assert(centers && ids && expected && seen);
    for (uint32_t i = 0; i < count; ++i) {
        centers[(size_t)i * 3] = tied ? 0 : (float)((int)(i % 97) - 48);
        centers[(size_t)i * 3 + 1] = tied ? 0 : (float)((int)(i % 43) - 21);
        centers[(size_t)i * 3 + 2] = tied ? 0 : (float)((int)(i % 257) - 128);
        ids[i] = count - 1 - i;
        const float *p = centers + (size_t)ids[i] * 3;
        float z = row[0]*p[0] + row[1]*p[1] + row[2]*p[2] + row[3];
        expected[i] = (expected_item){gs_depth_key(z), i, ids[i]};
    }
    // A fixed camera-space cutoff, expressed as a full view-depth key. The fixture spans
    // both sides of this value so the prefix and suffix are non-empty.
    uint32_t near_key = tied ? 0 : gs_depth_key(1.25f);
    gs_sort *s = GsplatWebSort_Create(count + 7);
    assert(s);
    GsplatWebSort_SetTimingEnabled(s, 1);
    assert(GsplatWebSort_StartHybrid(s, centers, count, ids, count, row[0], row[1], row[2], row[3], near_key));
    wait_complete(s);
    assert(GsplatWebSort_Status(s) == GS_COMPLETE);
    gs_timings timings;
    assert(GsplatWebSort_GetTimings(s, &timings));
    assert(isfinite(timings.keys_ms) && timings.keys_ms >= 0);
    assert(isfinite(timings.radix_ms) && timings.radix_ms >= 0);
    const uint32_t *out = GsplatWebSort_Output(s);
    assert(out);
    uint32_t previous = 0, previous_full = 0;
    const uint32_t near_start = s->near_start;
    assert(tied ? near_start == 0 : (near_start > 0 && near_start < count));
    assert(GsplatWebSort_NearStart(s) == near_start);
    for (uint32_t i = 0; i < count; ++i) {
        uint32_t id = out[i];
        assert(id < count && !seen[id]);
        seen[id] = 1;
        const float *p = centers + (size_t)id * 3;
        float z = row[0]*p[0] + row[1]*p[1] + row[2]*p[2] + row[3];
        uint32_t key = gs_depth_key(z);
        uint32_t bucket = key >> (32 - GS_BUCKET_BITS);
        assert(i == 0 || bucket >= previous);
        previous = bucket;
        if (i >= near_start) {
            assert(i == near_start || key >= previous_full);
            previous_full = key;
        }
    }
    // A null selection means the identity order, matching the no-culling C# path.
    memset(seen, 0, count);
    assert(GsplatWebSort_StartHybrid(s, centers, count, NULL, count, row[0], row[1], row[2], row[3], near_key));
    wait_complete(s);
    assert(GsplatWebSort_Status(s) == GS_COMPLETE);
    out = GsplatWebSort_Output(s);
    previous = 0;
    previous_full = 0;
    for (uint32_t i = 0; i < count; ++i) {
        uint32_t id = out[i];
        assert(id < count && !seen[id]);
        seen[id] = 1;
        const float *p = centers + (size_t)id * 3;
        float z = row[0]*p[0] + row[1]*p[1] + row[2]*p[2] + row[3];
        uint32_t key = gs_depth_key(z);
        uint32_t bucket = key >> (32 - GS_BUCKET_BITS);
        assert(i == 0 || bucket >= previous);
        previous = bucket;
        if (i >= near_start) {
            assert(i == near_start || key >= previous_full);
            previous_full = key;
        }
    }
    // A full sort on the same context must return the exact stable order from slot 0.
    for (uint32_t i = 0; i < count; ++i) {
        const float *p = centers + (size_t)ids[i] * 3;
        expected[i] = (expected_item){gs_depth_key(p[2]), i, ids[i]};
    }
    qsort(expected, count, sizeof(*expected), compare_expected);
    assert(GsplatWebSort_Start(s, centers, count, ids, count, 0, 0, 1, 0));
    wait_complete(s);
    assert(GsplatWebSort_Status(s) == GS_COMPLETE);
    assert(GsplatWebSort_NearStart(s) == 0);
    for (uint32_t i = 0; i < count; ++i) assert(GsplatWebSort_Output(s)[i] == expected[i].id);
    // UINT32_MAX means no exact suffix; output stays in global bucket order.
    memset(seen, 0, count);
    assert(GsplatWebSort_StartHybrid(s, centers, count, ids, count, row[0], row[1], row[2], row[3], UINT32_MAX));
    wait_complete(s);
    assert(GsplatWebSort_Status(s) == GS_COMPLETE);
    assert(s->near_start == count);
    assert(GsplatWebSort_NearStart(s) == count);
    out = GsplatWebSort_Output(s);
    previous = 0;
    for (uint32_t i = 0; i < count; ++i) {
        uint32_t id = out[i];
        assert(id < count && !seen[id]);
        seen[id] = 1;
        const float *p = centers + (size_t)id * 3;
        float z = row[0]*p[0] + row[1]*p[1] + row[2]*p[2] + row[3];
        uint32_t bucket = gs_depth_key(z) >> (32 - GS_BUCKET_BITS);
        assert(i == 0 || bucket >= previous);
        previous = bucket;
    }
    ids[0] = count;
    assert(GsplatWebSort_StartHybrid(s, centers, count, ids, 1, 0, 0, 1, 0, near_key));
    wait_complete(s);
    assert(GsplatWebSort_Status(s) == GS_INVALID);
    ids[0] = 0;
    GsplatWebSort_Destroy(s);
    for (int i = 0; i < 20; ++i) {
        s = GsplatWebSort_Create(count);
        assert(s && GsplatWebSort_StartHybrid(s, centers, count, ids, count, 0, 0, 1, 0, near_key));
        GsplatWebSort_Destroy(s);
    }
    free(seen); free(expected); free(ids); free(centers);
}

static void test_near_suffix_count(void) {
    const uint32_t count = 1000000;
    float *centers = calloc((size_t)count * 3, sizeof(float));
    gs_sort *s = GsplatWebSort_Create(count);
    assert(centers && s);
    for (uint32_t i = 0; i < count; ++i)
        centers[(size_t)i * 3 + 2] = i % 10 == 0 ? -10.0f : -1000.0f;
    assert(GsplatWebSort_StartHybrid(s, centers, count, NULL, count, 0, 0, 1, 0, gs_depth_key(-40.0f)));
    wait_complete(s);
    assert(GsplatWebSort_Status(s) == GS_COMPLETE);
    uint32_t near_start = GsplatWebSort_NearStart(s);
    assert(near_start == 900000 && count - near_start == 100000);
    const uint32_t *out = GsplatWebSort_Output(s);
    for (uint32_t i = 0; i < near_start; ++i) assert(out[i] == i + i / 9 + 1);
    for (uint32_t i = near_start; i < count; ++i) assert(out[i] == (i - near_start) * 10);
    assert(GsplatWebSort_StartHybrid(s, centers, count, NULL, count, 0, 0, 1, 0, 0));
    wait_complete(s);
    assert(GsplatWebSort_Status(s) == GS_COMPLETE && GsplatWebSort_NearStart(s) == 0);
    assert(GsplatWebSort_StartHybrid(s, centers, count, NULL, count, 0, 0, 1, 0, UINT32_MAX));
    wait_complete(s);
    assert(GsplatWebSort_Status(s) == GS_COMPLETE && GsplatWebSort_NearStart(s) == count);
    GsplatWebSort_Destroy(s);
    free(centers);
}

// Reuse dirty scratch across both output slots, with sparse IDs unrelated to input order.
// Compare the entire stable result, including the zero/singleton exact-suffix cases.
static void test_hybrid_reuse(uint32_t count, int selected) {
    const uint32_t center_count = count * 2 + 3;
    float *centers = calloc((size_t)center_count * 3, sizeof(float));
    uint32_t *selection = malloc((size_t)count * sizeof(uint32_t));
    expected_item *expected = malloc((size_t)count * sizeof(expected_item));
    gs_sort *s = GsplatWebSort_Create(count + 7);
    assert(centers && selection && expected && s);
    for (uint32_t i = 0; i < count; ++i) selection[i] = 2 * (count - 1 - i) + 1;
    for (unsigned mode = 0; mode < 8; ++mode) {
        int full = mode == 1 || mode == 5;
        uint32_t near_key = mode == 2 ? 0 : mode == 3 ? UINT32_MAX : gs_depth_key(-40.0f);
        const uint32_t threshold = (near_key >> (32 - GS_BUCKET_BITS)) << (32 - GS_BUCKET_BITS);
        for (uint32_t i = 0; i < center_count; ++i) {
            uint32_t mixed = i * 1664525u + 1013904223u;
            centers[(size_t)i * 3 + 2] = mode == 4 ? -1000.0f :
                (float)((int)(mixed % 8192) - 4096) * 0.125f;
        }
        if (mode == 4) {
            uint32_t id = selected ? selection[count / 2] : count / 2;
            centers[(size_t)id * 3 + 2] = -10.0f;
        } else if (count >= 7) {
            const float edge[] = {-40.00002f, -40.0f, -39.99998f, -0.0f, 0.0f, NAN, -INFINITY};
            for (uint32_t i = 0; i < 7; ++i) {
                uint32_t id = selected ? selection[i] : i;
                centers[(size_t)id * 3 + 2] = edge[i];
            }
        }
        uint32_t far_count = 0;
        for (uint32_t i = 0; i < count; ++i) {
            uint32_t id = selected ? selection[i] : i;
            uint32_t key = gs_depth_key(centers[(size_t)id * 3 + 2]);
            int near = full || (near_key != UINT32_MAX && key >= threshold);
            if (!near) ++far_count;
            expected[i] = (expected_item){near ? key : (key >> (32 - GS_BUCKET_BITS)) << (32 - GS_BUCKET_BITS), i, id};
        }
        qsort(expected, count, sizeof(*expected), compare_expected);
        for (int slot = 0; slot < 2; ++slot) {
            memset(s->keys[slot], 0xa5, (size_t)s->capacity * sizeof(uint32_t));
            memset(s->ids[slot], 0xff, (size_t)s->capacity * sizeof(uint32_t));
        }
        int started = full ? GsplatWebSort_Start(s, centers, center_count,
            selected ? selection : NULL, count, 0, 0, 1, 0) :
            GsplatWebSort_StartHybrid(s, centers, center_count,
                selected ? selection : NULL, count, 0, 0, 1, 0, near_key);
        assert(started);
        wait_complete(s);
        assert(GsplatWebSort_Status(s) == GS_COMPLETE);
        assert(GsplatWebSort_NearStart(s) == far_count);
        if (mode == 4) assert(far_count == count - 1);
        const uint32_t *out = GsplatWebSort_Output(s);
        for (uint32_t i = 0; i < count; ++i) assert(out[i] == expected[i].id);
    }
    GsplatWebSort_Destroy(s);
    free(expected); free(selection); free(centers);
}

int main(int argc, char **argv) {
    int requested = argc > 1 ? atoi(argv[1]) : 4;
    assert(GsplatWebSort_ApiVersion() == 2);
    assert(sizeof(gs_timings) == 4 * sizeof(double));
    assert(GsplatWebSort_Initialize(requested) > 0);
    assert(gs_depth_key(-INFINITY) < gs_depth_key(-1));
    assert(gs_depth_key(-0.0f) < gs_depth_key(0.0f));
    assert(gs_depth_key(NAN) == gs_depth_key(INFINITY));
    const uint32_t sizes[] = {1, 3, 257, 65537, 100003};
    for (unsigned i = 0; i < sizeof(sizes) / sizeof(sizes[0]); ++i)
        for (int selected = 0; selected < 2; ++selected)
            for (int tied = 0; tied < 2; ++tied)
                for (int transformed = 0; transformed < 2; ++transformed) test_order(sizes[i], selected, tied, transformed);
    test_queue_and_cancel();
    test_buckets(0);
    test_buckets(1);
    test_near_suffix_count();
    const uint32_t reuse_sizes[] = {1, 3, 257, 65537};
    for (unsigned i = 0; i < sizeof(reuse_sizes) / sizeof(reuse_sizes[0]); ++i)
        for (int selected = 0; selected < 2; ++selected) test_hybrid_reuse(reuse_sizes[i], selected);
    printf("PASS: %d worker(s), 40 full stable-order cases, 64 hybrid/full scratch-reuse cases (dense/sparse, 0/1/all/mixed near), million-point 10%% near count, optional timings, FIFO, invalid IDs, fixed-distance near-exact/far-bucket hybrid order, mode transitions\n", gs_workers);
#if defined(__EMSCRIPTEN__)
    emscripten_force_exit(0);
#endif
    return 0;
}
