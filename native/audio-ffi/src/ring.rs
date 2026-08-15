//! 播放侧的环形缓冲与漂移控制律。
//!
//! 与采集侧的队列取向相反：采集队列满则丢最旧，因为可视化只关心现在在响什么；
//! 播放这一侧丢最旧同样正确，但理由不同——积压意味着本机放得比上游发得慢，
//! 保留最新才能追上，保留最旧只会让延迟永久累积。

/// 交错立体声，恒 2 声道。与传输格式一致，此处不做声道数适配。
const CHANNELS: usize = 2;

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
        }
    }

    pub fn available_frames(&self) -> usize {
        self.filled
    }

    pub fn reset(&mut self) {
        self.write = 0;
        self.filled = 0;
    }

    /// 追加交错样本。末尾不足一帧的残样本丢弃——错位成右声道会让整条流的声道翻转。
    pub fn push(&mut self, interleaved: &[i16]) {
        let frames = interleaved.len() / CHANNELS;
        for frame in 0..frames {
            let src = frame * CHANNELS;
            let dst = self.write * CHANNELS;
            self.buffer[dst..dst + CHANNELS]
                .copy_from_slice(&interleaved[src..src + CHANNELS]);

            self.write = (self.write + 1) % self.capacity_frames;
            if self.filled < self.capacity_frames {
                self.filled += 1;
            }
            // filled 已满时不增长：写游标前移即等价于丢弃最旧的一帧。
        }
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
        let mut out = vec![0i16; CH];
        assert_eq!(ring.read_into(&mut out), 0);
    }

    #[test]
    fn odd_sample_count_is_ignored_at_the_tail() {
        // 交错立体声，样本数必为偶数。奇数说明上游有误，末尾残样本丢弃而非错位成右声道。
        let mut ring = PlaybackRing::new(8);
        ring.push(&[1, -1, 5]);

        assert_eq!(ring.available_frames(), 1);
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
        assert!((drift_ratio(100_000.0, 200.0) - (1.0 + MAX_DRIFT)).abs() < 1e-12);
        assert!((drift_ratio(0.0, 200.0) - (1.0 - MAX_DRIFT)).abs() < 1e-12);
    }

    #[test]
    fn drift_ratio_tolerates_zero_target() {
        // 目标为零是配置错误，但不该产生 NaN 送进重采样器。
        assert_eq!(drift_ratio(50.0, 0.0), 1.0);
    }
}
