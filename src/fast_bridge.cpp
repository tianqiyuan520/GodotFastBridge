#include "fast_bridge.h"

#include "gfb_abi.h"

#include <godot_cpp/classes/rendering_device.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/classes/time.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/utility_functions.hpp>

#include <cstring>

using namespace godot;

static inline uint64_t gfb_now_usec() {
	return Time::get_singleton()->get_ticks_usec();
}

void FastBridge::_bind_methods() {
	ClassDB::bind_method(D_METHOD("configure_texture", "slot", "texture", "byte_capacity"), &FastBridge::configure_texture);
	ClassDB::bind_method(D_METHOD("configure_storage_buffer", "slot", "buffer", "byte_capacity"), &FastBridge::configure_storage_buffer);
	ClassDB::bind_method(D_METHOD("configure_multimesh", "slot", "multimesh", "instance_count", "floats_per_instance"), &FastBridge::configure_multimesh);
	ClassDB::bind_method(D_METHOD("configure_image", "slot", "image", "texture", "width", "height", "format"), &FastBridge::configure_image);
	ClassDB::bind_method(D_METHOD("clear_slot", "slot"), &FastBridge::clear_slot);
	ClassDB::bind_method(D_METHOD("set_slot_enabled", "slot", "enabled"), &FastBridge::set_slot_enabled);
	ClassDB::bind_method(D_METHOD("get_submit_address"), &FastBridge::get_submit_address);
	ClassDB::bind_method(D_METHOD("get_submit_strided_address"), &FastBridge::get_submit_strided_address);
	ClassDB::bind_method(D_METHOD("get_read_address"), &FastBridge::get_read_address);
	ClassDB::bind_method(D_METHOD("get_context_address"), &FastBridge::get_context_address);
	ClassDB::bind_method(D_METHOD("get_abi_version"), &FastBridge::get_abi_version);
	ClassDB::bind_method(D_METHOD("get_stats"), &FastBridge::get_stats);
}

bool FastBridge::_valid_slot(int p_slot, Slot **r_slot) {
	if (p_slot < 0 || p_slot >= MAX_SLOTS) {
		ERR_FAIL_V_MSG(false, vformat("FastBridge: slot %d 越界（0..%d）", p_slot, MAX_SLOTS - 1));
	}
	*r_slot = &slots[p_slot];
	return true;
}

// ---------------------------------------------------------------- 配置

void FastBridge::configure_texture(int p_slot, const RID &p_texture, int p_byte_capacity) {
	Slot *slot = nullptr;
	ERR_FAIL_COND(!_valid_slot(p_slot, &slot));
	ERR_FAIL_COND(!p_texture.is_valid());
	ERR_FAIL_COND(p_byte_capacity <= 0);

	slot->kind = SLOT_TEXTURE;
	slot->rid = p_texture;
	slot->fixed_bytes = p_byte_capacity;
	slot->capacity_bytes = p_byte_capacity;
	slot->enabled = true;
	// texture_update 上传整个 PackedByteArray，长度必须与纹理字节数一致且此后不变。
	slot->buffer.resize(p_byte_capacity);
	if (slot->buffer.size() > slot->buffer_peak) {
		slot->buffer_peak = (int)slot->buffer.size();
	}
	memset(slot->buffer.ptrw(), 0, (size_t)p_byte_capacity);
}

void FastBridge::configure_storage_buffer(int p_slot, const RID &p_buffer, int p_byte_capacity) {
	Slot *slot = nullptr;
	ERR_FAIL_COND(!_valid_slot(p_slot, &slot));
	ERR_FAIL_COND(!p_buffer.is_valid());
	ERR_FAIL_COND(p_byte_capacity <= 0);

	slot->kind = SLOT_STORAGE_BUFFER;
	slot->rid = p_buffer;
	slot->fixed_bytes = 0;
	slot->capacity_bytes = p_byte_capacity;
	slot->enabled = true;
	slot->buffer.resize(p_byte_capacity);
	if (slot->buffer.size() > slot->buffer_peak) {
		slot->buffer_peak = (int)slot->buffer.size();
	}
	memset(slot->buffer.ptrw(), 0, (size_t)p_byte_capacity);
}

void FastBridge::configure_multimesh(int p_slot, const RID &p_multimesh, int p_instance_count, int p_floats_per_instance) {
	Slot *slot = nullptr;
	ERR_FAIL_COND(!_valid_slot(p_slot, &slot));
	ERR_FAIL_COND(!p_multimesh.is_valid());
	ERR_FAIL_COND(p_instance_count <= 0 || p_floats_per_instance <= 0);

	const int floats = p_instance_count * p_floats_per_instance;
	slot->kind = SLOT_MULTIMESH;
	slot->rid = p_multimesh;
	slot->fixed_bytes = floats * (int)sizeof(float);
	slot->capacity_bytes = slot->fixed_bytes;
	slot->enabled = true;
	slot->float_buffer.resize(floats);
	if ((int)slot->float_buffer.size() > slot->buffer_peak) {
		slot->buffer_peak = (int)slot->float_buffer.size();
	}
	memset(slot->float_buffer.ptrw(), 0, (size_t)floats * sizeof(float));
}

void FastBridge::configure_image(int p_slot, const Ref<Image> &p_image, const Ref<ImageTexture> &p_texture,
		int p_width, int p_height, int p_format) {
	Slot *slot = nullptr;
	ERR_FAIL_COND(!_valid_slot(p_slot, &slot));
	ERR_FAIL_COND(p_image.is_null());
	ERR_FAIL_COND(p_width <= 0 || p_height <= 0);
	ERR_FAIL_COND(p_format < 0 || p_format >= Image::FORMAT_MAX);

	// 容量由图像自身尺寸与格式决定，不允许调用方自定义（避免 set_data 长度不匹配）。
	const int64_t bytes = p_image->get_data_size();
	ERR_FAIL_COND_MSG(bytes <= 0, "FastBridge: 传入的 Image 尺寸/格式无效，get_data_size() <= 0");

	slot->kind = SLOT_IMAGE;
	slot->rid = RID();
	slot->fixed_bytes = (int)bytes;
	slot->capacity_bytes = (int)bytes;
	slot->enabled = true;
	slot->image = p_image;
	slot->image_texture = p_texture;
	slot->image_width = p_width;
	slot->image_height = p_height;
	slot->image_format = p_format;
	slot->buffer.resize(bytes);
	if (slot->buffer.size() > slot->buffer_peak) {
		slot->buffer_peak = (int)slot->buffer.size();
	}
	memset(slot->buffer.ptrw(), 0, (size_t)bytes);
}

void FastBridge::clear_slot(int p_slot) {
	Slot *slot = nullptr;
	ERR_FAIL_COND(!_valid_slot(p_slot, &slot));
	*slot = Slot();
}

void FastBridge::set_slot_enabled(int p_slot, bool p_enabled) {
	Slot *slot = nullptr;
	ERR_FAIL_COND(!_valid_slot(p_slot, &slot));
	slot->enabled = p_enabled;
}

// ---------------------------------------------------------------- 提交

void FastBridge::_submit_slot(Slot &r_slot, int p_bytes) {
	switch (r_slot.kind) {
		case SLOT_MULTIMESH: {
			RenderingServer::get_singleton()->multimesh_set_buffer(r_slot.rid, r_slot.float_buffer);
		} break;
		case SLOT_TEXTURE: {
			RenderingDevice *rd = RenderingServer::get_singleton()->get_rendering_device();
			ERR_FAIL_NULL(rd);
			rd->texture_update(r_slot.rid, 0, r_slot.buffer);
		} break;
		case SLOT_STORAGE_BUFFER: {
			RenderingDevice *rd = RenderingServer::get_singleton()->get_rendering_device();
			ERR_FAIL_NULL(rd);
			rd->buffer_update(r_slot.rid, 0, (uint32_t)p_bytes, r_slot.buffer);
		} break;
		case SLOT_IMAGE: {
			ERR_FAIL_COND(r_slot.image.is_null());
			r_slot.image->set_data(r_slot.image_width, r_slot.image_height, false,
					(Image::Format)r_slot.image_format, r_slot.buffer);
			// ImageTexture.update 走一遍 GPU 上传；调用方传 null 表示只更新 Image 本身。
			if (r_slot.image_texture.is_valid()) {
				r_slot.image_texture->update(r_slot.image);
			}
		} break;
		default:
			break;
	}
}

void FastBridge::submit(int p_slot, const uint8_t *p_src, int p_bytes, double *r_fill_usec, double *r_submit_usec) {
	if (r_fill_usec) {
		*r_fill_usec = 0.0;
	}
	if (r_submit_usec) {
		*r_submit_usec = 0.0;
	}
	Slot *slot = nullptr;
	ERR_FAIL_COND(!_valid_slot(p_slot, &slot));
	ERR_FAIL_NULL(p_src);
	ERR_FAIL_COND(p_bytes <= 0);

	Slot &s = *slot;
	ERR_FAIL_COND(s.kind == SLOT_NONE);
	ERR_FAIL_COND(!s.enabled);

	const uint64_t fill_start = gfb_now_usec();
	if (s.kind == SLOT_MULTIMESH) {
		if (p_bytes != s.fixed_bytes) {
			ERR_FAIL_MSG(vformat("FastBridge: slot %d（MULTIMESH）提交 %d 字节，须等于配置的 %d 字节",
					p_slot, p_bytes, s.fixed_bytes));
		}
		memcpy(s.float_buffer.ptrw(), p_src, (size_t)p_bytes);
	} else {
		if (s.kind != SLOT_STORAGE_BUFFER && p_bytes != s.fixed_bytes) {
			ERR_FAIL_MSG(vformat("FastBridge: slot %d 提交 %d 字节，须等于配置的 %d 字节",
					p_slot, p_bytes, s.fixed_bytes));
		}
		// STORAGE_BUFFER：grow-only，超限才扩容（扩容计入 buffer_peak）
		if (p_bytes > s.capacity_bytes) {
			s.capacity_bytes = p_bytes;
			s.buffer.resize(p_bytes);
			if ((int)s.buffer.size() > s.buffer_peak) {
				s.buffer_peak = (int)s.buffer.size();
			}
		}
		memcpy(s.buffer.ptrw(), p_src, (size_t)p_bytes);
	}
	const uint64_t submit_start = gfb_now_usec();

	_submit_slot(s, p_bytes);

	const uint64_t end = gfb_now_usec();
	const double fill_usec = (double)(submit_start - fill_start);
	const double submit_usec = (double)(end - submit_start);
	s.calls++;
	s.fill_usec_total += fill_usec;
	s.submit_usec_total += submit_usec;
	if (r_fill_usec) {
		*r_fill_usec = fill_usec;
	}
	if (r_submit_usec) {
		*r_submit_usec = submit_usec;
	}
}

void FastBridge::submit_strided(int p_slot, const uint8_t *p_src, int p_count, int p_stride, int p_elem_bytes,
		double *r_fill_usec, double *r_submit_usec) {
	if (r_fill_usec) {
		*r_fill_usec = 0.0;
	}
	if (r_submit_usec) {
		*r_submit_usec = 0.0;
	}
	Slot *slot = nullptr;
	ERR_FAIL_COND(!_valid_slot(p_slot, &slot));
	ERR_FAIL_NULL(p_src);
	ERR_FAIL_COND(p_count <= 0 || p_elem_bytes <= 0 || p_stride < p_elem_bytes);

	Slot &s = *slot;
	ERR_FAIL_COND(s.kind != SLOT_STORAGE_BUFFER);
	ERR_FAIL_COND(!s.enabled);

	const int need_bytes = p_count * p_elem_bytes;
	const uint64_t fill_start = gfb_now_usec();
	if (need_bytes > s.capacity_bytes) {
		s.capacity_bytes = need_bytes;
		s.buffer.resize(need_bytes);
		if ((int)s.buffer.size() > s.buffer_peak) {
			s.buffer_peak = (int)s.buffer.size();
		}
	}
	uint8_t *dst = s.buffer.ptrw();
	for (int i = 0; i < p_count; i++) {
		memcpy(dst + (size_t)i * p_elem_bytes, p_src + (size_t)i * p_stride, (size_t)p_elem_bytes);
	}
	const uint64_t submit_start = gfb_now_usec();

	_submit_slot(s, need_bytes);

	const uint64_t end = gfb_now_usec();
	const double fill_usec = (double)(submit_start - fill_start);
	const double submit_usec = (double)(end - submit_start);
	s.calls++;
	s.fill_usec_total += fill_usec;
	s.submit_usec_total += submit_usec;
	if (r_fill_usec) {
		*r_fill_usec = fill_usec;
	}
	if (r_submit_usec) {
		*r_submit_usec = submit_usec;
	}
}

int FastBridge::read_into(int p_slot, uint8_t *p_dst, int p_offset, int p_bytes,
		double *r_fill_usec, double *r_submit_usec) {
	if (r_fill_usec) {
		*r_fill_usec = 0.0;
	}
	if (r_submit_usec) {
		*r_submit_usec = 0.0;
	}
	Slot *slot = nullptr;
	ERR_FAIL_COND_V(!_valid_slot(p_slot, &slot), -1);
	ERR_FAIL_NULL_V(p_dst, -1);
	ERR_FAIL_COND_V(p_bytes <= 0 || p_offset < 0, -1);

	Slot &s = *slot;
	ERR_FAIL_COND_V(s.kind == SLOT_NONE, -1);
	ERR_FAIL_COND_V(!s.enabled, -1);

	const uint64_t start = gfb_now_usec();
	int written = 0;

	// 引擎的读接口都只返回 Packed 容器（见 docs/Godot-C#互操作机制.md §四），
	// 因此这里省掉的是托管数组分配与一次托管拷贝，而不是引擎内部那次装填。
	switch (s.kind) {
		case SLOT_STORAGE_BUFFER:
		case SLOT_TEXTURE: {
			RenderingDevice *rd = RenderingServer::get_singleton()->get_rendering_device();
			ERR_FAIL_NULL_V(rd, -1);
			PackedByteArray data = (s.kind == SLOT_TEXTURE)
					? rd->texture_get_data(s.rid, 0)
					: rd->buffer_get_data(s.rid, (uint32_t)p_offset, (uint32_t)p_bytes);
			const uint64_t mid = gfb_now_usec();
			const int64_t avail = data.size() - p_offset;
			ERR_FAIL_COND_V(avail < p_bytes, -1);
			memcpy(p_dst, data.ptr() + p_offset, (size_t)p_bytes);
			s.fill_usec_total += (double)(mid - start);
			s.submit_usec_total += (double)(gfb_now_usec() - mid);
			written = p_bytes;
		} break;
		case SLOT_MULTIMESH: {
			PackedFloat32Array data = RenderingServer::get_singleton()->multimesh_get_buffer(s.rid);
			const uint64_t mid = gfb_now_usec();
			const int64_t avail_bytes = data.size() * (int64_t)sizeof(float) - p_offset;
			ERR_FAIL_COND_V(avail_bytes < p_bytes, -1);
			memcpy(p_dst, reinterpret_cast<const uint8_t *>(data.ptr()) + p_offset, (size_t)p_bytes);
			s.fill_usec_total += (double)(mid - start);
			s.submit_usec_total += (double)(gfb_now_usec() - mid);
			written = p_bytes;
		} break;
		case SLOT_IMAGE: {
			ERR_FAIL_COND_V(s.image.is_null(), -1);
			PackedByteArray data = s.image->get_data();
			const uint64_t mid = gfb_now_usec();
			const int64_t avail = data.size() - p_offset;
			ERR_FAIL_COND_V(avail < p_bytes, -1);
			memcpy(p_dst, data.ptr() + p_offset, (size_t)p_bytes);
			s.fill_usec_total += (double)(mid - start);
			s.submit_usec_total += (double)(gfb_now_usec() - mid);
			written = p_bytes;
		} break;
		default:
			ERR_FAIL_V_MSG(-1, "FastBridge: 该槽类型不支持回读");
	}

	s.calls++;
	return written;
}

// ---------------------------------------------------------------- 地址 / 观测

int64_t FastBridge::get_submit_address() const {
	return reinterpret_cast<int64_t>(&gfb_submit);
}

int64_t FastBridge::get_submit_strided_address() const {
	return reinterpret_cast<int64_t>(&gfb_submit_strided);
}

int64_t FastBridge::get_read_address() const {
	return reinterpret_cast<int64_t>(&gfb_read_into);
}

int64_t FastBridge::get_context_address() const {
	return reinterpret_cast<int64_t>(this);
}

int FastBridge::get_abi_version() const {
	return GFB_ABI_VERSION;
}

Dictionary FastBridge::get_stats() const {
	Dictionary out;
	Array slots_info;
	for (int i = 0; i < MAX_SLOTS; i++) {
		const Slot &s = slots[i];
		if (s.kind == SLOT_NONE) {
			continue;
		}
		Dictionary d;
		d["slot"] = i;
		d["kind"] = s.kind;
		d["enabled"] = s.enabled;
		d["calls"] = (int64_t)s.calls;
		d["fill_usec_total"] = s.fill_usec_total;
		d["submit_usec_total"] = s.submit_usec_total;
		d["buffer_peak"] = s.buffer_peak;
		slots_info.push_back(d);
	}
	out["slots"] = slots_info;
	out["abi_version"] = GFB_ABI_VERSION;
	return out;
}

// ---------------------------------------------------------------- C ABI

extern "C" {

GFB_API int gfb_abi_version(void) {
	return GFB_ABI_VERSION;
}

GFB_API void gfb_submit(void *ctx, int slot, const void *src, int bytes, GfbTiming *out) {
	double fill = 0.0;
	double submit = 0.0;
	static_cast<FastBridge *>(ctx)->submit(slot, static_cast<const uint8_t *>(src), bytes, &fill, &submit);
	if (out) {
		out->fill_usec = fill;
		out->submit_usec = submit;
	}
}

GFB_API void gfb_submit_strided(void *ctx, int slot, const void *src, int count,
		int stride, int elem_bytes, GfbTiming *out) {
	double fill = 0.0;
	double submit = 0.0;
	static_cast<FastBridge *>(ctx)->submit_strided(slot, static_cast<const uint8_t *>(src), count,
			stride, elem_bytes, &fill, &submit);
	if (out) {
		out->fill_usec = fill;
		out->submit_usec = submit;
	}
}

GFB_API int gfb_read_into(void *ctx, int slot, void *dst, int offset, int bytes, GfbTiming *out) {
	double fill = 0.0;
	double submit = 0.0;
	const int written = static_cast<FastBridge *>(ctx)->read_into(slot, static_cast<uint8_t *>(dst),
			offset, bytes, &fill, &submit);
	if (out) {
		out->fill_usec = fill;
		out->submit_usec = submit;
	}
	return written;
}

GFB_API void gfb_flush(void *ctx) {
	(void)ctx;
}

} // extern "C"
