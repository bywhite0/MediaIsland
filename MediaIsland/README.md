# MediaIsland

在 ClassIsland 主界面上显示 Windows [SMTC](https://learn.microsoft.com/en-us/uwp/api/windows.media.systemmediatransportcontrols) 媒体信息。

## 组件

**正在播放**

![截图](https://ghproxy.net/https://raw.githubusercontent.com/bywhite0/MediaIsland/master/Assets/screenshot.png)

- 可选暂停时隐藏
- 支持自定义显示的部分

**正在播放(简)**

![截图](https://ghproxy.net/https://raw.githubusercontent.com/bywhite0/MediaIsland/master/Assets/screenshot_snpc.png)

- 可选暂停时隐藏
- 多种显示样式

**实时歌词**

![截图](https://ghproxy.net/https://raw.githubusercontent.com/bywhite0/MediaIsland/master/Assets/screenshot_lyrics.gif)

- 可选显示状态文本
- 可选无内容时隐藏
- 可选显示音符图标
- 支持原文 / 翻译 / 音译（可配置缺失时显示原文或不显示）
- 支持固定歌词宽度
- 可调节渲染帧率
- 播放源为 SPlayer-Next 时，可直接使用其外部 API（默认 `http://127.0.0.1:14558`）歌词并跳过在线搜索
- 可选把搜到的歌词缓存到本地，换播放器听同一首歌也能直接命中（默认开启，可在设置页清空）
- 可把当前歌词「固定」到某首歌，之后始终优先使用它，不再受搜索结果变化影响
- 可直接指定本地歌词文件（`.lrc` / `.qrc` / `.krc` / `.ttml`）作为某首歌的固定歌词

## 要求

本插件需要 Windows 10 Build 17763 (1809) 或以上版本。

## 许可

本项目基于 GNU Affero General Public License v3.0 许可。
