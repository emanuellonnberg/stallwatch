// Positive control for StallWatch frame-freeze detection: a D3D11 window presenting ~120 fps that
// deliberately stops presenting for `stallMs` every `everySeconds`. Usage: framestall [stallMs=500] [everySeconds=10] [seconds=60]
using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

var stallMs = args.Length > 0 ? int.Parse(args[0]) : 500;
var everySeconds = args.Length > 1 ? int.Parse(args[1]) : 10;
var totalSeconds = args.Length > 2 ? int.Parse(args[2]) : 60;

ApplicationConfiguration.Initialize();
using var form = new Form { Text = "StallWatch framestall", Width = 640, Height = 360 };
form.Show();

D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0],
    out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
var desc = new SwapChainDescription1
{
    Width = 640, Height = 360, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
    BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
    SwapEffect = SwapEffect.FlipDiscard,
};
using var swap = factory.CreateSwapChainForHwnd(device!, form.Handle, desc);
using var back = swap.GetBuffer<ID3D11Texture2D>(0);
using var rtv = device!.CreateRenderTargetView(back);

var sw = Stopwatch.StartNew();
var nextStall = everySeconds;
long frames = 0;
while (sw.Elapsed.TotalSeconds < totalSeconds && !form.IsDisposed)
{
    if (sw.Elapsed.TotalSeconds >= nextStall)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} stalling {stallMs} ms");
        Thread.Sleep(stallMs);
        nextStall += everySeconds;
    }
    var c = (float)(sw.Elapsed.TotalSeconds % 1);
    context!.ClearRenderTargetView(rtv, new Color4(c, 0.2f, 1 - c, 1));
    swap.Present(0, PresentFlags.None);
    frames++;
    Application.DoEvents();
    var due = TimeSpan.FromSeconds(frames / 120.0);
    if (due > sw.Elapsed) Thread.Sleep(due - sw.Elapsed);
}
Console.WriteLine($"{frames} frames in {sw.Elapsed.TotalSeconds:F1} s");
context?.Dispose();
device.Dispose();
