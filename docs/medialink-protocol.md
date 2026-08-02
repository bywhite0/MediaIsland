# MediaLink WebSocket 协议

> 本文档是面向客户端开发者的独立参考。实现一个基本客户端只需 WebSocket 支持和本文档。

## 概述

MediaLink 是 ClassIsland 媒体信息插件提供的本地 WebSocket 推送服务。客户端通过 `ws://` 连接，经过认证与订阅后，即可接收当前播放的媒体信息和逐字歌词。

- **传输**: 明文 `ws://`（无 TLS）
- **默认地址**: `ws://127.0.0.1:17654/v1/ws`
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

服务端响应 `subscribe_ok`，随后推送快照（当前状态）和增量事件。

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

客户端应使用三元组 `(positionMs, positionCapturedAtMs, serverTimeMs)` 本地插值:

```
当前位置 ≈ positionMs + (本地时钟 - serverTimeMs) × playbackRate
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
    "authRequired": true
  }
}
```

## 控制指令（Phase 2）

认证后可发送以下指令控制媒体播放:

| 类型 | 说明 |
|------|------|
| `media.inject` | 注入虚拟媒体信息 |
| `lyrics.inject` | 注入歌词 |
| `media.clear_inject` | 清除注入 |
| `playback.command` | 播放控制（play / pause / next / previous） |

详见源码 `MediaLinkSession.cs` 中的 `HandleMediaInjectAsync` 等方法。

## 缩略图端点

```
GET /v1/thumbnail?token=<token>&t=<trackToken>
```

- 共用同一 Token
- `t` 参数用于缓存失效，服务端只做存在性比对
- 成功返回 `image/png` 或 `image/jpeg`，`Cache-Control: no-store`
- 401: Token 不匹配
- 404: 无封面或曲目已过期

> **安全提示**: 此 URL 含 Token，不应写入日志或分享。

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

## 出站队列与背压

- 每个客户端有独立的 64 槽位出站队列
- `media.updated` 可丢弃最旧帧（客户端有插值兜底）
- `lyrics.updated` 和控制帧不可丢弃，队列满时以 `rate_limited` 关闭该客户端
- 一个慢客户端**不影响**其他客户端的推送

## 序列号 (`seq`)

每个事件携带单调递增的 `seq`（从 1 开始）。客户端可用于:

- 检测消息乱序
- 检测消息丢失（`seq` 跳变）

## 最小客户端示例

```python
import asyncio, websockets, json

async def main():
    async with websockets.connect("ws://127.0.0.1:17654/v1/ws") as ws:
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