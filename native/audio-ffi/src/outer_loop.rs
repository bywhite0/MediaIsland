//! 外环：出声时刻误差 → `target_ms`。
//!
//! 纯逻辑，不带 cfg 门。
//!
//! 两环各自抵消什么，决定了外环该多慢：
//!
//! 内环抵消晶振漂移。稳态下负反馈让占用趋向 `target_ms`，其输出是一个持续的非零比偏移
//! （如 1.0002），恰好抵消 200ppm 的持续速率差。
//!
//! 外环只修正设定值偏差——设备延迟估计残差、prefill 对齐残差。这些是准静态的，
//! 故外环可以很慢，两环带宽自然分离。

use crate::render::{clamp_target_ms, MAX_TARGET_MS, MIN_TARGET_MS};
use crate::ring::MAX_DRIFT;

/// 内环的时间常数，秒。
///
/// 由内环自己的控制律推出，不是量出来的：内环是比例控制，
/// `d(深度)/dt = −(深度 − 目标) / 目标 × MAX_DRIFT`，故时间常数是 `目标深度 / MAX_DRIFT`。
/// 默认目标 300 毫秒下即 300 秒。
///
/// 这个数容易被大幅低估。若按「比例项被夹到上限」算，内环把 10 毫秒偏差拉回只需
/// 10 秒——但夹紧只在偏差达到目标深度那个量级时才发生，而外环面对的偏差是几毫秒。
/// 按夹紧后的速率去定外环，外环会比内环快一个数量级，那是下面这条分离要防的事。
pub fn inner_loop_seconds(target_ms: u32) -> f64 {
    f64::from(target_ms) / 1_000.0 / MAX_DRIFT
}

/// 外环慢于内环的倍数。
///
/// 外环改的是内环的设定值，而设定值的变化只能由内环去执行。外环若快于内环，
/// 内环还没跟上，外环已经把设定值推远了；误差随后反向超调，两者以分钟为周期摆动。
/// 那不是发散，故没有任何东西会崩——出声时刻摆几十毫秒，是 10 毫秒预算的三倍。
///
/// 取 3 而非 10：10 倍分离下默认目标深度对应约 50 分钟的收敛时间，而外环要修的残差
/// 本就在单端 5 毫秒预算之内，用半小时去修一个已经合格的量不划算。
/// 若实测到深度与出声时刻同步缓慢摆动，这是第一个该调大的常量。
pub const LOOP_SEPARATION: f64 = 3.0;

/// 外环的积分时间常数，秒。
pub fn integration_seconds(target_ms: u32) -> f64 {
    LOOP_SEPARATION * inner_loop_seconds(target_ms)
}

/// 死区。与误差预算里「外环稳态残差 <1 毫秒」取同一个数：
/// 小于预算分配额的误差不值得动 `target_ms`，而每次动都在改输出延迟。
pub const DEAD_ZONE_MS: f64 = 1.0;

/// 速率上限，每秒最多改多少毫秒。
///
/// 它的作用不是限速而是兜底。按 [`integration_seconds`]，默认目标深度下 10 毫秒误差
/// 对应约 0.011 毫秒每秒，比这条线低两个数量级——正常工作时它永不触发。
/// 它挡的是误差信号本身坏掉：offset 抖动或对端换机时误差可以跳到数百毫秒，
/// 那时没有这条限制外环会把 `target_ms` 一次推很远，而那是可闻的。
/// 0.5 对应误差约 450 毫秒才开始触发，正是「信号已经不可信」那一档。
pub const MAX_RATE_MS_PER_SECOND: f64 = 0.5;

/// 实际出声时刻，本机时间轴的 100ns tick。
///
/// `device_position_qpc` 是取设备位置那一刻的 QPC，即此刻正被交给硬件的那个采样；
/// 它还要穿过设备尾段才出声，故加上延迟估计与用户的手动偏移。
///
/// 手动偏移加在这一侧而不是目标侧：它补的是自动估计没看见的那段真实硬件延迟
/// （DAC、功放、蓝牙），那是实际出声更晚，不是目标更早。两侧等价于差一个符号，
/// 而符号错了的表现是对齐往反方向跑，且看起来像手动偏移「刻度反了」。
pub fn actual_play_ticks(
    device_position_qpc: i64,
    device_latency_ticks: i64,
    manual_offset_ticks: i64,
) -> i64 {
    device_position_qpc
        .saturating_add(device_latency_ticks)
        .saturating_add(manual_offset_ticks)
}

/// 目标出声时刻，本机时间轴的 100ns tick。
///
/// 那个采样在发送端时间轴上采于 `sender_ticks`，按预算它应当在 `sender_ticks + D`
/// 出声；再经 offset 换到本机轴。`clock_offset_ticks` 的方向是本机减发送端，
/// 故这里是加。
pub fn target_play_ticks(sender_ticks: i64, d_ticks: i64, clock_offset_ticks: i64) -> i64 {
    sender_ticks
        .saturating_add(d_ticks)
        .saturating_add(clock_offset_ticks)
}

/// 出声时刻误差，tick。正表示出声偏晚。
pub fn play_time_error_ticks(actual_ticks: i64, target_ticks: i64) -> i64 {
    actual_ticks.saturating_sub(target_ticks)
}

#[derive(Default)]
pub struct OuterLoop {
    residual_ms: f64,
}

impl OuterLoop {
    /// 走一步。
    ///
    /// `error_ms` 的符号约定：正表示出声偏晚（实际出声时刻晚于目标）。偏晚要减小缓冲
    /// 深度，故 `target_ms` 往下走。这个约定只写在这里一处，调用方按它传参；两处各自
    /// 定义符号是最容易出的错，且错了的表现是缓慢发散而不是立刻错。
    ///
    /// 纯积分，无比例项。设定值偏差是准静态的，比例项只会把测量噪声直接搬到
    /// `target_ms` 上，而 `target_ms` 的每一次变化都是一次输出延迟的变化。
    pub fn step(&mut self, error_ms: f64, dt_seconds: f64, current_target_ms: u32) -> u32 {
        if !error_ms.is_finite() || !dt_seconds.is_finite() || dt_seconds <= 0.0 {
            return current_target_ms;
        }

        if error_ms.abs() < DEAD_ZONE_MS {
            return current_target_ms;
        }

        let step = -error_ms * dt_seconds / integration_seconds(current_target_ms);
        let cap = MAX_RATE_MS_PER_SECOND * dt_seconds;
        let step = step.clamp(-cap, cap);

        // 亚毫秒的增量攒起来，不丢。target_ms 是整数毫秒，逐次截断会让小误差永远
        // 修不掉——而默认目标深度下 10 毫秒误差的单轮增量约是万分之一毫秒，
        // 逐次截断等于外环整个不工作。
        self.residual_ms += step;
        let whole = self.residual_ms.trunc();
        self.residual_ms -= whole;

        clamp_target_ms((f64::from(current_target_ms) + whole).round() as u32)
    }

    /// 撞上边界意味着偏差超出外环能力，是硬重置的前兆，而不是一次普通的夹紧。
    /// 调用方据此记一次警告。
    pub fn is_saturated(target_ms: u32) -> bool {
        target_ms <= MIN_TARGET_MS || target_ms >= MAX_TARGET_MS
    }

    /// 换机或重连后清残差。留着上一段的残差会让新一段的第一次调整凭空多走一步。
    pub fn reset(&mut self) {
        self.residual_ms = 0.0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::timeline::TICKS_PER_MS;

    /// 默认播放延迟预算下的目标深度。
    const TARGET: u32 = 300;

    fn run(error_ms: f64, dt_seconds: f64, steps: usize) -> u32 {
        let mut loop_state = OuterLoop::default();
        let mut target = TARGET;
        for _ in 0..steps {
            target = loop_state.step(error_ms, dt_seconds, target);
        }
        target
    }

    #[test]
    fn inner_loop_constant_comes_from_the_drift_limit() {
        // 关系而非数值：内环时间常数乘 MAX_DRIFT 就是目标深度的秒数。
        // 这样写，MAX_DRIFT 一改它自动跟上，而写死 300 会在那时静默失真。
        for target in [50u32, 300, 1_000] {
            let seconds = inner_loop_seconds(target);
            assert!(
                (seconds * MAX_DRIFT - f64::from(target) / 1_000.0).abs() < 1e-9,
                "target={target} seconds={seconds}"
            );
        }
    }

    #[test]
    fn the_outer_loop_corrects_at_most_a_third_within_one_inner_time_constant() {
        // 分离度的行为形式：走完一个内环时间常数，外环最多修掉误差的三分之一。
        // 写成行为而不是 assert!(LOOP_SEPARATION >= 3.0)——后者是把常量换个写法
        // 再断言一次，改常量的同时改判据即可全绿。
        let error_ms = 30.0;
        let tau_inner = inner_loop_seconds(TARGET);

        let mut loop_state = OuterLoop::default();
        let after = loop_state.step(error_ms, tau_inner, TARGET);
        let corrected = f64::from(TARGET) - f64::from(after);

        assert!(corrected > 0.0, "正误差应当减小目标深度，实得 {corrected}");
        assert!(
            corrected <= error_ms / 3.0 + 1.0,
            "一个内环时间常数内修掉了 {corrected} 毫秒，超过误差的三分之一"
        );
    }

    #[test]
    fn a_late_output_lowers_the_target_depth() {
        // 出声偏晚要减小缓冲深度。符号接反的表现是缓慢发散，不是立刻错。
        assert!(run(10.0, 1.0, 200) < TARGET);
    }

    #[test]
    fn an_early_output_raises_the_target_depth() {
        assert!(run(-10.0, 1.0, 200) > TARGET);
    }

    #[test]
    fn an_error_inside_the_dead_zone_moves_nothing() {
        // 小于预算分配额的误差不值得动 target_ms，而每次动都在改输出延迟。
        assert_eq!(run(0.9, 1.0, 10_000), TARGET);
        assert_eq!(run(-0.9, 1.0, 10_000), TARGET);
        // 恰在死区边界上要动：成对钉住两侧。
        assert!(run(1.0, 1.0, 10_000) < TARGET);
    }

    #[test]
    fn a_huge_error_is_rate_limited() {
        // 误差信号坏掉时（offset 抖动、对端换机）外环不能把 target_ms 一次推很远。
        //
        // 上界写绝对值，不写 MAX_RATE_MS_PER_SECOND * dt：后者拿被测常量自己当界，
        // 把上限改成 1000 判据仍然全绿（变异实测如此）。这里断言的是这条限制存在的
        // 目的——一秒之内不得跳过一毫秒量级，而无上限的积分器会一步撞到下界。
        let after_one_second = run(1.0e9, 1.0, 1);
        assert!(
            f64::from(TARGET) - f64::from(after_one_second) <= 1.0,
            "一秒内动了 {} 毫秒",
            f64::from(TARGET) - f64::from(after_one_second)
        );

        // 十秒之后确实在动，但仍远离下界。
        let after_ten = run(1.0e9, 1.0, 10);
        assert!(after_ten < TARGET, "应当在动");
        assert!(
            after_ten > MIN_TARGET_MS + 100,
            "十秒就跌到 {after_ten}，几乎是一步撞到下界"
        );
    }

    #[test]
    fn sub_millisecond_steps_accumulate_instead_of_being_truncated() {
        // 默认目标深度下 10 毫秒误差的单轮增量约万分之一毫秒。逐次截断等于外环不工作。
        let mut loop_state = OuterLoop::default();
        let mut target = TARGET;
        let dt = 0.01; // 一轮渲染

        // 单轮绝对动不了一格。
        let after_one = loop_state.step(10.0, dt, target);
        assert_eq!(after_one, TARGET, "单轮不该动");

        for _ in 0..20_000 {
            target = loop_state.step(10.0, dt, target);
        }
        assert!(target < TARGET, "两万轮之后应当已经动了，实得 {target}");
    }

    #[test]
    fn the_target_is_clamped_and_saturation_is_reported() {
        // 撞边界意味着偏差超出外环能力，调用方要据此告警而不是当普通夹紧。
        let low = run(1.0e9, 1.0, 100_000);
        assert_eq!(low, MIN_TARGET_MS);
        assert!(OuterLoop::is_saturated(low));

        let high = run(-1.0e9, 1.0, 100_000);
        assert_eq!(high, MAX_TARGET_MS);
        assert!(OuterLoop::is_saturated(high));

        assert!(!OuterLoop::is_saturated(
            (MIN_TARGET_MS + MAX_TARGET_MS) / 2
        ));
    }

    #[test]
    fn nonsense_inputs_move_nothing() {
        // NaN 顺着积分器污染残差之后，此后每一步都是 NaN，而 target_ms 会卡在夹紧值上。
        let mut loop_state = OuterLoop::default();
        for bad in [f64::NAN, f64::INFINITY, f64::NEG_INFINITY] {
            assert_eq!(loop_state.step(bad, 1.0, TARGET), TARGET);
            assert_eq!(loop_state.step(10.0, bad, TARGET), TARGET);
        }
        assert_eq!(loop_state.step(10.0, 0.0, TARGET), TARGET);
        assert_eq!(loop_state.step(10.0, -1.0, TARGET), TARGET);
    }

    #[test]
    fn reset_drops_the_accumulated_residual() {
        let mut loop_state = OuterLoop::default();
        let mut target = TARGET;
        // 攒到差一点就够一格。
        for _ in 0..90 {
            target = loop_state.step(10.0, 1.0, target);
        }
        assert_eq!(target, TARGET, "这些步还不该凑够一格");

        loop_state.reset();
        // 残差清了，再走同样多步仍不该到位——若 reset 没清，下一步就跨过去了。
        let mut after_reset = target;
        for _ in 0..80 {
            after_reset = loop_state.step(10.0, 1.0, after_reset);
        }
        assert_eq!(after_reset, TARGET);
    }

    #[test]
    fn device_latency_and_manual_offset_both_push_the_output_later() {
        // 两者都是「实际出声更晚」，不是「目标更早」。加在目标侧等价于差一个符号，
        // 而符号错了看起来像手动偏移的刻度反了。
        let base = actual_play_ticks(1_000, 0, 0);
        assert_eq!(base, 1_000);
        assert!(actual_play_ticks(1_000, 5 * TICKS_PER_MS, 0) > base);
        assert!(actual_play_ticks(1_000, 0, 5 * TICKS_PER_MS) > base);
        // 手动偏移可以为负：用户听出来放晚了就往回拨。
        assert!(actual_play_ticks(1_000, 0, -5 * TICKS_PER_MS) < base);
    }

    #[test]
    fn the_budget_pushes_the_target_later_and_the_offset_shifts_the_axis() {
        let sender = 7 * TICKS_PER_MS;
        let d = 300 * TICKS_PER_MS;
        // 预算越大，目标出声时刻越晚。
        assert_eq!(target_play_ticks(sender, d, 0), sender + d);
        assert!(target_play_ticks(sender, 2 * d, 0) > target_play_ticks(sender, d, 0));
        // offset 是本机减发送端，故换轴时是加。它通常是个巨大的负数
        // （本机 QPC 自开机起算，发送端墙钟自 1970 起算）。
        let offset = -1_700_000_000_000_000;
        assert_eq!(target_play_ticks(sender, d, offset), sender + d + offset);
    }

    #[test]
    fn a_late_output_yields_a_positive_error() {
        // 整条链的符号：实际晚于目标即正，而正误差要减小缓冲深度。
        let sender = 0;
        let offset = 1_000_000;
        let d = 300 * TICKS_PER_MS;
        let target = target_play_ticks(sender, d, offset);

        let late = actual_play_ticks(target + TICKS_PER_MS, 0, 0);
        assert!(play_time_error_ticks(late, target) > 0);

        let early = actual_play_ticks(target - TICKS_PER_MS, 0, 0);
        assert!(play_time_error_ticks(early, target) < 0);
    }
}
