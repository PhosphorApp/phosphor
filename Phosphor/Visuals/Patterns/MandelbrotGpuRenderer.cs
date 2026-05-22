using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Phosphor;

/// <summary>
/// GPU-accelerated Mandelbrot renderer using Direct3D 11 + HLSL pixel shader.
/// Fully self-contained — owns its D3D device, shader, and WPF D3DImage interop.
/// Designed to be easily removable: only <see cref="MandelbrotPattern"/> references this class.
/// </summary>
internal sealed class MandelbrotGpuRenderer : IDisposable
{
    // ── HLSL shader (embedded to avoid loose file dependencies) ──
    // Uses perturbation theory: reference orbit is precomputed on CPU with arbitrary precision,
    // each pixel computes only its delta from the reference using float. This allows deep zoom
    // far beyond float precision (~1e5 → ~1e13+) because the per-pixel Δc values are tiny.
    private const string ShaderSource = @"
cbuffer Params : register(b0)
{
    float  zoom;         // magnification
    float  maxIter;      // adaptive iteration count
    float  palOffset;    // palette rotation offset (0..1)
    float  brightness;   // 1.0 + audio brightness boost
    float2 resolution;   // pixel dimensions
    int    orbitLength;  // number of valid entries in reference orbit
    float  cosA;         // view rotation cos(angle)
    float  sinA;         // view rotation sin(angle)
    float3 _pad;         // padding to 16-byte alignment
};

Texture1D<float4> paletteTex : register(t0);
StructuredBuffer<float2> refOrbit : register(t1);  // reference orbit Z_n values
SamplerState palSampler : register(s0);

struct VS_OUT { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VS_OUT VS(uint id : SV_VertexID)
{
    VS_OUT o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float4 PS(VS_OUT input) : SV_Target
{
    float2 uv = input.uv;
    float aspect = resolution.x / resolution.y;
    float scale = 3.0 / zoom;

    // Delta c: offset from center in complex plane (tiny value, fits in float).
    // Compute axis-aligned offset, then rotate so the visible bitmap stays
    // axis-aligned (no black corners) while the fractal appears rotated.
    float dx = (uv.x - 0.5) * scale * aspect;
    float dy = (uv.y - 0.5) * scale;
    float dcr = cosA * dx - sinA * dy;
    float dci = sinA * dx + cosA * dy;

    // Delta iteration: dr,di = delta from reference orbit
    float dr = 0, di = 0;
    int mi = min((int)maxIter, orbitLength);
    int i;

    for (i = 0; i < mi; i++)
    {
        float2 Z = refOrbit[i];  // reference Z_n

        // d_{n+1} = 2*Z_n*d_n + d_n^2 + dc
        float newDr = 2.0 * (Z.x * dr - Z.y * di) + dr * dr - di * di + dcr;
        float newDi = 2.0 * (Z.x * di + Z.y * dr) + 2.0 * dr * di + dci;
        dr = newDr;
        di = newDi;

        // Full value = Z_n + delta_n
        float fullR = Z.x + dr;
        float fullI = Z.y + di;
        float mag2 = fullR * fullR + fullI * fullI;

        if (mag2 > 65536.0)
        {
            float log_zn = log(mag2) * 0.5;
            float nu = log(log_zn / log(2.0)) / log(2.0);
            float smoothVal = (float)i + 1.0 - nu;
            // Cyclic palette mapping: every 'iterationsPerCycle' iterations equals
            // one full palette cycle. This avoids washout regardless of zoom depth
            // because palette coverage no longer depends on maxIter.
            // 12 iterations/cycle (was 24) so small iteration deltas — the halos
            // around mini-brot satellites — produce a visible color shift.
            const float iterationsPerCycle = 12.0;
            float t = smoothVal / iterationsPerCycle + palOffset;
            float4 color = paletteTex.SampleLevel(palSampler, frac(t), 0);
            return float4(color.rgb * brightness, 1.0);
        }
    }

    return float4(0, 0, 0, 1);  // inside the set
}
";

    // ── D3D resources ──
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _renderTarget;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _stagingTexture;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11ShaderResourceView? _paletteSrv;
    private ID3D11Texture1D? _paletteTexture;
    private ID3D11SamplerState? _sampler;
    private ID3D11Buffer? _orbitBuffer;
    private ID3D11ShaderResourceView? _orbitSrv;
    private int _orbitLength;

    private WriteableBitmap? _bitmap;
    private int _width;
    private int _height;
    private bool _disposed;
    private bool _initialized;
    private bool _loggedRenderError;

    public ImageSource? ImageSource => _bitmap;
    public bool IsAvailable => _initialized && !_disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuParams
    {
        public float Zoom;
        public float MaxIter;
        public float PalOffset;
        public float Brightness;
        public float ResX;
        public float ResY;
        public int OrbitLength;
        public float CosA;
        public float SinA;
        public float Pad0;
        public float Pad1;
        public float Pad2;
    }

    public bool Initialize(int pixelWidth, int pixelHeight)
    {
        try
        {
            _width = pixelWidth;
            _height = pixelHeight;

            LogGpu("Creating D3D11 device...");
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                out _device,
                out _context);

            if (_device == null || _context == null)
            {
                LogGpu("FAILED: D3D11 device/context is null");
                return false;
            }
            LogGpu($"D3D11 device created. Feature level: {_device.FeatureLevel}");

            // Compile shaders
            LogGpu("Compiling vertex shader...");
            CompileShader(ShaderSource, "VS", "vs_5_0", out var vsBytecode);
            LogGpu("Compiling pixel shader...");
            CompileShader(ShaderSource, "PS", "ps_5_0", out var psBytecode);

            if (vsBytecode == null || psBytecode == null)
            {
                LogGpu($"FAILED: Shader compile failed. VS={vsBytecode != null}, PS={psBytecode != null}");
                return false;
            }
            LogGpu($"Shaders compiled. VS={vsBytecode.Length} bytes, PS={psBytecode.Length} bytes");

            _vertexShader = _device.CreateVertexShader(vsBytecode);
            _pixelShader = _device.CreatePixelShader(psBytecode);

            // Render target (off-screen)
            var rtDesc = new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            };
            _renderTarget = _device.CreateTexture2D(rtDesc);
            _rtv = _device.CreateRenderTargetView(_renderTarget);

            // Staging texture for GPU→CPU readback
            var stagingDesc = new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            };
            _stagingTexture = _device.CreateTexture2D(stagingDesc);
            LogGpu($"Textures created ({_width}x{_height})");

            // Constant buffer (16-byte aligned)
            var cbSize = (uint)((Marshal.SizeOf<GpuParams>() + 15) & ~15);
            var cbDesc = new BufferDescription
            {
                ByteWidth = cbSize,
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            };
            _constantBuffer = _device.CreateBuffer(cbDesc);

            // Palette texture (placeholder)
            UpdatePaletteTexture(new byte[256 * 4], 256);

            // Reference orbit buffer (placeholder — will be populated before first render)
            UpdateReferenceOrbit(new float[2], 1);

            // Sampler
            var samplerDesc = new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
            };
            _sampler = _device.CreateSamplerState(samplerDesc);

            // WriteableBitmap for WPF display
            _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);

            _initialized = true;
            LogGpu("Initialization complete — GPU renderer ready");
            return true;
        }
        catch (Exception ex)
        {
            LogGpu($"FAILED with exception: {ex.Message}");
            Dispose();
            return false;
        }
    }

    private void CompileShader(string source, string entryPoint, string target, out byte[]? bytecode)
    {
        bytecode = null;
        Blob? codeBlob = null;
        Blob? errorBlob = null;
        try
        {
            Compiler.Compile(
                source,
                null!,  // defines
                null!,  // include
                entryPoint,
                null!,  // sourceName
                target,
                ShaderFlags.None,
                EffectFlags.None,
                out codeBlob,
                out errorBlob);

            if (errorBlob != null)
            {
                try
                {
                    var errText = Marshal.PtrToStringAnsi(errorBlob.BufferPointer, (int)(nuint)errorBlob.BufferSize);
                    LogGpu($"Shader compile message ({entryPoint}): {errText?.TrimEnd('\0')}");
                }
                catch { }
                finally { errorBlob.Dispose(); errorBlob = null; }
            }

            if (codeBlob == null)
            {
                LogGpu($"Shader compile returned null blob for {entryPoint}.");
                return;
            }

            var size = (int)(nuint)codeBlob.BufferSize;
            bytecode = new byte[size];
            Marshal.Copy(codeBlob.BufferPointer, bytecode, 0, size);
        }
        catch (Exception ex)
        {
            LogGpu($"Shader compile FAILED for {entryPoint}: {ex.Message}");
            bytecode = null;
        }
        finally
        {
            codeBlob?.Dispose();
            errorBlob?.Dispose();
        }
    }

    private void UpdatePaletteTexture(byte[] bgraData, int entryCount)
    {
        _paletteSrv?.Dispose();
        _paletteTexture?.Dispose();

        var texDesc = new Texture1DDescription
        {
            Width = (uint)entryCount,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        };

        var handle = GCHandle.Alloc(bgraData, GCHandleType.Pinned);
        try
        {
            var initData = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(entryCount * 4));
            _paletteTexture = _device!.CreateTexture1D(texDesc, [initData]);
        }
        finally
        {
            handle.Free();
        }

        _paletteSrv = _device!.CreateShaderResourceView(_paletteTexture);
    }

    public void UpdatePalette(byte[] paletteBgra, int entryCount)
    {
        if (!_initialized || _device == null) return;

        var data = new byte[entryCount * 4];
        Array.Copy(paletteBgra, data, data.Length);
        UpdatePaletteTexture(data, entryCount);
    }

    /// <summary>
    /// Upload the reference orbit data to a GPU StructuredBuffer.
    /// </summary>
    /// <param name="interleavedFloats">Interleaved [Zr0, Zi0, Zr1, Zi1, ...] as float.</param>
    /// <param name="orbitLength">Number of orbit entries (half the array length).</param>
    public void UpdateReferenceOrbit(float[] interleavedFloats, int orbitLength)
    {
        if (!_initialized || _device == null) return;

        _orbitSrv?.Dispose();
        _orbitBuffer?.Dispose();

        _orbitLength = orbitLength;

        var bufDesc = new BufferDescription
        {
            ByteWidth = (uint)(orbitLength * 8), // float2 per entry = 8 bytes
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = 8, // sizeof(float2)
        };

        var handle = GCHandle.Alloc(interleavedFloats, GCHandleType.Pinned);
        try
        {
            var initData = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(orbitLength * 8));
            _orbitBuffer = _device.CreateBuffer(bufDesc, initData);
        }
        finally
        {
            handle.Free();
        }

        _orbitSrv = _device.CreateShaderResourceView(_orbitBuffer);
    }

    public void RenderFrame(double centerRe, double centerIm, double zoom,
                            int maxIter, double paletteOffset, double brightness,
                            double viewAngle)
    {
        if (!_initialized || _device == null || _context == null || _bitmap == null) return;

        // Update constant buffer
        var cbData = new GpuParams
        {
            Zoom = (float)zoom,
            MaxIter = maxIter,
            PalOffset = (float)paletteOffset,  // already normalized [0,1) by caller
            Brightness = (float)brightness,
            ResX = _width,
            ResY = _height,
            OrbitLength = _orbitLength,
            CosA = (float)Math.Cos(viewAngle),
            SinA = (float)Math.Sin(viewAngle),
            Pad0 = 0,
            Pad1 = 0,
            Pad2 = 0,
        };

        var mapped = _context.Map(_constantBuffer!, MapMode.WriteDiscard);
        Marshal.StructureToPtr(cbData, mapped.DataPointer, false);
        _context.Unmap(_constantBuffer!, 0);

        // Set pipeline
        if (_rtv == null || _paletteSrv == null) return;
        _context.RSSetViewport(0, 0, _width, _height);
        _context.OMSetRenderTargets(_rtv);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShaderResource(0, _paletteSrv);
        if (_orbitSrv != null)
            _context.PSSetShaderResource(1, _orbitSrv);
        _context.PSSetSampler(0, _sampler);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // Draw full-screen triangle
        _context.Draw(3, 0);

        // Copy render target → staging texture, then read back to WriteableBitmap
        _context.CopyResource(_stagingTexture!, _renderTarget!);
        _context.Flush();

        try
        {
            var readMap = _context.Map(_stagingTexture!, 0, MapMode.Read);
            try
            {
                _bitmap!.Lock();
                unsafe
                {
                    var srcPtr = (byte*)readMap.DataPointer;
                    var dstPtr = (byte*)_bitmap.BackBuffer;
                    int dstStride = _bitmap.BackBufferStride;
                    int rowBytes = _width * 4;
                    for (int y = 0; y < _height; y++)
                    {
                        Buffer.MemoryCopy(
                            srcPtr + y * (long)readMap.RowPitch,
                            dstPtr + y * (long)dstStride,
                            dstStride, rowBytes);
                    }
                }
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
                _bitmap.Unlock();
            }
            catch
            {
                // Ensure bitmap is unlocked even on failure
                try { _bitmap!.Unlock(); } catch { }
                throw;
            }
            finally
            {
                _context.Unmap(_stagingTexture!, 0);
            }
        }
        catch (Exception ex)
        {
            if (!_loggedRenderError)
            {
                LogGpu($"Readback/present FAILED: {ex.Message}");
                _loggedRenderError = true;
            }
        }
    }

    private static void LogGpu(string message)
    {
        var msg = $"[MandelbrotGPU] {message}";
        System.Diagnostics.Debug.WriteLine(msg);
        DebugLog.Log("MandelbrotGPU", message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initialized = false;

        _sampler?.Dispose();
        _orbitSrv?.Dispose();
        _orbitBuffer?.Dispose();
        _paletteSrv?.Dispose();
        _paletteTexture?.Dispose();
        _constantBuffer?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _rtv?.Dispose();
        _stagingTexture?.Dispose();
        _renderTarget?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _bitmap = null;
    }
}
