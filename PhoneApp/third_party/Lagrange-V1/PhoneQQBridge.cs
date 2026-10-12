// SPDX-License-Identifier: GPL-3.0-or-later
// Xiao-Yanjiu, 2026-10-12. Bridge for Lagrange.Core V1 (0.3.1).
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entity;
using Lagrange.Core.Utility.Sign;

public sealed class PhoneQQBridge : IDisposable
{
    private BotContext bot;
    private Signer signer;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim sending = new(1);
    private int disposed;
    public bool Online { get; private set; }
    public uint Account => bot?.BotUin ?? 0;
    public async Task Login(string signUrl, string saved, bool scan, Action<string> status,
        Action<string> persist, Action<byte[]> qr, Action<uint,uint,string,string> receive, string appInfo)
    {
        using var data = JsonDocument.Parse(string.IsNullOrEmpty(saved) ? "{}" : saved);
        var device = data.RootElement.TryGetProperty("device",out var d)
            ? JsonSerializer.Deserialize<BotDeviceInfo>(d.GetRawText()) : BotDeviceInfo.GenerateInfo();
        var keys = !scan && data.RootElement.TryGetProperty("keys",out var k)
            ? JsonSerializer.Deserialize<BotKeystore>(k.GetRawText()) : new BotKeystore();
        signer = new Signer(signUrl);
        var config=new BotConfig { Protocol=Protocols.Linux, AutoReconnect=true,
            AutoReLogin=true, UseIPv6Network=false, CustomSignProvider=signer };
        bot = string.IsNullOrWhiteSpace(appInfo) ? BotFactory.Create(config,device,keys)
            : BotFactory.Create(config,device,keys,JsonSerializer.Deserialize<BotAppInfo>(appInfo));
        bot.Invoker.OnBotOnlineEvent += (_,e) => { if(stop.IsCancellationRequested)return;
            Online=true; persist(JsonSerializer.Serialize(new {device=bot.UpdateDeviceInfo(),keys=bot.UpdateKeystore()}));
            status("QQ 已连接 · "+bot.BotUin); };
        bot.Invoker.OnBotOfflineEvent += (_,e) => { Online=false; if(!stop.IsCancellationRequested)status("QQ 已离线，正在重连"); };
        bot.Invoker.OnBotCaptchaEvent += (_,e) => status("QQ 要求验证，请重新扫码登录");
        bot.Invoker.OnGroupMessageReceived += (_,e) => { var c=e.Chain;
            if(!stop.IsCancellationRequested && c.FriendUin!=bot.BotUin)
                receive(c.GroupUin??0,c.FriendUin,c.GroupMemberInfo?.MemberName??c.FriendUin.ToString(),string.Concat(c.OfType<TextEntity>().Select(x=>x.Text))); };
        bot.Invoker.OnFriendMessageReceived += (_,e) => { var c=e.Chain;
            if(!stop.IsCancellationRequested && c.FriendUin!=bot.BotUin)
                receive(0,c.FriendUin,c.FriendInfo?.Nickname??c.FriendUin.ToString(),string.Concat(c.OfType<TextEntity>().Select(x=>x.Text))); };
        if(!scan && keys.Uin>0){ status("QQ 正在恢复登录"); if(await bot.LoginByEasy(stop.Token).WaitAsync(TimeSpan.FromSeconds(25),stop.Token))return; }
        status("QQ 正在获取登录码");
        var code=await bot.FetchQrCode().WaitAsync(TimeSpan.FromSeconds(25),stop.Token);
        if(code==null)throw new InvalidOperationException("QQ 登录码获取失败");
        qr(code.Value.QrCode); status("请用 QQ 扫码，点击查看登录码");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        await bot.LoginByQrCode(deadline.Token);
    }
    public async Task<string> Send(uint target, bool group, string text, uint mention, CancellationToken token)
    {
        if(!Online || bot==null)return "QQ 未登录";
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(stop.Token,token);
        await sending.WaitAsync(linked.Token);
        try { var builder=group?MessageBuilder.Group(target):MessageBuilder.Friend(target);
            builder.Text(text); if(group && mention>0)builder.Mention(mention);
            var result=await bot.SendMessage(builder.Build()).WaitAsync(TimeSpan.FromSeconds(8),linked.Token);
            return result.Result==0?"":"QQ 拒绝发送（"+result.Result+"）";
        } finally { sending.Release(); }
    }
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;Online=false;stop.Cancel();bot?.Dispose();signer?.Dispose();}
    private sealed class Signer : SignProvider, IDisposable
    {
        private readonly Uri url;
        private readonly HttpClient http=new(new HttpClientHandler {AllowAutoRedirect=false}) {Timeout=TimeSpan.FromSeconds(8)};
        public Signer(string value){ if(!Uri.TryCreate(value,UriKind.Absolute,out url) ||
            !(url.Scheme=="https" || url.Scheme=="http" && url.IsLoopback) || url.UserInfo.Length>0)
                throw new ArgumentException("填写 HTTPS 签名地址或本机 HTTP 签名地址"); }
        public override byte[] Sign(string cmd,uint seq,byte[] body,out byte[] ver,out string token)
        {
            ver=null;token=null;if(!WhiteListCommand.Contains(cmd))return null;
            using var request=new HttpRequestMessage(HttpMethod.Post,url) { Content=new StringContent(
                JsonSerializer.Serialize(new {cmd,seq,src=Convert.ToHexString(body)}),Encoding.UTF8,"application/json") };
            using var response=http.Send(request);response.EnsureSuccessStatusCode();
            using var input=response.Content.ReadAsStream();using var output=new System.IO.MemoryStream();
            var buffer=new byte[2048];int count;while((count=input.Read(buffer,0,buffer.Length))>0){
                if(output.Length+count>16384)throw new InvalidOperationException("签名响应过大");output.Write(buffer,0,count);}
            using var json=JsonDocument.Parse(output.ToArray());var value=json.RootElement.GetProperty("value");
            ver=Convert.FromHexString(value.GetProperty("extra").GetString()??"");
            token=Encoding.UTF8.GetString(Convert.FromHexString(value.GetProperty("token").GetString()??""));
            return Convert.FromHexString(value.GetProperty("sign").GetString()??"");
        }
        public void Dispose()=>http.Dispose();
    }
}
