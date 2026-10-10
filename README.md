# OmniToolbox.xiaoyanjiu

小烟酒的 Omni Toolbox（树树妙妙屋）在线模块仓库。

## 模块列表

- **游戏内音乐播放器**（MusicPlayer）— QQ音乐播放器：真实播放（不需要装 QQ音乐客户端）、搜索、LRC 歌词滚动、扫码登录、我喜欢/收藏歌单读写、跟随歌曲变色的圆角悬浮窗（可固定在角色头顶）。无需 Node 或任何额外运行库
- **游戏内浏览器**（GameWebBrowser）— 在游戏窗口内打开一个或多个网页浏览器（Edge/Chrome），用于 Discord、鱼糕等网站，可设置窗口位置与大小，支持无边框 app 模式与网络代理
- **游戏聊天同步 QQ**（ChatToQQ）— 把游戏聊天镜像同步到 QQ 群，支持同步 Omni「国服频道」（跨服世界聊天）
- **自动丢垃圾**（AutoDiscardJunk）— 自动丢弃黑名单物品
- **更好的图标管理**（ToolbarIconPlus）— 网站图标（favicon）与本地图片入图标库后，可给游戏宏当图标（宏面板与热键栏原生显示），也可按编号把界面上任意图标换成图库图或别的游戏图标，支持一键还原

## 在 Omni 中添加

在「插件设置 → 在线模块」中填写清单地址。**国内推荐用镜像地址**（`raw.githubusercontent.com`
在部分网络环境下会被阻断，直连 GitHub API 也容易限流）：

```
https://gh-proxy.com/https://raw.githubusercontent.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/main/TreeHouseModules.json
```

备选地址（按稳定性排序，任选其一）：

```
https://ghproxy.net/https://raw.githubusercontent.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/main/TreeHouseModules.json
https://raw.githubusercontent.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/main/TreeHouseModules.json
https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu
```

首次安装后模块默认关闭，在「树树妙妙屋 → 在线」中手动启用。

## 关于运行环境

**所有模块都是单一 `.cs` 文件，不需要任何额外运行库或前置插件。**

音乐播放器的「我喜欢 / 取消收藏」写操作虽然要走 QQ音乐 `musics.fcg` 加密通道，但加解密与请求签名
（AES-128-GCM + 固定密钥循环异或 + `zzc` 签名）已经全部在模块内用 .NET 原生密码学实现，
**不需要 Node.js，也不会下载任何东西**。
