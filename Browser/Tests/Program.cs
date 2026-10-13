using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

if (args.Length == 0) return 2;
var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
if (args.Length > 1 && args[1] == "--mock-host")
{
    Console.WriteLine("{\"Kind\":\"ready\"}");
    string? line;
    while ((line = Console.ReadLine()) != null) Console.WriteLine("{\"Kind\":\"echo\"}");
    return 0;
}
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (var dir in args.Skip(2))
    { var path = Path.Combine(dir, name.Name + ".dll"); if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path); }
    return null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
var installer = assembly.GetType("OmniToolbox.LocalModules.CefInstaller", true)!;
var extract = installer.GetMethod("Extract", BindingFlags.NonPublic | BindingFlags.Static)!;
int passed = 0;
void Check(bool yes, string name) { if (!yes) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
string MakeZip(string name, params (string Path, string Text)[] entries)
{
    var path = Path.Combine(root, name);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var entry in entries) { using var writer = new StreamWriter(zip.CreateEntry(entry.Path).Open()); writer.Write(entry.Text); }
    return path;
}
var good = MakeZip("native.zip", ("runtimes/win-x64/native/libcef.dll", "mock"), ("runtimes/win-x64/native/locales/zh-CN.pak", "mock"),
    ("runtimes/win-arm64/native/libcef.dll", "wrong"), ("LICENSE.txt", "license"));
var output = Path.Combine(root, "extract"); Directory.CreateDirectory(output);
extract.Invoke(null, new object[] { good, output, true, CancellationToken.None });
Check(File.ReadAllText(Path.Combine(output, "libcef.dll")) == "mock" && File.Exists(Path.Combine(output, "locales/zh-CN.pak")) && File.Exists(Path.Combine(output, "CEF.LICENSE.txt")), "native architecture and license selection");
foreach (var bad in new[] { "../escape.dll", "C:/escape.dll", "normal/../../escape.dll" })
{
    var path = MakeZip(Guid.NewGuid() + ".zip", (bad, "bad")); bool rejected = false;
    try { extract.Invoke(null, new object[] { path, output, false, CancellationToken.None }); }
    catch (TargetInvocationException ex) when (ex.InnerException is IOException) { rejected = true; }
    Check(rejected, "reject traversal: " + bad);
}
var duplicate = MakeZip("duplicate.zip", ("a.dll", "one"), ("A.DLL", "two"));
bool duplicateRejected = false;
try { extract.Invoke(null, new object[] { duplicate, output, false, CancellationToken.None }); }
catch (TargetInvocationException ex) when (ex.InnerException is IOException) { duplicateRejected = true; }
Check(duplicateRejected, "reject duplicate paths");
var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool cancelRejected = false;
try { extract.Invoke(null, new object[] { good, output, true, cancelled.Token }); }
catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { cancelRejected = true; }
Check(cancelRejected, "extraction cancellation");
var instance = Activator.CreateInstance(installer, Path.Combine(root, "never-installed"))!;
Check(!(bool)installer.GetProperty("Ready")!.GetValue(instance)!, "missing runtime does not claim readiness");
Check(!Directory.Exists(Path.Combine(root, "never-installed")), "constructor does not download or create runtime");

// Local mock transport exercises progress and cancellation without accessing the CEF download URL.
var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); tcp.Start();
var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
var serving = Task.Run(async () =>
{
    using var client = await tcp.AcceptTcpClientAsync(); using var stream = client.GetStream();
    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
    byte[] body = new byte[262144]; new Random(123).NextBytes(body);
    byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 262144\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(head); await stream.WriteAsync(body);
});
var download = installer.GetMethod("Download", BindingFlags.NonPublic | BindingFlags.Instance)!;
await (Task)download.Invoke(instance, new object[] { $"http://127.0.0.1:{port}/mock", Path.Combine(root, "mock-download.bin"), "mock", CancellationToken.None })!;
await serving; tcp.Stop();
Check((long)installer.GetProperty("Bytes")!.GetValue(instance)! == 262144 && (long)installer.GetProperty("Total")!.GetValue(instance)! == 262144, "download byte progress");
bool downloadCancelled = false;
try { await (Task)download.Invoke(instance, new object[] { $"http://127.0.0.1:{port}/mock", Path.Combine(root, "cancelled.bin"), "mock", cancelled.Token })!; }
catch (OperationCanceledException) { downloadCancelled = true; }
Check(downloadCancelled, "download cancellation");

var surfaceType = assembly.GetType("OmniToolbox.LocalModules.GameWebBrowser+CefSurface", true)!;
using var surface = (IDisposable)Activator.CreateInstance(surfaceType)!;
string id = (string)surfaceType.GetField("Id")!.GetValue(surface)!;
using var memory = MemoryMappedFile.OpenExisting("Local\\Yanjiu.Cef." + id);
using var view = memory.CreateViewAccessor();
using var gate = Mutex.OpenExisting("Local\\Yanjiu.Cef.Lock." + id);
gate.WaitOne();
try { view.Write(0, 1); view.Write(4, 2); view.Write(8, 1); view.Write(12, 8); view.WriteArray(64, new byte[] { 1, 2, 3, 255, 5, 6, 7, 255 }, 0, 8); }
finally { gate.ReleaseMutex(); }
var read = surfaceType.GetMethod("TryRead")!;
object?[] values = { null, 0, 0 };
Check((bool)read.Invoke(surface, values)! && (int)values[1]! == 2 && ((byte[])values[0]!)[4] == 5, "shared BGRA frame round trip");
Check(!(bool)read.Invoke(surface, values)!, "unchanged frame does not reupload");
view.Write(0, 2); view.Write(4, 999999);
Check(!(bool)read.Invoke(surface, values)!, "reject invalid frame dimensions");

// The helper below echoes commands; it never references CefSharp or libcef.
var mockRoot = Path.Combine(root, "mock-host"); Directory.CreateDirectory(mockRoot);
var testBin = AppContext.BaseDirectory;
File.Copy(Path.Combine(testBin, "BrowserRegression.exe"), Path.Combine(mockRoot, "Yanjiu.BrowserHost.exe"));
foreach (var name in new[] { "BrowserRegression.dll", "BrowserRegression.deps.json", "BrowserRegression.runtimeconfig.json" })
    File.Copy(Path.Combine(testBin, name), Path.Combine(mockRoot, name));
var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
var peerType = assembly.GetType("OmniToolbox.LocalModules.CefPeer", true)!;
var peer = (IDisposable)Activator.CreateInstance(peerType, mockRoot, Path.Combine(root, "profile"), "--mock-host", new Action<string>(events.Enqueue))!;
var clock = System.Diagnostics.Stopwatch.StartNew();
while (events.IsEmpty && clock.ElapsedMilliseconds < 5000) await Task.Delay(20);
Check(events.Any(e => e.Contains("ready")), "owned helper startup without CEF");
peerType.GetMethod("Send")!.Invoke(peer, new object[] { new { Kind = "key", Id = "mock", Key = 65 } });
while (!events.Any(e => e.Contains("echo")) && clock.ElapsedMilliseconds < 5000) await Task.Delay(20);
Check(events.Any(e => e.Contains("echo")), "input command IPC");
var process = (System.Diagnostics.Process)peerType.GetField("process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(peer)!;
peer.Dispose();
await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
Check(process.HasExited, "owned helper cleanup");
Console.WriteLine($"{passed} checks passed. No CEF engine downloaded or started.");
return 0;
