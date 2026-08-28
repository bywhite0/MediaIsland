//! 时间轴锚点：把 ring 里的累积第 N 帧映射回发送端时刻。
//!
//! 纯逻辑，不带 cfg 门——与 `convert` 同一分层，故它在任何平台都被编译与测试。
//!
//! 本模块只做两件事：帧数与 tick 的换算，以及「累积写入位置到发送端时刻」这条线性
//! 映射的一个锚点。设备位置、设备延迟、手动偏移都不在这里——它们要么来自 WASAPI
//! （带 cfg 门），要么来自配置。

use crate::OUTPUT_SAMPLE_RATE;

/// 每毫秒的 100 纳秒 tick 数。
///
/// 这是 native 侧「100ns tick」这个单位的唯一定义处。WASAPI 的 REFERENCE_TIME 用的
/// 也是它，故采集与播放两侧的缓冲时长常量都由这里导出。同一个单位分散成三个字面量时，
/// 改一处而漏两处不会让任何测试变红：单位不是数值，是几方之间的约定。
pub const TICKS_PER_MS: i64 = 10_000;

/// 每秒的 tick 数。由 [`TICKS_PER_MS`] 导出，不另写一个 10_000_000。
pub const TICKS_PER_SECOND: i64 = 1_000 * TICKS_PER_MS;

/// 帧数换 tick。48kHz 下每帧 625/3 tick，不是整数，故一律先乘后除。
///
/// 走 i128 而非 i64 有两个理由，后者才是决定性的。
///
/// 一是量程：入参可以是累积帧数，而累积帧数乘 10^7 在 i64 里只剩约 9.2e11 帧的余量，
/// 48kHz 下约合 222 天连续播放，而累积帧数从起播开始只增。
///
/// 二是调用方要把两个 u64 位置相减。i64 装不下 u64 的全值域，故「u64 之差」这个量
/// 本身就要求一个比 u64 更宽的有符号类型；i128 是同时容纳它与「再乘 10^7」的最窄选择。
pub fn frames_to_ticks(frames: i128) -> i128 {
    frames * i128::from(TICKS_PER_SECOND) / i128::from(OUTPUT_SAMPLE_RATE)
}

/// tick 换帧数。截断而非取整——见 [`gap_silence_frames`] 对亚帧空档的处置。
pub fn ticks_to_frames(ticks: i128) -> i128 {
    ticks * i128::from(OUTPUT_SAMPLE_RATE) / i128::from(TICKS_PER_SECOND)
}

/// 帧头毫秒量化的噪声带上界。
///
/// 发送端的 capturedAtMs 由两个毫秒级截断合成（墙钟读数取整，以及帧龄的整除），
/// 每一个各贡献不足 1 毫秒，故相邻两帧的时刻差带约 2 毫秒的量化噪声——这就是
/// 这个数的来历。它此前是 [`MIN_GAP_TICKS`] 注释里的一句散文，具名是因为诊断计数的
/// 下界要引用它：噪声的正半边每秒出现几十次，计数下界若从 0 起算，计数器就是
/// 每秒加几十的白噪声，什么也诊断不了。
pub const GAP_NOISE_TICKS: i64 = 2 * TICKS_PER_MS;

/// 算作真空档的下限。低于它的差值来自帧头的毫秒量化，不是丢帧。
///
/// 量化噪声的带宽见 [`GAP_NOISE_TICKS`]；而丢一帧是一个采集周期的空档，实测约
/// 10 毫秒。取 5 毫秒落在两者之间：噪声带上界的 2.5 倍，半个帧长。
///
/// 这条下限不是可省的优化，它补的是一处不对称。重叠一侧刻意返回 0（裁剪要改 PCM
/// 内容，是另一件事），若正向一侧连量化噪声一起补上，零均值的噪声就变成单边偏置：
/// 48kHz 下 1 毫秒即 48 帧静音，每秒补上百次，比它要修的时间轴压缩严重得多。
///
/// 代价也要记账：落在噪声带（[`GAP_NOISE_TICKS`]）与丢帧（约 10 毫秒）之间的
/// 一次性停顿——比如发送端 3–4 毫秒的调度毛刺——会被整段吞掉，越过它向过去外推的
/// 位置就偏那么多，单次最坏接近 5 毫秒（预算的一半），随这段数据流出缓冲窗口
/// （约 300 毫秒）而消失。它与量化噪声在观测上不可分，故不可免；排查机间错位时
/// 这项要对账——对账的载体是 [`Timeline::gap_before`] 携带的分类计数。
///
/// 采集周期短于约 7 毫秒的设备上这条线要重估——那时丢一帧的空档会落进噪声带里，
/// 补与不补都不再可分。它是一处常量，重估只改这里。
pub const MIN_GAP_TICKS: i64 = 5 * TICKS_PER_MS;

/// 两帧之间的空档要补多少静音帧。
///
/// expected_ticks 是上一帧末尾按时长推出的时刻，actual_ticks 是新帧自称的时刻。
/// 不补则 ring 里的样本数小于它代表的时间轴长度，出声时刻被永久提前那么多，
/// 且每次丢帧累加。现有规则只在超过 500 毫秒的不连续时硬重置，所以 500 毫秒
/// 以内的丢帧此前是静默压缩时间轴的。
///
/// 重叠（新帧早于预期）返回 0：不补，也不裁。裁剪要改 PCM 内容，那是另一件事，
/// 而重叠在正常发送端上不会出现。
///
/// 亚帧空档向下取整而非向上：补多了同样破坏时间轴守恒，而向下取整的残差不足一帧
/// （约 21 微秒），且下一帧的空档从新锚点重算，残差不累积。
///
/// 本函数是纯算术。量化噪声的判定不在这里而在 [`Timeline::gap_before`]——
/// 噪声是入参的性质，不是这个换算的性质。
pub fn gap_silence_frames(expected_ticks: i64, actual_ticks: i64) -> usize {
    let delta = i128::from(actual_ticks) - i128::from(expected_ticks);
    if delta <= 0 {
        return 0;
    }

    ticks_to_frames(delta)
        .max(0)
        .try_into()
        .unwrap_or(usize::MAX)
}

/// [`Timeline::gap_before`] 的返回：该补的静音帧数，加本帧相对预期时刻的分类。
///
/// 分类判定留在这里（纯函数）而计数的自增在 render 侧持 stats cell 的地方做，
/// 与「噪声是入参的性质，不是换算的性质」同一条分层原则。
pub struct GapDecision {
    /// 本帧之前该补的静音帧数。
    pub silence_frames: usize,
    /// 本帧相对预期时刻落在哪一档。
    pub kind: GapKind,
}

/// 帧间隔的诊断分类。
///
/// [`MIN_GAP_TICKS`] 的注释写着「排查机间错位时这项要对账」，而被吞掉的亚下限
/// 空档与被忽略的重叠此前都没有计数，无从对账——这个分类就是对账的凭据。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GapKind {
    /// 噪声带内的抖动，或已按空档全量补足的真空档。两者都不欠账，不必计数。
    Normal,
    /// 亚下限空档：差值落在 [`GAP_NOISE_TICKS`] 与 [`MIN_GAP_TICKS`] 之间
    /// （含下界，不含上界）。正是「3–4 毫秒调度毛刺被整段吞掉」那一档，
    /// 也是机间错位对账时唯一查不到的量。
    SwallowedGap,
    /// 重叠：新帧比预期早了至少一个 [`GAP_NOISE_TICKS`]。发送端时间轴倒走，
    /// 正常发送端不该有。
    Overlap,
}

/// 累积帧轴上的一个位置：播放会话自起点累计写入的第 N 帧，48k 域，含补进去的静音。
/// 只增——不随环形覆盖回退，也不随重置归零，它是时间轴坐标，不是缓冲下标。
///
/// 与设备帧轴（render 侧的 DeviceFrames，WASAPI 端点按自身混音率数的帧）是两条
/// 互不通约的轴：溢出丢最旧让读游标与设备消耗数恒差一段，重采样又让两侧一帧的
/// 时长不同。第 7 期两次跨轴混用都以毫秒级错位收场，而错出来的数看起来是正常的。
/// 故本类型不提供跨轴算术，也不实现 Deref、From 这类会重新打开混用面的转换；
/// 裸整数经 new 与 raw 只在边界与测试里出入，接线处一律传递已类型化的值。
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct CumulativeFrames(u64);

impl CumulativeFrames {
    /// 从裸整数进入累积轴。只该出现在边界与测试里。
    pub fn new(raw: u64) -> Self {
        Self(raw)
    }

    /// 离开累积轴回裸整数。只该出现在边界（诊断快照一类）与测试里。
    pub fn raw(self) -> u64 {
        self.0
    }

    /// 沿轴前进 frames 帧。位置加帧数还是位置，不换轴。
    pub fn advance(self, frames: u64) -> Self {
        Self(self.0 + frames)
    }

    /// 沿轴回退 frames 帧，饱和到轴原点。位置减帧数还是位置，不换轴。
    pub fn saturating_rewind(self, frames: u64) -> Self {
        Self(self.0.saturating_sub(frames))
    }

    /// 相对同轴另一位置的有符号帧差。两个 u64 之差装不进 i64，故给 i128——
    /// 恰好也是 frames_to_ticks 的入参宽度。
    pub fn offset_from(self, origin: Self) -> i128 {
        i128::from(self.0) - i128::from(origin.0)
    }
}

/// 单锚点时间轴。
#[derive(Default)]
pub struct Timeline {
    anchor: Option<Anchor>,
}

/// 一轮带时刻写入的锚点。
///
/// 存末端而不是起点：末端同时就是下一帧应有的起始时刻，于是空档判定与位置外推共用
/// 同一份状态；分开存两个时刻会出现两份各自为真的量。
///
/// 除末端之外还要存本轮的起点，因为锚点在数学上是一条无限延伸的直线，而它实际只描述
/// 本轮写进来的那段数据。没有起点就无法拒绝本轮之外的位置，见
/// [`Timeline::sender_ticks_at`]。
struct Anchor {
    /// 本轮带时刻写入的起点，累积帧轴上的位置。
    run_start: CumulativeFrames,
    /// 最近一帧数据末端在累积帧轴上的位置。
    end_frames: CumulativeFrames,
    /// 该末端对应的发送端时刻。
    end_ticks: i64,
}

impl Timeline {
    /// 本帧之前该补多少静音帧，以及本帧相对预期时刻的分类。
    ///
    /// 无锚点即流的第一帧，没有可比的前一帧，故补 0 且无从分类（归入正常）。
    pub fn gap_before(&self, sender_ticks: i64) -> GapDecision {
        let Some(anchor) = self.anchor.as_ref() else {
            return GapDecision {
                silence_frames: 0,
                kind: GapKind::Normal,
            };
        };
        let expected = anchor.end_ticks;
        let delta = i128::from(sender_ticks) - i128::from(expected);

        if delta >= i128::from(MIN_GAP_TICKS) {
            // 真空档按实测全量补足，不欠账。
            return GapDecision {
                silence_frames: gap_silence_frames(expected, sender_ticks),
                kind: GapKind::Normal,
            };
        }
        if delta >= i128::from(GAP_NOISE_TICKS) {
            return GapDecision {
                silence_frames: 0,
                kind: GapKind::SwallowedGap,
            };
        }
        if delta <= -i128::from(GAP_NOISE_TICKS) {
            return GapDecision {
                silence_frames: 0,
                kind: GapKind::Overlap,
            };
        }

        GapDecision {
            silence_frames: 0,
            kind: GapKind::Normal,
        }
    }

    /// 记一次写入。position 是本帧数据起点在累积帧轴上的位置（已含补进去的静音），
    /// sender_ticks 是本帧自称的时刻，frames 是本帧的数据帧数。
    ///
    /// 每次 push 都必须调用。「每次」是必须的，不是优化：只在不连续时更新锚点，
    /// 外推距离就从缓冲深度（约 300 毫秒，误差 0.06 毫秒）变成整段播放时长——
    /// 60 秒即 12 毫秒，直接击穿 10 毫秒的对齐预算。
    pub fn note_write(&mut self, position: CumulativeFrames, sender_ticks: i64, frames: usize) {
        let frames = frames as u64;
        let end_ticks = i128::from(sender_ticks) + frames_to_ticks(i128::from(frames));
        let Ok(end_ticks) = i64::try_from(end_ticks) else {
            // 末端时刻已经表达不出，锚点宁可为空：留一个回绕后的锚点，
            // 此后每一次外推都会给出一个看起来正常的错时刻。
            self.anchor = None;
            return;
        };

        self.anchor = Some(Anchor {
            // 本轮起点：锚点为空即本帧是本轮的第一帧，此后沿用不变。
            run_start: match self.anchor.as_ref() {
                Some(anchor) => anchor.run_start,
                None => position,
            },
            end_frames: position.advance(frames),
            end_ticks,
        });
    }

    /// 累积第 cumulative_frames 帧对应的发送端时刻。
    ///
    /// 入参通常小于锚点末端——读游标与设备位置都落后于写入位置，差值即缓冲深度。
    /// 故这里是向过去外推，delta 为负是常态而非异常。
    ///
    /// 返回 None 有三种情形，对调用方是同一件事（没有可用答案）：还没有锚点；
    /// 入参落在本轮锚点的管辖范围之外；或外推结果已经落在 i64 之外。
    ///
    /// 第二种是必须挡的。锚点在数学上是一条无限延伸的直线，但它只描述本轮写进来的数据。
    /// 有两条路会造出「已在累积轴上、却不属于本轮」的位置：一是 reset 之后——累积坐标
    /// 刻意保留而锚点作废，此后第一次带时刻写入把锚点钉在当时的写入位置，于它之前的
    /// 位置全属上一轮；二是带时刻与不带时刻的写入混用——不带时刻的那段样本仍在缓冲里
    /// 可读，而它没有任何时刻记账。对这两种位置外推，得到的是一个看起来正常的错时刻，
    /// 而调用方无从分辨（`is_anchored` 此时为真，锚点是新的）。宁可没有答案。
    pub fn sender_ticks_at(&self, cumulative_frames: CumulativeFrames) -> Option<i64> {
        let anchor = self.anchor.as_ref()?;
        if cumulative_frames < anchor.run_start {
            return None;
        }

        let delta = cumulative_frames.offset_from(anchor.end_frames);
        i64::try_from(i128::from(anchor.end_ticks) + frames_to_ticks(delta)).ok()
    }

    /// 有锚点即可外推。
    pub fn is_anchored(&self) -> bool {
        self.anchor.is_some()
    }

    /// 硬重置或换曲后清空。留着旧锚点会让重置后的第一次外推跨越整个空档。
    pub fn reset(&mut self) {
        self.anchor = None;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 一帧 10 毫秒，与实测的采集帧长同量级。
    const FRAME_FRAMES: usize = 480;
    const FRAME_TICKS: i64 = 10 * TICKS_PER_MS;

    fn frames_u64(frames: usize) -> u64 {
        u64::try_from(frames).unwrap()
    }

    /// 测试侧进累积轴的唯一入口，省得每处铺开构造函数。
    fn at(raw: u64) -> CumulativeFrames {
        CumulativeFrames::new(raw)
    }

    #[test]
    fn tick_unit_is_expressed_once() {
        // 与本 crate 的输出率对照，钉住「一秒的 tick 数与一秒的帧数说的是同一秒」。
        // （曾有一条 TICKS_PER_SECOND == 1_000 * TICKS_PER_MS：它逐字重复 :19 的定义式，
        // 改常量时定义与断言一起变，挡不住任何错法，已删。）
        assert_eq!(
            frames_to_ticks(i128::from(OUTPUT_SAMPLE_RATE)),
            i128::from(TICKS_PER_SECOND)
        );
    }

    #[test]
    fn frames_to_ticks_multiplies_before_dividing() {
        // 先除后乘的实现会让不足一秒的帧数直接归零。
        assert_eq!(frames_to_ticks(1), 208);
        // 48kHz 下每帧 625/3 tick，3 的倍数上是精确的。
        assert_eq!(frames_to_ticks(3), 625);
        assert_eq!(
            frames_to_ticks(i128::from(FRAME_FRAMES as i64)),
            i128::from(FRAME_TICKS)
        );
    }

    #[test]
    fn whole_milliseconds_map_to_whole_frames() {
        // 帧头的时刻是毫秒量化的，而 48kHz 下 1 毫秒恰为 48 帧。
        // 这条事实是空档换算不产生残差的地基。
        assert_eq!(ticks_to_frames(i128::from(TICKS_PER_MS)), 48);
        assert_eq!(
            ticks_to_frames(i128::from(TICKS_PER_SECOND)),
            i128::from(OUTPUT_SAMPLE_RATE)
        );
    }

    #[test]
    fn frame_count_survives_the_round_trip() {
        for frames in [1i128, 2, 3, 47, 480, 48_000, 1_234_567] {
            let back = ticks_to_frames(frames_to_ticks(frames));
            // 钉到恰好：48kHz 下一帧是 625/3 tick，3 的倍数换算精确，非 3 的倍数
            // 截断恰亏 1。旧写法（back == frames || back == frames - 1）下
            // 一个恒少一帧的实现会全绿。
            let expected = if frames % 3 == 0 { frames } else { frames - 1 };
            assert_eq!(back, expected, "frames={frames}");
        }
    }

    #[test]
    fn unanchored_timeline_has_no_answer() {
        let timeline = Timeline::default();
        assert!(!timeline.is_anchored());
        assert_eq!(timeline.sender_ticks_at(at(0)), None);
        // 也没有可比的前一帧，故不补静音、无从分类。
        let decision = timeline.gap_before(1_000_000);
        assert_eq!(decision.silence_frames, 0);
        assert_eq!(decision.kind, GapKind::Normal);
    }

    #[test]
    fn extrapolation_runs_into_the_past() {
        let mut timeline = Timeline::default();
        timeline.note_write(at(0), 5_000_000, FRAME_FRAMES);

        // 锚点在本帧末端，故本帧起点处读回它自称的时刻，末端处晚一个帧长。
        assert_eq!(timeline.sender_ticks_at(at(0)), Some(5_000_000));
        assert_eq!(
            timeline.sender_ticks_at(at(frames_u64(FRAME_FRAMES))),
            Some(5_000_000 + FRAME_TICKS)
        );
        // 再往锚点之前退一帧，得到的时刻必须比本帧起点更早。
        assert_eq!(
            timeline.sender_ticks_at(at(0)).unwrap() - FRAME_TICKS,
            5_000_000 - FRAME_TICKS
        );
    }

    #[test]
    fn anchor_sits_at_the_end_of_the_written_data() {
        // 锚点若存成本帧起点，紧邻的下一帧就会被判成一整帧的空档。
        let mut timeline = Timeline::default();
        timeline.note_write(at(0), 0, FRAME_FRAMES);

        assert_eq!(timeline.gap_before(FRAME_TICKS).silence_frames, 0);
    }

    #[test]
    fn reset_drops_the_anchor() {
        let mut timeline = Timeline::default();
        timeline.note_write(at(0), 7_000_000, FRAME_FRAMES);
        timeline.reset();

        assert!(!timeline.is_anchored());
        assert_eq!(timeline.sender_ticks_at(at(0)), None);
    }

    #[test]
    fn gap_of_one_lost_frame_is_filled_in_full() {
        // 丢一帧：新帧自称的时刻比预期晚一个帧长。
        assert_eq!(gap_silence_frames(0, FRAME_TICKS), FRAME_FRAMES);
    }

    #[test]
    fn contiguous_and_overlapping_frames_fill_nothing() {
        assert_eq!(gap_silence_frames(0, 0), 0);
        // 重叠不裁：裁剪要改 PCM 内容，那是另一件事。
        assert_eq!(gap_silence_frames(FRAME_TICKS, 0), 0);
    }

    #[test]
    fn sub_frame_gap_floors_to_nothing() {
        // 一帧是 625/3 tick，208 tick 还不足一帧。
        assert_eq!(gap_silence_frames(0, 208), 0);
        assert_eq!(gap_silence_frames(0, 209), 1);
    }

    #[test]
    fn quantization_noise_is_below_the_gap_floor() {
        // 帧头的毫秒量化让相邻两帧的差值带约 2 毫秒噪声。纯算术会把它当空档补上，
        // 而空档判定必须不补：正向补、负向不裁会把零均值噪声变成单边偏置。
        let mut timeline = Timeline::default();
        timeline.note_write(at(0), 0, FRAME_FRAMES);

        let noise = 2 * TICKS_PER_MS;
        assert!(gap_silence_frames(FRAME_TICKS, FRAME_TICKS + noise) > 0);
        assert_eq!(timeline.gap_before(FRAME_TICKS + noise).silence_frames, 0);
    }

    #[test]
    fn gap_noise_ticks_is_pinned_to_its_literal() {
        // 2ms 噪声带上界的字面锚，防系数或 TICKS_PER_MS 误改静默漂移。
        assert_eq!(GAP_NOISE_TICKS, 20_000);
    }

    #[test]
    fn a_gap_at_the_floor_is_filled_in_full_not_docked() {
        // 判定用下限，补的量仍是实测全量：下限是「是不是空档」的门，不是要减掉的偏置。
        let mut timeline = Timeline::default();
        timeline.note_write(at(0), 0, FRAME_FRAMES);

        let at_floor = FRAME_TICKS + MIN_GAP_TICKS;
        let filled = timeline.gap_before(at_floor);
        assert_eq!(
            filled.silence_frames,
            usize::try_from(ticks_to_frames(i128::from(MIN_GAP_TICKS))).unwrap()
        );
        // 全量补足的真空档不欠账，不该进任何计数。
        assert_eq!(filled.kind, GapKind::Normal);
        // 恰在下限上要补，差一个 tick 就不补——成对钉住这条边界的两侧。
        assert_eq!(timeline.gap_before(at_floor - 1).silence_frames, 0);
    }

    #[test]
    fn the_swallowed_band_is_pinned_on_both_edges() {
        // 亚下限空档的判定区间是 [GAP_NOISE_TICKS, MIN_GAP_TICKS)。四个边界逐个钉：
        // 下界左移一个 tick 就把量化噪声计进来（每秒加几十的白噪声，什么也诊断不了），
        // 上界右移一个 tick 就把已全量补足的真空档重复计一次。
        let mut timeline = Timeline::default();
        timeline.note_write(at(0), 0, FRAME_FRAMES);

        // 噪声带内（差一个 tick 不到下界）：不计。计数下界从 0 起算的实现在这里红。
        assert_eq!(
            timeline.gap_before(FRAME_TICKS + GAP_NOISE_TICKS - 1).kind,
            GapKind::Normal
        );
        // 恰在下界上：计。
        assert_eq!(
            timeline.gap_before(FRAME_TICKS + GAP_NOISE_TICKS).kind,
            GapKind::SwallowedGap
        );
        // 差一个 tick 不到真空档下限：仍是被吞的那一档，且确实没补。
        let just_below_floor = timeline.gap_before(FRAME_TICKS + MIN_GAP_TICKS - 1);
        assert_eq!(just_below_floor.kind, GapKind::SwallowedGap);
        assert_eq!(just_below_floor.silence_frames, 0);
        // 恰在真空档下限上：全量补足，归入正常。
        assert_eq!(
            timeline.gap_before(FRAME_TICKS + MIN_GAP_TICKS).kind,
            GapKind::Normal
        );
    }

    #[test]
    fn overlap_is_classified_only_beyond_the_noise_band() {
        // 重叠一侧同样不能数噪声：量化噪声零均值，负半边与正半边一样每秒几十次。
        let mut timeline = Timeline::default();
        timeline.note_write(at(0), 0, FRAME_FRAMES);

        // 噪声带内的倒走：不计。
        assert_eq!(
            timeline
                .gap_before(FRAME_TICKS - (GAP_NOISE_TICKS - 1))
                .kind,
            GapKind::Normal
        );
        // 恰倒走一个噪声带：重叠。不补也不裁（裁剪要改 PCM 内容，是另一件事）。
        let overlap = timeline.gap_before(FRAME_TICKS - GAP_NOISE_TICKS);
        assert_eq!(overlap.kind, GapKind::Overlap);
        assert_eq!(overlap.silence_frames, 0);
        // 严格连续（差值恰为零）：正常。
        assert_eq!(timeline.gap_before(FRAME_TICKS).kind, GapKind::Normal);
    }

    #[test]
    fn long_playback_stays_within_a_tenth_of_a_millisecond() {
        // 每次 push 都更新锚点，故外推距离恒为缓冲深度而非整段播放时长。
        //
        // 模拟里必须带钟差，否则这条判据是假通过的：发送端时刻与本机帧计数严格等速时，
        // 从哪个锚点外推都得到同一个答案，「距离」不影响结果，冻住锚点的实现照样全绿。
        // 200ppm 是采集设备时钟与发送端墙钟之间的常规量级。
        let mut timeline = Timeline::default();
        let buffered_frames = 300 * 48; // 约 300 毫秒缓冲深度
        let rounds = 6_000u64; // 60 秒，每帧 10 毫秒
        let drifted_frame_ticks = FRAME_TICKS + FRAME_TICKS * 200 / 1_000_000;

        for round in 0..rounds {
            timeline.note_write(
                at(round * frames_u64(FRAME_FRAMES)),
                i64::try_from(round).unwrap() * drifted_frame_ticks,
                FRAME_FRAMES,
            );
        }

        // 真值来自数据本身：读游标落在第几帧，那一帧自称的时刻就是答案。
        let read_cursor = rounds * frames_u64(FRAME_FRAMES) - buffered_frames;
        let round_at_cursor = i64::try_from(read_cursor / frames_u64(FRAME_FRAMES)).unwrap();
        let want = round_at_cursor * drifted_frame_ticks;
        let got = timeline.sender_ticks_at(at(read_cursor)).unwrap();

        // 缓冲深度上的 200ppm 即 0.06 毫秒；整段播放时长上的 200ppm 是 12 毫秒。
        assert!(
            (got - want).abs() < TICKS_PER_MS / 10,
            "60 秒后外推误差 {} tick，超过 0.1 毫秒",
            got - want
        );
    }

    #[test]
    fn an_unrepresentable_extrapolation_has_no_answer() {
        // 锚点贴着 i64 上界时向未来外推。回绕会给出一个看起来正常的错时刻，
        // 那比没有答案坏得多。
        let mut timeline = Timeline::default();
        timeline.note_write(at(0), i64::MAX - 1, 0);
        assert_eq!(timeline.sender_ticks_at(at(u64::MAX)), None);

        // 末端时刻本身就溢出时锚点为空，而不是留一个回绕后的锚点。
        let mut overflowing = Timeline::default();
        overflowing.note_write(at(0), i64::MAX, FRAME_FRAMES);
        assert!(!overflowing.is_anchored());
    }
}
