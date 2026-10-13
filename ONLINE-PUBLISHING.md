# 在线模块发布

当前推荐清单入口：
https://raw.githubusercontent.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/main/TreeHouseModulesOnline.json

TreeHouseModulesOnline.json 与 TreeHouseModules.json 必须在每次发布时保持完全一致。
浏览器模块文件使用独立版本目录；已发布版本目录禁止覆盖。
先上传并验证版本目录中的实际文件，再发布两份清单的版本、路径和 SHA256。
禁止重复覆盖同一 DLL 下载地址，以免缓存清单与新版文件校验不一致。