#pragma once

#include <godot_cpp/classes/image.hpp>
#include <godot_cpp/classes/image_texture.hpp>
#include <godot_cpp/classes/node.hpp>
#include <godot_cpp/classes/ref.hpp>
#include <godot_cpp/variant/packed_byte_array.hpp>
#include <godot_cpp/variant/packed_float32_array.hpp>
#include <godot_cpp/variant/rid.hpp>

namespace godot {

// 配置面（低频，RID / Ref 经 Variant 传入）；热路径在 gfb_abi.h 的 C ABI 上。
class FastBridge : public Node {
	GDCLASS(FastBridge, Node)

public:
	enum SlotKind {
		SLOT_NONE = 0,
		SLOT_TEXTURE = 1, // RenderingDevice 纹理（texture_update / texture_get_data）
		SLOT_STORAGE_BUFFER = 2, // RenderingDevice 存储缓冲（buffer_update / buffer_get_data）
		SLOT_MULTIMESH = 3, // MultiMesh 实例缓冲（multimesh_set_buffer / multimesh_get_buffer）
		SLOT_IMAGE = 4, // Image 像素直传（image.set_data，可选 ImageTexture.update）
	};

	static constexpr int MAX_SLOTS = 16;

	// --- 配置 ---
	void configure_texture(int p_slot, const RID &p_texture, int p_byte_capacity);
	void configure_storage_buffer(int p_slot, const RID &p_buffer, int p_byte_capacity);
	void configure_multimesh(int p_slot, const RID &p_multimesh, int p_instance_count, int p_floats_per_instance);
	void configure_image(int p_slot, const Ref<Image> &p_image, const Ref<ImageTexture> &p_texture,
			int p_width, int p_height, int p_format);
	void clear_slot(int p_slot);
	void set_slot_enabled(int p_slot, bool p_enabled);

	// --- 热路径地址（C# 只取一次）---
	int64_t get_submit_address() const;
	int64_t get_submit_strided_address() const;
	int64_t get_read_address() const;
	int64_t get_context_address() const;
	int get_abi_version() const;

	// --- 观测 ---
	Dictionary get_stats() const;

	// --- C ABI 入口（由 gfb_abi.h 的导出包装调用）---
	void submit(int p_slot, const uint8_t *p_src, int p_bytes, double *r_fill_usec, double *r_submit_usec);
	void submit_strided(int p_slot, const uint8_t *p_src, int p_count, int p_stride, int p_elem_bytes,
			double *r_fill_usec, double *r_submit_usec);
	int read_into(int p_slot, uint8_t *p_dst, int p_offset, int p_bytes,
			double *r_fill_usec, double *r_submit_usec);

protected:
	static void _bind_methods();

private:
	struct Slot {
		int kind = SLOT_NONE;
		RID rid;
		bool enabled = true;
		// TEXTURE / MULTIMESH / IMAGE：固定容量（每次提交必须等长）
		int fixed_bytes = 0;
		// STORAGE_BUFFER：grow-only 容量
		int capacity_bytes = 0;
		PackedByteArray buffer;
		PackedFloat32Array float_buffer;
		// IMAGE 槽
		Ref<Image> image;
		Ref<ImageTexture> image_texture;
		int image_width = 0;
		int image_height = 0;
		int image_format = 0;
		// 统计
		uint64_t calls = 0;
		double fill_usec_total = 0.0;
		double submit_usec_total = 0.0;
		int buffer_peak = 0;
	};

	Slot slots[MAX_SLOTS];

	void _submit_slot(Slot &r_slot, int p_bytes);
	bool _valid_slot(int p_slot, Slot **r_slot);
};

} // namespace godot
