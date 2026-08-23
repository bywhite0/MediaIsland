//! 播放侧的环形缓冲与漂移控制律。
//!
//! 与采集侧的队列取向相反：采集队列满则丢最旧，因为可视化只关心现在在响什么；
//! 播放这一侧丢最旧同样正确，但理由不同——积压意味着本机放得比上游发得慢，
//! 保留最新才能追上，保留最旧只会让延迟永久累积。

use crate::timeline::Timeline;

/// 交错立体声。由 crate 根的 [`crate::OUTPUT_CHANNELS`] 导出而非另写一个 2——
/// 两份声明各自为真时，声道数一旦变更，本模块仍按 2 解释交错布局，每帧都会错位，
/// 而这种错位没有任何测试抓得到（改成导出是恒等变换，验不出来的正是它要防的事）。
const CHANNELS: usize = crate::OUTPUT_CHANNELS as usize;

/// 漂移比率的单边上限。±0.1% 无可听伪声，是网络音频的常规量级。
pub const MAX_DRIFT: f64 = 0.001;

pub struct PlaybackRing {
    /// 交错样本，长度 = capacity_frames * CHANNELS。
    buffer: Vec<i16>,
    capacity_frames: usize,
    /// 下一个写入帧的下标（对 capacity_frames 取模）。
    write: usize,
    /// 当前可读帧数。写满后不再增长，读游标随丢弃前移。
    filled: usize,
    /// 累积写入帧数，含补进去的静音。
    ///
    /// 只增：不随环形覆盖回退，也不随 [`PlaybackRing::reset`] 归零——它是时间轴坐标，
    /// 不是缓冲下标。归零会让它与仍在推进的设备位置错开一整段，而那种错开没有任何
    /// 症状可循，只表现为出声时刻算错。
    written: u64,
    /// 写入位置到发送端时刻的映射。
    ///
    /// 与样本同处一把锁下而不是另立一处：锚点描述的正是这些样本，分开存时两者会在
    /// 并发写入下错配，而错配后算出的时刻仍然是个正常数字。
    timeline: Timeline,
}

impl PlaybackRing {
    pub fn new(capacity_frames: usize) -> Self {
        // 夹到至少 1 帧：容量为 0 会让 push/read 里的取模直接除零 panic。
        let capacity_frames = capacity_frames.max(1);
        Self {
            buffer: vec![0; capacity_frames * CHANNELS],
            capacity_frames,
            write: 0,
            filled: 0,
            written: 0,
            timeline: Timeline::default(),
        }
    }

    pub fn available_frames(&self) -> usize {
        self.filled
    }

    /// 累积写入帧数，即时间轴上的写入位置。
    pub fn written_frames(&self) -> u64 {
        self.written
    }

    /// 读游标在累积轴上的位置：下一个交给设备的样本是累积第几帧。
    ///
    /// 这不等于设备已消耗的帧数。溢出丢最旧会跳过若干个写入位置，此后两者恒差那么多；
    /// 而重采样又让设备侧的一帧与这里的一帧长度不同。要问「即将出声的样本来自发送端
    /// 何时」，只有这个位置对得上锚点。
    pub fn read_cursor_frames(&self) -> u64 {
        self.written - self.filled as u64
    }

    /// 累积第 cumulative_frames 帧对应的发送端时刻，无锚点时为 None。
    pub fn sender_ticks_at(&self, cumulative_frames: u64) -> Option<i64> {
        self.timeline.sender_ticks_at(cumulative_frames)
    }

    /// 时间轴是否已有锚点。
    pub fn is_anchored(&self) -> bool {
        self.timeline.is_anchored()
    }

    pub fn reset(&mut self) {
        self.write = 0;
        self.filled = 0;
        // 锚点随样本一起作废：留着它，重置后的第一次外推会跨越整个空档。
        // written 不归零，理由见该字段的注释。
        self.timeline.reset();
    }

    /// 追加交错样本，不带时刻。
    ///
    /// 锚点随之作废。混用两种写入时，锚点会一边过期一边继续被外推，算出的时刻
    /// 看起来正常，而实际偏了整段没记账的数据——宁可没有答案。
    pub fn push(&mut self, interleaved: &[i16]) {
        self.timeline.reset();
        self.write_interleaved(interleaved);
    }

    /// 追加交错样本，并带上本帧自称的发送端时刻。
    ///
    /// 三步的顺序不能改：先按空档补静音，再写真实样本，最后记锚点。先记锚点再补静音，
    /// 锚点的帧数就少算了补进去的那一段，此后每一次外推都偏那么多。
    pub fn push_at(&mut self, interleaved: &[i16], sender_ticks: i64) {
        let gap = self.timeline.gap_before(sender_ticks);
        if gap >= self.capacity_frames {
            // 空档已超过整个缓冲能表达的长度：补进去的静音会把仍要播的样本全部挤掉，
            // 故补与清空等价，而清空还省掉一整轮无用写入。本帧就是新流的第一帧。
            self.reset();
        } else {
            self.write_silence(gap);
        }

        let position = self.written;
        self.write_interleaved(interleaved);
        self.timeline
            .note_write(position, sender_ticks, interleaved.len() / CHANNELS);
    }

    /// 末尾不足一帧的残样本丢弃——错位成右声道会让整条流的声道翻转。
    fn write_interleaved(&mut self, interleaved: &[i16]) {
        let frames = interleaved.len() / CHANNELS;
        for frame in 0..frames {
            let src = frame * CHANNELS;
            self.write_frame(&interleaved[src..src + CHANNELS]);
        }
    }

    fn write_silence(&mut self, frames: usize) {
        for _ in 0..frames {
            self.write_frame(&[0i16; CHANNELS]);
        }
    }

    fn write_frame(&mut self, frame: &[i16]) {
        let dst = self.write * CHANNELS;
        self.buffer[dst..dst + CHANNELS].copy_from_slice(frame);

        self.write = (self.write + 1) % self.capacity_frames;
        if self.filled < self.capacity_frames {
            self.filled += 1;
        }
        // filled 已满时不增长：写游标前移即等价于丢弃最旧的一帧。
        self.written += 1;
    }

    /// 取出 `out` 能装下的帧数。不足的部分填零并返回实际取到的帧数。
    ///
    /// 填零而非重复上一帧：重复会产生蜂鸣，静音只是听感上的一个空隙。
    pub fn read_into(&mut self, out: &mut [i16]) -> usize {
        let wanted = out.len() / CHANNELS;
        let taken = wanted.min(self.filled);

        // 读游标是「写游标往回数 filled 帧」，不单独维护——两个游标各自维护时，
        // 溢出丢弃要同时改两个，漏一个就错位。
        let start = (self.write + self.capacity_frames - self.filled) % self.capacity_frames;

        for frame in 0..taken {
            let src = ((start + frame) % self.capacity_frames) * CHANNELS;
            let dst = frame * CHANNELS;
            out[dst..dst + CHANNELS].copy_from_slice(&self.buffer[src..src + CHANNELS]);
        }

        for sample in out.iter_mut().skip(taken * CHANNELS) {
            *sample = 0;
        }

        self.filled -= taken;
        taken
    }
}

/// 按缓冲填充度算重采样比率。
///
/// 缓冲比目标深说明本机放得慢，要放快一点（比率 > 1）把深度拉回目标；反之放慢。
/// 夹紧到 ±MAX_DRIFT 是必须的：硬重置前后误差可以远超目标深度，
/// 不夹紧会让比率跳到可听的量级。
pub fn drift_ratio(available_ms: f64, target_ms: f64) -> f64 {
    // 非有限输入一律回中性比率：NaN 传进重采样器会污染整条输出流，
    // 而这里没有比「不调速」更安全的选择。
    if target_ms <= 0.0 || !target_ms.is_finite() || !available_ms.is_finite() {
        return 1.0;
    }

    let error = (available_ms - target_ms) / target_ms;
    1.0 + error.clamp(-1.0, 1.0) * MAX_DRIFT
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::timeline::{ticks_to_frames, TICKS_PER_MS};

    const CH: usize = 2;

    fn stereo(frames: &[(i16, i16)]) -> Vec<i16> {
        frames.iter().flat_map(|&(l, r)| [l, r]).collect()
    }

    #[test]
    fn push_then_read_returns_same_order() {
        let mut ring = PlaybackRing::new(8);
        ring.push(&stereo(&[(1, -1), (2, -2), (3, -3)]));

        let mut out = vec![0i16; 3 * CH];
        assert_eq!(ring.read_into(&mut out), 3);
        assert_eq!(out, stereo(&[(1, -1), (2, -2), (3, -3)]));
        assert_eq!(ring.available_frames(), 0);
    }

    #[test]
    fn read_wraps_around_the_end() {
        // 跨回绕：物理布局断开时起点或掩码算错的实现，单次写入的用例仍会全绿。
        let mut ring = PlaybackRing::new(4);
        ring.push(&stereo(&[(1, 1), (2, 2), (3, 3)]));
        let mut drain = vec![0i16; 2 * CH];
        ring.read_into(&mut drain);
        ring.push(&stereo(&[(4, 4), (5, 5)]));

        let mut out = vec![0i16; 3 * CH];
        assert_eq!(ring.read_into(&mut out), 3);
        assert_eq!(out, stereo(&[(3, 3), (4, 4), (5, 5)]));
    }

    #[test]
    fn overflow_drops_oldest() {
        // 丢最旧而非拒绝新帧：积压的历史帧已经过期，最新的才对得上现在在响什么。
        let mut ring = PlaybackRing::new(2);
        ring.push(&stereo(&[(1, 1), (2, 2), (3, 3)]));

        assert_eq!(ring.available_frames(), 2);
        let mut out = vec![0i16; 2 * CH];
        ring.read_into(&mut out);
        assert_eq!(out, stereo(&[(2, 2), (3, 3)]));
    }

    #[test]
    fn underrun_fills_zeros_not_repeats() {
        // 重复上一帧会产生蜂鸣，静音不会。
        let mut ring = PlaybackRing::new(8);
        ring.push(&stereo(&[(7, 7)]));

        let mut out = vec![99i16; 3 * CH];
        assert_eq!(ring.read_into(&mut out), 1);
        assert_eq!(out, stereo(&[(7, 7), (0, 0), (0, 0)]));
    }

    #[test]
    fn reset_discards_everything() {
        let mut ring = PlaybackRing::new(8);
        ring.push(&stereo(&[(1, 1), (2, 2)]));
        ring.reset();

        assert_eq!(ring.available_frames(), 0);
        // 预填非零：全欠载时必须把整个 out 写成静音。WASAPI 交回来的缓冲带着上一轮残留，
        // 不填零等于把上一轮音频原样重播一遍。
        let mut out = vec![99i16; CH];
        assert_eq!(ring.read_into(&mut out), 0);
        assert_eq!(out, vec![0i16; CH]);
    }

    #[test]
    fn odd_sample_count_is_ignored_at_the_tail() {
        // 交错立体声，样本数必为偶数。奇数说明上游有误，末尾残样本丢弃而非错位成右声道。
        //
        // 必须把数据读回来比对：只查帧数的话，「整段右移一个样本」这类错位仍然让帧数为 1，
        // 而它的失效形态不是丢一帧，是该次 push 的每一帧左右声道互换。
        let mut ring = PlaybackRing::new(8);
        ring.push(&[1, -1, 5]);

        assert_eq!(ring.available_frames(), 1);
        let mut out = vec![0i16; CH];
        assert_eq!(ring.read_into(&mut out), 1);
        assert_eq!(out, stereo(&[(1, -1)]));
    }

    #[test]
    fn frame_after_odd_tail_stays_aligned() {
        // 残样本不能留着跟下一次 push 拼起来——那会让此后每一帧都左右互换，
        // 且错位会一直传下去，比丢一个样本严重得多。
        let mut ring = PlaybackRing::new(8);
        ring.push(&[1, -1, 5]);
        ring.push(&stereo(&[(2, -2)]));

        let mut out = vec![0i16; 2 * CH];
        assert_eq!(ring.read_into(&mut out), 2);
        assert_eq!(out, stereo(&[(1, -1), (2, -2)]));
    }

    #[test]
    fn zero_capacity_is_clamped_to_one_frame() {
        // 容量 0 会让 push / read_into 里的取模直接除零 panic。
        let mut ring = PlaybackRing::new(0);
        ring.push(&stereo(&[(1, 1), (2, 2)]));

        assert_eq!(ring.available_frames(), 1);
        let mut out = vec![0i16; CH];
        assert_eq!(ring.read_into(&mut out), 1);
        assert_eq!(out, stereo(&[(2, 2)]));
    }

    #[test]
    fn drift_ratio_is_one_at_target() {
        assert!((drift_ratio(200.0, 200.0) - 1.0).abs() < 1e-12);
    }

    #[test]
    fn drift_ratio_speeds_up_when_buffer_is_deep() {
        // 缓冲偏深说明本机放得比上游发得慢，要放快一点把深度拉回目标。
        assert!(drift_ratio(400.0, 200.0) > 1.0);
    }

    #[test]
    fn drift_ratio_slows_down_when_buffer_is_shallow() {
        assert!(drift_ratio(100.0, 200.0) < 1.0);
    }

    #[test]
    fn drift_ratio_is_clamped_to_one_permille() {
        // 硬重置前后 e 可以远超 target。不夹紧会让比率跳到可听的量级。
        //
        // 两边的性质不同：上界生产必然触发（target 可低至 50ms，而缓冲深度可达 2000ms）；
        // 下界纯防御——available≥0 且 target>0 时 e≥-1 恒成立，故只有负 available 才验得到它。
        // 用 (0.0, 200.0) 验下界是无效的：那时 e 恰为 -1，正落在夹紧边界上，松开也不变。
        assert!((drift_ratio(100_000.0, 200.0) - (1.0 + MAX_DRIFT)).abs() < 1e-12);
        assert!((drift_ratio(-1000.0, 200.0) - (1.0 - MAX_DRIFT)).abs() < 1e-12);
        assert!((drift_ratio(0.0, 200.0) - (1.0 - MAX_DRIFT)).abs() < 1e-12);
    }

    #[test]
    fn drift_ratio_tolerates_non_finite_inputs() {
        // NaN 会顺着比率污染整条输出流，而这里没有比「不调速」更安全的选择。
        assert_eq!(drift_ratio(f64::NAN, 200.0), 1.0);
        assert_eq!(drift_ratio(f64::INFINITY, 200.0), 1.0);
        assert_eq!(drift_ratio(100.0, f64::NAN), 1.0);
        assert_eq!(drift_ratio(100.0, f64::INFINITY), 1.0);
    }

    #[test]
    fn drift_ratio_tolerates_zero_target() {
        // 目标为零是配置错误，但不该产生 NaN 送进重采样器。
        assert_eq!(drift_ratio(50.0, 0.0), 1.0);
    }

    /// 一帧 10 毫秒，与实测的采集帧长同量级。
    const FRAME: usize = 480;
    const FRAME_TICKS: i64 = 10 * TICKS_PER_MS;

    /// 非零样本，为的是让补进去的静音在读回时可分辨。
    fn tone(frames: usize) -> Vec<i16> {
        vec![7i16; frames * CH]
    }

    #[test]
    fn an_untimed_push_leaves_no_anchor() {
        // 原路径：不带时刻的写入照旧只搬样本，时间轴无从建立。
        let mut ring = PlaybackRing::new(8);
        ring.push(&stereo(&[(1, 1), (2, 2)]));

        assert_eq!(ring.written_frames(), 2);
        assert!(!ring.is_anchored());
        assert_eq!(ring.sender_ticks_at(0), None);
    }

    #[test]
    fn an_untimed_push_invalidates_an_existing_anchor() {
        // 混用两种写入时，不带时刻的那一段没有记账。锚点若留着，
        // 此后的外推会偏过整段，而算出的时刻仍是个正常数字。
        let mut ring = PlaybackRing::new(8);
        ring.push_at(&stereo(&[(1, 1)]), 0);
        assert!(ring.is_anchored());

        ring.push(&stereo(&[(2, 2)]));
        assert!(!ring.is_anchored());
    }

    #[test]
    fn a_timed_push_maps_the_read_cursor_to_the_frame_time() {
        let mut ring = PlaybackRing::new(48_000);
        ring.push_at(&tone(FRAME), 5 * TICKS_PER_MS);

        // 读游标停在本帧起点，故读回本帧自称的时刻。
        assert_eq!(
            ring.sender_ticks_at(ring.read_cursor_frames()),
            Some(5 * TICKS_PER_MS)
        );

        // 取走半帧，读游标前移 240 帧，对应的时刻随之晚 5 毫秒。
        let mut out = vec![0i16; 240 * CH];
        assert_eq!(ring.read_into(&mut out), 240);
        assert_eq!(
            ring.sender_ticks_at(ring.read_cursor_frames()),
            Some(10 * TICKS_PER_MS)
        );
    }

    #[test]
    fn time_axis_length_matches_the_sample_count() {
        // 本期新增的不变量：ring 里的样本数等于它代表的时间轴长度。
        // 丢帧不补静音时样本数会短一截，出声时刻被永久提前那么多，且每次丢帧累加。
        let mut ring = PlaybackRing::new(48_000);
        ring.push_at(&tone(FRAME), 0);
        ring.push_at(&tone(FRAME), FRAME_TICKS);
        // 第三帧的时刻比预期晚一帧，即中间丢了一帧。
        ring.push_at(&tone(FRAME), 3 * FRAME_TICKS);

        let span = ticks_to_frames(i128::from(4 * FRAME_TICKS));
        assert_eq!(i128::from(ring.written_frames()), span);
        assert_eq!(i128::try_from(ring.available_frames()).unwrap(), span);

        // 补进去的静音必须落在第二帧与第三帧之间。补在末尾同样让总数守恒，
        // 而那会把第三帧提前 10 毫秒出声——总数相等挡不住这种错，读回来比对才挡得住。
        let mut out = vec![0i16; 4 * FRAME * CH];
        assert_eq!(ring.read_into(&mut out), 4 * FRAME);
        assert!(out[..2 * FRAME * CH].iter().all(|&s| s == 7));
        assert!(out[2 * FRAME * CH..3 * FRAME * CH].iter().all(|&s| s == 0));
        assert!(out[3 * FRAME * CH..].iter().all(|&s| s == 7));
    }

    #[test]
    fn quantization_noise_does_not_insert_silence() {
        // 帧头的毫秒量化让相邻两帧的时刻差带约 2 毫秒噪声。把它当空档补上，
        // 就是每帧往时间轴里塞几十帧静音，比它要修的时间轴压缩坏得多。
        let mut ring = PlaybackRing::new(48_000);
        ring.push_at(&tone(FRAME), 0);
        ring.push_at(&tone(FRAME), FRAME_TICKS + 2 * TICKS_PER_MS);

        assert_eq!(ring.written_frames(), 2 * FRAME as u64);
    }

    #[test]
    fn filled_silence_counts_toward_the_anchor_position() {
        // 锚点的帧数必须含补进去的静音。少算那一段，此后每一次外推都偏那么多——
        // 而样本总数仍然守恒，故守恒判据挡不住这个错，两者说的是不同的性质。
        let mut ring = PlaybackRing::new(48_000);
        ring.push_at(&tone(FRAME), 0);
        // 丢一帧：补 480 帧静音，然后才是真实数据。
        ring.push_at(&tone(FRAME), 2 * FRAME_TICKS);

        // 一个样本都还没取走，读游标仍停在第一帧起点，那里的时刻就是 0。
        assert_eq!(ring.read_cursor_frames(), 0);
        assert_eq!(ring.sender_ticks_at(0), Some(0));
    }

    #[test]
    fn dropping_oldest_advances_the_read_cursor() {
        // 累积轴上的读游标不等于设备已消耗的帧数：丢最旧会跳过若干个写入位置。
        // 拿设备的消耗计数去问锚点，此后恒偏丢掉的那一段。
        let mut ring = PlaybackRing::new(2);
        ring.push(&stereo(&[(1, 1), (2, 2), (3, 3)]));

        assert_eq!(ring.written_frames(), 3);
        assert_eq!(ring.read_cursor_frames(), 1);
    }

    #[test]
    fn a_gap_beyond_the_buffer_is_a_reset() {
        // 空档超过容量时，补进去的静音会把仍要播的样本全部挤掉。补与清空等价，
        // 清空还省掉一整轮无用写入。
        let mut ring = PlaybackRing::new(FRAME);
        ring.push_at(&tone(96), 0);
        ring.push_at(&tone(96), 100 * TICKS_PER_MS);

        assert_eq!(ring.available_frames(), 96);
        // 锚点属于新流：读游标处的时刻就是新帧自称的时刻。
        assert_eq!(
            ring.sender_ticks_at(ring.read_cursor_frames()),
            Some(100 * TICKS_PER_MS)
        );
    }

    #[test]
    fn a_position_from_before_the_reset_has_no_answer() {
        // reset 保留累积坐标而作废锚点，此后第一次带时刻写入把锚点钉在当时的写入位置。
        // 于它之前的位置属于上一轮：对它外推会得到一个看起来正常的错时刻，
        // 而调用方无从分辨——is_anchored 此时为真，锚点是新的。
        //
        // 这条路在生产上必经：出声位置是读游标再减去设备里压着的帧数，恒在读游标之前，
        // 而硬重置刚过时读游标恰好等于本轮起点。硬重置的触发条件是累积欠载，
        // 也就是真实不连续量最大的那一刻。
        let mut ring = PlaybackRing::new(FRAME);
        ring.push_at(&tone(96), 0);
        let before_reset = ring.read_cursor_frames();

        ring.reset();
        ring.push_at(&tone(96), 500 * TICKS_PER_MS);
        let run_start = ring.read_cursor_frames();

        assert!(ring.is_anchored(), "锚点是真的，故 is_anchored 挡不住这件事");
        assert_eq!(
            ring.sender_ticks_at(run_start),
            Some(500 * TICKS_PER_MS),
            "本轮之内照常有答案"
        );
        assert_eq!(
            ring.sender_ticks_at(before_reset),
            None,
            "重置前的位置属于上一轮，不该有答案"
        );
        assert_eq!(ring.sender_ticks_at(run_start - 1), None, "起点之前一帧也不该有");
    }

    #[test]
    fn a_position_in_untimed_data_has_no_answer() {
        // 不带时刻写进来的那段样本仍在缓冲里可读，却没有任何时刻记账。push 作废了锚点，
        // 但下一次 push_at 重建锚点之后，读游标只要还指在那段里，问出来的就是编造的时刻。
        let mut ring = PlaybackRing::new(FRAME * 2);
        ring.push(&tone(96));
        ring.push_at(&tone(96), 500 * TICKS_PER_MS);

        // 读游标此刻正指在那段没记账的数据里——这不是构造出来的边界，是混用两个入口的常态。
        assert_eq!(ring.read_cursor_frames(), 0);
        assert_eq!(
            ring.sender_ticks_at(ring.read_cursor_frames()),
            None,
            "没记账的那段不该有答案"
        );
    }

    #[test]
    fn reset_keeps_the_cumulative_coordinate_monotonic() {
        // 累积帧数是时间轴坐标，不是缓冲下标。归零会让它与仍在推进的设备位置
        // 错开一整段，而那种错开只表现为出声时刻算错。
        let mut ring = PlaybackRing::new(8);
        ring.push(&stereo(&[(1, 1), (2, 2)]));
        ring.reset();

        assert_eq!(ring.written_frames(), 2);
        assert_eq!(ring.read_cursor_frames(), 2);
        assert!(!ring.is_anchored());

        ring.push_at(&stereo(&[(3, 3)]), 9 * TICKS_PER_MS);
        assert_eq!(ring.sender_ticks_at(2), Some(9 * TICKS_PER_MS));
    }
}
