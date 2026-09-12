#pragma once

#include <cstdint>

// GodotFastBridge C ABI.
// 这一层是热路径：只接受裸指针与字节数，不出现任何 Godot 类型（Variant/RID/String）。
// RID 的配置走 GDExtension 类的 bound method（见 fast_bridge.h），此处只做数据提交与回读。

#define GFB_ABI_VERSION 1
#define GFB_MAX_SLOTS 16

#ifdef _WIN32
#ifdef GFB_EXPORTS
#define GFB_API __declspec(dllexport)
#else
#define GFB_API __declspec(dllimport)
#endif
#else
#define GFB_API __attribute__((visibility("default")))
#endif

extern "C" {

typedef struct {
	double fill_usec;
	double submit_usec;
} GfbTiming;

GFB_API int gfb_abi_version(void);

// 把 src 指向的 bytes 字节填进 slot 的复用缓冲并提交。
// 契约：TEXTURE / MULTIMESH 槽要求 bytes 等于 configure 时确定的容量。
GFB_API void gfb_submit(void *ctx, int slot, const void *src, int bytes, GfbTiming *out);

// 从 src 起按 stride 取 count 个元素，每元素取 elem_bytes，拼成紧凑 payload 后提交。
GFB_API void gfb_submit_strided(void *ctx, int slot, const void *src, int count,
		int stride, int elem_bytes, GfbTiming *out);

// 把槽源读进调用方缓冲（不回传托管数组）。
GFB_API int gfb_read_into(void *ctx, int slot, void *dst, int offset, int bytes, GfbTiming *out);

GFB_API void gfb_flush(void *ctx);

} // extern "C"
