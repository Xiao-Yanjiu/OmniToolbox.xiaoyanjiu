# 游戏内浏览器 1.4.0 测试版

本版将在线入口改为 `Modules/GameWebBrowser.dll`，取消 WebView2 抽帧模式，保留独立 Edge/Chrome 模式，使用 CEF 离屏模式承接原贴图外观。旧配置的 WebView2 选项自动迁移到 CEF。源码由 `Modules/GameWebBrowser.cs` 和 `Browser/Module/CefIntegration.cs` 共同组成，不再将前者作为单文件模块导入。

## 使用

在 Omni 刷新本仓库并更新游戏内浏览器。若旧版本仍在运行，停用后重新加载或重启 Omni，再选择“CEF 游戏内浏览器”。初次使用需要点击“下载 CEF”，进度条显示当前下载文件的已下载大小、总大小和速度，可以取消或失败后重试。没有安装内核时打开网页会显示提示，不会自动下载。

网页、收藏、宏命令和外观沿用现有配置。CEF 的登录资料独立保存，代理切换会重建窗口。CEF 输出 BGRA 像素，通过本机共享内存写入游戏设备的动态纹理，沿用原贴图模式的窗口、标题栏、缩放和主题。第一版不使用 GPU 跨进程共享纹理；不是外部窗口捕获，也没有图片压缩编码。

鼠标、滚轮、键盘与中文输入提交给 CEF 输入接口。此实现不伪造浏览器身份，也不承诺通过所有站点的人机验证。网页新窗口链接转为模块标签页；下拉列表的浏览器弹出画面合成进网页贴图。

## 下载与隔离

- 固定 CefSharp 154.0.340 / CEF 154.0.34 的匹配版本，不使用浮动 latest。
- 用户点击按钮后，下载 CefSharp 维护的 `chromiumembeddedframework.runtime` 官方 NuGet 包和本仓库 Release 的 `BrowserHost.zip`。
- 原生包按 NuGet 公布的 SHA512 校验，桥接包按源码内固定 SHA256 校验。安装文件另保存逐文件 SHA256 清单。
- 只提取 Windows x64 资源，限制解压大小并拒绝越界路径和重复文件。全部校验后才将临时目录移动为安装目录。
- 辅助进程自带 .NET 运行时；CEF 不加载进游戏。输入通过该进程的标准输入输出传递，没有远程监听端口。
- 画面只保留最新帧，游戏线程尝试立即取锁，拿不到就使用上一帧；支持最高 2560×1600 网页画面，超过时按显示区域缩放。
- 关闭窗口或停用模块终止本模块启动的进程树并释放共享画面；登录资料保留。

## 构建

需要 .NET 10 SDK、本机 Omni 1.1.6.6 和对应 Dalamud API 15 程序集。`Browser/Host/BrowserHost.csproj` 使用明确的本地托管接口引用，避免通过 NuGet 依赖链自动下载 CEF 原生包。

`CefManagedPath` 指向解压后的 `CefSharp.Common.NETCore` 和 `CefSharp.OffScreen.NETCore` 154.0.340 包，目录名分别为 `cefsharp.common.netcore` 和 `cefsharp.offscreen.netcore`。`OmniLibPath`、`DalamudLibPath` 指向实际安装目录。发布桥接程序时使用 `-r win-x64 --self-contained true`。

发行桥接 ZIP 中附带 CefSharp BSD 许可和 .NET 第三方声明；CEF 包安装时保留其许可文件。没有修改或再分发 Omni 程序集。

## 验证边界

本版在未下载 CEF 内核的条件下构建。可验证真实 Omni 引用编译、下载进度/取消、路径解压防护、共享帧数据及模块元数据；CEF 网页显示、实际输入法、站点验证和游戏帧率需要安装内核后实测。不能将编译通过当作上述功能已经在游戏中通过。
