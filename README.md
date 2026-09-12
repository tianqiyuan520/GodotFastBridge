# GodotFastBridge

**C# → Godot 原生**的大数据提交桥：GDExtension（C ABI 热路径）+ C# facade。
把「每次调用新建原生临时容器 + 整块拷贝 + 释放」换成「复用缓冲 + 直接吃调用方指针」，并把回读从「每次新建托管数组」改成「写进调用方缓冲」。

- 目标版本：Godot **4.4+**（已在 4.4.1 / 4.7 stable 上加载自检通过）
- 依赖：仅 godot-cpp；零第三方依赖、零跨项目耦合
- 文档：[设计分析](docs/设计分析.md) · [收益模型与实测](docs/收益模型与实测.md) · [Godot-C# 互操作机制](docs/Godot-C#互操作机制.md)

---

## 一、为什么存在（机制）

Godot 的 C# 绑定在每次传数组给引擎 API 时，都会在**原生侧**新建一块 `Packed*Array` 并整块 `memcpy`，调用返回后立刻释放：

```csharp
// Generated/NativeCalls.cs（Godot 4.5 快照）
using godot_packed_float32_array arg2_in = Marshaling.ConvertSystemArrayToNativePackedFloat32Array(arg2);
void** call_args = stackalloc void*[2] { &arg1, &arg2_in };
NativeFuncs.godotsharp_method_bind_ptrcall(method, ptr, call_args, null);
```

`Span<T>` 重载**不能**免除这次拷贝（走同一个 `new_mem_copy`）；而引擎容器不接受外部内存（`CowData` 无 wrap/proxy），RD 也没有 `buffer_map` —— 所以**真零拷贝不可达**。GFB 能省的只有：那次原生**分配/释放**、当调用方本来有中转时的**一次拷贝**，以及回读方向的**托管数组分配**。

---

## 二、实测收益（release 导出，Godot 4.7 stable，RTX 4060 Laptop，30000 实例）

**提交（IN）**

| 用例 | C# 直接 | C# + 新数组 | GFB 桥 | vs 直接 | vs 新数组 |
|---|---|---|---|---|---|
| SSBO 1406 KB（`RenderingDevice.BufferUpdate`） | 239 µs | 309 µs | **57 µs** | **4.17×** | **5.39×** |
| MultiMesh 937 KB（`MultimeshSetBuffer`） | 78 µs | 132 µs | **44 µs** | **1.79×** | **3.03×** |
| RD 纹理 4096 KB（`TextureUpdate`） | 857 µs | — | **515 µs** | **1.66×** | — |
| Image 4096 KB（`Image.SetData`+`Update`） | 846 µs | — | 927 µs | **0.91×（无收益）** | — |

**回读（OUT）**

| 用例 | C# 回读 | GFB `read_into` | 加速 | 托管分配/次 |
|---|---|---|---|---|
| SSBO 1406 KB | 509 µs | 403 µs | 1.26× | 1,440,024 B → **0 B** |
| MultiMesh 937 KB | 298 µs | 270 µs | 1.10× | 960,024 B → **0 B** |
| RD 纹理 4096 KB | 1285 µs | 1154 µs | 1.11× | 4,194,328 B → **0 B** |
| Image 4096 KB | 197 µs | 99 µs | **2.00×** | 4,194,328 B → **0 B** |

逐字节一致性全部通过；debug host 与 release 数字一致（收益来自原生侧，非 JIT 假象）。

**换算成帧内占比（决定"值不值得用"）**

| 场景 | payload/帧 | 收益/帧 | 占帧比 |
|---|---|---|---|
| 1M 单位（如 CSBS，位置+动画+渲染序） | ~16 MB | ~1.4 ms | **~14%**（其整帧 9.45 ms） |
| 30000 实例（如僵尸大战僵尸） | 1.44 MB +两块 1.44 MB 分配 | 252 µs | **~1.5%**@60fps / 5%@200fps |
| 每帧 < 1 MB | — | < 100 µs | 噪声级 |

> **结论：这套东西只对「每帧搬运 ≥ 8 MB」的项目有量级收益。** 小 payload 项目接它不划算。

---

## 三、准入门槛（不满足不要接）

```
单次 payload ≥ 256 KB  或  同一帧跨界次数 ≥ 1000
且数据在 C# 侧已是连续内存
且走的是以下三条提交通道之一：
    RenderingDevice.BufferUpdate / TextureUpdate / RenderingServer.MultimeshSetBuffer
```

**明确无收益**：`Image.SetData` 写入方向（实测 0.85~1.19× ≈ 持平甚至略劣）、任何 < 256 KB 的调用、一次性上传、参数类小调用（`SetShaderParameter`、4 字节 `BufferUpdate`）、字符串/集合类 API。

---

## 四、快速开始

### 构建

```shell
git submodule update --init godot-cpp
scons target=template_debug   -j 8      # 编辑器用
scons target=template_release -j 8      # 导出用
# 产物自动安装到 project/addons/GodotFastBridge/bin/<platform>/
```

### 接入

1. 把 `project/addons/GodotFastBridge/` 整个目录拷进你的 Godot 项目。
2. C# 项目需允许 unsafe：`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`。
3. 场景里加一个 `FastBridge` 节点（GDExtension 类），`FastBridge.Initialize()` 会自动找它。

### C# 用法

```csharp
using GodotFastBridge;

public override void _Ready()
{
    FastBridge.Initialize(this);                                  // 查找名为 "FastBridge" 的子节点
    _ssboSlot = FastBridge.ConfigureStorageBuffer(_ssboRid, bytes);
    _mmSlot   = FastBridge.ConfigureMultiMesh(_mm.GetRid(), 30000, 8);
    _texSlot  = FastBridge.ConfigureTexture(_textureRd, bytes);
    _imgSlot  = FastBridge.ConfigureImage(_image, _imageTexture, w, h, (int)Image.Format.Rgf);
}

private unsafe void SubmitFrame(byte[] data)
{
    if (!FastBridge.Enabled) { /* 回退原生路径：A/B 与线上兜底 */ return; }
    fixed (byte* p = data)
    {
        FastBridge.Submit(_ssboSlot, p, data.Length, out double fillMs, out double submitMs);
    }
}

private unsafe void ReadBack(byte[] dst)
{
    fixed (byte* p = dst) { FastBridge.ReadInto(_ssboSlot, p, 0, dst.Length); }   // 不回传托管数组
}
```

数据源可以是 `EntJoy NativeArray<T>.GetUnsafePtr()`、pinned 数组或 `stackalloc` —— **GFB 只认 `void*`，不提供自己的容器类型**。

---

## 五、已实现的后端

| 槽位类型 | 提交 | 回读 |
|---|---|---|
| `configure_storage_buffer`（RD 存储缓冲） | `RenderingDevice.buffer_update` | `buffer_get_data` |
| `configure_texture`（RD 纹理） | `RenderingDevice.texture_update` | `texture_get_data` |
| `configure_multimesh`（MultiMesh 实例缓冲） | `RenderingServer.multimesh_set_buffer` | `multimesh_get_buffer` |
| `configure_image`（Image + 可选 ImageTexture） | `Image.set_data`（+`ImageTexture.update`）**（无收益，仅回读推荐）** | `Image.get_data` |

C ABI（`src/gfb_abi.h`，ABI v1）：

```c
int   gfb_abi_version(void);
void  gfb_submit        (void *ctx, int slot, const void *src, int bytes, GfbTiming *out);
void  gfb_submit_strided(void *ctx, int slot, const void *src, int count, int stride, int elem_bytes, GfbTiming *out);
int   gfb_read_into     (void *ctx, int slot, void *dst, int offset, int bytes, GfbTiming *out);
void  gfb_flush         (void *ctx);
```

RID / Image 等 Godot 句柄走 GDExtension 类的 bound method 配置（低频）；热路径走 `delegate* unmanaged[Cdecl]` 函数指针（零 Variant）。

---

## 六、明确不做

- 不提供 PackedArray / NativeArray 等容器类型（`Span<T>` 已是零拷贝视图；容器用你自己的）
- 不承诺零拷贝（引擎容器必须独占内存，见 `docs/Godot-C#互操作机制.md` §四）
- 不做 StringName / 集合 / `EmitSignal` / `Call-Get-Set` 缓存 —— 那些是「用错 API」，改 C# 代码即可
- 不做批量物理读写（`PhysicsServer*` 不在中转拷贝族里，为 0 处）
- 不做 compute / indirect / 剔除等 GPU 侧逻辑
- **MultiMesh 物理插值提交已实测劣化（0.38~0.46×）并移除**，不要重新加回

---

## 七、基准

`project/benchmark/` 是完整的 A/B 场景（4 类 IN + 4 类 OUT + 逐字节校验 + 托管分配统计）：

```shell
dotnet build project/GodotFastBridgeDemo.csproj -c Debug
"D:\...\Godot_v4.7-stable_mono_win64_console.exe" --path project
# 结果同时打印并写入 user://gfb_bench.txt

# release 环境
"...godot_console.exe" --headless --path project --export-release "Windows Desktop"
project/build/GFBench.exe     # 导出 exe 是 GUI 子系统，stdout 取不到，看 user://gfb_bench.txt
```

---

## 八、目录结构

```
GodotFastBridge/
├─ src/
│  ├─ gfb_abi.h            C ABI 契约（ABI 版本 + GfbTiming）
│  ├─ fast_bridge.h/.cpp   GDExtension 类 + C ABI 实现（槽位/缓冲/后端/计时/统计）
│  └─ register_types.*     类注册（入口符号 godot_fast_bridge_library_init）
├─ project/
│  ├─ addons/GodotFastBridge/
│  │  ├─ bin/GodotFastBridge.gdextension
│  │  └─ csharp/FastBridge.cs、FastBridgeNative.cs
│  ├─ benchmark/Benchmark.cs + benchmark.tscn
│  └─ demo.gd/.tscn        加载自检
├─ doc_classes/FastBridge.xml
└─ docs/                   设计分析 / 收益模型与实测 / Godot-C#互操作机制
```

## 九、已知限制

- 提交必须发生在引擎允许的线程；与 Job System 组合时用「并行填充 → 主线程提交」
- C# 传入的指针**仅在调用期内有效**，桥不保留
- `TEXTURE` / `MULTIMESH` / `IMAGE` 槽每次提交字节数必须等于配置容量（引擎侧上传整个容器）
- 上游风险：若 Godot 把 `new_mem_copy` 改成零拷贝 wrap，提交方向的收益将归零（回读与批量原语不受影响）

## 许可证

见 [LICENSE.md](LICENSE.md)。
