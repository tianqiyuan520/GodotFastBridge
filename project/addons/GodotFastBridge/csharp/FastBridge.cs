using System;
using Godot;

namespace GodotFastBridge
{
    /// <summary>槽位句柄（由 facade 分配，0..15）。</summary>
    public readonly struct GfbSlot
    {
        public readonly int Index;
        internal GfbSlot(int index) { Index = index; }
        public bool IsValid => Index >= 0;
    }

    /// <summary>
    /// GodotFastBridge 的高层封装。用法：
    /// <code>
    /// FastBridge.Initialize(this);                       // 场景里需有名为 FastBridge 的节点
    /// var slot = FastBridge.ConfigureStorageBuffer(rid, bytes);
    /// fixed (byte* p = data) FastBridge.Submit(slot, p, bytes, out double fillMs, out double submitMs);
    /// </code>
    /// Enabled = false 时所有提交/回读变为 no-op，供 A/B 对照走原生路径。
    /// 传入的指针仅在调用期内有效，桥不保留。
    /// </summary>
    public static unsafe class FastBridge
    {
        private const int MaxSlots = 16;

        // 名字缓存：避免每次 Call 都新建 StringName（见 docs/Godot-C#互操作机制.md §五）
        private static readonly StringName NameConfigureTexture = "configure_texture";
        private static readonly StringName NameConfigureStorageBuffer = "configure_storage_buffer";
        private static readonly StringName NameConfigureMultiMesh = "configure_multimesh";
        private static readonly StringName NameConfigureImage = "configure_image";
        private static readonly StringName NameClearSlot = "clear_slot";
        private static readonly StringName NameSetSlotEnabled = "set_slot_enabled";
        private static readonly StringName NameGetSubmitAddress = "get_submit_address";
        private static readonly StringName NameGetSubmitStridedAddress = "get_submit_strided_address";
        private static readonly StringName NameGetReadAddress = "get_read_address";
        private static readonly StringName NameGetContextAddress = "get_context_address";
        private static readonly StringName NameGetAbiVersion = "get_abi_version";
        private static readonly StringName NameGetStats = "get_stats";

        private static GodotObject _bridge;
        private static bool[] _used;
        private static bool _loaded;

        /// <summary>false 时所有提交/回读 no-op（调用方据此走原生路径）。</summary>
        public static bool Enabled { get; set; } = true;

        public static bool IsLoaded => _loaded;

        /// <summary>在 host 下查找名为 <paramref name="nodeName"/> 的 FastBridge 节点并加载函数指针。</summary>
        public static void Initialize(Node host, string nodeName = "FastBridge")
        {
            var node = host.GetNodeOrNull<GodotObject>(nodeName);
            if (node == null)
            {
                throw new InvalidOperationException(
                    $"GodotFastBridge: 场景中找不到名为 '{nodeName}' 的 FastBridge 节点。");
            }
            Initialize(node);
        }

        /// <summary>直接使用已有的 FastBridge 节点。</summary>
        public static void Initialize(GodotObject bridgeNode)
        {
            if (bridgeNode == null)
            {
                throw new ArgumentNullException(nameof(bridgeNode));
            }
            if (!bridgeNode.HasMethod(NameGetAbiVersion))
            {
                throw new InvalidOperationException("GodotFastBridge: 该节点不是 FastBridge（缺少 get_abi_version）。");
            }

            int abi = (int)bridgeNode.Call(NameGetAbiVersion);
            if (abi != FastBridgeNative.AbiVersion)
            {
                throw new InvalidOperationException(
                    $"GodotFastBridge: ABI 版本不匹配（native={abi}, csharp={FastBridgeNative.AbiVersion}）。");
            }

            FastBridgeNative.Load(
                (long)bridgeNode.Call(NameGetSubmitAddress),
                (long)bridgeNode.Call(NameGetSubmitStridedAddress),
                (long)bridgeNode.Call(NameGetReadAddress),
                (long)bridgeNode.Call(NameGetContextAddress));

            _bridge = bridgeNode;
            _used = new bool[MaxSlots];
            _loaded = true;
        }

        // ------------------------------------------------------------ 配置

        public static GfbSlot ConfigureTexture(Rid texture, int byteCapacity)
        {
            int slot = AllocSlot();
            _bridge.Call(NameConfigureTexture, slot, texture, byteCapacity);
            return new GfbSlot(slot);
        }

        public static GfbSlot ConfigureStorageBuffer(Rid buffer, int byteCapacity)
        {
            int slot = AllocSlot();
            _bridge.Call(NameConfigureStorageBuffer, slot, buffer, byteCapacity);
            return new GfbSlot(slot);
        }

        public static GfbSlot ConfigureMultiMesh(Rid multimesh, int instanceCount, int floatsPerInstance)
        {
            int slot = AllocSlot();
            _bridge.Call(NameConfigureMultiMesh, slot, multimesh, instanceCount, floatsPerInstance);
            return new GfbSlot(slot);
        }

        /// <summary>
        /// Image 像素直传槽：容量由 Image 自身尺寸/格式决定；texture 传 null 表示只更新 Image。
        /// </summary>
        public static GfbSlot ConfigureImage(Image image, ImageTexture texture, int width, int height, int format)
        {
            int slot = AllocSlot();
            _bridge.Call(NameConfigureImage, slot, image, texture, width, height, format);
            return new GfbSlot(slot);
        }

        public static void SetSlotEnabled(GfbSlot slot, bool enabled)
        {
            RequireLoaded();
            _bridge.Call(NameSetSlotEnabled, slot.Index, enabled);
        }

        public static void ReleaseSlot(GfbSlot slot)
        {
            RequireLoaded();
            if (!slot.IsValid || slot.Index >= MaxSlots)
            {
                return;
            }
            _bridge.Call(NameClearSlot, slot.Index);
            _used[slot.Index] = false;
        }

        // ------------------------------------------------------------ 提交

        /// <summary>提交 bytes 字节。TEXTURE/MULTIMESH 槽要求 bytes == 配置容量。</summary>
        public static bool Submit(GfbSlot slot, void* src, int bytes, out double fillMs, out double submitMs)
        {
            fillMs = 0.0;
            submitMs = 0.0;
            if (!Enabled || !_loaded || src == null || bytes <= 0)
            {
                return false;
            }
            FastBridgeNative.GfbTiming timing;
            FastBridgeNative.Submit(FastBridgeNative.Context, slot.Index, src, bytes, &timing);
            fillMs = timing.FillUsec / 1000.0;
            submitMs = timing.SubmitUsec / 1000.0;
            return true;
        }

        /// <summary>按 stride 取 count 个元素、每元素 elemBytes 字节，拼成紧凑 payload 后提交（STORAGE_BUFFER 槽）。</summary>
        public static bool SubmitStrided(GfbSlot slot, void* src, int count, int stride, int elemBytes,
            out double fillMs, out double submitMs)
        {
            fillMs = 0.0;
            submitMs = 0.0;
            if (!Enabled || !_loaded || src == null || count <= 0 || elemBytes <= 0 || stride < elemBytes)
            {
                return false;
            }
            FastBridgeNative.GfbTiming timing;
            FastBridgeNative.SubmitStrided(FastBridgeNative.Context, slot.Index, src, count, stride, elemBytes, &timing);
            fillMs = timing.FillUsec / 1000.0;
            submitMs = timing.SubmitUsec / 1000.0;
            return true;
        }

        /// <summary>把槽源读进调用方缓冲，返回写入字节数（&lt;0 表示失败）。</summary>
        public static int ReadInto(GfbSlot slot, void* dst, int offset, int bytes)
        {
            if (!Enabled || !_loaded || dst == null || bytes <= 0)
            {
                return -1;
            }
            FastBridgeNative.GfbTiming timing;
            return FastBridgeNative.ReadInto(FastBridgeNative.Context, slot.Index, dst, offset, bytes, &timing);
        }

        // ------------------------------------------------------------ 观测

        public static Godot.Collections.Dictionary GetStats()
        {
            RequireLoaded();
            return (Godot.Collections.Dictionary)_bridge.Call(NameGetStats);
        }

        // ------------------------------------------------------------ 内部

        private static int AllocSlot()
        {
            RequireLoaded();
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!_used[i])
                {
                    _used[i] = true;
                    return i;
                }
            }
            throw new InvalidOperationException($"GodotFastBridge: 槽位已用尽（上限 {MaxSlots}）。");
        }

        private static void RequireLoaded()
        {
            if (!_loaded)
            {
                throw new InvalidOperationException("GodotFastBridge: 请先调用 FastBridge.Initialize()。");
            }
        }
    }
}
