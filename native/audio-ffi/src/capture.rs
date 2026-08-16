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

use windows::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0};
use windows::Win32::Media::Audio::{
    eConsole, eRender, IAudioCaptureClient, IAudioClient, IMMDeviceEnumerator, MMDeviceEnumerator,
    AUDCLNT_BUFFERFLAGS_SILENT, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
    AUDCLNT_STREAMFLAGS_LOOPBACK, WAVEFORMATEX, WAVEFORMATEXTENSIBLE,
};
use windows::Win32::Media::KernelStreaming::{KSDATAFORMAT_SUBTYPE_PCM, WAVE_FORMAT_EXTENSIBLE};
use windows::Win32::Media::Multimedia::KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
use windows::Win32::System::Com::{
    CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_ALL,
    COINIT_MULTITHREADED,
};
use windows::Win32::System::Threading::{CreateEventW, SetEvent, WaitForSingleObject};

use crate::convert::{self, SampleFormat, StereoResampler};
use crate::{
    AudioFrame, AudioFrameCallback, OUTPUT_CHANNELS, OUTPUT_SAMPLE_RATE, STATUS_ALREADY_RUNNING,
    STATUS_DEVICE_ERROR,
};

/// 20ms 缓冲。这是 WASAPI 共享模式下的实用下限，再降需承担 glitch 风险。
const BUFFER_DURATION_100NS: i64 = 20 * 10_000;

/// 重采样的定长块，对应 20ms @ 48kHz。仅非 48kHz 设备走这条路径。
const RESAMPLE_CHUNK_FRAMES: usize = 960;

#[derive(Debug)]
pub struct CaptureError {
    pub message: String,
    pub status: i32,
}

impl CaptureError {
    fn device(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
            status: STATUS_DEVICE_ERROR,
        }
    }
}

/// 设备混音格式的解析结果。播放侧同样按它成型输出，故对 crate 内可见。
pub(crate) struct MixFormat {
    pub(crate) sample_rate: u32,
    pub(crate) channels: u16,
    pub(crate) format: SampleFormat,
    pub(crate) block_align: usize,
}

pub struct WasapiLoopbackCapture {
    callback: AudioFrameCallback,
    user_data: usize,
    running: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
    stop_event: Option<Arc<StopEvent>>,
}

/// 停止事件的所有权包装，确保句柄只被关闭一次。
pub(crate) struct StopEvent(pub(crate) HANDLE);

// HANDLE 是裸指针包装，Windows 事件对象本身可跨线程使用。
unsafe impl Send for StopEvent {}
unsafe impl Sync for StopEvent {}

impl Drop for StopEvent {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
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

    pub fn start(&mut self) -> Result<(), CaptureError> {
        if self.running.load(Ordering::SeqCst) {
            return Err(CaptureError {
                message: "采集已在运行".to_string(),
                status: STATUS_ALREADY_RUNNING,
            });
        }

        let stop_handle = unsafe { CreateEventW(None, true, false, None) }
            .map_err(|err| CaptureError::device(format!("创建停止事件失败：{err}")))?;
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
                let result = unsafe { capture_loop(callback, user_data, &running, thread_stop.0) };
                running.store(false, Ordering::SeqCst);
                if let Err(err) = result {
                    // 采集线程内无法回传错误串（句柄归调用线程所有），只能落日志式忽略。
                    // 托管侧据「帧不再到达」感知失败，与 native 缺失的降级路径同构。
                    let _ = err;
                }
            })
            .map_err(|err| CaptureError::device(format!("创建采集线程失败：{err}")))?;

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
) -> Result<(), CaptureError> {
    CoInitializeEx(None, COINIT_MULTITHREADED)
        .ok()
        .map_err(|err| CaptureError::device(format!("CoInitializeEx 失败：{err}")))?;

    let result = capture_loop_inner(callback, user_data, running, stop_event);

    CoUninitialize();
    result
}

unsafe fn capture_loop_inner(
    callback: AudioFrameCallback,
    user_data: usize,
    running: &AtomicBool,
    stop_event: HANDLE,
) -> Result<(), CaptureError> {
    let enumerator: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)
        .map_err(|err| CaptureError::device(format!("创建设备枚举器失败：{err}")))?;

    // eRender + LOOPBACK：抓的是默认输出端点的全量混音，包含本机所有正在出声的应用。
    // 进程级 loopback 不做（见 design.md）：它要求 TargetProcessId，而 SMTC 给的是
    // AUMID 字符串，两者之间没有官方映射。
    let device = enumerator
        .GetDefaultAudioEndpoint(eRender, eConsole)
        .map_err(|err| CaptureError::device(format!("获取默认输出设备失败：{err}")))?;

    let client: IAudioClient = device
        .Activate(CLSCTX_ALL, None)
        .map_err(|err| CaptureError::device(format!("激活音频客户端失败：{err}")))?;

    let mix_format_ptr = client
        .GetMixFormat()
        .map_err(|err| CaptureError::device(format!("获取混音格式失败：{err}")))?;
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
    init_result.map_err(|err| CaptureError::device(format!("初始化音频客户端失败：{err}")))?;

    let buffer_event = CreateEventW(None, false, false, None)
        .map_err(|err| CaptureError::device(format!("创建缓冲事件失败：{err}")))?;
    let _buffer_event_guard = StopEvent(buffer_event);

    client
        .SetEventHandle(buffer_event)
        .map_err(|err| CaptureError::device(format!("设置事件句柄失败：{err}")))?;

    let capture_client: IAudioCaptureClient = client
        .GetService()
        .map_err(|err| CaptureError::device(format!("获取采集客户端失败：{err}")))?;

    client
        .Start()
        .map_err(|err| CaptureError::device(format!("启动采集失败：{err}")))?;

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

#[derive(PartialEq, Eq)]
pub(crate) enum WaitObject {
    Buffer,
    Stop,
    Timeout,
}

pub(crate) unsafe fn wait_for_any(handles: &[HANDLE; 2], timeout_ms: u32) -> WaitObject {
    // 逐个轮询而非 WaitForMultipleObjects：停止事件是手动重置的，先查它可保证
    // 停止请求不会被持续到达的缓冲事件饿死。
    if WaitForSingleObject(handles[1], 0) == WAIT_OBJECT_0 {
        return WaitObject::Stop;
    }

    if WaitForSingleObject(handles[0], timeout_ms) == WAIT_OBJECT_0 {
        return WaitObject::Buffer;
    }

    if WaitForSingleObject(handles[1], 0) == WAIT_OBJECT_0 {
        return WaitObject::Stop;
    }

    WaitObject::Timeout
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

/// 解析设备混音格式。
///
/// `WAVE_FORMAT_EXTENSIBLE` 时真正的格式在 `SubFormat` GUID 里，`wFormatTag` 只是个占位。
pub(crate) unsafe fn parse_mix_format(ptr: *const WAVEFORMATEX) -> Result<MixFormat, CaptureError> {
    if ptr.is_null() {
        return Err(CaptureError::device("混音格式为空"));
    }

    let wave = &*ptr;
    let bits = wave.wBitsPerSample;

    let format = if wave.wFormatTag as u32 == WAVE_FORMAT_EXTENSIBLE {
        let extensible = &*(ptr as *const WAVEFORMATEXTENSIBLE);
        let sub = extensible.SubFormat;
        if sub == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT {
            SampleFormat::F32
        } else if sub == KSDATAFORMAT_SUBTYPE_PCM {
            integer_format(bits)?
        } else {
            return Err(CaptureError::device(format!(
                "不支持的混音子格式，位深 {bits}"
            )));
        }
    } else if bits == 32 && wave.wFormatTag == 3 {
        // WAVE_FORMAT_IEEE_FLOAT = 3
        SampleFormat::F32
    } else {
        integer_format(bits)?
    };

    Ok(MixFormat {
        sample_rate: wave.nSamplesPerSec,
        channels: wave.nChannels,
        format,
        block_align: wave.nBlockAlign as usize,
    })
}

fn integer_format(bits: u16) -> Result<SampleFormat, CaptureError> {
    match bits {
        16 => Ok(SampleFormat::Pcm16),
        24 => Ok(SampleFormat::Pcm24),
        32 => Ok(SampleFormat::Pcm32),
        other => Err(CaptureError::device(format!("不支持的位深 {other}"))),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

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

    #[test]
    fn integer_formats_are_mapped_by_bit_depth() {
        assert_eq!(integer_format(16).unwrap(), SampleFormat::Pcm16);
        assert_eq!(integer_format(24).unwrap(), SampleFormat::Pcm24);
        assert_eq!(integer_format(32).unwrap(), SampleFormat::Pcm32);
    }

    #[test]
    fn unsupported_bit_depth_is_rejected_with_message() {
        // smtc-suite 在这里直接退出；本实现支持 16/24/32，仅真正未知的位深才失败，
        // 且错误串要能让用户看懂是格式问题。
        let err = integer_format(8).unwrap_err();

        assert!(err.message.contains('8'), "错误串应指出实际位深");
        assert_eq!(err.status, STATUS_DEVICE_ERROR);
    }
}
