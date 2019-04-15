#include <stdlib.h>
#include "string.h"
#include <stdint.h>

#include "BumpAllocator.h"

#ifdef _WIN32
#define DOEXPORT __declspec(dllexport)
#define CALLEXPORT __stdcall
#else
#define DOEXPORT __attribute__ ((visibility ("default")))
#define CALLEXPORT
#endif

//#define GUARD_HEAP

enum class Allocator
{
    // NOTE: The items must be kept in sync with Runtime/Export/Collections/NativeCollectionAllocator.h
    Invalid = 0,
    // NOTE: this is important to let Invalid = 0 so that new NativeArray<xxx>() will lead to an invalid allocation by default.
    None = 1,
    Temp = 2,
    TempJob = 3,
    Persistent = 4
};

static BumpAllocator sBumpAlloc;

#ifdef GUARD_HEAP

#define GUARD_EXTRAALIGN 64

static void guardfail() {
    #ifdef _WIN32
        __debugbreak();
    #else
        abort();
    #endif
}

static void guardcheck(unsigned char *p, unsigned char x, size_t s) {
    for ( size_t i=0; i<s; i++ ) {
        if (p[i]!=x) {
            guardfail();
            return;
        }
    }
}

struct GuardHeader {
    unsigned char front[64];
    union {
        struct {
            int64_t size;
            void *unalignedbase;
        };
        unsigned char pad[64];
    };
    unsigned char back[64];
};

#endif

extern "C"{
DOEXPORT
void* CALLEXPORT unsafeutility_malloc(int64_t size, int alignment, Allocator allocatorType)
{
#ifdef GUARD_HEAP
    size_t sall = (size_t)size+2*sizeof(GuardHeader)+GUARD_EXTRAALIGN;
    unsigned char *runaligned = (unsigned char*)malloc(sall);
    memset(runaligned, 0xbc, sall);

    intptr_t rp = (((intptr_t)runaligned) + GUARD_EXTRAALIGN - 1) & (~(GUARD_EXTRAALIGN-1));
    unsigned char *r = (unsigned char *)rp;

    GuardHeader *hstart = (GuardHeader*)r;
    GuardHeader *hend = (GuardHeader*)(r+sizeof(GuardHeader)+size);

    memset (hstart->front, 0xf1, sizeof(hstart->front));
    memset (hstart->back, 0xf2, sizeof(hstart->back));
    memset (hstart->pad, 0xf3, sizeof(hstart->pad));
    hstart->unalignedbase = runaligned;
    hstart->size = size;

    memset (hend->front, 0xa1, sizeof(hend->front));
    memset (hend->back, 0xa2, sizeof(hend->back));
    memset (hend->pad, 0xa3, sizeof(hend->pad));
    hend->unalignedbase = runaligned;
    hend->size = size;

    return r+sizeof(GuardHeader);
#else
    if (allocatorType == Allocator::Temp)
        return sBumpAlloc.alloc((int)size, alignment);
    return malloc((size_t)size);
#endif
}

DOEXPORT
void CALLEXPORT unsafeutility_free(void* ptr, Allocator allocatorType)
{
    if (ptr == nullptr)
        return;
    if (allocatorType == Allocator::Temp)
        return;
#ifdef GUARD_HEAP
    if ( (((intptr_t)ptr) & (GUARD_EXTRAALIGN-1))!=0 )
        guardfail();

    unsigned char *r = (unsigned char*)ptr;
    r -= sizeof(GuardHeader);

    GuardHeader *hstart = (GuardHeader*)r;
    GuardHeader *hend = (GuardHeader*)(r+sizeof(GuardHeader)+hstart->size);

    if ( hstart->size != hend->size || hstart->unalignedbase != hend->unalignedbase ) {
        guardfail();
        return;
    }

    guardcheck(hstart->front, 0xf1, sizeof(hstart->front));
    guardcheck(hstart->back, 0xf2, sizeof(hstart->back));
    guardcheck (hend->front, 0xa1, sizeof(hend->front));
    guardcheck (hend->back, 0xa2, sizeof(hend->back));

    free(hstart->unalignedbase);
#else
    free(ptr);
#endif
}

DOEXPORT
void CALLEXPORT unsafeutility_memclear(void* destination, int64_t size)
{
    memset(destination, 0, static_cast<size_t>(size));
}

DOEXPORT
void CALLEXPORT unsafeutility_freetemp()
{
    sBumpAlloc.reset();
}

#define UNITY_MEMCPY memcpy
typedef uint8_t UInt8;

DOEXPORT
void CALLEXPORT unsafeutility_memcpy(void* destination, void* source, int64_t count)
{
    UNITY_MEMCPY(destination, source, (size_t)count);
}

DOEXPORT
void CALLEXPORT unsafeutility_memcpystride(void* destination_, int destinationStride, void* source_, int sourceStride, int elementSize, int64_t count)
{
    UInt8* destination = (UInt8*)destination_;
    UInt8* source = (UInt8*)source_;
    if (elementSize == destinationStride && elementSize == sourceStride)
    {
        UNITY_MEMCPY(destination, source, static_cast<size_t>(count) * static_cast<size_t>(elementSize));
    }
    else
    {
        for (int i = 0; i != count; i++)
        {
            UNITY_MEMCPY(destination, source, elementSize);
            destination += destinationStride;
            source += sourceStride;
        }
    }
}

DOEXPORT
int32_t CALLEXPORT unsafeutility_memcmp(void* ptr1, void* ptr2, uint64_t size)
{
    return memcmp(ptr1, ptr2, (size_t)size);
}

DOEXPORT
void CALLEXPORT unsafeutility_memcpyreplicate(void* dst, void* src, int size, int count)
{
    uint8_t* dstbytes = (uint8_t*)dst;
    // TODO something smarter
    for (int i = 0; i < count; ++i)
    {
        memcpy(dstbytes, src, size);
        dstbytes += size;
    }
}

DOEXPORT
void CALLEXPORT unsafeutility_memmove(void* dst, void* src, uint64_t size)
{
    memmove(dst, src, (size_t)size);
}
}
