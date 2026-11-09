// SPDX-License-Identifier: MIT
// CPU sorting: stable global view-depth radix or a near-exact/far-bucket hybrid.
// Public ABI calls run on Unity's main thread.
// Inputs are borrowed, immutable and pinned until terminal completion or CancelAndWait returns.
// Workers allocate nothing and call no Unity/JS proxies, so draining them needs no main-thread proxy callbacks.
// Keep -ffp-contract=off in the plugin flags so depth keys match the managed multiply/add sequence.
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#define GS_NATIVE_API_VERSION 2
#if defined(__EMSCRIPTEN__)
#include <emscripten.h>
#define GS_EXPORT EMSCRIPTEN_KEEPALIVE
#else
#include <time.h>
#define GS_EXPORT
#endif
typedef struct gs_timings {
    double queue_ms, keys_ms, radix_ms, collect_ms;
} gs_timings;
#if defined(__EMSCRIPTEN_PTHREADS__) || defined(GSPLAT_SORT_TEST_THREADS)
#include <pthread.h>
#include <stdatomic.h>
#define GS_MAX_WORKERS 8
#define GS_RADIX 256
#define GS_BUCKET_BITS 18
#define GS_BUCKETS (1u << GS_BUCKET_BITS)
// Hybrid policy: points nearer than the caller-supplied camera-space cutoff are sorted
// with the full stable 32-bit radix; farther points keep the 18-bit bucket order. The
// cutoff is an absolute distance, not a fraction of the visible depth span or a point
// count. The boundary bucket joins the exact suffix so the two ranges concatenate without
// an order seam.
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
    uint32_t *bucket_hist[GS_MAX_WORKERS];
    uint32_t near_key, near_start;
    uint32_t near_count[GS_MAX_WORKERS], near_offsets[GS_MAX_WORKERS];
    atomic_int status, cancel, invalid;
    int stop, timing_enabled, hybrid_mode;
    double queued_at, started_at, keys_done_at, completed_at;
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
static double gs_now(void) {
#if defined(__EMSCRIPTEN__)
    // Emscripten synchronizes this clock across pthread time origins; no main-thread proxy.
    return emscripten_get_now();
#else
    struct timespec now;
    timespec_get(&now, TIME_UTC);
    return now.tv_sec * 1000.0 + now.tv_nsec / 1000000.0;
#endif
}
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
// Global view-depth buckets, with everything nearer than the caller's fixed camera-space
// cutoff made exact. A far point is never nearer than a near point, so the bucket-scattered
// far prefix and exact near suffix can be concatenated without an order seam. Points in the
// boundary bucket are all moved into the exact suffix; the suffix is then globally correct
// even though its bucket start is not a point boundary.
//
// Only far points enter the 18-bit histogram and scatter. Near points are packed directly
// into the suffix with the full key from the key pass, so the near range costs no bucket
// counter updates, no random bucket writes, and no second center/row read.
static void gs_process_buckets(gs_sort *s, int worker) {
    const unsigned shift = 32 - GS_BUCKET_BITS;
    const int near_enabled = s->near_key != UINT32_MAX;
    const uint32_t near_bucket = near_enabled ? (s->near_key >> shift) : GS_BUCKETS;
    // The boundary bucket joins the exact suffix, so the first exact key is the bucket start.
    const uint32_t near_threshold = near_enabled ? (near_bucket << shift) : UINT32_MAX;
    uint32_t begin = (uint32_t)((uint64_t)s->count * worker / gs_workers);
    uint32_t end = (uint32_t)((uint64_t)s->count * (worker + 1) / gs_workers);
    // After the histogram barrier, counts become per-worker scatter cursors in place.
    uint32_t *hist = s->bucket_hist[worker], *offset = hist;
    memset(hist, 0, GS_BUCKETS * sizeof(uint32_t));
    s->near_count[worker] = 0;
    for (uint32_t i = begin; i < end; ++i) {
        if ((i & 16383u) == 0 && atomic_load(&s->cancel)) break;
        uint32_t id = s->selection ? s->selection[i] : i;
        uint32_t key;
        if (id >= s->center_count) {
            key = 0;
            atomic_store(&s->invalid, 1);
        } else {
            const float *p = s->centers + (size_t)id * 3;
            float depth = s->row[0] * p[0] + s->row[1] * p[1] + s->row[2] * p[2] + s->row[3];
            key = gs_depth_key(depth);
        }
        s->keys[0][i] = key;
        if (near_enabled && key >= near_threshold) {
            ++s->near_count[worker];
        } else {
            // Keep the histogram index valid even though an invalid job will not publish.
            ++hist[key >> shift];
        }
    }
    gs_barrier();
    if (worker == 0) {
        s->stop = atomic_load(&s->cancel) || atomic_load(&s->invalid);
        if (!s->stop) {
            // Far buckets start at zero; the near suffix begins after the far count. Near
            // points keep their per-worker input order until the exact radix rewrites it.
            uint32_t total = 0;
            for (unsigned bucket = 0; bucket < GS_BUCKETS; ++bucket) {
                for (int w = 0; w < gs_workers; ++w) {
                    uint32_t amount = s->bucket_hist[w][bucket];
                    s->bucket_hist[w][bucket] = total;
                    total += amount;
                }
            }
            s->near_start = total;
            for (int w = 0; w < gs_workers; ++w) {
                s->near_offsets[w] = total;
                total += s->near_count[w];
            }
            if (s->timing_enabled) s->keys_done_at = gs_now();
            s->stop = atomic_load(&s->cancel);
        }
    }
    gs_barrier();
    if (s->stop) return;
    uint32_t near_cursor = s->near_offsets[worker];
    for (uint32_t i = begin; i < end; ++i) {
        uint32_t key = s->keys[0][i];
        // The immutable input already supplies the ID; no initial ids[0] copy is needed.
        uint32_t id = s->selection ? s->selection[i] : i;
        if (near_enabled && key >= near_threshold) {
            uint32_t index = near_cursor++;
            s->ids[1][index] = id;
            s->keys[1][index] = key;
        } else {
            uint32_t index = offset[key >> shift]++;
            s->ids[1][index] = id;
        }
    }
    gs_barrier();
    // Zero or one packed near point is already sorted; all workers take this branch.
    if (s->count - s->near_start <= 1) return;

    // The suffix already holds its full keys in keys[1] at the final positions, so the
    // radix starts from keys[1]. Source slot alternation keeps the result in ids[1].
    uint32_t exact_begin = s->near_start + (uint32_t)((uint64_t)(s->count - s->near_start) * worker / gs_workers);
    uint32_t exact_end = s->near_start + (uint32_t)((uint64_t)(s->count - s->near_start) * (worker + 1) / gs_workers);
    for (unsigned pass = 0; pass < 4; ++pass) {
        unsigned key_source = (pass + 1) & 1, key_destination = key_source ^ 1, byte_shift = pass * 8;
        // pass 0: ids[1] -> ids[0], pass 1: ids[0] -> ids[1], ...
        uint32_t *source_ids = (pass & 1) ? s->ids[0] : s->ids[1];
        uint32_t *destination_ids = (pass & 1) ? s->ids[1] : s->ids[0];
        uint32_t *pass_hist = s->histogram[worker], *pass_offset = s->offsets[worker];
        memset(pass_hist, 0, GS_RADIX * sizeof(uint32_t));
        for (uint32_t i = exact_begin; i < exact_end; ++i)
            ++pass_hist[(s->keys[key_source][i] >> byte_shift) & 255u];
        gs_barrier();
        if (worker == 0) {
            uint32_t total = s->near_start;
            for (unsigned bucket = 0; bucket < GS_RADIX; ++bucket)
                for (int w = 0; w < gs_workers; ++w) {
                    s->offsets[w][bucket] = total;
                    total += s->histogram[w][bucket];
                }
        }
        gs_barrier();
        // Only IDs are consumed after the last pass; the next job regenerates keys.
        if (pass == 3) {
            for (uint32_t i = exact_begin; i < exact_end; ++i) {
                uint32_t key = s->keys[key_source][i], index = pass_offset[(key >> byte_shift) & 255u]++;
                destination_ids[index] = source_ids[i];
            }
        } else {
            for (uint32_t i = exact_begin; i < exact_end; ++i) {
                uint32_t key = s->keys[key_source][i], index = pass_offset[(key >> byte_shift) & 255u]++;
                s->keys[key_destination][index] = key;
                destination_ids[index] = source_ids[i];
            }
        }
        gs_barrier();
        if (worker == 0) s->stop = atomic_load(&s->cancel);
        gs_barrier();
        if (s->stop) return;
    }
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
        if (s->timing_enabled) s->keys_done_at = gs_now();
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
        if (pass == 3) {
            // The published output contains IDs only, not the final sorted keys.
            for (uint32_t i = begin; i < end; ++i) {
                uint32_t key = s->keys[source][i], index = offset[(key >> shift) & 255u]++;
                s->ids[destination][index] = s->ids[source][i];
            }
        } else {
            for (uint32_t i = begin; i < end; ++i) {
                uint32_t key = s->keys[source][i], index = offset[(key >> shift) & 255u]++;
                s->keys[destination][index] = key;
                s->ids[destination][index] = s->ids[source][i];
            }
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
    if (gs_active->timing_enabled) {
        gs_active->started_at = gs_now();
    }
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
        if (s->hybrid_mode) gs_process_buckets(s, worker);
        else gs_process(s, worker);
        pthread_mutex_lock(&gs_mutex);
        if (--gs_remaining == 0) {
            int status = atomic_load(&s->cancel) ? GS_CANCELLED :
                atomic_load(&s->invalid) ? GS_INVALID : GS_COMPLETE;
            if (s->timing_enabled) s->completed_at = gs_now();
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
// Queued work is removed; running work cancels at cooperative phase boundaries. No timeout may
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
    for (int i = 0; i < GS_MAX_WORKERS; ++i) {
        free(s->bucket_hist[i]);
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
    s->hybrid_mode = 0;
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
    if (s->timing_enabled) s->queued_at = gs_now();
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

// near_key is a full view-depth key; UINT32_MAX means no exact near suffix. The caller
// derives it from a fixed camera-space distance, so this entry point has no scene-span or
// point-count policy of its own.
GS_EXPORT int GsplatWebSort_StartHybrid(gs_sort *s, const float *centers, uint32_t center_count,
    const uint32_t *selection, uint32_t count, float m20, float m21, float m22, float m23,
    uint32_t near_key) {
    if (!s || !centers || !count || count > s->capacity || (!selection && count > center_count)) return 0;
    pthread_mutex_lock(&gs_mutex);
    int state = atomic_load(&s->status);
    if (state == GS_QUEUED || state == GS_RUNNING) {
        pthread_mutex_unlock(&gs_mutex);
        return 0;
    }
    // Per-worker bucket histograms are allocated once per session; partial allocation stays
    // for Destroy to free if a later allocation fails.
    for (int w = 0; w < gs_workers; ++w) {
        if (!s->bucket_hist[w]) s->bucket_hist[w] = malloc(GS_BUCKETS * sizeof(uint32_t));
        if (!s->bucket_hist[w]) {
            pthread_mutex_unlock(&gs_mutex);
            return 0;
        }
    }
    s->hybrid_mode = 1;
    s->centers = centers;
    s->center_count = center_count;
    s->selection = selection;
    s->count = count;
    s->row[0] = m20;
    s->row[1] = m21;
    s->row[2] = m22;
    s->row[3] = m23;
    s->near_key = near_key;
    atomic_store(&s->cancel, 0);
    atomic_store(&s->invalid, 0);
    s->stop = 0;
    s->next = NULL;
    if (s->timing_enabled) s->queued_at = gs_now();
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
GS_EXPORT void GsplatWebSort_SetTimingEnabled(gs_sort *s, int enabled) {
    if (!s) return;
    int state = GsplatWebSort_Status(s);
    if (state == GS_QUEUED || state == GS_RUNNING) return;
    s->timing_enabled = enabled;
}
GS_EXPORT int GsplatWebSort_GetTimings(gs_sort *s, gs_timings *timings) {
    if (!s || !timings || GsplatWebSort_Status(s) != GS_COMPLETE || !s->timing_enabled) return 0;
    timings->queue_ms = s->started_at - s->queued_at;
    timings->keys_ms = s->keys_done_at - s->started_at;
    timings->radix_ms = s->completed_at - s->keys_done_at;
    timings->collect_ms = gs_now() - s->completed_at;
    return 1;
}
GS_EXPORT uint32_t *GsplatWebSort_Output(gs_sort *s) { return s ? s->ids[s->hybrid_mode ? 1 : 0] : NULL; }
// Start index of the exact suffix (the far-prefix count), NOT its size.
// Full exact jobs report zero, not a stale start from an earlier hybrid job.
// The exact-suffix size is the job count minus this index.
GS_EXPORT uint32_t GsplatWebSort_NearStart(gs_sort *s) {
    return s && s->hybrid_mode ? s->near_start : 0;
}
GS_EXPORT int GsplatWebSort_Seed(gs_sort *s, const uint32_t *selection, uint32_t count) {
    if (!s || count > s->capacity || GsplatWebSort_Status(s) == GS_RUNNING || GsplatWebSort_Status(s) == GS_QUEUED) return 0;
    for (uint32_t i = 0; i < count; ++i) s->ids[0][i] = selection ? selection[i] : i;
    // The seeded identity order lives in ids[0]; make GsplatWebSort_Output expose it even
    // after a previous hybrid job left the output selector on ids[1].
    s->hybrid_mode = 0;
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
GS_EXPORT int GsplatWebSort_StartHybrid(void *s, const float *p, uint32_t n,
    const uint32_t *ids, uint32_t count, float a, float b, float c, float d, uint32_t near_key) {
    (void)s; (void)p; (void)n; (void)ids; (void)count;
    (void)a; (void)b; (void)c; (void)d; (void)near_key; return 0;
}
GS_EXPORT int GsplatWebSort_Status(void *s) { (void)s; return 5; }
GS_EXPORT void GsplatWebSort_SetTimingEnabled(void *s, int enabled) { (void)s; (void)enabled; }
GS_EXPORT int GsplatWebSort_GetTimings(void *s, gs_timings *timings) { (void)s; (void)timings; return 0; }
GS_EXPORT uint32_t *GsplatWebSort_Output(void *s) { (void)s; return NULL; }
GS_EXPORT uint32_t GsplatWebSort_NearStart(void *s) { (void)s; return 0; }
GS_EXPORT int GsplatWebSort_Seed(void *s, const uint32_t *ids, uint32_t count) { (void)s; (void)ids; (void)count; return 0; }
#endif
GS_EXPORT int GsplatWebSort_ApiVersion(void) { return GS_NATIVE_API_VERSION; }
