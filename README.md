# MediaIsland

MediaIsland 是一款 [ClassIsland](https://classisland.tech) 插件，用于在 ClassIsland 主界面上显示 Windows [SMTC](https://learn.microsoft.com/en-us/uwp/api/windows.media.systemmediatransportcontrols) 媒体信息。

![截图](./Assets/screenshot.png)
<details>
  <summary>更多使用方式...</summary>
    搭配 <img src="https://github.com/LiPolymer.png" alt="LiPolymer" style="width:16px" /><a href="https://github.com/LiPolymer">LiPolymer</a> 佬的 <a href="https://github.com/LiPolymer/ExtraIsland">ExtraIsland</a> 的【当前活动】组件，并启用「启用歌词」功能：
    <img src="Assets/screenshot_with_ei.png" alt="搭配ei使用截图" />
  </details>

> [!IMPORTANT]
>
> 本插件的内存占用较大，低性能设备请谨慎使用。

## 要求

本插件需要 Windows 10 Build 17763 (1809) 或以上版本。

## TODO

- [x] 显示播放器图标
- [x] 【正在播放】组件设置页面
- [ ] 完善【正在播放】组件设置（隐藏专辑封面、播放器等）
- [ ] 拆分 SMTC 事件处理到外部服务便于其他功能使用
- [ ] 播放控制及配套自动化功能（求大佬 PR）
- [ ] ~~与 [LyricsIsland](https://github.com/jiangyin14/LyricsIsland) 联动（？~~ 根据当前媒体信息获取并显示歌词（画大饼ing）

## 致谢

本项目使用了以下第三方库：

- [ClassIsland.PluginSdk](https://www.nuget.org/packages/ClassIsland.PluginSdk)
- [WindowsAPICodePackShell](https://www.nuget.org/packages/WindowsAPICodePackShell)
- [Dubya.WindowsMediaController](https://www.nuget.org/packages/Dubya.WindowsMediaController)

## 许可

本项目基于 GNU Affero General Public License v3.0 许可。

本项目部分使用了 [DubyaDude/WindowsMediaController](https://github.com/DubyaDude/WindowsMediaController) 的代码，其内容仍基于 MIT 许可。
