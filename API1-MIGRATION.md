# API 1 迁移（2026-10-28）

本次七个在线模块统一升级至 2.0.0，使用 ModuleApiVersion(1)，最低 Omni 版本为 1.1.8.2；使用 Omni 1.1.8.4 的实际公共库和当前 Dalamud API 15 编译验证。

## 用户更新

只保留一个仓库入口：https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu 。在 Omni 在线模块设置中刷新仓库并更新模块，首次安装后手动启用。无需添加备用 raw 链接，也无需安装 OmenTools、TreeTools 或 TreeToolKit。

若旧安装描述无法被新版宿主读取，按提示重新建立仓库记录。保留用户配置，不要删除 pluginConfigs。网络错误导致无法确认仓库公开时，宿主会阻止本次加载；修复连接后重试。

## 兼容性

- 入口改为 OmniModuleBase，移除旧 OnEnable/OnDisable/OnDispose 生命周期。
- 初始化移出公开构造函数；停用清理使用幂等入口，启动中途失败也会回收。Task.Run 工作登记到本次 FeatureLifetime，现有停止逻辑先取消工作、关闭连接，再由宿主等待任务结束。
- 字体改用 TreeToolKit.Fonts；发布文件不携带宿主共享库。
- 模块词条内嵌于发布文件，通过 ModuleLocalization.Create 交付，简繁词库由公共库生成与校验。组合框的选项分隔符在取词后还原，避免 XLIFF 拒绝空字符。
- 保留已有 JSON 属性、序列化属性、文件路径、命令别名及手机协议。只调整 C# 配置类型／绑定字段名；手机服务端配置统一由 config 字段提供，原 settings 使用方式保留为内部转发属性。
- 手机端不需要因本次宿主接口迁移更换 APK，既有配套 App 协议保持。手机页面及游戏画面串流模式未改动。
- 浏览器作为单个 DLL 发布；其 CEF 辅助程序和内核仍由原有流程提供。旧的版本化下载路径保留，新清单指向 2.0.0。

## 开发构建

Windows、.NET SDK 10：

```powershell
./Build/Build-OnlineModules.ps1 -OmniLibPath '宿主依赖目录' -DalamudLibPath '卫月依赖目录'
```

可用 `-Dotnet` 指定 dotnet.exe。省略依赖路径时查找 APPDATA 中的国服安装。输出到 `.artifacts/api1`，不修改已安装插件。更新后重新计算最终发布文件 SHA256，保持两份清单一致。

## 验证范围

七个模块 Release 编译通过；静态检查覆盖 API 声明、旧生命周期、构造函数、词条引用／格式参数、原配置属性及编译后的依赖类型。还使用真实 Omni 1.1.8.4 公共库调用 ModuleContract.ValidateType，并校验全部简繁目录，浏览器原有 20 项回归检查通过。

这些检查不启动模块、不操作角色，不替代游戏内功能实测。更新后应依次验证启用、停用、再次启用、语言切换、旧配置读取，以及浏览器、音乐播放、手机连接与串流；原生 Hook 功能需与当前游戏版本一并实测。

可在构建后离线运行实际宿主契约／简繁目录校验（不创建模块实例，不调用游戏 API）：

```powershell
dotnet run --project Tests/OnlineApi1/OnlineApi1.csproj -- . '宿主依赖目录' '卫月依赖目录'
```

已有源码保留部分可空性及过时 API 警告；本次不以关闭功能或更改用户行为来消除这些旧警告。
