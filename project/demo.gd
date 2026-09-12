extends Node

# 加载自检：确认扩展被 Godot 载入、FastBridge 类可用、C ABI 地址可取。
func _ready() -> void:
	var bridge := FastBridge.new()
	bridge.name = "FastBridge"
	add_child(bridge)

	var abi: int = bridge.get_abi_version()
	var submit_addr: int = bridge.get_submit_address()
	var ctx: int = bridge.get_context_address()
	print("[GFB] abi_version=", abi, " submit_addr=", submit_addr, " ctx=", ctx)
	if abi != 1 or submit_addr == 0 or ctx == 0:
		push_error("[GFB] 自检失败：ABI 地址不可用")
		return
	print("[GFB] stats=", bridge.get_stats())
	get_tree().quit()
