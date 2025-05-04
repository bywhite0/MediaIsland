# MediaIsland

MediaIsland 是一款 [ClassIsland](https://classisland.tech) 插件，用于在 ClassIsland 主界面上显示 Windows [SMTC](https://learn.microsoft.com/en-us/uwp/api/windows.media.systemmediatransportcontrols) 媒体信息。

![截图](./Assets/screenshot.png)

> [!NOTE]
>
> 本项目由 WinForms 迁移而来，目前代码还是一片屎山，欢迎大家参与贡献🙏

## 要求

本插件需要 Windows 10 Build 17763 (1809) 或以上版本。

## TODO

- [x] 显示播放器图标
- [x] 【正在播放】组件设置页面
- [ ] 完善【正在播放】组件设置（隐藏专辑封面、播放器等）
- [ ] 与 [LyricsIsland](https://github.com/jiangyin14/LyricsIsland) 联动（？

## 致谢

本项目使用了以下第三方库：

- [ClassIsland.PluginSdk](https://www.nuget.org/packages/ClassIsland.PluginSdk)
- [Microsoft-WindowsAPICodePack-Core](https://www.nuget.org/packages/Microsoft-WindowsAPICodePack-Core/)
- [WindowsAPICodePackShell](https://www.nuget.org/packages/WindowsAPICodePackShell)
- [Dubya.WindowsMediaController](https://www.nuget.org/packages/Dubya.WindowsMediaController)

## 许可

本项目基于 GNU Affero General Public License v3.0 许可。

本项目部分使用了 [DubyaDude/WindowsMediaController](https://github.com/DubyaDude/WindowsMediaController) 的代码，其内容仍基于 MIT 许可。
