using SkiaSharp;
using System;
using System.Diagnostics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace AnimationImage
{
    /// <summary>
    /// 基于 Silk.NET.Direct3D12 + Silk.NET.DXGI 的 Windows GPU 后端。
    /// 初始化流程：创建 FL12_0 设备 → 按 LUID 反查物理适配器 → 创建 Direct 命令队列
    ///          → 组装 GRD3DBackendContext → 建立 Skia GRContext 与 GPU SKSurface。
    /// </summary>
    internal sealed unsafe class D3D12Backend : IGpuBackend
    {
        private D3D12? _d3d12;
        private DXGI? _dxgi;
        private ComPtr<ID3D12Device> _device;
        private ComPtr<IDXGIAdapter1> _adapter;
        private ComPtr<ID3D12CommandQueue> _commandQueue;

        // Skia GRD3DBackendContext 对 Device/Adapter/Queue 采用 adopts 语义（接管不 AddRef），
        // CreateDirect3D 成功后三个 native 对象完全交给 Skia 管理；Context.Dispose() 时 Skia 会 Release 它们。
        // 这个 flag = true 时表示“已交给 Skia”，DisposeInternal 不再释放三个 ComPtr。
        private bool _handedToSkia;

        public GRContext? Context { get; private set; }
        public SKSurface? Surface { get; private set; }

        public bool TryInitialize(SKImageInfo info)
        {
            ComPtr<IDXGIFactory4> factory = default;
            try
            {
                _d3d12 = D3D12.GetApi();

                // 1) 创建 D3D12 Device（FeatureLevel 12_0，Adapter=null 让运行时自选默认适配器）
                var hrDevice = _d3d12.CreateDevice(
                    (ComPtr<IUnknown>)default,
                    D3DFeatureLevel.Level120,
                    out _device);
                if (HResult.IndicatesFailure(hrDevice) || _device.Handle == null)
                    return false;

                // 2) 通过设备 LUID 反查 IDXGIAdapter1
                _dxgi = DXGI.GetApi(null, false);
                var hrFactory = _dxgi.CreateDXGIFactory1(out factory);
                if (HResult.IndicatesFailure(hrFactory) || factory.Handle == null)
                    return false;

                Luid luid = _device.GetAdapterLuid();
                var hrAdapter = factory.EnumAdapterByLuid(luid, out _adapter);
                if (HResult.IndicatesFailure(hrAdapter) || _adapter.Handle == null)
                    return false;

                // 3) 创建 Direct 类型命令队列
                var queueDesc = new CommandQueueDesc
                {
                    Type = CommandListType.Direct,
                    Priority = (int)CommandQueuePriority.Normal,
                    Flags = CommandQueueFlags.None,
                    NodeMask = 0,
                };
                var hrQueue = _device.CreateCommandQueue(
                    in queueDesc,
                    out _commandQueue);
                if (HResult.IndicatesFailure(hrQueue) || _commandQueue.Handle == null)
                    return false;

                // 4) 组装 Skia D3D 后端上下文，建立 GRContext 与 SKSurface
                var backendContext = new GRD3DBackendContext()
                {
                    Device = (IntPtr)_device.Handle,
                    Adapter = (IntPtr)_adapter.Handle,
                    Queue = (IntPtr)_commandQueue.Handle,
                };

                Context = GRContext.CreateDirect3D(backendContext);
                if (Context == null)
                    return false;

                // 三个 native 对象此时完全属于 Skia，后续释放由 Context.Dispose() 代为完成。
                _handedToSkia = true;

                Surface = SKSurface.Create(Context, false, info);
                return Surface != null;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} D3D12 GPU 初始化失败：{e.Message}");
                DisposeInternal();
                return false;
            }
            finally
            {
                // factory 仅作为中间对象查适配器，无需与设备同生命周期
                factory.Dispose();
            }
        }

        public void Resize(SKImageInfo info)
        {
            Surface?.Dispose();
            Surface = null;
            if (Context == null)
                return;

            Surface = SKSurface.Create(Context, false, info);
            if (Surface == null)
            {
                Debug.WriteLine("GPU Surface 重建失败，释放 GPU 资源");
                DisposeInternal();
            }
        }

        public void Dispose() => DisposeInternal();

        private void DisposeInternal()
        {
            // Skia GRContext.Dispose 会 Release 它接管的 Device/Adapter/Queue。
            Surface?.Dispose();
            Surface = null;

            Context?.Dispose();
            Context = null;

            // 只有当初始化未成功交给 Skia（_handedToSkia == false）时，
            // 才需要自己 Release 这三个 ComPtr；否则 double-Release 会引发 0xC0000409。
            if (!_handedToSkia)
            {
                try { _commandQueue.Dispose(); } catch { }
                try { _adapter.Dispose(); } catch { }
                try { _device.Dispose(); } catch { }
            }

            _dxgi?.Dispose();
            _dxgi = null;
            _d3d12?.Dispose();
            _d3d12 = null;
        }
    }
}
