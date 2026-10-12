# 小烟酒手机 App / 手机app支持

手机支持模块版本 0.3.35，APK 仍为 0.3.34，无需重新安装。APK 软件名为“小烟酒”，Android 包名 `cn.xiaoyanjiu.ff14chat`。

QQ 扫码登录需先填写可用的 V1 签名服务地址。此版补充输入检查、登录进度和失败提示，生成登录码后自动打开；真实 QQ 登录仍取决于签名服务和协议版本。`tests/QQLoginRegression.csproj` 提供无需真实账号的回归检查，构建时指定 `YanjiuLibraryPath` 和 `DalamudLibPath`。

在 Omni 在线模块中安装并启用“手机app支持”。下载 [小烟酒 APK](https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/tag/phone-v0.3.35)，在手机填写电脑可访问的地址与端口，提交验证码，再由电脑模块确认设备授权。路由器转发需由用户配置；默认端口 18765。

使用个人云推送时，WxPusher、Server酱及 QQ 的凭据由用户自行设置。QQ 使用 Lagrange V1；真实登录仍需可用的 V1 签名服务和匹配协议版本，不提供内置公共签名服务。

- `../Modules/PhoneChat.cs`：可在 Omni 使用的单文件模块。
- `android/`：APK 对应源码、资源、构建脚本和说明。
- `third_party/Lagrange-V1/`：QQ 内嵌组件的对应源码、重建脚本和许可证。
- `LICENSE` / `../Modules/PhoneChat.LICENSE`：本功能 GPL-3.0-or-later 许可证。

嵌入 QRCoder 的 MIT 版权与许可证保留在模块文件内，Android 扫码库的许可证随源码保留。用户设备票据、推送凭据、QQ 登录数据及 APK 私人签名密钥均不随仓库发布。

独立卫月插件在 [Yanjiu 仓库](https://github.com/Xiao-Yanjiu/Yanjiu) 开发，本目录为 Omni 单文件模块版本。
