// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
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
 try{await Run(args);}catch(Exception e){Console.Error.WriteLine(e.Message);Environment.ExitCode=1;}
}
static async Task Run(string[] args){
 if(args.Length!=5||!int.TryParse(args[0],out int pid)||!int.TryParse(args[1],out int fps)||fps is not (10 or 30 or 60)||!int.TryParse(args[2],out int bitrate)||bitrate is not (2000000 or 8000000 or 20000000))throw new Exception("Invalid stream configuration");
 System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
 var adapters=StreamAdapters.List();var adapter=args[3]=="auto"?adapters.FirstOrDefault():adapters.FirstOrDefault(a=>a.Id==args[3]);if(adapter==null)throw new Exception("所选显卡不存在或不支持硬件编码，请在模块设置重新选择");
 var process=Process.GetProcessById(pid);if(process.ProcessName!="ffxiv_dx11")throw new Exception("Only FFXIV may be captured");var hwnd=process.MainWindowHandle;if(hwnd==0||!GraphicsCaptureSession.IsSupported())throw new Exception("游戏窗口采集不可用");
 using var device=CreateDevice(adapter.Index);await Capture(hwnd,device,adapter,fps,bitrate,args[4]);
}
static unsafe IDirect3DDevice CreateDevice(int index){nint factory=0,selected=0,native=0,context=0,dxgi=0,abi=0;var factoryId=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref factoryId,out factory));try{var enumerate=(delegate* unmanaged[Stdcall]<nint,uint,nint*,int>)(*(nint**)factory)[12];nint a=0;Marshal.ThrowExceptionForHR(enumerate(factory,(uint)index,&a));selected=a;Marshal.ThrowExceptionForHR(D3D11CreateDevice(selected,0,0,0x20,0,0,7,out native,out _,out context));var guid=new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native,in guid,out dxgi));Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi,out abi));return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(abi);}finally{foreach(nint p in new[]{abi,dxgi,context,native,selected,factory})if(p!=0)Marshal.Release(p);}}
[DllImport("dxgi.dll")]static extern int CreateDXGIFactory1(ref Guid iid,out nint factory);
static async Task Capture(nint hwnd,IDirect3DDevice device,StreamAdapters.Adapter adapter,int fps,int bitrate,string executable){
 var item=CaptureWindow(hwnd);var size=item.Size;if(!GetClientRect(hwnd,out var client)||!GetWindowRect(hwnd,out var outer))throw new Exception("游戏窗口尺寸不可用");var origin=new Point();ClientToScreen(hwnd,ref origin);if(DwmGetWindowAttribute(hwnd,9,out var bounds,Marshal.SizeOf<Rect>())!=0)bounds=outer;
 int x=Math.Max(0,origin.X-bounds.Left),y=Math.Max(0,origin.Y-bounds.Top),width=Math.Min(client.Right,size.Width-x)&~1,height=Math.Min(client.Bottom,size.Height-y)&~1;if(width<2||height<2)throw new Exception("游戏窗口客户区无效");
 var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};void Add(params string[] values){foreach(var v in values)start.ArgumentList.Add(v);}
 Add("-hide_banner","-loglevel","error","-init_hw_device",$"d3d11va=selected:{adapter.Index}");if(adapter.Vendor==0x8086)Add("-init_hw_device","qsv=encoder@selected");
 Add("-filter_hw_device",adapter.Vendor==0x8086?"encoder":"selected","-f","rawvideo","-pixel_format","bgra","-video_size",$"{size.Width}x{size.Height}","-framerate",fps.ToString(),"-i","pipe:0","-an","-vf",$"crop={width}:{height}:{x}:{y},format=nv12,hwupload","-c:v",adapter.Encoder,"-b:v",bitrate.ToString(),"-maxrate",bitrate.ToString(),"-bufsize",Math.Max(100000,bitrate/fps*2).ToString(),"-bf","0","-g",fps.ToString());
 if(adapter.Vendor==0x10de)Add("-preset","p1","-tune","ull","-rc-lookahead","0","-zerolatency","1","-delay","0");else if(adapter.Vendor==0x1002)Add("-usage","ultralowlatency","-quality","speed");else Add("-async_depth","1","-look_ahead","0");
 Add("-bsf:v","h264_metadata=aud=insert","-flush_packets","1","-f","h264","pipe:1");
 using var encoder=new Process{StartInfo=start};if(!encoder.Start())throw new Exception("无法启动硬件编码器");Console.Error.WriteLine($"H264 adapter={adapter.Name}; encoder={adapter.Encoder}; size={width}x{height}; fps={fps}; bitrate={bitrate}");
 var errors=encoder.StandardError.ReadToEndAsync();using var lifetime=new CancellationTokenSource();var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var raw=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true,SingleWriter=true});
 var writer=Task.Run(async()=>{try{await foreach(var bytes in raw.Reader.ReadAllAsync(lifetime.Token))await encoder.StandardInput.BaseStream.WriteAsync(bytes,lifetime.Token);}catch(Exception e){if(!lifetime.IsCancellationRequested)Console.Error.WriteLine(e.Message);}finally{done.TrySetResult();}});
 var packets=Task.Run(async()=>{try{await SendAccessUnits(encoder.StandardOutput.BaseStream,Console.OpenStandardOutput(),lifetime.Token);}catch(Exception e){if(!lifetime.IsCancellationRequested)Console.Error.WriteLine(e.Message);}finally{done.TrySetResult();}});
 using var pool=Direct3D11CaptureFramePool.CreateFreeThreaded(device,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,size);using var session=pool.CreateCaptureSession(item);session.IsCursorCaptureEnabled=false;item.Closed+=(_,_)=>done.TrySetResult();int busy=0;long last=0;
 pool.FrameArrived+=async(sender,_)=>{if(Interlocked.Exchange(ref busy,1)!=0)return;try{using var frame=sender.TryGetNextFrame();if(frame==null)return;long now=Environment.TickCount64;if(now-last<1000d/fps)return;last=now;if(frame.ContentSize.Width!=size.Width||frame.ContentSize.Height!=size.Height)throw new Exception("游戏窗口尺寸改变，请重新开始串流");using var bitmap=await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);var bytes=new byte[checked(size.Width*size.Height*4)];bitmap.CopyToBuffer(bytes.AsBuffer());raw.Writer.TryWrite(bytes);}catch(Exception e){Console.Error.WriteLine(e.Message);done.TrySetResult();}finally{Interlocked.Exchange(ref busy,0);}};
 try{SetThreadExecutionState(0x80000003);session.StartCapture();var exited=encoder.WaitForExitAsync();await Task.WhenAny(done.Task,exited);if(exited.IsCompleted)throw new Exception("硬件编码器退出："+await errors);}finally{lifetime.Cancel();raw.Writer.TryComplete();try{if(!encoder.HasExited)encoder.Kill(true);}catch{}await Task.WhenAll(writer,packets);SetThreadExecutionState(0x80000000);}
}
// AUD boundaries produce independently framed Annex-B access units; never cut at pipe read boundaries.
internal static async Task SendAccessUnits(Stream input,Stream output,CancellationToken ct){
 var pending=new List<byte>();var buffer=new byte[65536];int scan=0;bool aud=false;
 while(true){int count=await input.ReadAsync(buffer,ct);if(count==0)break;pending.AddRange(buffer.AsSpan(0,count).ToArray());while(scan+4<pending.Count){int prefix=pending[scan]==0&&pending[scan+1]==0?(pending[scan+2]==1?3:pending[scan+2]==0&&pending[scan+3]==1?4:0):0;if(prefix>0&&scan+prefix<pending.Count&&(pending[scan+prefix]&31)==9){if(aud&&scan>0){var packet=pending.GetRange(0,scan).ToArray();await output.WriteAsync(BitConverter.GetBytes(packet.Length),ct);await output.WriteAsync(packet,ct);await output.FlushAsync(ct);pending.RemoveRange(0,scan);scan=prefix+1;}else{aud=true;scan+=prefix+1;}}else scan++;}if(pending.Count>4194304)throw new Exception("H.264 access unit exceeds limit");}
}
}
