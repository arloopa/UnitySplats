// SPDX-License-Identifier: MIT
// Stable view-depth radix sorting. Public ABI calls run on Unity's main thread.
// Inputs are borrowed, immutable and pinned until terminal completion or CancelAndWait returns.
// Workers allocate nothing and call no Unity/JS proxies, so draining them needs no main-thread proxy callbacks.
// Keep -ffp-contract=off in the plugin flags so depth keys match the managed multiply/add sequence.
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#if defined(__EMSCRIPTEN__)
#include <emscripten.h>
#define GS_EXPORT EMSCRIPTEN_KEEPALIVE
#else
#define GS_EXPORT
#endif
#if defined(__EMSCRIPTEN_PTHREADS__) || defined(GSPLAT_SORT_TEST_THREADS)
#include <pthread.h>
#include <stdatomic.h>
#define GS_MAX_WORKERS 8
#define GS_RADIX 256
enum {
    GS_IDLE = 0,
    GS_QUEUED = 1,
    GS_RUNNING = 2,
    GS_COMPLETE = 3,
    GS_CANCELLED = 4,
    GS_INVALID = 5
};
typedef struct gs_sort {
    uint32_t *keys[2], *ids[2];
    uint32_t capacity, count, center_count;
    const float *centers;
    const uint32_t *selection;
    float row[4];
    uint32_t histogram[GS_MAX_WORKERS][GS_RADIX];
    uint32_t offsets[GS_MAX_WORKERS][GS_RADIX];
    atomic_int status, cancel, invalid;
    int stop;
    struct gs_sort *next;
} gs_sort;
// One page-lifetime pool cooperates on one sort at a time; sessions queue in FIFO order.
// Session disposal frees its scratch, not this pool. Do not shut down workers per renderer.
static pthread_t gs_threads[GS_MAX_WORKERS];
static int gs_workers, gs_remaining;
static uint64_t gs_epoch;
static gs_sort *gs_active, *gs_head, *gs_tail;
static pthread_mutex_t gs_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t gs_work = PTHREAD_COND_INITIALIZER;
static pthread_cond_t gs_done = PTHREAD_COND_INITIALIZER;
static pthread_mutex_t gs_barrier_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t gs_barrier_cond = PTHREAD_COND_INITIALIZER;
static unsigned gs_arrivals, gs_phase;
#if defined(__EMSCRIPTEN__)
EM_JS(int, GsplatWebSort_AvailableWorkers, (void), {
    if (typeof SharedArrayBuffer === 'undefined' ||
        typeof HEAPU8 === 'undefined' || !(HEAPU8.buffer instanceof SharedArrayBuffer) ||
        (typeof crossOriginIsolated !== 'undefined' && !crossOriginIsolated)) return 0;
    // Use Unity/Emscripten's existing idle pool, not hardwareConcurrency. Creating an
    // unavailable worker and then blocking the main thread can stall pthread startup.
    return typeof PThread !== 'undefined' && PThread.unusedWorkers ? PThread.unusedWorkers.length : 0;
});
#else
static int GsplatWebSort_AvailableWorkers(void) { return GS_MAX_WORKERS; }
#endif
static void gs_barrier(void) {
    pthread_mutex_lock(&gs_barrier_mutex);
    unsigned phase = gs_phase;
    if (++gs_arrivals == (unsigned)gs_workers) {
        gs_arrivals = 0;
        ++gs_phase;
        pthread_cond_broadcast(&gs_barrier_cond);
    } else {
        while (phase == gs_phase) {
            pthread_cond_wait(&gs_barrier_cond, &gs_barrier_mutex);
        }
    }
    pthread_mutex_unlock(&gs_barrier_mutex);
}
static uint32_t gs_depth_key(float depth) {
    if (isnan(depth)) depth = INFINITY;
    uint32_t bits;
    memcpy(&bits, &depth, sizeof(bits));
    return bits ^ ((bits & 0x80000000u) ? UINT32_MAX : 0x80000000u);
}
static void gs_process(gs_sort *s, int worker) {
    uint32_t begin = (uint32_t)((uint64_t)s->count * worker / gs_workers);
    uint32_t end = (uint32_t)((uint64_t)s->count * (worker + 1) / gs_workers);
    for (uint32_t i = begin; i < end; ++i) {
        if ((i & 16383u) == 0 && atomic_load(&s->cancel)) break;
        uint32_t id = s->selection ? s->selection[i] : i;
        s->ids[0][i] = id;
        if (id >= s->center_count) {
            atomic_store(&s->invalid, 1);
            continue;
        }
        const float *p = s->centers + (size_t)id * 3;
        float depth = s->row[0] * p[0] + s->row[1] * p[1] + s->row[2] * p[2] + s->row[3];
        s->keys[0][i] = gs_depth_key(depth);
    }
    // Every participant must cross the same barriers, even for empty partitions or cancellation.
    // A worker returning independently from a radix pass would leave its peers blocked forever.
    gs_barrier();
    if (worker == 0) {
        s->stop = atomic_load(&s->cancel) || atomic_load(&s->invalid);
    }
    gs_barrier();
    if (s->stop) return;
    for (unsigned pass = 0; pass < 4; ++pass) {
        unsigned source = pass & 1, destination = source ^ 1, shift = pass * 8;
        uint32_t *hist = s->histogram[worker], *offset = s->offsets[worker];
        memset(hist, 0, GS_RADIX * sizeof(uint32_t));
        for (uint32_t i = begin; i < end; ++i) ++hist[(s->keys[source][i] >> shift) & 255u];
        gs_barrier();
        if (worker == 0) {
            uint32_t total = 0;
            // Prefix buckets first, then workers in source-partition order. This preserves
            // input order for equal keys; a global atomic scatter counter would not.
            for (unsigned bucket = 0; bucket < GS_RADIX; ++bucket)
                for (int w = 0; w < gs_workers; ++w) {
                    s->offsets[w][bucket] = total;
                    total += s->histogram[w][bucket];
                }
        }
        gs_barrier();
        // Input partitions and each partition's traversal order are stable.
        for (uint32_t i = begin; i < end; ++i) {
            uint32_t key = s->keys[source][i], index = offset[(key >> shift) & 255u]++;
            s->keys[destination][index] = key;
            s->ids[destination][index] = s->ids[source][i];
        }
        gs_barrier();
        if (worker == 0) {
            s->stop = atomic_load(&s->cancel);
        }
        gs_barrier();
        if (s->stop) return;
    }
}
// FIFO across resources prevents an always-dirty renderer starving others.
static void gs_dispatch(void) {
    if (gs_active || !gs_head) return;
    gs_active = gs_head;
    gs_head = gs_head->next;
    if (!gs_head) {
        gs_tail = NULL;
    }
    gs_active->next = NULL;
    gs_remaining = gs_workers;
    atomic_store(&gs_active->status, GS_RUNNING);
    ++gs_epoch;
    pthread_cond_broadcast(&gs_work);
}
static void *gs_worker(void *argument) {
    int worker = (int)(intptr_t)argument;
    uint64_t seen = 0;
    for (;;) {
        pthread_mutex_lock(&gs_mutex);
        while (gs_epoch == seen) {
            pthread_cond_wait(&gs_work, &gs_mutex);
        }
        seen = gs_epoch;
        gs_sort *s = gs_active;
        pthread_mutex_unlock(&gs_mutex);
        gs_process(s, worker);
        pthread_mutex_lock(&gs_mutex);
        if (--gs_remaining == 0) {
            int status = atomic_load(&s->cancel) ? GS_CANCELLED :
                atomic_load(&s->invalid) ? GS_INVALID : GS_COMPLETE;
            gs_active = NULL;
            // No worker accesses this context after completion is published.
            atomic_store_explicit(&s->status, status, memory_order_release);
            gs_dispatch();
            pthread_cond_broadcast(&gs_done);
        }
        pthread_mutex_unlock(&gs_mutex);
    }
    return NULL;
}
GS_EXPORT int GsplatWebSort_Initialize(int requested) {
    if (gs_workers) return gs_workers;
    int available = GsplatWebSort_AvailableWorkers();
    if (requested > available) requested = available;
    if (requested > GS_MAX_WORKERS) requested = GS_MAX_WORKERS;
    if (requested < 1) return 0;
    for (int i = 0; i < requested; ++i) {
        if (pthread_create(&gs_threads[i], NULL, gs_worker, (void *)(intptr_t)i) != 0) {
            break;
        }
        ++gs_workers;
    }
    return gs_workers;
}
// Queued work is removed; running work cancels at a shared phase boundary. No timeout may
// release the context or unpin its inputs while any worker can still access them.
GS_EXPORT void GsplatWebSort_CancelAndWait(gs_sort *s) {
    if (!s) return;
    pthread_mutex_lock(&gs_mutex);
    atomic_store(&s->cancel, 1);
    if (atomic_load(&s->status) == GS_QUEUED) {
        gs_sort *previous = NULL, *item = gs_head;
        while (item && item != s) {
            previous = item;
            item = item->next;
        }
        if (item) {
            if (previous) {
                previous->next = item->next;
            } else {
                gs_head = item->next;
            }
            if (gs_tail == item) {
                gs_tail = previous;
            }
            item->next = NULL;
        }
        atomic_store(&s->status, GS_CANCELLED);
    }
    while (gs_active == s) {
        pthread_cond_wait(&gs_done, &gs_mutex);
    }
    pthread_mutex_unlock(&gs_mutex);
}
GS_EXPORT void GsplatWebSort_Destroy(gs_sort *s) {
    if (!s) return;
    GsplatWebSort_CancelAndWait(s);
    for (int i = 0; i < 2; ++i) {
        free(s->keys[i]);
        free(s->ids[i]);
    }
    free(s);
}
GS_EXPORT gs_sort *GsplatWebSort_Create(uint32_t capacity) {
    if (!gs_workers || !capacity || capacity > SIZE_MAX / sizeof(uint32_t)) return NULL;
    gs_sort *s = calloc(1, sizeof(gs_sort));
    if (!s) return NULL;
    atomic_init(&s->status, GS_IDLE);
    atomic_init(&s->cancel, 0);
    atomic_init(&s->invalid, 0);
    s->capacity = capacity;
    for (int i = 0; i < 2; ++i) {
        s->keys[i] = malloc((size_t)capacity * sizeof(uint32_t));
        // Upload covers the full texture capacity, including unused padding slots.
        s->ids[i] = calloc(capacity, sizeof(uint32_t));
        if (!s->keys[i] || !s->ids[i]) {
            GsplatWebSort_Destroy(s);
            return NULL;
        }
    }
    return s;
}
GS_EXPORT int GsplatWebSort_Start(gs_sort *s, const float *centers, uint32_t center_count,
    const uint32_t *selection, uint32_t count, float m20, float m21, float m22, float m23) {
    if (!s || !centers || !count || count > s->capacity || (!selection && count > center_count)) return 0;
    pthread_mutex_lock(&gs_mutex);
    int state = atomic_load(&s->status);
    if (state == GS_QUEUED || state == GS_RUNNING) {
        pthread_mutex_unlock(&gs_mutex);
        return 0;
    }
    s->centers = centers;
    s->center_count = center_count;
    s->selection = selection;
    s->count = count;
    s->row[0] = m20;
    s->row[1] = m21;
    s->row[2] = m22;
    s->row[3] = m23;
    atomic_store(&s->cancel, 0);
    atomic_store(&s->invalid, 0);
    s->stop = 0;
    s->next = NULL;
    atomic_store(&s->status, GS_QUEUED);
    if (gs_tail) {
        gs_tail->next = s;
    } else {
        gs_head = s;
    }
    gs_tail = s;
    gs_dispatch();
    pthread_mutex_unlock(&gs_mutex);
    return 1;
}
GS_EXPORT int GsplatWebSort_Status(gs_sort *s) {
    return s ? atomic_load_explicit(&s->status, memory_order_acquire) : GS_INVALID;
}
GS_EXPORT uint32_t *GsplatWebSort_Output(gs_sort *s) { return s ? s->ids[0] : NULL; }
GS_EXPORT int GsplatWebSort_Seed(gs_sort *s, const uint32_t *selection, uint32_t count) {
    if (!s || count > s->capacity || GsplatWebSort_Status(s) == GS_RUNNING || GsplatWebSort_Status(s) == GS_QUEUED) return 0;
    for (uint32_t i = 0; i < count; ++i) s->ids[0][i] = selection ? selection[i] : i;
    return 1;
}
#else
// Non-threaded Unity builds link without forcing pthreads through plugin flags.
GS_EXPORT int GsplatWebSort_Initialize(int requested) { (void)requested; return 0; }
GS_EXPORT void *GsplatWebSort_Create(uint32_t capacity) { (void)capacity; return NULL; }
GS_EXPORT void GsplatWebSort_CancelAndWait(void *s) { (void)s; }
GS_EXPORT void GsplatWebSort_Destroy(void *s) { (void)s; }
GS_EXPORT int GsplatWebSort_Start(void *s, const float *p, uint32_t n, const uint32_t *ids,
    uint32_t count, float a, float b, float c, float d) {
    (void)s; (void)p; (void)n; (void)ids; (void)count; (void)a; (void)b; (void)c; (void)d; return 0;
}
GS_EXPORT int GsplatWebSort_Status(void *s) { (void)s; return 5; }
GS_EXPORT uint32_t *GsplatWebSort_Output(void *s) { (void)s; return NULL; }
GS_EXPORT int GsplatWebSort_Seed(void *s, const uint32_t *ids, uint32_t count) { (void)s; (void)ids; (void)count; return 0; }
#endif
GS_EXPORT int GsplatWebSort_ApiVersion(void) { return 1; }
