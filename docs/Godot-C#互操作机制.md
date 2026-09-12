# Godot ↔ C# 互操作机制（证据链）

> 版本：2026 初稿
> 依据：引擎源码 `D:\Godot\GODOT\Source\godot-master`（**4.5.0-beta** master 快照）、GodotSharp 程序集反射（**4.4.1.0** / **4.7.0.0**）、godot-cpp 生成头（4.4 分支）
> 用途：GFB 全部设计决策的事实基础；**改动本文件前必须先复验证据**

---

## 一、结论速览

1. C# 的**强类型绑定调用不慢**：`MethodBind` 指针缓存在 C# 静态字段，热路径 = 判空 + 一次 ptrcall。
2. **数组参数每次调用都会新建一块原生临时容器并整块拷贝**，调用后立刻释放 —— 这是 GFB 存在的唯一理由。
3. **`Span<T>` 重载不能免除该拷贝**（两个重载走同一个 icall，icall 体内做 `new_mem_copy`）。
4. **真零拷贝不可达**：引擎容器（`PackedXArray` = `Vector<T>`）不接受外部内存，RD 也没有 `buffer_map`。
5. C# 相对 GDScript 的真实劣势不在调用，而在**用法**（字符串名 / Variant / 集合 / 包装对象）—— 那不属于 GFB 范围。

---

## 二、调用路径

### 2.1 快路径（强类型绑定方法）

```csharp
// 生成的绑定：MethodBind 指针是静态缓存的（只解析一次）
private static readonly IntPtr MethodBind83 = ClassDB_get_method_with_compatibility(...);
public static void MultimeshSetBuffer(Rid multimesh, ReadOnlySpan<float> buffer)
    => NativeCalls.godot_icall_2_1096(MethodBind83, GodotObject.GetPtr(Singleton), multimesh, buffer);
```
```cpp
// C++ 侧全部开销
void godotsharp_method_bind_ptrcall(MethodBind *p_method_bind, Object *p_instance, const void **p_args, void *p_ret) {
    p_method_bind->ptrcall(p_instance, p_args, p_ret);
}
```
- 证据：`modules/mono/glue/runtime_interop.cpp:583-585`、`Generated/GodotObjects/RenderingServer.cs:3281-3284`
- 结论：**无字符串查找、无 Variant、无反射**。

### 2.2 慢路径（动态调用）

```cpp
godot_variant godotsharp_method_bind_call(MethodBind*, Object*, const godot_variant** p_args,
                                          int32_t p_arg_count, Callable::CallError*);
```
- 证据：`runtime_interop.cpp:587-596`；C# 侧 `GodotObject.cs:726/739`（`Call(StringName, params Variant[])`）
- 结论：`Call/Get/Set/EmitSignal` 每次都要 Variant 数组 + 逐参数转换 + CallError → **热路径禁用**。

---

## 三、中转容器：GFB 的全部理由

### 3.1 五步证据链

**① Span 重载最终调的是同一个 icall**
`Generated/GodotObjects/RenderingServer.cs:3281` →
`NativeCalls.godot_icall_2_1096(MethodBind83, GodotObject.GetPtr(Singleton), multimesh, buffer)`

**② 数组重载只多一条隐式转换，随后走同一 icall**
反射 IL：`MultimeshSetBuffer(Rid, float[])` = 28 字节 IL（多 `ReadOnlySpan.op_Implicit`）；
`MultimeshSetBuffer(Rid, ReadOnlySpan<float>)` = 23 字节 IL。**托管侧没有额外拷贝。**

**③ icall 体内新建原生临时容器**
```csharp
// Generated/NativeCalls.cs:10048（4.5 快照；4.4.1 为 godot_icall_2_1059，4.7 为 godot_icall_2_1185，形态一致）
internal static unsafe void godot_icall_2_1096(IntPtr method, IntPtr ptr, Rid arg1, ReadOnlySpan<float> arg2)
{
    ExceptionUtils.ThrowIfNullPtr(ptr);
    using godot_packed_float32_array arg2_in = Marshaling.ConvertSystemArrayToNativePackedFloat32Array(arg2); // ← 临时容器
    void** call_args = stackalloc void*[2] { &arg1, &arg2_in };
    NativeFuncs.godotsharp_method_bind_ptrcall(method, ptr, call_args, null);
}   // ← using：调用后立刻释放
```

**④ 转换实现 = 固定 span + 原生 mem_copy**
```csharp
// Core/NativeInterop/Marshaling.cs:524-530
public static unsafe godot_packed_float32_array ConvertSystemArrayToNativePackedFloat32Array(scoped ReadOnlySpan<float> p_array)
{
    if (p_array.IsEmpty) return new godot_packed_float32_array();
    fixed (float* src = p_array)
        return NativeFuncs.godotsharp_packed_float32_array_new_mem_copy(src, p_array.Length);
}
```

**⑤ 原生侧 = 分配 + memcpy**
```cpp
// modules/mono/glue/runtime_interop.cpp:429-437
godot_packed_array godotsharp_packed_float32_array_new_mem_copy(const float *p_src, int32_t p_length) {
    godot_packed_array ret;
    memnew_placement(&ret, PackedFloat32Array);
    PackedFloat32Array *array = reinterpret_cast<PackedFloat32Array *>(&ret);
    array->resize(p_length);                       // ← 原生堆分配
    float *dst = array->ptrw();
    memcpy(dst, p_src, p_length * sizeof(float));  // ← 整块拷贝
    return ret;
}
```

### 3.2 版本一致性（三方均验证）

| 版本 | MultimeshSetBuffer 的 Span 重载 | icall 体内的调用 |
|---|---|---|
| GodotSharp **4.4.1.0** | `godot_icall_2_1059` | `Marshaling.ConvertSystemArrayToNativePackedFloat32Array` + `Dispose` |
| 引擎源码 **4.5.0-beta** | `godot_icall_2_1096` | 同上 |
| GodotSharp **4.7.0.0** | `godot_icall_2_1185` | 同上 |

编号随版本变，**形态不变** → GFB 的机制假设在 4.4.1 与 4.7 同时成立。

### 3.3 回读方向

```csharp
// Marshaling.cs:450-461 形态
var array = new T[size];              // ← 每次调用新建托管数组
fixed (T* dest = array) Buffer.MemoryCopy(buffer, dest, sizeInBytes, sizeInBytes);
```
→ `BufferGetData` / `TextureGetData` / `MultimeshGetBuffer` / `Image.GetData` 每次调用都产生托管分配。

---

## 四、天花板：为什么做不到零拷贝

| 事实 | 证据 |
|---|---|
| GDExtension 侧 API 参数就是引擎容器 | `godot-cpp/gen/include/godot_cpp/classes/rendering_device.hpp:717`（`texture_update(..., const PackedByteArray&)`）、`:758`（`buffer_update(..., const PackedByteArray&)`）、`:760`（`buffer_get_data → PackedByteArray`）；`rendering_server.hpp:901/904` |
| 容器即 `Vector<T>`，底层 `CowData` 恒持有引用计数堆块 | `core/variant/variant.h:77,80`（`typedef Vector<uint8_t> PackedByteArray;`）；`core/templates/cowdata.h`（`_ptr` + `DATA_OFFSET` 头，`Memory::alloc_static` / `free_static`） |
| `CowData` **无 wrap / proxy / 外部内存构造** | `cowdata.h` 全文检索无 proxy/wrap 入口 |
| RD **无 buffer_map / unmap** | `servers/rendering/rendering_device.h:209`（内部 `buffer_update(RID,offset,size,const void*)`，非 method bind）、`:1706`（绑定版收 `const Vector<uint8_t>&`） |

**因此**：任何 GDExtension 桥都必须把数据拷进一个引擎容器 → GFB 只能省「分配/释放 + 一次中转」，不能省「引擎那次拷贝」。

**上游风险**：`new_mem_copy` 完全可以改成零拷贝 wrap（调用期间 `fixed` 住 span 内存）。C++ 侧现为 9 个 `new_mem_copy`（`runtime_interop.cpp:1605-1613`），**无 wrap 变体**。若上游实施，GFB 的入向收益归零（出向和批量原语不受影响）。

---

## 五、其他优化的边界（不属于 GFB）

| # | 开销 | 证据 | C# 修法 | 归属 |
|---|---|---|---|---|
| 1 | 字面量名字每次新建 `StringName`/`NodePath` | `Core/StringName.cs:68-72`；官方注释建议用缓存名（`GodotObject.cs:694`） | 用生成的 `Xxx.MethodName/PropertyName/SignalName` 常量（如 `Node.cs:2944+/3495+`）；引擎侧有 `SNAME` 静态缓存机制（`string_name.h:213`） | **改代码** |
| 2 | `Godot.Collections.Array/Dictionary` 每次操作都是跨界 | 每个索引/Add/Count 一次 icall | 热路径换数组 / `Span<T>` | **改代码** |
| 3 | `EmitSignal` 每次 `params Variant[]` + 逐参 Variant | `GodotObject.cs:696/710` | 高频信号改 C# `event`；至少缓存信号名 | **改代码** |
| 4 | 返回包装对象（`GetChildren()` 等）→ GC | 上游提案 #12375（对象池）**未实现** | 热路径少调 + 缓存 | **上游** |
| 5 | `Call/Get/Set` 走 Variant 而非 ptrcall | `runtime_interop.cpp:587` | 热路径禁用 | **改代码** |
| 6 | 编辑器/调试构建的额外互操作开销 | 上游 PR #116305（warappa）声称编辑器最高 ~10x；本项目未复现；其发布物为 4.7-dev 构建 | 导出用 release | **上游** |

---

## 六、API 面（谁在付费）

静态扫描 `.../GodotSharp/Generated/`：**511 个 API / 790 个重载**走中转拷贝；
`byte 193 / Vector2 158 / String 147 / int32 104 / float 66 / Vector3 64 / Color 28 / int64 24 / float64 6`；
类分布前 12：`RenderingDevice 20 / RenderingServer 12 / Geometry2D 9 / OS 7 / DisplayServer 6 / CanvasItem 6 / TextServer 5 / MultiMesh 5 / Image 4 / FileAccess 4 / Polygon2D 4 / NavigationPolygon 4`。
**`PhysicsServer2D/3D` 为 0 处。**

分级与接入建议见 `docs/收益模型与实测.md` §三。

---

## 七、已证伪 / 已更正（避免重复踩坑）

| 曾经的判断 | 实际 | 证据 |
|---|---|---|
| 「C# 数组在托管侧被逐元素封送成 PackedArray」 | 托管侧只有一次 `op_Implicit`；拷贝发生在**原生** `new_mem_copy` | §3.1 ①②⑤ |
| 「加 Span 重载可以省掉这次拷贝」（`docs/AI聊天记录.txt:183/325`） | Span 重载**在 4.4.1 就已存在**，且不能省 | §3.2 |
| 「`MeshSurfaceAddVertex` / `MeshAddSurfaceFromArrays` 存在」（聊天记录 :600-601） | **不存在**；真实 API 是 `MeshAddSurfaceFromArrays(Rid, PrimitiveType, Array arrays, …)`（走 `Godot.Collections.Array`） | 反射 4.7 + 引擎源码 |
| 「C# 侧的 `PackedFloat32Array` 提供 `AsSpan()`/`GetPtrw()`」（聊天记录 :641/650） | 4.4 起 packed 类型**不在 C# 公共 API 中**（无 `Packed*Array` 公共类型） | 反射 4.4.1/4.7 |
| 「每帧 N 次 `PhysicsServer2D.BodyGetState` 属于 GFB 的优化对象」 | **不属于**该机制（物理服务器无数组参数中转）；需另立「批量原语」 | §六 + 扫描结果 0 处 |
| 「僵尸大战僵尸每帧 `MultimeshGetBuffer` + `MultimeshSetBuffer` 是热点」 | **死代码**：`SetMultiMeshBufferEmpty/ByEntities` 全项目无调用者 | 全项目 grep |
| 「`MultimeshSetBuffer` 在 4.7 才有 Span 重载」 | 4.4.1 就有 | §3.2 |

---

## 八、复验方法（改结论前先跑）

```
1) 反射 API 面：%TEMP%\gfbprobe（dotnet 探针）
   dotnet run --project <probe> -c Release -- <GodotSharp.dll> [icall 名]
2) 扫描 API 面：%TEMP%\gfbprobe\scan_api.ps1
   输入 = <引擎源码>/modules/mono/glue/GodotSharp/GodotSharp/Generated/
   输出 = api_marshal.csv
3) 引擎源码位置：D:\Godot\GODOT\Source\godot-master（version.py = 4.5.0-beta）
```
