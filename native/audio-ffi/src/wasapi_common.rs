//! 采集与播放共用的 WASAPI 原语。
//!
//! 这些项原本住在 capture.rs，于是 render.rs 只能反向 use crate::capture——
//! 而播放并不依赖 loopback 采集，那条边的唯一成因是它们当初先写在那里。
//! 挪到这里之后，capture 与 render 同为兄弟，共同向下引用本模块与 convert，
//! 这才是两者的真实关系：都是 WASAPI 端点的一端，谁都不是谁的基础。

use windows::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0};
use windows::Win32::Media::Audio::{WAVEFORMATEX, WAVEFORMATEXTENSIBLE};
use windows::Win32::Media::KernelStreaming::{KSDATAFORMAT_SUBTYPE_PCM, WAVE_FORMAT_EXTENSIBLE};
use windows::Win32::Media::Multimedia::KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
use windows::Win32::System::Threading::WaitForSingleObject;

use crate::convert::{integer_format, MixFormat, SampleFormat};
use crate::AudioError;

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

/// 解析设备混音格式。
///
/// `WAVE_FORMAT_EXTENSIBLE` 时真正的格式在 `SubFormat` GUID 里，`wFormatTag` 只是个占位。
pub(crate) unsafe fn parse_mix_format(ptr: *const WAVEFORMATEX) -> Result<MixFormat, AudioError> {
    if ptr.is_null() {
        return Err(AudioError::device("混音格式为空"));
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
            return Err(AudioError::device(format!(
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
