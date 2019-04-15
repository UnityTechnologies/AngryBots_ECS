#pragma once

#include <memory>
#include <stdlib.h>

class BumpAllocator
{
public:
    BumpAllocator(int chunkSize = 16384)
        : mChunkSize(chunkSize)
        , mChunkLeft(0)
    {
        mCurChunk = nullptr;
        mCurPtr = nullptr;
    }

    void* alloc(int size, int alignment = 0) {
        if (mChunkLeft < (size + alignment))
            newChunk(size + alignment);
        void* result = mCurPtr;
        if (alignment) {
            intptr_t v = alignment;
            result = (void*) ((intptr_t(mCurPtr) + (v - 1)) & ~(v-1));
        }
        mChunkLeft -= (intptr_t(result) - intptr_t(mCurPtr)) + size;
        mCurPtr = (uint8_t*) result + size;
        return result;
    }

    void reset() {
        // we're going to keep the first chunk arond, because chances are 16k per frame is all we'll need
        if (!mCurChunk)
            return;
        while (*(void**)mCurChunk != nullptr) {
            void *chunk = mCurChunk;
            mCurChunk = *(void**)mCurChunk;
            free(chunk);
        }

        mCurPtr = ((uint8_t*)mCurChunk) + sizeof(intptr_t);
        mChunkLeft = mChunkSize - sizeof(intptr_t);
    }

protected:
    size_t mChunkSize;
    size_t mChunkLeft;
    void* mCurChunk;
    uint8_t* mCurPtr;

    void newChunk(int neededSize) {
        neededSize += 32; // align forever // sizeof(intptr_t);
        size_t sz = neededSize < mChunkSize ? mChunkSize : neededSize;
        void* chunk = malloc(sz);

        // stuff the pointer back to the current chunk at the start of this chunk
        *(void**)chunk = mCurChunk;
        mCurChunk = chunk;

        mCurPtr = ((uint8_t*)chunk) + sizeof(intptr_t);
        mChunkLeft = sz - sizeof(intptr_t);
    }
};
