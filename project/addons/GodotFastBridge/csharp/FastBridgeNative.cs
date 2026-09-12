using System;
using System.Runtime.InteropServices;

namespace GodotFastBridge
{
    /// <summary>
    /// C ABI 镜像。地址由 GDExtension 侧通过 get_*_address() 回传（Godot 已加载本 DLL，
    /// 不需要 C# 再 LoadLibrary）。热路径全部用 delegate* unmanaged[Cdecl]，无委托分配。
    /// </summary>
    internal static unsafe class FastBridgeNative
    {
        internal const int AbiVersion = 1;

        [StructLayout(LayoutKind.Sequential)]
        internal struct GfbTiming
        {
            public double FillUsec;
            public double SubmitUsec;
        }

        internal static delegate* unmanaged[Cdecl]<IntPtr, int, void*, int, GfbTiming*, void> Submit;
        internal static delegate* unmanaged[Cdecl]<IntPtr, int, void*, int, int, int, GfbTiming*, void> SubmitStrided;
        internal static delegate* unmanaged[Cdecl]<IntPtr, int, void*, int, int, GfbTiming*, int> ReadInto;

        internal static IntPtr Context;

        internal static void Load(long submitAddr, long submitStridedAddr, long readAddr, long context)
        {
            if (submitAddr == 0 || submitStridedAddr == 0 || readAddr == 0 || context == 0)
            {
                throw new InvalidOperationException("GodotFastBridge: 导出地址无效（DLL 与 C# facade 版本不匹配？）。");
            }
            Submit = (delegate* unmanaged[Cdecl]<IntPtr, int, void*, int, GfbTiming*, void>)submitAddr;
            SubmitStrided = (delegate* unmanaged[Cdecl]<IntPtr, int, void*, int, int, int, GfbTiming*, void>)submitStridedAddr;
            ReadInto = (delegate* unmanaged[Cdecl]<IntPtr, int, void*, int, int, GfbTiming*, int>)readAddr;
            Context = (IntPtr)context;
        }
    }
}
