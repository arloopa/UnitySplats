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
    assert(GsplatWebSort_Start(s, centers, count, selected ? ids : NULL, count, row[0], row[1], row[2], row[3]));
    wait_complete(s);
    assert(GsplatWebSort_Status(s) == GS_COMPLETE);
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
int main(int argc, char **argv) {
    int requested = argc > 1 ? atoi(argv[1]) : 4;
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
    printf("PASS: %d worker(s), 40 stable-order cases, FIFO, invalid IDs, 100 cancel/destroy cycles\n", gs_workers);
#if defined(__EMSCRIPTEN__)
    emscripten_force_exit(0);
#endif
    return 0;
}
