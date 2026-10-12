# 小烟酒 Android App

当前源码版本：0.3.34；包名：`cn.xiaoyanjiu.ff14chat`。
Java 源码在 `java/`，WebView 界面在 `assets/`，Android 资源在 `res/`。

使用 PowerShell 7 运行 `build.ps1`。设置环境变量 `YANJIU_BUILD_TOOLS` 为本地构建工具目录。
该目录需要：`jdk/<JDK17目录>/bin`、`build-tools/<版本>/`、`platform/android-35/android.jar`、`zxing.jar`。
工具可以继续使用桌面“FF14聊天/构建工具”中的现有工具，构建输出写入仓库 `local/artifacts/`。

默认构建输出未签名 APK。正式签名时在本机设置：

- `YANJIU_KEYSTORE`：原签名密钥文件路径。
- `YANJIU_KEY_ALIAS`：密钥别名。
- `YANJIU_KEYSTORE_PASSWORD`：密钥库密码。
- `YANJIU_KEY_PASSWORD`：密钥密码，不填则使用密钥库密码。

密码只通过环境变量交给签名工具，脚本不包含密码或生成新的正式密钥。
继续沿用原密钥才能覆盖更新已安装 App。不要把密钥、密码、安装包或本机配置提交到 Git。
