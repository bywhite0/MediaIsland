"""MediaLink Python 客户端示例 — 显示当前播放信息与逐字歌词，可选把远端音频放到本机

用法:
    python media_client.py <TOKEN> [ws://host:port/v1/ws] [--audio] [--offset-ms=N]
    python media_client.py medialink:<配置码> [--audio]
    python media_client.py --self-test

--audio 需要 sounddevice（pip install sounddevice）。缺了它其余功能照常。
--offset-ms=N 在对齐模式下把出声时刻整体后移 N 毫秒（可负、可带小数），
是自动估计对不上耳朵时的手动旁路。
--self-test 只跑纯算术自检，不碰声卡也不碰网络。
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

# 手动偏移旁路（毫秒，可负、可带小数）：对齐的自动估计对不上耳朵时的最后手段，
# 正值让本机更晚出声。硬件尾段（功放、蓝牙）在任何 API 里都读不到，这个旁路
# 是对可观测性边界的承认，不是权宜。
# 注意方向：本示例的手动偏移作用在目标时刻上（正值推后出声），与本仓产品实现
# （作用在实测出声侧：正值声明实际出声更晚，补偿后听感提前）同名反向——
# 移植时刻度要取反。
#
# 界与浏览器示例的滑块、产品设置一致（±500）：超界的值几乎必然是单位错填，
# 且正向大偏移会把可行性顶出积压上限（见 aligned_depth_ms）。
OFFSET_LIMIT_MS = 500.0

def clamp_offset_ms(value):
    """手动偏移夹紧到 ±OFFSET_LIMIT_MS 毫秒。"""
    return max(-OFFSET_LIMIT_MS, min(OFFSET_LIMIT_MS, value))

OFFSET_MS = 0.0
for _a in sys.argv[1:]:
    if _a.startswith("--offset-ms="):
        try:
            _raw = float(_a.split("=", 1)[1])
        except ValueError:
            sys.exit("✗ --offset-ms 需要数字，例如 --offset-ms=25")
        OFFSET_MS = clamp_offset_ms(_raw)
        if OFFSET_MS != _raw:
            print(f"⚠ --offset-ms={_raw:g} 超界，已夹紧到 {OFFSET_MS:+g}ms（界 ±{OFFSET_LIMIT_MS:g}）")

TOKEN = _args[0] if _args else "YOUR_TOKEN"
URL   = _args[1] if len(_args) > 1 else "ws://127.0.0.1:21757/v1/ws"

# 抖动缓冲目标深度。与本仓渲染器的默认值一致：低于一个设备周期加一次重传的量级，
# 缓冲会持续欠载，表现为断续。
TARGET_BUFFER_MS = 200

# 缓冲上限。发送端时钟比本机设备快时深度会单调增长，不设上限就是把延迟永久背下去。
MAX_BUFFER_MS = 600

# 连续欠载累计到此值才硬重置。瞬时欠载填零即可，重置会丢掉已经收到的音频。
UNDERRUN_RESET_MS = 500

# ---- 回程对时（audio.clock）与跨机启动排程对齐 ----

# offset 样本窗口深度。8 个样本配 1 秒稳态周期覆盖约 8 秒，而 8 秒内两台机器
# 单调时钟的相互漂移只有约 0.08 毫秒，整窗可当作一个常量偏移，不必估 skew。
# 真正 200ppm 量级的偏差来自音频设备晶振，那个本示例刻意不处理（见 JitterBuffer）。
AUDIO_CLOCK_WINDOW = 8

# 最小往返超过它就报「offset 不可用」而不是照样给值：offset 误差上界是往返的
# 一半，50 毫秒往返意味着误差可达 25 毫秒，已吃掉整个对齐预算——退回非对齐
# 比假装对齐正确。
MAX_RTT_TICKS = 50 * 10_000        # 50 毫秒，100ns tick

# 探测节奏。窗口没满时每 200ms 一次（约 1.6 秒填满），满了降到 1 秒保活。
# 阶段按「已接受的样本数」而非「已发出的探测数」判断：样本没配成时窗口仍是
# 空的，该继续密集探，不能因为发够了次数就降速。
CLOCK_FAST_S = 0.2
CLOCK_STEADY_S = 1.0

# 单次应答的等待上限。超时按「这次没成」计入连续计数。
CLOCK_TIMEOUT_S = 1.0

# 连续 3 次没成算失联，清空整个样本窗。取 3 而非 1：单次丢包在无线链路上是
# 常态，一次丢包就清窗会让 offset 反复进出可用状态。
CLOCK_MAX_MISSES = 3

# 对齐所需的时钟三件套（offset、墙钟桥、设备出声时刻）未就绪时最多等这么久：
# 快速探测约 1.6 秒填满样本窗，3 秒足够。超时退回缓冲深度控制——协议要求
# 对时不可用时退回非对齐播放，宁可不对齐也要出声。
ALIGN_WAIT_MS = 3000

# 播放延迟预算的防御上界（毫秒）。真实链路用不到 10 秒量级，落在其外的数只
# 可能是配置写错或单位错填；更要紧的是预算换算成 100ns tick 要乘一万，不设
# 上界时一个荒唐大的值可能溢出成负数目标——上界把这条路堵在换算之前。
D_MS_MAX = 10_000


def now_ticks():
    """本地单调时钟，100 纳秒 tick——与协议的 t1–t4 同单位，不用毫秒。

    offset 估计的误差本身只有 0.5 毫秒量级，毫秒量化会污染它。
    time.monotonic_ns() 是整数纳秒，整除 100 不引入任何精度损失；
    墙钟不参与——它会被 NTP 调整，一次调整就让此前所有样本失效且无从察觉。
    """
    return time.monotonic_ns() // 100


class ClockOffset:
    """四时间戳 → 本机与服务端单调时钟的偏移（客户端时刻 + offset = 服务端时刻）。

    挑最小 RTT 那次，不取平均：排队延迟只会让 RTT 变大，而它对 offset 的污染
    上界是 RTT/2，取最小者即取污染上界最小者。取平均会把所有样本的排队延迟
    都掺进结果，且一个离群值就能带偏。

    刻意不估 skew：对时用的是单调时钟，两台机器间偏差约 10ppm，8 秒窗口内漂移
    0.08 毫秒——比估计噪声还小。真正 200ppm 量级的偏差来自音频设备晶振，
    那个本示例不处理（只做启动排程，见 JitterBuffer）。
    """

    def __init__(self):
        self.samples = []

    def add(self, t1, t2, t3, t4):
        rtt = (t4 - t1) - (t3 - t2)
        if rtt < 0:
            # 物理上不可能：字段错位，或对端时钟中途被重置。丢弃而不是夹紧到
            # 零——夹紧会让坏样本看起来像最好的那一个，而「最好的那一个」正是
            # 选择依据。
            return False
        self.samples.append((rtt, ((t2 - t1) + (t3 - t4)) // 2))
        del self.samples[:-AUDIO_CLOCK_WINDOW]
        return True

    def offset(self):
        # 快照再算：设备回调线程会并发读，探测任务可能正在清窗。
        samples = list(self.samples)
        if not samples:
            return None
        rtt, off = min(samples)
        return None if rtt > MAX_RTT_TICKS else off


class ClockSync:
    """回程对时的状态机：样本窗、墙钟桥、对端支持性、连续未成计数。

    纯状态无 IO，收发与等待都在外面——这一层真正难测的是「应答迟到一拍」
    「对端只实现三时刻」这些网络上极难复现的情形，拆开后每种都能用几行构造。
    """

    def __init__(self):
        self.estimator = ClockOffset()
        self.bridge_w = None      # 发送端墙钟 − 发送端单调钟，100ns tick
        self.unsupported = False  # 对端只实现三时间戳，本条连接内不再探
        self.misses = 0           # 连续未成：超时、回显不符、负 rtt 都算
        self.accepted = 0         # 已接受样本数，决定快速/稳态节奏

    def on_reply(self, t1, payload, env_ts_ms, t4):
        """消化一条应答，返回是否配成了可用样本。所有失败只进计数，不抛。"""
        t2, t3 = payload.get("t2"), payload.get("t3")
        if t2 is None or t3 is None:
            # 对端只实现了三时间戳。不退化成「假设服务端处理耗时为零」——那会
            # 给出一个看起来可用的错值，比报不可用坏得多。
            self.unsupported = True
            self.estimator.samples.clear()
            return False
        if payload.get("t1") != t1:
            # 回显不符：这条应答配的是别的请求。拿它与本次的 t4 凑样本，往返
            # 会算成两次探测的间隔——一个大得离谱又看起来合法的数。
            self.miss()
            return False
        if (isinstance(env_ts_ms, bool)
                or not isinstance(env_ts_ms, (int, float)) or env_ts_ms <= 0):
            # 信封 ts 是墙钟桥的原料（协议规定必在）。缺了或非法时整个样本按
            # 「这次没成」计——四时刻即使齐全也不能收：桥必须配被接受样本
            # 同一条应答，收样本不收桥会让两者不同源；拿 0 凑数则造出错桥。
            # bool 先拒：True 是 int 的子类且 True > 0，放行会造出 10⁴−t3 的错桥。
            self.miss()
            return False
        if not self.estimator.add(t1, t2, t3, t4):
            self.miss()
            return False
        # 桥与样本同处写入：信封 ts 与载荷 t3 是服务端同瞬取的一对（协议义务），
        # 桥必须配被接受的样本——配一条被拒样本的 ts 等于拿坏数据修正好数据。
        # 不做窗口平滑：W 在同一台对端上是准常量，逐样本差异只有 ts 的毫秒量化。
        self.bridge_w = env_ts_ms * 10_000 - t3
        self.misses = 0
        self.accepted += 1
        return True

    def miss(self):
        """记一次没成。超时不回、回显不符、样本被拒都计入——否则一个每次都
        回应但样本恒不可用的对端永远到不了失联阈值，旧 offset 被无限期沿用。"""
        self.misses += 1
        if self.misses < CLOCK_MAX_MISSES:
            return
        # 失联：丢掉整窗。留着旧样本会让上层在链路已断时继续按一个越来越旧的
        # offset 对齐，而它的误差没有上界。accepted 一并清零，探测节奏回到
        # 快速阶段重新填窗。桥不清：合成被「offset 不可用」挡住，且下一个被
        # 接受的样本会带来新桥。
        self.estimator.samples.clear()
        self.accepted = 0

    def reset(self):
        """断连即清空：样本窗、墙钟桥、「对端不支持」判定一起清，重连从空窗开始。

        重连后的对端可以是另一台机器，旧样本与旧桥描述的是一段已经不存在的
        时钟映射；「不支持」同理指向旧对端，不清的话连过一个三时刻对端后，
        连到谁都不再对时，且归因指向错误的那一台。（清空的理由是换了机器，
        不是进程重启：单调时钟零点是系统级的，同机重启不改变它。）
        """
        self.estimator.samples.clear()
        self.bridge_w = None
        self.unsupported = False
        self.misses = 0
        self.accepted = 0

    def online_offset(self):
        """线上offset = −offset − W：本机单调钟 − 发送端墙钟（100ns 表示）。

        两段刻意分开：offset 走四时刻的最小往返选择（0.5ms 量级），W 走同瞬的
        （信封 ts，t3）一对（±0.5ms 量化）。把墙钟直接混进 offset 估计会让
        NTP 调整污染整窗。任一不可用即整体不可用。
        """
        off = self.estimator.offset()
        if off is None or self.bridge_w is None:
            return None
        return -off - self.bridge_w

    def next_interval(self):
        return CLOCK_FAST_S if self.accepted < AUDIO_CLOCK_WINDOW else CLOCK_STEADY_S


def target_play_ticks(captured_at_ms, d_ms, online_offset, manual_offset_ms=0):
    """目标出声时刻（本机单调钟轴，100ns tick）。

    capturedAtMs 在发送端墙钟轴上，「capturedAt + D + 手动偏移」经线上offset
    搬到本机单调钟轴。物理含义：该采样在本机时间轴上的采集时刻 + D。
    """
    return int((captured_at_ms + d_ms + manual_offset_ms) * 10_000) + online_offset


def aligned_depth_ms(d_ms, device_latency_ms, manual_offset_ms=0.0):
    """对齐模式下的缓冲深度估计（D − 设备延迟），并判本机装不装得下。

    返回 (深度, "") 或 (None, 原因)。TARGET_BUFFER_MS 在对齐模式下从目标深度
    降为下界：深度不再由本机配置，由发送端的预算决定。原因里给出该往哪边改——
    「没声明」查版本，「办不到」改数值，两者的排查方向不同，这里全属后者。

    正向手动偏移把目标时刻整体推后，等效加深所需积压，故计入上界检查；不计入
    就给「永久静音 + 持续丢帧」留了旁门——帧在等到目标时刻之前被丢最旧顶掉，
    闸门永不开。负向偏移只提前出声、不加深积压，不参与。
    """
    depth = d_ms - device_latency_ms
    if depth < TARGET_BUFFER_MS:
        return None, (f"预算 dMs={d_ms}ms 减设备延迟 {device_latency_ms:.0f}ms 后不足"
                      f"最小缓冲 {TARGET_BUFFER_MS}ms——调大发送端 dMs，或换更快的本机设备")
    if depth + max(manual_offset_ms, 0.0) > MAX_BUFFER_MS:
        # 深度超过积压上限时，帧在等到目标时刻之前就会被「丢最旧」顶掉，
        # 对齐会被静默顶穿——与其那样，不如在这里退回并说明。
        extra = f" 加正向手动偏移 {manual_offset_ms:g}ms" if manual_offset_ms > 0 else ""
        return None, (f"预算 dMs={d_ms}ms{extra} 超出本示例缓冲上限 {MAX_BUFFER_MS}ms"
                      f"——调小发送端 dMs" + ("，或减小 --offset-ms" if manual_offset_ms > 0 else ""))
    return depth, ""


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

    对齐模式只改闸门的开启条件，三条规则不动：不再是「攒够目标深度就开」，
    而是「这一批样本的出声时刻 ≥ 目标时刻才开」，落后时先丢掉已过期的帧数再开。
    开闸之后不做持续微调——只排首块，此后误差随晶振漂移与欠载填零累积，状态行
    把这个数摊开给读者看：它怎么长，就是产品实现为什么必须有内环（重采样调速）
    的实测演示。TARGET_BUFFER_MS 在对齐模式下从目标深度降为可行性下界，实际
    深度由预算减去途中各段延迟自然形成。

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
        # 对齐状态。target_of 把发送端墙钟毫秒换算成本机单调钟 tick 的目标出声
        # 时刻，时钟未就绪时返回 None——换算依赖 offset 与墙钟桥，由持有协议
        # 状态的一侧注入。缓冲自身不懂协议，纯算术才可测。
        self.aligned = False
        self.align_reason = ""
        self.target_of = None
        self.head_captured_ms = None   # 缓冲头部字节的采样时刻（发送端墙钟毫秒）
        self.first_feed_mono = None    # 等待时钟就绪的起点
        self.first_err_ms = None       # 首块排程误差——判据看它，不看稳态
        self.err_ms = None             # 最近一批的时刻误差，随漂移累积（刻意不修）

    def set_aligned(self, aligned, reason="", target_of=None):
        """开关对齐模式。hello 可在连接存活期间重发并改声明，故随时可切换；
        已开闸的流不回头重排——本示例只做启动排程对齐。"""
        with self.lock:
            self.aligned = aligned
            self.align_reason = reason
            self.target_of = target_of

    def _bytes_for(self, ms):
        return int(self.sample_rate * ms / 1000) * self.bytes_per_frame

    def _advance_head(self, nbytes):
        if self.head_captured_ms is not None:
            self.head_captured_ms += nbytes / self.bytes_per_frame / self.sample_rate * 1000

    def feed(self, pcm, captured_at_ms=None):
        with self.lock:
            if not self.buf:
                # 锚定缓冲头部的采样时刻，之后随消费与丢弃推进。缓冲一旦放空
                # （欠载或硬重置）就在下一次进料时重新锚定，误差累积从头再算。
                self.head_captured_ms = None if captured_at_ms is None else float(captured_at_ms)
                if self.first_feed_mono is None:
                    self.first_feed_mono = time.monotonic()
            self.buf += pcm
            over = len(self.buf) - self.limit
            if over > 0:
                del self.buf[:over]
                self.dropped += over // self.bytes_per_frame
                self._advance_head(over)

    def depth_ms(self):
        with self.lock:
            return len(self.buf) / self.bytes_per_frame / self.sample_rate * 1000

    def _gate(self, play_ticks):
        """预填充闸门，返回 True 即开闸——开了就不再回退（原有规则不变）。

        对齐模式下开启条件换成「出声时刻 ≥ 目标时刻」。时钟未就绪（offset、
        墙钟桥、设备出声时刻任一缺）时先等：快速探测约 1.6 秒填满样本窗；
        超过 ALIGN_WAIT_MS 就退回深度闸门——宁可不对齐也要出声。
        """
        if self.aligned:
            verdict = self._align_gate(play_ticks)
            if verdict is not None:
                return verdict
            if (self.first_feed_mono is None
                    or time.monotonic() - self.first_feed_mono < ALIGN_WAIT_MS / 1000):
                return False
            self.aligned = False
            self.align_reason = f"时钟超 {ALIGN_WAIT_MS}ms 未就绪，已退回缓冲深度控制"
        if len(self.buf) < self.target:
            return False
        self.gate_open = True
        return True

    def _align_gate(self, play_ticks):
        """对齐闸门本体：True 开闸 / False 还没到点 / None 时钟未就绪。"""
        if not self.buf or self.head_captured_ms is None:
            return False
        if play_ticks is None or self.target_of is None:
            return None
        tgt = self.target_of(self.head_captured_ms)
        if tgt is None:
            return None
        if play_ticks < tgt:
            return False
        # 落后了：按 lead 丢掉已过期的帧数再开。丢弃按整帧算，残余误差小于一帧。
        drop = min((play_ticks - tgt) * self.sample_rate // 10_000_000 * self.bytes_per_frame,
                   len(self.buf))
        del self.buf[:drop]
        self.dropped += drop // self.bytes_per_frame
        self._advance_head(drop)
        if not self.buf:
            # 缓冲里的音频已全部过期：丢锚不开闸，等新帧按它自己的目标时刻排。
            self.head_captured_ms = None
            return False
        self.gate_open = True
        tgt = self.target_of(self.head_captured_ms)
        self.first_err_ms = None if tgt is None else (play_ticks - tgt) / 1e4
        return True

    def take(self, need, play_ticks=None):
        """取出 need 字节。不够就补零，返回的长度恒为 need。

        play_ticks 是这一批样本将要出声的本机单调钟时刻（100ns tick），由设备
        回调换算供给；对齐闸门与时刻误差都以它为准。
        """
        with self.lock:
            if not self.gate_open and not self._gate(play_ticks):
                return bytes(need)

            if self.aligned and play_ticks is not None and self.target_of \
                    and self.head_captured_ms is not None:
                tgt = self.target_of(self.head_captured_ms)
                if tgt is not None:
                    # 正值即声音落后于目标。开闸后误差随两端晶振漂移与欠载填零
                    # 单调走远，这里刻意不修：持续微调是产品实现（重采样内环）
                    # 的事，示例只演示启动排程能对到多准、之后会散多快。
                    self.err_ms = (play_ticks - tgt) / 1e4

            take = min(need, len(self.buf))
            out = bytes(self.buf[:take])
            del self.buf[:take]
            self._advance_head(take)
            if take == need:
                self.underrun_ms = 0.0   # 欠载是「连续」欠载，取到过就重新计
                return out

            self.underruns += 1
            self.underrun_ms += (need - take) / self.bytes_per_frame / self.sample_rate * 1000
            if self.underrun_ms >= UNDERRUN_RESET_MS:
                self.buf.clear()
                self.gate_open = False
                self.underrun_ms = 0.0
                # 硬重置后丢锚：下一段来料重新按目标时刻排程，闸门语义与首次
                # 启动一致——对齐的「恢复」就这样复用了既有的重置规则。
                self.head_captured_ms = None
                self.first_feed_mono = None
            return out + bytes(need - take)


class AudioOutput:
    """把 JitterBuffer 接到声卡上。除了搬字节与换算时钟，这一层不含任何策略。"""

    def __init__(self, sample_rate, channels):
        self.jitter = JitterBuffer(sample_rate, channels)
        self.bytes_per_frame = 2 * channels
        self.stream_to_mono = None   # PortAudio 流时钟 → time.monotonic 的常量差，秒
        self.stream = sd.RawOutputStream(
            samplerate=sample_rate, channels=channels, dtype="int16",
            callback=self._callback)
        self.stream.start()

    # 在 PortAudio 的线程上跑，不能阻塞、不能抛。
    def _callback(self, outdata, frames, time_info, _status):
        outdata[:] = self.jitter.take(frames * self.bytes_per_frame,
                                      self._play_ticks(time_info))

    def _play_ticks(self, time_info):
        """这一批样本的出声时刻，本机单调钟 100ns tick。

        「何时出声」在 Python 侧是现成的：PortAudio 回调本就带 time_info，
        outputBufferDacTime 即本批样本到达 DAC 的流时钟时刻（此前一直被丢弃）；
        浏览器侧同理（outputLatency / getOutputTimestamp 已在用）。同一个信息
        在 .NET/WASAPI 侧要新开一条 FFI 并变更 ABI 才能拿到——成本差来自平台
        的可观测性，不是设计。

        流时钟与 time.monotonic() 都是恒速单调钟，与 currentTime 配对一次即得
        常量差。个别宿主 API 不填 DAC 时刻（报 0），退回「现在 + 报告的输出
        延迟」——精度降一档，但闸门仍能工作。
        """
        if time_info.currentTime > 0 and self.stream_to_mono is None:
            self.stream_to_mono = time.monotonic() - time_info.currentTime
        if time_info.outputBufferDacTime > 0 and self.stream_to_mono is not None:
            return int((time_info.outputBufferDacTime + self.stream_to_mono) * 1e7)
        return int((time.monotonic() + (self.stream.latency or 0)) * 1e7)

    def device_latency_ms(self):
        """设备尾段延迟（毫秒）。PortAudio 报告的输出延迟，喂给深度可行性判定。"""
        return (self.stream.latency or 0) * 1000

    def feed(self, pcm, captured_at_ms=None):
        self.jitter.feed(pcm, captured_at_ms)

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
        # 跨机对齐：能力、预算与对时状态。归因分三种（见 _consume_hello）。
        self.clock_cap = False    # hello 声明了 audio.clock
        self.d_ms = None          # 合法的播放延迟预算；None 即不进对齐
        self.align_reason = ""    # 协议层不对齐的原因
        self.clock = ClockSync()
        self._clock_wait = None   # 在途探测的应答 Future，主收循环喂它

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
            # 设备已开、延迟已知，此刻才能判对齐的本机可行性。
            self._sync_alignment()
            if self.out.jitter.aligned:
                print(f"✓ 对齐模式：目标 采样时刻+{self.d_ms}ms 出声"
                      + (f"，手动偏移 {OFFSET_MS:+g}ms" if OFFSET_MS else ""))
            else:
                print(f"✗ 非对齐播放：{self.align_reason or self.out.jitter.align_reason}")
        elif self.want_audio:
            print(f"✗ {self.audio_reason}，不请求该频道")
        elif self.audio_requested:
            print("✗ 未安装 sounddevice，音频输出不可用（pip install sounddevice）")

    def _consume_hello(self, payload):
        """hello 不止握手时来一次，同一条连接上可以再发，每条都是一份完整声明。

        故能力以最新一条为准整体覆盖。缺 capabilities 字段的旧服务端按空集合处理。

        「没声音」的两种原因分开记：能力缺失是对端不懂这个协议，格式不认得是懂但
        说的方言不同。混成一句会把排查方向指错——前者该换服务端，后者该改客户端。

        「没对齐」是第三种，单独归因，与出不出声无关：对时只服务于对齐，缺了它
        音频照放，只是退回本地缓冲深度控制。其内部再分「能力缺失」「预算未声明」
        「预算办不到」——排查方向分别是升级服务端、查发送端版本、改发送端配置。
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

        # 对齐能力与预算成组读取：分两次取回的两半可能来自两条 hello，会让归因
        # 指错方向。缺 dMs 不得回落默认值——两端各自默认成一个数，看起来一致，
        # 实际是两份各自为真的声明，改了一端就静默失配，症状像硬件延迟。
        self.clock_cap = "audio.clock" in caps
        d_ms = (payload.get("audioClock") or {}).get("dMs")
        self.d_ms = None
        if not self.clock_cap:
            self.align_reason = "服务端未声明 audio.clock 能力（不支持跨机对齐，可升级服务端）"
        elif d_ms is None:
            self.align_reason = "服务端未声明播放延迟预算 dMs（查发送端版本与配置完整性）"
        elif not 0 < d_ms <= D_MS_MAX:
            self.align_reason = (f"播放延迟预算 dMs={d_ms} 不在 (0, {D_MS_MAX}] 毫秒内"
                                 f"（改发送端配置，先查单位是不是毫秒）")
        else:
            self.d_ms = d_ms
            self.align_reason = ""
        self._sync_alignment()

    def _sync_alignment(self):
        """把 hello 声明与本机可行性合成到抖动缓冲的对齐开关上。

        hello 可重发并改 dMs：改小到本机办不到就退回非对齐，改回来要能重新进入。
        判定顺序先协议层（能力、预算声明与取值），再本机静态可行性（预算装不装
        得下）；运行时的 offset 可用性留给闸门——它是唯一会自己好转的一条，
        排在前面会让用户把配置错误当成「再等等」。
        """
        if not self.out:
            return
        if self.d_ms is None:
            self.out.jitter.set_aligned(False, self.align_reason)
            return
        depth, why = aligned_depth_ms(self.d_ms, self.out.device_latency_ms(), OFFSET_MS)
        if depth is None:
            self.out.jitter.set_aligned(False, why)
        else:
            self.out.jitter.set_aligned(True, "", self._target_of)

    def _target_of(self, captured_at_ms):
        """发送端墙钟毫秒 → 本机单调钟目标出声时刻；时钟未就绪时 None。

        在设备回调线程上被调，offset 与桥可能正被探测任务更新——各自的读取
        有快照保护，瞬时拿到 None 只是让闸门再等一拍，不会算出错值。
        """
        online = self.clock.online_offset()
        if online is None or self.d_ms is None:
            return None
        return target_play_ticks(captured_at_ms, self.d_ms, online, OFFSET_MS)

    async def _clock_probe_loop(self):
        """audio.clock 探测任务：快速阶段填窗，稳态低频保活。

        仅当 hello 声明了 audio.clock 且本机确实在放音频时才发——对时只服务于
        对齐。能力可随 hello 重发变化，故不该发时轮询等待而不是退出。任何失败
        只进计数，不抛也不断连：对时坏了不该波及 media/lyrics/audio 三条通道。
        """
        n = 0
        while True:
            if self.clock.unsupported or not (self.out and self.clock_cap):
                await asyncio.sleep(CLOCK_STEADY_S)
                continue
            n += 1
            t1 = now_ticks()
            fut = asyncio.get_running_loop().create_future()
            self._clock_wait = fut
            reply = None
            try:
                await self.ws.send(json.dumps({
                    "type": "audio.clock", "id": f"clk-{n}", "v": 1, "ts": 0,
                    "payload": {"t1": t1}}))
                reply = await asyncio.wait_for(fut, CLOCK_TIMEOUT_S)
            except (asyncio.TimeoutError, OSError, websockets.WebSocketException):
                pass    # 超时或链路故障：按「这次没成」计，下面统一走 miss
            finally:
                self._clock_wait = None
            if reply is None:
                self.clock.miss()
            else:
                self.clock.on_reply(t1, *reply)
            await asyncio.sleep(self.clock.next_interval())

    async def _send(self, msg_type, payload):
        await self.ws.send(json.dumps({"type": msg_type, "id": f"{msg_type}-1", "v": 1, "ts": 0, "payload": payload}))

    def output_latency_ms(self):
        """本机出声引入的延迟。

        喇叭放的是抖动缓冲里攒了一段的内容，而媒体时钟给的是服务端的实时位置，
        故呈现要后移同样多才对得上听觉。取目标深度而非实测占用：后者在目标值附近
        持续波动，跟着它走会让位置来回抖。

        对齐生效（开过闸）时改用声明的 D：「采样时刻 + D 出声」是排程不变量，
        缓冲深度与设备延迟都已折算在目标时刻里，不再另加。

        判据是「帧还在来」而不是「请求过音频」：服务端原生音频库缺失时
        audio.play_start 照样回 ok 却永远不推帧，那时没有本机延迟可补。
        """
        if not self.out or time.monotonic() - self.audio_at > 0.5:
            return 0
        j = self.out.jitter
        if j.aligned and j.gate_open and self.d_ms is not None:
            return self.d_ms
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
        self.out.feed(pcm, captured_at)

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
                  f"丢 {self.out.dropped}/{self.audio_rejected} · 欠载 {self.out.underruns}"
                  f"{silent}{self._align_status()}")

    def _align_status(self):
        """状态行的对齐段。误差随时间累积是刻意的：本示例只排首块、不做持续
        微调，这个数怎么走就是产品实现为什么必须有内环的实测演示。"""
        if not self.out:
            return ""
        j = self.out.jitter
        if j.aligned and j.err_ms is not None and j.first_err_ms is not None:
            return (f" · 对齐 D={self.d_ms}ms 误差 首块 {j.first_err_ms:+.1f}ms /"
                    f" 当前 {j.err_ms:+.1f}ms（漂移刻意不修）")
        if j.aligned:
            off = "有" if self.clock.online_offset() is not None else "无"
            return f" · 对齐等待排程（offset {off}）"
        if j.align_reason or self.align_reason:
            return f" · 非对齐（{j.align_reason or self.align_reason}）"
        return ""

    async def run(self):
        probe = None
        try:
            await self.connect()
            # 对时探测与收发并行。它只服务于对齐，任何失败都不该波及数据通道。
            probe = asyncio.create_task(self._clock_probe_loop())
            async for raw in self.ws:
                # t4 的取点义务（对称于服务端的 t2）：收到应答字节处、进任何
                # 分支与 JSON 解析之前取，且与 t1 同源（同一个单调时钟）。
                t4 = now_ticks()
                # 不走 JSON 信封的入站帧只有音频，按类型分流即可。
                if isinstance(raw, (bytes, bytearray)):
                    self.on_audio_frame(raw)
                    continue

                msg = json.loads(raw)
                # 对时应答交回探测任务。没有在途探测时它是迟到或重复的，直接丢；
                # 回显 t1 的比对在 ClockSync 里做。
                if msg.get("type") == "audio.clock":
                    if self._clock_wait and not self._clock_wait.done():
                        self._clock_wait.set_result(
                            (msg.get("payload") or {}, msg.get("ts"), t4))
                    continue
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
            if probe:
                probe.cancel()
            # 断连即清空：样本窗、墙钟桥与「对端不支持」判定（义务，见 ClockSync.reset）。
            # 本示例断连即退出，清空是为了把义务落在正确的位置——照抄本文件加
            # 重连循环的人不该需要知道这条陷阱。
            self.clock.reset()
            # 连接一断服务端就撤销本连接的采集意愿，无需也无法补发 play_stop；
            # 要收的只有本机的设备句柄。
            if self.out:
                self.out.close()

def self_test():
    """纯算术自检：不碰 sounddevice、不碰网络——与 JitterBuffer「与设备分开是
    为了可测」同一条理由。每条断言对应一条协议义务或换算。"""
    n = [0]

    def ok(cond, what):
        n[0] += 1
        assert cond, f"[self-test #{n[0]}] {what}"

    # ---- offset 与 rtt 算术 ----
    co = ClockOffset()
    ok(co.add(1000, 5000, 5200, 1400), "正常四时刻样本应被接受")
    ok(co.samples == [(200, 3900)],
       "rtt=(t4−t1)−(t3−t2)、offset=((t2−t1)+(t3−t4))//2")
    ok(co.offset() == 3900, "单样本窗口的 offset 即该样本的 offset")

    # ---- 负 RTT 丢弃（物理上不可能，夹紧会让坏样本像最好的那一个）----
    ok(not co.add(1000, 5000, 5600, 1400), "负 rtt 样本应被拒收")
    ok(co.samples == [(200, 3900)], "被拒样本不得进窗")

    # ---- 选中最小 RTT，而非平均 ----
    co = ClockOffset()
    co.add(0, 10_150, 10_150, 300)      # rtt 300，offset 10000
    co.add(0, 20_050, 20_050, 100)      # rtt 100，offset 20000
    co.add(0, 30_250, 30_250, 500)      # rtt 500，offset 30000
    ok(co.offset() == 20_000, "应选最小 rtt 那次的 offset（排队污染上界最小）")

    # ---- 最小 rtt 超上限报不可用，而不是照样给值 ----
    co = ClockOffset()
    co.add(0, 0, 0, MAX_RTT_TICKS + 1)
    ok(co.offset() is None, "最小 rtt 超 50ms 应报 offset 不可用")
    co = ClockOffset()
    co.add(0, 0, 0, MAX_RTT_TICKS)
    ok(co.offset() == -(MAX_RTT_TICKS // 2), "恰在上限仍可用（上限不含等号）")

    # ---- 窗口只保留最近 8 个 ----
    co = ClockOffset()
    co.add(0, 802, 802, 50)             # rtt 50 全场最小，offset 777 可区分——即将被挤出窗
    for i in range(AUDIO_CLOCK_WINDOW):
        co.add(0, 550 + i, 550 + i, 1100 + 2 * i)   # rtt 1100+2i，offset 0
    ok(len(co.samples) == AUDIO_CLOCK_WINDOW, "窗口深度为 8")
    ok(co.offset() == 0, "被挤出窗口的最小 rtt 样本不得再参与选择（淘汰坏掉时它会给出 777）")

    # ---- tick 换算 ----
    ok(abs(now_ticks() - time.monotonic_ns() // 100) < 10_000,
       "now_ticks 是 100ns tick 的单调钟（与 monotonic_ns//100 同轴）")
    ok(target_play_ticks(1, 0, 0) == 10_000, "1 毫秒 = 10⁴ tick")
    ok(target_play_ticks(0, 1, 0) == 10_000, "dMs 同以毫秒换算")

    # ---- 墙钟桥合成与目标出声时刻 ----
    # 造一个已知的世界：服务端单调钟领先本机 O=55000 tick，服务端墙钟 =
    # 服务端单调钟 + 6943000 tick。探测往返网络单程 1500、服务端处理 500。
    sync = ClockSync()
    ok(sync.on_reply(1_000_000, {"t1": 1_000_000, "t2": 1_056_500, "t3": 1_057_000},
                     800, 1_003_500), "一致的四时刻样本应被接受")
    ok(sync.estimator.offset() == 55_000, "单调钟轴 offset 还原出 O")
    ok(sync.bridge_w == 6_943_000, "W = ts×10⁴ − t3")
    ok(sync.online_offset() == -55_000 - 6_943_000, "线上offset = −offset − W")
    ok(target_play_ticks(900, 300, sync.online_offset()) == 5_002_000,
       "目标 = capturedAt×10⁴ + D×10⁴ + 线上offset（即该采样的本机采集时刻 + D）")
    ok(target_play_ticks(900, 300, sync.online_offset(), 10) == 5_102_000,
       "手动偏移逐毫秒后移目标")

    # ---- 回显不符、缺 ts 与三时刻对端 ----
    ok(not sync.on_reply(2_000_000, {"t1": 1234, "t2": 5, "t3": 6}, 800, 2_003_000),
       "回显 t1 不符的应答应被弃（迟到或重复）")
    ok(sync.misses == 1 and len(sync.estimator.samples) == 1,
       "回显不符计一次未成，样本不进窗")
    ok(not sync.on_reply(3_000_000, {"t1": 3_000_000, "t2": 3_056_500, "t3": 3_057_000},
                         None, 3_003_500),
       "信封缺 ts 的应答按没成计（不得用 0 造桥）")
    ok(sync.misses == 2 and len(sync.estimator.samples) == 1
       and sync.bridge_w == 6_943_000,
       "缺 ts 时四时刻再合法也不进窗、不动桥——桥必须配被接受样本同条应答")
    peer3 = ClockSync()
    peer3.on_reply(1, {"t1": 1, "t3": 2}, 0, 3)
    ok(peer3.unsupported and not peer3.estimator.samples,
       "缺 t2 即对端只实现三时刻：判不支持并清窗，不退化成三时刻估计")
    boolts = ClockSync()
    boolts.on_reply(1_000_000, {"t1": 1_000_000, "t2": 1_056_500, "t3": 1_057_000},
                    800, 1_003_500)          # 先立一个好样本与好桥
    ok(not boolts.on_reply(2_000_000, {"t1": 2_000_000, "t2": 2_056_500, "t3": 2_057_000},
                           True, 2_003_500)
       and boolts.misses == 1 and len(boolts.estimator.samples) == 1
       and boolts.bridge_w == 6_943_000,
       "信封 ts 为 bool 按没成计：True 是 int 子类且 True>0，放行会造出 10⁴−t3 的错桥")

    # ---- 失联清窗（连续 3 次未成），稳态节奏回到快速阶段 ----
    steady = ClockSync()
    for i in range(AUDIO_CLOCK_WINDOW):
        t1 = 1_000_000 + i * 10_000
        steady.on_reply(t1, {"t1": t1, "t2": t1 + 56_500, "t3": t1 + 57_000},
                        800, t1 + 3_500)
    ok(steady.accepted == AUDIO_CLOCK_WINDOW
       and steady.next_interval() == CLOCK_STEADY_S, "喂满 8 样本后进稳态节奏")
    steady.miss()
    steady.miss()
    ok(steady.estimator.samples, "两次未成还不清窗（单次丢包是无线链路的常态）")
    steady.miss()
    ok(not steady.estimator.samples and steady.accepted == 0,
       "连续 3 次未成即失联清窗")
    ok(steady.online_offset() is None, "清窗后 offset 不可用（桥独存也合成不了）")
    ok(steady.next_interval() == CLOCK_FAST_S, "清窗后从稳态回快速阶段重新填窗")
    ok(steady.bridge_w is not None, "失联只清窗不清桥（下个被接受样本会覆写它）")

    # ---- 断连清空：窗、桥、「不支持」判定一起清（清之前先备齐全部状态）----
    full = ClockSync()
    full.on_reply(1_000_000, {"t1": 1_000_000, "t2": 1_056_500, "t3": 1_057_000},
                  800, 1_003_500)
    full.unsupported = True
    full.misses = 2
    ok(full.estimator.samples and full.bridge_w is not None and full.accepted == 1,
       "断连测例的前置：样本、桥、accepted 都非空")
    full.reset()
    ok(full.bridge_w is None and not full.unsupported and full.misses == 0
       and full.accepted == 0 and not full.estimator.samples,
       "断连 reset 清空样本窗、墙钟桥与「对端不支持」判定")

    # ---- D 与深度换算 ----
    ok(aligned_depth_ms(300, 20)[0] == 280, "深度 = D − 设备延迟")
    ok(aligned_depth_ms(TARGET_BUFFER_MS + 20, 20)[0] == TARGET_BUFFER_MS,
       "恰到下界仍可行（下界含等号）")
    bad, why = aligned_depth_ms(TARGET_BUFFER_MS + 19, 20)
    ok(bad is None and "调大" in why, "深度不足下界退回非对齐，且说出往哪边改")
    bad, why = aligned_depth_ms(MAX_BUFFER_MS + 100, 0)
    ok(bad is None and "调小" in why, "深度超缓冲上限同样退回，方向相反")
    ok(clamp_offset_ms(9_999.0) == 500.0 and clamp_offset_ms(-9_999.0) == -500.0
       and clamp_offset_ms(25.0) == 25.0,
       "--offset-ms 夹紧到 ±500（与浏览器滑块、产品设置同界），带内不动")
    bad, why = aligned_depth_ms(MAX_BUFFER_MS, 0, manual_offset_ms=1)
    ok(bad is None and "offset-ms" in why,
       "正向手动偏移计入上界：D 恰在上限时 +1ms 偏移即装不下，且原因点名偏移")
    ok(aligned_depth_ms(MAX_BUFFER_MS, 0, manual_offset_ms=-500)[0] == MAX_BUFFER_MS,
       "负向偏移不参与上界（只提前出声，不加深积压）")

    # ---- hello 归因：三种原因各自可判 ----
    c = MediaLinkClient("ws://x", "t")
    c._consume_hello({"capabilities": ["audio"], "audio": {"format": "s16le"}})
    ok(c.audio_supported and not c.clock_cap and "audio.clock" in c.align_reason,
       "有声可放但无对时能力：归因指向服务端能力")
    c._consume_hello({"capabilities": ["audio", "audio.clock"], "audio": {}})
    ok(c.clock_cap and c.d_ms is None and "dMs" in c.align_reason,
       "有能力但未声明预算：不回落默认值，归因指向发送端配置")
    c._consume_hello({"capabilities": ["audio", "audio.clock"], "audio": {},
                      "audioClock": {"dMs": 0}})
    ok(c.d_ms is None and "dMs=0" in c.align_reason, "dMs=0 属「办不到」，同样退回")
    c._consume_hello({"capabilities": ["audio", "audio.clock"], "audio": {},
                      "audioClock": {"dMs": 20_000}})
    ok(c.d_ms is None and "10000" in c.align_reason,
       "超上界报办不到——上界把 tick 换算溢出堵在前面")
    c._consume_hello({"capabilities": ["audio", "audio.clock"], "audio": {},
                      "audioClock": {"dMs": 300}})
    ok(c.d_ms == 300 and c.align_reason == "", "合法声明放行")
    ok(c.audio_supported, "对齐归因不影响出声归因——对时只服务于对齐")

    # ---- 呈现补偿：对齐生效时用 D，其余维持目标深度 ----
    jb0 = JitterBuffer(48000, 2)
    c.out = type("OutStub", (), {})()   # 只需要 jitter 属性，不碰声卡
    c.out.jitter = jb0
    c.audio_at = time.monotonic()
    ok(c.output_latency_ms() == TARGET_BUFFER_MS, "非对齐：补偿为抖动缓冲目标深度")
    jb0.aligned = True
    ok(c.output_latency_ms() == TARGET_BUFFER_MS,
       "对齐待开闸：还没按目标排程，仍用目标深度")
    jb0.gate_open = True
    ok(c.output_latency_ms() == 300,
       "对齐生效（开过闸）：整条链路的延迟即声明的 D，深度与设备延迟已折算在内")
    c.out = None
    ok(c.output_latency_ms() == 0, "没在出声：无本机延迟可补")

    # ---- 对齐闸门（纯内存驱动 JitterBuffer；断言先验有真实样本再验时刻）----
    ticks_of = lambda ms: int(ms * 10_000)   # 恒等换算：假装线上offset 为 0
    pcm = bytes(range(256)) * 75             # 19200 字节 = 100ms @48k/2ch，非零好认
    need = 1920                              # 一次回调取 10ms

    jb = JitterBuffer(48000, 2)
    jb.set_aligned(True, "", ticks_of)
    jb.feed(pcm, 1000)                       # 头部目标时刻 = 10_000_000 tick
    ok(jb.take(need, 9_950_000) == bytes(need) and not jb.gate_open,
       "出声时刻还差 5ms 到目标：闸门不开，先填零")
    ok(jb.take(need, 10_000_000) == pcm[:need] and jb.gate_open,
       "出声时刻到达目标（含等号）：开闸，出的是真样本且首字节即首采样")
    ok(abs(jb.first_err_ms) < 5, "首块排程误差 <5ms（本 Task 判据）")

    jb = JitterBuffer(48000, 2)
    jb.set_aligned(True, "", ticks_of)
    jb.feed(pcm, 1000)
    ok(jb.take(need, 10_250_005) == pcm[4800:4800 + need] and jb.gate_open,
       "落后 25ms 开局：按 lead 丢 1200 帧过期样本，从追上的位置放真样本")
    ok(jb.dropped == 1200 and abs(jb.first_err_ms) < 5,
       "丢弃按整帧计数，残余误差小于一帧")

    jb = JitterBuffer(48000, 2)
    jb.set_aligned(True, "", ticks_of)
    jb.feed(pcm, 1000)
    ok(jb.take(need, 12_000_000) == bytes(need) and not jb.gate_open,
       "缓冲里的音频全部过期：清掉不开闸，等新帧")
    jb.feed(pcm, 1250)
    ok(jb.take(need, 12_500_000) == pcm[:need] and jb.gate_open,
       "新帧按它自己的目标时刻照常开闸")

    # ---- 时钟未就绪：等待期内不开，超时退回深度闸门 ----
    jb = JitterBuffer(48000, 2)
    jb.set_aligned(True, "", lambda ms: None)
    jb.feed(pcm, 1000)
    ok(jb.take(need, 10_000_000) == bytes(need) and jb.aligned,
       "offset/桥未就绪：等待期内不开闸也不放弃对齐")
    jb.first_feed_mono -= ALIGN_WAIT_MS / 1000 + 1
    jb.take(need, 10_000_000)
    ok(not jb.aligned and jb.align_reason, "超过等待上限退回缓冲深度控制并说明")
    jb.feed(pcm, None)
    jb.feed(pcm, None)
    ok(jb.take(need) == pcm[:need] and jb.gate_open,
       "退回后按攒深度的老规则开闸出真样本")

    # ---- 欠载硬重置丢锚：对齐恢复复用既有的重置规则 ----
    jb = JitterBuffer(48000, 2)
    jb.set_aligned(True, "", ticks_of)
    jb.feed(pcm, 1000)
    jb.take(need, 10_000_000)                # 开闸
    for _ in range(9 + 50):                  # 排干 90ms，再欠载填零 500ms
        jb.take(need, 10_000_000)
    ok(not jb.gate_open and jb.head_captured_ms is None,
       "累计欠载硬重置：闸门关闭并丢锚，下一段来料重新按目标时刻排程")

    print(f"✓ self-test 通过：{n[0]} 条断言全绿")


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
    if "--self-test" in sys.argv[1:]:
        self_test()
        sys.exit(0)
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
