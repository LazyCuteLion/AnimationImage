using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D11on12;
using Windows.Win32.Graphics.Direct3D12;
using WinRT;

// 消歧义：Silk.NET / CsWin32 存在同名类型
using SilkID3D12Resource = Silk.NET.Direct3D12.ID3D12Resource;
using CsID3D11Device = Windows.Win32.Graphics.Direct3D11.ID3D11Device;
using CsID3D11DeviceContext = Windows.Win32.Graphics.Direct3D11.ID3D11DeviceContext;
using CsID3D11Resource = Windows.Win32.Graphics.Direct3D11.ID3D11Resource;
using CsID3D11On12Device = Windows.Win32.Graphics.Direct3D11on12.ID3D11On12Device;
using CsIDXGIDevice = Windows.Win32.Graphics.Dxgi.IDXGIDevice;
using CsIDXGISurface = Windows.Win32.Graphics.Dxgi.IDXGISurface;
using GfxIDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;
using GfxIDirect3DSurface = Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface;
using SilkAdapter = Silk.NET.DXGI.IDXGIAdapter;

namespace AnimationImage
{
    /// <summary>
    /// <see cref="CompositionHost"/> 的 GPU 直渲分部：
    /// Skia 在 D3D12 上渲染到共享纹理，D3D11On12 无拷贝地把该资源暴露为 D3D11 纹理，
    /// Win2D 再把它 blit 到 <see cref="CompositionDrawingSurface"/>。全程零 CPU raster / 零 CPU 拷贝。
    /// </summary>
    internal sealed unsafe partial class CompositionHost
    {
        #region D3D12 (Silk.NET)

        private D3D12? _d3d12Api;
        private DXGI? _dxgiApi;
        private ComPtr<ID3D12Device> _device12;
        private ComPtr<IDXGIAdapter1> _adapter;
        private ComPtr<ID3D12CommandQueue> _queue12;
        private ComPtr<SilkID3D12Resource> _res12;

        #endregion

        #region D3D11On12 (CsWin32)

        private CsID3D11Device? _device11;
        private CsID3D11DeviceContext? _context11;
        private CsID3D11On12Device? _on12;
        private CsID3D11Resource? _res11;

        #endregion

        #region Skia / Win2D

        private GRContext? _grContext;
        private SKSurface? _skSurface;
        private CanvasBitmap? _canvasBitmap;
        private CanvasRenderTarget? _canvasRT;

        #endregion

        // 缓存 Acquire/Release 数组，避免每帧 GC 分配
        private readonly CsID3D11Resource[] _acquireArr = new CsID3D11Resource[1];
        private bool _gpuInitialized;

        /// <summary>Tier 2 模式：D3D12+Skia GPU 光栅化可用，但 D3D11On12/Win2D 桥接失败，走 ReadPixels + CPU Present。</summary>
        private bool _gpuTier2;

        /// <summary>
        /// 创建 GPU host，内部三级降级：
        /// <list type="number">
        ///   <item>Tier 1 — D3D12 + D3D11On12 + Win2D 零拷贝直渲到 Composition；</item>
        ///   <item>Tier 2 — D3D12 + Skia GPU 光栅 → ReadPixels 到 BackBuffer → CPU Present 到 Composition；</item>
        ///   <item>返回 null — 上层回退到纯 CPU 光栅化。</item>
        /// </list>
        /// </summary>
        internal static CompositionHost? TryCreateGpu(Compositor compositor, int width, int height)
        {
            var host = new CompositionHost(compositor);
            try
            {
                // D3D12 基础设施（失败 → GPU 完全不可用）
                if (!host.InitD3D12()) { Log("InitD3D12 failed"); host.Dispose(); return null; }

                // Skia D3D12 后端（只依赖 D3D12，不依赖 D3D11On12）
                if (!host.InitSkia()) { Log("InitSkia failed"); host.Dispose(); return null; }

                host._pixelW = Math.Max(1, width);
                host._pixelH = Math.Max(1, height);
                host._gpuInitialized = true;

                // 尝试完整零拷贝管线（D3D11On12 + Win2D 桥接）
                if (host.InitD3D11On12() && host.InitWin2DAndComposition())
                {
                    // Tier 1：零拷贝直渲
                    host.InitCompositionSkeleton(host._pixelW, host._pixelH);
                    host.CreateSizedGpuResources(host._pixelW, host._pixelH);
                    Log("Init ok (Tier 1: zero-copy)");
                    return host;
                }

                // Tier 2：桥接失败，退而使用 GPU 光栅 + ReadPixels + CPU Present
                Log("Bridge failed, falling back to Tier 2 (GPU rasterize + ReadPixels)");
                host.CleanupBridgeResources();
                host._gpuTier2 = true;

                // CPU 呈现基础设施（共享 CanvasDevice + BackBuffer）
                host._canvasDevice = CanvasDevice.GetSharedDevice();
                host._graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(host._compositor, host._canvasDevice);
                host.InitCompositionSkeleton(host._pixelW, host._pixelH);
                host.EnsureBackBuffer(host._pixelW, host._pixelH);

                // 普通 GPU SKSurface（用于 GPU 光栅化后 ReadPixels 到 BackBuffer）
                var skInfo = new SKImageInfo(host._pixelW, host._pixelH, SKColorType.Bgra8888, SKAlphaType.Premul);
                host._skSurface = SKSurface.Create(host._grContext!, false, skInfo);
                if (host._skSurface == null)
                {
                    Log("Tier 2 SKSurface creation failed");
                    host.Dispose();
                    return null;
                }

                Log("Init ok (Tier 2: GPU rasterize + ReadPixels)");
                return host;
            }
            catch (Exception ex)
            {
                Log($"初始化异常 ({ex.GetType().Name}): {ex.Message}");
                host.Dispose();
                return null;
            }
        }

        private static void Log(string msg)
            => Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} CompositionHost.Gpu {msg}");

        #region Init

        private bool InitD3D12()
        {
            _d3d12Api = D3D12.GetApi();
            ComPtr<SilkAdapter> nullAdapter = default;
            var hr = _d3d12Api.CreateDevice(nullAdapter, D3DFeatureLevel.Level120, out _device12);
            if (HResult.IndicatesFailure(hr) || _device12.Handle == null) return false;

            _dxgiApi = DXGI.GetApi(null, false);
            ComPtr<IDXGIFactory4> factory = default;
            try
            {
                hr = _dxgiApi.CreateDXGIFactory1(out factory);
                if (HResult.IndicatesFailure(hr) || factory.Handle == null) return false;
                Luid luid = _device12.GetAdapterLuid();
                hr = factory.EnumAdapterByLuid(luid, out _adapter);
                if (HResult.IndicatesFailure(hr) || _adapter.Handle == null) return false;
            }
            finally { factory.Dispose(); }

            var qDesc = new CommandQueueDesc
            {
                Type = CommandListType.Direct,
                Priority = (int)CommandQueuePriority.Normal,
                Flags = CommandQueueFlags.None,
                NodeMask = 0,
            };
            hr = _device12.CreateCommandQueue(in qDesc, out _queue12);
            return HResult.IndicatesSuccess(hr) && _queue12.Handle != null;
        }

        private bool InitD3D11On12()
        {
            //return false;
            // Silk.NET COM 指针 → object（IUnknown marshaling）
            object d3d12DeviceObj = Marshal.GetObjectForIUnknown((IntPtr)_device12.Handle);
            object d3d12QueueObj = Marshal.GetObjectForIUnknown((IntPtr)_queue12.Handle);
            var queues = new object[] { d3d12QueueObj };

            try
            {
                var hr = WinPInvoke.D3D11On12CreateDevice(
                    d3d12DeviceObj,
                    (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    null, 0,
                    queues, 1, 0,
                    out _device11!, out _context11!, null);
                if (hr.Failed || _device11 == null) return false;
                _on12 = (CsID3D11On12Device)_device11;
                return _on12 != null;
            }
            finally
            {
                // GetObjectForIUnknown 递增了引用计数；用完释放 RCW 避免泄漏
                Marshal.ReleaseComObject(d3d12QueueObj);
                Marshal.ReleaseComObject(d3d12DeviceObj);
            }
        }

        private bool InitSkia()
        {
            var backend = new GRD3DBackendContext
            {
                Adapter = (IntPtr)_adapter.Handle,
                Device = (IntPtr)_device12.Handle,
                Queue = (IntPtr)_queue12.Handle,
            };
            _grContext = GRContext.CreateDirect3D(backend);
            return _grContext != null;
        }

        private bool InitWin2DAndComposition()
        {
            if (_device11 == null) return false;

            // WinRT projection 中间对象需释放（CanvasDevice 内部会 AddRef 存引用）
            using var direct3dDevice = CreateDirect3DDevice(_device11);
            _canvasDevice = CanvasDevice.CreateFromDirect3D11Device(direct3dDevice);
            _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(_compositor, _canvasDevice);
            return true;
        }

        #endregion

        #region Resize / Render

        partial void ResizeGpu(int width, int height)
        {
            if (!_gpuInitialized) return;
            Log($"Resize {width}x{height}");

            if (_gpuTier2)
            {
                // Tier 2：重建 GPU SKSurface + BackBuffer
                try { _skSurface?.Dispose(); } catch { }
                _skSurface = null;

                var skInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                _skSurface = SKSurface.Create(_grContext!, false, skInfo);

                _cache?.Dispose();
                _cache = null;
                EnsureBackBuffer(width, height);
                return;
            }

            // Tier 1
            DisposeSizedGpuResources();
            CreateSizedGpuResources(width, height);
        }

        private void CreateSizedGpuResources(int width, int height)
        {
            if (!CreateSharedResource(width, height)) { Log("CreateSharedResource FAIL"); return; }
            if (!CreateSkSurface(width, height)) { Log("CreateSkSurface FAIL"); return; }
            if (!CreateCanvasBitmap()) { Log("CreateCanvasBitmap FAIL"); return; }

            // 中转用 CanvasRenderTarget（D2D-native）：D2D 无法直接对 D3D11On12 wrapped texture 采样，
            // 每帧走 D2D CopyFromBitmap（GPU→GPU）中转。
            _canvasRT = new CanvasRenderTarget(
                _canvasDevice!, width, height, 96,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                CanvasAlphaMode.Premultiplied);
        }

        private bool CreateSharedResource(int width, int height)
        {
            // 1) D3D12 端创建共享纹理（Silk.NET）
            var heap = new HeapProperties(HeapType.Default);
            var desc = new ResourceDesc
            {
                Dimension = ResourceDimension.Texture2D,
                Alignment = 0,
                Width = (ulong)width,
                Height = (uint)height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = Format.FormatB8G8R8A8Unorm,
                SampleDesc = new SampleDesc(1, 0),
                Layout = TextureLayout.LayoutUnknown,
                Flags = ResourceFlags.AllowRenderTarget,
            };
            var clear = new ClearValue { Format = Format.FormatB8G8R8A8Unorm };
            clear.Anonymous.Color[0] = 0;
            clear.Anonymous.Color[1] = 0;
            clear.Anonymous.Color[2] = 0;
            clear.Anonymous.Color[3] = 0;

            var iid = SilkID3D12Resource.Guid;
            void* pRes;
            var hr = _device12.CreateCommittedResource(
                in heap, HeapFlags.None, in desc,
                ResourceStates.RenderTarget,
                in clear,
                ref iid, &pRes);
            if (HResult.IndicatesFailure(hr) || pRes == null) return false;
            _res12 = new ComPtr<SilkID3D12Resource>((SilkID3D12Resource*)pRes);

            // 2) D3D11On12 无拷贝包装成 D3D11 资源
            if (_on12 == null) return false;

            var flags11 = new D3D11_RESOURCE_FLAGS
            {
                BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
                CPUAccessFlags = 0,
                MiscFlags = 0,
                StructureByteStride = 0,
            };

            object d3d12ResObj = Marshal.GetObjectForIUnknown((IntPtr)_res12.Handle);
            try
            {
                var iidRes11 = typeof(CsID3D11Resource).GUID;
                void* pOut11 = null;
                _on12.CreateWrappedResource(
                    d3d12ResObj, &flags11,
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
                    &iidRes11, &pOut11);
                if (pOut11 == null) return false;

                _res11 = (CsID3D11Resource)Marshal.GetObjectForIUnknown((IntPtr)pOut11);
                Marshal.Release((IntPtr)pOut11);
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(d3d12ResObj);
            }
        }

        private bool CreateSkSurface(int width, int height)
        {
            if (_grContext == null) return false;

            var info = new GRD3DTextureResourceInfo
            {
                Resource = (IntPtr)_res12.Handle,
                ResourceState = (uint)ResourceStates.RenderTarget,
                Format = (uint)Format.FormatB8G8R8A8Unorm,
                SampleCount = 1,
                LevelCount = 1,
                SampleQualityPattern = 0,
                Protected = false,
            };

            using var rt = new GRBackendRenderTarget(width, height, info);
            _skSurface = SKSurface.Create(
                _grContext, rt,
                GRSurfaceOrigin.TopLeft,
                SKColorType.Bgra8888);
            return _skSurface != null;
        }

        // wrap 是 non-copying：底层 D3D11 纹理内容变化会自动反映到 CanvasBitmap，故 Resize 时创建一次即可
        private bool CreateCanvasBitmap()
        {
            if (_canvasDevice == null || _res11 == null) return false;

            using var d3dSurface = CreateDirect3DSurface(_res11);
            _canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(_canvasDevice, d3dSurface);
            return true;
        }

        /// <summary>GPU 渲染：交出 SKCanvas 给上层绘制，内部根据 Tier 完成提交。</summary>
        public void Render(Action<SKCanvas, SKSize> draw)
        {
            if (_disposed) return;
            if (!IsGpu) throw new InvalidOperationException("CPU 模式请写入 PixelAddress 并调用 Present()。");
            if (!_gpuInitialized || _skSurface == null)
            {
                Log($"Render skipped: init={_gpuInitialized} sk={_skSurface != null}");
                return;
            }

            try
            {
                var canvas = _skSurface.Canvas;
                canvas.Clear();
                draw(canvas, new SKSize(_pixelW, _pixelH));

                if (_gpuTier2)
                {
                    // Tier 2：Flush → ReadPixels（内部自带 submit+sync）→ CPU Present
                    // 不走 grContext.Flush()/Submit()，避免双重提交造成额外等待
                    _skSurface.Flush();
                    var info = new SKImageInfo(_pixelW, _pixelH, SKColorType.Bgra8888, SKAlphaType.Premul);
                    _skSurface.ReadPixels(info, _pixelAddress, _pixelW * 4, 0, 0);
                    PresentCore();
                    return;
                }

                // Tier 1：Flush + Submit 后由 D3D11On12 AcquireWrappedResources 完成同步
                _skSurface.Flush();
                _grContext?.Flush();
                _grContext?.Submit(false);

                // Tier 1：零拷贝 Composition 提交
                if (_canvasBitmap == null || _drawingSurface == null || _res11 == null || _on12 == null)
                {
                    Log($"Render skipped (Tier 1): cb={_canvasBitmap != null} ds={_drawingSurface != null} r11={_res11 != null} on12={_on12 != null}");
                    return;
                }

                _acquireArr[0] = _res11;
                _on12.AcquireWrappedResources(_acquireArr, 1);
                try
                {
                    // D2D 无法直接对 D3D11On12 wrapped texture 采样 SRV；
                    // 用 GPU→GPU 拷贝到 D2D-native 的 _canvasRT（走 D2D CopyFromBitmap，不需要 SRV）
                    _canvasRT!.CopyPixelsFromBitmap(_canvasBitmap);

                    using (var session = CanvasComposition.CreateDrawingSession(_drawingSurface))
                    {
                        session.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                        session.DrawImage(_canvasRT);
                    }
                    // Flush 确保 D2D/D3D11 命令先于 D3D11On12 Release 落到 GPU
                    _context11?.Flush();
                }
                finally
                {
                    _on12.ReleaseWrappedResources(_acquireArr, 1);
                }
            }
            catch (Exception ex)
            {
                Log($"Render ex ({ex.GetType().Name}): {ex.Message}");
            }
        }

        #endregion

        /// <summary>释放 D3D11On12 / Win2D 桥接资源（Tier 2 降级时调用）。</summary>
        private void CleanupBridgeResources()
        {
            try { _graphicsDevice?.Dispose(); } catch { }
            _graphicsDevice = null;
            try { _canvasDevice?.Dispose(); } catch { }
            _canvasDevice = null;

            _on12 = null;
            if (_context11 != null) { try { Marshal.ReleaseComObject(_context11); } catch { } _context11 = null; }
            if (_device11 != null) { try { Marshal.ReleaseComObject(_device11); } catch { } _device11 = null; }
        }

        #region Dispose

        private void DisposeSizedGpuResources()
        {
            // 释放 sized 资源前先把 GPU 命令队列走干净
            try { _context11?.Flush(); } catch { }
            try
            {
                _grContext?.Flush();
                _grContext?.Submit(synchronous: true);
            }
            catch { }

            try { _canvasBitmap?.Dispose(); } catch { }
            _canvasBitmap = null;
            try { _canvasRT?.Dispose(); } catch { }
            _canvasRT = null;
            try { _skSurface?.Dispose(); } catch { }
            _skSurface = null;
            if (_res11 != null) { try { Marshal.ReleaseComObject(_res11); } catch { } _res11 = null; }
            try { _res12.Dispose(); } catch { }
            _res12 = default;
        }

        partial void DisposeGpuResources()
        {
            if (!_gpuInitialized && _device12.Handle == null) return;

            try { DisposeSizedGpuResources(); } catch { }

            // Tier 1 的 CanvasDevice / GraphicsDevice 是从 D3D11On12 创建的，由此路径释放
            if (!_gpuTier2)
            {
                try { _graphicsDevice?.Dispose(); } catch { }
                _graphicsDevice = null;
                try { _canvasDevice?.Dispose(); } catch { }
                _canvasDevice = null;
            }

            try { _grContext?.Dispose(); } catch { }
            _grContext = null;

            // _on12 与 _device11 是同一个 RCW（QueryInterface 得到）；只走一次 ReleaseComObject
            _on12 = null;
            if (_context11 != null) { try { Marshal.ReleaseComObject(_context11); } catch { } _context11 = null; }
            if (_device11 != null) { try { Marshal.ReleaseComObject(_device11); } catch { } _device11 = null; }

            try { _queue12.Dispose(); } catch { }
            try { _adapter.Dispose(); } catch { }
            try { _device12.Dispose(); } catch { }

            _d3d12Api?.Dispose(); _d3d12Api = null;
            _dxgiApi?.Dispose(); _dxgiApi = null;

            _gpuInitialized = false;
        }

        #endregion

        #region WinRT 桥：ID3D11Device/Resource → IDirect3DDevice/Surface

        // 手写 P/Invoke：CsWin32 生成的 IUnknown/IInspectable 会走 RCW/CCW 二次封装，
        // 导致 CreateDirect3D11DeviceFromDXGIDevice 收到 CCW 后 QI IDirect3DDevice 抛 ArgumentException。
        // 这里直接用原始 IntPtr 走 native ABI，再用 CsWinRT 的 MarshalInspectable.FromAbi 拿 projection。
        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

        private static GfxIDirect3DDevice CreateDirect3DDevice(CsID3D11Device device11)
        {
            IntPtr dxgiPtr = Marshal.GetComInterfaceForObject(device11, typeof(CsIDXGIDevice));
            try
            {
                int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out IntPtr abi);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                try
                {
                    return MarshalInspectable<GfxIDirect3DDevice>.FromAbi(abi);
                }
                finally { Marshal.Release(abi); }
            }
            finally { Marshal.Release(dxgiPtr); }
        }

        private static GfxIDirect3DSurface CreateDirect3DSurface(CsID3D11Resource resource)
        {
            // 手动 QueryInterface 到 IDXGISurface；避开 Marshal.GetComInterfaceForObject 对 CsWin32 RCW 可能的 QI 错误
            IntPtr punk = Marshal.GetIUnknownForObject(resource);
            try
            {
                Guid iidDxgi = typeof(CsIDXGISurface).GUID;
                int qhr = Marshal.QueryInterface(punk, ref iidDxgi, out IntPtr dxgiPtr);
                if (qhr < 0) Marshal.ThrowExceptionForHR(qhr);
                try
                {
                    int hr = CreateDirect3D11SurfaceFromDXGISurface(dxgiPtr, out IntPtr abi);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                    try
                    {
                        return MarshalInspectable<GfxIDirect3DSurface>.FromAbi(abi);
                    }
                    finally { Marshal.Release(abi); }
                }
                finally { Marshal.Release(dxgiPtr); }
            }
            finally { Marshal.Release(punk); }
        }

        #endregion
    }
}
