// SPDX-License-Identifier: MIT
// Compile without -pthread: optional native entry points must link and remain inert.
#include <assert.h>
#include <stdio.h>
#include "../../Runtime/Plugins/WebGL/GsplatWebNativeSort.c"
int main(void) {
    gs_timings timings = {0};
    assert(GsplatWebSort_ApiVersion() == 2);
    assert(GsplatWebSort_Initialize(4) == 0);
    assert(GsplatWebSort_Create(12) == NULL);
    assert(GsplatWebSort_StartHybrid(NULL, NULL, 0, NULL, 0, 0, 0, 0, 0, 0) == 0);
    GsplatWebSort_SetTimingEnabled(NULL, 1);
    assert(GsplatWebSort_GetTimings(NULL, &timings) == 0);
    GsplatWebSort_CancelAndWait(NULL);
    GsplatWebSort_Destroy(NULL);
    puts("PASS: nonthreaded native fallback, hybrid ABI and inert entry points");
    return 0;
}
