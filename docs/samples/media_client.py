"""MediaLink Python 客户端示例 — 显示当前播放信息与逐字歌词"""
import asyncio, json, sys, time, base64, websockets

TOKEN = sys.argv[1] if len(sys.argv) > 1 else "YOUR_TOKEN"
URL   = sys.argv[2] if len(sys.argv) > 2 else "ws://127.0.0.1:21757/v1/ws"

class MediaLinkClient:
    def __init__(self, url, token):
        self.url, self.token = url, token
        self.ws = None
        self.epoch = None
        self.last_seq = 0
        self.media = None
        self.recv_at = 0  # 收到 media.updated 时的本地单调时钟
        self.track_token = None

    async def connect(self):
        self.ws = await websockets.connect(self.url)
        hello = json.loads(await self.ws.recv())
        if hello.get("name") == "server.hello":
            self.epoch = hello["payload"].get("sessionEpoch")
            self.last_seq = 0
        await self._send("auth", {"token": self.token})
        await self.ws.recv()  # auth_ok
        await self._send("subscribe", {"channels": ["media", "lyrics"]})
        await self.ws.recv()  # subscribe_ok
        print("✓ 已连接并订阅 media / lyrics")

    async def _send(self, msg_type, payload):
        await self.ws.send(json.dumps({"type": msg_type, "id": f"{msg_type}-1", "v": 1, "ts": 0, "payload": payload}))

    def position_now(self):
        """插值计算当前播放位置（毫秒），不假设两台机器时钟同步"""
        if not self.media or self.media["playbackState"] != "Playing":
            return self.media["positionMs"] if self.media else 0
        elapsed = (self.media["serverTimeMs"] - self.media["positionCapturedAtMs"]) + \
                  ((time.monotonic() - self.recv_at) * 1000)
        pos = self.media["positionMs"] + elapsed * self.media.get("playbackRate", 1.0)
        return max(0, min(pos, self.media.get("durationMs", pos)))

    async def run(self):
        await self.connect()
        async for raw in self.ws:
            msg = json.loads(raw)
            # seq 去重（服务端重启时 epoch 变化需重置水位）
            if msg.get("name") == "server.hello":
                if msg["payload"].get("sessionEpoch") != self.epoch:
                    self.epoch = msg["payload"]["sessionEpoch"]
                    self.last_seq = 0
                continue
            if (seq := msg.get("seq", 0)) > 0:
                if seq <= self.last_seq:
                    continue
                self.last_seq = seq

            if msg.get("name") == "media.updated":
                p = msg["payload"]
                self.media = {
                    "positionMs": p.get("positionMs", 0),
                    "durationMs": p.get("durationMs", 0),
                    "positionCapturedAtMs": p.get("positionCapturedAtMs", p.get("serverTimeMs", 0)),
                    "serverTimeMs": p.get("serverTimeMs", 0),
                    "playbackRate": p.get("playbackRate", 1.0),
                    "playbackState": p.get("playbackState", "Unknown"),
                }
                self.recv_at = time.monotonic()
                self.track_token = p.get("trackToken")
                pos_s = int(self.position_now() // 1000)
                dur_s = int(self.media["durationMs"] // 1000)
                print(f"♫ {p.get('title', '?')} — {p.get('artist', '?')}  "
                      f"[{pos_s//60}:{pos_s%60:02d}/{dur_s//60}:{dur_s%60:02d}]  {self.media['playbackState']}")

            elif msg.get("name") == "lyrics.updated":
                p = msg.get("payload") or {}
                # 歌词可能早于对应的 media.updated 到达，此时本地还不知道 trackToken。
                # 只在两边都已知且不相等时才判定为错配——无条件丢弃会让先到的歌词永远显示不出来。
                if p.get("trackToken") and self.track_token and p["trackToken"] != self.track_token:
                    continue
                doc = p.get("document") or {}
                lines = doc.get("lines") or []
                print(f"  📝 歌词: {len(lines)} 行 (来源: {p.get('source', '?')})")
                for line in lines[:3]:  # 显示前 3 行示例
                    words = line.get("words") or []
                    if words:
                        text = "".join(w["text"] for w in words)
                        print(f"     {line['startMs']//1000}s: {text}")
                    else:
                        print(f"     {line['startMs']//1000}s: {line.get('text', '')}")

# 配置码解析（可选功能）
def parse_config_code(code: str):
    """解析 MediaLink 配置码，返回 (endpoint, token) 或 None"""
    text = "".join(code.strip().split())  # 去除所有空白
    if text.lower().startswith("medialink:"):
        text = text[10:]
    try:
        padded = text.replace("-", "+").replace("_", "/")
        padded += "=" * ((4 - len(padded) % 4) % 4)
        payload = json.loads(base64.b64decode(padded))
        return payload.get("e"), payload.get("t")
    except Exception:
        return None

if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1].lower().startswith("medialink:"):
        result = parse_config_code(sys.argv[1])
        if result:
            endpoint, token = result
            print(f"✓ 配置码解析成功: {endpoint}")
            asyncio.run(MediaLinkClient(f"ws://{endpoint}/v1/ws", token).run())
        else:
            print("✗ 配置码格式不正确")
    else:
        asyncio.run(MediaLinkClient(URL, TOKEN).run())