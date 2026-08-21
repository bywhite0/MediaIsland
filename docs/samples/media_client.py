"""MediaLink Python 客户端示例 — 显示当前播放信息与逐字歌词，可选把远端音频放到本机

用法:
    python media_client.py <TOKEN> [ws://host:port/v1/ws] [--audio]
    python media_client.py medialink:<配置码> [--audio]

--audio 需要 sounddevice（pip install sounddevice）。缺了它其余功能照常。
"""
import asyncio, json, sys, time, base64, struct, threading, websockets

# 音频输出是可选功能：标准库没有播 PCM 到设备的办法，而只看信息与歌词的用户
# 不该为此被迫装 PortAudio。缺库时降级而不是报错。
try:
    import sounddevice as sd
except ImportError:
    sd = None

_args = [a for a in sys.argv[1:] if not a.startswith("--")]
WANT_AUDIO = "--audio" in sys.argv[1:]

TOKEN = _args[0] if _args else "YOUR_TOKEN"
URL   = _args[1] if len(_args) > 1 else "ws://127.0.0.1:21757/v1/ws"

# 抖动缓冲目标深度。与本仓渲染器的默认值一致：低于一个设备周期加一次重传的量级，
# 缓冲会持续欠载，表现为断续。
TARGET_BUFFER_MS = 200

# 缓冲上限。发送端时钟比本机设备快时深度会单调增长，不设上限就是把延迟永久背下去。
MAX_BUFFER_MS = 600

# 连续欠载累计到此值才硬重置。瞬时欠载填零即可，重置会丢掉已经收到的音频。
UNDERRUN_RESET_MS = 500

# 帧头：magic(2) version flags startPositionMs capturedAtMs serverTimeMs seq trackTokenLen
# 全部小端，定长 34 字节，之后是变长 trackToken 与裸 PCM。
AUDIO_HEADER = struct.Struct("<BBBBqqqIH")
AUDIO_MAGIC = (0xA1, 0x01)
AUDIO_VERSION = 1
AUDIO_FLAG_SILENT = 1


class JitterBuffer:
    """PCM 抖动缓冲。

    网络按突发到达，而声卡按固定速率消费，两者之间必须有缓冲。三条规则：

    - **预填充闸门**：攒够目标深度才开始出声，开了就不再回退。反复进出预填充
      会一顿一顿，而短暂欠载填零就能度过。
    - **欠载填零**，连续欠载累计过 UNDERRUN_RESET_MS 才清空重来——重置会丢掉
      已经收到的音频，不该为一次抖动付这个代价。
    - **积压丢最旧**：保留最新才追得上，保留最旧只会让延迟永久累积。

    刻意不做重采样调速。发送端与本机声卡是两块晶振，长期必然漂移，本仓的
    native 渲染器用 ±0.1% 的比率把深度拉回目标；示例改用上面两条硬边界，
    代价是漂移到边界时有一次可听的跳变，收益是不必引入重采样器。

    与设备分开是为了可测：连着 PortAudio 的话，这套规则只能在有声卡的机器上验。
    """

    def __init__(self, sample_rate, channels):
        self.sample_rate = sample_rate
        self.bytes_per_frame = 2 * channels
        self.target = self._bytes_for(TARGET_BUFFER_MS)
        self.limit = self._bytes_for(MAX_BUFFER_MS)
        self.buf = bytearray()
        self.lock = threading.Lock()
        self.gate_open = False
        self.underrun_ms = 0.0
        self.dropped = 0
        self.underruns = 0

    def _bytes_for(self, ms):
        return int(self.sample_rate * ms / 1000) * self.bytes_per_frame

    def feed(self, pcm):
        with self.lock:
            self.buf += pcm
            over = len(self.buf) - self.limit
            if over > 0:
                del self.buf[:over]
                self.dropped += over // self.bytes_per_frame

    def depth_ms(self):
        with self.lock:
            return len(self.buf) / self.bytes_per_frame / self.sample_rate * 1000

    def take(self, need):
        """取出 need 字节。不够就补零，返回的长度恒为 need。"""
        with self.lock:
            if not self.gate_open:
                if len(self.buf) < self.target:
                    return bytes(need)
                self.gate_open = True

            take = min(need, len(self.buf))
            out = bytes(self.buf[:take])
            del self.buf[:take]
            if take == need:
                self.underrun_ms = 0.0   # 欠载是「连续」欠载，取到过就重新计
                return out

            self.underruns += 1
            self.underrun_ms += (need - take) / self.bytes_per_frame / self.sample_rate * 1000
            if self.underrun_ms >= UNDERRUN_RESET_MS:
                self.buf.clear()
                self.gate_open = False
                self.underrun_ms = 0.0
            return out + bytes(need - take)


class AudioOutput:
    """把 JitterBuffer 接到声卡上。除了搬字节，这一层不含任何策略。"""

    def __init__(self, sample_rate, channels):
        self.jitter = JitterBuffer(sample_rate, channels)
        self.bytes_per_frame = 2 * channels
        self.stream = sd.RawOutputStream(
            samplerate=sample_rate, channels=channels, dtype="int16",
            callback=self._callback)
        self.stream.start()

    # 在 PortAudio 的线程上跑，不能阻塞、不能抛。
    def _callback(self, outdata, frames, _time, _status):
        outdata[:] = self.jitter.take(frames * self.bytes_per_frame)

    def feed(self, pcm):
        self.jitter.feed(pcm)

    def depth_ms(self):
        return self.jitter.depth_ms()

    @property
    def dropped(self):
        return self.jitter.dropped

    @property
    def underruns(self):
        return self.jitter.underruns

    def close(self):
        self.stream.stop()
        self.stream.close()


class MediaLinkClient:
    def __init__(self, url, token, want_audio=False):
        self.url, self.token = url, token
        self.ws = None
        self.epoch = None
        self.last_seq = 0
        self.media = None
        self.recv_at = 0  # 收到 media.updated 时的本地单调时钟
        self.track_token = None
        # 音频：意愿、服务端能力与线格式三者分开。能力未知时一律不请求 audio 频道。
        self.audio_requested = want_audio
        self.want_audio = want_audio and sd is not None
        self.audio_supported = False
        self.audio_reason = ""
        self.audio_format = (48000, 2)
        self.out = None
        self.audio_at = 0     # 最近一帧音频到达的本地单调时钟
        self.audio_frames = 0
        self.audio_rejected = 0
        self.next_stat_at = 0

    async def connect(self):
        self.ws = await websockets.connect(self.url)
        hello = json.loads(await self.ws.recv())
        if hello.get("name") == "server.hello":
            self.epoch = hello["payload"].get("sessionEpoch")
            self.last_seq = 0
            self._consume_hello(hello["payload"])
        await self._send("auth", {"token": self.token})
        await self.ws.recv()  # auth_ok

        # 先开设备再订阅。反过来的话，设备开不起来就白占了一路音频帧，还得再来一轮
        # 退订才收得回去；而这里只要不把 audio 放进频道列表即可。
        # 开不起来就回落到「不出声」而不是退出：为了听声音把信息与歌词一起搭上，
        # 比没声音更坏。
        if self.want_audio and self.audio_supported:
            try:
                self.out = AudioOutput(*self.audio_format)
            except Exception as err:
                print(f"✗ 打不开音频输出设备，继续显示信息与歌词：{err}")
                self.out = None

        # subscribe 是整体替换语义，故每次都发全量频道列表而非增量。audio 只在服务端
        # 声明支持时才带上：向不认识该频道的旧服务端请求它，整个 subscribe 会被拒，
        # media 与 lyrics 一起没了——为了听声音连歌词都会失去。
        channels = ["media", "lyrics"]
        if self.out:
            channels.append("audio")
        await self._send("subscribe", {"channels": channels})
        await self.ws.recv()  # subscribe_ok
        print(f"✓ 已连接并订阅 {' / '.join(channels)}")

        if self.out:
            # 订阅只是让帧有路可走，采集与否另由这条指令表达。两者缺一都不会有帧。
            await self._send("audio.play_start", None)
            print(f"✓ 音频输出已启动（{self.audio_format[0]}Hz/{self.audio_format[1]}ch，"
                  f"抖动缓冲 {TARGET_BUFFER_MS}ms）")
        elif self.want_audio:
            print(f"✗ {self.audio_reason}，不请求该频道")
        elif self.audio_requested:
            print("✗ 未安装 sounddevice，音频输出不可用（pip install sounddevice）")

    def _consume_hello(self, payload):
        """hello 不止握手时来一次，同一条连接上可以再发，每条都是一份完整声明。

        故能力以最新一条为准整体覆盖。缺 capabilities 字段的旧服务端按空集合处理。

        「没声音」的两种原因分开记：能力缺失是对端不懂这个协议，格式不认得是懂但
        说的方言不同。混成一句会把排查方向指错——前者该换服务端，后者该改客户端。
        """
        caps = payload.get("capabilities") or []
        fmt = payload.get("audio") or {}
        self.audio_supported = False
        if "audio" not in caps:
            self.audio_reason = "服务端未声明 audio 能力"
        elif fmt.get("format", "s16le") != "s16le":
            # 只解 s16le。宁可不出声也不能按错的格式解释字节——那是刺耳的噪声，
            # 而噪声比静默更难归因。
            self.audio_reason = f"服务端声明的线格式 {fmt['format']} 本客户端不认得（只支持 s16le）"
        else:
            self.audio_supported = True
            self.audio_reason = ""
        if fmt:
            self.audio_format = (fmt.get("sampleRate", 48000), fmt.get("channels", 2))

    async def _send(self, msg_type, payload):
        await self.ws.send(json.dumps({"type": msg_type, "id": f"{msg_type}-1", "v": 1, "ts": 0, "payload": payload}))

    def output_latency_ms(self):
        """本机出声引入的延迟。

        喇叭放的是抖动缓冲里攒了一段的内容，而媒体时钟给的是服务端的实时位置，
        故呈现要后移同样多才对得上听觉。取目标深度而非实测占用：后者在目标值附近
        持续波动，跟着它走会让位置来回抖。

        判据是「帧还在来」而不是「请求过音频」：服务端原生音频库缺失时
        audio.play_start 照样回 ok 却永远不推帧，那时没有本机延迟可补。
        """
        if not self.out or time.monotonic() - self.audio_at > 0.5:
            return 0
        return TARGET_BUFFER_MS

    def position_now(self):
        """插值计算当前播放位置（毫秒），不假设两台机器时钟同步"""
        if not self.media:
            return 0
        if self.media["playbackState"] != "Playing":
            pos = self.media["positionMs"]
        else:
            elapsed = (self.media["serverTimeMs"] - self.media["positionCapturedAtMs"]) + \
                      ((time.monotonic() - self.recv_at) * 1000)
            pos = self.media["positionMs"] + elapsed * self.media.get("playbackRate", 1.0)
        # 钳到非负：歌曲刚起播、位置还小于缓冲深度时减出来是负值。
        pos = max(0, pos - self.output_latency_ms())
        return min(pos, self.media.get("durationMs", pos) or pos)

    def on_audio_frame(self, data):
        """一帧入站二进制即一帧音频。

        所有失败路径都是「丢帧但不断连」：未知 magic 可能是未来版本的其他二进制
        帧类型，按协议的「必须容忍未知」原则处理。
        """
        if len(data) < AUDIO_HEADER.size:
            return
        m0, m1, version, flags, _start, captured_at, server_time, _seq, token_len = \
            AUDIO_HEADER.unpack_from(data)
        if (m0, m1) != AUDIO_MAGIC or version != AUDIO_VERSION:
            return
        if len(data) < AUDIO_HEADER.size + token_len:
            return

        token = data[AUDIO_HEADER.size:AUDIO_HEADER.size + token_len].decode("utf-8", "replace")
        pcm = data[AUDIO_HEADER.size + token_len:]

        # i16 要求偶数字节，奇数说明上游编码有误。
        if len(pcm) % 2:
            self.audio_rejected += 1
            return

        # 与歌词同源的规则：切歌后过期曲目的 PCM 直接丢弃。当前无曲目时也不接受——
        # 无从校验即不可信，而 media.updated 紧随 subscribe_ok 到达，这个窗口很短。
        if not self.track_token or token != self.track_token:
            self.audio_rejected += 1
            return

        self.audio_at = time.monotonic()
        if not pcm or not self.out:
            return

        self.audio_frames += 1
        self.out.feed(pcm)

        # 帧时长按实际字节数算，绝不写死 10ms：帧长由采集设备的周期决定，
        # 换一台设备就可能不同，写死的下游会静默漂移而不报错。
        sample_rate, channels = self.audio_format
        frame_ms = len(pcm) / (2 * channels) / sample_rate * 1000

        now = time.monotonic()
        if now >= self.next_stat_at:
            self.next_stat_at = now + 5
            silent = " 静音" if flags & AUDIO_FLAG_SILENT else ""
            # serverTimeMs - capturedAtMs 是服务端内部的采样延迟，两值同出自发送端
            # 时钟。不要拿 serverTimeMs 与本地时钟直接相减——两台机器没有对时。
            print(f"  🔊 帧长 {frame_ms:.1f}ms · 缓冲 {self.out.depth_ms():.0f}ms · "
                  f"服务端内部延迟 {server_time - captured_at}ms · 已放 {self.audio_frames} 帧 · "
                  f"丢 {self.out.dropped}/{self.audio_rejected} · 欠载 {self.out.underruns}{silent}")

    async def run(self):
        try:
            await self.connect()
            async for raw in self.ws:
                # 不走 JSON 信封的入站帧只有音频，按类型分流即可。
                if isinstance(raw, (bytes, bytearray)):
                    self.on_audio_frame(raw)
                    continue

                msg = json.loads(raw)
                # seq 去重（服务端重启时 epoch 变化需重置水位）
                if msg.get("name") == "server.hello":
                    if msg["payload"].get("sessionEpoch") != self.epoch:
                        self.epoch = msg["payload"]["sessionEpoch"]
                        self.last_seq = 0
                    self._consume_hello(msg["payload"])
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
        finally:
            # 连接一断服务端就撤销本连接的采集意愿，无需也无法补发 play_stop；
            # 要收的只有本机的设备句柄。
            if self.out:
                self.out.close()

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
    if _args and _args[0].lower().startswith("medialink:"):
        result = parse_config_code(_args[0])
        if result:
            endpoint, token = result
            print(f"✓ 配置码解析成功: {endpoint}")
            asyncio.run(MediaLinkClient(f"ws://{endpoint}/v1/ws", token, WANT_AUDIO).run())
        else:
            print("✗ 配置码格式不正确")
    else:
        asyncio.run(MediaLinkClient(URL, TOKEN, WANT_AUDIO).run())
