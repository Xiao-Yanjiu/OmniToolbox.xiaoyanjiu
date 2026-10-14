# OmniToolbox.xiaoyanjiu

小烟酒的 Omni Toolbox（树树妙妙屋）在线模块仓库。

## 小烟酒手机 App

- **狒狒食饲《服务端》**（PhoneChat）— 需 Omni 1.1.6.6 或更新版本。
- [下载小烟酒 APK](https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/tag/phone-v0.3.36)
- 配置、对应源码和第三方许可见 [PhoneApp](PhoneApp/README.md)。

## 模块列表


- **游戏内音乐播放器**（MusicPlayer）— QQ音乐播放器：真实播放（不需要装 QQ音乐客户端）、搜索、LRC 歌词滚动、扫码登录、我喜欢/收藏歌单读写、跟随歌曲变色的圆角悬浮窗（可固定在角色头顶）。
 
- **游戏内浏览器**（GameWebBrowser）— DLL 模块，CEF 游戏内贴图浏览器与独立 Edge/Chrome 两种模式；沿用原窗口外观，共用网页、宏命令、代理和收藏，内核由用户点击下载并显示实时进度。详见 [浏览器说明](Browser/README.md)。
 
 
- **自动丢垃圾**（AutoDiscardJunk）— 三栏（黑名单 / 背包 / 白名单）管理随身背包，黑名单物品自动丢弃，HQ 与普通物品分开管理
 
- **更好的图标管理**（ToolbarIconPlus）— 网站图标（favicon）与本地图片入图标库后，可给游戏宏当图标（宏面板与热键栏原生显示），也可按编号把界面上任意图标换成图库图或别的游戏图标，支持一键还原；每个图标可单独设置是否走代理（代理地址在面板底部）
 
- **宏指令目录**（CommandCatalog）— 游戏与插件斜杠指令的实时目录，支持说明搜索、参数执行和复制，使用 /cmdcatalog 打开
 
- **IChing**（IChingModule）— 基于原作者代码制作，不保证全部功能可用
 

## 在 Omni 中添加

在「插件设置 → 在线模块」中填写清单地址。

地址：

```
https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu
```

链接：

```
https://raw.githubusercontent.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/main/TreeHouseModules.json
```

首次安装后模块默认关闭，在「树树妙妙屋 → 在线」中手动启用。
