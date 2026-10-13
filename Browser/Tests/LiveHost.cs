using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var bin = Path.GetFullPath(args[0]); var profile = Path.GetFullPath(args[1]); Directory.CreateDirectory(profile);
using var stop = new CancellationTokenSource();
var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
var server = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        try
        {
            using var client = await tcp.AcceptTcpClientAsync(stop.Token); using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
            var html = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>CEF_TEST_READY</title></head><body style='margin:0;background:white'><input style='width:200px;height:30px' oninput=\"document.title='INPUT_'+this.value\"><div style='color:red'>真实 CEF 离屏渲染测试</div></body></html>");
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, stop.Token); await stream.WriteAsync(html, stop.Token);
        }
        catch (OperationCanceledException) { break; }
        catch (IOException) { }
    }
});
string id = Guid.NewGuid().ToString("N");
using var memory = MemoryMappedFile.CreateNew("Local\\Yanjiu.Cef." + id, 64 + 2560 * 1600 * 4);
using var view = memory.CreateViewAccessor(); using var gate = new Mutex(false, "Local\\Yanjiu.Cef.Lock." + id);
var info = new ProcessStartInfo(Path.Combine(bin, "Yanjiu.BrowserHost.exe"))
{ WorkingDirectory = bin, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
info.ArgumentList.Add("--yanjiu-profile=" + profile); info.ArgumentList.Add("--yanjiu-proxy=");
using var process = Process.Start(info)!;
var messages = new ConcurrentQueue<string>();
var output = Task.Run(async () => { string? line; while ((line = await process.StandardOutput.ReadLineAsync()) != null) messages.Enqueue(line); });
var errors = process.StandardError.ReadToEndAsync();
async Task Send(object message) { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)); await process.StandardInput.FlushAsync(); }
async Task Wait(Func<bool> condition, string name)
{
    var timer = Stopwatch.StartNew();
    while (!condition() && !process.HasExited && timer.ElapsedMilliseconds < 20000) await Task.Delay(20);
    if (!condition()) throw new Exception(name + ": " + string.Join(" | ", messages));
    Console.WriteLine("PASS " + name);
}
try
{
    await Wait(() => messages.Any(m => m.Contains("\"ready\"")), "real CEF initialization without IPC-losing restart");
    await Send(new { Kind = "create", Id = id, Url = $"http://127.0.0.1:{port}/", Width = 800, Height = 400 });
    await Wait(() => messages.Any(m => m.Contains("CEF_TEST_READY")), "local webpage and JavaScript title callback");
    await Wait(() => view.ReadInt32(0) > 0, "actual shared BGRA frame");
    gate.WaitOne();
    try
    {
        if (view.ReadInt32(4) != 800 || view.ReadInt32(8) != 400 || view.ReadByte(67) == 0) throw new Exception("Invalid view frame");
        Console.WriteLine("PASS frame dimensions and opaque pixel");
    }
    finally { gate.ReleaseMutex(); }
    await Send(new { Kind = "focus", Id = id, Focus = true });
    await Send(new { Kind = "move", Id = id, X = 30, Y = 15 });
    await Send(new { Kind = "click", Id = id, X = 30, Y = 15, Button = 0, Up = false, Count = 1 });
    await Send(new { Kind = "click", Id = id, X = 30, Y = 15, Button = 0, Up = true, Count = 1 });
    await Send(new { Kind = "key", Id = id, Key = 65, NativeKey = 0x001e0001 });
    await Send(new { Kind = "key", Id = id, Key = 97, Character = true });
    await Send(new { Kind = "key", Id = id, Key = 65, Up = true, NativeKey = unchecked((int)0xc01e0001) });
    await Wait(() => messages.Any(m => m.Contains("INPUT_a")), "CEF mouse focus and keyboard input APIs");
    await Send(new { Kind = "shutdown" });
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
    if (process.ExitCode != 0) throw new Exception("Unexpected exit " + process.ExitCode);
    Console.WriteLine("PASS clean CEF shutdown");
    info.ArgumentList.Clear(); info.ArgumentList.Add("--browser-subprocess-path=invalid-bootstrap-test");
    using var invalid = Process.Start(info)!;
    var invalidOutput = invalid.StandardOutput.ReadToEndAsync(); var invalidErrors = invalid.StandardError.ReadToEndAsync();
    await invalid.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    if (invalid.ExitCode != 2 || !(await invalidOutput).Contains("\"error\"")) throw new Exception("Bootstrap switches were misread as a profile");
    await invalidErrors;
    Console.WriteLine("PASS Chromium-only arguments are rejected without directory crash");
}
finally
{
    if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    stop.Cancel(); tcp.Stop();
    await output; await server;
    await File.WriteAllTextAsync(Path.Combine(profile, "test-stderr.txt"), await errors);
    await File.WriteAllLinesAsync(Path.Combine(profile, "test-events.jsonl"), messages);
}
