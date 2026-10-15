// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Buffers;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
[ComImport,Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ICaptureInterop { [PreserveSig] int CreateForWindow(nint hwnd,ref Guid iid,out nint result); [PreserveSig] int CreateForMonitor(nint monitor,ref Guid iid,out nint result); }
class Program {
[StructLayout(LayoutKind.Sequential)] struct Rect{public int Left,Top,Right,Bottom;}
[StructLayout(LayoutKind.Sequential)] struct Point{public int X,Y;}
[DllImport("user32.dll")]static extern bool SetProcessDpiAwarenessContext(nint value);
[DllImport("user32.dll")] static extern bool GetClientRect(nint window,out Rect result);
[DllImport("user32.dll")] static extern bool GetWindowRect(nint window,out Rect result);
[DllImport("user32.dll")] static extern bool ClientToScreen(nint window,ref Point point);
[DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint window,int attribute,out Rect result,int size);
[DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint state);
[DllImport("combase.dll")] static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string value,int length,out nint result);
[DllImport("combase.dll")] static extern int WindowsDeleteString(nint value);
[DllImport("combase.dll")] static extern int RoGetActivationFactory(nint name,ref Guid iid,out nint factory);
static GraphicsCaptureItem CaptureWindow(nint hwnd){string name="Windows.Graphics.Capture.GraphicsCaptureItem";Marshal.ThrowExceptionForHR(WindowsCreateString(name,name.Length,out var hstring));nint factory=0,item=0;object? interop=null;try{var iid=typeof(ICaptureInterop).GUID;Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring,ref iid,out factory));interop=Marshal.GetObjectForIUnknown(factory);var itemId=new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");Marshal.ThrowExceptionForHR(((ICaptureInterop)interop).CreateForWindow(hwnd,ref itemId,out item));return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);}finally{if(item!=0)Marshal.Release(item);if(interop!=null)Marshal.ReleaseComObject(interop);if(factory!=0)Marshal.Release(factory);WindowsDeleteString(hstring);}}

[DllImport("d3d11.dll")] static extern int D3D11CreateDevice(nint adapter,uint type,nint software,uint flags,nint levels,uint count,uint sdk,out nint device,out uint level,out nint context);
[DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi,out nint device);
static async Task Main(string[] args){
 try{Console.SetError(new StreamWriter(Console.OpenStandardError(),new System.Text.UTF8Encoding(false)){AutoFlush=true});await Run(args);}catch(Exception e){Console.Error.WriteLine(e.Message);Environment.ExitCode=1;}
}
static async Task Run(string[] args){
 if(args.Length is not (5 or 6)||!int.TryParse(args[0],out int pid)||!int.TryParse(args[1],out int fps)||fps is not (10 or 30 or 60)||!int.TryParse(args[2],out int bitrate)||bitrate is not (2000000 or 8000000 or 20000000))throw new Exception("Invalid stream configuration");
 SetProcessDpiAwarenessContext((nint)(-4));
 var adapters=StreamAdapters.List();var adapter=args[3]=="auto"?adapters.FirstOrDefault():adapters.FirstOrDefault(a=>a.Id==args[3]);if(adapter==null)throw new Exception("所选显卡不存在或不支持硬件编码，请在模块设置重新选择");
 var process=Process.GetProcessById(pid);if(process.ProcessName!="ffxiv_dx11")throw new Exception("Only FFXIV may be captured");var hwnd=process.MainWindowHandle;if(hwnd==0||!GraphicsCaptureSession.IsSupported())throw new Exception("游戏窗口采集不可用");
 if(args.Length==5||args[5]=="gpu"){await CaptureGpu(hwnd,adapter,fps,bitrate,args[4]);return;}if(args[5]!="cpu")throw new Exception("Invalid capture pipeline");using var device=CreateDevice(adapter.Index);await Capture(hwnd,device,adapter,fps,bitrate,args[4]);
}
static async Task CaptureGpu(nint hwnd,StreamAdapters.Adapter adapter,int fps,int initialRate,string executable){
 if(!GetClientRect(hwnd,out var client))throw new Exception("游戏窗口尺寸不可用");int width=client.Right&~1,height=client.Bottom&~1;double scale=Math.Min(1d,Math.Min(3840d/width,2160d/height));width=(int)(width*scale)&~1;height=(int)(height*scale)&~1;if(width<2||height<2)throw new Exception("游戏窗口尺寸无效");
 int requested=initialRate;using var change=new SemaphoreSlim(0,1);using var lifetime=new CancellationTokenSource();_=Task.Run(async()=>{try{while(!lifetime.IsCancellationRequested&&await Console.In.ReadLineAsync() is string line){if(line.StartsWith("RATE ")&&int.TryParse(line.AsSpan(5),out int value)&&value is 2000000 or 8000000 or 20000000){if(Interlocked.Exchange(ref requested,value)!=value&&change.CurrentCount==0)change.Release();}}}catch{}});
 SetThreadExecutionState(0x80000003);try{while(!lifetime.IsCancellationRequested){int bitrate=Volatile.Read(ref requested);var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};void Add(params string[] values){foreach(var value in values)start.ArgumentList.Add(value);}
 Add("-hide_banner","-loglevel","error","-init_hw_device",$"d3d11va=selected:{adapter.Index}","-filter_hw_device","selected");
 string graph=$"gfxcapture=hwnd={(ulong)hwnd}:capture_cursor=0:capture_border=0:display_border=0:width={width}:height={height}:max_framerate={fps}:resize_mode=scale_aspect";
 if(adapter.Vendor==0x8086)graph+=",hwmap=derive_device=qsv:mode=read+direct,format=qsv,vpp_qsv=format=nv12";
 Add("-filter_complex",graph,"-an","-c:v",adapter.Encoder,"-b:v",bitrate.ToString(),"-maxrate",bitrate.ToString(),"-bufsize",Math.Max(100000,bitrate/fps*2).ToString(),"-bf","0","-g",fps.ToString());
 if(adapter.Vendor==0x10de)Add("-preset","p1","-tune","ull","-rc-lookahead","0","-zerolatency","1","-delay","0");else if(adapter.Vendor==0x1002)Add("-usage","ultralowlatency","-quality","speed");else Add("-async_depth","1","-look_ahead","0");
 Add("-fps_mode","passthrough","-bsf:v","h264_metadata=aud=insert","-flush_packets","1","-f","h264","pipe:1");
 using var encoder=new Process{StartInfo=start};if(!encoder.Start())throw new Exception("GPU 编码器启动失败");Console.Error.WriteLine($"H264 pipeline=gpu-direct; adapter={adapter.Name}; encoder={adapter.Encoder}; size={width}x{height}; fps={fps}; bitrate={bitrate}; cpu_readback=0");
 var errors=encoder.StandardError.ReadToEndAsync();var packets=SendAccessUnits(encoder.StandardOutput.BaseStream,Console.OpenStandardOutput(),lifetime.Token);var exited=encoder.WaitForExitAsync();using var wait=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var changed=change.WaitAsync(wait.Token);Task winner=await Task.WhenAny(changed,exited,packets);bool reconfigure=winner==changed;wait.Cancel();try{if(!encoder.HasExited)encoder.Kill(true);}catch{}try{await packets;}catch when(reconfigure){}await exited;string error=await errors;if(!reconfigure)throw new Exception("GPU 直通编码中断："+(error.Length>800?error[..800]:error));}
 }finally{lifetime.Cancel();SetThreadExecutionState(0x80000000);}}

static unsafe IDirect3DDevice CreateDevice(int index){nint factory=0,selected=0,native=0,context=0,dxgi=0,abi=0;var factoryId=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref factoryId,out factory));try{var enumerate=(delegate* unmanaged[Stdcall]<nint,uint,nint*,int>)(*(nint**)factory)[12];nint a=0;Marshal.ThrowExceptionForHR(enumerate(factory,(uint)index,&a));selected=a;Marshal.ThrowExceptionForHR(D3D11CreateDevice(selected,0,0,0x20,0,0,7,out native,out _,out context));var guid=new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native,in guid,out dxgi));Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi,out abi));return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(abi);}finally{foreach(nint p in new[]{abi,dxgi,context,native,selected,factory})if(p!=0)Marshal.Release(p);}}
[DllImport("dxgi.dll")]static extern int CreateDXGIFactory1(ref Guid iid,out nint factory);
static async Task Capture(nint hwnd,IDirect3DDevice device,StreamAdapters.Adapter adapter,int fps,int bitrate,string executable){
 var item=CaptureWindow(hwnd);var size=item.Size;if(!GetClientRect(hwnd,out var client)||!GetWindowRect(hwnd,out var outer))throw new Exception("游戏窗口尺寸不可用");var origin=new Point();ClientToScreen(hwnd,ref origin);if(DwmGetWindowAttribute(hwnd,9,out var bounds,Marshal.SizeOf<Rect>())!=0)bounds=outer;
 int x=Math.Max(0,origin.X-bounds.Left),y=Math.Max(0,origin.Y-bounds.Top),width=Math.Min(client.Right,size.Width-x)&~1,height=Math.Min(client.Bottom,size.Height-y)&~1;if(width<2||height<2)throw new Exception("游戏窗口客户区无效");
 double scale=Math.Min(1d,Math.Min(3840d/width,2160d/height));int encodedWidth=(int)(width*scale)&~1,encodedHeight=(int)(height*scale)&~1;string resize=scale<1?$",scale={encodedWidth}:{encodedHeight}":"";
 var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};void Add(params string[] values){foreach(var v in values)start.ArgumentList.Add(v);}
 Add("-hide_banner","-loglevel","error","-init_hw_device",$"d3d11va=selected:{adapter.Index}");if(adapter.Vendor==0x8086)Add("-init_hw_device","qsv=encoder@selected");
 Add("-filter_hw_device",adapter.Vendor==0x8086?"encoder":"selected","-f","rawvideo","-pixel_format","bgra","-video_size",$"{size.Width}x{size.Height}","-framerate",fps.ToString(),"-i","pipe:0","-an","-vf",$"crop={width}:{height}:{x}:{y}{resize},format=nv12,hwupload","-c:v",adapter.Encoder,"-b:v",bitrate.ToString(),"-maxrate",bitrate.ToString(),"-bufsize",Math.Max(100000,bitrate/fps*2).ToString(),"-bf","0","-g",fps.ToString());
 if(adapter.Vendor==0x10de)Add("-preset","p1","-tune","ull","-rc-lookahead","0","-zerolatency","1","-delay","0");else if(adapter.Vendor==0x1002)Add("-usage","ultralowlatency","-quality","speed");else Add("-async_depth","1","-look_ahead","0");
 Add("-bsf:v","h264_metadata=aud=insert","-flush_packets","1","-f","h264","pipe:1");
 using var encoder=new Process{StartInfo=start};if(!encoder.Start())throw new Exception("无法启动硬件编码器");Console.Error.WriteLine($"H264 adapter={adapter.Name}; encoder={adapter.Encoder}; size={encodedWidth}x{encodedHeight}; fps={fps}; bitrate={bitrate}");
 var errors=encoder.StandardError.ReadToEndAsync();using var lifetime=new CancellationTokenSource();var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var raw=Channel.CreateBounded<FramePixels>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true,SingleWriter=true},p=>p.Dispose());var captured=Channel.CreateBounded<Direct3D11CaptureFrame>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true,SingleWriter=true},f=>f.Dispose());
 var writer=Task.Run(async()=>{try{await foreach(var pixels in raw.Reader.ReadAllAsync(lifetime.Token)){using(pixels)await encoder.StandardInput.BaseStream.WriteAsync(pixels.Bytes.AsMemory(0,pixels.Length),lifetime.Token);}}catch(Exception e){if(!lifetime.IsCancellationRequested)Console.Error.WriteLine(e.Message);}finally{done.TrySetResult();}});
 var packets=Task.Run(async()=>{try{await SendAccessUnits(encoder.StandardOutput.BaseStream,Console.OpenStandardOutput(),lifetime.Token);}catch(Exception e){if(!lifetime.IsCancellationRequested)Console.Error.WriteLine(e.Message);}finally{done.TrySetResult();}});
 using var pool=Direct3D11CaptureFramePool.CreateFreeThreaded(device,DirectXPixelFormat.B8G8R8A8UIntNormalized,3,size);using var session=pool.CreateCaptureSession(item);session.IsCursorCaptureEnabled=false;item.Closed+=(_,_)=>done.TrySetResult();long arrivals=0,reads=0,readTicks=0;var clock=new FrameClock(fps,Stopwatch.Frequency);long metricsStart=Stopwatch.GetTimestamp();
 using var metrics=new Timer(_=>{double elapsed=(Stopwatch.GetTimestamp()-metricsStart)/(double)Stopwatch.Frequency;Console.Error.WriteLine($"H264 stats source={Interlocked.Read(ref arrivals)/Math.Max(.001,elapsed):F1} readback={Interlocked.Read(ref reads)/Math.Max(.001,elapsed):F1} readback_ms={Interlocked.Read(ref readTicks)*1000d/Stopwatch.Frequency/Math.Max(1,Interlocked.Read(ref reads)):F2}");},null,2000,2000);
 pool.FrameArrived+=(sender,_)=>{try{var frame=sender.TryGetNextFrame();if(frame==null)return;Interlocked.Increment(ref arrivals);if(!captured.Writer.TryWrite(frame))frame.Dispose();}catch(Exception e){Console.Error.WriteLine(e.Message);done.TrySetResult();}};
 var readback=Task.Run(async()=>{try{await foreach(var frame in captured.Reader.ReadAllAsync(lifetime.Token)){using(frame){long now=Stopwatch.GetTimestamp();if(!clock.Accept(now))continue;if(frame.ContentSize.Width!=size.Width||frame.ContentSize.Height!=size.Height)throw new Exception("游戏窗口尺寸改变，请重新开始串流");long begin=Stopwatch.GetTimestamp();using var bitmap=await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);var pixels=new FramePixels(checked(size.Width*size.Height*4));try{bitmap.CopyToBuffer(pixels.Bytes.AsBuffer(0,pixels.Length));if(!raw.Writer.TryWrite(pixels))pixels.Dispose();}catch{pixels.Dispose();throw;}Interlocked.Increment(ref reads);Interlocked.Add(ref readTicks,Stopwatch.GetTimestamp()-begin);}}}catch(Exception e){if(!lifetime.IsCancellationRequested)Console.Error.WriteLine(e.Message);}finally{done.TrySetResult();}});

 try{SetThreadExecutionState(0x80000003);session.StartCapture();var exited=encoder.WaitForExitAsync();await Task.WhenAny(done.Task,exited);if(!exited.IsCompleted)await Task.WhenAny(exited,Task.Delay(100));if(exited.IsCompleted)throw new Exception("硬件编码器退出："+await errors);}finally{lifetime.Cancel();captured.Writer.TryComplete();raw.Writer.TryComplete();try{if(!encoder.HasExited)encoder.Kill(true);}catch{}await Task.WhenAll(writer,packets,readback);while(captured.Reader.TryRead(out var stale))stale.Dispose();while(raw.Reader.TryRead(out var pixels))pixels.Dispose();double seconds=(Stopwatch.GetTimestamp()-metricsStart)/(double)Stopwatch.Frequency;Console.Error.WriteLine($"H264 stats source={arrivals/Math.Max(.001,seconds):F1} readback={reads/Math.Max(.001,seconds):F1} readback_ms={readTicks*1000d/Stopwatch.Frequency/Math.Max(1,reads):F2}");SetThreadExecutionState(0x80000000);}
}
sealed class FramePixels:IDisposable {public byte[] Bytes{get;}public int Length{get;}bool disposed;public FramePixels(int length){Length=length;Bytes=ArrayPool<byte>.Shared.Rent(length);}public void Dispose(){if(disposed)return;disposed=true;ArrayPool<byte>.Shared.Return(Bytes);}}
// AUD boundaries produce independently framed Annex-B access units; never cut at pipe read boundaries.
internal static async Task SendAccessUnits(Stream input,Stream output,CancellationToken ct){
 var pending=new List<byte>();var buffer=new byte[65536];int scan=0;bool aud=false;
 while(true){int count=await input.ReadAsync(buffer,ct);if(count==0)break;pending.AddRange(buffer.AsSpan(0,count).ToArray());while(scan+4<pending.Count){int prefix=pending[scan]==0&&pending[scan+1]==0?(pending[scan+2]==1?3:pending[scan+2]==0&&pending[scan+3]==1?4:0):0;if(prefix>0&&scan+prefix<pending.Count&&(pending[scan+prefix]&31)==9){if(aud&&scan>0){var packet=pending.GetRange(0,scan).ToArray();await output.WriteAsync(BitConverter.GetBytes(packet.Length),ct);await output.WriteAsync(packet,ct);await output.FlushAsync(ct);pending.RemoveRange(0,scan);scan=prefix+1;}else{aud=true;scan+=prefix+1;}}else scan++;}if(pending.Count>4194304)throw new Exception("H.264 access unit exceeds limit");}
}
}
