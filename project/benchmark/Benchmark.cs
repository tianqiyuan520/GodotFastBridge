using System;
using System.Diagnostics;
using System.Text;
using Godot;
using GodotFastBridge;

/// <summary>
/// GodotFastBridge A/B 基准：同一份 payload，C# 原生路径 vs GFB 桥路径，覆盖全部已实现后端。
/// 输入方向：SSBO / MultiMesh（含物理插值）/ RD 纹理 / Image(SetData+ImageTexture.Update)
/// 输出方向：SSBO 回读 / MultiMesh 回读 / RD 纹理回读 / Image 回读
/// 结果同时打印到 stdout 并写入 user://gfb_bench.txt（导出运行时便于取回）。
/// </summary>
public partial class Benchmark : Node
{
	private const int FloatsPerInstance2D = 8;

	[Export] public int InstanceCount { get; set; } = 30000;
	[Export] public int Iterations { get; set; } = 200;
	[Export] public int WarmupFrames { get; set; } = 10;
	[Export] public int TexWidth { get; set; } = 1024;
	[Export] public int TexHeight { get; set; } = 512;

	private RenderingDevice _rd;
	private readonly StringBuilder _log = new();

	private Rid _ssbo;
	private int _ssboBytes;
	private byte[] _ssboData;
	private float[] _ssboFloats;
	private GfbSlot _ssboSlot;

	private MultiMesh _multiMesh;
	private int _mmBytes;
	private float[] _mmFloats;
	private GfbSlot _mmSlot;

	private Rid _rdTexture;
	private int _texBytes;
	private byte[] _texData;
	private GfbSlot _texSlot;

	private Image _image;
	private ImageTexture _imageTexture;
	private int _imgBytes;
	private byte[] _imgData;
	private GfbSlot _imgSlot;

	private int _frame;
	private bool _done;

	public override void _Ready()
	{
		_rd = RenderingServer.GetRenderingDevice();
		if (_rd == null)
		{
			GD.PrintErr("[GFB-Bench] 无 RenderingDevice —— 本基准需要真实渲染器。");
			GetTree().Quit();
			return;
		}

		FastBridge.Initialize(this);

		// --- SSBO（僵尸 RenderingManager.cs:408 的形态：12 floats/实例）---
		_ssboBytes = InstanceCount * 12 * sizeof(float);
		_ssboFloats = new float[InstanceCount * 12];
		for (int i = 0; i < _ssboFloats.Length; i++)
		{
			_ssboFloats[i] = i * 0.25f;
		}
		_ssboData = new byte[_ssboBytes];
		Buffer.BlockCopy(_ssboFloats, 0, _ssboData, 0, _ssboBytes);
		_ssbo = _rd.StorageBufferCreate((uint)_ssboBytes, (byte[])null);
		_ssboSlot = FastBridge.ConfigureStorageBuffer(_ssbo, _ssboBytes);

		// --- MultiMesh（2D，8 floats/实例）---
		_multiMesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			InstanceCount = InstanceCount,
		};
		_mmBytes = InstanceCount * FloatsPerInstance2D * sizeof(float);
		_mmFloats = new float[InstanceCount * FloatsPerInstance2D];
		for (int i = 0; i < InstanceCount; i++)
		{
			int b = i * FloatsPerInstance2D;
			_mmFloats[b] = 1f;
			_mmFloats[b + 3] = i % 100;
			_mmFloats[b + 5] = 1f;
			_mmFloats[b + 7] = i / 100;
		}
		_mmSlot = FastBridge.ConfigureMultiMesh(_multiMesh.GetRid(), InstanceCount, FloatsPerInstance2D);

		// --- RD 纹理（CSBS 位置纹理的形态：R32G32_SFLOAT）---
		_texBytes = TexWidth * TexHeight * 2 * sizeof(float);
		_texData = Fill(_texBytes, 7);
		var fmt = new RDTextureFormat
		{
			Format = RenderingDevice.DataFormat.R32G32Sfloat,
			Width = (uint)TexWidth,
			Height = (uint)TexHeight,
			Depth = 1,
			ArrayLayers = 1,
			Mipmaps = 1,
			TextureType = RenderingDevice.TextureType.Type2D,
			Samples = RenderingDevice.TextureSamples.Samples1,
			UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
						RenderingDevice.TextureUsageBits.CanUpdateBit |
						RenderingDevice.TextureUsageBits.CanCopyFromBit,
		};
		_rdTexture = _rd.TextureCreate(fmt, new RDTextureView(),
			new Godot.Collections.Array<byte[]> { new byte[_texBytes] });
		if (!_rdTexture.IsValid)
		{
			GD.PrintErr("[GFB-Bench] RD 纹理创建失败。");
			GetTree().Quit();
			return;
		}
		_texSlot = FastBridge.ConfigureTexture(_rdTexture, _texBytes);

		// --- Image + ImageTexture（TerritorialWar BallRenderingManager.cs:93-94 的形态）---
		_imgBytes = TexWidth * TexHeight * 2 * sizeof(float);
		_imgData = Fill(_imgBytes, 13);
		_image = Image.CreateFromData(TexWidth, TexHeight, false, Image.Format.Rgf, new byte[_imgBytes]);
		_imageTexture = ImageTexture.CreateFromImage(_image);
		_imgSlot = FastBridge.ConfigureImage(_image, _imageTexture, TexWidth, TexHeight, (int)Image.Format.Rgf);

		Emit($"[GFB-Bench] engine={Engine.GetVersionInfo()["string"]} debug_build={OS.HasFeature("debug")} " +
			 $"device={_rd.GetDeviceName()}");
		Emit($"[GFB-Bench] instances={InstanceCount} iterations={Iterations} SSBO={_ssboBytes / 1024}KB  " +
			 $"MultiMesh={_mmBytes / 1024}KB  RDTexture={_texBytes / 1024}KB  Image={_imgBytes / 1024}KB");
	}

	private static byte[] Fill(int size, int seed)
	{
		var b = new byte[size];
		var rnd = new Random(seed);
		rnd.NextBytes(b);
		return b;
	}

	public override void _Process(double delta)
	{
		if (_done)
		{
			return;
		}
		if (++_frame < WarmupFrames)
		{
			return;
		}
		_done = true;
		Run();
		Flush();
		GetTree().Quit();
	}

	private unsafe void Run()
	{
		Emit("============ GodotFastBridge A/B （IN：提交） ============");
		Emit($"{"用例",-32}{"C# 直接",12}{"C# + 拷贝",12}{"GFB 桥",12}{"vs直接",8}{"vs拷贝",8}");

		double ssboPlain = Measure(() => _rd.BufferUpdate(_ssbo, 0, (uint)_ssboBytes, _ssboData));
		double ssboCopy = Measure(() =>
		{
			byte[] tmp = new byte[_ssboBytes];
			Buffer.BlockCopy(_ssboFloats, 0, tmp, 0, _ssboBytes);
			_rd.BufferUpdate(_ssbo, 0, (uint)_ssboBytes, tmp);
		});
		double ssboGfb = Measure(() =>
		{
			fixed (byte* p = _ssboData)
			{
				FastBridge.Submit(_ssboSlot, p, _ssboBytes, out _, out _);
			}
		});
		InRow($"SSBO {_ssboBytes / 1024}KB", ssboPlain, ssboCopy, ssboGfb);

		double mmPlain = Measure(() => RenderingServer.MultimeshSetBuffer(_multiMesh.GetRid(), _mmFloats));
		double mmCopy = Measure(() =>
		{
			float[] tmp = new float[_mmFloats.Length];
			Array.Copy(_mmFloats, tmp, _mmFloats.Length);
			RenderingServer.MultimeshSetBuffer(_multiMesh.GetRid(), tmp);
		});
		double mmGfb = Measure(() =>
		{
			fixed (float* p = _mmFloats)
			{
				FastBridge.Submit(_mmSlot, p, _mmBytes, out _, out _);
			}
		});
		InRow($"MultiMesh {_mmBytes / 1024}KB", mmPlain, mmCopy, mmGfb);

		double texPlain = Measure(() => _rd.TextureUpdate(_rdTexture, 0, _texData));
		double texGfb = Measure(() =>
		{
			fixed (byte* p = _texData)
			{
				FastBridge.Submit(_texSlot, p, _texBytes, out _, out _);
			}
		});
		InRow($"RDTexture {_texBytes / 1024}KB", texPlain, texPlain, texGfb);

		double imgPlain = Measure(() =>
		{
			_image.SetData(TexWidth, TexHeight, false, Image.Format.Rgf, _imgData);
			_imageTexture.Update(_image);
		});
		double imgGfb = Measure(() =>
		{
			fixed (byte* p = _imgData)
			{
				FastBridge.Submit(_imgSlot, p, _imgBytes, out _, out _);
			}
		});
		InRow($"Image {_imgBytes / 1024}KB (SetData+Update)", imgPlain, imgPlain, imgGfb);

		Emit("============ GodotFastBridge A/B （OUT：回读） ==========");
		Emit($"{"用例",-32}{"C# 回读",12}{"GFB read_into",14}{"加速",8}   {"托管分配 C#→GFB",20}");

		byte[] readback = new byte[Math.Max(_ssboBytes, Math.Max(_texBytes, _imgBytes))];
		fixed (byte* d = readback)
		{
			double ssboReadPlain = Measure(() => { var x = _rd.BufferGetData(_ssbo, 0, (uint)_ssboBytes); _ = x.Length; });
			double ssboReadGfb = TimeRead(_ssboSlot, d, _ssboBytes);
			long a1 = MeasureAlloc(() => { var x = _rd.BufferGetData(_ssbo, 0, (uint)_ssboBytes); _ = x.Length; });
			long a2 = AllocRead(_ssboSlot, d, _ssboBytes);
			OutRow($"SSBO {_ssboBytes / 1024}KB", ssboReadPlain, ssboReadGfb, a1, a2);

			double mmReadPlain = Measure(() => { var x = RenderingServer.MultimeshGetBuffer(_multiMesh.GetRid()); _ = x.Length; });
			double mmReadGfb = TimeRead(_mmSlot, d, _mmBytes);
			long b1 = MeasureAlloc(() => { var x = RenderingServer.MultimeshGetBuffer(_multiMesh.GetRid()); _ = x.Length; });
			long b2 = AllocRead(_mmSlot, d, _mmBytes);
			OutRow($"MultiMesh {_mmBytes / 1024}KB", mmReadPlain, mmReadGfb, b1, b2);

			double texReadPlain = Measure(() => { var x = _rd.TextureGetData(_rdTexture, 0); _ = x.Length; });
			double texReadGfb = TimeRead(_texSlot, d, _texBytes);
			long c1 = MeasureAlloc(() => { var x = _rd.TextureGetData(_rdTexture, 0); _ = x.Length; });
			long c2 = AllocRead(_texSlot, d, _texBytes);
			OutRow($"RDTexture {_texBytes / 1024}KB", texReadPlain, texReadGfb, c1, c2);

			double imgReadPlain = Measure(() => { var x = _image.GetData(); _ = x.Length; });
			double imgReadGfb = TimeRead(_imgSlot, d, _imgBytes);
			long e1 = MeasureAlloc(() => { var x = _image.GetData(); _ = x.Length; });
			long e2 = AllocRead(_imgSlot, d, _imgBytes);
			OutRow($"Image {_imgBytes / 1024}KB", imgReadPlain, imgReadGfb, e1, e2);
		}

		Emit("============ 正确性（逐字节） ============");
		Emit($"SSBO={VerifyInto(_ssboSlot, _ssboBytes, _ssboData)}  MultiMesh={VerifyInto(_mmSlot, _mmBytes, FloatsToBytes(_mmFloats))}  " +
			 $"RDTexture={VerifyInto(_texSlot, _texBytes, _texData)}  Image={VerifyInto(_imgSlot, _imgBytes, _imgData)}");
		Emit("========================================");
	}

	private static unsafe double TimeRead(GfbSlot slot, byte* dst, int bytes)
	{
		for (int i = 0; i < 20; i++)
		{
			FastBridge.ReadInto(slot, dst, 0, bytes);
		}
		double best = double.MaxValue;
		for (int i = 0; i < 200; i++)
		{
			long t0 = Stopwatch.GetTimestamp();
			FastBridge.ReadInto(slot, dst, 0, bytes);
			double us = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
			if (us < best)
			{
				best = us;
			}
		}
		return best;
	}

	private static unsafe long AllocRead(GfbSlot slot, byte* dst, int bytes)
	{
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 10; i++)
		{
			FastBridge.ReadInto(slot, dst, 0, bytes);
		}
		return (GC.GetAllocatedBytesForCurrentThread() - before) / 10;
	}

	private static byte[] FloatsToBytes(float[] src)
	{
		var b = new byte[src.Length * sizeof(float)];
		Buffer.BlockCopy(src, 0, b, 0, b.Length);
		return b;
	}

	private unsafe bool VerifyInto(GfbSlot slot, int bytes, byte[] expected)
	{
		byte[] got = new byte[bytes];
		bool ok;
		fixed (byte* d = got)
		{
			ok = FastBridge.ReadInto(slot, d, 0, bytes) == bytes;
		}
		return ok && Same(got, expected);
	}

	private void InRow(string name, double plain, double copy, double gfb)
	{
		Emit($"{name,-32}{plain,10:F1}us{copy,10:F1}us{gfb,10:F1}us{plain / gfb,7:F2}x{copy / gfb,7:F2}x");
	}

	private void OutRow(string name, double plain, double gfb, long allocPlain, long allocGfb)
	{
		Emit($"{name,-32}{plain,10:F1}us{gfb,12:F1}us{plain / gfb,7:F2}x   {allocPlain} B → {allocGfb} B");
	}

	private double Measure(Action body)
	{
		for (int i = 0; i < 20; i++)
		{
			body();
		}
		double best = double.MaxValue;
		for (int i = 0; i < Iterations; i++)
		{
			long t0 = Stopwatch.GetTimestamp();
			body();
			long t1 = Stopwatch.GetTimestamp();
			double us = (t1 - t0) * 1_000_000.0 / Stopwatch.Frequency;
			if (us < best)
			{
				best = us;
			}
		}
		return best;
	}

	private static long MeasureAlloc(Action body)
	{
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 10; i++)
		{
			body();
		}
		return (GC.GetAllocatedBytesForCurrentThread() - before) / 10;
	}

	private static bool Same(byte[] a, byte[] b)
	{
		if (a.Length != b.Length)
		{
			GD.Print($"[verify] 长度不一致: {a.Length} != {b.Length}");
			return false;
		}
		for (int i = 0; i < a.Length; i++)
		{
			if (a[i] != b[i])
			{
				GD.Print($"[verify] 首处不一致 at {i}: {a[i]} != {b[i]}");
				return false;
			}
		}
		return true;
	}

	private void Emit(string line)
	{
		_log.AppendLine(line);
		GD.Print(line);
	}

	private void Flush()
	{
		using var f = FileAccess.Open("user://gfb_bench.txt", FileAccess.ModeFlags.Write);
		if (f != null)
		{
			f.StoreString(_log.ToString());
			GD.Print($"[GFB-Bench] 结果已写入 {ProjectSettings.GlobalizePath("user://gfb_bench.txt")}");
		}
	}

	public override void _ExitTree()
	{
		if (_rd == null)
		{
			return;
		}
		if (_ssbo.IsValid)
		{
			_rd.FreeRid(_ssbo);
		}
		if (_rdTexture.IsValid)
		{
			_rd.FreeRid(_rdTexture);
		}
	}
}
