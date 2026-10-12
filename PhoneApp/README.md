# 小烟酒手机 App / 手机app支持

手机支持模块版本 0.3.37，APK 版本 0.3.36（versionCode 29）。APK 软件名为“小烟酒”，Android 包名 `cn.xiaoyanjiu.ff14chat`。

个人 QQ 已改为 LLBot 8.3.2 官方 Windows CLI 的纯协议模式，移除 V1 签名地址输入。首次点击准备 LLBot（约 91 MB，下载后校验 SHA-256），到 https://auth.luckylillia.com 获取个人授权并填写 Token，再点击机器人 QQ 扫码登录；请用机器人账号扫码。可选填机器人 QQ 号，填后会核对扫码账号。恢复登录和随模块恢复 QQ 不自动下载程序；首次使用需先准备。现有推送顺位、群同步和白名单 # 回复游戏配置保持不变，V1 登录凭证不能迁移，QQ 首次需重新扫码。APK 保持 0.3.36，无需重装。

在 Omni 在线模块中安装并启用“手机app支持”。下载 [小烟酒 APK](https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/tag/phone-v0.3.36)，在手机填写电脑可访问的地址与端口，提交验证码，再由电脑模块确认设备授权。路由器转发需由用户配置；默认端口 18765。

使用个人云推送时，WxPusher、Server酱及 QQ 的凭据由用户自行设置。QQ 使用 LLBot，授权 Token 由用户自行获取；模块用 Windows 当前用户 DPAPI 保存，不上传到仓库。LLBot 是独立后台进程，不把原生 QQ 组件加载进游戏。它仍依赖官方授权服务，不能承诺永不掉线。

- `../Modules/PhoneChat.cs`：可在 Omni 使用的单文件模块。
- `android/`：APK 对应源码、资源、构建脚本和说明。
- `third_party/LLBot/`：当前 QQ 后端的官方来源、固定版本校验值和许可说明。`third_party/Lagrange-V1/` 留作旧发布版本的对应源码，当前模块不再加载。
- `LICENSE` / `../Modules/PhoneChat.LICENSE`：本功能 GPL-3.0-or-later 许可证。

嵌入 QRCoder 的 MIT 版权与许可证保留在模块文件内，Android 扫码库的许可证随源码保留。用户设备票据、推送凭据、QQ 登录数据及 APK 私人签名密钥均不随仓库发布。

独立卫月插件在 [Yanjiu 仓库](https://github.com/Xiao-Yanjiu/Yanjiu) 开发，本目录为 Omni 单文件模块版本。

## 手机内更新

首次安装 0.3.36 后，启动会自动检查本仓库的 PhoneApp/update.json；设置内也可点击检查并下载最新版。发现新版本后选择下载，下载显示进度，完成时校验大小、SHA-256、包名、版本号及与已安装应用一致的签名，再打开系统安装确认。首次需允许小烟酒安装应用；取消安装可再次检查更新。GitHub 无法访问时可稍后重试。更新不清除设备授权和聊天数据。

后续发布 APK 时必须使用原签名，递增 versionCode，在 Releases 上传 APK 并更新 update.json 的版本、下载地址、大小和 SHA-256。模块继续通过 TreeHouseModules.json 独立更新，无需随每次 APK 发布改版本。

更新地址回归检查见 tests/UpdatePolicyRegression.java；真实 Android 安装权限及覆盖安装需在设备上验证。
LLBot 回归检查：`tests/QQLoginRegression.csproj`。构建时指定 `YanjiuLibraryPath`、`DalamudLibPath`；运行参数为模块源码、Yanjiu 测试库目录、卫月 API 目录。可选第四参数为独立测试 runtime 目录，运行官方包无账号启动检查。测试区分本地接口验收与真实 QQ 登录，不能把本地 API 成功视为实际手机送达。