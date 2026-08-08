# MediaLink WebSocket 协议

> 本文档是面向客户端开发者的独立参考。实现一个基本客户端只需 WebSocket 支持和本文档。

## 概述

MediaLink 是 ClassIsland 媒体信息插件提供的本地 WebSocket 推送服务。客户端通过 `ws://` 连接，经过认证与订阅后，即可接收当前播放的媒体信息和逐字歌词。

- **传输**: 明文 `ws://`（无 TLS）
- **默认地址**: `ws://127.0.0.1:21757/v1/ws`
- **协议版本**: `v: 1`（仅追加，不破坏已有客户端）
- **认证**: Token（base64url 字符串，在 ClassIsland 设置页生成）

## 连接

### WebSocket 握手

标准 WebSocket 握手，路径为 `/v1/ws`。支持 query 参数（如 `/v1/ws?foo=1`）。

服务端校验以下请求头（全部不通过则拒绝连接）:

| 请求头 | 要求 | 拒绝时状态码 |
|--------|------|-------------|
| 路径 | 精确为 `/v1/ws`（允许 query） | 404 |
| `Connection` | 包含 `Upgrade` | 400 |
| `Upgrade` | `websocket` | 400 |
| `Sec-WebSocket-Version` | `13` | 426 |
| `Sec-WebSocket-Key` | 存在且为 16 字节 base64 | 400 |
| `Host` | 匹配监听地址 / localhost / 127.0.0.1 | 403 |
| `Origin`（可选） | 在允许白名单内 | 403 |

### 握手超时

服务端等待完整请求头的时间上限为 **5 秒**，超时则断开。

## 信封格式

所有消息（客户端→服务端和服务端→客户端）使用统一 JSON 信封:

```json
{
  "v": 1,
  "type": "消息类型",
  "id": "请求ID（可选）",
  "ts": 1710000000000,
  "seq": 42,
  "name": "事件名（仅 event 类型）",
  "payload": { ... }
}
```

| 字段 | 类型 | 必需 | 说明 |
|------|------|------|------|
| `v` | int | 是 | 协议版本，当前为 1 |
| `type` | string | 是 | 消息类型（见下文） |
| `id` | string | 否 | 请求-响应关联 ID |
| `ts` | long | 是 | Unix 毫秒时间戳 |
| `seq` | long | 否 | 单调递增序号（默认 0 时不发送） |
| `name` | string | 否 | 事件类型名（仅 `type=event` 时存在） |
| `payload` | object | 否 | 消息载荷 |

> **必须容忍**未知字段和未知枚举值，忽略即可。

## 消息类型

### 认证

#### `auth`（客户端→服务端）

连接后 10 秒内必须发送，否则被强制断开。

```json
{
  "type": "auth",
  "id": "a1",
  "v": 1,
  "ts": 1710000000000,
  "payload": {
    "token": "你的Token字符串"
  }
}
```

服务端响应:

- `auth_ok` — 认证成功
- `auth_fail` — Token 错误，随后断开连接
- `error` + `unauthorized` — 未认证即发送业务消息

#### 认证失败限速

同一 IP 认证失败 **5 次/60 秒**后，新 TCP 连接将被立即关闭。

### 订阅

#### `subscribe`（客户端→服务端）

```json
{
  "type": "subscribe",
  "id": "s1",
  "v": 1,
  "ts": 1710000000000,
  "payload": {
    "channels": ["media", "lyrics"]
  }
}
```

可用频道:

| 频道 | 说明 |
|------|------|
| `media` | 媒体信息变更事件 |
| `lyrics` | 歌词搜索结果事件 |
| `audio` | PCM 音频帧（二进制，见「音频」一节） |

服务端响应 `subscribe_ok`，随后推送快照（当前状态）和增量事件。

> `subscribe` 的语义是**整体替换**而非追加：服务端以本次请求的 `channels` 取代原有订阅集合。
> 因此追加一个频道时必须把已订阅的频道一并带上，只发新增的那一个会把其余频道顶掉。
> 需要精确移除某个频道时用 `unsubscribe`。

#### `unsubscribe`（客户端→服务端）

退订部分频道:

```json
{
  "type": "unsubscribe",
  "id": "u1",
  "v": 1,
  "ts": 1710000000000,
  "payload": {
    "channels": ["lyrics"]
  }
}
```

服务端响应 `unsubscribe_ok`，携带已移除的频道列表。退订未订阅的频道会返回 `error` + `bad_request`。

### 心跳

#### `ping` / `pong`

```json
{"type": "ping", "id": "p1", "v": 1, "ts": 1710000000000}
```

服务端回复 `pong`，携带相同的 `id`。

## 事件

### `media.updated`

媒体状态变更。`payload` 结构:

```json
{
  "changeKind": "MediaProperties",
  "sourceApp": "Spotify.exe",
  "title": "歌曲名",
  "artist": "歌手",
  "albumTitle": "专辑",
  "positionMs": 30000,
  "durationMs": 180000,
  "playbackState": "Playing",
  "playbackRate": 1.0,
  "hasThumbnail": true,
  "trackToken": "abc123def",
  "positionCapturedAtMs": 1710000030000,
  "serverTimeMs": 1710000030050
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `changeKind` | string | 变更类型：`CurrentSession` / `MediaProperties` / `Playback` / `Timeline` |
| `sourceApp` | string | 来源进程名 |
| `title` | string? | 歌曲名 |
| `artist` | string? | 歌手 |
| `albumTitle` | string? | 专辑名 |
| `positionMs` | long | 当前播放位置（毫秒） |
| `durationMs` | long | 总时长（毫秒） |
| `playbackState` | string | 播放状态：`Playing` / `Paused` / `Stopped` / `Opened` / `Changing` / `Closed` / `Unknown` |
| `playbackRate` | double? | 播放速率 |
| `hasThumbnail` | bool | 是否有封面（获取方式见缩略图端点） |
| `trackToken` | string | 曲目标识，同一曲目稳定不变，切歌时变化 |
| `positionCapturedAtMs` | long | 位置采样时刻（Unix 毫秒） |
| `serverTimeMs` | long | 消息发送时刻（Unix 毫秒） |

#### 进度插值

服务端**不会**周期性推送位置。播放中位置由客户端本地插值得出，仅在
`playbackState` / `playbackRate` 变化或切歌时才有新的 `media.updated`。

客户端应使用三元组 `(positionMs, positionCapturedAtMs, serverTimeMs)` 插值：

```
若 playbackState == "Playing":
    位置 = positionMs
         + ((serverTimeMs - positionCapturedAtMs) + (本地当前时刻 - 本地收帧时刻))
           × playbackRate
否则:
    位置 = positionMs
```

两个差值各自在**同一个时钟内**求得，因此客户端不需要与服务端对时：

- `serverTimeMs - positionCapturedAtMs`：服务端内部从采样到发送的延迟，两值都出自服务端时钟。
- `本地当前时刻 - 本地收帧时刻`：客户端收到该帧后经过的时间，两值都出自客户端时钟。

> **不要**写成 `本地当前时刻 - serverTimeMs`。那是拿两台机器的时钟直接相减，
> 只在双方时钟严格同步时才成立；跨机消费（例如另一台设备上的歌词条）会得到
> 一个等于时钟偏差的固定误差。

本地时刻建议取单调时钟（浏览器 `performance.now()`、Python
`time.monotonic()`），避免系统时间被 NTP 校正时进度跳变。

结果应按 `[0, durationMs]` 截断。

JavaScript 参考实现：

```js
// 收到 media.updated 时：recvAt = performance.now()，并存下 payload
function positionNow(p, recvAt) {
  if (p.playbackState !== 'Playing') return p.positionMs;
  const elapsed = (p.serverTimeMs - p.positionCapturedAtMs)
                + (performance.now() - recvAt);
  const pos = p.positionMs + elapsed * (p.playbackRate || 1);
  return Math.min(Math.max(0, pos), p.durationMs || pos);
}
```

### `lyrics.updated`

歌词搜索结果。`payload` 结构:

```json
{
  "id": "歌词ID",
  "title": "歌曲名",
  "artist": "歌手",
  "durationMs": 180000,
  "score": 87,
  "source": "QqMusic",
  "trackToken": "abc123def",
  "document": { ... }
}
```

`trackToken` 用于关联歌词与当前曲目。当歌词到达晚于切歌时，`lyrics.trackToken` 可能与当前 `media.trackToken` 不同，客户端应据此丢弃错配歌词。

### `server.hello`

连接后服务端自动发送:

```json
{
  "type": "event",
  "name": "server.hello",
  "v": 1,
  "ts": 1710000000000,
  "payload": {
    "protocolVersion": 1,
    "authRequired": true,
    "sessionEpoch": 3,
    "capabilities": ["audio"],
    "audio": { "sampleRate": 48000, "channels": 2, "format": "s16le" }
  }
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `protocolVersion` | int | 服务端协议版本 |
| `authRequired` | bool | 是否需要认证 |
| `sessionEpoch` | long | 监听器实例代号，进程内单调递增 |
| `capabilities` | string[]? | 服务端支持的可选能力；缺失表示只支持基础频道 |
| `audio` | object? | 音频线格式；`capabilities` 含 `audio` 时存在 |

`sessionEpoch` 用于识别服务端重启：`seq` 在监听器重建后从 0 重新开始，客户端若一律「丢弃 seq ≤ 已处理值」会永久停止更新。正确做法是**发现 `sessionEpoch` 变化时重置已处理的 seq 水位**。

#### 消费 capabilities

`server.hello` 并非只在握手时出现一次：**同一条连接存活期间服务端可能再次发送**，
用于重新声明当前状态。每条 hello 都是一份完整声明，客户端应**以最新一条为准**
覆盖已记录的能力，而不是只在握手时记一次——否则中途的能力变化无从察觉。

缺 `capabilities` 字段的旧服务端应按空集合处理，即「不支持任何可选能力」。

> 订阅 `audio` 前应先检查 `capabilities` 是否含 `"audio"`。旧版服务端不认识该频道，
> 会以 `bad_request` 拒绝整个 `subscribe` 请求——包括其中的 `media` 与 `lyrics`。

## 控制指令（Phase 2）

认证后可发送以下指令控制媒体播放:

| 类型 | 说明 |
|------|------|
| `media.inject` | 注入虚拟媒体信息 |
| `lyrics.inject` | 注入歌词 |
| `media.clear_inject` | 清除注入 |
| `playback.command` | 播放控制（play / pause / next / previous） |
| `thumbnail.get` | 取当前封面（见「封面」一节） |
| `audio.play_start` / `audio.play_stop` | 音频采集开关（见「音频」一节） |

详见源码 `MediaLinkSession.cs` 中的 `HandleMediaInjectAsync` 等方法。

### `media.inject`

```json
{
  "type": "media.inject",
  "id": "inj1",
  "v": 1,
  "ts": 1710000000000,
  "payload": {
    "sourceApp": "upstream-instance",
    "title": "歌曲名",
    "artist": "歌手",
    "albumTitle": "专辑",
    "positionMs": 30000,
    "durationMs": 180000,
    "playbackState": "Playing",
    "playbackRate": 1.0,
    "positionAgeMs": 120
  }
}
```

| 字段 | 类型 | 必需 | 说明 |
|------|------|------|------|
| `title` | string | 是 | 空白则 `bad_request` |
| `sourceApp` | string? | 否 | 省略时为 `external` |
| `positionMs` / `durationMs` | long | 否 | 必须 ≥ 0 |
| `playbackState` | string | 是 | 同 `media.updated` 的取值 |
| `playbackRate` | double? | 否 | 默认 1.0 |
| `positionAgeMs` | long? | 否 | `positionMs` 的采样距今已过去多少毫秒，默认 0 |

#### `positionAgeMs` 与转发

把本实例收到的 `media.updated` 转发给下游实例时，**必须**填 `positionAgeMs`，
否则每转发一跳，进度都会落后一次传输与处理的耗时。

用相对量而非绝对时间戳是刻意的：绝对值要求两台机器时钟同步，相对值只依赖
发送方自己的时钟差。计算方式与 §进度插值同源：

```
positionAgeMs = (serverTimeMs - positionCapturedAtMs) + (本地当前时刻 - 本地收帧时刻)
```

服务端会据此把时间基准回拨，使注入后的位置从采样那一刻算起继续推进。
`playbackState` 非 `Playing` 时该字段无效——暂停的曲目位置不随时间前进。

#### 实例间消费

MediaIsland 自带客户端实现，可直接消费另一台实例的推送：在设置页「消费其它实例」
中填入对方的地址与 Token 即可。收到的媒体与歌词写入本机的**外部注入**，因此还需
把「媒体源模式」设为「外部优先」或「仅外部注入」才会生效——保持「仅系统会话」时
数据会被收下但不参与合成。

推送与消费相互独立，一台实例可以只推、只收，或两者同时（即转发中继）。

**配置码**：设置页可把本机的「地址 + 密钥」打包成一串 `medialink:` 开头的文本，
在另一台设备一键粘贴导入，免去手抄 32 字节密钥。

> 配置码只是 base64url 编码的明文 JSON，**不是加密**。任何拿到它的人都能解出密钥，
> 只应发给信任的设备。之所以编码而非直接展示 JSON：避免用户漏抄花括号与引号，
> 也让「这是机密」在视觉上更明显。

歌词的 `source` 字段在转发时保留其**最初来源**（如 `QqMusic`）而非转发通道，
故多跳转发后仍能看出歌词实际来自哪里。


## 音频

音频帧不走 JSON 信封，使用 WebSocket **二进制帧**。一帧 20ms 的 PCM 约 3840 字节，
base64 进 JSON 会膨胀三分之一，且每帧都要过一遍序列化器。

订阅 `audio` 频道后开始接收。格式由 `server.hello` 的 `audio` 字段声明，当前恒为
48000Hz / 2 声道 / i16 小端交错。

### 帧长约定

第 1 期约定每帧 **20ms**：48000Hz × 2 声道 × i16 → PCM 部分 **3840 字节**。

这是**约定，不是协议强制**。帧头不携带时长字段，`server.hello` 的 `audio` 声明也只有
`sampleRate` / `channels` / `format`，因此帧长无法从协议本身推导。接收端**不应假设固定帧长**，
应按实际收到的 PCM 字节数计算本帧时长：

```
帧时长(ms) = PCM 字节数 / (sampleRate × channels × 每样本字节数) × 1000
```

把 20ms 写死在接收端，采集侧一旦改帧长，缓冲时长会静默漂移而不报错。
下文「8 帧约 160ms」等时长换算都建立在这条约定之上。

### 订阅 audio

`subscribe` 是整体替换语义（见「订阅」一节），所以订阅音频时必须**一次带上全部需要的频道**：

```json
{"channels": ["media", "lyrics", "audio"]}
```

只发 `["audio"]` 会把 `media` 与 `lyrics` 顶掉。

> 这条面向**自行实现音频订阅的客户端**。MediaIsland 自带的实例间消费客户端在第 1 期
> 并不订阅 `audio`，订阅音频是第 3 期才引入的行为。

### 帧格式

定长头 34 字节，之后是变长 `trackToken` 与裸 PCM，全部小端：

```
偏移  长度  字段
0     2    magic = 0xA1 0x01
2     1    version = 1
3     1    flags              bit0: 静音帧  bit1: 曲目首帧
4     8    startPositionMs    i64  本块首采样对应的曲目位置
12    8    capturedAtMs       i64  采样时刻（发送端时钟）
20    8    serverTimeMs       i64  发送时刻（发送端时钟）
28    4    seq                u32  音频帧独立序号
32    2    trackTokenLen      u16  UTF-8 字节数
34    N    trackToken         UTF-8
34+N  ...  PCM                i16 交错
```

`trackToken` 变长置于末尾，使头部定长部分可按固定偏移读取。

magic 或 `version` 不匹配、长度不足头部要求时，接收端**丢弃该帧但不断开连接**——未知 magic
可能是未来版本的其他二进制帧类型，按「必须容忍未知」原则处理。

### 三个时间戳

- `startPositionMs` — 曲目轴上的位置，用于把音频与歌词对齐。
- `capturedAtMs` / `serverTimeMs` — 同一时钟内求差的一对值，用法与 §进度插值
  完全相同：`(serverTimeMs - capturedAtMs) + (本地当前时刻 - 本地收帧时刻)` 即
  该帧的总延迟。**不要**拿 `serverTimeMs` 与本地时钟直接相减。

`seq` 独立于 JSON 事件的 `seq`，因两者队列独立、丢弃策略不同。

### trackToken 校验

与歌词同理：帧的 `trackToken` 与当前曲目不符时应丢弃，避免切歌后把上一首的音频
配到新曲目上。

### 控制指令

| 类型 | 说明 |
|------|------|
| `audio.play_start` | 请求开始推送音频 |
| `audio.play_stop` | 停止推送 |

均需先认证。服务端回 `ok`，携带相同的 `id`，`payload.for` 为原请求的 `type`。

两者只表达**采集意愿**，不改变订阅状态——订阅仍由 `subscribe` / `unsubscribe` 管理。
无订阅者时服务端不进行音频采集，因此必须显式发送 `audio.play_start`。

> 第 1 期只冻结协议表面，**采集尚未接入**：`audio.play_start` 会正常返回 `ok`，
> 但在采集实现（第 2 期）落地前不会有音频帧推出。入站音频帧同样会被正常解码后丢弃。
> 客户端可据此实现并联调协议，但不要期待第 1 期服务端产生音频。

### 注入（转发链路）

音频的注入**没有独立消息类型**。一条连接上入站的音频二进制帧即是注入，格式与推送
完全相同——方向已由「谁发的」确定，无需再用类型字段区分。这与 `media.inject` /
`lyrics.inject` 需要独立类型不同：那两者的入站与出站载荷形状本就不一样。

转发时**原样透传**帧，不重组、不改写时间戳。`media.inject` 需要 `positionAgeMs`
来补偿每一跳的耗时，音频帧则因头部自带 `capturedAtMs` / `serverTimeMs` 而不需要——
接收端用同一个公式即可算出累计延迟。

### 背压

音频有独立于 JSON 帧的出站队列（8 帧，按上述 20ms 约定约 160ms），**满时丢最旧，不关闭连接**。

这与 §出站队列与背压 描述的 JSON 队列策略相反，是刻意的：可视化只关心「现在在
响什么」，积压的历史帧已经过期；播放侧的连续性由接收端缓冲负责。

因此音频 `seq` 跳号是正常的背压结果，不代表故障。

## 封面

有两条获取途径，任选其一。已经建立 WebSocket 连接的客户端**建议走 WebSocket**：无需把 Token 放进 URL，也不必为一张图另开一条 TCP 连接。

### 途径一：WebSocket（推荐）

#### `thumbnail.get`（客户端→服务端）

认证后可发送，不要求订阅任何频道。

```json
{
  "type": "thumbnail.get",
  "id": "th1",
  "v": 1,
  "ts": 1710000000000,
  "payload": {
    "trackToken": "abc123def"
  }
}
```

`payload.trackToken` 可选。填入时服务端会与当前有效曲目比对，不一致则返回空数据——这可以防止切歌后把上一首的封面配到新曲目上。省略则不做校验。

服务端回复 `thumbnail`，携带相同的 `id`：

```json
{
  "type": "thumbnail",
  "id": "th1",
  "v": 1,
  "ts": 1710000000000,
  "payload": {
    "trackToken": "abc123def",
    "mimeType": "image/png",
    "dataBase64": "iVBORw0KGgo..."
  }
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `trackToken` | string? | 本次封面所属曲目的标识；无媒体时为 null |
| `mimeType` | string? | 有数据时为 MIME 类型，否则为 null |
| `dataBase64` | string? | base64 编码的图片数据；无可用封面时为 null |

`dataBase64` 为 null 的三种情形——曲目无封面、封面超过 1 MiB、`trackToken` 与当前曲目失配——**都不是错误**，客户端按「当前无可用封面」处理即可。此时 `trackToken` 仍会返回当前值，客户端可据此判断是否需要重新请求。

无任何媒体会话时返回 `error` + `no_session`。

浏览器端可直接拼成 data URL：

```js
img.src = `data:${p.mimeType};base64,${p.dataBase64}`;
```

### 途径二：HTTP 端点

```
GET /v1/thumbnail?token=<token>&t=<trackToken>
```

适用于无法维持 WebSocket 连接的场景（例如把 URL 直接填进 OBS 图像源）。

- 与 WebSocket 共用同一 Token 与同一监听端口
- `t` 参数可选，用于缓存失效，服务端只做存在性比对
- 成功返回 `image/png`，`Cache-Control: no-store`
- 401: Token 不匹配
- 404: 无媒体、无封面或 `t` 已过期

> **安全提示**: 此 URL 含 Token，不应写入日志或分享。浏览器 `<img>` 无法携带自定义请求头，这是该端点只能用 query 传 Token 的原因；能用 WebSocket 时优先用 WebSocket。

封面上限为 **1 MiB**，超限时两条途径都按「无封面」处理。

## 错误码

| 错误码 | 说明 | 是否关闭连接 |
|--------|------|-------------|
| `unauthorized` | 未认证或 Token 错误 | 是 (1008) |
| `bad_request` | 请求格式错误 | 否 |
| `protocol_error` | 协议级错误 | 是 |
| `internal` | 服务端内部错误 | 否 |
| `rate_limited` | 出站队列溢出 | 是 (1011) |
| `not_supported` | 功能未启用 | 否 |
| `no_session` | 无活跃媒体会话 | 否 |

### 服务端主动关闭

服务停止（插件禁用、设置变更触发监听器重建、宿主退出）时，服务端会先发
**1001 GoingAway** 再断开，不携带 `error` 消息。

客户端据此区分「服务端正常下线」与「网络故障」：收到 1001 表示对端有序停止，
通常应当继续重连等待其恢复；而 1006（异常断开）则可能是网络问题。
注意 Token 变更同样走这条路径——重连时需要使用新 Token。

## 出站队列与背压

- 每个客户端有独立的 64 槽位出站队列
- 队列满时只挤掉**最旧的可丢帧**（`media.updated`，客户端有插值兜底）；
  队头恰好是歌词或控制帧时不会被误丢
- `lyrics.updated` 与控制帧不可丢。队列满且无可丢帧可牺牲时，以 `rate_limited` + 1011 关闭该客户端
- 单帧发送超时 5 秒，超时视同该会话故障并断开
- 一个慢客户端**不影响**其他客户端的推送

因此 `media.updated` 的 `seq` 可能跳号，这是正常的背压结果，不是消息丢失故障；
`lyrics.updated` 的 `seq` 不会因背压跳号。

## 序列号 (`seq`)

每个事件携带单调递增的 `seq`（从 1 开始）。客户端可用于:

- 检测消息乱序（丢弃 `seq` 不大于已处理值的消息）
- 检测消息丢失（`seq` 跳变，通常是队列丢弃了可丢帧）

> **重要**: `seq` 在服务端重建监听器后从 0 重新开始。客户端必须在 `server.hello` 的
> `sessionEpoch` 变化时重置已处理水位，否则重启后会永久停止更新。

## 最小客户端示例

```python
import asyncio, websockets, json

async def main():
    async with websockets.connect("ws://127.0.0.1:21757/v1/ws") as ws:
        await ws.recv()  # server.hello
        await ws.send(json.dumps({"type":"auth","id":"a1","v":1,"ts":0,"payload":{"token":"YOUR_TOKEN"}}))
        await ws.recv()  # auth_ok
        await ws.send(json.dumps({"type":"subscribe","id":"s1","v":1,"ts":0,"payload":{"channels":["media","lyrics"]}}))
        await ws.recv()  # subscribe_ok
        while True:
            msg = json.loads(await ws.recv())
            if msg.get("name") == "media.updated":
                p = msg["payload"]
                print(f"{p.get('title')} - {p.get('artist')} [{p.get('playbackState')}]")

asyncio.run(main())
```