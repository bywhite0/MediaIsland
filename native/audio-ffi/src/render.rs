//! WASAPI 播放。结构照 `capture.rs`：`new` 建对象、`start` 起线程并同步等启动结果、
//! `stop` 同步等线程退出。关闭竞态崩在 native 里会带走整个宿主进程，
//! 那不是可恢复的托管异常。
//!
//! 与采集侧的两处结构差异：
//!
//! 1. 数据源在对面。采集的数据源是 WASAPI，播放的数据源是 FFI 调用方
//!    （C# 的网络收循环），故环形缓冲跨线程共享、由互斥量保护。
//! 2. 重采样器换 `SincFixedOut`。采集侧 `GetBuffer` 给多少就处理多少，输入侧被动；
//!    播放侧相反——本次要输出几帧是 WASAPI 定的，输入是攒在环形缓冲里的任意长度。
//!
//! `played_cb` 送的是重采样前的 48k i16，不是写进设备缓冲的那一份：
//! C# 侧的分析器只认 48000/2ch/i16。

use std::sync::atomic::{AtomicI64, AtomicU32, AtomicU64, Ordering as AtomicOrdering};

use rubato::{SincFixedOut, SincInterpolationParameters, SincInterpolationType, WindowFunction};

use crate::outer_loop::DEAD_ZONE_MS;
use crate::timeline::{CumulativeFrames, TICKS_PER_MS};
use crate::{OUTPUT_CHANNELS, OUTPUT_SAMPLE_RATE};

/// 抖动缓冲目标深度的取值范围。
///
/// 下界不取更小：低于一个 WASAPI 周期（约 20ms）加一次 TCP 重传的量级，
/// 缓冲会持续欠载，表现为断续。上界即环形缓冲容量的一半。
pub const MIN_TARGET_MS: u32 = 50;
pub const MAX_TARGET_MS: u32 = 1_000;

/// 连续欠载累计到此值才硬重置。瞬时欠载填零即可，重置会丢掉已经收到的音频。
const UNDERRUN_RESET_MS: f64 = 500.0;

/// 交给 `rubato` 构造器的相对比率上限。
///
/// 漂移只需 ±0.1%，留到 1% 是因为超出这个带 `set_resample_ratio` 直接返回 Err，
/// 而那发生在实时线程上、没有可上报的地方——宁可让边界宽到永不触发。
/// [`resample_ratio`] 的实际取值范围由测试钉在这个带之内。
const MAX_RELATIVE_RATIO: f64 = 1.01;

/// sinc 插值核长度，与采集侧一致。
///
/// 单列出来是因为它同时进两处：`rubato` 的插值参数，以及单轮输入帧数上界的估算
/// （见 `max_input_frames`）。两处各写一份 128，改一处就会让上界算小。
const SINC_LEN: usize = 128;

pub fn clamp_target_ms(raw: u32) -> u32 {
    raw.clamp(MIN_TARGET_MS, MAX_TARGET_MS)
}

pub fn target_frames(target_ms: u32) -> usize {
    (OUTPUT_SAMPLE_RATE as usize) * (target_ms as usize) / 1_000
}

/// 容量取目标深度上界的两倍。目标深度是稳态深度——尖峰到来时缓冲必须还有地方放；
/// 容量等于目标深度会让每一次抖动都触发溢出丢帧，而丢帧正是抖动缓冲要消除的东西。
///
/// 由 [`MAX_TARGET_MS`] 导出而非直接写 96000：写死的数字与上界是两份各自为真的声明，
/// 上界一旦调整，容量仍是旧值，而「容量是上界两倍」这条性质会静默失效。
pub const RING_CAPACITY_FRAMES: usize =
    (OUTPUT_SAMPLE_RATE as usize) * (MAX_TARGET_MS as usize) / 1_000 * 2;

/// 预填充与欠载统计。抽成独立结构是为了让它可以脱离 WASAPI 被测。
pub struct PrefillState {
    target_frames: usize,
    open: bool,
    underrun_ms: f64,
}

impl PrefillState {
    /// 目标帧数是构造时定下的一个数，不是对某处当前值的引用。
    /// prefill 是一次性粗调，外环是持续微调，前者不跟着后者走。
    pub fn target_frames(&self) -> usize {
        self.target_frames
    }

    pub fn new(target_frames: usize) -> Self {
        Self {
            target_frames,
            open: false,
            underrun_ms: 0.0,
        }
    }

    /// 闸门开了就不再回退——短暂欠载靠填零度过，反复进出预填充会一顿一顿。
    pub fn is_open(&mut self, available_frames: usize) -> bool {
        if !self.open && available_frames >= self.target_frames {
            self.open = true;
        }
        self.open
    }

    /// 记一次欠载。返回真表示该硬重置，调用方须清空环形缓冲并重新预填充。
    pub fn note_underrun(&mut self, missing_ms: f64) -> bool {
        self.underrun_ms += missing_ms;
        if self.underrun_ms >= UNDERRUN_RESET_MS {
            self.open = false;
            self.underrun_ms = 0.0;
            return true;
        }
        false
    }

    /// 取到了数据。欠载是「连续」欠载，中间取到过就重新计。
    pub fn note_progress(&mut self) {
        self.underrun_ms = 0.0;
    }
}

/// 重同步门的超额阈值,毫秒。
///
/// 250ms 高于稳态噪声——对齐 |e| 中位 1.3ms、FIFO 占用锯齿 ±50ms、网络突发簇
/// ~100-200ms 都够不着它——且低于病灶量级(卡顿积压是秒级)。低了会把正常抖动
/// 当积压丢帧,高了会让本可自愈的滞后拖到可感知才动手。
pub const RESYNC_EXCESS_MS: u32 = 250;

/// 重同步门的持续期,以消费帧数计(48k 轴):24_000 帧 = 500ms × 48 帧/ms。
///
/// 500ms 的持续期把一个 RTT 内自行排空的突发簇全部滤掉——只有棘轮/积压这类
/// 不自愈的超额撑得过它。以消费帧数计而非另读时钟:渲染循环的推进本身就是
/// 48k 轴上的时间,不引入第二时基。
pub const RESYNC_SUSTAIN_FRAMES: usize = 24_000;

/// 重同步门:持续超额才点火,与 [`PrefillState`] 同为脱离 WASAPI 可测的纯件。
///
/// 对齐外环限速 0.5ms/s、内环 ±0.1%,秒级积压追不动;硬重置只由欠载触发,
/// 而积压场景恰恰不欠载——这道门补的就是这条自愈缺口。
#[derive(Default)]
pub struct ResyncGate {
    sustained_frames: usize,
}

impl ResyncGate {
    /// 记一轮观测。over 为假立即清零并返回假——回落即重新起算,瞬时超额不积累;
    /// over 为真则累计本轮消费帧数,累计 ≥ [`RESYNC_SUSTAIN_FRAMES`] 即返回真
    /// 并自清(恰达阈也点火,用 `>=`;自清让下一次点火重新攒满整个持续期)。
    pub fn note(&mut self, over_threshold: bool, consumed_frames: usize) -> bool {
        if !over_threshold {
            self.sustained_frames = 0;
            return false;
        }

        self.sustained_frames += consumed_frames;
        if self.sustained_frames >= RESYNC_SUSTAIN_FRAMES {
            self.sustained_frames = 0;
            return true;
        }
        false
    }
}

/// 读游标前跳量的上界钳:一步最多丢到目标深度,丢穿目标深度就是亲手制造欠载。
///
/// 为缩短缓冲而主动丢最旧的两个调用点(重同步门点火、声明预算执行器下调)都过这里,
/// 不各自写一份 min——写两份的那一天,两份里只有一份记得目标深度是下界。使读游标在
/// 累积轴上前跳的另有正常消费、硬重置与空档超容量时的整清,那三条都不是为缩短缓冲丢
/// 最旧。还有一条是丢最旧却不过这道钳:占用顶到容量时继续推入,写游标前移即等价于丢
/// 掉最旧那一帧。那一条丢完占用仍等于容量,而容量是目标深度上限的两倍,故它结构上丢
/// 不穿目标深度——这道钳要守的下界在那一支上自动成立,不是漏了它。
///
/// `available` 必须与真剪处于同一把 ring 锁内；由 [`apply_read_cursor_shift`] 统一执行。
/// 旧快照会跨过正常消费或 `push_at` 的整清，不能作为真剪的盈余依据。
///
/// `saturating_sub` 而非裸减:占用低于目标深度时盈余为负,裸减法在 debug 下当场
/// panic 在实时线程上(带走整个宿主进程),release 下回绕成天文数字、经 min 之后
/// 等于把整个缓冲一次丢空。饱和到 0 的语义恰好正确:没盈余就一帧都不丢。
pub fn clamp_shift_frames(frames: usize, available: usize, target_ms: u32) -> usize {
    frames.min(available.saturating_sub(target_frames(target_ms)))
}

/// 在调用方持有的 ring 锁内取当前盈余、真剪并返回实际位移量。
pub fn apply_read_cursor_shift(
    ring: &mut crate::ring::PlaybackRing,
    want_frames: usize,
    target_ms: u32,
) -> usize {
    let before = ring.available_frames();
    let allowed = clamp_shift_frames(want_frames, before, target_ms);
    ring.drop_oldest(allowed);
    before - ring.available_frames()
}

/// 将重同步超额毫秒折成 48k 轴上的请求帧数；负超额不请求位移。
/// 乘法饱和后除以 1000，保留既有的大值折算口径，盈余钳留给应用者。
pub fn resync_drop_frames(excess_ms: i64) -> usize {
    (OUTPUT_SAMPLE_RATE as usize).saturating_mul(excess_ms.max(0) as usize) / 1_000
}

/// 声明预算阶跃的死区,tick。就是外环死区 [`DEAD_ZONE_MS`] 折成 tick,同一个数
/// 同一个理由:小于误差预算分配额的变化不值得动 target,而每次动 target 都在改
/// 输出延迟。由那个常量导出而不另写一个 10_000——两份各自为真的声明,改动时
/// 总有一份记不住。
///
/// 它不得低于一毫秒,因为生效量折的是整毫秒:[`DEAD_ZONE_MS`] 若降到 1.0 以下,
/// 落在死区与一毫秒之间的变化会越过死区却折出 0 毫秒。那一轮不出错,只是白走
/// 一趟——锚同样前进 0,余量留在原处继续攒,够一毫秒才真动 target。
pub const SETPOINT_DEAD_ZONE_TICKS: i64 = (DEAD_ZONE_MS * TICKS_PER_MS as f64) as i64;

/// 声明预算 D 的阶跃检测:D 变了多少,折成 target 该动多少。与 [`ResyncGate`]
/// 同为脱离 WASAPI 可测的纯件。
///
/// 前馈,不是反馈。收敛条件下出声时刻 = 采集段 + 网络段 + 缓冲深度 + 设备尾段,
/// D 变而后三段里只有缓冲深度可控、另两段不变,故 target 定态值的变化量恰等于
/// ΔD——精确 1:1,不是近似。让外环去「发现」这个已知量是用错了通路:外环按
/// 准静态残差整定(见 [`crate::outer_loop`] 的时间常数),而用户拨滑块是阶跃。
#[derive(Default)]
pub struct SetpointStep {
    anchor: Option<i64>,
}

impl SetpointStep {
    /// 记一轮 D 观测,返回本轮该加到 target 上的毫秒数(0 = 不动作)。
    ///
    /// 首轮只记锚:起播已按那一刻请求的深度起,首轮没有「变化」可言。
    ///
    /// 死区内不更新锚。若逐次更新,连续漂移就永远进不了执行——相邻两轮各 +0.9ms
    /// 都在死区之内,而它们相对同一个锚的累计早已越过死区。这与外环把亚毫秒增量
    /// 攒起来不丢是同一件事。
    ///
    /// 锚按钳前折出的整毫秒前进,与 applied 被钳掉多少无关:钳掉的量本就无处
    /// 可去。若改成「按实际生效量(钳后)前进、把钳掉的部分记成欠账」,target 撞上界
    /// 之后欠账永远还不掉,于是每一轮都点火、每一次 applied 都是 0——那是空转点火。
    ///
    /// 「钳掉的余量」与「截断的余量」是两回事,只有前者要丢。折毫秒向零截断留下的
    /// 不到一毫秒仍留在锚与 `d_ticks` 之间,下一轮接着攒;锚若整量跳到 `d_ticks`,
    /// 每次点火最多永久丢弃将近一毫秒,而 `d_ticks` 每个渲染回调读一次,用户拖动
    /// 滑块时每拍都丢一截,欠跟的那部分会落回外环——绕开外环恰是本纯件的存在理由。
    pub fn note(&mut self, d_ticks: i64, current_target_ms: u32) -> i64 {
        let Some(anchor) = self.anchor else {
            self.anchor = Some(d_ticks);
            return 0;
        };

        // 饱和而非裸减:`d_ticks` 是对端在报文里声明的值,畸形或敌意报文可以给出
        // 任意 i64,而这条链跑在实时渲染线程上,debug 下的溢出 panic 会带走整个
        // 宿主进程,不是可恢复的托管异常。`saturating_abs` 同理(i64::MIN 的 abs)。
        let delta_ticks = d_ticks.saturating_sub(anchor);
        if delta_ticks.saturating_abs() < SETPOINT_DEAD_ZONE_TICKS {
            return 0;
        }

        // 折毫秒用 i64 除法(向零截断),两个方向同一口径。给负方向另写一套取整规则,
        // 等于让「上调半毫秒」与「下调半毫秒」的处置不对称,而 D 是用户来回拨的。
        let delta_ms = delta_ticks / TICKS_PER_MS;
        self.anchor = Some(anchor.saturating_add(delta_ms.saturating_mul(TICKS_PER_MS)));

        let current = i64::from(current_target_ms);
        // 先夹到 u32 值域只为让下面那次 as 无损:负值裸 `as u32` 回绕成天文数字,
        // 经 clamp_target_ms 之后落在上界,即「下调」被静默翻成「上调到上界」。
        // 目标深度那条界仍然只由 clamp_target_ms 一处说。
        let desired = current
            .saturating_add(delta_ms)
            .clamp(0, i64::from(u32::MAX));
        i64::from(clamp_target_ms(desired as u32)) - current
    }
}

/// 位移余额:欠下的读游标位移,按轮摊还。与 [`ResyncGate`]、[`SetpointStep`] 同为
/// 脱离 WASAPI 可测的纯件。
///
/// 只有垫零方向需要记账。下调声明预算要读游标前跳(丢最旧),那一步当场做得完;
/// 上调要读游标暂停——某几轮不从缓冲读、直接写零,生产者继续写故占用上涨——而一轮
/// 只垫得下本轮的输出帧数,要跨若干轮才摊得完,所以待垫的量必须留在某处。
///
/// 本件不知道环形缓冲的存在:容量余量由调用方算好传进来,真正的丢帧与写零也由调用方
/// 去做。它只管 pad_frames 这一个数。
#[derive(Default)]
pub struct PendingShift {
    pad_frames: u64,
}

impl PendingShift {
    /// 完整记录已生效阶跃。生产单笔最多45600帧；保证域要求会话QPC有效单调，
    /// 且target写者只有声明阶跃与限速外环。公开方法不承诺任意usize无限累加安全。
    pub fn owe_pad(&mut self, frames: usize) {
        self.pad_frames += frames as u64;
    }

    /// 欠下一笔真剪(读游标前跳、丢最旧),返回抵扣之后还该真剪多少帧。
    ///
    /// 抵扣在先:pad 是尚未发生的位移,取消它零代价——缓冲里一帧都还没动过。若先把
    /// 垫零垫完再去剪,用户来回拨滑块时会听到一串本可互相抵消的静音与跳跃,而两个
    /// 方向本就是同一根轴上的反向操作,相消是它们的本性。
    ///
    /// 两者各减:抵扣掉的那部分既不再欠垫,也不必真剪。返回值是调用方拿去剪的量,
    /// 怎么剪不归本件知道。
    pub fn owe_trim(&mut self, frames: usize) -> usize {
        let cancel = (frames as u64).min(self.pad_frames);
        self.pad_frames -= cancel;
        frames - cancel as usize
    }

    /// 按需求与当刻容量余量支付，只扣实付。无新命令或重置时，累计支付机会
    /// 达到初始债即偿清；每轮机会至少h、间隔至多δ时，期限为ceil(P/h)*δ。
    pub fn take_pad(&mut self, needed: usize, headroom: usize) -> usize {
        let taken = self.pad_frames.min(needed.min(headroom) as u64);
        self.pad_frames -= taken;
        taken as usize
    }

    /// 硬重置用:ring 已全清,欠下的位移无所指——它记的是「把现有占用推到某处」,
    /// 而现有占用已经不在了。留着会让重新预填充之后凭空垫上一段静音。
    pub fn clear(&mut self) {
        self.pad_frames = 0;
    }
}

pub struct ShiftRead {
    pub padded: usize,
    pub wanted: usize,
    pub taken: usize,
}

/// 持同一把锁读取实际余量并消费；全垫轮也取锁，锁内不分配。
pub fn consume_shifted(
    ring: &std::sync::Mutex<crate::ring::PlaybackRing>,
    shift: &mut PendingShift,
    out: &mut [i16],
) -> Option<ShiftRead> {
    let mut ring = ring.lock().ok()?;
    let channels = usize::from(OUTPUT_CHANNELS);
    let needed = out.len() / channels;
    let padded = shift.take_pad(needed, ring.capacity_frames() - ring.available_frames());
    out[..padded * channels].fill(0);
    let wanted = needed - padded;
    let taken = ring.read_into(&mut out[padded * channels..]);
    Some(ShiftRead {
        padded,
        wanted,
        taken,
    })
}

pub struct ResyncObservation {
    pub applied: i64,
    pub aligned_error_ticks: Option<i64>,
    pub available_frames: usize,
    pub observation_target_ms: u32,
    pub current_target_ms: u32,
    pub needed: usize,
}

/// 阶跃轮舍弃旧观测；只有真实重定位才结算当前动作段未付债。
pub fn apply_resync_gate(
    gate: &mut ResyncGate,
    shift: &mut PendingShift,
    ring: &std::sync::Mutex<crate::ring::PlaybackRing>,
    obs: ResyncObservation,
) -> Option<usize> {
    if obs.applied != 0 {
        return Some(0);
    }
    let excess_ms = match obs.aligned_error_ticks {
        Some(error) => error / TICKS_PER_MS,
        None => frames_to_ms(obs.available_frames) as i64 - i64::from(obs.observation_target_ms),
    };
    if !gate.note(excess_ms > i64::from(RESYNC_EXCESS_MS), obs.needed) {
        return Some(0);
    }
    let mut ring = ring.lock().ok()?;
    let dropped = apply_read_cursor_shift(
        &mut ring,
        resync_drop_frames(excess_ms),
        obs.current_target_ms,
    );
    if dropped > 0 {
        shift.clear();
    }
    Some(dropped)
}

/// 声明预算 D 的有效域闸:offset 不可用时 D 无所指,既不执行也不动锚。
///
/// 这不是一道防抖动的护栏,是 D 的定义决定的。D 的含义是「在发送端采样之后 D 毫秒
/// 出声」,而把发送端的那个时刻映射到本机轴要靠跨机 offset。offset 不可用时,那个时刻
/// 在本机没有坐标,「D 毫秒之后」也就没有所指;此时让目标深度去追 D,追的是一个没有
/// 物理所指的数。下发侧按同一口径:判不过时 D 与 offset 一起置零,那个零的含义是
/// 「D 此刻不可用」,不是「D 是 0」。不带闸去读它,是在它的有效域之外读它。
///
/// 收整枚 [`AlignmentCell`] 而不是收一个 `bool`:闸的判据在于它绑到哪个条件上,
/// 而绑定关系一旦落在形参上就跑到调用点去了,判据再也看不见它——传 `true` 与传
/// [`AlignmentCell::is_enabled`] 在那种形态下无从分辨,而后者在 offset 判不过时仍为真。
/// D 也一并在这里读,理由相同:两次读同属一个绑定。
///
/// 闸住的必须是整个 [`SetpointStep::note`],不能只闸返回值。锚若在闸关期间跟着那个
/// 不可用的值挪过去,恢复的那一轮 D 一回到真值,锚与它的差就成了一次凭空的反向阶跃
/// ——用户没拨过滑块,读游标却跳了一整段。
///
/// 代价是闸关期间用户拨滑块不当场生效。这与用户在该状态下本就成立的可见契约一致
/// (声音照出、只是不对齐),且锚停在最后一个有效 D 上,恢复那一轮一次性补上。
pub fn live_setpoint_step(
    setpoint: &mut SetpointStep,
    alignment: &AlignmentCell,
    current_target_ms: u32,
) -> i64 {
    if !alignment.offset_available() {
        return 0;
    }

    setpoint.note(alignment.d_ticks(), current_target_ms)
}

/// 一次声明预算阶跃的位移计划:目标深度推到哪,以及抵扣后请求剪多少帧。
///
/// 两个方向是同一根轴上的反向操作。`applied` 为负是下调:读游标前跳、丢最旧,当场
/// 做得完,故本件把该剪的帧数算出来交调用方去剪。`applied` 为正是上调:读游标暂停,
/// 某几轮不从缓冲读而直接写零,生产者继续写故占用上涨,跨若干轮摊完,故本件只把欠账
/// 记进 `shift`、真剪帧数返 0。`applied` 为零两侧都不动,连 `shift` 都不碰。
///
/// 下调仅返回抵扣后的请求，调用方在同锁真剪时按新目标与当前占用钳制。
/// 上调完整记债，容量约束留给消费时点。applied须来自SetpointStep::note，
/// 因而绝对值不超过目标范围；这里不接纳任意未经验证的i64位移。
pub fn plan_setpoint_shift(applied: i64, target_ms: u32, shift: &mut PendingShift) -> (u32, usize) {
    if applied == 0 {
        return (target_ms, 0);
    }

    let next_target_ms = (i64::from(target_ms) + applied) as u32;
    if applied < 0 {
        // 抵扣在先:pad 是尚未发生的位移,取消它零代价——缓冲里一帧都还没动过。若先把
        // 垫零垫完再去剪,用户来回拨滑块会听到一串本可互相抵消的静音与跳跃。
        let owed = shift.owe_trim(target_frames((-applied) as u32));
        return (next_target_ms, owed);
    }

    shift.owe_pad(target_frames(applied as u32));
    (next_target_ms, 0)
}

/// 送给 `rubato` 的重采样比率。
///
/// 两个方向必须分清，接反了在真机上表现为「放十几分钟后开始周期性卡顿」，
/// 极难归因到一个符号上：
///
/// - `ring::drift_ratio` 说的是播放速度：缓冲比目标深 → 放快一点 → 返回 > 1。
/// - `rubato` 的比率是输出帧数 / 输入帧数：本次要输出的帧数是 WASAPI 定的，
///   放快一点等价于多吃输入，故比率要变小。
///
/// 所以漂移项取倒数。基准比率是 `设备率 / 48000`——48k 的流要变成设备要的帧率。
pub fn resample_ratio(device_rate: u32, available_ms: f64, target_ms: f64) -> f64 {
    let base = device_rate as f64 / OUTPUT_SAMPLE_RATE as f64;
    base / crate::ring::drift_ratio(available_ms, target_ms)
}

/// 帧数换毫秒，按传输采样率。
pub fn frames_to_ms(frames: usize) -> f64 {
    frames as f64 * 1_000.0 / OUTPUT_SAMPLE_RATE as f64
}

/// 本轮是否处于预填充；是则返回该送出的零值帧数（48k 域）。
///
/// 抽成纯函数不是为了复用，是为了让这段接线可测——原先它内嵌在渲染循环里，
/// 而循环没有任何自动化测试进得去，于是「用设备帧数还是 48k 帧数」这个选择
/// 无人看守，实际就选错了：>48k 的设备上首轮即越界 panic。
///
/// 夹到 `max_input_frames` 是最后一道防线：越界发生在实时线程上，
/// panic 会带走整个宿主进程，不是可恢复的托管异常。
pub fn prefill_silence_frames(
    prefill: &mut PrefillState,
    available_frames: usize,
    writable_device_frames: DeviceFrames,
    device_rate: u32,
    max_input_frames: usize,
) -> Option<usize> {
    if prefill.is_open(available_frames) {
        return None;
    }

    Some(output_frames_for(writable_device_frames, device_rate).min(max_input_frames))
}

/// 设备帧轴的围栏子模块：类型、构造入口与唯一换算通道住在这里，
/// .0 字段访问全部收在模块内。
mod device_frames {
    use crate::OUTPUT_SAMPLE_RATE;

    /// 设备帧轴上的帧数：WASAPI 端点按自身混音率数出来的帧，本轮 padding、本轮可写数、
    /// 端点缓冲容量都在这条轴上。
    ///
    /// 与累积帧轴（timeline 侧的 CumulativeFrames，48k 域）是两条互不通约的轴：设备率
    /// 不等于 48000 时两侧一帧的时长不同，直接混用曾把误差整体偏移一个端点缓冲长度
    /// （本机 22 毫秒），且各设备不同。故本类型不提供跨轴算术，也不实现 Deref、From
    /// 一类会重新打开混用面的转换；换轴的唯一通道是 output_frames_for，
    /// 裸整数只在 WASAPI 交数的那一处进入。收进私有子模块使同文件的 .0 也不可达，
    /// 模块外任何一处字段访问都是编译错误。
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub struct DeviceFrames(usize);

    impl DeviceFrames {
        /// 从裸整数进入设备帧轴。只该出现在 WASAPI 边界与测试里。
        pub fn new(raw: usize) -> Self {
            Self(raw)
        }
    }

    /// 设备帧数折算成同时长的传输帧数（48k 域）。两轴之间唯一的换算通道。
    ///
    /// 两个域必须分清。送给 `played_cb` 的缓冲按 48k 输入帧分配，而 WASAPI 说的
    /// 「本轮可写几帧」是设备帧。设备率 >48000 时设备帧数多于同时长的 48k 帧数，
    /// 拿设备帧数去索引按 48k 分配的缓冲就是越界——96kHz / 20ms 下是 3840 索引进
    /// 长 2182 的缓冲，渲染线程当场 panic，而此时 `render_start` 已经返回过 OK。
    pub fn output_frames_for(device_frames: DeviceFrames, device_rate: u32) -> usize {
        if device_rate == 0 || device_rate == OUTPUT_SAMPLE_RATE {
            return device_frames.0;
        }

        device_frames.0 * OUTPUT_SAMPLE_RATE as usize / device_rate as usize
    }
}

pub use device_frames::{output_frames_for, DeviceFrames};

/// 正在出声的采样在累积轴（48k 域）上的位置。
///
/// `read_cursor` 数的是从 ring 读走的 48k 帧，`padding_device_frames` 是端点里还压着
/// 的帧数——但那是设备帧，必须先折回 48k 域再减。这一步与 `prefill_silence_frames` 当年抽出的
/// 理由相同：「用设备帧数还是 48k 帧数」内嵌在循环里无人看守时选错过一次，而这里
/// 选错的后果是误差整体偏移一个端点缓冲长度（本机 22 毫秒），且各设备不同，
/// 直接变成机间错位。padding 大于读游标（刚起播、硬重置刚过）时饱和到 0，
/// 调用方经 `sender_ticks_at` 的本轮门自然得到 `None`。
pub fn playing_position_frames(
    read_cursor: CumulativeFrames,
    padding_device_frames: DeviceFrames,
    device_rate: u32,
) -> CumulativeFrames {
    read_cursor.saturating_rewind(output_frames_for(padding_device_frames, device_rate) as u64)
}

/// 重采样比换成 ppm（百万分之一）。
///
/// 为什么不把 f64 直接送出 FFI：跨 FFI 的结构体全字段用 u64 是为了消除 padding 歧义
/// （见 crate::RenderStats），而 f64 要走 to_bits / from_bits，两端各多一处可错的地方。
/// ppm 对判据够用——控制律的相对幅度上界是 MAX_RELATIVE_RATIO，即偏离 1.0 最多
/// 一万 ppm，而 ppm 的分辨率是 1。
///
/// 非有限或非正的输入返回 0，与「未起播」共用同一个哨兵：两者对读者的含义相同，
/// 都是「这个数不可用，别拿它算」。
pub fn ratio_to_ppm(ratio: f64) -> u64 {
    if !ratio.is_finite() || ratio <= 0.0 {
        return 0;
    }

    let ppm = (ratio * 1_000_000.0).round();
    if ppm >= u64::MAX as f64 {
        return u64::MAX;
    }

    ppm as u64
}

/// `IAudioClock::GetPosition` 的位置归一为 48000Hz 域的帧数。
///
/// 归一一律经 `GetFrequency`，不按 `nBlockAlign` 推。实测某端点的 `GetFrequency`
/// 恰等于采样率乘 `nBlockAlign`（384000 = 48000 × 8），故按后者算也得到正确帧数——
/// 那是巧合而非正确性。错的是常数因子时误差看起来是稳定的，不容易暴露。
///
/// 频率为 0 时返回 0：那意味着位置无从归一，而 0 与「还没起播」同义，
/// 恰是这个字段既有的空值约定。
pub fn output_frames_at_device_position(position: u64, frequency: u64) -> u64 {
    if frequency == 0 {
        return 0;
    }

    // 先乘后除，且走 u128：位置在字节单位下每秒涨 384000，u64 里乘 48000 的余量
    // 约合三十年，够用但没有理由去贴那个上限。
    let frames = u128::from(position) * u128::from(OUTPUT_SAMPLE_RATE) / u128::from(frequency);
    u64::try_from(frames).unwrap_or(u64::MAX)
}

/// 设备取走数据之后到出声那段固定尾段的估计，微秒。
///
/// 主体是引擎周期：`GetStreamLatency` 在共享模式下实测报 0——本机 14 个输出端点
/// （含两个真实硬件端点）无一例外，故它只是可选的附加项，有值才叠加。
/// 负值同样当没有：那是「取不到」的表示，不是一段负延迟。
///
/// 剩下的硬件尾段（DAC、功放、尤其蓝牙）在软件层不可观测，由用户的 per-device
/// 手动偏移承担。这不是偷懒，是承认可观测性边界。
///
/// 端点缓冲的容量不进这里。它当中的实际占用由位置锚点逐轮测得，两者相加是把同一段
/// 延迟计两次；容量另有用处——判「本机最小可达延迟是否超过 D」时它是下限的组成部分。
pub fn device_latency_us(engine_period_100ns: i64, stream_latency_100ns: i64) -> u64 {
    let period = engine_period_100ns.max(0);
    let stream = stream_latency_100ns.max(0);
    let total_100ns = period.saturating_add(stream);
    u64::try_from(total_100ns / 10).unwrap_or(0)
}

/// 本包该不该走时间轴（补空档静音并记锚点），还是走不带时刻的原路径。
///
/// 抽成纯函数不是为了复用，是为了让这段接线可测——它此前内嵌在 FFI 包装层里，
/// 而那里没有任何自动化测试进得去，于是把两个分支对调也不会有判据变红。
/// 同一个理由抽出过 [`prefill_silence_frames`] 与 [`output_frames_for`]。
///
/// 两个条件必须同时成立。只看时刻会让 native 侧有两个互不知情的对齐开关：调用方送了
/// 非零时刻，即使用户关着对齐，这一包也会走补静音与锚点那条路，而「对齐关闭时热路径
/// 逐字走原路径」这条约束点名的是那个性质本身，不只是它的可测后果。
///
/// 时刻为 0 表示本帧没有时刻。这条约定要求送进来的是自 1970 起算的 100ns 计次——
/// 0 在那个时基下不是会出现的值。以起播为原点的相对时基不能直接送进来：
/// 那种时基的第一帧恰好是 0，会被读成「没有时刻」。
pub fn should_use_timeline(sender_ticks: i64, alignment_enabled: bool) -> bool {
    sender_ticks != 0 && alignment_enabled
}

/// 重采样器与它的状态。住在 cfg 门之外：`rubato` 是平台无关的依赖，这里没有一处
/// WASAPI 调用，故它可以脱离设备被测——而「建还是不建」正是本模块最容易悄悄错的判断。
pub struct ResamplerState {
    pub(crate) inner: SincFixedOut<f32>,
}

/// 按设备率与对齐模式决定是否建重采样器。
///
/// 设备混音率恰为 48000 且未开对齐时返回 `None`——直接格式转换后写入，零额外缓冲。
/// 那是最常见的配置。
///
/// 开了对齐则 48000 也要建。理由是内环需要一个执行器：`resample_ratio` 算出的比率
/// 只能由重采样器执行，而 48k 端点上此前它是 `None`，于是漂移控制律在最常见的配置上
/// 根本没有载体。音频设备晶振典型 ±50ppm，两台之间相对偏差可达 200ppm，
/// 10 毫秒除以 200ppm 是 50 秒——一次性对齐撑不过一分钟。
///
/// 代价是 48k 路径不再 bit-exact：`f_cutoff` 为 0.95，即使恒等重采样也会低通到约
/// 22.8kHz。人耳无感，但这是一次真实的信号改动，故按需付费——不开对齐的用户逐字
/// 走原路径，一个采样都不经过重采样器。
///
/// `device_rate` 或 `buffer_frames` 为 0 时无条件不建，与模式无关：那是参数无效，
/// 而无效参数下建出来的东西没有正确形态可言。
///
/// 决定在起播那一刻做出，此后不再变——重建要在实时线程上分配，而分配的停顿就是
/// 可听的 glitch。故这里读的是用户的对齐设置（起播前已知），不是运行时的 offset
/// 可用性（连上几秒后才有）。两者混为一谈会让 48k 端点在对齐启用后没有内环。
pub fn build_resampler(
    alignment_enabled: bool,
    device_rate: u32,
    buffer_frames: u32,
) -> Option<ResamplerState> {
    if device_rate == 0 || buffer_frames == 0 {
        return None;
    }

    if !alignment_enabled && device_rate == OUTPUT_SAMPLE_RATE {
        return None;
    }

    let params = SincInterpolationParameters {
        sinc_len: SINC_LEN,
        f_cutoff: 0.95,
        interpolation: SincInterpolationType::Linear,
        oversampling_factor: 128,
        window: WindowFunction::BlackmanHarris2,
    };

    let inner = SincFixedOut::<f32>::new(
        device_rate as f64 / OUTPUT_SAMPLE_RATE as f64,
        MAX_RELATIVE_RATIO,
        params,
        buffer_frames as usize,
        OUTPUT_CHANNELS as usize,
    )
    .ok()?;

    Some(ResamplerState { inner })
}

/// 对齐参数的可写载体。
///
/// 与 [`TargetDepthCell`] 同一形态与同一理由：托管侧写它，渲染线程每轮读。
/// 四个值按生命周期分成两半，写入口也分成两半：
///
/// - `enabled` 是起播时点的量：它决定要不要付重采样器的代价，而那个决定只能在
///   起播那一刻做（重建要在实时线程上分配），会话内不可变。[`WasapiRenderer::start`]
///   在拉起渲染线程之前写它恰好一次，此后没有别的写者——「先设好再起播」不再是
///   调用方要记住的约定，而是参数表的形状。
/// - d / offset / manual_offset 是运行时量，随对时结果与设置变化，播放中随时可写。
///   三个值各自一个原子、不保证同一瞬间——它们变化的时间尺度是秒级（对时窗口更新、
///   用户拖动偏移），而渲染轮次是十毫秒级，跨轮取到新旧混合的一组至多影响一轮。
///
/// `enabled` 与「offset 是否可用」仍是两件事，不能合成一个位：前者决定建不建执行器，
/// 后者决定外环此刻能不能动。两者合一的写法会让 48k 端点在「起播时还没对上时钟、
/// 几秒后对上了」这条最常见的时序上没有内环。
///
/// offset 不可用由 `offset_ticks` 为 0 表示。0 是不可能值：offset 是本机 QPC（自开机
/// 起算）减发送端墙钟的 100ns 表示（自 1970 起算），两者相差约 1.7e16 tick，
/// 恰好抵成 0 要求发送端的墙钟等于本机的开机时长。与 `render_push` 的时刻用 0 表示
/// 「没有时刻」同一形态、同一理由。
#[derive(Default)]
pub struct AlignmentCell {
    enabled: AtomicU32,
    d_ticks: AtomicI64,
    offset_ticks: AtomicI64,
    manual_offset_ticks: AtomicI64,
}

impl AlignmentCell {
    /// 写起播参数。只该由 [`WasapiRenderer::start`] 在拉起渲染线程之前调用。
    ///
    /// Release 与 [`AlignmentCell::is_enabled`] 的 Acquire 配对。托管侧的起播路径
    /// 先补发暂存的运行时三项（另一次 FFI 调用，同一条托管线程），随后才走到这里——
    /// Release store 把程序顺序上先行的那三次 Relaxed store 一并带给读到真值的读者，
    /// 故读到启用位为真即保证起播前下发的三项也已可见。
    ///
    /// x86-TSO 下硬件本就不重排 store，这处写对与写错在本机实测上不可区分——
    /// 它只能靠推理保证，不能靠一条判据。
    pub fn set_enabled(&self, enabled: bool) {
        self.enabled
            .store(u32::from(enabled), AtomicOrdering::Release);
    }

    /// 写运行时三项。播放中随时可调，未起播时也可调——值存在这里等下次会话读。
    pub fn set_runtime(&self, d_ticks: i64, offset_ticks: i64, manual_offset_ticks: i64) {
        self.d_ticks.store(d_ticks, AtomicOrdering::Relaxed);
        self.offset_ticks
            .store(offset_ticks, AtomicOrdering::Relaxed);
        self.manual_offset_ticks
            .store(manual_offset_ticks, AtomicOrdering::Relaxed);
    }

    /// 用户的对齐设置，即本会话的起播参数。
    ///
    /// Acquire 而非 Relaxed：与 [`AlignmentCell::set_enabled`] 那次 Release 配对，
    /// 读到启用位为真即保证起播前下发的运行时三项也已可见。其余三个读取器可以留
    /// Relaxed，因为通往它们的路径都先经过这里。
    pub fn is_enabled(&self) -> bool {
        self.enabled.load(AtomicOrdering::Acquire) != 0
    }

    /// 跨机 offset 此刻可用。外环据它决定动不动。
    pub fn offset_available(&self) -> bool {
        self.is_enabled() && self.offset_ticks.load(AtomicOrdering::Relaxed) != 0
    }

    pub fn d_ticks(&self) -> i64 {
        self.d_ticks.load(AtomicOrdering::Relaxed)
    }

    pub fn offset_ticks(&self) -> i64 {
        self.offset_ticks.load(AtomicOrdering::Relaxed)
    }

    pub fn manual_offset_ticks(&self) -> i64 {
        self.manual_offset_ticks.load(AtomicOrdering::Relaxed)
    }
}

/// 目标深度的可写载体。
///
/// 抽成独立结构与 [`RenderStatsCell`] 同理：它要脱离 WASAPI 被测。
/// 之前这个值是 render 循环外读一次的 u32 快照，于是外环算出多少都没有载体可写。
///
/// Relaxed 够用的理由与 stats 那段相同：这个值不参与同步任何其他内存访问，
/// 读者最终看到即可。它也绝不去抢 ring 那把互斥量——对面等那把锁的是 WASAPI 实时线程。
pub struct TargetDepthCell(AtomicU32);

impl TargetDepthCell {
    /// 起播前无人读它，[`WasapiRenderer::start`] 必定覆写。
    /// 初值给下界而不是 0：0 不在合法区间内，读到它的人会以为配置坏了。
    pub fn new(raw_ms: u32) -> Self {
        Self(AtomicU32::new(clamp_target_ms(raw_ms)))
    }

    /// 写入侧的唯一入口，一律过 [`clamp_target_ms`]。
    ///
    /// 夹紧放在写入侧而不是读出侧：读在 WASAPI 实时线程上每轮一次，而写来自外环，
    /// 频率低两个数量级；更要紧的是越界值若能存进来，此后每一次读都要重新判一遍它。
    pub fn set_ms(&self, raw_ms: u32) {
        let clamped = clamp_target_ms(raw_ms);
        self.0.store(clamped, AtomicOrdering::Relaxed);
    }

    pub fn current_ms(&self) -> u32 {
        self.0.load(AtomicOrdering::Relaxed)
    }
}

/// 渲染线程的统计量。
///
/// 全部原子且只用 Relaxed。Relaxed 够用的理由：这些值不参与同步任何其他内存访问，
/// 读者只要最终看到即可，而它们之间也不需要互相有序。
///
/// 更要紧的是它们绝不去抢 ring 那把互斥量——对面等那把锁的是 WASAPI 实时线程，
/// 而观测手段不该改变被观测对象的时序。
///
/// 代价：十六个字段不是同一瞬间的快照，可能跨越一次渲染轮次。判据应看斜率与累计计数的
/// 单调性，不要依赖十六元组的瞬时一致性。这一点对新增的位置锚点尤其要紧：
/// `device_position_frames` 与 `device_position_qpc` 是同一次 `GetPosition` 的一对返回值，
/// 但两个原子分开写，读者可能取到跨轮的一对。判据应比它们的斜率，不比某一瞬的差值。
#[derive(Default)]
pub struct RenderStatsCell {
    ring_frames: AtomicU64,
    underrun_count: AtomicU64,
    hard_reset_count: AtomicU64,
    device_frames_rendered: AtomicU64,
    device_sample_rate: AtomicU64,
    resample_ratio_ppm: AtomicU64,
    device_position_frames: AtomicU64,
    device_position_qpc: AtomicU64,
    device_latency_us: AtomicU64,
    play_time_error_us: AtomicI64,
    target_ms_current: AtomicU64,
    clock_offset_available: AtomicU64,
    device_buffer_frames: AtomicU64,
    device_clock_available: AtomicU64,
    swallowed_gap_count: AtomicU64,
    overlap_count: AtomicU64,
}

impl RenderStatsCell {
    pub fn set_ring_frames(&self, frames: usize) {
        self.ring_frames
            .store(frames as u64, AtomicOrdering::Relaxed);
    }

    pub fn note_underrun(&self) {
        self.underrun_count.fetch_add(1, AtomicOrdering::Relaxed);
    }

    pub fn note_hard_reset(&self) {
        self.hard_reset_count.fetch_add(1, AtomicOrdering::Relaxed);
    }

    /// 记一次亚下限空档：帧间隔落在噪声带与真空档下限之间、被吞掉不补的那一档。
    /// 分类由 timeline 判（纯函数），这里只累计——与「噪声是入参的性质」同一条分层。
    pub fn note_swallowed_gap(&self) {
        self.swallowed_gap_count
            .fetch_add(1, AtomicOrdering::Relaxed);
    }

    /// 记一次重叠：发送端时间轴倒走超过噪声带。正常发送端不该有。
    pub fn note_overlap(&self) {
        self.overlap_count.fetch_add(1, AtomicOrdering::Relaxed);
    }

    pub fn add_rendered(&self, device_frames: usize) {
        self.device_frames_rendered
            .fetch_add(device_frames as u64, AtomicOrdering::Relaxed);
    }

    pub fn set_device_rate(&self, rate: u32) {
        self.device_sample_rate
            .store(rate as u64, AtomicOrdering::Relaxed);
    }

    pub fn set_ratio_ppm(&self, ppm: u64) {
        self.resample_ratio_ppm.store(ppm, AtomicOrdering::Relaxed);
    }

    /// 起播时清零。
    ///
    /// 停播时刻意不清：本次会话的累计值是停播后唯一还能读到的诊断信息，
    /// 而「这次播放共欠载几次、硬重置几次」正是关停检查要看的。
    /// 托管侧的句柄在停播后依然活着（只有释放才销毁），故读得到。
    pub fn reset(&self) {
        self.ring_frames.store(0, AtomicOrdering::Relaxed);
        self.underrun_count.store(0, AtomicOrdering::Relaxed);
        self.hard_reset_count.store(0, AtomicOrdering::Relaxed);
        self.device_frames_rendered
            .store(0, AtomicOrdering::Relaxed);
        self.device_sample_rate.store(0, AtomicOrdering::Relaxed);
        self.resample_ratio_ppm.store(0, AtomicOrdering::Relaxed);
        self.device_position_frames
            .store(0, AtomicOrdering::Relaxed);
        self.device_position_qpc.store(0, AtomicOrdering::Relaxed);
        self.device_latency_us.store(0, AtomicOrdering::Relaxed);
        self.play_time_error_us.store(0, AtomicOrdering::Relaxed);
        self.target_ms_current.store(0, AtomicOrdering::Relaxed);
        self.clock_offset_available
            .store(0, AtomicOrdering::Relaxed);
        self.device_buffer_frames.store(0, AtomicOrdering::Relaxed);
        self.device_clock_available
            .store(0, AtomicOrdering::Relaxed);
        self.swallowed_gap_count.store(0, AtomicOrdering::Relaxed);
        self.overlap_count.store(0, AtomicOrdering::Relaxed);
    }

    /// 设备位置锚点：`IAudioClock::GetPosition` 的位置（已归一为 48000Hz 域的帧数）
    /// 与取该位置时的 QPC 时刻。
    ///
    /// 两个值一次写入，但落在两个原子上，故读者可能取到跨轮的一对。它们的用途是算斜率
    /// 与算出声时刻，两者都容忍一轮的错配（一轮约 10 毫秒，而位置本身在推进）。
    pub fn set_device_position(&self, frames: u64, qpc_100ns: u64) {
        self.device_position_frames
            .store(frames, AtomicOrdering::Relaxed);
        self.device_position_qpc
            .store(qpc_100ns, AtomicOrdering::Relaxed);
    }

    /// 设备取走数据之后到出声那段固定尾段的估计，微秒。
    ///
    /// 0 的含义是「没有估计」，不是「零延迟」。实测本机 14 个输出端点
    /// （含两个真实硬件端点）无一报出非零的流延迟，故这个值的主体是引擎周期，
    /// 流延迟只在报了值时叠加。硬件尾段（DAC、功放、蓝牙）在软件层不可观测，
    /// 由用户的 per-device 手动偏移承担。
    pub fn set_device_latency_us(&self, us: u64) {
        self.device_latency_us.store(us, AtomicOrdering::Relaxed);
    }

    /// 外环误差：出声时刻减目标时刻，微秒。有符号。
    ///
    /// 不用「加偏置存成无符号」那种编码：偏置是一个必须两侧同时记得的约定，
    /// 而 i64 与 long 在两侧都是原生类型，少一个约定就少一处会漂移的地方。
    pub fn set_play_time_error_us(&self, us: i64) {
        self.play_time_error_us.store(us, AtomicOrdering::Relaxed);
    }

    /// 渲染循环当前实际在用的目标深度。
    ///
    /// 与请求值分开是必须的：外环会把它推离请求值，而「循环在用哪个值」此前从外部
    /// 不可观测——把每轮重读挪回循环外，没有任何判据会变红。这个字段就是那条缺口的收口。
    pub fn set_target_ms_current(&self, ms: u32) {
        self.target_ms_current
            .store(u64::from(ms), AtomicOrdering::Relaxed);
    }

    /// 对齐此刻是否在进行，即用户开了对齐且 offset 已下发。
    ///
    /// 它不是「offset 可用」单独一件事：offset 由托管侧算并下发，托管侧本来就知道
    /// 它算出来没有。这个字段对托管侧的用处是回读确认，不是新信息。
    pub fn set_clock_offset_available(&self, available: bool) {
        self.clock_offset_available
            .store(u64::from(available), AtomicOrdering::Relaxed);
    }

    /// 端点缓冲容量，设备帧数。起播时写一次——它由设备定，会话期间不变。
    ///
    /// 报容量而非当前占用：占用走位置锚点，两者相加是把同一段延迟计两次。
    /// 托管侧要它来算「本机最小可达延迟」，而缓冲长度比请求值大且各设备不同，
    /// 按请求值推算会低估。
    pub fn set_device_buffer_frames(&self, frames: u32) {
        self.device_buffer_frames
            .store(u64::from(frames), AtomicOrdering::Relaxed);
    }

    /// 设备时钟服务是否可用。为假时位置锚点根本不产生，对齐无从进行。
    ///
    /// 这是 native 独占的一条事实。少了它，托管侧看到的只是位置恒为 0，
    /// 而那与「刚起播还没转起来」不可区分，于是一台取不到时钟的机器会一直报
    /// 「尚未对上时钟」——那条提示指向等待，而它永远不会好转。
    pub fn set_device_clock_available(&self, available: bool) {
        self.device_clock_available
            .store(u64::from(available), AtomicOrdering::Relaxed);
    }

    pub fn snapshot(&self) -> crate::RenderStats {
        crate::RenderStats {
            ring_frames: self.ring_frames.load(AtomicOrdering::Relaxed),
            underrun_count: self.underrun_count.load(AtomicOrdering::Relaxed),
            hard_reset_count: self.hard_reset_count.load(AtomicOrdering::Relaxed),
            device_frames_rendered: self.device_frames_rendered.load(AtomicOrdering::Relaxed),
            device_sample_rate: self.device_sample_rate.load(AtomicOrdering::Relaxed),
            resample_ratio_ppm: self.resample_ratio_ppm.load(AtomicOrdering::Relaxed),
            device_position_frames: self.device_position_frames.load(AtomicOrdering::Relaxed),
            device_position_qpc: self.device_position_qpc.load(AtomicOrdering::Relaxed),
            device_latency_us: self.device_latency_us.load(AtomicOrdering::Relaxed),
            play_time_error_us: self.play_time_error_us.load(AtomicOrdering::Relaxed),
            target_ms_current: self.target_ms_current.load(AtomicOrdering::Relaxed),
            clock_offset_available: self.clock_offset_available.load(AtomicOrdering::Relaxed),
            device_buffer_frames: self.device_buffer_frames.load(AtomicOrdering::Relaxed),
            device_clock_available: self.device_clock_available.load(AtomicOrdering::Relaxed),
            swallowed_gap_count: self.swallowed_gap_count.load(AtomicOrdering::Relaxed),
            overlap_count: self.overlap_count.load(AtomicOrdering::Relaxed),
        }
    }
}

#[cfg(windows)]
pub use wasapi::WasapiRenderer;

/// WASAPI 互操作。整段只在 Windows 编译，故用一处 cfg 门而非逐项标注；
/// 上面的纯逻辑不带门，这样它在任何平台都被编译与测试。
#[cfg(windows)]
mod wasapi {
    use std::ffi::c_void;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::mpsc;
    use std::sync::{Arc, Mutex};
    use std::thread::JoinHandle;

    use rubato::Resampler;
    use windows::Win32::Foundation::HANDLE;
    use windows::Win32::Media::Audio::{
        eConsole, eRender, IAudioClient, IAudioClock, IAudioRenderClient, IMMDeviceEnumerator,
        MMDeviceEnumerator, AUDCLNT_BUFFERFLAGS_SILENT, AUDCLNT_SHAREMODE_SHARED,
        AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
    };
    use windows::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_ALL,
        COINIT_MULTITHREADED,
    };
    use windows::Win32::System::Threading::{CreateEventW, SetEvent};

    use super::{
        apply_read_cursor_shift, apply_resync_gate, build_resampler, consume_shifted,
        device_latency_us, frames_to_ms, live_setpoint_step, output_frames_at_device_position,
        plan_setpoint_shift, playing_position_frames, prefill_silence_frames, ratio_to_ppm,
        resample_ratio, target_frames, AlignmentCell, DeviceFrames, PendingShift, PrefillState,
        RenderStatsCell, ResamplerState, ResyncGate, ResyncObservation, SetpointStep,
        TargetDepthCell, MIN_TARGET_MS, RING_CAPACITY_FRAMES,
    };
    use crate::convert;
    use crate::convert::MixFormat;
    use crate::outer_loop::{
        actual_play_ticks, play_time_error_ticks, target_play_ticks, OuterLoop,
    };
    use crate::ring::PlaybackRing;
    use crate::run_guarded;
    use crate::timeline::{GapKind, TICKS_PER_MS, TICKS_PER_SECOND};
    use crate::wasapi_common::{parse_mix_format, wait_for_any, StopEvent, WaitObject};
    use crate::{
        AudioError, PlayedFrame, PlayedFrameCallback, OUTPUT_CHANNELS, STATUS_ALREADY_RUNNING,
        STATUS_PANIC,
    };

    /// 20ms 缓冲，与采集侧同量级。共享模式下的实用下限。
    ///
    /// 单位由 `timeline` 导出，理由同采集侧：REFERENCE_TIME 就是 100ns tick。
    const BUFFER_DURATION_100NS: i64 = 20 * crate::timeline::TICKS_PER_MS;

    /// 线程句柄与停止事件。收进 `Mutex` 是为了让 `start` / `stop` 只需 `&self`。
    ///
    /// FFI 层拿到的是同一个裸指针，若 `start` / `stop` 需要 `&mut`，而 `push` 在
    /// 网络线程并发调用，就会同时存在两个指向同一对象的引用——那在 Rust 里是 UB，
    /// 且靠「文档要求调用方串行」是保不住的。全部改成 `&self` 后这个面在构造上消失。
    struct RenderThread {
        worker: Option<JoinHandle<()>>,
        stop_event: Option<Arc<StopEvent>>,
    }

    pub struct WasapiRenderer {
        callback: PlayedFrameCallback,
        user_data: usize,
        /// 与渲染线程共享。`push` 来自 FFI 调用方的任意线程，`read_into` 在渲染线程，
        /// 两者都要 `&mut`，故必须互斥。
        ring: Arc<Mutex<PlaybackRing>>,
        running: Arc<AtomicBool>,
        /// 与渲染线程共享。只有渲染线程写、FFI 只读，故不需要锁——
        /// 尤其不能去抢上面那把 ring 的互斥量，对面等它的是 WASAPI 实时线程。
        stats: Arc<RenderStatsCell>,
        /// 与渲染线程共享。渲染线程每轮读一次，外环与 start 写它。
        target: Arc<TargetDepthCell>,
        /// 与渲染线程共享。托管侧在播放中写，渲染线程每轮读。
        alignment: Arc<AlignmentCell>,
        thread: Mutex<RenderThread>,
    }

    impl WasapiRenderer {
        pub fn new(callback: PlayedFrameCallback, user_data: usize) -> Self {
            Self {
                callback,
                user_data,
                ring: Arc::new(Mutex::new(PlaybackRing::new(RING_CAPACITY_FRAMES))),
                running: Arc::new(AtomicBool::new(false)),
                stats: Arc::new(RenderStatsCell::default()),
                target: Arc::new(TargetDepthCell::new(MIN_TARGET_MS)),
                alignment: Arc::new(AlignmentCell::default()),
                thread: Mutex::new(RenderThread {
                    worker: None,
                    stop_event: None,
                }),
            }
        }

        /// 读运行时统计。未起播的句柄返回全零，`device_sample_rate` 为 0 即未起播。
        pub fn stats(&self) -> crate::RenderStats {
            self.stats.snapshot()
        }

        /// 播放中改目标深度。渲染循环下一轮即读到，越界值被夹到支持区间内。
        ///
        /// 本期不导出到 FFI：新增导出是 ABI 变更，而 ABI 只在一处升一次。
        /// 这里先把内部表示改成可写的。
        ///
        /// 运行时写者现在有两个，都在渲染线程内、彼此无竞争：外环每轮那次读-改-写，
        /// 以及声明预算执行器那一笔。后者的读取在本轮开头、写入在阶跃算完之后，
        /// 中间隔着大半个循环体，连外环自己那次写都落在当中，故它吃得尤其宽。
        /// 一旦本方法接上 FFI 成为第三个写者，这两条读-改-写都会静默吃掉外部设置。
        ///
        /// 届时两处都要改，改法不同。外环那条不是换一个原子读-改-写就能了事：替它的写法
        /// 须同时满足三条约束。一、[`OuterLoop::step`] 带跨轮残差，进位一次扣一次，故不能
        /// 落在会被重跑的闭包里——`fetch_update` 的闭包按标准库契约可以被跑多次，单次执行
        /// 不在契约里；而重跑最常发生在有并发写时，也就是引入它要对付的那件事上。
        /// 二、那枚原子只有 [`TargetDepthCell::set_ms`] 一个写入入口、夹紧就长在那里，
        /// 而字段在本模块拿得到(私有字段对后代模块可见)、编译器不挡，故绕开 `set_ms` 的
        /// 写入必须自带夹紧，否则越界值存得进去。
        /// 三、[`OuterLoop::step`] 交的是绝对目标值而非增量，故「把增量原子地并进当前值」
        /// 这个形状先得自己算出那个增量。
        ///
        /// 执行器那条另有一条：它必须能分辨读到的新值是「外部设置」还是「外环本轮那笔
        /// 增量」——前者要并进来，后者按不变量 I 仍须丢(理由见执行器写 target 那处)。
        /// 直白的 `fetch_update(|cur| cur + applied)` 拿到的可能正是外环刚写完的值，
        /// 而只要可能就够——那正是那处判为错的写法。(只是可能:外环那次写另要过
        /// 重采样器在场与上一轮 QPC 在手,而执行器点火只过 offset 可用这一闸。)
        pub fn set_target_ms(&self, raw_ms: u32) {
            self.target.set_ms(raw_ms);
        }

        /// 下发运行时对齐参数。渲染循环下一轮即读到。
        /// 对齐开关不在这里——它是起播参数，走 [`WasapiRenderer::start`]。
        pub fn set_alignment(&self, d_ticks: i64, offset_ticks: i64, manual_offset_ticks: i64) {
            self.alignment
                .set_runtime(d_ticks, offset_ticks, manual_offset_ticks);
        }

        /// 起渲染线程，同步等它汇报启动结果再返回。
        ///
        /// 必须等：设备被独占、混音格式不受支持这些失败只有线程里知道，不等就只能靠
        /// 「声音没出来」感知，而那与「上游没在发」无从区分——两者的排查方向相反。
        pub fn start(&self, target_ms: u32, alignment_enabled: bool) -> Result<(), AudioError> {
            // 持锁贯穿整个启动：并发两次 start 时，后者应当看到前者已把线程装好，
            // 从而走 ALREADY_RUNNING，而不是各起一个线程抢同一个设备。
            let Ok(mut thread) = self.thread.lock() else {
                return Err(AudioError {
                    message: "播放状态锁已中毒".to_string(),
                    status: STATUS_PANIC,
                });
            };

            if self.running.load(Ordering::SeqCst) {
                return Err(AudioError {
                    message: "播放已在运行".to_string(),
                    status: STATUS_ALREADY_RUNNING,
                });
            }

            // 夹紧在 cell 的写入侧，这里只管把起播值交给它。
            self.target.set_ms(target_ms);
            // 起播参数先落 cell 再拉线程：渲染线程首轮读到的就是本次起播的开关值，
            // 「起播前设好」由此变成参数表的形状，不再是调用方要记住的时序。
            self.alignment.set_enabled(alignment_enabled);

            let stop_handle = unsafe { CreateEventW(None, true, false, None) }
                .map_err(|err| AudioError::device(format!("创建停止事件失败：{err}")))?;
            let stop_event = Arc::new(StopEvent(stop_handle));

            // 换目标深度等于换一条流：残留的旧音频按新深度解释会先响一下上次的尾巴。
            if let Ok(mut ring) = self.ring.lock() {
                ring.reset();
            }

            // 与 ring.reset() 同处，理由相同：上一次会话的计数混进来，会让
            // 「本次播放共欠载几次」变成历史累计，静默失真。停播时不清，
            // 那些计数是停播后唯一还能读到的诊断信息。
            self.stats.reset();

            let (ready_tx, ready_rx) = mpsc::channel::<Result<(), AudioError>>();

            let callback = self.callback;
            let user_data = self.user_data;
            let ring = Arc::clone(&self.ring);
            let running = Arc::clone(&self.running);
            let stats = Arc::clone(&self.stats);
            let target = Arc::clone(&self.target);
            let alignment = Arc::clone(&self.alignment);
            let thread_stop = Arc::clone(&stop_event);

            self.running.store(true, Ordering::SeqCst);

            let worker = std::thread::Builder::new()
                .name("medialink-audio-render".to_string())
                .spawn(move || {
                    let context = LoopContext {
                        callback,
                        user_data,
                        target,
                        alignment,
                    };
                    // 守卫的理由与写法见 crate::run_guarded：panic 时 unwind 会跳过
                    // 尾部语句，而 running 卡在真会让此后的 start 一直报已在运行。
                    run_guarded(&running, || {
                        unsafe {
                            render_loop(&context, &ring, &running, &stats, thread_stop.0, &ready_tx)
                        };
                    });
                })
                .map_err(|err| {
                    self.running.store(false, Ordering::SeqCst);
                    AudioError::device(format!("创建渲染线程失败：{err}"))
                })?;

            // 阻塞等启动结果。线程无论成败都恰好发一次，故这里不会永久挂住；
            // 线程 panic 时发送端随之析构，recv 拿到 Err 而非死等。
            match ready_rx.recv() {
                Ok(Ok(())) => {
                    thread.worker = Some(worker);
                    thread.stop_event = Some(stop_event);
                    Ok(())
                }
                Ok(Err(err)) => {
                    self.running.store(false, Ordering::SeqCst);
                    // 线程已在退出路上，仍要 join：不 join 会让句柄与 COM 单元
                    // 在下一次 start 时与新线程重叠。
                    unsafe {
                        let _ = SetEvent(stop_event.0);
                    }
                    let _ = worker.join();
                    Err(err)
                }
                Err(_) => {
                    self.running.store(false, Ordering::SeqCst);
                    let _ = worker.join();
                    Err(AudioError {
                        message: "渲染线程启动时异常退出".to_string(),
                        status: STATUS_PANIC,
                    })
                }
            }
        }

        /// 送入交错 i16。
        ///
        /// 帧对齐由调用方保证：参数是帧数而非样本数，半帧无从表达，
        /// 而 [`PlaybackRing::push`] 不跨调用结转残样本——若调用方按字节数算帧数
        /// 且没向下取整到整帧，错位会一路传下去。
        /// `sender_ticks` 为 0 即「本帧没有时刻」，走不带时间轴的原路径。
        /// 追加一包样本。走不走时间轴由对齐开关与本帧有没有时刻共同决定。
        ///
        /// 两个条件必须同时成立才建时间轴，而不是各自独立判断。此前只看 `sender_ticks`：
        /// 于是 native 侧有了两个互不知情的对齐开关——调用方只要送了非零时刻，即使用户
        /// 关着对齐，这一包也会走补静音与锚点那条路。「对齐关闭时热路径逐字走原路径」
        /// 这条约束当时只由托管侧的自觉维持，而约束点名的是那个性质本身。
        ///
        /// `sender_ticks` 为 0 表示本帧没有时刻。这条编码约定对调用方有一个硬要求：
        /// 送进来的必须是自 1970 起算的 100ns 计次，因为 0 在那个时基下不是一个会出现的
        /// 值。任何以起播为原点的相对时基都不能直接送进来——那种时基的第一帧恰好是 0，
        /// 而它会被读成「没有时刻」。
        pub fn push(&self, interleaved: &[i16], sender_ticks: i64) {
            let timed = super::should_use_timeline(sender_ticks, self.alignment.is_enabled());
            // 渲染线程持锁的时间是一次 memcpy。拿不到锁只能是持锁者 panic 了，
            // 此时丢这一包而非把 panic 传进网络线程。
            let Ok(mut ring) = self.ring.lock() else {
                return;
            };
            let kind = if timed {
                ring.push_at(interleaved, sender_ticks)
            } else {
                ring.push(interleaved);
                GapKind::Normal
            };
            drop(ring);

            // 计数在锁外自增：stats 是各自独立的原子，不为诊断多持一拍 ring 锁——
            // 对面等那把锁的是 WASAPI 实时线程。关闭路径恒为 Normal，一次比较即返回，
            // 热路径逐字如旧。
            match kind {
                GapKind::Normal => {}
                GapKind::SwallowedGap => self.stats.note_swallowed_gap(),
                GapKind::Overlap => self.stats.note_overlap(),
            }
        }

        /// 停止并同步等待渲染线程退出。
        ///
        /// 必须等：线程仍持有 C# 传来的回调指针与 user_data，提前返回会让托管侧
        /// 释放 GCHandle 后线程还在往里写，那是进程级崩溃而非可恢复的托管异常。
        pub fn stop(&self) {
            let Ok(mut thread) = self.thread.lock() else {
                // 锁中毒说明持锁者 panic 过。此时线程状态不可知，能做的只有
                // 置停止位——不能 join 一个拿不到句柄的线程。
                self.running.store(false, Ordering::SeqCst);
                return;
            };

            if let Some(event) = &thread.stop_event {
                unsafe {
                    let _ = SetEvent(event.0);
                }
            }

            self.running.store(false, Ordering::SeqCst);

            if let Some(worker) = thread.worker.take() {
                let _ = worker.join();
            }

            thread.stop_event = None;
        }
    }

    impl Drop for WasapiRenderer {
        fn drop(&mut self) {
            self.stop();
        }
    }

    /// 线程入口参数。聚成一个结构而非七个形参——`clippy::too_many_arguments` 之外，
    /// 这三项是「一次播放会话的身份」，散着传容易在改动时错位。
    struct LoopContext {
        callback: PlayedFrameCallback,
        user_data: usize,
        target: Arc<TargetDepthCell>,
        alignment: Arc<AlignmentCell>,
    }

    /// 渲染主循环。COM 在本线程初始化并在退出前反初始化——COM 单元是线程局部的。
    ///
    /// 启动结果经 `ready` 恰好发一次；此后的失败无人接收，与采集侧同构：
    /// 托管侧据「声音停了」感知，native 侧没有可上报的地方。
    ///
    /// 会话的建立与循环收在 `render_session_loop` 里，这是为了让所有 COM 对象在
    /// `CoUninitialize` 之前析构。把会话开在本函数里会让 `IAudioClient` 的 Release
    /// 跑在已退出的单元里——`capture.rs` 因为把会话开在 `capture_loop_inner` 内而
    /// 天然正确，那一半结构不能丢。
    unsafe fn render_loop(
        context: &LoopContext,
        ring: &Mutex<PlaybackRing>,
        running: &AtomicBool,
        stats: &RenderStatsCell,
        stop_event: HANDLE,
        ready: &mpsc::Sender<Result<(), AudioError>>,
    ) {
        if let Err(err) = CoInitializeEx(None, COINIT_MULTITHREADED).ok() {
            let _ = ready.send(Err(AudioError::device(format!(
                "CoInitializeEx 失败：{err}"
            ))));
            return;
        }

        render_session_loop(context, ring, running, stats, stop_event, ready);

        CoUninitialize();
    }

    unsafe fn render_session_loop(
        context: &LoopContext,
        ring: &Mutex<PlaybackRing>,
        running: &AtomicBool,
        stats: &RenderStatsCell,
        stop_event: HANDLE,
        ready: &mpsc::Sender<Result<(), AudioError>>,
    ) {
        let session = match open_render_session() {
            Ok(session) => {
                let _ = ready.send(Ok(()));
                session
            }
            Err(err) => {
                let _ = ready.send(Err(err));
                return;
            }
        };

        render_frames(context, ring, running, stats, stop_event, &session);
    }

    unsafe fn render_frames(
        context: &LoopContext,
        ring: &Mutex<PlaybackRing>,
        running: &AtomicBool,
        stats: &RenderStatsCell,
        stop_event: HANDLE,
        session: &RenderSession,
    ) {
        // 设备率是判「重采样到底走没走」的唯一依据，且必须在第一轮渲染之前就可读：
        // 大于 48k 的设备曾在首轮渲染即 panic，而那时 render_start 已经返回过 OK。
        stats.set_device_rate(session.mix.sample_rate);
        // 会话常量，起播时写一次即可。0 的含义是「没有估计」，不是「零延迟」。
        stats.set_device_latency_us(session.device_latency_us);
        // 同样是会话常量：缓冲容量由设备定，托管侧要它来算本机最小可达延迟。
        stats.set_device_buffer_frames(session.buffer_frames);
        // native 独占的一条事实。取不到时钟即位置锚点根本不产生，对齐无从进行，
        // 而托管侧只看位置的话，那与「刚起播还没转起来」不可区分。
        stats.set_device_clock_available(session.clock.is_some());

        // prefill 用起播那一刻的目标深度，此后不跟着外环变。这不是漏改一处：
        // prefill 是一次性粗调，外环是持续微调，两者若在同一时间尺度上互相追，
        // 正是双环分层要避免的形态。
        let mut prefill = PrefillState::new(target_frames(context.target.current_ms()));
        // 建不建在起播这一刻定下。读用户设置而非 offset 可用性，理由见 AlignmentCell。
        // 外环与它的上一轮时刻。dt 取设备位置的 QPC 增量：那与误差信号同源，
        // 另读一个时钟会引入两个时基之间的偏差。
        let mut outer = OuterLoop::default();
        let mut last_qpc: Option<u64> = None;
        // 重同步门与外环同寿命:积压是跨轮累计出来的病灶,门的记忆也要跨轮。
        let mut gate = ResyncGate::default();
        // 声明预算执行器的两枚会话内状态,与门同处声明,起停播天然重建。
        // 两者的记忆同样跨轮,而在新会话里都无所指:锚记的是上一次观测到的 D,
        // 余额记的是尚未摊完的读游标位移,而新会话的缓冲是空的、深度从预填充重新起。
        let mut setpoint = SetpointStep::default();
        let mut shift = PendingShift::default();
        let device_latency_ticks = i64::try_from(session.device_latency_us * 10).unwrap_or(0);

        let mut resampler = build_resampler(
            context.alignment.is_enabled(),
            session.mix.sample_rate,
            session.buffer_frames,
        );

        // 三块缓冲一次分配到位。渲染是实时线程，循环内分配会引入不可预测的停顿，
        // 而停顿就是可听的 glitch；`ring` 的三个方法也是全程无分配的。
        //
        // 上界取 rubato 自己的 `input_frames_max`——那正是它为此用途提供的契约值。
        // 曾自己按 `update_needed_len` 推过一个更大的界，理由是「`input_frames_max`
        // 少加了半个 sinc_len 故会低估」，那个理由是错的：`last_index` 稳态收敛到
        // ≈ −sinc_len，正好抵掉式子里加的整个 sinc_len，于是稳态 `needed` 就是
        // `ceil(chunk/ratio)`，峰值只出现在构造与 reset 那一轮。
        let max_input = resampler
            .as_ref()
            .map_or(session.buffer_frames as usize, |state| {
                state.inner.input_frames_max()
            });
        let channels = OUTPUT_CHANNELS as usize;
        let mut staging = vec![0i16; max_input * channels];
        let mut planar = vec![vec![0f32; max_input], vec![0f32; max_input]];
        let mut device_planar = vec![
            vec![0f32; session.buffer_frames as usize],
            vec![0f32; session.buffer_frames as usize],
        ];

        while running.load(Ordering::SeqCst) {
            let handles = [session.buffer_event, stop_event];
            if wait_for_any(&handles, 200) == WaitObject::Stop {
                break;
            }

            let Ok(padding) = session.client.GetCurrentPadding() else {
                break;
            };
            let writable = session.buffer_frames.saturating_sub(padding);
            if writable == 0 {
                continue;
            }

            // 设备位置紧贴 padding 取：两者是误差的两个端点，中间隔一次与网络线程
            // 争用的 ring 锁会引入正偏的时差（锁等待不会为负），积分器平均不掉。
            // 只在对齐开着时取——关闭时这一段此前不存在，「关闭走原路径」说的就是它；
            // 代价是关闭时 device_position 两个诊断字段保持 0，与 clock 缺失同形。
            let mut position_qpc = None;
            if context.alignment.is_enabled() {
                if let Some(clock) = session.clock.as_ref() {
                    let mut position = 0u64;
                    let mut qpc_100ns = 0u64;
                    if clock
                        .GetPosition(&mut position, Some(&mut qpc_100ns))
                        .is_ok()
                    {
                        stats.set_device_position(
                            output_frames_at_device_position(position, session.clock_frequency),
                            qpc_100ns,
                        );
                        position_qpc = Some(qpc_100ns);
                    }
                }
            }

            // 端点里还压着 padding 帧，故正在出声的那个采样在累积轴上落在读游标
            // 之前 padding 那么多——折算与减法抽在 playing_position_frames 里。
            // 一次持锁取全，不为诊断多抢一次那把锁——对面等它的是 WASAPI 实时线程。
            let Ok((available, playing_sender_ticks)) = ring.lock().map(|ring| {
                let playing_at = playing_position_frames(
                    ring.read_cursor_frames(),
                    DeviceFrames::new(padding as usize),
                    session.mix.sample_rate,
                );
                (ring.available_frames(), ring.sender_ticks_at(playing_at))
            }) else {
                break;
            };

            stats.set_ring_frames(available);

            if let Some(output_frames) = prefill_silence_frames(
                &mut prefill,
                available,
                DeviceFrames::new(writable as usize),
                session.mix.sample_rate,
                max_input,
            ) {
                // 预填充期送等长零值帧而非跳过回调：跳过会让频谱冻结在上一帧波形上，
                // 而硬重置后的预填充可达 1 秒。同 `capture.rs` 的第三处刻意偏离。
                let silent = output_frames * channels;
                staging[..silent].fill(0);
                if write_silence(&session.render_client, writable).is_err() {
                    break;
                }
                // 预填充也要计入已渲染帧：漏掉它会让「已渲染时长对墙钟」这条判据
                // 在每次硬重置后凭空少掉一段（预填充可达一秒），而那会被误读成设备丢帧。
                stats.add_rendered(writable as usize);
                emit_played(context, &staging[..silent]);
                continue;
            }

            // 每轮重读目标深度。读成循环外的一次快照，外环算出多少都没有载体可写——
            // 那正是本处此前的形态。
            let target_ms = context.target.current_ms();
            // 循环实际在用的那个值。与起播请求值分开上报，否则「每轮重读」这条性质
            // 从外部不可观测——把上面那行挪回循环外，没有任何判据会变红。
            stats.set_target_ms_current(target_ms);
            stats.set_clock_offset_available(context.alignment.offset_available());

            // 外环走一步。offset 不可用时整段跳过——停步而不是喂 0：喂 0 落在死区里
            // 看着像「没误差」，而它会让上一轮攒下的残差继续被当成有效积分。
            // 没有执行器（48k 端点起播时对齐没开、或建重采样器失败）时同样不走：
            // target_ms 那时对出声时刻毫无作用，走步就是纯积分器一路 windup 到边界，
            // 而 target_ms_current 贴在边界会指向一个不存在的结论。
            // 本轮误差另拿一份给重同步门:门要的是「对齐活跃且本轮 error 有效」这一
            // 完整事实,None 即本轮走了下面的停摆臂。
            let mut aligned_error_ticks: Option<i64> = None;
            match (
                context.alignment.offset_available() && resampler.is_some(),
                position_qpc,
                playing_sender_ticks,
            ) {
                (true, Some(qpc_now), Some(sender_ticks)) => {
                    let actual = actual_play_ticks(
                        i64::try_from(qpc_now).unwrap_or(i64::MAX),
                        device_latency_ticks,
                        context.alignment.manual_offset_ticks(),
                    );
                    let goal = target_play_ticks(
                        sender_ticks,
                        context.alignment.d_ticks(),
                        context.alignment.offset_ticks(),
                    );
                    let error_ticks = play_time_error_ticks(actual, goal);
                    stats.set_play_time_error_us(error_ticks / 10);
                    aligned_error_ticks = Some(error_ticks);

                    if let Some(previous) = last_qpc {
                        let dt =
                            (qpc_now.saturating_sub(previous)) as f64 / TICKS_PER_SECOND as f64;
                        let next =
                            outer.step(error_ticks as f64 / TICKS_PER_MS as f64, dt, target_ms);
                        context.target.set_ms(next);
                    }
                    last_qpc = Some(qpc_now);
                }
                _ => {
                    // 误差此刻无意义。写 0 而非留着上一次的值：留着的话，
                    // 对端失联后设置页仍显示一个像是当前的误差。读者据
                    // clock_offset_available 判断这个 0 是「没误差」还是「算不出」。
                    stats.set_play_time_error_us(0);
                    last_qpc = None;
                }
            }

            // 声明预算的执行器。D 变了就当场把目标深度推到新值,不经外环:外环按
            // 准静态残差整定(设备延迟估计残差、prefill 对齐残差那一类),时间常数
            // 是几百秒的量级,而用户拨滑块是阶跃。把阶跃塞进一条为准静态残差设计的
            // 通路是用错了通路——慢不是病根。
            //
            // 目标当场更新，未付位移留债；外环穿插写target时不能用单向望远镜
            // 推导常量债界。支付机会不足时不承诺墙钟期限。
            //
            // 闸在 offset 可用性上,理由住在 live_setpoint_step 一处:D 的有效域,
            // 不是防抖动。判定与位移计划都是纯的,理由与判据跟着那两个纯件走;
            // 整枚 cell 交进去而不是在这里读出条件,是为了让「闸绑到哪一项」本身可判。
            let applied = live_setpoint_step(&mut setpoint, &context.alignment, target_ms);
            let (next_target_ms, want_frames) = plan_setpoint_shift(applied, target_ms, &mut shift);

            if want_frames > 0 {
                let Ok(mut ring) = ring.lock() else { break };
                apply_read_cursor_shift(&mut ring, want_frames, next_target_ms);
            }

            if applied != 0 {
                // 同一轮里外环可能刚写过一次 target,这里覆盖掉它是刻意的:
                // next_target_ms 算自本轮开头那个外环之前的快照,故外环那一步的增量
                // 在此丢失。丢得对,而两种情形各有一条理由:外环那次读 d_ticks(误差臂)
                // 与执行器那次(live_setpoint_step 里)是对同一个原子的两次独立 Relaxed
                // load,两次读同值时外环的误差就是拿新 D 算的,那一步已是对同一次跳变的
                // 部分响应,留着就是同一个跳变算两遍、深度多走一截;托管侧 set_runtime 的
                // store 恰落在两次读之间时,外环用的是旧 D,那一步根本不是对这次跳变的
                // 响应,依据已经不在。反向不可达:同线程对同一位置的两次读受 coherence 约束。
                context.target.set_ms(next_target_ms);

                // 地基跳变了就清空全部承载收敛进度的累积器。外环的残差里此刻混着两样,
                // 两样都得走:跳变之前按旧目标攒下的那部分,依据已经不在了;本轮那一小步
                // 与上面 target 那一笔同理,两种情形一条是重复计入、一条是依据已不在,
                // 哪一条都不该留。取斜率用的上一次 QPC 一并清,它是残差的时间基准。
                //
                // 与门点火块那个「真剪了才清」的条件不同,不要照抄过去:那里钳成 0 意味着
                // 读游标一帧都没动、误差信号的地基没跳;而这里即使尚未发生真实位移,
                // target 与 D 也已经变了,地基照样跳了整整一个 applied。
                //
                // 门的持续期也显式归零:「超额已持续多久」是相对旧地基说的。这一处是显式
                // 清,不靠 note 自清——note 自己那两处清是点火那一轮、以及阈下的每一轮
                // (赋值幂等,可观测的转移只在回落那一轮),两处都是门自身的设计(前者限速、
                // 后者「回落即重新起算」),没有一处是为地基跳变而设的,故不能指望它替这里清。
                outer.reset();
                gate = ResyncGate::default();
                last_qpc = None;
                // 重采样器刻意不重置,这一点与硬重置相反:流未断,读游标只是在连续流上
                // 跳了一段或停了一段,历史样本仍属同一条流。硬重置那边是样本整体作废,
                // 留着历史才会把旧尾巴混进新流。
            }

            // 本次要吃多少输入：重采样时由 rubato 说，比率与输出块都会改变它，
            // 故两者都要先设好再问。
            let needed = match resampler.as_mut() {
                Some(state) => {
                    let ratio = resample_ratio(
                        session.mix.sample_rate,
                        frames_to_ms(available),
                        target_ms as f64,
                    );
                    // 输出块每轮跟着 writable 变：SincFixedOut 的块是固定的，
                    // 不设就会要求写满整个缓冲，而 WASAPI 只让写 writable 帧。
                    if state.inner.set_chunk_size(writable as usize).is_err() {
                        break;
                    }
                    let _ = state.inner.set_resample_ratio(ratio, true);
                    stats.set_ratio_ppm(ratio_to_ppm(ratio));
                    state.inner.input_frames_next()
                }
                None => {
                    // 不重采样等于比率恰好 1，显式写下而不是留 0——0 的含义是
                    // 「不可用」，让 48k 设备读到 0 会与「还没算出来」混淆。
                    stats.set_ratio_ppm(1_000_000);
                    writable as usize
                }
            };

            debug_assert!(
                needed <= max_input,
                "输入帧上界算小了：需要 {needed}，预分配 {max_input}"
            );
            if needed > max_input {
                // 上界算错时宁可丢一轮也不在实时线程上分配。
                continue;
            }

            // 垫零消费路径。本轮该垫的那部分直接写零、不从缓冲读:不消费即读游标暂停,
            // 而生产者继续写,占用因此上涨——这正是上调声明预算要的效果。填零逻辑不必
            // 新写一份,`read_into` 取不足时本就填零(重复上一帧会产生蜂鸣);垫零要的
            // 只是「不消费」这一件事。
            let Some(read) = consume_shifted(ring, &mut shift, &mut staging[..needed * channels])
            else {
                break;
            };
            let want = read.wanted;
            let taken = read.taken;

            // 欠载判定只对真去读的那部分生效。垫零帧既不计 stats 的欠载数,也不计
            // prefill 的连续欠载:它是用户刻意调设定值的结果,不是数据不够。计进去会让
            // 整轮全垫的那几轮凑够连续欠载阈值而触发硬重置,那会把 ring 全清、重新
            // 预填充,恰好毁掉执行器刚做到位的事——而硬重置计数还是真机判据用来判别
            // 工况域的锚,污染了它,判据就分不清「设备真缺了数据」与「用户拨了滑块」。
            // want == 0 即整轮全垫,走 note_progress 那一臂:那一轮一帧都没缺。
            if want > 0 && taken < want {
                stats.note_underrun();
                if prefill.note_underrun(frames_to_ms(want - taken)) {
                    stats.note_hard_reset();
                    // 时间轴随 ring 一起作废，误差信号从头来。残差留着会让重置后的
                    // 第一次调整凭空多走一步。
                    outer.reset();
                    last_qpc = None;
                    // 凡使误差信号地基跳变的动作,必须清空全部依赖该信号的累积器。
                    // ring 整体作废使「超额已持续多久」一并失去意义:漏清它,重置后的
                    // 第一轮就带着上一段攒下的持续期,一超阈立刻点火再丢一段最旧。
                    gate = ResyncGate::default();
                    // 位移余额同理作废:它记的是「把现有占用推到某处」,而现有占用已经
                    // 不在了。留着会让重新预填充之后凭空垫上一段静音,而那一段静音在
                    // 用户听来与欠载不可区分。
                    //
                    // setpoint 的锚刻意不动:硬重置这件事本身不改变 D,锚照样有效。
                    // 同一轮里执行器也可能刚点过火,那不影响这条:那一笔的锚已经在点火
                    // 时按它自己的规则前进过了。重置锚会让下一轮把「当前 D 与陈旧锚之
                    // 差」看作一次新阶跃执行下去,那是把用户从没拨过的滑块替他拨了一次。
                    shift.clear();
                    // prefill 重建到本轮开头读到的那个目标深度，而不是沿用起播时冻结的目标：
                    // 外环已把 target_ms 挪走时，填回旧深度会让内环随即用 τ内（默认
                    // 300 秒）去追差额，两环在同一件事上做功。起播那条「prefill 不随
                    // 外环变」说的是不要每轮重读，重置时重读一次不违反它。
                    //
                    // 一处已登记的过期:target_ms 是本轮开头取的快照,而在它之后写过
                    // cell 的有两个,不止一个。外环那次写(误差臂里那一步)幅度小但常有;
                    // 执行器点火那次是整笔阶跃,可达数百毫秒,但要硬重置与阶跃撞在同一轮
                    // 才可达,极罕见。两者都会让这里按一个已经不是当前值的深度重建
                    // prefill。共同点是它不像重采样比率那处一轮自愈——prefill 的目标深度
                    // 是构造时定死的一个数,要等下一次硬重置才会重取。本期登记不修:
                    // 改它要动这条重置路径的取值来源,而本笔只做接线。
                    prefill = PrefillState::new(target_frames(target_ms));
                    let Ok(mut ring) = ring.lock() else { break };
                    ring.reset();
                    if let Some(state) = resampler.as_mut() {
                        // 重采样器内部还留着重置前的历史样本，不清会把旧尾巴
                        // 混进新流的第一块里。
                        state.inner.reset();
                    }
                    continue;
                }
            } else {
                prefill.note_progress();
            }

            // 重同步门:持续超额即丢最旧,补「积压不欠载、硬重置永不触发」的自愈缺口。
            // 超额分两臂取——对齐活跃时用本轮外环误差(它就是「实际比目标晚多少」,
            // 只有正值算超额:负 error 是放早,不是本门的事,i64 除法向零截断天然满足);
            // 对齐不活跃时退回缓冲深度对目标的超出。prefill 轮次在上面 continue,
            // 天然不进门。
            let Some(dropped) = apply_resync_gate(
                &mut gate,
                &mut shift,
                ring,
                ResyncObservation {
                    applied,
                    aligned_error_ticks,
                    available_frames: available,
                    observation_target_ms: target_ms,
                    current_target_ms: context.target.current_ms(),
                    needed,
                },
            ) else {
                break;
            };
            {
                // 真剪了才清；零位移时仍需保留外环的亚毫秒残差。
                if dropped > 0 {
                    // 与硬重置共享的两步:读游标跳段之后误差的历史随之失效,残差留着
                    // 会让下一步凭空多走;last_qpc 清空让外环下轮重新取基准。
                    outer.reset();
                    last_qpc = None;
                    // 门自身的持续期不在此清:note 点火时已自清,再清一遍是同一件事
                    // 写两处。重采样器也刻意不重置:流未断,丢最旧只是读游标在连续流上
                    // 跳了一段,历史样本仍属同一条流——与硬重置场景相反,那边样本整体
                    // 作废,留着历史才会把旧尾巴混进新流。
                }
            }

            let device_frames = match resampler.as_mut() {
                Some(state) => {
                    match resample_into(
                        state,
                        &staging[..needed * channels],
                        &mut planar,
                        &mut device_planar,
                    ) {
                        Ok(frames) => frames,
                        Err(_) => break,
                    }
                }
                None => needed,
            };

            let write_frames = device_frames.min(writable as usize);
            let written = write_device_buffer(
                &session.render_client,
                &session.mix,
                &staging[..needed * channels],
                &device_planar,
                resampler.is_some(),
                write_frames,
            );
            if written.is_err() {
                break;
            }

            stats.add_rendered(write_frames);

            // 送出的是重采样前的 48k i16：C# 侧的分析器只认这一种格式。
            emit_played(context, &staging[..needed * channels]);
        }

        let _ = session.client.Stop();
    }

    fn emit_played(context: &LoopContext, played: &[i16]) {
        if played.is_empty() {
            return;
        }

        let frame = PlayedFrame::new(played);
        (context.callback)(&frame, context.user_data as *mut c_void);
    }

    struct RenderSession {
        client: IAudioClient,
        render_client: IAudioRenderClient,
        /// 设备位置与取位置时的 QPC 时刻。取不到时为 None——某些端点不给这个服务，
        /// 那时对齐用不了，但播放本身照旧。
        clock: Option<IAudioClock>,
        /// [`IAudioClock::GetFrequency`] 的返回值，位置归一的除数。
        clock_frequency: u64,
        /// 设备取走数据之后到出声那段固定尾段的估计，微秒。会话常量。
        device_latency_us: u64,
        mix: MixFormat,
        buffer_frames: u32,
        buffer_event: HANDLE,
        /// 只为在会话析构时关闭事件句柄，不被读。
        _buffer_event_guard: StopEvent,
    }

    unsafe fn open_render_session() -> Result<RenderSession, AudioError> {
        let enumerator: IMMDeviceEnumerator =
            CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)
                .map_err(|err| AudioError::device(format!("创建设备枚举器失败：{err}")))?;

        let device = enumerator
            .GetDefaultAudioEndpoint(eRender, eConsole)
            .map_err(|err| AudioError::device(format!("获取默认输出设备失败：{err}")))?;

        let client: IAudioClient = device
            .Activate(CLSCTX_ALL, None)
            .map_err(|err| AudioError::device(format!("激活音频客户端失败：{err}")))?;

        let mix_format_ptr = client
            .GetMixFormat()
            .map_err(|err| AudioError::device(format!("获取混音格式失败：{err}")))?;
        let mix = parse_mix_format(mix_format_ptr).map_err(|err| AudioError {
            message: err.message,
            status: err.status,
        })?;

        // 不带 LOOPBACK：这是真正的播放流。其余参数与采集侧一致。
        let init_result = client.Initialize(
            AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
            BUFFER_DURATION_100NS,
            0,
            mix_format_ptr,
            None,
        );
        CoTaskMemFree(Some(mix_format_ptr as *const c_void));
        init_result.map_err(|err| AudioError::device(format!("初始化音频客户端失败：{err}")))?;

        let buffer_event = CreateEventW(None, false, false, None)
            .map_err(|err| AudioError::device(format!("创建缓冲事件失败：{err}")))?;
        let guard = StopEvent(buffer_event);

        client
            .SetEventHandle(buffer_event)
            .map_err(|err| AudioError::device(format!("设置事件句柄失败：{err}")))?;

        let render_client: IAudioRenderClient = client
            .GetService()
            .map_err(|err| AudioError::device(format!("获取播放客户端失败：{err}")))?;

        let buffer_frames = client
            .GetBufferSize()
            .map_err(|err| AudioError::device(format!("获取缓冲大小失败：{err}")))?;

        if buffer_frames == 0 {
            return Err(AudioError::device("设备报告的缓冲大小为零"));
        }

        // Start 必须在这里，不能挪到循环里。启动结果由调用方同步等待，
        // 而 `Start` 是「设备被独占」这类失败真正暴露的地方；放到上报之后失败，
        // `render_start` 会返回 OK 且 `last_error` 为空，表现为无声且无错误——
        // 那恰好否掉了 `WasapiRenderer::start` 文档声称的性质。
        client
            .Start()
            .map_err(|err| AudioError::device(format!("启动播放失败：{err}")))?;

        // 时钟服务取不到不算失败：播放照旧，只是对齐没有位置锚点可用。
        let clock: Option<IAudioClock> = client.GetService().ok();
        let clock_frequency = clock
            .as_ref()
            .and_then(|clock| clock.GetFrequency().ok())
            .unwrap_or(0);

        // 引擎周期总有值，流延迟实测在共享模式下报 0，故前者是这段估计的主体。
        let mut engine_period = 0i64;
        let _ = client.GetDevicePeriod(Some(&mut engine_period), None);
        let stream_latency = client.GetStreamLatency().unwrap_or(0);
        let device_latency_us = device_latency_us(engine_period, stream_latency);

        Ok(RenderSession {
            client,
            render_client,
            clock,
            clock_frequency,
            device_latency_us,
            mix,
            buffer_frames,
            buffer_event,
            _buffer_event_guard: guard,
        })
    }

    /// 交错 i16 → 分声道 f32 → 重采样 → 分声道 f32。返回产出的设备帧数。
    fn resample_into(
        state: &mut ResamplerState,
        interleaved: &[i16],
        planar: &mut [Vec<f32>],
        out: &mut [Vec<f32>],
    ) -> Result<usize, AudioError> {
        let channels = OUTPUT_CHANNELS as usize;
        let frames = interleaved.len() / channels;
        for channel in planar.iter_mut() {
            channel.resize(frames, 0.0);
        }

        let (left, right) = planar.split_at_mut(1);
        for (frame, samples) in interleaved.chunks_exact(channels).enumerate() {
            left[0][frame] = samples[0] as f32 / i16::MAX as f32;
            right[0][frame] = samples[1] as f32 / i16::MAX as f32;
        }

        let wanted = state.inner.output_frames_next();
        for channel in out.iter_mut() {
            channel.resize(wanted, 0.0);
        }

        let (_, produced) = state
            .inner
            .process_into_buffer(planar, out, None)
            .map_err(|err| AudioError::device(format!("重采样失败：{err}")))?;

        Ok(produced)
    }

    /// 交回一块静音。用 `AUDCLNT_BUFFERFLAGS_SILENT` 让 WASAPI 自己填零，
    /// 省一次全量写——`GetBuffer` 交回的内存带着上一轮残留，不置这个位就等于重播。
    unsafe fn write_silence(
        render_client: &IAudioRenderClient,
        frames: u32,
    ) -> Result<(), AudioError> {
        if frames == 0 {
            return Ok(());
        }

        render_client
            .GetBuffer(frames)
            .map_err(|err| AudioError::device(format!("取播放缓冲失败：{err}")))?;

        render_client
            .ReleaseBuffer(frames, AUDCLNT_BUFFERFLAGS_SILENT.0 as u32)
            .map_err(|err| AudioError::device(format!("提交静音缓冲失败：{err}")))?;
        Ok(())
    }

    /// 把 PCM 按设备混音格式写进播放缓冲。
    unsafe fn write_device_buffer(
        render_client: &IAudioRenderClient,
        mix: &MixFormat,
        interleaved: &[i16],
        resampled: &[Vec<f32>],
        used_resampler: bool,
        frames: usize,
    ) -> Result<(), AudioError> {
        if frames == 0 || mix.block_align == 0 {
            return Ok(());
        }

        let ptr = render_client
            .GetBuffer(frames as u32)
            .map_err(|err| AudioError::device(format!("取播放缓冲失败：{err}")))?;
        if ptr.is_null() {
            return Err(AudioError::device("播放缓冲为空指针"));
        }

        let bytes = std::slice::from_raw_parts_mut(ptr, frames * mix.block_align);
        let channels = OUTPUT_CHANNELS as usize;
        let stride = mix.format.bytes_per_sample();

        for frame in 0..frames {
            // 先定出本帧的两路源样本，再谈它们怎么映到设备声道。
            let stereo = if used_resampler {
                [
                    resampled[0].get(frame).copied().unwrap_or(0.0),
                    resampled[1].get(frame).copied().unwrap_or(0.0),
                ]
            } else {
                let base = frame * channels;
                [
                    interleaved.get(base).copied().unwrap_or(0) as f32 / i16::MAX as f32,
                    interleaved.get(base + 1).copied().unwrap_or(0) as f32 / i16::MAX as f32,
                ]
            };

            // 按设备声道数迭代而非按 stereo 的两路：设备可以有 6 路，高声道必须显式
            // 写零，否则留着 GetBuffer 交回来的上一轮残留。内容只有前置左右，
            // 把左右复制进中置与环绕会让声场糊成一团。
            for channel in 0..mix.channels as usize {
                let sample = stereo.get(channel).copied().unwrap_or(0.0);
                let offset = frame * mix.block_align + channel * stride;
                convert::write_sample(&mut bytes[offset..offset + stride], sample, mix.format);
            }
        }

        render_client
            .ReleaseBuffer(frames as u32, 0)
            .map_err(|err| AudioError::device(format!("提交播放缓冲失败：{err}")))?;
        Ok(())
    }
    /// push 接线的判据：ring 透传的分类真的走到了 stats cell 的计数上。
    ///
    /// 不开设备：`new` 只建对象，`push` 只碰 ring 锁与原子计数，与 WASAPI 无涉，
    /// 故不必 ignore。分类判定本身的边界在 timeline 的判据里，这里只钉接线——
    /// 接线断了或两个计数接反了，timeline 全绿而计数恒 0 或互串。
    #[cfg(test)]
    mod push_wiring {
        use super::*;

        extern "C" fn noop(_: *const PlayedFrame, _: *mut c_void) {}

        /// 一包 960 帧即 20 毫秒。
        const PACKET_FRAMES: usize = 960;
        const PACKET_TICKS: i64 = 20 * TICKS_PER_MS;

        #[test]
        fn gap_classifications_reach_the_stats_counters() {
            let renderer = WasapiRenderer::new(noop, 0);
            renderer.alignment.set_enabled(true);
            let tone = [7i16; PACKET_FRAMES * 2];
            // 基准取非零：0 是「本帧没有时刻」的哨兵，第一包也不能落在它上。
            let base = 1_000_000_000i64;

            renderer.push(&tone, base);
            // 两类事件刻意取不等次数（亚下限 2 次、重叠 1 次）：各触发一次时，
            // 把 push 里 match 两臂对调后两计数仍各为 1，判据照绿——1/1 分不出互串。
            // 每包比锚点末端晚 3 毫秒：亚下限空档。
            let second = base + PACKET_TICKS + 3 * TICKS_PER_MS;
            renderer.push(&tone, second);
            let third = second + PACKET_TICKS + 3 * TICKS_PER_MS;
            renderer.push(&tone, third);
            // 相对新锚点末端倒走 3 毫秒：重叠。
            let fourth = third + PACKET_TICKS - 3 * TICKS_PER_MS;
            renderer.push(&tone, fourth);

            let stats = renderer.stats();
            assert_eq!(stats.swallowed_gap_count, 2);
            assert_eq!(stats.overlap_count, 1);
        }

        #[test]
        fn a_disabled_switch_keeps_the_counters_dark() {
            // 对齐关着时热路径逐字走原路径，分类根本不产生——计数若在这里动了，
            // 说明接线绕过了 should_use_timeline 那道门。
            let renderer = WasapiRenderer::new(noop, 0);
            let tone = [7i16; PACKET_FRAMES * 2];

            renderer.push(&tone, 1_000_000_000);
            // 同一时刻再来一包：开着对齐这是重叠形态。
            renderer.push(&tone, 1_000_000_000);

            let stats = renderer.stats();
            assert_eq!(stats.swallowed_gap_count, 0);
            assert_eq!(stats.overlap_count, 0);
        }
    }

    /// 三条 WASAPI 事实的真机实测。
    ///
    /// 默认 ignore 而不是靠环境变量早退：早退的测试会计进 passed，
    /// 于是「跑过」与「跳过」在计数上不可分。手动跑：
    /// `cargo test --manifest-path native/audio-ffi/Cargo.toml -- --ignored --nocapture`
    ///
    /// 它会占用默认输出端点约一秒并写入静音（不出声）。
    ///
    /// 三条都不凭文档写死：
    ///
    /// 1. `GetPosition` 的位置单位一律经 `GetFrequency` 归一。写死「除以 nBlockAlign」
    ///    在某些驱动上会错一个常数因子，而错的是常数因子恰好不容易被「误差看起来稳定」暴露。
    /// 2. `pu64QPCPosition` 是否为 100ns 单位。这里验的是单位（同一间隔上的增量），
    ///    零点是否与 `Stopwatch` 同源要在托管侧验——native 侧读不到裸 QPC，
    ///    而为此新开 `Win32_System_Performance` 特性会改动 Cargo.toml 的依赖段。
    /// 3. `GetStreamLatency` 在共享模式下的量级。它是误差预算里最大的一项。
    #[cfg(test)]
    mod device_clock_probe {
        use std::time::{Duration, Instant};

        use windows::Win32::Media::Audio::DEVICE_STATE_ACTIVE;

        use super::*;
        use crate::timeline::TICKS_PER_MS;

        /// 喂一段静音并返回实际经过的时间。
        ///
        /// 失败 panic 而不是 break：break 会让「没喂成」表现为「时间短」，而事实一
        /// 的比较在 10 毫秒窗口下测不出任何东西——负向条件恰好满足的假通过。
        unsafe fn feed_silence(session: &RenderSession, rounds: u32) -> Duration {
            let started = Instant::now();
            for _ in 0..rounds {
                std::thread::sleep(Duration::from_millis(10));
                let padding = session
                    .client
                    .GetCurrentPadding()
                    .expect("GetCurrentPadding 失败，窗口不可信");
                let writable = session.buffer_frames.saturating_sub(padding);
                write_silence(&session.render_client, writable)
                    .expect("write_silence 失败，窗口不可信");
            }
            started.elapsed()
        }

        /// 逐个输出端点看流延迟与引擎周期各报什么。
        ///
        /// 默认端点报 0 之后需要分清：0 是这个 API 的普遍行为，还是虚拟端点没有硬件链
        /// 因而无可报。两者对代码的结论相同（0 一律当无信息），但对误差预算的表述不同。
        ///
        /// 只读，不改系统默认端点。端点名不在这里取——`PKEY_Device_FriendlyName` 要开
        /// `Win32_Devices_FunctionDiscovery` 特性，而那会改动 `Cargo.toml` 的依赖段。
        /// 打出端点 ID，名字由注册表侧对照。
        #[test]
        #[ignore = "需要真实音频设备，会逐个初始化并短暂启动每个输出端点"]
        fn survey_every_render_endpoint() {
            unsafe {
                CoInitializeEx(None, COINIT_MULTITHREADED)
                    .ok()
                    .expect("CoInitializeEx");

                {
                    let enumerator: IMMDeviceEnumerator =
                        CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)
                            .expect("创建设备枚举器");
                    let collection = enumerator
                        .EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE)
                        .expect("枚举输出端点");
                    let count = collection.GetCount().expect("端点数");

                    println!("--- 活动输出端点共 {count} 个 ---");
                    let mut reported = 0u32;

                    for index in 0..count {
                        let device = collection.Item(index).expect("取端点");
                        let id_ptr = device.GetId().expect("取端点 ID");
                        let id = id_ptr.to_string().unwrap_or_default();
                        CoTaskMemFree(Some(id_ptr.0 as *const c_void));

                        let client: IAudioClient = match device.Activate(CLSCTX_ALL, None) {
                            Ok(client) => client,
                            Err(err) => {
                                println!("[{index}] {id}");
                                println!("     激活失败：{err}");
                                continue;
                            }
                        };

                        let Ok(mix_ptr) = client.GetMixFormat() else {
                            println!("[{index}] {id}");
                            println!("     取混音格式失败");
                            continue;
                        };
                        let mix = parse_mix_format(mix_ptr);
                        // 事件回调模式要先 SetEventHandle 才能 Start，这里不需要事件，故传 0。
                        let init = client.Initialize(
                            AUDCLNT_SHAREMODE_SHARED,
                            0,
                            BUFFER_DURATION_100NS,
                            0,
                            mix_ptr,
                            None,
                        );
                        CoTaskMemFree(Some(mix_ptr as *const c_void));
                        if let Err(err) = init {
                            println!("[{index}] {id}");
                            println!("     初始化失败：{err}");
                            continue;
                        }

                        let after_init = client.GetStreamLatency().unwrap_or(-1);
                        let mut default_period = 0i64;
                        let mut min_period = 0i64;
                        let periods = client
                            .GetDevicePeriod(Some(&mut default_period), Some(&mut min_period));
                        let buffer_frames = client.GetBufferSize().unwrap_or(0);

                        let after_start = match client.Start() {
                            Ok(()) => {
                                let value = client.GetStreamLatency().unwrap_or(-1);
                                let _ = client.Stop();
                                value
                            }
                            Err(_) => -1,
                        };

                        let rate = mix.as_ref().map(|m| m.sample_rate).unwrap_or(0);
                        let align = mix.as_ref().map(|m| m.block_align).unwrap_or(0);
                        println!("[{index}] {id}");
                        println!(
                            "     混音 {rate} Hz / block_align {align}，缓冲 {buffer_frames} 帧"
                        );
                        println!(
                            "     流延迟 init={} start={} (100ns)",
                            after_init, after_start
                        );
                        if periods.is_ok() {
                            println!(
                                "     引擎周期 默认 {:.3} ms / 最小 {:.3} ms",
                                default_period as f64 / f64::from(TICKS_PER_MS as i32),
                                min_period as f64 / f64::from(TICKS_PER_MS as i32)
                            );
                        }

                        if after_init > 0 || after_start > 0 {
                            reported += 1;
                        }
                    }

                    println!("--- 报出非零流延迟的端点：{reported} / {count} ---");
                    assert!(count > 0, "本机没有活动的输出端点，这条实测无从进行");
                }

                CoUninitialize();
            }
        }

        #[test]
        #[ignore = "需要真实输出设备，会占用默认输出端点约一秒并写入静音"]
        fn measure_device_clock_facts() {
            unsafe {
                CoInitializeEx(None, COINIT_MULTITHREADED)
                    .ok()
                    .expect("CoInitializeEx");

                {
                    let session = open_render_session().expect("打开播放会话");
                    let clock: IAudioClock = session.client.GetService().expect("取 IAudioClock");

                    let freq = clock.GetFrequency().expect("GetFrequency");
                    let latency_100ns =
                        session.client.GetStreamLatency().expect("GetStreamLatency");

                    // 等位置时钟真正起走再取基准，而不是垫固定几轮：流刚 Start 时端点
                    // 先填缓冲，位置有一段启动死区（本机实测可近 200 毫秒），把死区算进
                    // 窗口会把「启动延迟」误读成「归一错误」——比值判据在那种窗口下
                    // 红的是端点行为，不是归一方式。位置两秒不走则端点本身不可测。
                    let mut warmup_rounds = 0;
                    loop {
                        feed_silence(&session, 5);
                        let mut p = 0u64;
                        let mut q = 0u64;
                        clock
                            .GetPosition(&mut p, Some(&mut q))
                            .expect("GetPosition");
                        if p > 0 {
                            break;
                        }
                        warmup_rounds += 1;
                        assert!(warmup_rounds < 40, "位置时钟两秒不走，端点无从实测");
                    }

                    let mut pos1 = 0u64;
                    let mut qpc1 = 0u64;
                    clock
                        .GetPosition(&mut pos1, Some(&mut qpc1))
                        .expect("GetPosition");

                    let elapsed = feed_silence(&session, 50);

                    let mut pos2 = 0u64;
                    let mut qpc2 = 0u64;
                    clock
                        .GetPosition(&mut pos2, Some(&mut qpc2))
                        .expect("GetPosition");

                    // GetStreamLatency 若为 0，替代来源有两个：起播后再读一次
                    // （某些驱动只在流稳态后才给值），以及引擎周期本身。
                    let latency_after = session.client.GetStreamLatency().unwrap_or(-1);
                    let mut default_period = 0i64;
                    let mut min_period = 0i64;
                    session
                        .client
                        .GetDevicePeriod(Some(&mut default_period), Some(&mut min_period))
                        .expect("GetDevicePeriod");
                    println!("延迟二次读取      : {latency_after} (100ns)");
                    println!("GetDevicePeriod   : 默认 {default_period} = {:.3} ms，最小 {min_period} = {:.3} ms",
                        default_period as f64 / f64::from(TICKS_PER_MS as i32),
                        min_period as f64 / f64::from(TICKS_PER_MS as i32));
                    println!(
                        "GetBufferSize     : {} 帧 = {:.3} ms",
                        session.buffer_frames,
                        f64::from(session.buffer_frames) * 1000.0
                            / f64::from(session.mix.sample_rate)
                    );

                    let pos_delta = pos2 - pos1;
                    let qpc_delta = qpc2 - qpc1;
                    let elapsed_100ns = elapsed.as_nanos() as f64 / 100.0;
                    let seconds_by_clock = pos_delta as f64 / freq as f64;

                    println!("--- WASAPI 时钟实测 ---");
                    println!(
                        "混音格式        : {} Hz, block_align {}",
                        session.mix.sample_rate, session.mix.block_align
                    );
                    println!("GetFrequency    : {freq}");
                    println!(
                        "  与采样率之比  : {:.4}",
                        freq as f64 / f64::from(session.mix.sample_rate)
                    );
                    println!("位置增量        : {pos_delta}");
                    println!("  归一后秒数    : {seconds_by_clock:.4} s");
                    println!("墙钟经过        : {:.4} s", elapsed.as_secs_f64());
                    println!("QPC 增量        : {qpc_delta} (100ns 则应约 {elapsed_100ns:.0})");
                    println!("  与墙钟之比    : {:.4}", qpc_delta as f64 / elapsed_100ns);
                    println!(
                        "GetStreamLatency: {latency_100ns} (100ns) = {:.3} ms",
                        latency_100ns as f64 / f64::from(TICKS_PER_MS as i32)
                    );

                    // 事实一：频率可用于归一，归一后的秒数逼近墙钟。
                    // 窗口先立地基：50 轮 × 10ms 至少该有 400ms，且位置确实在走——
                    // 窗口塌缩或时钟冻结时，任何差值比较都在真空里成立。
                    assert!(freq > 0, "GetFrequency 返回 0，位置无从归一");
                    assert!(
                        elapsed >= Duration::from_millis(400),
                        "喂静音只维持了 {elapsed:?}，窗口不足以比较时钟"
                    );
                    assert!(pos_delta > 0, "位置时钟没有走动");
                    // 比值判据，与事实二同形：绝对差在窗口变短时会自动落进容差，
                    // 比值不会。
                    let clock_ratio = seconds_by_clock / elapsed.as_secs_f64();
                    assert!(
                        (0.95..1.05).contains(&clock_ratio),
                        "位置经 GetFrequency 归一后与墙钟之比 {clock_ratio:.4}，归一方式有误"
                    );

                    // 事实二：QPC 时间戳的单位是 100ns。零点同源留给托管侧验。
                    let qpc_ratio = qpc_delta as f64 / elapsed_100ns;
                    assert!(
                        (0.95..1.05).contains(&qpc_ratio),
                        "QPC 增量与墙钟之比 {qpc_ratio:.4}，单位不是 100ns"
                    );

                    // 事实三：这一条是实测，不是闸门。
                    //
                    // 0 是一个合法的回答，含义是「这个端点不报」——虚拟端点没有硬件链，
                    // 本就无可报。故这里不断言它大于零：那会把「没有信息」误判成故障，
                    // 而真正的错误是把 0 当成「零延迟」拿去算出声时刻。
                    // 引擎周期与缓冲长度是另外两个总有值的量，一并打出来供归因。
                    assert!(
                        (0..100 * TICKS_PER_MS).contains(&latency_100ns),
                        "共享模式延迟 {latency_100ns} (100ns) 不在可信量级内"
                    );
                    assert!(default_period > 0, "GetDevicePeriod 未给出引擎周期");
                    if latency_100ns == 0 {
                        println!("注意：本端点不报流延迟，自动估计缺这一项，须靠 per-device 手动偏移补。");
                    }
                }

                CoUninitialize();
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ring::MAX_DRIFT;
    use rubato::Resampler;

    /// 测试侧进两条轴的入口，省得每处铺开构造函数。
    fn cum(raw: u64) -> CumulativeFrames {
        CumulativeFrames::new(raw)
    }

    fn dev(raw: usize) -> DeviceFrames {
        DeviceFrames::new(raw)
    }

    #[test]
    fn target_frames_converts_ms_at_output_rate() {
        assert_eq!(target_frames(200), 9_600);
        assert_eq!(target_frames(50), 2_400);
    }

    #[test]
    fn the_timeline_needs_both_a_timestamp_and_the_alignment_switch() {
        // 四种组合逐个钉。此前这段接线内嵌在 FFI 包装层，把两个分支对调不会有判据变红，
        // 而它决定的是「对齐关闭时热路径走不走原路径」这条硬约束。
        assert!(should_use_timeline(1, true), "有时刻且开了对齐才走时间轴");
        assert!(
            !should_use_timeline(1, false),
            "关了对齐即走原路径，哪怕调用方送了时刻——否则 native 侧就有两个互不知情的开关"
        );
        assert!(!should_use_timeline(0, true), "没有时刻就无从建锚点");
        assert!(!should_use_timeline(0, false));
    }

    #[test]
    fn a_negative_timestamp_still_counts_as_timed() {
        // 只有 0 是「没有时刻」。负数是一个办不到的时刻，与「没给」的排查方向不同,
        // 不能被这道门吞成后者——它该由时间轴自己的溢出与外推判断处置。
        assert!(should_use_timeline(-1, true));
        assert!(should_use_timeline(i64::MIN, true));
    }

    #[test]
    fn target_buffer_ms_is_clamped_to_the_supported_range() {
        assert_eq!(clamp_target_ms(0), MIN_TARGET_MS);
        assert_eq!(clamp_target_ms(10), MIN_TARGET_MS);
        assert_eq!(clamp_target_ms(200), 200);
        assert_eq!(clamp_target_ms(9_999), MAX_TARGET_MS);
    }

    #[test]
    fn device_position_is_normalised_by_the_reported_frequency() {
        // 实测某端点 GetFrequency = 384000 = 48000 × block_align 8，即位置单位是字节。
        // 归一后 384000 个单位应恰为一秒的输出帧数。
        assert_eq!(output_frames_at_device_position(384_000, 384_000), 48_000);
        // 位置单位就是帧的端点：频率等于采样率，归一是恒等。
        assert_eq!(output_frames_at_device_position(4_800, 48_000), 4_800);
        // 先乘后除：不足一秒的位置不该归零。
        assert_eq!(output_frames_at_device_position(8, 384_000), 1);
    }

    #[test]
    fn a_missing_frequency_yields_no_position() {
        // 频率为 0 时位置无从归一。返回 0 与「还没起播」同义，是这个字段既有的空值约定。
        assert_eq!(output_frames_at_device_position(1_000_000, 0), 0);
    }

    #[test]
    fn device_latency_falls_back_to_the_engine_period() {
        // 实测：引擎周期 10 毫秒，流延迟 0（本机 14 个端点无一报值）。
        assert_eq!(device_latency_us(100_000, 0), 10_000);
        // 报了值就叠加。
        assert_eq!(device_latency_us(100_000, 30_000), 13_000);
        // 负值是「取不到」的表示，不是一段负延迟——不能把估计拉小。
        assert_eq!(device_latency_us(100_000, -1), 10_000);
        assert_eq!(device_latency_us(-1, -1), 0);
    }

    #[test]
    fn a_48k_endpoint_skips_the_resampler_unless_alignment_is_on() {
        // 关模式 + 48k 走原路径：一个采样都不经过重采样器，48k 仍是 bit-exact。
        assert!(build_resampler(false, 48_000, 1_056).is_none());
        // 开模式 + 48k 必须建：比率的执行者只有它，48k 上没有它就没有内环，
        // 而 200ppm 的相对晶振偏差在 50 秒后就吃掉 10 毫秒预算。
        assert!(build_resampler(true, 48_000, 1_056).is_some());
        // 非 48k 端点两种模式下都要建，与对齐无关——那是格式转换的需要。
        assert!(build_resampler(false, 44_100, 1_056).is_some());
        assert!(build_resampler(true, 44_100, 1_056).is_some());
    }

    #[test]
    fn invalid_parameters_never_build_a_resampler_in_either_mode() {
        // 参数无效与模式无关：无效参数下建出来的东西没有正确形态可言。
        for aligned in [false, true] {
            assert!(build_resampler(aligned, 0, 1_056).is_none(), "率为 0");
            assert!(build_resampler(aligned, 48_000, 0).is_none(), "缓冲为 0");
            assert!(build_resampler(aligned, 0, 0).is_none());
        }
    }

    #[test]
    fn a_48k_resampler_starts_at_the_identity_ratio() {
        // 起始比率必须是 1.0，否则开对齐的那一刻音调会跳一下。
        // 比率不直接可读，改看它的等价可观测量：恒等比率下要产出 N 个输出帧，
        // 稳态需要的输入帧数也是 N。
        let mut state = build_resampler(true, 48_000, 480).expect("48k 开对齐应当建");
        state.inner.set_chunk_size(480).expect("设块长");

        // 首轮的需求量含 sinc 核的预热，故取稳态：喂几轮之后再比。
        let mut out = vec![vec![0f32; 480], vec![0f32; 480]];
        for _ in 0..4 {
            let needed = state.inner.input_frames_next();
            let input = vec![vec![0f32; needed], vec![0f32; needed]];
            state
                .inner
                .process_into_buffer(&input, &mut out, None)
                .expect("重采样");
        }
        assert_eq!(
            state.inner.input_frames_next(),
            480,
            "恒等比率下输入需求应等于输出块长"
        );
    }

    #[test]
    fn alignment_parameters_read_back_what_was_written() {
        let cell = AlignmentCell::default();
        assert!(!cell.is_enabled());

        cell.set_runtime(3_000_000, -1_234, 500);
        cell.set_enabled(true);
        assert!(cell.is_enabled());
        assert_eq!(cell.d_ticks(), 3_000_000);
        // offset 与手动偏移都可以为负，故两者都不能存成无符号。
        assert_eq!(cell.offset_ticks(), -1_234);
        assert_eq!(cell.manual_offset_ticks(), 500);

        // 运行时三项在开关不动时随时可改——这正是两个写入口分开的形态。
        cell.set_runtime(4_000_000, 42, -7);
        assert!(cell.is_enabled());
        assert_eq!(cell.offset_ticks(), 42);

        cell.set_enabled(false);
        assert!(!cell.is_enabled());
    }

    #[test]
    fn target_depth_reads_back_what_was_written() {
        // 这是外环唯一的载体：写不进去，外环算出多少都落不了地。
        let target = TargetDepthCell::new(200);
        assert_eq!(target.current_ms(), 200);

        target.set_ms(320);
        assert_eq!(target.current_ms(), 320);
    }

    #[test]
    fn target_depth_writes_go_through_the_clamp() {
        // 夹紧在写入侧：越界值若能存进来，此后每一次读都要重新判一遍它，
        // 而读在 WASAPI 实时线程上每轮一次。
        let target = TargetDepthCell::new(200);

        target.set_ms(0);
        assert_eq!(target.current_ms(), MIN_TARGET_MS);
        target.set_ms(u32::MAX);
        assert_eq!(target.current_ms(), MAX_TARGET_MS);
        // 起播值走同一道闸门。
        assert_eq!(TargetDepthCell::new(0).current_ms(), MIN_TARGET_MS);
        assert_eq!(TargetDepthCell::new(9_999).current_ms(), MAX_TARGET_MS);
    }

    #[test]
    fn prefill_target_is_a_snapshot_not_a_live_read() {
        // prefill 是一次性粗调，外环是持续微调。prefill 跟着外环变，两者就在同一
        // 时间尺度上互相追，正是双环分层要避免的形态。
        let target = TargetDepthCell::new(200);
        let prefill = PrefillState::new(target_frames(target.current_ms()));

        target.set_ms(400);
        assert_eq!(prefill.target_frames(), target_frames(200));
        // （曾有第二个断言 prefill.target_frames() != target_frames(current_ms())：
        // 在第一条成立的前提下它退化成 target_frames(200) != target_frames(400)，
        // 说的是 target_frames 的单调性而非快照性质，已删。）
    }

    #[test]
    fn playing_position_folds_the_padding_into_the_48k_domain() {
        // padding 是设备帧，读游标是 48k 帧。漏掉折算或减号写错，误差整体偏移
        // 一个端点缓冲长度（本机 22 毫秒），且各设备不同——直接变成机间错位。
        // 期望值全部手算，不引用 output_frames_for 重算一遍。
        assert_eq!(
            playing_position_frames(cum(10_000), dev(960), 48_000),
            cum(9_040)
        );
        // 96k：960 设备帧只值 480 个 48k 帧——当年 prefill panic 的那一档。
        assert_eq!(
            playing_position_frames(cum(10_000), dev(960), 96_000),
            cum(9_520)
        );
        // 44.1k：960 × 48000 / 44100 截断为 1044。
        assert_eq!(
            playing_position_frames(cum(10_000), dev(960), 44_100),
            cum(8_956)
        );
        // 刚起播、硬重置刚过：padding 大于读游标，饱和到 0 而不是回绕，
        // 下游 sender_ticks_at 的本轮门会把 0 判成 None。
        assert_eq!(playing_position_frames(cum(100), dev(960), 48_000), cum(0));
    }

    #[test]
    fn ring_capacity_is_twice_the_upper_bound() {
        // 容量等于目标深度会让每次抖动都触发溢出丢帧，
        // 而丢帧正是抖动缓冲要消除的东西。
        assert_eq!(RING_CAPACITY_FRAMES, target_frames(MAX_TARGET_MS) * 2);
    }

    #[test]
    fn the_capacity_covers_the_deepest_target_plus_one_largest_step() {
        // C1 仅核目标上界加单笔最大阶跃的容量账；真实Q可达容量，外环穿插
        // 时总债也可超过单笔上限，不能据此断言容量钳不会咬或总债恒有界。
        let deepest = target_frames(MAX_TARGET_MS);
        let largest_debt = target_frames(MAX_TARGET_MS - MIN_TARGET_MS);
        assert!(deepest + largest_debt < RING_CAPACITY_FRAMES);

        // 余量恰 2400 帧,即输出率上的 50ms。这个数在代数上恒等于
        // target_frames(MIN_TARGET_MS):容量是上界的两倍,减去上界、再减去「上界减
        // 下界」,剩下的正是下界那一份。故字面钉住 2400 钉的是下界与输出率这两项,
        // 上界改动不从这条露头——那一项归容量绝对值那条钉。
        assert_eq!(RING_CAPACITY_FRAMES - (deepest + largest_debt), 2_400);
    }

    #[test]
    fn the_ring_capacity_derives_to_two_seconds_at_the_output_rate() {
        // C2 字面钉:96_000 帧即输出率上的 2000ms。
        //
        // 与上面 ring_capacity_is_twice_the_upper_bound 分工,两条各挡各的:那条钉
        // 「容量由上界的两倍导出」这条来历,上界与容量一起变时它恒绿;这条钉容量的
        // 绝对值,故上界从 1000 改成别的数(导出值随之走)只有这条看得见。两条断言若
        // 合写在一处,那条就再无独有的红点——任何使它红的改动都会连带这条一起红,
        // 而两处同时红分不出是来历坏了还是数值变了。
        assert_eq!(RING_CAPACITY_FRAMES, 96_000);
    }

    #[test]
    fn prefill_gate_opens_only_after_target_is_reached() {
        let mut state = PrefillState::new(target_frames(200));

        assert!(!state.is_open(1_000));
        assert!(state.is_open(9_600));
        // 开闸后不再回退：短暂欠载靠填零度过，反复进出预填充会一顿一顿。
        assert!(state.is_open(0));
    }

    #[test]
    fn sustained_underrun_triggers_hard_reset() {
        let mut state = PrefillState::new(target_frames(200));
        state.is_open(9_600);

        // 累计欠载 500ms 才重置。瞬时欠载填零即可，重置会丢掉已经收到的音频。
        assert!(!state.note_underrun(400.0));
        assert!(state.note_underrun(150.0));
    }

    #[test]
    fn any_successful_read_clears_the_underrun_tally() {
        let mut state = PrefillState::new(target_frames(200));
        state.is_open(9_600);

        state.note_underrun(400.0);
        state.note_progress();
        assert!(!state.note_underrun(400.0));
    }

    #[test]
    fn resync_excess_threshold_is_pinned_at_250ms() {
        // 字面钉。阈值立论(高于稳态噪声、低于病灶量级)钉的是 250 这个数本身,
        // 谁改它谁就得先来改这条判据,而不是让立论静默失效。
        assert_eq!(RESYNC_EXCESS_MS, 250);
    }

    #[test]
    fn resync_sustain_frames_is_pinned_at_24_000() {
        // 字面钉:24_000 = 500ms × 48 帧/ms。系数或时基误改都会先撞在这里。
        assert_eq!(RESYNC_SUSTAIN_FRAMES, 24_000);
    }

    #[test]
    fn the_gate_does_not_accumulate_below_threshold() {
        // 阈下轮次的消费帧数再大也不进账:门计的是「超额的持续」,
        // 不是「播放的持续」。
        let mut gate = ResyncGate::default();
        for _ in 0..10 {
            assert!(!gate.note(false, 1_000_000));
        }
        assert!(!gate.note(true, 23_999), "此前的阈下轮次不得留下积累");
    }

    #[test]
    fn the_gate_fires_exactly_when_sustained_frames_reach_the_threshold() {
        // 恰达阈点火(>=):8_000 × 3 = 24_000,第三次就是点火轮。
        let mut gate = ResyncGate::default();
        assert!(!gate.note(true, 8_000));
        assert!(!gate.note(true, 8_000));
        assert!(gate.note(true, 8_000), "恰累计到 24_000 应当点火");
    }

    #[test]
    fn a_dip_below_threshold_clears_the_tally() {
        // 回落清零:500ms 持续期滤的就是「一个 RTT 内自行排空的突发簇」,
        // 残留积累会让两个不相干的突发拼成一次误点。
        let mut gate = ResyncGate::default();
        assert!(!gate.note(true, 16_000));
        assert!(!gate.note(false, 960));
        assert!(!gate.note(true, 23_999), "回落之后必须从零重新攒");
    }

    #[test]
    fn firing_clears_the_gate() {
        // 点火自清:下一次点火要重新攒满整个持续期,否则一次积压会连环点火,
        // 每轮都丢一段最旧。前三步即 the_gate_fires_... 的点火序列。
        let mut gate = ResyncGate::default();
        gate.note(true, 8_000);
        gate.note(true, 8_000);
        gate.note(true, 8_000);
        assert!(!gate.note(true, 23_999), "点火后积累必须归零");
    }

    #[test]
    fn the_sustain_boundary_sits_between_23_999_and_24_000() {
        // 边界钉两侧:23_999 不点、再进 1 帧恰达 24_000 即点。
        let mut gate = ResyncGate::default();
        assert!(!gate.note(true, 23_999));
        assert!(gate.note(true, 1));
    }

    #[test]
    fn resync_excess_folds_without_an_occupancy_limit() {
        // D1 请求只折帧，盈余限制由应用者负责。
        assert_eq!(resync_drop_frames(300), 14_400);
    }

    #[test]
    fn no_surplus_over_target_shifts_nothing() {
        // D2 占用恰在目标深度上，盈余为零，一帧都不许丢。
        assert_eq!(
            clamp_shift_frames(target_frames(300), target_frames(200), 200),
            0
        );
    }

    #[test]
    fn the_clamp_caps_the_shift_at_the_surplus() {
        // D3 钳生效于部分:盈余 50ms,想丢 300ms,只能丢 50ms。
        assert_eq!(
            clamp_shift_frames(target_frames(300), target_frames(250), 200),
            target_frames(50)
        );
    }

    #[test]
    fn occupancy_below_target_saturates_instead_of_wrapping() {
        // D4 available < target:盈余为负,饱和到 0 而不回绕。裸减法在 debug 下当场
        // panic 在实时线程上,release 下回绕成天文数字、经 min 之后把缓冲一次丢空。
        assert_eq!(
            clamp_shift_frames(target_frames(300), target_frames(100), 200),
            0
        );
    }

    #[test]
    fn excess_milliseconds_fold_at_the_output_rate() {
        // D5 100ms 在 48k 轴上是 4800 帧，独立钉住折算时基。
        assert_eq!(resync_drop_frames(100), 4_800);
    }

    #[test]
    fn a_negative_excess_folds_to_no_shift() {
        // D6 负超额:那是放早,不是本门的事。裸 `as usize` 会把它回绕成天文数字,
        // 经饱和乘与钳之后表现为「一步把盈余全丢光」;折成 0 帧让它什么都不做。
        // 钉的是纯函数的定义域——点火轮的超额恒为正,接线上这一条永不走到。
        assert_eq!(resync_drop_frames(-300), 0);
    }

    #[test]
    fn a_read_cursor_shift_obeys_the_current_surplus() {
        // 等于目标、低于目标、零请求、小请求、超大请求和部分盈余。
        for (available, want, target, dropped, remaining) in [
            (16_800, 14_400, 300, 2_400, 14_400),
            (9_600, 14_400, 200, 0, 9_600),
            (480, 14_400, 200, 0, 480),
            (16_800, 0, 300, 0, 16_800),
            (16_800, 480, 300, 480, 16_320),
            (16_800, usize::MAX, 300, 2_400, 14_400),
            (0, usize::MAX, 200, 0, 0),
        ] {
            let mut ring = crate::ring::PlaybackRing::new(RING_CAPACITY_FRAMES);
            ring.push(&vec![7; available * 2]);
            assert_eq!(apply_read_cursor_shift(&mut ring, want, target), dropped);
            assert_eq!(ring.available_frames(), remaining);
        }
    }

    #[test]
    fn a_timestamp_gap_invalidates_the_old_surplus_before_a_shift() {
        let mut ring = crate::ring::PlaybackRing::new(RING_CAPACITY_FRAMES);
        ring.push_at(&vec![1; 24_000 * 2], 10_000_000);
        assert_eq!(ring.available_frames(), 24_000);
        let mut shift = PendingShift::default();
        let (next, want) = plan_setpoint_shift(-300, 500, &mut shift);
        ring.push_at(&vec![9; 480 * 2], 100_000_000);
        assert_eq!(ring.available_frames(), 480, "真实空档必须已触发整清");
        assert_eq!(want, 14_400);
        assert_eq!(apply_read_cursor_shift(&mut ring, want, next), 0);
        assert_eq!(ring.available_frames(), 480);
        assert_eq!(
            ring.sender_ticks_at(ring.read_cursor_frames()),
            Some(100_000_000)
        );
        let mut samples = vec![0; 960];
        assert_eq!(ring.read_into(&mut samples), 480);
        assert_eq!(samples, vec![9; 960]);
    }

    #[test]
    fn consecutive_shifts_use_the_remaining_occupancy() {
        let mut ring = crate::ring::PlaybackRing::new(RING_CAPACITY_FRAMES);
        ring.push(&vec![1; 16_800 * 2]);
        assert_eq!(apply_read_cursor_shift(&mut ring, 1_440, 300), 1_440);
        assert_eq!(apply_read_cursor_shift(&mut ring, 1_440, 300), 960);
        assert_eq!(ring.available_frames(), 14_400);
    }

    #[test]
    fn a_read_cursor_shift_preserves_samples_and_the_sender_timeline() {
        let mut ring = crate::ring::PlaybackRing::new(RING_CAPACITY_FRAMES);
        let samples: Vec<i16> = (0..16_800).flat_map(|frame| [frame, -frame]).collect();
        ring.push_at(&samples, 10_000_000);
        assert_eq!(apply_read_cursor_shift(&mut ring, 14_400, 300), 2_400);
        assert_eq!(
            ring.sender_ticks_at(ring.read_cursor_frames()),
            Some(10_500_000)
        );
        let mut remaining = vec![0; 14_400 * 2];
        assert_eq!(ring.read_into(&mut remaining), 14_400);
        assert_eq!(remaining, samples[4_800..]);
    }

    #[test]
    fn the_largest_excess_saturates_before_folding_to_frames() {
        assert_eq!(resync_drop_frames(i64::MAX), usize::MAX / 1_000);
        assert_eq!(resync_drop_frames(0), 0);
        assert_eq!(resync_drop_frames(i64::MIN), 0);
    }

    /// 声明预算的一个任意起点。绝对值不进任何判据——纯件只看差量。
    const SETPOINT_D: i64 = 300 * TICKS_PER_MS;

    /// 默认播放延迟预算下的目标深度。
    const SETPOINT_TARGET_MS: u32 = 300;

    /// 已记锚的纯件。首轮返回值刻意不在这里断言:S1 是它唯一的执行者,
    /// 助手里再断言一遍会让「锚有初值」那类改动红遍全组,失去定位力。
    fn anchored_setpoint() -> SetpointStep {
        let mut step = SetpointStep::default();
        step.note(SETPOINT_D, SETPOINT_TARGET_MS);
        step
    }

    #[test]
    fn the_setpoint_dead_zone_is_the_outer_loop_dead_zone_folded_to_ticks() {
        // S8 两条各挡各的,谁也替不了谁。字面这条挡的是「常量仍由 DEAD_ZONE_MS
        // 导出,而 DEAD_ZONE_MS 或时基被改」;导出那条挡的是「常量被脱钩写死成
        // 10_000,此后 DEAD_ZONE_MS 再改」——那时字面条恒绿,只有它看得见两份
        // 声明已经各自为真,而那正是常量文档注释点名要防的形态。
        assert_eq!(SETPOINT_DEAD_ZONE_TICKS, 10_000);
        assert_eq!(
            SETPOINT_DEAD_ZONE_TICKS,
            (DEAD_ZONE_MS * TICKS_PER_MS as f64) as i64
        );
    }

    #[test]
    fn the_first_observation_only_records_the_anchor() {
        // S1 首轮没有「变化」可言:起播已按那一刻请求的深度起。锚若有初值 0,
        // 首轮就会把整个 D 当成一次阶跃执行下去,即起播当场再挪一次深度。
        let mut step = SetpointStep::default();
        assert_eq!(step.note(SETPOINT_D, SETPOINT_TARGET_MS), 0);
    }

    #[test]
    fn a_setpoint_change_inside_the_dead_zone_moves_nothing() {
        // S2 与外环死区同一个理由:小于误差预算分配额的变化不值得动 target,
        // 而每次动 target 都在改输出延迟。
        let mut step = anchored_setpoint();
        assert_eq!(step.note(SETPOINT_D + 9_000, SETPOINT_TARGET_MS), 0);
    }

    #[test]
    fn dead_zone_rounds_leave_the_anchor_where_it_was() {
        // S3 本组核心。连着两轮 +0.9ms:第一轮在死区内返 0 且不动锚,第二轮相对
        // 原锚已是 1.8ms,越过死区并向零截断成 +1。锚若被第一轮更新,第二轮的差量
        // 只剩 0.9ms、仍在死区内 → 返 0,于是连续漂移永远进不了执行。
        let mut step = anchored_setpoint();
        assert_eq!(step.note(SETPOINT_D + 9_000, SETPOINT_TARGET_MS), 0);
        assert_eq!(step.note(SETPOINT_D + 18_000, SETPOINT_TARGET_MS), 1);
    }

    #[test]
    fn the_anchor_advances_by_the_folded_milliseconds_not_by_the_whole_step() {
        // S12 钉锚的前进量:是折出的整毫秒,不是跳到本轮的 d_ticks。向零截断丢掉的
        // 那不到一毫秒留在锚与 d_ticks 之间,下一轮接着攒。
        // 两轮各 +0.9ms:第一轮相对锚是 1.8ms,折出 +1 并把锚推进 1ms;第二轮相对
        // 新锚是 1.7ms,仍越过死区,再折出 +1。锚若整量跳到 D+1.8ms,第二轮的差量
        // 只剩 0.9ms、落进死区 → 返 0,那 0.8ms 就永久丢了。
        // d_ticks 每个渲染回调读一次,拖动滑块时每拍都丢一截,欠跟的量会落回外环。
        let mut step = anchored_setpoint();
        assert_eq!(step.note(SETPOINT_D + 18_000, SETPOINT_TARGET_MS), 1);
        assert_eq!(step.note(SETPOINT_D + 27_000, SETPOINT_TARGET_MS), 1);
    }

    #[test]
    fn a_step_down_past_the_lower_bound_applies_only_what_fits() {
        // S4 下调 300ms:300 − 300 = 0 在下界之外,钳到 50,故实际生效 −250。
        // 账按钳后的量走——多出来的 50ms 本就无处可去。
        let mut step = anchored_setpoint();
        assert_eq!(
            step.note(SETPOINT_D - 300 * TICKS_PER_MS, SETPOINT_TARGET_MS),
            -250
        );
    }

    #[test]
    fn a_step_down_below_zero_is_not_silently_turned_into_a_step_up() {
        // 这一条的必要性是实测出来的:S4 的 −300ms 恰好落在 0 上,不触发回绕,于是
        // 「先夹到 u32 值域」那道护栏在其余判据之下无人看守——去掉它全组照绿。
        // −400ms 让 target + ΔD 真的为负:负值裸 `as u32` 回绕成天文数字,经
        // clamp_target_ms 之后落在上界,即用户下调被静默翻成上调到上界。
        let mut step = anchored_setpoint();
        assert_eq!(
            step.note(SETPOINT_D - 400 * TICKS_PER_MS, SETPOINT_TARGET_MS),
            -250
        );
    }

    #[test]
    fn a_step_inside_the_range_is_applied_one_to_one() {
        // S5 上调 300ms,600 在界内。前馈是精确 1:1:收敛条件下 D 变而采集段、
        // 网络段、设备尾段不变,target 定态值的变化量恰等于 ΔD。
        let mut step = anchored_setpoint();
        assert_eq!(
            step.note(SETPOINT_D + 300 * TICKS_PER_MS, SETPOINT_TARGET_MS),
            300
        );
    }

    #[test]
    fn a_step_up_past_the_upper_bound_applies_only_what_fits() {
        // S6 上调 900ms:1200 越上界,钳到 1000,故实际生效 +700。
        let mut step = anchored_setpoint();
        assert_eq!(
            step.note(SETPOINT_D + 900 * TICKS_PER_MS, SETPOINT_TARGET_MS),
            700
        );
    }

    #[test]
    fn a_step_up_at_the_upper_bound_applies_nothing() {
        // S7 target 已贴在上界:钳后与钳前同值,故本轮不动作。
        let mut step = SetpointStep::default();
        step.note(SETPOINT_D, MAX_TARGET_MS);
        assert_eq!(step.note(SETPOINT_D + 300 * TICKS_PER_MS, MAX_TARGET_MS), 0);
    }

    #[test]
    fn a_change_exactly_at_the_dead_zone_fires() {
        // S9 恰达阈也点火(死区判 `<`,即点火判 `>=`),与门的恰达阈同一口径。
        let mut step = anchored_setpoint();
        assert_eq!(step.note(SETPOINT_D + TICKS_PER_MS, SETPOINT_TARGET_MS), 1);
    }

    #[test]
    fn the_anchor_advances_in_full_even_when_the_clamp_eats_the_whole_step() {
        // S10 钉的是「锚按钳前的折出量前进」:target 贴在上界,+300ms 那轮被钳成
        // applied = 0,而锚照样前进了整 300ms,故紧接着拨回 D 是一次完整的 −300。
        //
        // 锚若改成按 applied(钳后)前进,此刻差量为 0 → 返 0:撞上界后欠账永远
        // 还不掉,于是每一轮都点火而每一次 applied 都是 0,那是空转点火。
        // 这与 S12「按折出毫秒前进」不冲突——被截断的余量与被钳掉的余量是两回事:
        // 前者下一轮还攒得回来,后者本就无处可去。
        let mut step = SetpointStep::default();
        step.note(SETPOINT_D, MAX_TARGET_MS);
        assert_eq!(step.note(SETPOINT_D + 300 * TICKS_PER_MS, MAX_TARGET_MS), 0);
        assert_eq!(step.note(SETPOINT_D, MAX_TARGET_MS), -300);
    }

    #[test]
    fn a_setpoint_from_the_wire_cannot_overflow_the_difference() {
        // S13 钉两处饱和。d_ticks 是对端在报文里声明的值,畸形或敌意报文可以给出
        // 任意 i64,故这条路径可达:裸减与裸 abs 在 debug 下都是当场 panic,而这条
        // 链要跑在实时渲染线程上,panic 会带走整个宿主进程。
        // 饱和之后语义仍然正确:差量饱和成极大负值,折毫秒后夹到 0、钳到下界。
        let mut step = anchored_setpoint();
        assert_eq!(step.note(i64::MIN, SETPOINT_TARGET_MS), -250);
    }

    // 有限的48k/10ms纯组件闭环，不模拟WASAPI padding、rubato或生产循环绑定；
    // 不建模last_qpc首轮/复位后跳过一次outer.step。
    struct FiniteShiftTrace {
        ring: std::sync::Mutex<crate::ring::PlaybackRing>,
        step: SetpointStep,
        shift: PendingShift,
        outer: crate::outer_loop::OuterLoop,
        gate: ResyncGate,
        target: u32,
        now: i64,
        rounds: usize,
        padded: usize,
        trims: Vec<usize>,
        drops: Vec<(usize, usize)>,
    }

    impl FiniteShiftTrace {
        fn new(q_ms: u32) -> Self {
            let mut ring = crate::ring::PlaybackRing::new(RING_CAPACITY_FRAMES);
            ring.push_at(&vec![7; target_frames(q_ms) * 2], 10_000_000);
            let mut step = SetpointStep::default();
            step.note(200 * TICKS_PER_MS, 200);
            Self {
                ring: std::sync::Mutex::new(ring),
                step,
                shift: PendingShift::default(),
                outer: crate::outer_loop::OuterLoop::default(),
                gate: ResyncGate::default(),
                target: 200,
                now: 10_000_000 + i64::from(q_ms) * TICKS_PER_MS,
                rounds: 0,
                padded: 0,
                trims: Vec::new(),
                drops: Vec::new(),
            }
        }

        fn tick(&mut self, d_ms: i64) {
            let (available, sender) = {
                let ring = self.ring.lock().unwrap();
                (
                    ring.available_frames(),
                    ring.sender_ticks_at(ring.read_cursor_frames()).unwrap(),
                )
            };
            let error = crate::outer_loop::play_time_error_ticks(
                crate::outer_loop::actual_play_ticks(self.now, 0, 0),
                crate::outer_loop::target_play_ticks(sender, d_ms * TICKS_PER_MS, 0),
            );
            let observed_target = self.target;
            self.target =
                self.outer
                    .step(error as f64 / TICKS_PER_MS as f64, 0.01, observed_target);
            let applied = self.step.note(d_ms * TICKS_PER_MS, observed_target);
            let (next, want) = plan_setpoint_shift(applied, observed_target, &mut self.shift);
            if applied != 0 {
                self.target = next;
                self.outer.reset();
                self.gate = ResyncGate::default();
                self.trims.push(apply_read_cursor_shift(
                    &mut self.ring.lock().unwrap(),
                    want,
                    next,
                ));
            }
            let read = consume_shifted(&self.ring, &mut self.shift, &mut [99; 960]).unwrap();
            assert_eq!(read.taken, read.wanted, "有限稳定供给不应欠载");
            self.padded += read.padded;
            self.rounds += 1;
            let dropped = apply_resync_gate(
                &mut self.gate,
                &mut self.shift,
                &self.ring,
                ResyncObservation {
                    applied,
                    aligned_error_ticks: Some(error),
                    available_frames: available,
                    observation_target_ms: observed_target,
                    current_target_ms: self.target,
                    needed: 480,
                },
            )
            .unwrap();
            if dropped > 0 {
                self.drops.push((self.rounds, dropped));
                self.outer.reset();
            }
            self.ring.lock().unwrap().push_at(&[7; 960], self.now);
            self.now += 10 * TICKS_PER_MS;
        }
    }

    #[test]
    fn finite_high_backlog_step_settles_debt_without_a_second_pad_trim_cycle() {
        for (q_ms, pad_ms) in [(1950, 50), (2000, 0)] {
            let mut trace = FiniteShiftTrace::new(q_ms);
            for _ in 0..150 {
                trace.tick(900);
            }
            assert_eq!(
                trace.padded,
                target_frames(pad_ms),
                "Q={q_ms}:门后不得续付旧债"
            );
            assert_eq!(
                trace.drops,
                [(51, target_frames(1090))],
                "阶跃轮不计门持续期"
            );
            assert_eq!(trace.shift.pad_frames, 0);
            assert_eq!(
                trace.ring.lock().unwrap().available_frames(),
                target_frames(910)
            );
            assert_eq!(trace.target, 900);
        }
    }

    #[test]
    fn finite_normal_step_pays_the_whole_seven_hundred_ms() {
        let mut trace = FiniteShiftTrace::new(200);
        for _ in 0..150 {
            trace.tick(900);
        }
        assert_eq!(trace.padded, target_frames(700));
        assert!(trace.drops.is_empty());
        assert_eq!(trace.shift.pad_frames, 0);
        assert_eq!(
            trace.ring.lock().unwrap().available_frames(),
            target_frames(900)
        );
    }

    #[test]
    fn finite_reversal_before_resync_trims_only_the_paid_part() {
        for (q_ms, rounds, paid_ms) in [(2000, 1, 0), (1950, 3, 30)] {
            let mut trace = FiniteShiftTrace::new(q_ms);
            for _ in 0..rounds {
                trace.tick(900);
            }
            trace.tick(200);
            assert_eq!(trace.padded, target_frames(paid_ms));
            assert_eq!(trace.trims, [0, target_frames(paid_ms)]);
            assert!(trace.drops.is_empty());
            assert_eq!(trace.shift.pad_frames, 0);
            assert_eq!(
                trace.ring.lock().unwrap().available_frames(),
                target_frames(q_ms)
            );
        }
    }

    #[test]
    fn finite_reversal_after_resync_uses_the_new_position() {
        let mut trace = FiniteShiftTrace::new(1950);
        for _ in 0..51 {
            trace.tick(900);
        }
        trace.tick(200);
        for _ in 52..150 {
            trace.tick(200);
        }
        assert_eq!(trace.trims, [0, target_frames(700)]);
        assert_eq!(trace.drops, [(51, target_frames(1090))]);
        assert_eq!(trace.padded, target_frames(50));
        assert_eq!(trace.shift.pad_frames, 0);
        assert_eq!(
            trace.ring.lock().unwrap().available_frames(),
            target_frames(210)
        );
    }

    #[test]
    fn deferred_pad_keeps_unpaid_debt_until_payment_opportunities_arrive() {
        let mut shift = PendingShift::default();
        shift.owe_pad(1_000);
        assert_eq!(shift.take_pad(400, 0), 0);
        assert_eq!(shift.take_pad(0, 400), 0);
        assert_eq!(shift.take_pad(400, 400), 400);
        assert_eq!(shift.take_pad(400, 400), 400);
        assert_eq!(shift.take_pad(400, 400), 200);
        assert_eq!(shift.take_pad(400, 400), 0);
    }

    #[test]
    fn shifted_consumption_uses_the_locked_actual_capacity_and_samples() {
        use std::sync::Mutex;
        let ring = Mutex::new(crate::ring::PlaybackRing::new(6));
        let mut shift = PendingShift::default();
        shift.owe_pad(5);
        ring.lock().unwrap().push(&[7; 8]);
        let mut out = [99; 8];
        let read = consume_shifted(&ring, &mut shift, &mut out).unwrap();
        assert_eq!((read.padded, read.wanted, read.taken), (2, 2, 2));
        assert_eq!(out, [0, 0, 0, 0, 7, 7, 7, 7]);
        assert_eq!(shift.pad_frames, 3);
    }

    #[test]
    fn a_setpoint_round_skips_the_gate_even_for_a_large_callback() {
        use std::sync::Mutex;
        let ring = Mutex::new(crate::ring::PlaybackRing::new(RING_CAPACITY_FRAMES));
        ring.lock()
            .unwrap()
            .push(&vec![1; RING_CAPACITY_FRAMES * 2]);
        for needed in [480, 24_000, 48_000] {
            let mut gate = ResyncGate::default();
            let mut shift = PendingShift::default();
            shift.owe_pad(33_600);
            let obs = ResyncObservation {
                applied: 700,
                aligned_error_ticks: Some(1_100 * TICKS_PER_MS),
                available_frames: RING_CAPACITY_FRAMES,
                observation_target_ms: 200,
                current_target_ms: 900,
                needed,
            };
            assert_eq!(
                apply_resync_gate(&mut gate, &mut shift, &ring, obs),
                Some(0)
            );
            assert_eq!(gate.sustained_frames, 0, "阶跃轮不得累计旧门观测");
            assert_eq!(shift.pad_frames, 33_600);
            let next = ResyncObservation {
                applied: 0,
                aligned_error_ticks: Some(1_100 * TICKS_PER_MS),
                available_frames: RING_CAPACITY_FRAMES,
                observation_target_ms: 900,
                current_target_ms: 900,
                needed,
            };
            let dropped = apply_resync_gate(&mut gate, &mut shift, &ring, next).unwrap();
            if needed == 480 {
                assert_eq!(dropped, 0);
                assert_eq!(gate.sustained_frames, 480, "下一轮只累计新观测");
            } else {
                assert!(dropped > 0);
                assert_eq!(shift.pad_frames, 0);
                ring.lock()
                    .unwrap()
                    .push(&vec![1; RING_CAPACITY_FRAMES * 2]);
            }
        }
    }

    #[test]
    fn resync_settles_only_after_a_real_shift_and_uses_both_targets() {
        use std::sync::Mutex;
        for (q, current, expected) in [
            (43_200, 900, 0),
            (81_600, 900, 14_400),
            (96_000, 900, 14_400),
        ] {
            let ring = Mutex::new(crate::ring::PlaybackRing::new(RING_CAPACITY_FRAMES));
            ring.lock().unwrap().push(&vec![1; q * 2]);
            let mut shift = PendingShift::default();
            shift.owe_pad(33_600);
            let mut gate = ResyncGate::default();
            let obs = ResyncObservation {
                applied: 0,
                aligned_error_ticks: Some(300 * TICKS_PER_MS),
                available_frames: 0,
                observation_target_ms: 200,
                current_target_ms: current,
                needed: 24_000,
            };
            assert_eq!(
                apply_resync_gate(&mut gate, &mut shift, &ring, obs),
                Some(expected)
            );
            assert_eq!(
                shift.pad_frames,
                if expected == 0 { 33_600 } else { 0 },
                "只有真实重定位才结算债"
            );
        }
        let ring = Mutex::new(crate::ring::PlaybackRing::new(RING_CAPACITY_FRAMES));
        ring.lock().unwrap().push(&vec![1; 48_000 * 2]);
        let mut shift = PendingShift::default();
        shift.owe_pad(100);
        let obs = ResyncObservation {
            applied: 0,
            aligned_error_ticks: None,
            available_frames: 48_000,
            observation_target_ms: 200,
            current_target_ms: 900,
            needed: 24_000,
        };
        assert_eq!(
            apply_resync_gate(&mut ResyncGate::default(), &mut shift, &ring, obs),
            Some(4_800),
            "FIFO按观察target点火，按当前target钳真剪"
        );
        assert_eq!(shift.pad_frames, 0);
    }

    #[test]
    fn shifted_consumption_observes_pushes_resets_and_real_missing_frames() {
        use std::sync::Mutex;
        let ring = Mutex::new(crate::ring::PlaybackRing::new(6));
        let mut shift = PendingShift::default();
        shift.owe_pad(10);
        let early = ring.lock().unwrap().available_frames();
        assert_eq!(early, 0);
        ring.lock().unwrap().push_at(&[7; 12], 10_000_000);
        let mut out = [99; 8];
        let read = consume_shifted(&ring, &mut shift, &mut out).unwrap();
        assert_eq!(
            (read.padded, read.wanted, read.taken),
            (0, 4, 4),
            "支付不得使用push之前的空余快照"
        );
        ring.lock().unwrap().push_at(&[8; 2], 100_000_000);
        let read = consume_shifted(&ring, &mut shift, &mut out).unwrap();
        assert_eq!((read.padded, read.wanted, read.taken), (4, 0, 0));
        assert_eq!(out, [0; 8], "全垫实际写零");
        assert_eq!(shift.pad_frames, 6, "push_at整清不擅自清债");
        shift.clear();
        let read = consume_shifted(&ring, &mut shift, &mut out).unwrap();
        assert_eq!((read.padded, read.wanted, read.taken), (0, 4, 1));
        assert_eq!(
            read.wanted - read.taken,
            3,
            "欠载仅是真正要求读但未读到的帧"
        );
        assert_eq!(out, [8, 8, 0, 0, 0, 0, 0, 0]);
    }

    #[test]
    fn pad_representation_keeps_bits_above_u32() {
        // 表示边界测试，不声称完整闭环能走到该余额。
        let mut shift = PendingShift {
            pad_frames: u64::from(u32::MAX) + 100,
        };
        assert_eq!(shift.take_pad(40, 40), 40);
        assert_eq!(shift.owe_trim(60), 0);
        assert_eq!(shift.pad_frames, u64::from(u32::MAX));
        assert_eq!(shift.take_pad(1, 1), 1);
        assert_eq!(shift.pad_frames, u64::from(u32::MAX) - 1);
    }

    #[test]
    fn invalid_alignment_preserves_commands_until_payment_or_real_resync() {
        use std::sync::Mutex;
        for backlog in [43_200, 96_000] {
            let mut step = SetpointStep::default();
            let live = alignment_cell(true, 200 * TICKS_PER_MS, LIVE_OFFSET_TICKS);
            live_setpoint_step(&mut step, &live, 200);
            live.set_runtime(900 * TICKS_PER_MS, LIVE_OFFSET_TICKS, 0);
            let mut shift = PendingShift::default();
            let applied = live_setpoint_step(&mut step, &live, 200);
            let (target, _) = plan_setpoint_shift(applied, 200, &mut shift);
            live.set_runtime(0, 0, 0);
            assert_eq!(live_setpoint_step(&mut step, &live, target), 0);
            let ring = Mutex::new(crate::ring::PlaybackRing::new(RING_CAPACITY_FRAMES));
            ring.lock().unwrap().push(&vec![1; backlog * 2]);
            let dropped = apply_resync_gate(
                &mut ResyncGate::default(),
                &mut shift,
                &ring,
                ResyncObservation {
                    applied: 0,
                    aligned_error_ticks: None,
                    available_frames: backlog,
                    observation_target_ms: target,
                    current_target_ms: target,
                    needed: 24_000,
                },
            )
            .unwrap();
            assert_eq!(dropped, if backlog == 96_000 { 52_800 } else { 0 });
            live.set_runtime(900 * TICKS_PER_MS, LIVE_OFFSET_TICKS, 0);
            assert_eq!(
                live_setpoint_step(&mut step, &live, target),
                0,
                "恢复同D不重复接债"
            );
            live.set_runtime(200 * TICKS_PER_MS, LIVE_OFFSET_TICKS, 0);
            let reverse = live_setpoint_step(&mut step, &live, target);
            let (next, want) = plan_setpoint_shift(reverse, target, &mut shift);
            assert_eq!(want, if dropped > 0 { 33_600 } else { 0 });
            assert_eq!(
                apply_read_cursor_shift(&mut ring.lock().unwrap(), want, next),
                want
            );
        }
    }

    #[test]
    fn locked_shift_helpers_report_poison_without_consuming_debt() {
        use std::sync::Mutex;
        let ring = Mutex::new(crate::ring::PlaybackRing::new(6));
        let _ = std::panic::catch_unwind(|| {
            let _guard = ring.lock().unwrap();
            panic!("测试锁毒化");
        });
        let mut shift = PendingShift::default();
        shift.owe_pad(100);
        assert!(consume_shifted(&ring, &mut shift, &mut [0; 8]).is_none());
        let obs = ResyncObservation {
            applied: 0,
            aligned_error_ticks: Some(300 * TICKS_PER_MS),
            available_frames: 0,
            observation_target_ms: 200,
            current_target_ms: 200,
            needed: 24_000,
        };
        assert!(apply_resync_gate(&mut ResyncGate::default(), &mut shift, &ring, obs).is_none());
        assert_eq!(shift.pad_frames, 100);
    }

    #[test]
    fn outer_variation_bounds_debt_across_steps_and_resets() {
        let mut outer = crate::outer_loop::OuterLoop::default();
        let mut step = SetpointStep::default();
        let mut shift = PendingShift::default();
        let mut target = 200;
        let mut d = 200 * TICKS_PER_MS;
        let mut downward = 0u64;
        step.note(d, target);
        for (error, delta, reset) in [
            (10_000.0, 700, false),
            (-10_000.0, -200, false),
            (10_000.0, 100, true),
            (10_000.0, -600, false),
            (-10_000.0, 700, true),
        ] {
            let next = outer.step(error, 4.0, target);
            downward += u64::from(target.saturating_sub(next));
            target = next;
            d += delta * TICKS_PER_MS;
            let applied = step.note(d, target);
            (target, _) = plan_setpoint_shift(applied, target, &mut shift);
            assert!(shift.pad_frames <= 48 * (u64::from(target - MIN_TARGET_MS) + downward));
            if reset {
                outer.reset();
                shift.clear();
            }
        }
    }

    /// 大到不参与的余量与需求量。给到饱和值是刻意的:这些条要读的是钳与 min 之外的
    /// 那部分语义,让它们在条内恒不触发,红点才不会串到别处去。
    const PLENTY: usize = usize::MAX;

    #[test]
    fn pad_debt_accumulates_across_rounds() {
        // P1 余额累加:垫零跨轮摊完,两笔各 4800 帧(48k 轴 100ms)的欠账都得还在。
        // 后一笔若覆盖前一笔,用户连拨两次滑块只有后一次生效。
        let mut shift = PendingShift::default();
        shift.owe_pad(4_800);
        shift.owe_pad(4_800);
        assert_eq!(shift.take_pad(PLENTY, PLENTY), 9_600);
    }

    #[test]
    fn the_headroom_caps_payment_without_discarding_debt() {
        // P2 余量只限制本轮支付，余下7200帧仍在账上。
        let mut shift = PendingShift::default();
        shift.owe_pad(9_600);
        assert_eq!(shift.take_pad(PLENTY, 2_400), 2_400);
        assert_eq!(shift.take_pad(PLENTY, PLENTY), 7_200);
    }

    #[test]
    fn the_headroom_is_rechecked_on_each_payment() {
        // P9 两笔完整累加；容量每轮独立变化，不裁掉未支付余额。
        let mut shift = PendingShift::default();
        shift.owe_pad(4_800);
        shift.owe_pad(9_600);
        assert_eq!(shift.take_pad(PLENTY, 2_400), 2_400);
        assert_eq!(shift.take_pad(PLENTY, 0), 0);
        assert_eq!(shift.take_pad(PLENTY, 4_800), 4_800);
        assert_eq!(shift.take_pad(PLENTY, PLENTY), 7_200);
    }

    #[test]
    fn each_round_takes_only_what_that_round_needs() {
        // P3 按轮消费:欠 1000 帧,每轮只取本轮要的 400,第三轮只剩 200。取多了本轮
        // 无处写,不扣余额则永远垫不完。第三轮的 200 才是这条的钉点——前两轮的 400
        // 在「取了不扣」的写法下碰巧也对。
        let mut shift = PendingShift::default();
        shift.owe_pad(1_000);
        assert_eq!(shift.take_pad(400, PLENTY), 400);
        assert_eq!(shift.take_pad(400, PLENTY), 400);
        assert_eq!(shift.take_pad(400, PLENTY), 200);
    }

    #[test]
    fn a_trim_cancels_pending_pad_before_it_cuts_anything() {
        // P4 抵扣优先:欠着 1000 帧垫零未清,来了 400 的真剪,先取消 400 尚未发生的
        // 垫零,一帧都不必真剪(返 0),余额剩 600。取消未发生的位移零代价,而先垫
        // 后剪会让来回拨滑块听到一串本可互相抵消的静音与跳跃。
        let mut shift = PendingShift::default();
        shift.owe_pad(1_000);
        assert_eq!(shift.owe_trim(400), 0);
        assert_eq!(shift.take_pad(PLENTY, PLENTY), 600);
    }

    #[test]
    fn a_trim_larger_than_the_pad_leaves_the_remainder_to_cut() {
        // P5 抵扣不足:欠 400 垫零,来了 1000 的真剪,抵掉 400 之后 600 交调用方去剪。
        // 第二条断言钉的是 pad 那一侧也被扣了——只算抵扣量而不扣余额的写法返回值
        // 仍对,却会把已经抵消掉的 400 再垫一次。
        let mut shift = PendingShift::default();
        shift.owe_pad(400);
        assert_eq!(shift.owe_trim(1_000), 600);
        assert_eq!(shift.take_pad(PLENTY, PLENTY), 0);
    }

    #[test]
    fn a_trim_with_no_pad_to_cancel_passes_through_whole() {
        // P6 无余额可抵:整笔原样交给调用方去剪。抵扣是一条旁路而非必经之路——用户
        // 没在拨滑块的常态下,每一次真剪走的都是这条。
        let mut shift = PendingShift::default();
        assert_eq!(shift.owe_trim(1_000), 1_000);
    }

    #[test]
    fn a_hard_reset_drops_the_whole_balance() {
        // P7 硬重置:ring 已全清,欠下的位移无所指——它记的是「把现有占用推到某处」,
        // 而现有占用已经不在了。留着会让重新预填充之后凭空垫上一段静音。
        let mut shift = PendingShift::default();
        shift.owe_pad(1_000);
        shift.clear();
        assert_eq!(shift.take_pad(PLENTY, PLENTY), 0);
    }

    #[test]
    fn taking_from_an_empty_balance_pads_nothing() {
        // P8 空余额:没欠就一帧都不垫。这是渲染循环绝大多数轮次走的路——垫零是例外,
        // 而例外的默认值必须是「什么都不做」。
        let mut shift = PendingShift::default();
        assert_eq!(shift.take_pad(400, PLENTY), 0);
    }

    #[test]
    fn a_zero_step_moves_neither_the_target_nor_the_balance() {
        // R1 零不动。这是渲染每一轮的常态路径:D 没变时执行器必须什么都不做,连余额
        // 都不许碰。若零也走进某一条臂并顺手动了余额,未清完的垫零会被每一轮的零阶跃
        // 反复啃掉,用户上调一次深度只涨一点点就再也不涨。
        let mut shift = PendingShift::default();
        shift.owe_pad(target_frames(100));

        assert_eq!(plan_setpoint_shift(0, 300, &mut shift), (300, 0));
        assert_eq!(
            shift.take_pad(PLENTY, PLENTY),
            target_frames(100),
            "余额须原封不动"
        );
    }

    #[test]
    fn a_step_up_owes_a_pad_and_cuts_nothing() {
        // R2 上调走垫零臂:读游标暂停,一帧都不剪。剪了就是把刚要推高的占用又推回去。
        let mut shift = PendingShift::default();

        let (next, drop) = plan_setpoint_shift(300, 300, &mut shift);

        assert_eq!(next, 600);
        assert_eq!(drop, 0, "上调不得真剪");
        assert_eq!(
            shift.take_pad(PLENTY, PLENTY),
            target_frames(300),
            "整笔记成垫零欠账"
        );
    }

    #[test]
    fn a_step_down_cuts_now_and_owes_no_pad() {
        // R3 下调返回原始欠剪量，不留跨轮剪账。
        let mut shift = PendingShift::default();

        let (next, drop) = plan_setpoint_shift(-300, 600, &mut shift);

        assert_eq!(next, 300);
        assert_eq!(drop, 14_400);
        assert_eq!(shift.take_pad(PLENTY, PLENTY), 0, "下调不留垫零欠账");
    }

    #[test]
    fn a_pending_pad_is_cancelled_before_anything_is_cut() {
        // R4 抵扣在先。已欠 100ms 垫零时来一笔 300ms 下调:先抵掉那 100ms——取消一笔
        // 尚未发生的位移零代价,缓冲里一帧都还没动过——只剩 200ms 交真剪。若先垫完再剪,
        // 用户来回拨滑块会听到一串本可互相抵消的静音与跳跃。
        let mut shift = PendingShift::default();
        shift.owe_pad(target_frames(100));

        let (_, drop) = plan_setpoint_shift(-300, 600, &mut shift);

        assert_eq!(drop, target_frames(200), "抵扣之后才是该真剪的量");
        assert_eq!(shift.take_pad(PLENTY, PLENTY), 0, "抵扣掉的那部分不再欠垫");
    }

    #[test]
    fn a_small_step_down_only_eats_into_the_pending_pad() {
        // R5 下调量小于欠垫量:全额被抵扣,一帧都不剪,余额只减不清。
        // 与 R4 各挡各的:R4 挡「抵扣之后剩下的量交真剪」,这条挡「抵扣够用时不许动
        // 缓冲」——把抵扣写成「先剪了再补记」时,只有这条看得见。
        let mut shift = PendingShift::default();
        shift.owe_pad(target_frames(300));

        let (_, drop) = plan_setpoint_shift(-100, 400, &mut shift);

        assert_eq!(drop, 0, "抵扣够用就一帧都不该剪");
        assert_eq!(
            shift.take_pad(PLENTY, PLENTY),
            target_frames(200),
            "余额只减去抵扣掉的那部分"
        );
    }

    #[test]
    fn a_cut_never_digs_below_the_new_target_depth() {
        // R6 计划不吃掉请求，应用者按新目标与当前占用限制实际位移。
        let mut shift = PendingShift::default();
        let available = target_frames(300) + target_frames(50);
        let mut ring = crate::ring::PlaybackRing::new(RING_CAPACITY_FRAMES);
        ring.push(&vec![1; available * 2]);

        let (next, drop) = plan_setpoint_shift(-300, 600, &mut shift);

        assert_eq!(next, 300, "钳咬合不妨碍目标深度走满整笔");
        assert_eq!(drop, 14_400, "计划返回原始欠剪量，不按快照钳");
        assert_eq!(apply_read_cursor_shift(&mut ring, drop, next), 2_400);
        assert_eq!(ring.available_frames(), 14_400);
    }

    #[test]
    fn a_comfortable_buffer_takes_the_whole_cut_at_the_new_floor() {
        // R7 600 → 300、占用 700ms：新下界允许整剪 300ms，旧下界只允许 100ms。
        let mut shift = PendingShift::default();

        let (next, drop) = plan_setpoint_shift(-300, 600, &mut shift);
        let mut ring = crate::ring::PlaybackRing::new(RING_CAPACITY_FRAMES);
        ring.push(&vec![1; 33_600 * 2]);

        assert_eq!(drop, 14_400, "计划返回整笔欠剪量");
        assert_eq!(apply_read_cursor_shift(&mut ring, drop, next), 14_400);
        assert_eq!(ring.available_frames(), 19_200);
    }

    #[test]
    fn the_pad_debt_survives_a_partial_capacity_payment() {
        // R8 请求完整记债，本轮只能支付100ms，余下200ms保留。
        let mut shift = PendingShift::default();
        let (next, _) = plan_setpoint_shift(300, 300, &mut shift);
        assert_eq!(next, 600);
        assert_eq!(shift.take_pad(PLENTY, target_frames(100)), 4_800);
        assert_eq!(shift.take_pad(PLENTY, PLENTY), 9_600);
    }

    #[test]
    fn a_blocked_pad_keeps_the_whole_step_as_debt() {
        // R9 无余量时目标仍走满，债不会因暂时无法支付而丢失。
        let mut shift = PendingShift::default();
        let (next, drop) = plan_setpoint_shift(700, 200, &mut shift);
        assert_eq!(next, 900);
        assert_eq!(drop, 0);
        assert_eq!(shift.take_pad(PLENTY, 0), 0);
        assert_eq!(shift.pad_frames, 33_600);
    }

    #[test]
    fn a_reverse_cuts_only_the_already_paid_part() {
        // R10 同一动作段反向先抵未付债，只剪实际已经垫过的量。
        for paid in [0, 9_600] {
            let mut shift = PendingShift::default();
            plan_setpoint_shift(700, 200, &mut shift);
            assert_eq!(shift.take_pad(paid, PLENTY), paid);
            let (next, drop) = plan_setpoint_shift(-700, 900, &mut shift);
            let mut ring = crate::ring::PlaybackRing::new(RING_CAPACITY_FRAMES);
            ring.push(&vec![1; RING_CAPACITY_FRAMES * 2]);
            assert_eq!(drop, paid);
            assert_eq!(apply_read_cursor_shift(&mut ring, drop, next), paid);
            assert_eq!(shift.pad_frames, 0);
        }
    }

    /// 一个可用的跨机 offset。取非零即可:0 是「不可用」的编码约定,不兼作别的。
    const LIVE_OFFSET_TICKS: i64 = 5 * TICKS_PER_MS;

    /// 造一枚对齐参数格。下发侧在可行性判不过时把 D 与 offset 一起置零,
    /// 这里照那个形态构造,故两项分开传。
    fn alignment_cell(enabled: bool, d_ticks: i64, offset_ticks: i64) -> AlignmentCell {
        let cell = AlignmentCell::default();
        cell.set_enabled(enabled);
        cell.set_runtime(d_ticks, offset_ticks, 0);
        cell
    }

    #[test]
    fn a_dead_setpoint_domain_neither_steps_nor_moves_the_anchor() {
        // R11 闸关那一轮不动作,而且不动锚——后者才是要害。第三行:闸关期间 D 被下发侧
        // 置零,恢复时 D 回到真值;锚若在闸关那一轮跟着那个 0 挪过去,这一行就会得到一次
        // 凭空的反向阶跃,用户没拨过滑块而读游标跳一整段。
        //
        // 关态取「对齐开关关掉而 offset 仍在」的档位,是为了同时挡住把闸误绑到 offset
        // 单项上的写法——那种绑法在这一档会判成开。绑到开关上的那种写法由 R13 挡。
        let mut step = SetpointStep::default();
        let live = alignment_cell(true, SETPOINT_D, LIVE_OFFSET_TICKS);
        let dead = alignment_cell(false, 0, LIVE_OFFSET_TICKS);

        assert_eq!(
            live_setpoint_step(&mut step, &live, SETPOINT_TARGET_MS),
            0,
            "首轮只记锚"
        );
        assert_eq!(
            live_setpoint_step(&mut step, &dead, SETPOINT_TARGET_MS),
            0,
            "闸关那一轮不动作"
        );
        assert_eq!(
            live_setpoint_step(&mut step, &live, SETPOINT_TARGET_MS),
            0,
            "恢复时不得凭空产生阶跃"
        );
    }

    #[test]
    fn a_real_change_still_fires_once_the_domain_is_live_again() {
        // R12 闸不是把执行器关死。把闸写成恒关时 R11 三行全绿,只有这条与 R14 红。
        // 顺带钉住闸关期间用户真拨了滑块的处置:锚停在最后一个有效 D 上,恢复那一轮
        // 一次性补上,拨了多少就生效多少——闸推迟生效,不吞掉。
        let mut step = SetpointStep::default();
        live_setpoint_step(
            &mut step,
            &alignment_cell(true, SETPOINT_D, LIVE_OFFSET_TICKS),
            SETPOINT_TARGET_MS,
        );
        live_setpoint_step(
            &mut step,
            &alignment_cell(false, 0, LIVE_OFFSET_TICKS),
            SETPOINT_TARGET_MS,
        );

        let moved = alignment_cell(true, SETPOINT_D + 200 * TICKS_PER_MS, LIVE_OFFSET_TICKS);
        assert_eq!(
            live_setpoint_step(&mut step, &moved, SETPOINT_TARGET_MS),
            200
        );
    }

    #[test]
    fn the_switch_alone_does_not_open_the_setpoint_domain() {
        // R13 闸绑的是 offset 可用性,不是对齐开关。这一档正是下发侧判不过时的真实状态:
        // 开关仍开着(它是用户设置,可行性判不过不会去改它),而 D 与 offset 一起被置零。
        // 闸若误绑到开关上,这一档会判成开,于是那个「D 此刻不可用」的 0 被当成一次
        // 真阶跃执行下去——默认预算下就是 −250,缓冲当场被剪到下界。R14 是它的正面对照。
        let mut step = SetpointStep::default();
        live_setpoint_step(
            &mut step,
            &alignment_cell(true, SETPOINT_D, LIVE_OFFSET_TICKS),
            SETPOINT_TARGET_MS,
        );

        let judged_infeasible = alignment_cell(true, 0, 0);
        assert_eq!(
            live_setpoint_step(&mut step, &judged_infeasible, SETPOINT_TARGET_MS),
            0,
            "开关开着但 offset 不可用,仍算关"
        );
    }

    #[test]
    fn both_the_switch_and_the_offset_open_the_setpoint_domain() {
        // R14 R13 的正面对照。两条合起来才把闸钉成「offset 可用性」这个双条件:R13 说
        // offset 不可用即关,这条说开关与 offset 皆可用即开。两处的 cell 只差 offset
        // 一项,差别就摆在源码里看得见。
        let mut step = SetpointStep::default();
        live_setpoint_step(
            &mut step,
            &alignment_cell(true, SETPOINT_D, LIVE_OFFSET_TICKS),
            SETPOINT_TARGET_MS,
        );

        let moved = alignment_cell(true, SETPOINT_D + 120 * TICKS_PER_MS, LIVE_OFFSET_TICKS);
        assert_eq!(
            live_setpoint_step(&mut step, &moved, SETPOINT_TARGET_MS),
            120,
            "开关与 offset 皆可用即照常执行,前馈是精确 1:1"
        );
    }

    #[test]
    fn deep_buffer_makes_the_resampler_consume_more_input() {
        // 控制律的方向。drift_ratio 说的是「放快一点」（缓冲深 → 返回 > 1），
        // 而 rubato 的比率是 输出/输入：要放快就得多吃输入，比率必须变小。
        // 直接把 drift_ratio 交给 set_resample_ratio 会把负反馈接成正反馈——
        // 缓冲越深越不排空，深度一路涨到溢出丢帧，而这在真机上表现为
        // 「放了十几分钟后开始周期性卡顿」，极难归因到一个符号上。
        let deep = resample_ratio(48_000, 400.0, 200.0);
        let shallow = resample_ratio(48_000, 100.0, 200.0);

        assert!(deep < 1.0, "缓冲偏深应多吃输入，实际比率 {deep}");
        assert!(shallow > 1.0, "缓冲偏浅应少吃输入，实际比率 {shallow}");
    }

    #[test]
    fn resample_ratio_is_anchored_at_the_device_rate() {
        // 基准比率是 设备率/48000：44.1k 设备要把 48k 的流放慢成 44.1k 的帧数。
        let base = resample_ratio(44_100, 200.0, 200.0);

        assert!((base - 44_100.0 / 48_000.0).abs() < 1e-12, "实际 {base}");
    }

    #[test]
    fn drift_never_exceeds_the_relative_ratio_rubato_was_built_with() {
        // 漂移是在基准比率上的微调，而 rubato 构造时给的 MAX_RELATIVE_RATIO 是硬边界：
        // 超出它 set_resample_ratio 返回 Err，实时线程上表现为「不调速」——
        // 控制律静默失效，缓冲深度就再没有东西往回拉。
        //
        // 注意这个带是不对称的：漂移项取了倒数，故相对比率落在
        // [1/(1+d), 1/(1−d)] = [0.999001, 1.001001]，上侧比 1+d 略宽。
        let base = 44_100.0 / 48_000.0;
        let upper = 1.0 / (1.0 - MAX_DRIFT);
        let lower = 1.0 / (1.0 + MAX_DRIFT);

        for available in [0.0, 1.0, 200.0, 100_000.0, -1_000.0, f64::NAN] {
            let relative = resample_ratio(44_100, available, 200.0) / base;

            assert!(
                (lower..=upper).contains(&relative),
                "available={available} 时相对比率 {relative} 超出漂移带"
            );
            assert!(
                relative < MAX_RELATIVE_RATIO && relative > 1.0 / MAX_RELATIVE_RATIO,
                "available={available} 时相对比率 {relative} 会被 rubato 拒掉"
            );
        }
    }

    #[test]
    fn silent_frame_count_is_expressed_at_the_output_rate() {
        // 预填充期送出的零值帧是喂给频谱的，长度必须按 48k 解释。
        // 直接用设备帧数会在 >48k 的设备上越界读 staging——后者按 48k 输入帧分配。
        for (rate, buffer_frames) in [
            (44_100u32, 882usize),
            (48_000, 960),
            (88_200, 1_764),
            (96_000, 1_920),
            (192_000, 3_840),
        ] {
            let frames = output_frames_for(dev(buffer_frames), rate);
            assert_eq!(
                frames, 960,
                "{rate}Hz 的 20ms 缓冲应折算成 960 个 48k 帧，实际 {frames}"
            );
        }
    }

    #[test]
    fn output_rate_conversion_never_inflates_high_rate_devices() {
        // 上一条的失效形态：>48k 时设备帧数多于同时长的 48k 帧数，
        // 照设备帧数去索引按 48k 分配的缓冲就是越界 panic。
        assert!(output_frames_for(dev(1_920), 96_000) < 1_920);
        assert!(output_frames_for(dev(3_840), 192_000) < 3_840);
        assert_eq!(output_frames_for(dev(960), 48_000), 960);
        // 低速设备反向：同时长的 48k 帧数更多，故折算是放大。
        assert!(output_frames_for(dev(882), 44_100) > 882);
    }

    #[test]
    fn prefill_round_reports_silence_in_output_frames_within_the_buffer() {
        // 本条钉的是复审找出的 Critical。96kHz / 20ms 下设备帧数是 1920，
        // 而 staging 按 48k 输入帧分配只有约 1091 帧——照设备帧数索引就是越界 panic，
        // 且此时 render_start 已经返回过 OK，表现为静默无声。
        let mut state = PrefillState::new(target_frames(200));
        let max_input = 1_091;

        let frames = prefill_silence_frames(&mut state, 0, dev(1_920), 96_000, max_input)
            .expect("未达目标深度时应处于预填充");

        assert_eq!(frames, 960, "1920 个 96k 设备帧等于 960 个 48k 帧");
        assert!(frames <= max_input, "零值帧数不得超过 staging 容量");
    }

    #[test]
    fn prefill_silence_is_clamped_to_the_staging_bound() {
        // 换算结果仍可能超过上界（例如上界自身被算小），故必须夹。
        // 这是最后一道防线：越界发生在实时线程上，panic 会带走整个宿主进程。
        let mut state = PrefillState::new(target_frames(200));

        let frames =
            prefill_silence_frames(&mut state, 0, dev(4_800), 48_000, 100).expect("预填充");

        assert_eq!(frames, 100, "超过上界时必须夹到上界");
    }

    #[test]
    fn reaching_target_depth_ends_the_prefill_round() {
        let mut state = PrefillState::new(target_frames(200));

        assert!(prefill_silence_frames(&mut state, 9_600, dev(1_920), 96_000, 1_091).is_none());
    }

    #[test]
    fn ratio_one_maps_to_one_million_ppm() {
        assert_eq!(ratio_to_ppm(1.0), 1_000_000);
    }

    #[test]
    fn ratio_ppm_keeps_the_base_of_a_non_48k_device() {
        // 44.1k 设备的基准比率是 44100/48000 = 0.91875，即 918750 ppm。
        // 这条钉住的是「ppm 的分辨率够用」：判据要从 ppm 反推漂移项，
        // 而漂移项的量级只有千分之一，若 ppm 把基准比率也算糊了就无从反推。
        let base = 44_100.0 / 48_000.0;

        assert_eq!(ratio_to_ppm(base), 918_750);
    }

    #[test]
    fn ratio_ppm_resolves_the_drift_term() {
        // 控制律的相对幅度上界是 MAX_RELATIVE_RATIO，即偏离 1.0 最多一万 ppm。
        // 千分之一的漂移必须体现为 ppm 上可见的差，否则这个字段判不了符号。
        let with_drift = ratio_to_ppm(1.001);

        assert!(
            with_drift > 1_000_000,
            "千分之一的漂移应当在 ppm 上可见，实际 {with_drift}"
        );
        assert_eq!(with_drift, 1_001_000);
    }

    #[test]
    fn non_finite_ratio_yields_zero_ppm() {
        // 承 ring.rs 的 drift_ratio_tolerates_non_finite_inputs：非有限输入不得产生
        // 垃圾值。0 是「不可用」的哨兵，与「未起播」共用同一个值。
        assert_eq!(ratio_to_ppm(f64::NAN), 0);
        assert_eq!(ratio_to_ppm(f64::INFINITY), 0);
        assert_eq!(ratio_to_ppm(f64::NEG_INFINITY), 0);
        assert_eq!(ratio_to_ppm(0.0), 0);
        assert_eq!(ratio_to_ppm(-1.0), 0);
    }

    #[test]
    fn stats_cell_starts_at_zero_and_reads_back_what_was_written() {
        let cell = RenderStatsCell::default();
        let empty = cell.snapshot();
        assert_eq!(empty.device_sample_rate, 0, "未起播时设备率必须为 0");
        assert_eq!(empty.ring_frames, 0);

        cell.set_device_rate(48_000);
        cell.set_ring_frames(9_600);
        cell.set_ratio_ppm(1_000_000);
        cell.add_rendered(960);
        cell.note_underrun();
        cell.note_underrun();
        cell.note_hard_reset();
        cell.set_device_position(4_800, 123_456_789);
        cell.set_device_latency_us(10_000);
        // 负误差必须原样穿过快照。存成无符号会让它回绕成一个极大正值，
        // 而外环据符号决定往哪个方向调——符号丢了，调整方向就反了。
        cell.set_play_time_error_us(-2_500);
        cell.set_target_ms_current(320);
        cell.set_clock_offset_available(true);
        cell.set_device_buffer_frames(1_056);
        cell.set_device_clock_available(true);
        cell.note_swallowed_gap();
        cell.note_swallowed_gap();
        cell.note_swallowed_gap();
        cell.note_overlap();

        let s = cell.snapshot();
        assert_eq!(s.device_sample_rate, 48_000);
        assert_eq!(s.device_position_frames, 4_800);
        assert_eq!(s.device_position_qpc, 123_456_789);
        assert_eq!(s.device_latency_us, 10_000);
        assert_eq!(s.play_time_error_us, -2_500);
        assert!(s.play_time_error_us < 0, "负误差不得回绕成正值");
        assert_eq!(s.target_ms_current, 320);
        assert_eq!(s.clock_offset_available, 1);
        assert_eq!(s.device_buffer_frames, 1_056);
        assert_eq!(s.device_clock_available, 1);
        assert_eq!(s.ring_frames, 9_600);
        assert_eq!(s.resample_ratio_ppm, 1_000_000);
        assert_eq!(s.device_frames_rendered, 960);
        assert_eq!(s.underrun_count, 2, "欠载是累加的");
        assert_eq!(s.hard_reset_count, 1);
        // 与欠载不同次数：两个计数写串了（互相接反）时这里必红。
        assert_eq!(s.swallowed_gap_count, 3, "亚下限空档是累加的");
        assert_eq!(s.overlap_count, 1);
    }

    #[test]
    fn stats_cell_reset_clears_every_field() {
        // 起播时清零。若漏掉任一字段，上一次会话的计数会混进这一次，
        // 而「本次播放共欠载几次」这条判据就变成了历史累计，静默失真。
        let cell = RenderStatsCell::default();
        cell.set_device_rate(44_100);
        cell.set_ring_frames(1);
        cell.set_ratio_ppm(918_750);
        cell.add_rendered(1);
        cell.note_underrun();
        cell.note_hard_reset();
        cell.set_device_position(7, 8);
        cell.set_device_latency_us(9);
        cell.set_play_time_error_us(-10);
        cell.set_target_ms_current(320);
        cell.set_clock_offset_available(true);
        cell.set_device_buffer_frames(1056);
        cell.set_device_clock_available(true);
        cell.note_swallowed_gap();
        cell.note_overlap();

        cell.reset();

        let s = cell.snapshot();
        assert_eq!(s.ring_frames, 0);
        assert_eq!(s.underrun_count, 0);
        assert_eq!(s.hard_reset_count, 0);
        assert_eq!(s.device_frames_rendered, 0);
        assert_eq!(s.device_sample_rate, 0);
        assert_eq!(s.resample_ratio_ppm, 0);
        assert_eq!(s.device_position_frames, 0);
        assert_eq!(s.device_position_qpc, 0);
        assert_eq!(s.device_latency_us, 0);
        assert_eq!(s.play_time_error_us, 0);
        assert_eq!(s.target_ms_current, 0);
        assert_eq!(s.clock_offset_available, 0);
        assert_eq!(s.device_buffer_frames, 0);
        assert_eq!(s.device_clock_available, 0);
        // 计数语义照抄 underrun_count：起播时清零（reset 在 start 路径被调），
        // 停播不清。这里钉「起播清零」那一半。
        assert_eq!(s.swallowed_gap_count, 0);
        assert_eq!(s.overlap_count, 0);
    }

    #[test]
    fn render_stats_layout_has_no_padding() {
        // 布局判据。托管侧有一条对称的 Marshal.SizeOf 断言，两条都成立才说明两端一致。
        // 错位是静默的：读到的是别的字段的值，表现为「数值不对」，
        // 与「逻辑算错了」无从区分。
        // 十六个 8 字节字段，无 padding。混进一个 u32 只会产生尾部填充，
        // 而尾部填充的大小两端各自按对齐规则推——那是又一处不必存在的约定，
        // 故可用性标志也取 u64。
        assert_eq!(std::mem::size_of::<crate::RenderStats>(), 128);
        assert_eq!(std::mem::align_of::<crate::RenderStats>(), 8);

        // 大小与对齐不够，必须逐字段钉偏移，且两端各钉自己的。
        //
        // 变异实测出的缺口：把本侧两个字段的声明顺序对调，托管侧那张 OffsetOf 表全绿——
        // 它钉的是托管结构自己的布局，看不见本侧的顺序；而本侧原先只断言大小与对齐，
        // 对调不改大小。于是两端各自的判据都绿，两端的字段顺序已经不一致，
        // 而那正是这条判据声称要防的形态。两端各钉自己的偏移之后，任一侧动顺序都必红。
        use std::mem::offset_of;
        type S = crate::RenderStats;
        assert_eq!(offset_of!(S, ring_frames), 0);
        assert_eq!(offset_of!(S, underrun_count), 8);
        assert_eq!(offset_of!(S, hard_reset_count), 16);
        assert_eq!(offset_of!(S, device_frames_rendered), 24);
        assert_eq!(offset_of!(S, device_sample_rate), 32);
        assert_eq!(offset_of!(S, resample_ratio_ppm), 40);
        assert_eq!(offset_of!(S, device_position_frames), 48);
        assert_eq!(offset_of!(S, device_position_qpc), 56);
        assert_eq!(offset_of!(S, device_latency_us), 64);
        assert_eq!(offset_of!(S, play_time_error_us), 72);
        assert_eq!(offset_of!(S, target_ms_current), 80);
        assert_eq!(offset_of!(S, clock_offset_available), 88);
        assert_eq!(offset_of!(S, device_buffer_frames), 96);
        assert_eq!(offset_of!(S, device_clock_available), 104);
        assert_eq!(offset_of!(S, swallowed_gap_count), 112);
        assert_eq!(offset_of!(S, overlap_count), 120);
    }
}
