//! FFI 边界。
//!
//! 与 `ttml-ffi` 的请求-响应形态不同，音频是持续流，故改为**回调推送**。
//!
//! # 回调的内存契约（违反即崩溃或读到脏数据）
//!
//! 回调期间 [`AudioFrame::samples`] 借用 Rust 侧缓冲，**回调返回后立即失效**。
//! C# 必须在回调内同步拷出。如此零分配、无需 `_free`、不产生跨语言所有权问题。
//!
//! C# 侧回调须为 `[UnmanagedCallersOnly]` 静态方法，实例经 `GCHandle` 传递——
//! 用实例委托会被 GC 回收，表现为随机时刻的 AccessViolation。

use std::ffi::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

pub mod convert;
pub mod render;
pub mod ring;

#[cfg(windows)]
pub mod capture;

pub const ABI_VERSION: u32 = 2;

/// 传输格式恒为 48000Hz / 2 声道 / i16，与 `server.hello` 的 `audio` 声明一致。
pub const OUTPUT_SAMPLE_RATE: u32 = 48_000;
pub const OUTPUT_CHANNELS: u16 = 2;

pub const STATUS_OK: i32 = 0;
pub const STATUS_INVALID_ARG: i32 = 1;
pub const STATUS_UNSUPPORTED_PLATFORM: i32 = 2;
pub const STATUS_DEVICE_ERROR: i32 = 3;
pub const STATUS_ALREADY_RUNNING: i32 = 4;
pub const STATUS_PANIC: i32 = 5;

/// 一块采集到的 PCM。字段布局是 C# 侧 `StructLayout(LayoutKind.Sequential)` 的镜像，
/// 改动即 ABI 变更，须同步提升 [`ABI_VERSION`]。
#[repr(C)]
pub struct AudioFrame {
    /// 交错 L,R,L,R...；**仅在回调期间有效**。
    pub samples: *const i16,
    /// 每声道采样数。字节数 = `frame_count * channels * 2`。
    pub frame_count: usize,
    /// 恒 [`OUTPUT_SAMPLE_RATE`]，仍显式传递——让接收端读到的是数据而非约定。
    pub sample_rate: u32,
    pub channels: u16,
    /// 本块是否静音（`AUDCLNT_BUFFERFLAGS_SILENT`）。
    ///
    /// 静音时仍送出**完整长度的零值 PCM** 而非跳过该包：跳过会让接收端 FFT
    /// 冻结在最后一帧波形上而非归零。这是与 smtc-suite 的第三处偏离。
    pub is_silent: u8,
    _padding: u8,
    /// `GetBuffer` 的 `pu64QPCPosition`，100ns 单位。原样传出不做换算——
    /// Rust 侧不知晓曲目概念，曲目位置由 C# 侧结合 SMTC 插值位置推出。
    pub qpc_position: u64,
}

impl AudioFrame {
    pub(crate) fn new(samples: &[i16], is_silent: bool, qpc_position: u64) -> Self {
        Self {
            samples: samples.as_ptr(),
            frame_count: samples.len() / OUTPUT_CHANNELS as usize,
            sample_rate: OUTPUT_SAMPLE_RATE,
            channels: OUTPUT_CHANNELS,
            is_silent: u8::from(is_silent),
            _padding: 0,
            qpc_position,
        }
    }
}

pub type AudioFrameCallback = extern "C" fn(*const AudioFrame, *mut c_void);

/// 一块**已写进设备缓冲**的 PCM，供 C# 侧接着喂频谱。布局同 [`AudioFrame`] 的前四项。
///
/// 没有 `is_silent` 与 `qpc_position`：播放侧的静音就是零值 PCM，没有单独的标志位；
/// 而时间戳由 C# 侧在收到回调时取本机时刻，native 侧的 QPC 对它无增量。
///
/// 送出的是**重采样前的 48000/2ch/i16**，不是设备格式的那一份——C# 的分析器只认这一种。
///
/// # 精度说明
///
/// 回调在 PCM 写进 WASAPI 缓冲后触发，而写进缓冲不等于已出声：共享模式下还隔着
/// 10–30ms 的端点缓冲。**残余误差是 10–30ms 而非零**，在 50ms 可察觉阈值之下。
/// 记这一条是因为「回调已播出的 PCM」容易被读成零误差，据此去调别的延迟会调错方向。
#[repr(C)]
pub struct PlayedFrame {
    /// 交错 L,R,L,R...；**仅在回调期间有效**。
    pub samples: *const i16,
    pub frame_count: usize,
    pub sample_rate: u32,
    pub channels: u16,
}

impl PlayedFrame {
    pub(crate) fn new(samples: &[i16]) -> Self {
        Self {
            samples: samples.as_ptr(),
            frame_count: samples.len() / OUTPUT_CHANNELS as usize,
            sample_rate: OUTPUT_SAMPLE_RATE,
            channels: OUTPUT_CHANNELS,
        }
    }
}

pub type PlayedFrameCallback = extern "C" fn(*const PlayedFrame, *mut c_void);

/// 采集句柄。跨 FFI 传递的是它的裸指针。
pub struct CaptureHandle {
    #[cfg(windows)]
    inner: capture::WasapiLoopbackCapture,
    last_error: Option<String>,
}

#[no_mangle]
pub extern "C" fn mediaisland_audio_abi_version() -> u32 {
    ABI_VERSION
}

/// 创建采集句柄。`user_data` 原样回传给回调，C# 侧用它还原 `GCHandle`。
///
/// # Safety
/// `out_handle` 必须指向可写的指针大小内存。
#[no_mangle]
pub unsafe extern "C" fn mediaisland_audio_capture_create(
    callback: AudioFrameCallback,
    user_data: *mut c_void,
    out_handle: *mut *mut CaptureHandle,
) -> i32 {
    if out_handle.is_null() {
        return STATUS_INVALID_ARG;
    }

    *out_handle = ptr::null_mut();

    #[cfg(not(windows))]
    {
        let _ = (callback, user_data);
        return STATUS_UNSUPPORTED_PLATFORM;
    }

    #[cfg(windows)]
    {
        let result = catch_unwind(AssertUnwindSafe(|| {
            capture::WasapiLoopbackCapture::new(callback, user_data as usize)
        }));

        match result {
            Ok(inner) => {
                let handle = Box::new(CaptureHandle {
                    inner,
                    last_error: None,
                });
                *out_handle = Box::into_raw(handle);
                STATUS_OK
            }
            Err(_) => STATUS_PANIC,
        }
    }
}

/// # Safety
/// `handle` 必须是 [`mediaisland_audio_capture_create`] 返回且尚未销毁的指针。
#[no_mangle]
pub unsafe extern "C" fn mediaisland_audio_capture_start(handle: *mut CaptureHandle) -> i32 {
    let Some(handle) = handle.as_mut() else {
        return STATUS_INVALID_ARG;
    };

    #[cfg(not(windows))]
    {
        handle.last_error = Some("当前平台不支持音频采集".to_string());
        STATUS_UNSUPPORTED_PLATFORM
    }

    #[cfg(windows)]
    {
        match catch_unwind(AssertUnwindSafe(|| handle.inner.start())) {
            Ok(Ok(())) => {
                handle.last_error = None;
                STATUS_OK
            }
            Ok(Err(err)) => {
                let status = err.status;
                handle.last_error = Some(err.message);
                status
            }
            Err(_) => {
                handle.last_error = Some("采集启动时发生 panic".to_string());
                STATUS_PANIC
            }
        }
    }
}

/// 停止采集。**同步等待采集线程真正退出**再返回——否则关闭竞态会崩在 native 里，
/// 那种崩溃会带走整个宿主进程，不是可恢复的托管异常。
///
/// # Safety
/// 同 [`mediaisland_audio_capture_start`]。
#[no_mangle]
pub unsafe extern "C" fn mediaisland_audio_capture_stop(handle: *mut CaptureHandle) -> i32 {
    let Some(handle) = handle.as_mut() else {
        return STATUS_INVALID_ARG;
    };

    #[cfg(not(windows))]
    {
        let _ = &handle;
        STATUS_UNSUPPORTED_PLATFORM
    }

    #[cfg(windows)]
    {
        match catch_unwind(AssertUnwindSafe(|| handle.inner.stop())) {
            Ok(()) => STATUS_OK,
            Err(_) => {
                handle.last_error = Some("采集停止时发生 panic".to_string());
                STATUS_PANIC
            }
        }
    }
}

/// # Safety
/// `handle` 此后不可再用。
#[no_mangle]
pub unsafe extern "C" fn mediaisland_audio_capture_destroy(handle: *mut CaptureHandle) {
    if handle.is_null() {
        return;
    }

    let mut handle = Box::from_raw(handle);

    // 先停再释放：采集线程仍持有回调指针，此时释放会让它写进已回收的内存。
    #[cfg(windows)]
    {
        let _ = catch_unwind(AssertUnwindSafe(|| handle.inner.stop()));
    }

    #[cfg(not(windows))]
    {
        handle.last_error = None;
    }
}

/// 取最近一次错误的 UTF-8 描述。返回的缓冲需由 [`mediaisland_audio_free`] 释放。
///
/// # Safety
/// 同 [`mediaisland_audio_capture_start`]。
#[no_mangle]
pub unsafe extern "C" fn mediaisland_audio_last_error(handle: *mut CaptureHandle) -> FfiBuffer {
    let Some(handle) = handle.as_ref() else {
        return FfiBuffer::empty();
    };

    match &handle.last_error {
        Some(message) => FfiBuffer::from_vec(message.clone().into_bytes()),
        None => FfiBuffer::empty(),
    }
}

/// 播放句柄。跨 FFI 传递的是它的裸指针。
///
/// **所有 render 导出都只取 `&RenderHandle`，从不取 `&mut`。** C# 侧会在网络线程
/// `push`、在 UI 线程 `stop`，若任一导出取 `&mut`，两个引用就同时指向同一对象——
/// 那在 Rust 里是 UB，且靠「文档要求调用方串行」保不住。故 `last_error` 上锁，
/// `WasapiRenderer` 的 start / stop / push 也全部只需 `&self`。
pub struct RenderHandle {
    #[cfg(windows)]
    inner: render::WasapiRenderer,
    last_error: std::sync::Mutex<Option<String>>,
}

impl RenderHandle {
    /// 记下错误串。锁中毒时放弃记录——诊断信息丢失好过在 FFI 边界上 panic。
    fn set_error(&self, message: Option<String>) {
        if let Ok(mut slot) = self.last_error.lock() {
            *slot = message;
        }
    }
}

/// 创建播放句柄。`user_data` 原样回传给回调，C# 侧用它还原 `GCHandle`。
///
/// # Safety
/// `out_handle` 必须指向可写的指针大小内存。
#[no_mangle]
pub unsafe extern "C" fn mediaisland_audio_render_create(
    callback: PlayedFrameCallback,
    user_data: *mut c_void,
    out_handle: *mut *mut RenderHandle,
) -> i32 {
    if out_handle.is_null() {
        return STATUS_INVALID_ARG;
    }

    *out_handle = ptr::null_mut();

    #[cfg(not(windows))]
    {
        let _ = (callback, user_data);
        return STATUS_UNSUPPORTED_PLATFORM;
    }

    #[cfg(windows)]
    {
        let result = catch_unwind(AssertUnwindSafe(|| {
            render::WasapiRenderer::new(callback, user_data as usize)
        }));

        match result {
            Ok(inner) => {
                let handle = Box::new(RenderHandle {
                    inner,
                    last_error: std::sync::Mutex::new(None),
                });
                *out_handle = Box::into_raw(handle);
                STATUS_OK
            }
            Err(_) => STATUS_PANIC,
        }
    }
}

/// 起播。`target_buffer_ms` 是抖动缓冲的目标深度，越界值被夹到受支持的范围内
/// 而非报错——它来自用户设置，夹紧比让播放整个失败更符合预期。
///
/// **不支持在线调整深度**：改动设置即停播重启。深度变更要么丢音要么静音填充，
/// 两者都不如一次干净的重启，而这是罕见操作。
///
/// # Safety
/// `handle` 必须是 [`mediaisland_audio_render_create`] 返回且尚未销毁的指针。
#[no_mangle]
pub unsafe extern "C" fn mediaisland_audio_render_start(
    handle: *mut RenderHandle,
    target_buffer_ms: u32,
) -> i32 {
    let Some(handle) = handle.as_ref() else {
        return STATUS_INVALID_ARG;
    };

    #[cfg(not(windows))]
    {
        let _ = target_buffer_ms;
        handle.set_error(Some("当前平台不支持音频播放".to_string()));
        STATUS_UNSUPPORTED_PLATFORM
    }

    #[cfg(windows)]
    {
        match catch_unwind(AssertUnwindSafe(|| handle.inner.start(target_buffer_ms))) {
            Ok(Ok(())) => {
                handle.set_error(None);
                STATUS_OK
            }
            Ok(Err(err)) => {
                let status = err.status;
                handle.set_error(Some(err.message));
                status
            }
            Err(_) => {
                handle.set_error(Some("播放启动时发生 panic".to_string()));
                STATUS_PANIC
            }
        }
    }
}

/// 送入交错 i16 立体声。
///
/// `frame_count` 是**每声道**采样数，故样本总数为 `frame_count * 2`。
/// 调用方按字节数算帧数时**必须向下取整到整帧**：环形缓冲不跨调用结转半帧，
/// 多出来的样本会被丢弃。
///
/// # Safety
/// `handle` 同 [`mediaisland_audio_render_start`]；`samples` 须指向至少
/// `frame_count * 2` 个 `i16`，仅在本次调用期间被读取。
#[no_mangle]
pub unsafe extern "C" fn mediaisland_audio_render_push(
    handle: *mut RenderHandle,
    samples: *const i16,
    frame_count: usize,
) -> i32 {
    let Some(handle) = handle.as_ref() else {
        return STATUS_INVALID_ARG;
    };

    // 空指针配零长度也要短路：slice::from_raw_parts(null, 0) 在 Rust 里是 UB，
    // 而「零长度所以无所谓」这个直觉恰恰不成立。
    if frame_count == 0 {
        return STATUS_OK;
    }
    if samples.is_null() {
        return STATUS_INVALID_ARG;
    }

    #[cfg(not(windows))]
    {
        handle.set_error(Some("当前平台不支持音频播放".to_string()));
        STATUS_UNSUPPORTED_PLATFORM
    }

    #[cfg(windows)]
    {
        let interleaved =
            std::slice::from_raw_parts(samples, frame_count * OUTPUT_CHANNELS as usize);

        match catch_unwind(AssertUnwindSafe(|| handle.inner.push(interleaved))) {
            Ok(()) => STATUS_OK,
            Err(_) => {
                handle.set_error(Some("送入播放数据时发生 panic".to_string()));
                STATUS_PANIC
            }
        }
    }
}

/// 停止播放。**同步等待渲染线程真正退出**再返回，理由同
/// [`mediaisland_audio_capture_stop`]。
///
/// # Safety
/// 同 [`mediaisland_audio_render_start`]。
#[no_mangle]
pub unsafe extern "C" fn mediaisland_audio_render_stop(handle: *mut RenderHandle) -> i32 {
    let Some(handle) = handle.as_ref() else {
        return STATUS_INVALID_ARG;
    };

    #[cfg(not(windows))]
    {
        let _ = &handle;
        STATUS_UNSUPPORTED_PLATFORM
    }

    #[cfg(windows)]
    {
        match catch_unwind(AssertUnwindSafe(|| handle.inner.stop())) {
            Ok(()) => STATUS_OK,
            Err(_) => {
                handle.set_error(Some("播放停止时发生 panic".to_string()));
                STATUS_PANIC
            }
        }
    }
}

/// # Safety
/// `handle` 此后不可再用。
#[no_mangle]
pub unsafe extern "C" fn mediaisland_audio_render_destroy(handle: *mut RenderHandle) {
    if handle.is_null() {
        return;
    }

    let handle = Box::from_raw(handle);

    // 先停再释放：渲染线程仍持有回调指针，此时释放会让它写进已回收的内存。
    #[cfg(windows)]
    {
        let _ = catch_unwind(AssertUnwindSafe(|| handle.inner.stop()));
    }

    drop(handle);
}

/// 取播放侧最近一次错误。返回的缓冲需由 [`mediaisland_audio_free`] 释放。
///
/// # Safety
/// 同 [`mediaisland_audio_render_start`]。
#[no_mangle]
pub unsafe extern "C" fn mediaisland_audio_render_last_error(
    handle: *mut RenderHandle,
) -> FfiBuffer {
    let Some(handle) = handle.as_ref() else {
        return FfiBuffer::empty();
    };

    let Ok(slot) = handle.last_error.lock() else {
        return FfiBuffer::empty();
    };

    match slot.as_ref() {
        Some(message) => FfiBuffer::from_vec(message.clone().into_bytes()),
        None => FfiBuffer::empty(),
    }
}

/// 与 `ttml-ffi` 的 `FfiBuffer` 同布局，C# 侧可复用既有的读取与释放写法。
#[repr(C)]
pub struct FfiBuffer {
    pub ptr: *mut u8,
    pub len: usize,
    pub status: i32,
}

impl FfiBuffer {
    fn empty() -> Self {
        Self {
            ptr: ptr::null_mut(),
            len: 0,
            status: STATUS_OK,
        }
    }

    fn from_vec(bytes: Vec<u8>) -> Self {
        let len = bytes.len();
        let mut boxed = bytes.into_boxed_slice();
        let ptr = boxed.as_mut_ptr();
        std::mem::forget(boxed);
        Self {
            ptr,
            len,
            status: STATUS_OK,
        }
    }
}

/// # Safety
/// `ptr` / `len` 必须来自本库返回的 [`FfiBuffer`]，且只释放一次。
#[no_mangle]
pub unsafe extern "C" fn mediaisland_audio_free(ptr: *mut u8, len: usize) {
    if ptr.is_null() || len == 0 {
        return;
    }

    drop(Vec::from_raw_parts(ptr, len, len));
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn abi_version_is_stable() {
        // ABI 版本是 C# 侧 ExpectedAbiVersion 的对端，改动必须是有意识的。
        assert_eq!(mediaisland_audio_abi_version(), 2);
    }

    #[test]
    fn played_frame_reports_per_channel_count() {
        let samples = [1i16, 2, 3, 4, 5, 6];

        let frame = PlayedFrame::new(&samples);

        assert_eq!(frame.frame_count, 3);
        assert_eq!(frame.channels, OUTPUT_CHANNELS);
        assert_eq!(frame.sample_rate, OUTPUT_SAMPLE_RATE);
    }

    #[test]
    fn frame_reports_per_channel_count() {
        let samples = [1i16, 2, 3, 4, 5, 6];

        let frame = AudioFrame::new(&samples, false, 42);

        assert_eq!(frame.frame_count, 3);
        assert_eq!(frame.channels, 2);
        assert_eq!(frame.sample_rate, 48_000);
        assert_eq!(frame.is_silent, 0);
        assert_eq!(frame.qpc_position, 42);
    }

    #[test]
    fn silent_frame_still_carries_samples() {
        // 静音帧携带完整长度的零值 PCM，接收端才能推进时间轴而非冻结。
        let samples = [0i16; 8];

        let frame = AudioFrame::new(&samples, true, 0);

        assert_eq!(frame.is_silent, 1);
        assert_eq!(frame.frame_count, 4);
    }

    #[test]
    fn create_rejects_null_out_handle() {
        extern "C" fn noop(_: *const AudioFrame, _: *mut c_void) {}

        let status = unsafe {
            mediaisland_audio_capture_create(noop, ptr::null_mut(), ptr::null_mut())
        };

        assert_eq!(status, STATUS_INVALID_ARG);
    }

    #[test]
    fn start_rejects_null_handle() {
        assert_eq!(
            unsafe { mediaisland_audio_capture_start(ptr::null_mut()) },
            STATUS_INVALID_ARG
        );
    }

    #[test]
    fn destroy_tolerates_null() {
        unsafe { mediaisland_audio_capture_destroy(ptr::null_mut()) };
    }

    #[test]
    fn free_tolerates_null_and_zero_len() {
        unsafe {
            mediaisland_audio_free(ptr::null_mut(), 0);
            mediaisland_audio_free(ptr::null_mut(), 8);
        }
    }

    #[test]
    fn error_buffer_round_trips() {
        let buffer = FfiBuffer::from_vec("设备被独占".as_bytes().to_vec());
        assert!(!buffer.ptr.is_null());

        let text = unsafe {
            std::str::from_utf8(std::slice::from_raw_parts(buffer.ptr, buffer.len))
                .unwrap()
                .to_string()
        };
        assert_eq!(text, "设备被独占");

        unsafe { mediaisland_audio_free(buffer.ptr, buffer.len) };
    }

    extern "C" fn noop_played(_: *const PlayedFrame, _: *mut c_void) {}

    #[test]
    fn render_create_rejects_null_out_handle() {
        let status = unsafe {
            mediaisland_audio_render_create(noop_played, ptr::null_mut(), ptr::null_mut())
        };

        assert_eq!(status, STATUS_INVALID_ARG);
    }

    #[test]
    fn render_start_rejects_null_handle() {
        assert_eq!(
            unsafe { mediaisland_audio_render_start(ptr::null_mut(), 200) },
            STATUS_INVALID_ARG
        );
    }

    #[test]
    fn render_push_rejects_null_handle() {
        let samples = [0i16; 4];
        assert_eq!(
            unsafe { mediaisland_audio_render_push(ptr::null_mut(), samples.as_ptr(), 2) },
            STATUS_INVALID_ARG
        );
    }

    #[test]
    fn render_stop_rejects_null_handle() {
        assert_eq!(
            unsafe { mediaisland_audio_render_stop(ptr::null_mut()) },
            STATUS_INVALID_ARG
        );
    }

    #[test]
    fn render_destroy_tolerates_null() {
        unsafe { mediaisland_audio_render_destroy(ptr::null_mut()) };
    }

    #[test]
    fn render_last_error_null_handle_is_empty() {
        let buffer = unsafe { mediaisland_audio_render_last_error(ptr::null_mut()) };

        assert!(buffer.ptr.is_null());
        assert_eq!(buffer.len, 0);
    }

    #[test]
    fn render_push_of_zero_frames_never_builds_a_slice() {
        // slice::from_raw_parts(null, 0) 在 Rust 里是 UB，「零长度所以无所谓」
        // 这个直觉不成立。零帧必须在取切片之前就短路掉。
        //
        // 用真句柄而非 null：null 会先被句柄检查挡掉，那样这条测的就不是短路。
        let mut handle: *mut RenderHandle = ptr::null_mut();
        let created =
            unsafe { mediaisland_audio_render_create(noop_played, ptr::null_mut(), &mut handle) };
        if created != STATUS_OK {
            // 非 Windows 平台不建句柄，此路径无从驱动。
            return;
        }

        let status = unsafe { mediaisland_audio_render_push(handle, ptr::null(), 0) };

        assert_eq!(status, STATUS_OK);
        unsafe { mediaisland_audio_render_destroy(handle) };
    }

    #[test]
    fn render_push_rejects_null_samples_with_frames() {
        let mut handle: *mut RenderHandle = ptr::null_mut();
        let created =
            unsafe { mediaisland_audio_render_create(noop_played, ptr::null_mut(), &mut handle) };
        if created != STATUS_OK {
            return;
        }

        let status = unsafe { mediaisland_audio_render_push(handle, ptr::null(), 4) };

        assert_eq!(status, STATUS_INVALID_ARG);
        unsafe { mediaisland_audio_render_destroy(handle) };
    }

    #[test]
    fn render_stop_without_start_is_ok() {
        // 托管侧停服路径会无条件调 stop，此时可能从未起播过。
        let mut handle: *mut RenderHandle = ptr::null_mut();
        let created =
            unsafe { mediaisland_audio_render_create(noop_played, ptr::null_mut(), &mut handle) };
        if created != STATUS_OK {
            return;
        }

        assert_eq!(unsafe { mediaisland_audio_render_stop(handle) }, STATUS_OK);
        unsafe { mediaisland_audio_render_destroy(handle) };
    }
}
