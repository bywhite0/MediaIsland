//! 设备原始 PCM 到传输格式（48000Hz / 2ch / i16 交错）的归一化。
//!
//! 全部是纯函数，故可脱离 WASAPI 单测——真机只需验证采集与线程生命周期。
//!
//! 参照 `apoint123/smtc-suite`（MIT）`src/audio_capture.rs` 的实现思路，未复制代码。
//! 三处刻意偏离见 `capture.rs` 头部说明；本文件承担其中两处：
//!
//! 1. **格式兼容性** — smtc-suite 遇非 f32/32bit 混音格式直接返回 `UnsupportedFormat` 退出。
//!    用户设备的混音格式可能是 s16 或 s24，直接失败不可接受。本实现支持 PCM 16/24/32 位
//!    整数与 IEEE float 全部归一到 f32。
//! 2. **直出 i16** — smtc-suite 输出 f32 交给上层再转 i16，多一次全量遍历与分配。
//!    本协议的传输格式就是 i16，故在此一次成型。

use rubato::{Resampler, SincFixedIn, SincInterpolationParameters, SincInterpolationType, WindowFunction};

/// 设备混音格式中每样本的存储形态。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SampleFormat {
    /// `WAVE_FORMAT_PCM`，16 位有符号整数。
    Pcm16,
    /// `WAVE_FORMAT_PCM`，24 位有符号整数（3 字节紧密排列，非 32 位容器）。
    Pcm24,
    /// `WAVE_FORMAT_PCM`，32 位有符号整数。
    Pcm32,
    /// `WAVE_FORMAT_IEEE_FLOAT`，32 位浮点。
    F32,
}

impl SampleFormat {
    pub const fn bytes_per_sample(self) -> usize {
        match self {
            SampleFormat::Pcm16 => 2,
            SampleFormat::Pcm24 => 3,
            SampleFormat::Pcm32 | SampleFormat::F32 => 4,
        }
    }
}

/// 把设备原始字节归一到 f32（标称范围 ±1.0）。
///
/// 整数格式除以各自类型的最大值而非 2^n：除以 `i16::MAX` 让满量程负值映射到略小于 -1.0，
/// 这在后续 clamp 中被吸收，且保证 `i16::MAX` 精确映射到 1.0——往返回 i16 时不掉一个最低位。
pub fn normalize_to_f32(bytes: &[u8], format: SampleFormat) -> Vec<f32> {
    let stride = format.bytes_per_sample();
    let count = bytes.len() / stride;
    let mut out = Vec::with_capacity(count);

    for chunk in bytes.chunks_exact(stride) {
        let value = match format {
            SampleFormat::Pcm16 => {
                let raw = i16::from_le_bytes([chunk[0], chunk[1]]);
                raw as f32 / i16::MAX as f32
            }
            SampleFormat::Pcm24 => {
                // 三字节小端，最高位是符号位：左移到 i32 高位再算术右移完成符号扩展。
                let raw = ((chunk[2] as i32) << 24 | (chunk[1] as i32) << 16 | (chunk[0] as i32) << 8) >> 8;
                raw as f32 / 8_388_607.0 // i24::MAX
            }
            SampleFormat::Pcm32 => {
                let raw = i32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]);
                raw as f32 / i32::MAX as f32
            }
            SampleFormat::F32 => f32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]),
        };
        out.push(value);
    }

    out
}

/// 混为立体声。
///
/// 多声道**取前两路**而非求平均：5.1 的前两路就是前置左右，求平均会把中置与环绕
/// 混进来，得到一个没有任何扬声器真正在放的信号。可视化要的是「听感上的左右」。
/// 单声道复制为双声道。
pub fn downmix_to_stereo(samples: &[f32], channels: u16) -> Vec<f32> {
    match channels {
        0 => Vec::new(),
        1 => {
            let mut out = Vec::with_capacity(samples.len() * 2);
            for &sample in samples {
                out.push(sample);
                out.push(sample);
            }
            out
        }
        2 => samples.to_vec(),
        n => {
            let stride = n as usize;
            let frames = samples.len() / stride;
            let mut out = Vec::with_capacity(frames * 2);
            for frame in 0..frames {
                out.push(samples[frame * stride]);
                out.push(samples[frame * stride + 1]);
            }
            out
        }
    }
}

/// f32 转 i16。
///
/// **必须先 clamp。** 超过 ±1.0 的样本（响度归一化或效果器可能产生）直接 `as i16`
/// 会在整数转换时回绕，把峰值变成反相的谷值——听感上是刺耳爆音，而非轻微失真。
pub fn f32_to_i16(samples: &[f32]) -> Vec<i16> {
    samples
        .iter()
        .map(|&sample| (sample.clamp(-1.0, 1.0) * i16::MAX as f32) as i16)
        .collect()
}

/// i16 交错样本转小端字节。
pub fn i16_to_le_bytes(samples: &[i16]) -> Vec<u8> {
    let mut out = Vec::with_capacity(samples.len() * 2);
    for &sample in samples {
        out.extend_from_slice(&sample.to_le_bytes());
    }
    out
}

/// 把一个 f32 样本按设备格式写进 `dst` 的开头。[`normalize_to_f32`] 的逆运算。
///
/// 三条防护，都不是理论上的：
///
/// 1. **先 clamp**，理由同 [`f32_to_i16`]——回绕会把峰值变成反相的谷值，
///    听感是刺耳爆音。重采样对满量程信号会产生过冲。
/// 2. **非有限值写零**。NaN 走整数分支时 `as i32` 得 0，但走 f32 分支会被原样写进
///    设备缓冲，那是驱动层面的未定义行为。
/// 3. **切片短于一个样本时整体跳过**。设备 `block_align` 与声道数不自洽时会切出短片，
///    越界写崩在实时线程上会带走整个宿主进程。
pub fn write_sample(dst: &mut [u8], sample: f32, format: SampleFormat) {
    let stride = format.bytes_per_sample();
    if dst.len() < stride {
        return;
    }

    let value = if sample.is_finite() {
        sample.clamp(-1.0, 1.0)
    } else {
        0.0
    };

    match format {
        SampleFormat::Pcm16 => {
            let raw = (value * i16::MAX as f32) as i16;
            dst[..2].copy_from_slice(&raw.to_le_bytes());
        }
        SampleFormat::Pcm24 => {
            // 三字节紧密排列的小端，写低三字节即可——i24::MAX 保证不会溢出到第四字节。
            let raw = (value * 8_388_607.0) as i32;
            dst[..3].copy_from_slice(&raw.to_le_bytes()[..3]);
        }
        SampleFormat::Pcm32 => {
            let raw = (value as f64 * i32::MAX as f64) as i32;
            dst[..4].copy_from_slice(&raw.to_le_bytes());
        }
        SampleFormat::F32 => {
            dst[..4].copy_from_slice(&value.to_le_bytes());
        }
    }
}

/// 立体声重采样器。
///
/// `SincFixedIn` 要求**固定输入块大小**，而 WASAPI `GetBuffer` 返回的帧数不保证恒定，
/// 故调用方需在此之前定长分块（引入不到一个块的延迟）。48kHz 设备完全不走这条路径——
/// [`StereoResampler::new`] 对同采样率返回 `None`，调用方直接透传，零额外缓冲。
pub struct StereoResampler {
    inner: SincFixedIn<f32>,
    chunk_frames: usize,
    pending: Vec<Vec<f32>>,
}

impl StereoResampler {
    /// 采样率相同则返回 `None`——此时任何重采样都是纯粹的损耗。
    pub fn new(input_rate: u32, output_rate: u32, chunk_frames: usize) -> Option<Self> {
        if input_rate == output_rate || input_rate == 0 || output_rate == 0 {
            return None;
        }

        let params = SincInterpolationParameters {
            sinc_len: 128,
            f_cutoff: 0.95,
            interpolation: SincInterpolationType::Linear,
            oversampling_factor: 128,
            window: WindowFunction::BlackmanHarris2,
        };

        let inner = SincFixedIn::<f32>::new(
            output_rate as f64 / input_rate as f64,
            1.0,
            params,
            chunk_frames,
            2,
        )
        .ok()?;

        Some(Self {
            inner,
            chunk_frames,
            pending: vec![Vec::new(), Vec::new()],
        })
    }

    pub fn chunk_frames(&self) -> usize {
        self.chunk_frames
    }

    /// 送入交错立体声样本，取回重采样后的交错样本。
    ///
    /// 不足一个块的尾巴留在内部等待下次调用——这正是「不到一个块的延迟」的来源。
    pub fn process_interleaved(&mut self, interleaved: &[f32]) -> Vec<f32> {
        for frame in interleaved.chunks_exact(2) {
            self.pending[0].push(frame[0]);
            self.pending[1].push(frame[1]);
        }

        let mut out = Vec::new();
        while self.pending[0].len() >= self.chunk_frames {
            let block: Vec<Vec<f32>> = self
                .pending
                .iter_mut()
                .map(|channel| channel.drain(..self.chunk_frames).collect())
                .collect();

            let Ok(resampled) = self.inner.process(&block, None) else {
                break;
            };

            let frames = resampled[0].len();
            out.reserve(frames * 2);
            for (left, right) in resampled[0].iter().zip(resampled[1].iter()) {
                out.push(*left);
                out.push(*right);
            }
        }

        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pcm16_maps_full_scale_to_unit_range() {
        let bytes = [
            0xFF, 0x7F, // i16::MAX
            0x00, 0x80, // i16::MIN
            0x00, 0x00, // 0
        ];

        let out = normalize_to_f32(&bytes, SampleFormat::Pcm16);

        assert!((out[0] - 1.0).abs() < 1e-6);
        assert!(out[1] < -1.0 && out[1] > -1.001);
        assert_eq!(out[2], 0.0);
    }

    #[test]
    fn pcm24_sign_extends_correctly() {
        // 三字节小端 0xFFFFFF = -1；0x000080 = 8388608 的负半 = i24::MIN
        let bytes = [0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00];

        let out = normalize_to_f32(&bytes, SampleFormat::Pcm24);

        assert!(out[0] < 0.0, "0xFFFFFF 应为负值，实际 {}", out[0]);
        assert!(out[1] < -0.99, "i24::MIN 应接近 -1.0，实际 {}", out[1]);
        assert_eq!(out[2], 0.0);
    }

    #[test]
    fn pcm24_max_maps_to_one() {
        let bytes = [0xFF, 0xFF, 0x7F];

        let out = normalize_to_f32(&bytes, SampleFormat::Pcm24);

        assert!((out[0] - 1.0).abs() < 1e-6);
    }

    #[test]
    fn pcm32_maps_full_scale() {
        let bytes = [0xFF, 0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x00, 0x00];

        let out = normalize_to_f32(&bytes, SampleFormat::Pcm32);

        assert!((out[0] - 1.0).abs() < 1e-6);
        assert_eq!(out[1], 0.0);
    }

    #[test]
    fn f32_passes_through() {
        let bytes = [0.5f32.to_le_bytes(), (-0.25f32).to_le_bytes()].concat();

        let out = normalize_to_f32(&bytes, SampleFormat::F32);

        assert_eq!(out, vec![0.5, -0.25]);
    }

    #[test]
    fn partial_trailing_bytes_are_ignored() {
        // 半个样本不能解释成一个样本，否则会读到相邻帧的数据。
        let bytes = [0x00, 0x00, 0x00];

        let out = normalize_to_f32(&bytes, SampleFormat::Pcm16);

        assert_eq!(out.len(), 1);
    }

    #[test]
    fn out_of_range_is_clamped_not_wrapped() {
        // 回绕会把峰值变成反相的谷值——听感是刺耳爆音而非轻微失真。
        let out = f32_to_i16(&[2.0, -2.0, 1.0, -1.0]);

        assert_eq!(out[0], i16::MAX);
        assert_eq!(out[1], -i16::MAX);
        assert_eq!(out[2], i16::MAX);
        assert_eq!(out[3], -i16::MAX);
    }

    #[test]
    fn mono_is_duplicated_to_both_channels() {
        let out = downmix_to_stereo(&[0.1, 0.2], 1);

        assert_eq!(out, vec![0.1, 0.1, 0.2, 0.2]);
    }

    #[test]
    fn stereo_passes_through_unchanged() {
        let out = downmix_to_stereo(&[0.1, 0.2, 0.3, 0.4], 2);

        assert_eq!(out, vec![0.1, 0.2, 0.3, 0.4]);
    }

    #[test]
    fn multichannel_takes_first_two_not_average() {
        // 5.1 的前两路就是前置左右；求平均会把中置与环绕混进来，
        // 得到一个没有任何扬声器真正在放的信号。
        let frame = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0];

        let out = downmix_to_stereo(&frame, 6);

        assert_eq!(out, vec![1.0, 2.0]);
    }

    #[test]
    fn zero_channels_yields_empty() {
        assert!(downmix_to_stereo(&[1.0, 2.0], 0).is_empty());
    }

    #[test]
    fn same_rate_needs_no_resampler() {
        assert!(StereoResampler::new(48_000, 48_000, 960).is_none());
    }

    #[test]
    fn resampling_44100_to_48000_grows_sample_count() {
        let chunk = 441;
        let mut resampler = StereoResampler::new(44_100, 48_000, chunk).expect("需要重采样");

        // 送入 10 个块共 4410 帧（100ms @ 44.1kHz），应产出约 4800 帧（100ms @ 48kHz）。
        let input: Vec<f32> = (0..chunk * 10 * 2).map(|i| (i as f32 * 0.001).sin()).collect();
        let out = resampler.process_interleaved(&input);

        let out_frames = out.len() / 2;
        let expected = 4_800;
        assert!(
            (out_frames as i64 - expected as i64).abs() < 200,
            "期望约 {expected} 帧，实际 {out_frames}"
        );
    }

    #[test]
    fn resampler_holds_incomplete_chunk() {
        let chunk = 480;
        let mut resampler = StereoResampler::new(44_100, 48_000, chunk).expect("需要重采样");

        // 不足一块时不应产出——凑不满的尾巴要留到下次。
        let out = resampler.process_interleaved(&vec![0.0; (chunk - 1) * 2]);

        assert!(out.is_empty());
    }

    #[test]
    fn i16_round_trips_through_le_bytes() {
        let samples = [0i16, 1, -1, i16::MAX, i16::MIN];

        let bytes = i16_to_le_bytes(&samples);

        assert_eq!(bytes.len(), samples.len() * 2);
        assert_eq!(i16::from_le_bytes([bytes[6], bytes[7]]), i16::MAX);
        assert_eq!(i16::from_le_bytes([bytes[8], bytes[9]]), i16::MIN);
    }

    #[test]
    fn full_pipeline_pcm16_stereo_is_identity_at_48k() {
        // 48kHz / 立体声 / s16 是最常见的设备格式，这条路径必须零损耗。
        let original: Vec<i16> = vec![0, 100, -100, 12_345, -12_345, i16::MAX];
        let bytes = i16_to_le_bytes(&original);

        let floats = normalize_to_f32(&bytes, SampleFormat::Pcm16);
        let stereo = downmix_to_stereo(&floats, 2);
        let back = f32_to_i16(&stereo);

        assert_eq!(back, original);
    }

    /// `write_sample` 与 [`normalize_to_f32`] 互为逆运算，故用往返比对做判据：
    /// 单独断言字节值只能证明「写了某些字节」，证明不了两者对同一格式的解释一致，
    /// 而播放侧写错格式的失效形态是刺耳噪声，不是轻微失真。
    fn round_trip(value: f32, format: SampleFormat) -> f32 {
        let mut bytes = vec![0u8; format.bytes_per_sample()];
        write_sample(&mut bytes, value, format);
        normalize_to_f32(&bytes, format)[0]
    }

    #[test]
    fn write_sample_round_trips_every_format() {
        for format in [
            SampleFormat::Pcm16,
            SampleFormat::Pcm24,
            SampleFormat::Pcm32,
            SampleFormat::F32,
        ] {
            for value in [0.0f32, 0.5, -0.5, 1.0, -1.0] {
                let back = round_trip(value, format);
                assert!(
                    (back - value).abs() < 1e-4,
                    "{format:?} 往返 {value} 得到 {back}"
                );
            }
        }
    }

    #[test]
    fn write_sample_clamps_instead_of_wrapping() {
        // 与 f32_to_i16 同一条理由：回绕会把峰值变成反相的谷值，
        // 听感是刺耳爆音而非轻微失真。漂移控制律不会产生越界值，
        // 但上游发来的 PCM 经重采样后可以有过冲。
        for format in [
            SampleFormat::Pcm16,
            SampleFormat::Pcm24,
            SampleFormat::Pcm32,
            SampleFormat::F32,
        ] {
            assert!(round_trip(4.0, format) > 0.9, "{format:?} 正向过冲被回绕了");
            assert!(round_trip(-4.0, format) < -0.9, "{format:?} 负向过冲被回绕了");
        }
    }

    #[test]
    fn write_sample_ignores_non_finite_input() {
        // NaN as i32 在 Rust 里是 0，但 f32 格式会把 NaN 原样写进设备缓冲，
        // 那是设备驱动层面的未定义行为。一律写零。
        for format in [
            SampleFormat::Pcm16,
            SampleFormat::Pcm24,
            SampleFormat::Pcm32,
            SampleFormat::F32,
        ] {
            assert_eq!(round_trip(f32::NAN, format), 0.0, "{format:?} 放过了 NaN");
            assert_eq!(
                round_trip(f32::INFINITY, format),
                0.0,
                "{format:?} 放过了 inf"
            );
        }
    }

    #[test]
    fn write_sample_tolerates_short_slice() {
        // 设备 block_align 与声道数不自洽时切片会短于一个样本。
        // 越界写会崩在实时线程上，而那会带走整个宿主进程。
        let mut bytes = [0u8; 1];

        write_sample(&mut bytes, 0.5, SampleFormat::Pcm16);

        assert_eq!(bytes, [0u8; 1], "短切片应整体跳过而非部分写入");
    }
}
