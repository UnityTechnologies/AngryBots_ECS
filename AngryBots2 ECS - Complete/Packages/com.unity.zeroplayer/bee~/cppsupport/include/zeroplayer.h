#pragma once
#include <cstdint>

#if WIN32

#define ZEROPLAYER_EXPORT extern "C" __declspec(dllexport)
#define ZEROPLAYER_CALL __stdcall

#else

#if __cplusplus
#define ZEROPLAYER_EXPORT extern "C" __attribute__ ((visibility ("default")))
#else
#define ZEROPLAYER_EXPORT __attribute__ ((visibility ("default")))
#endif
#define ZEROPLAYER_CALL

#endif
