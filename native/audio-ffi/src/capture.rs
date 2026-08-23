//! WASAPI loopback 采集。
//!
//! 参照 `apoint123/smtc-suite`（MIT）`src/audio_capture.rs` 的实现思路，未复制代码。
//! 不直接依赖该 crate 的三个理由（详见 design.md）：作者已标记 abandoned；
//! 它捆绑整套 SMTC 会话管理而本仓已有成熟的 SMTC 层；需要三处偏离。
//!
//! **三处刻意偏离：**
//!
//! 1. **格式兼容性** — smtc-suite 遇非 f32/32bit 混音格式直接退出。本实现支持
//!    PCM 16/24/32 与 IEEE float 全部归一（见 `convert.rs`）。
//! 2. **直出 i16** — 传输格式就是 i16，在 Rust 侧一次成型（见 `convert.rs`）。
//! 3. **静音填零而非跳过** — smtc-suite 在 `AUDCLNT_BUFFERFLAGS_SILENT` 时跳过该包，
//!    会让接收端 FFT 冻结在最后一帧波形上而非归零。本实现送零值帧并置静音位。
//!
//! **帧节奏跟随 WASAPI 事件，不做 100ms 累积。** smtc-suite 与 UniLyric 均按约 100ms
//! 打包，那对本项目不合适：累积的唯一收益是减少发送次数，在 50 帧/秒量级上不存在；
//! 代价则是线性累加的延迟（单跳 ~120ms vs ~40ms），中继重组还会翻倍。

use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread::JoinHandle;

use windows::Win32::Foundation::HANDLE;
use windows::Win32::Media::Audio::{
    eConsole, eRender, IAudioCaptureClient, IAudioClient, IMMDeviceEnumerator, MMDeviceEnumerator,
    AUDCLNT_BUFFERFLAGS_SILENT, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
    AUDCLNT_STREAMFLAGS_LOOPBACK,
};
use windows::Win32::System::Com::{
    CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_ALL,
    COINIT_MULTITHREADED,
};
use windows::Win32::System::Threading::{CreateEventW, SetEvent};

use crate::convert::{self, MixFormat, StereoResampler};
use crate::run_guarded;
use crate::wasapi_common::{parse_mix_format, wait_for_any, StopEvent, WaitObject};
use crate::{
    AudioError, AudioFrame, AudioFrameCallback, OUTPUT_CHANNELS, OUTPUT_SAMPLE_RATE,
    STATUS_ALREADY_RUNNING,
};

/// 20ms 缓冲。这是 WASAPI 共享模式下的实用下限，再降需承担 glitch 风险。
///
/// 单位由 `timeline` 导出：REFERENCE_TIME 就是 100ns tick，而那个单位在本 crate 里
/// 只有一处定义。自己写一个 10_000 等于让同一个约定多出一份各自为真的声明。
const BUFFER_DURATION_100NS: i64 = 20 * crate::timeline::TICKS_PER_MS;

/// 重采样的定长块，对应 20ms @ 48kHz。仅非 48kHz 设备走这条路径。
const RESAMPLE_CHUNK_FRAMES: usize = 960;

pub struct WasapiLoopbackCapture {
    callback: AudioFrameCallback,
    user_data: usize,
    running: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
    stop_event: Option<Arc<StopEvent>>,
}

impl WasapiLoopbackCapture {
    pub fn new(callback: AudioFrameCallback, user_data: usize) -> Self {
        Self {
            callback,
            user_data,
            running: Arc::new(AtomicBool::new(false)),
            worker: None,
            stop_event: None,
        }
    }

    pub fn start(&mut self) -> Result<(), AudioError> {
        if self.running.load(Ordering::SeqCst) {
            return Err(AudioError {
                message: "采集已在运行".to_string(),
                status: STATUS_ALREADY_RUNNING,
            });
        }

        let stop_handle = unsafe { CreateEventW(None, true, false, None) }
            .map_err(|err| AudioError::device(format!("创建停止事件失败：{err}")))?;
        // 事件句柄由 Arc 共享：采集线程与调用线程都要用，且必须恰好关闭一次。
        let stop_event = Arc::new(StopEvent(stop_handle));

        self.running.store(true, Ordering::SeqCst);

        let callback = self.callback;
        let user_data = self.user_data;
        let running = Arc::clone(&self.running);
        let thread_stop = Arc::clone(&stop_event);

        let worker = std::thread::Builder::new()
            .name("medialink-audio-capture".to_string())
            .spawn(move || {
                // 原先靠尾部一句 running.store(false)，那在 panic 的 unwind 路径上
                // 会被跳过，标志卡真使采集永久无法重启且不报错。播放侧同形问题已修，
                // 这里补上。写法与理由见 crate::run_guarded。
                run_guarded(&running, || {
                    let result = unsafe { capture_loop(callback, user_data, &running, thread_stop.0) };
                    if let Err(err) = result {
                        // 采集线程内无法回传错误串（句柄归调用线程所有），只能落日志式忽略。
                        // 托管侧据「帧不再到达」感知失败，与 native 缺失的降级路径同构。
                        let _ = err;
                    }
                });
            })
            .map_err(|err| AudioError::device(format!("创建采集线程失败：{err}")))?;

        self.worker = Some(worker);
        self.stop_event = Some(stop_event);
        Ok(())
    }

    /// 停止并**同步等待采集线程退出**。
    ///
    /// 必须等：线程仍持有 C# 传来的回调指针与 user_data，提前返回会让托管侧
    /// 释放 GCHandle 后线程还在往里写，那是进程级崩溃而非可恢复的托管异常。
    pub fn stop(&mut self) {
        if let Some(event) = &self.stop_event {
            unsafe {
                let _ = SetEvent(event.0);
            }
        }

        self.running.store(false, Ordering::SeqCst);

        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }

        self.stop_event = None;
    }
}

impl Drop for WasapiLoopbackCapture {
    fn drop(&mut self) {
        self.stop();
    }
}

/// 采集主循环。COM 在本线程初始化并在退出前反初始化——COM 单元是线程局部的。
unsafe fn capture_loop(
    callback: AudioFrameCallback,
    user_data: usize,
    running: &AtomicBool,
    stop_event: HANDLE,
) -> Result<(), AudioError> {
    CoInitializeEx(None, COINIT_MULTITHREADED)
        .ok()
        .map_err(|err| AudioError::device(format!("CoInitializeEx 失败：{err}")))?;

    let result = capture_loop_inner(callback, user_data, running, stop_event);

    CoUninitialize();
    result
}

unsafe fn capture_loop_inner(
    callback: AudioFrameCallback,
    user_data: usize,
    running: &AtomicBool,
    stop_event: HANDLE,
) -> Result<(), AudioError> {
    let enumerator: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)
        .map_err(|err| AudioError::device(format!("创建设备枚举器失败：{err}")))?;

    // eRender + LOOPBACK：抓的是默认输出端点的全量混音，包含本机所有正在出声的应用。
    // 进程级 loopback 不做（见 design.md）：它要求 TargetProcessId，而 SMTC 给的是
    // AUMID 字符串，两者之间没有官方映射。
    let device = enumerator
        .GetDefaultAudioEndpoint(eRender, eConsole)
        .map_err(|err| AudioError::device(format!("获取默认输出设备失败：{err}")))?;

    let client: IAudioClient = device
        .Activate(CLSCTX_ALL, None)
        .map_err(|err| AudioError::device(format!("激活音频客户端失败：{err}")))?;

    let mix_format_ptr = client
        .GetMixFormat()
        .map_err(|err| AudioError::device(format!("获取混音格式失败：{err}")))?;
    let mix = parse_mix_format(mix_format_ptr)?;

    let init_result = client.Initialize(
        AUDCLNT_SHAREMODE_SHARED,
        AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
        BUFFER_DURATION_100NS,
        0,
        mix_format_ptr,
        None,
    );
    CoTaskMemFree(Some(mix_format_ptr as *const c_void));
    init_result.map_err(|err| AudioError::device(format!("初始化音频客户端失败：{err}")))?;

    let buffer_event = CreateEventW(None, false, false, None)
        .map_err(|err| AudioError::device(format!("创建缓冲事件失败：{err}")))?;
    let _buffer_event_guard = StopEvent(buffer_event);

    client
        .SetEventHandle(buffer_event)
        .map_err(|err| AudioError::device(format!("设置事件句柄失败：{err}")))?;

    let capture_client: IAudioCaptureClient = client
        .GetService()
        .map_err(|err| AudioError::device(format!("获取采集客户端失败：{err}")))?;

    client
        .Start()
        .map_err(|err| AudioError::device(format!("启动采集失败：{err}")))?;

    let mut resampler =
        StereoResampler::new(mix.sample_rate, OUTPUT_SAMPLE_RATE, RESAMPLE_CHUNK_FRAMES);

    while running.load(Ordering::SeqCst) {
        // 同时等缓冲事件与停止事件：只等缓冲会在静音时卡到超时才响应停止。
        let handles = [buffer_event, stop_event];
        let wait = wait_for_any(&handles, 200);
        if wait == WaitObject::Stop {
            break;
        }

        loop {
            let Ok(packet_frames) = capture_client.GetNextPacketSize() else {
                break;
            };
            if packet_frames == 0 {
                break;
            }

            let mut data_ptr = std::ptr::null_mut();
            let mut frames_available = 0u32;
            let mut flags = 0u32;
            let mut qpc_position = 0u64;

            if capture_client
                .GetBuffer(
                    &mut data_ptr,
                    &mut frames_available,
                    &mut flags,
                    None,
                    Some(&mut qpc_position),
                )
                .is_err()
            {
                break;
            }

            let is_silent = flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0;
            let byte_len = frames_available as usize * mix.block_align;
            let raw = std::slice::from_raw_parts(data_ptr, byte_len);

            let pcm = build_output_frame(raw, &mix, is_silent, &mut resampler);

            if !pcm.is_empty() {
                let frame = AudioFrame::new(&pcm, is_silent, qpc_position);
                callback(&frame, user_data as *mut c_void);
            }

            let _ = capture_client.ReleaseBuffer(frames_available);
        }
    }

    let _ = client.Stop();
    Ok(())
}

/// 把设备原始字节转成传输格式的交错 i16。
///
/// 静音包的 `data_ptr` 内容按 WASAPI 文档是未定义的，故不读它，直接产出等长零值——
/// 这样时间轴照常推进，接收端只是跳过 FFT 计算。
fn build_output_frame(
    raw: &[u8],
    mix: &MixFormat,
    is_silent: bool,
    resampler: &mut Option<StereoResampler>,
) -> Vec<i16> {
    let frames = if mix.block_align == 0 {
        0
    } else {
        raw.len() / mix.block_align
    };

    if is_silent {
        let out_frames = if mix.sample_rate == OUTPUT_SAMPLE_RATE {
            frames
        } else {
            frames * OUTPUT_SAMPLE_RATE as usize / mix.sample_rate.max(1) as usize
        };
        return vec![0i16; out_frames * OUTPUT_CHANNELS as usize];
    }

    let floats = convert::normalize_to_f32(raw, mix.format);
    let stereo = convert::downmix_to_stereo(&floats, mix.channels);

    let resampled = match resampler {
        Some(resampler) => resampler.process_interleaved(&stereo),
        None => stereo,
    };

    convert::f32_to_i16(&resampled)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::convert::SampleFormat;

    fn mix(sample_rate: u32, channels: u16, format: SampleFormat) -> MixFormat {
        MixFormat {
            sample_rate,
            channels,
            format,
            block_align: channels as usize * format.bytes_per_sample(),
        }
    }

    #[test]
    fn silent_packet_yields_zeroed_pcm_of_same_length() {
        // 静音帧必须等长，否则接收端的时间轴会被压缩。
        let mixfmt = mix(48_000, 2, SampleFormat::Pcm16);
        let raw = vec![0xAAu8; 480 * mixfmt.block_align]; // 内容按文档未定义，不应被读取
        let mut resampler = None;

        let out = build_output_frame(&raw, &mixfmt, true, &mut resampler);

        assert_eq!(out.len(), 480 * OUTPUT_CHANNELS as usize);
        assert!(out.iter().all(|&s| s == 0), "静音帧必须是零值而非原始内容");
    }

    #[test]
    fn silent_packet_scales_length_when_resampling() {
        let mixfmt = mix(44_100, 2, SampleFormat::Pcm16);
        let raw = vec![0u8; 441 * mixfmt.block_align];
        let mut resampler = None;

        let out = build_output_frame(&raw, &mixfmt, true, &mut resampler);

        // 441 帧 @44.1k ≈ 480 帧 @48k
        assert_eq!(out.len() / OUTPUT_CHANNELS as usize, 480);
    }

    #[test]
    fn passthrough_at_48k_stereo_s16() {
        let mixfmt = mix(48_000, 2, SampleFormat::Pcm16);
        let samples: Vec<i16> = vec![100, -100, 200, -200];
        let raw = convert::i16_to_le_bytes(&samples);
        let mut resampler = None;

        let out = build_output_frame(&raw, &mixfmt, false, &mut resampler);

        assert_eq!(out, samples);
    }

    #[test]
    fn mono_source_becomes_stereo() {
        let mixfmt = mix(48_000, 1, SampleFormat::Pcm16);
        let raw = convert::i16_to_le_bytes(&[500, 600]);
        let mut resampler = None;

        let out = build_output_frame(&raw, &mixfmt, false, &mut resampler);

        assert_eq!(out, vec![500, 500, 600, 600]);
    }

    #[test]
    fn zero_block_align_does_not_divide_by_zero() {
        let mixfmt = MixFormat {
            sample_rate: 48_000,
            channels: 2,
            format: SampleFormat::Pcm16,
            block_align: 0,
        };
        let mut resampler = None;

        let out = build_output_frame(&[], &mixfmt, true, &mut resampler);

        assert!(out.is_empty());
    }
}
