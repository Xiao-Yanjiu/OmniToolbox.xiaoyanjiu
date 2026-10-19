using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text;
using System.Net;
using System.Security.Cryptography;
using Player = OmniToolbox.LocalModules.MusicPlayer;

class Program
{
    static async Task Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (var dir in typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Select(x => x.Value!))
            { var p = Path.Combine(dir, name.Name + ".dll"); if (File.Exists(p)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(p); }
            return null;
        };
        await Checks(args.Contains("--live"));
    }
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static object? Field(object o, string name) => o.GetType().GetField(name, Flags)!.GetValue(o);
    static void Set(object o, string name, object? value) => o.GetType().GetField(name, Flags)!.SetValue(o, value);
    static object? Invoke(object o, string name, params object?[] values) => o.GetType().GetMethod(name, Flags)!.Invoke(o, values);
    static async Task<object?> Call(object o, string name, params object?[] values)
    { var task = (Task)Invoke(o, name, values)!; await task; return task.GetType().GetProperty("Result")?.GetValue(task); }
    static int passed;
    static void Check(bool result, string name) { if (!result) throw new Exception(name); passed++; }
    static async Task Reject(Func<Task> action, string expected)
    { try { await action(); } catch (Exception ex) { Check(ex.ToString().Contains(expected), expected); return; } throw new Exception("Expected error: " + expected); }
    static async Task Checks(bool live)
    {
        var root = new Player(); Invoke(root, "EnsurePlatforms");
        var net = Field(root, "neteasePlayer")!;
        var qqConfig = (Player.MusicPlayerConfig)Field(root, "config")!;
        var netConfig = (Player.MusicPlayerConfig)Field(net, "config")!;
        Check(!ReferenceEquals(qqConfig, netConfig), "config independence");
        Check(ReferenceEquals(qqConfig.Netease, netConfig), "nested config persistence");
        Check((string)root.GetType().GetProperty("MciAlias", Flags)!.GetValue(root)! != (string)net.GetType().GetProperty("MciAlias", Flags)!.GetValue(net)!, "independent audio devices");
        qqConfig.QQCookie = "qq=test"; netConfig.QQCookie = "MUSIC_U=netease";
        qqConfig.Queue.Add(new Player.Track { Id = "qq_mid", Title = "QQ song" });
        netConfig.Queue.Add(new Player.Track { Id = "123", Source = "netease", Title = "Net song" });
        netConfig.Volume = 30; qqConfig.Volume = 80;
        Set(root, "mainPanelOpen", true); Set(root, "enginePlaying", true); Set(net, "enginePlaying", true);
        Set(root, "requestedProvider", 1); Invoke(root, "ApplyPlatformSwitch");
        Check(qqConfig.SelectedProvider == 1 && !(bool)Field(root, "mainPanelOpen")! && (bool)Field(net, "mainPanelOpen")!, "page moves to selected platform");
        Check((bool)Field(root, "enginePlaying")! && (bool)Field(net, "enginePlaying")!, "switch does not pause either engine");
        Set(root, "requestedProvider", 0); Invoke(root, "ApplyPlatformSwitch");
        Check((bool)Field(root, "mainPanelOpen")! && !(bool)Field(net, "mainPanelOpen")! && netConfig.Queue.Count == 1, "switch back retains other queue");
        var restored = JsonSerializer.Deserialize<Player.MusicPlayerConfig>(JsonSerializer.Serialize(qqConfig))!;
        Check(restored.QQCookie == "qq=test" && restored.Netease!.QQCookie == "MUSIC_U=netease", "account roundtrip");
        Check(restored.Queue[0].Source == "qq" && restored.Netease!.Queue[0].Source == "netease", "queue roundtrip");
        Check(restored.Volume == 80 && restored.Netease!.Volume == 30, "volume roundtrip");
        Check(JsonSerializer.Deserialize<Player.MusicPlayerConfig>("{\"QQNickname\":\"old\",\"Queue\":[{\"Id\":\"abc\"}]}")!.Queue[0].Source == "qq", "legacy default");
        var payload = Player.NetEncrypt("{\"s\":\"hello\"}", "0123456789abcdef");
        string Decrypt(string value, string key) { using var aes = Aes.Create(); aes.Key = Encoding.ASCII.GetBytes(key); return Encoding.UTF8.GetString(aes.DecryptCbc(Convert.FromBase64String(value), Encoding.ASCII.GetBytes("0102030405060708"))); }
        Check(Decrypt(Decrypt(payload["params"], "0123456789abcdef"), "0CoJUm6Qyw8W8jud") == "{\"s\":\"hello\"}", "AES payload roundtrip");
        Check(payload["encSecKey"].Length == 256 && payload["encSecKey"].All(Uri.IsHexDigit), "RSA output length");
        string invalidFile = Path.Combine(Path.GetTempPath(), "OmniInvalidDecode-" + Guid.NewGuid().ToString("N") + ".wav");
        await Reject(() => Task.Run(() => Player.DecodeNeteaseMp3(Array.Empty<byte>(), invalidFile, new CancellationToken(true))), "canceled");
        Check(!File.Exists(invalidFile), "canceled decoder leaves no file");
        try { Player.DecodeNeteaseMp3(new byte[32], invalidFile, CancellationToken.None); throw new Exception("Invalid MP3 accepted"); }
        catch (System.IO.InvalidDataException) { Check(!File.Exists(invalidFile), "invalid audio cleanup"); }
        var qr = OmniMusicQr.QrCode.EncodeText("https://music.163.com/login?codekey=test-key", OmniMusicQr.QrCode.Ecc.Medium);
        Check(qr.Size >= 21 && qr.GetModule(0, 0) && qr.GetModule(6, 6) && !qr.GetModule(1, 1), "QR finder pattern");
        var q2 = OmniMusicQr.QrCode.EncodeText("https://music.163.com/login?codekey=test-key", OmniMusicQr.QrCode.Ecc.Medium);
        Check(qr.ToSvgString(4) == q2.ToSvgString(4), "deterministic QR");
        using var song = JsonDocument.Parse("{\"id\":123,\"name\":\"Song\",\"ar\":[{\"name\":\"Artist\"}],\"al\":{\"picUrl\":\"http://p1.music.126.net/a.jpg\"},\"fee\":1}");
        var track = Player.NetTrack(song.RootElement)!;
        Check(track.Source == "netease" && track.SongId == 123 && track.IsVip && track.CoverUrl.StartsWith("https:"), "song mapping");
        using var alt = JsonDocument.Parse("{\"id\":2,\"name\":\"Alt\",\"artists\":[{\"name\":\"A\"}],\"album\":{\"picUrl\":\"https://pic\"}}");
        Check(Player.NetTrack(alt.RootElement)!.Artist == "A", "alternate search schema");
        var fake = new FakeHandler(); Set(net, "netHttp", new HttpClient(fake)); Set(net, "netCookie", "MUSIC_U=net-only; __csrf=net-csrf");
        fake.Body = "{\"code\":200,\"result\":{\"songs\":[{\"id\":123,\"name\":\"Found\"}]}}";
        var found = (List<Player.Track>)(await Call(net, "NetSearch", "hello"))!;
        Check(found.Count == 1 && found[0].Id == "123", "mocked search");
        Check(fake.Cookie.Contains("net-only") && !fake.Cookie.Contains("qq="), "no QQ cookie leakage");
        Check(fake.Path == "/eapi/cloudsearch/pc", "search endpoint");
        fake.Body = "{\"code\":200,\"data\":[{\"url\":\"https://m.music.126.net/a.mp3\",\"type\":\"mp3\",\"freeTrialInfo\":null}]}";
        var url = ((string?, bool))(await Call(net, "NetUrl", track))!;
        Check(url.Item2 && url.Item1!.EndsWith("a.mp3"), "authorized MP3 result");
        fake.Body = "{\"code\":200,\"data\":[{\"url\":\"https://m.music.126.net/a.mp3\",\"type\":\"mp3\",\"freeTrialInfo\":{\"start\":0,\"end\":30}}]}";
        await Reject(async () => await Call(net, "NetUrl", track), "试听");
        fake.Body = "{\"code\":200,\"data\":[{\"url\":\"https://m.music.126.net/a.flac\",\"type\":\"flac\"}]}";
        await Reject(async () => await Call(net, "NetUrl", track), "flac");
        fake.Body = "{\"code\":200,\"data\":[{\"url\":null}]}";
        await Reject(async () => await Call(net, "NetUrl", track), "版权");
        fake.Body = "{\"code\":301}";
        await Reject(async () => await Call(net, "NetSearch", "hi"), "登录已失效");
        fake.Body = "{\"code\":200,\"lrc\":{\"lyric\":\"[00:01.00]Hello\"}}";
        Check((string)(await Call(net, "NetLyric", "123"))! == "[00:01.00]Hello", "lyric response");
        fake.Body = "{\"code\":200}";
        Check((string)(await Call(net, "NetLyric", "123"))! == "", "instrumental fallback");
        // A playlist response can have only one embedded track, while trackIds contains the full list.
        fake.Route = path => path.EndsWith("playlist/detail") ? "{\"code\":200,\"playlist\":{\"trackIds\":[{\"id\":2},{\"id\":1}],\"tracks\":[{\"id\":1}]}}" : "{\"code\":200,\"songs\":[{\"id\":1,\"name\":\"A\"},{\"id\":2,\"name\":\"B\"}]}";
        var full = (List<Player.Track>)(await Call(net, "NetPlaylist", "5"))!;
        Check(full.Count == 2 && full[0].Id == "2" && full[1].Id == "1", "full playlist and order");
        fake.Route = null;
        var tasks = new[] { Call(net, "NetLyric", "1"), Call(net, "NetLyric", "2") }; await Task.WhenAll(tasks);
        Check(tasks.All(t => t.IsCompletedSuccessfully), "concurrent read requests");
        bool stalePublished = false; int oldSession = (int)Field(net, "netSession")!;
        Invoke(net, "ResetNetSession"); Invoke(net, "NetPost", oldSession, (Action)(() => stalePublished = true)); Invoke(net, "ConsumePending");
        Check(!stalePublished && qqConfig.QQCookie == "qq=test" && qqConfig.Queue.Count == 1, "logout discards stale results without changing QQ");
        if (live)
        {
            Set(net, "netHttp", new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) }); Set(net, "netCookie", "");
            try
            {
                var rows = (List<Player.Track>)(await Call(net, "NetSearch", "海阔天空"))!;
                Console.WriteLine("LIVE search: " + rows.Count + " results");
                string lyric = (string)(await Call(net, "NetLyric", "33894312"))!;
                Console.WriteLine("LIVE lyric: " + lyric.Length + " chars");
                var keyReply = await Call(net, "NetApi", "login/qrcode/unikey", new Dictionary<string, object> { ["type"] = 3 }, "", true);
                var keyBody = (JsonElement)keyReply!.GetType().GetProperty("Body")!.GetValue(keyReply)!;
                bool hasKey = keyBody.TryGetProperty("unikey", out var key) && key.GetString()!.Length > 0;
                Console.WriteLine("LIVE QR key: " + (hasKey ? "received" : "missing"));
                if (hasKey)
                {
                    var waiting = await Call(net, "NetApi", "login/qrcode/client/login", new Dictionary<string, object> { ["key"] = key.GetString()!, ["type"] = 3 }, "", false);
                    var state = (JsonElement)waiting!.GetType().GetProperty("Body")!.GetValue(waiting)!;
                    Console.WriteLine("LIVE QR polling: " + state.GetProperty("code").GetInt32());
                }
                try
                {
                    var free = new Player.Track { Id = "33894312", SongId = 33894312, Source = "netease" };
                    var music = ((string?, bool))(await Call(net, "NetUrl", free))!;
                    using var probe = new HttpClient();
                    using var req = new HttpRequestMessage(HttpMethod.Get, music.Item1);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
                    req.Headers.Referrer = new Uri("https://music.163.com/");
                    using var result = await probe.SendAsync(req);
                    Console.WriteLine("LIVE MP3 probe: " + (int)result.StatusCode + " " + result.Content.Headers.ContentType);
                    if (Environment.GetCommandLineArgs().Contains("--audio"))
                    {
                        string file = Path.Combine(Path.GetTempPath(), "OmniNeteaseDecode-" + Guid.NewGuid().ToString("N") + ".mp3");
                        string alias = "omniNetDecodeCheck";
                        var mci = typeof(Player).GetMethod("Mci", BindingFlags.Static | BindingFlags.NonPublic)!;
                        string Run(string command) => (string)mci.Invoke(null, new object[] { command })!;
                        try
                        {
                            using var audioRequest = new HttpRequestMessage(HttpMethod.Get, music.Item1);
                            // Decode a complete authorized standard-quality file; partial MP3 can fail MCI indexing.
                            audioRequest.Headers.Referrer = new Uri("https://music.163.com/");
                            using var audio = await probe.SendAsync(audioRequest); audio.EnsureSuccessStatusCode();
                            await File.WriteAllBytesAsync(file, await audio.Content.ReadAsByteArrayAsync());
                            string wav = Path.ChangeExtension(file, ".wav");
                            Player.DecodeNeteaseMp3(File.ReadAllBytes(file), wav, CancellationToken.None);
                            File.Delete(file); file = wav;
                            var raw = typeof(Player).GetMethod("mciSendString", BindingFlags.Static | BindingFlags.NonPublic)!;
                            int openCode = (int)raw.Invoke(null, new object?[] { "open \"" + file + "\" type mpegvideo alias " + alias, null, 0, IntPtr.Zero })!;
                            if (openCode != 0) openCode = (int)raw.Invoke(null, new object?[] { "open \"" + file + "\" alias " + alias, null, 0, IntPtr.Zero })!;
                            var signature = File.ReadAllBytes(file);
                            Console.WriteLine("LIVE decoded PCM file: " + signature.Length + " bytes, prefix=" + Convert.ToHexString(signature.Take(12).ToArray()) + ", MCI open=" + openCode);
                            Run("setaudio " + alias + " volume to 0"); Run("play " + alias);
                            Console.WriteLine("LIVE managed MP3 → WAV MCI (muted): " + Run("status " + alias + " mode"));
                        }
                        finally { Run("close " + alias); if (File.Exists(file)) File.Delete(file); }
                    }
                }
                catch (Exception ex) { Console.WriteLine("LIVE playback URL: " + ex.GetBaseException().Message); }
                Console.WriteLine("LIVE reads completed (no account login or writes performed)");
            }
            catch (Exception ex) { Console.WriteLine("LIVE unavailable: " + ex.GetBaseException().Message); }
        }
        if (Environment.GetCommandLineArgs().Contains("--audio"))
        {
            string temp = Path.Combine(Path.GetTempPath(), "OmniMusicCheck-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try
            {
                foreach (string name in new[] { "qq.wav", "netease.wav" })
                {
                    using var stream = File.Create(Path.Combine(temp, name)); using var writer = new BinaryWriter(stream);
                    int length = 8000 * 2 * 3;
                    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + length); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
                    writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(length); writer.Write(new byte[length]);
                }
                bool qq = (bool)Invoke(root, "MciOpen", Path.Combine(temp, "qq.wav"))!;
                bool ne = (bool)Invoke(net, "MciOpen", Path.Combine(temp, "netease.wav"))!;
                Invoke(root, "MciClose");
                var query = typeof(Player).GetMethod("Mci", BindingFlags.Static | BindingFlags.NonPublic)!;
                string mode = (string)query.Invoke(null, new object[] { "status omniMusicPlayerNetease mode" })!;
                Console.WriteLine("AUDIO independent channels: " + (qq && ne && mode == "playing" ? "passed" : "unavailable (QQ=" + qq + ", NetEase=" + ne + ", mode=" + mode + ")"));
            }
            finally { Invoke(root, "MciClose"); Invoke(net, "MciClose"); Directory.Delete(temp, true); }
        }
        Console.WriteLine(passed + " managed checks passed; game UI not executed.");
    }
    class FakeHandler : HttpMessageHandler
    {
        public string Body = "{}", Cookie = "", Path = "";
        public Func<string, string>? Route;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Path = request.RequestUri!.AbsolutePath; Cookie = string.Join(";", request.Headers.GetValues("Cookie"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Route?.Invoke(Path) ?? Body, Encoding.UTF8, "application/json") });
        }
    }
}
