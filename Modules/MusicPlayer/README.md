# 游戏内音乐播放器 1.3.1

QQ音乐和网易云音乐共用原有 UI，通过账号登录旁的下拉框切换。各平台独立维护登录态、曲库、喜欢列表、歌单缓存、播放队列、当前歌曲、音量和音频设备。切换页面不暂停另一平台，若同时播放会叠加出声，可分别暂停。

## 功能

- QQ 原有功能继续保留；网易云增加扫码登录、搜索、榜单、歌词、封面、喜欢/取消喜欢及账号歌单和完整曲目读取。
- 推荐榜单和收藏歌单在左侧竖排，歌单列表独立滚动，支持主题边框与阴影。去掉右侧重复的歌单名称和首数；长名称悬停查看。
- 网易云收藏页仅显示账号返回的歌单，不额外插入固定“我喜欢的音乐”入口；QQ 保留这个入口。
- 圆角悬浮窗、动态颜色、角色头顶歌词及播放控制复用现有 UI。
- 网易云标准 MP3 使用内置纯 C# 解码器转换为临时 PCM WAV，避免依赖系统 MP3 解码驱动。切歌取消过期下载；音频关闭后清理当前文件。无需 Node、额外 API 服务或网易云桌面客户端。
- 老配置与 QQ 队列继续使用；新增网易云配置独立保存。

## 使用和范围

通过仓库在线清单安装或更新，最低 Omni 版本 1.1.7.5。也可把上一级 `MusicPlayer.cs` 作为单文件本地模块加载。不要同时启用多个来源的同名模块。

网易云完整下载并解码后开始播放。版权、地区与会员权益仍由平台决定；仅试听、无可用音源或不支持的编码会提示原因。当前未加入 FLAC 无损及 YRC 逐字歌词。

账号凭据只用于对应平台请求；不要公开分享带 Cookie 的用户配置。第三方协议更新或平台风控可能导致部分接口需要维护。

## 构建与验证

Windows、.NET 10、Dalamud CN API 15，当前验证依赖为 Hooks 26-10-18-01 与 Omni 1.1.7.5。

```powershell
dotnet build Modules/MusicPlayer/MusicPlayer.csproj -c Release
dotnet run --project Modules/MusicPlayer/Tests/Checks.csproj -c Release
```

依赖路径可用 `-p:DalamudLibPath=目录` 和 `-p:OmniLibPath=目录` 覆盖。默认检查不联网；添加 `-- --live --audio` 会执行匿名接口和静音音频检查，不登录个人账号或写入收藏。

- Release 编译通过，44 项托管检查通过：账号与队列隔离、旧配置兼容、切换不暂停音频、协议编码、接口返回与错误、完整歌单、过期结果和解码清理。
- 匿名搜索、歌词、二维码 key 和 801 等待扫码状态实测成功；真实标准 MP3 经下载、内置解码后静音播放成功。
- 双音频设备同时播放、关闭其中一个不影响另一个，实测通过。
- 游戏内完整布局、扫码确认后的账号持久化、私人歌单和收藏写入仍需实机验证。

## 第三方源码

为兼容单文件模块，二维码库与 MP3 解码库源码内置在 `MusicPlayer.cs`。

- [QrCodeGenerator](https://github.com/manuelbl/QrCodeGenerator)，v2.0.4，MIT，命名空间调整为 OmniMusicQr。
- [NLayer](https://github.com/naudio/NLayer)，提交 046c7ce422970f8f0f0bc205d6c6341bcb7debf1，MIT，命名空间调整为 OmniMusicMpeg。
- 网易云请求协议参考 [NeteaseCloudMusicApiEnhanced](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced)。

许可证副本见 licenses 目录及单文件源码中的许可说明。

## 1.3.1 悬浮窗命令

启用模块后使用 `/omnimusic` 切换当前平台悬浮窗，`/omnimusic show` 显示，`/omnimusic hide` 隐藏；支持在动作前指定 `qq` 或 `netease`。控制悬浮窗不会停止歌曲。专用命令随模块启停注册/注销，不替换宿主的 `/omni`。

补齐显式的命名单参数/双参数分发兼容，宏更改标记保存，并将提示延迟到绘制线程处理。宿主默认转发已单独验证正常；无参数 `/omni MusicPlayer` 是否先走模块启停取决于宿主分发，控制悬浮窗优先使用专用命令。
