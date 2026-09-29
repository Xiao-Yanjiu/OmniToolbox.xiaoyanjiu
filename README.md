# OmniToolbox.xiaoyanjiu

小烟酒的 Omni Toolbox（树树妙妙屋）在线模块仓库。

## 模块列表

- **游戏内音乐播放器**（MusicPlayer）— QQ音乐播放器：真实播放（不需要装 QQ音乐客户端）、搜索、LRC 歌词滚动、扫码登录、我喜欢/收藏歌单读写、跟随歌曲变色的圆角悬浮窗（可固定在角色头顶）
- **游戏内浏览器**（GameWebBrowser）— 在游戏窗口内打开一个或多个网页浏览器（Edge/Chrome），用于 Discord、鱼糕等网站
- **游戏聊天同步 QQ**（ChatToQQ）— 把游戏聊天镜像同步到 QQ 群
- **自动丢垃圾**（AutoDiscardJunk）— 自动丢弃黑名单物品
- **自定义热键栏 Plus**（HotbarPlus）— 在屏幕上放置可自定义的技能栏：真实图标 / 冷却 / 资源消耗 / 连击高亮 / 距离判定全部由游戏原生计算，支持**多页翻页**、每格自定义键位、自定义图标与本地图片、一键导入游戏原生热键栏
- **宏图标替换**（MacroIconReplace）— 把游戏宏的图标替换成自定义图片（本地图片 / 网址图标），设置自动保存、重启或重载后自动重放，可一键还原成原生图标

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

## 关于 Node 运行时（只有音乐播放器需要）

音乐播放器的「我喜欢 / 取消收藏」写操作要走 QQ音乐 `musics.fcg` 加密通道，
而签名与加解密是官方 VMP 混淆脚本，无法在 C# 里复算，因此需要本机的 Node 运行时来执行。

**普通使用完全不用管它**：

1. **本机已经装了 Node.js**（或 PATH 里有 `node.exe`）→ 直接使用，**不会下载任何东西**；
2. **本机没有 Node** → 模块首次加载时自动下载安装一次，装到
   `%LOCALAPPDATA%\OmniMusic\node\node.exe`，之后就一直用它，**不会再重复下载**；
3. 自动安装失败不会反复重试：24 小时内不再自动尝试，也可以在
   「音乐播放器设置 → Node 运行时」里点「立即下载并安装 Node」，或手动指定 `node.exe` 路径。

下载源（模块内置、按顺序自动回退，任一成功即用）：

| 顺序 | 来源 | 实测速度 |
| --- | --- | --- |
| 1 | 本仓库 Release 附件（走 `gh-proxy.com` 反代镜像） | 约 500KB/s |
| 2 | 本仓库 Release 附件（走 `ghproxy.net` 反代镜像） | 约 300KB/s |
| 3 | 本仓库 Release 附件（走 `ghfast.top` 反代镜像） | 备用 |
| 4 | 本仓库 Release 附件（直连 GitHub） | 约 7KB/s，一般不用 |
| 5 | Node 官方发行包（npmmirror 国内镜像，终极兜底，不依赖本仓库） | 最快 |

对应 Release：<https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/tag/node-v22.22.2>

附件 SHA256：`7c93e9d92bf68c07182b471aa187e35ee6cd08ef0f24ab060dfff605fcc1c57c`
