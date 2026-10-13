# 小烟酒手机 App / 手机app支持

手机支持模块版本 0.3.38，APK 版本 0.3.36（versionCode 29）。APK 软件名为“小烟酒”，Android 包名 `cn.xiaoyanjiu.ff14chat`。

个人 QQ 已改为 NapCat 4.18.38 官方 Windows Node 包，包含 QQ 运行组件，无需申请 LLBot 授权或填写签名地址。首次点击准备 QQ 服务（约 118 MB，下载后校验 SHA-256），再点击机器人 QQ 扫码登录；请用机器人账号扫码。二维码直接显示在模块面板，也可点击查看登录码打开 PNG。可选填机器人 QQ 号，填后会核对扫码账号。恢复登录和随模块恢复 QQ 不自动下载程序；首次使用需先准备。推送顺位、群同步和白名单 # 回复游戏配置保持不变，旧后端登录凭证不能迁移，首次需重新扫码。APK 保持 0.3.36，无需重装。

在 Omni 在线模块中安装并启用“手机app支持”。下载 [小烟酒 APK](https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/tag/phone-v0.3.36)，在手机填写电脑可访问的地址与端口，提交验证码，再由电脑模块确认设备授权。路由器转发需由用户配置；默认端口 18765。

使用个人云推送时，WxPusher、Server酱及 QQ 的凭据由用户自行设置。QQ 使用本地 NapCat 服务，不使用 LLBot 审核或签名服务。模块自动生成仅本机有效的接口密码，不要求用户填写；QQ 原生组件只运行在独立子进程，不加载进游戏。QQ 账号自己的登录验证或风控仍可能要求手机确认，不能承诺永不掉线。

- `../Modules/PhoneChat.cs`：可在 Omni 使用的单文件模块。
- `android/`：APK 对应源码、资源、构建脚本和说明。
- `third_party/NapCat/`：当前 QQ 后端的官方来源、固定版本校验值和许可说明。`third_party/LLBot/` 留作旧版本记录，不再加载。`third_party/Lagrange-V1/` 留作旧发布版本的对应源码，当前模块不再加载。
- `LICENSE` / `../Modules/PhoneChat.LICENSE`：本功能 GPL-3.0-or-later 许可证。

嵌入 QRCoder 的 MIT 版权与许可证保留在模块文件内，Android 扫码库的许可证随源码保留。用户设备票据、推送凭据、QQ 登录数据及 APK 私人签名密钥均不随仓库发布。

独立卫月插件在 [Yanjiu 仓库](https://github.com/Xiao-Yanjiu/Yanjiu) 开发，本目录为 Omni 单文件模块版本。

## 手机内更新

首次安装 0.3.36 后，启动会自动检查本仓库的 PhoneApp/update.json；设置内也可点击检查并下载最新版。发现新版本后选择下载，下载显示进度，完成时校验大小、SHA-256、包名、版本号及与已安装应用一致的签名，再打开系统安装确认。首次需允许小烟酒安装应用；取消安装可再次检查更新。GitHub 无法访问时可稍后重试。更新不清除设备授权和聊天数据。

后续发布 APK 时必须使用原签名，递增 versionCode，在 Releases 上传 APK 并更新 update.json 的版本、下载地址、大小和 SHA-256。模块继续通过 TreeHouseModules.json 独立更新，无需随每次 APK 发布改版本。

更新地址回归检查见 tests/UpdatePolicyRegression.java；真实 Android 安装权限及覆盖安装需在设备上验证。
NapCat 回归检查：`tests/QQLoginRegression.csproj`。构建时指定 `YanjiuLibraryPath`、`DalamudLibPath`；运行参数为模块源码、Yanjiu 测试库目录、卫月 API 目录。可选第四参数为独立测试 runtime 目录，运行官方包无账号启动检查。测试区分本地接口验收与真实 QQ 登录，不能把本地 API 成功视为实际手机送达。
NapCat 的运行文件校验后只读，账号数据保存在模块配置目录下 OmniPhoneChat-NapCat/qq-data，运行设置在 work/config。独立宿主预加载适配器只改变自身及接收子进程的数据目录返回值，不修改上游源码、原生文件或桌面 QQ 的全局设置。已验证中文及空格路径。HTTP 接口和 WebUI 使用独立随机端口，仅绑定 127.0.0.1，本地接口密码每次启动重新生成。

验证包括真实无账号二维码生成、PNG 内容解码、后台启动、本地 API 鉴权/业务失败、路径隔离和进程清理；真实用户扫码确认、QQ 消息送达、游戏内回复及持续在线还需实际使用验证。图片编解码回归见 tests/QrImageRegression.java（使用 Android 构建工具中的 ZXing）。