//! WASAPI 播放。结构照 `capture.rs`：`new` 建对象、`start` 起线程并同步等启动结果、
//! `stop` 同步等线程退出。关闭竞态崩在 native 里会带走整个宿主进程，
//! 那不是可恢复的托管异常。
//!
//! 与采集侧的两处结构差异：
//!
//! 1. **数据源在对面。** 采集的数据源是 WASAPI，播放的数据源是 FFI 调用方
//!    （C# 的网络收循环），故环形缓冲跨线程共享、由互斥量保护。
//! 2. **重采样器换 `SincFixedOut`。** 采集侧 `GetBuffer` 给多少就处理多少，输入侧被动；
//!    播放侧相反——本次要输出几帧是 WASAPI 定的，输入是攒在环形缓冲里的任意长度。
//!
//! **`played_cb` 送的是重采样前的 48k i16**，不是写进设备缓冲的那一份：
//! C# 侧的分析器只认 48000/2ch/i16。

use std::sync::atomic::{AtomicU64, Ordering as AtomicOrdering};

use crate::OUTPUT_SAMPLE_RATE;

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

/// 容量取目标深度上界的两倍。目标深度是**稳态**深度，尖峰到来时缓冲必须还有地方放；
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

/// 送给 `rubato` 的重采样比率。
///
/// 两个方向必须分清，接反了在真机上表现为「放十几分钟后开始周期性卡顿」，
/// 极难归因到一个符号上：
///
/// - `ring::drift_ratio` 说的是**播放速度**：缓冲比目标深 → 放快一点 → 返回 > 1。
/// - `rubato` 的比率是**输出帧数 / 输入帧数**：本次要输出的帧数是 WASAPI 定的，
///   放快一点等价于**多吃输入**，故比率要变**小**。
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

/// 本轮是否处于预填充；是则返回该送出的零值帧数（**48k 域**）。
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
    writable_device_frames: usize,
    device_rate: u32,
    max_input_frames: usize,
) -> Option<usize> {
    if prefill.is_open(available_frames) {
        return None;
    }

    Some(output_frames_for(writable_device_frames, device_rate).min(max_input_frames))
}

/// 设备帧数折算成同时长的传输帧数（48k 域）。
///
/// **两个域必须分清。** 送给 `played_cb` 的缓冲按 48k 输入帧分配，而 WASAPI 说的
/// 「本轮可写几帧」是设备帧。设备率 >48000 时设备帧数**多于**同时长的 48k 帧数，
/// 拿设备帧数去索引按 48k 分配的缓冲就是越界——96kHz / 20ms 下是 3840 索引进
/// 长 2182 的缓冲，渲染线程当场 panic，而此时 `render_start` 已经返回过 OK。
pub fn output_frames_for(device_frames: usize, device_rate: u32) -> usize {
    if device_rate == 0 || device_rate == OUTPUT_SAMPLE_RATE {
        return device_frames;
    }

    device_frames * OUTPUT_SAMPLE_RATE as usize / device_rate as usize
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

/// 渲染线程的统计量。
///
/// 全部原子且只用 Relaxed。Relaxed 够用的理由：这些值不参与同步任何其他内存访问，
/// 读者只要最终看到即可，而它们之间也不需要互相有序。
///
/// 更要紧的是它们绝不去抢 ring 那把互斥量——对面等那把锁的是 WASAPI 实时线程，
/// 而观测手段不该改变被观测对象的时序。
///
/// 代价：六个字段不是同一瞬间的快照，可能跨越一次渲染轮次。判据应看斜率与累计计数的
/// 单调性，不要依赖六元组的瞬时一致性。
#[derive(Default)]
pub struct RenderStatsCell {
    ring_frames: AtomicU64,
    underrun_count: AtomicU64,
    hard_reset_count: AtomicU64,
    device_frames_rendered: AtomicU64,
    device_sample_rate: AtomicU64,
    resample_ratio_ppm: AtomicU64,
}

impl RenderStatsCell {
    pub fn set_ring_frames(&self, frames: usize) {
        self.ring_frames.store(frames as u64, AtomicOrdering::Relaxed);
    }

    pub fn note_underrun(&self) {
        self.underrun_count.fetch_add(1, AtomicOrdering::Relaxed);
    }

    pub fn note_hard_reset(&self) {
        self.hard_reset_count.fetch_add(1, AtomicOrdering::Relaxed);
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
    /// 停播时刻意**不**清：本次会话的累计值是停播后唯一还能读到的诊断信息，
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
    }

    pub fn snapshot(&self) -> crate::RenderStats {
        crate::RenderStats {
            ring_frames: self.ring_frames.load(AtomicOrdering::Relaxed),
            underrun_count: self.underrun_count.load(AtomicOrdering::Relaxed),
            hard_reset_count: self.hard_reset_count.load(AtomicOrdering::Relaxed),
            device_frames_rendered: self
                .device_frames_rendered
                .load(AtomicOrdering::Relaxed),
            device_sample_rate: self.device_sample_rate.load(AtomicOrdering::Relaxed),
            resample_ratio_ppm: self.resample_ratio_ppm.load(AtomicOrdering::Relaxed),
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

    use rubato::{
        Resampler, SincFixedOut, SincInterpolationParameters, SincInterpolationType, WindowFunction,
    };
    use windows::Win32::Foundation::HANDLE;
    use windows::Win32::Media::Audio::{
        eConsole, eRender, IAudioClient, IAudioRenderClient, IMMDeviceEnumerator,
        MMDeviceEnumerator, AUDCLNT_BUFFERFLAGS_SILENT, AUDCLNT_SHAREMODE_SHARED,
        AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
    };
    use windows::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_ALL,
        COINIT_MULTITHREADED,
    };
    use windows::Win32::System::Threading::{CreateEventW, SetEvent};

    use super::{
        clamp_target_ms, frames_to_ms, prefill_silence_frames, ratio_to_ppm, resample_ratio,
        target_frames, PrefillState, RenderStatsCell, MAX_RELATIVE_RATIO, RING_CAPACITY_FRAMES,
        SINC_LEN,
    };
    use crate::convert;
    use crate::convert::MixFormat;
    use crate::ring::PlaybackRing;
    use crate::wasapi_common::{parse_mix_format, wait_for_any, StopEvent, WaitObject};
    use crate::{
        AudioError, PlayedFrame, PlayedFrameCallback, OUTPUT_CHANNELS, OUTPUT_SAMPLE_RATE,
        STATUS_ALREADY_RUNNING, STATUS_PANIC,
    };
    use crate::run_guarded;

    /// 20ms 缓冲，与采集侧同量级。共享模式下的实用下限。
    ///
    /// 单位由 `timeline` 导出，理由同采集侧：REFERENCE_TIME 就是 100ns tick。
    const BUFFER_DURATION_100NS: i64 = 20 * crate::timeline::TICKS_PER_MS;

    /// 线程句柄与停止事件。**收进 `Mutex` 是为了让 `start` / `stop` 只需 `&self`。**
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

        /// 起渲染线程，**同步等它汇报启动结果**再返回。
        ///
        /// 必须等：设备被独占、混音格式不受支持这些失败只有线程里知道，不等就只能靠
        /// 「声音没出来」感知，而那与「上游没在发」无从区分——两者的排查方向相反。
        pub fn start(&self, target_ms: u32) -> Result<(), AudioError> {
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

            let target_ms = clamp_target_ms(target_ms);

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
            let thread_stop = Arc::clone(&stop_event);

            self.running.store(true, Ordering::SeqCst);

            let worker = std::thread::Builder::new()
                .name("medialink-audio-render".to_string())
                .spawn(move || {
                    let context = LoopContext {
                        callback,
                        user_data,
                        target_ms,
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
        /// **帧对齐由调用方保证**：参数是帧数而非样本数，半帧无从表达，
        /// 而 [`PlaybackRing::push`] 不跨调用结转残样本——若调用方按字节数算帧数
        /// 且没向下取整到整帧，错位会一路传下去。
        pub fn push(&self, interleaved: &[i16]) {
            // 渲染线程持锁的时间是一次 memcpy。拿不到锁只能是持锁者 panic 了，
            // 此时丢这一包而非把 panic 传进网络线程。
            if let Ok(mut ring) = self.ring.lock() {
                ring.push(interleaved);
            }
        }

        /// 停止并**同步等待渲染线程退出**。
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
        target_ms: u32,
    }

    /// 渲染主循环。COM 在本线程初始化并在退出前反初始化——COM 单元是线程局部的。
    ///
    /// 启动结果经 `ready` 恰好发一次；此后的失败无人接收，与采集侧同构：
    /// 托管侧据「声音停了」感知，native 侧没有可上报的地方。
    ///
    /// 会话的建立与循环收在 `render_session_loop` 里，**这是为了让所有 COM 对象在
    /// `CoUninitialize` 之前析构**。把会话开在本函数里会让 `IAudioClient` 的 Release
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

        let target_ms = context.target_ms;
        let mut prefill = PrefillState::new(target_frames(target_ms));
        let mut resampler = build_resampler(session.mix.sample_rate, session.buffer_frames);

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

            let Ok(available) = ring.lock().map(|ring| ring.available_frames()) else {
                break;
            };

            stats.set_ring_frames(available);

            if let Some(output_frames) = prefill_silence_frames(
                &mut prefill,
                available,
                writable as usize,
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

            // 本次要吃多少输入：重采样时由 rubato 说，比率与输出块都会改变它，
            // 故两者都要先设好再问。
            let needed = match resampler.as_mut() {
                Some(state) => {
                    let ratio =
                        resample_ratio(session.mix.sample_rate, frames_to_ms(available), target_ms as f64);
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

            let taken = {
                let Ok(mut ring) = ring.lock() else { break };
                ring.read_into(&mut staging[..needed * channels])
            };

            if taken < needed {
                stats.note_underrun();
                if prefill.note_underrun(frames_to_ms(needed - taken)) {
                    stats.note_hard_reset();
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

            let device_frames = match resampler.as_mut() {
                Some(state) => {
                    match resample_into(state, &staging[..needed * channels], &mut planar, &mut device_planar) {
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

            // 送出的是重采样**前**的 48k i16：C# 侧的分析器只认这一种格式。
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

        // **Start 必须在这里，不能挪到循环里。** 启动结果由调用方同步等待，
        // 而 `Start` 是「设备被独占」这类失败真正暴露的地方；放到上报之后失败，
        // `render_start` 会返回 OK 且 `last_error` 为空，表现为无声且无错误——
        // 那恰好否掉了 `WasapiRenderer::start` 文档声称的性质。
        client
            .Start()
            .map_err(|err| AudioError::device(format!("启动播放失败：{err}")))?;

        Ok(RenderSession {
            client,
            render_client,
            mix,
            buffer_frames,
            buffer_event,
            _buffer_event_guard: guard,
        })
    }

    struct ResamplerState {
        inner: SincFixedOut<f32>,
    }

    /// 设备混音率恰为 48000 时返回 `None`——直接格式转换后写入，零额外缓冲。
    /// 那是最常见的配置。
    fn build_resampler(device_rate: u32, buffer_frames: u32) -> Option<ResamplerState> {
        if device_rate == OUTPUT_SAMPLE_RATE || device_rate == 0 || buffer_frames == 0 {
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
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ring::MAX_DRIFT;

    #[test]
    fn target_frames_converts_ms_at_output_rate() {
        assert_eq!(target_frames(200), 9_600);
        assert_eq!(target_frames(50), 2_400);
    }

    #[test]
    fn target_buffer_ms_is_clamped_to_the_supported_range() {
        assert_eq!(clamp_target_ms(0), MIN_TARGET_MS);
        assert_eq!(clamp_target_ms(10), MIN_TARGET_MS);
        assert_eq!(clamp_target_ms(200), 200);
        assert_eq!(clamp_target_ms(9_999), MAX_TARGET_MS);
    }

    #[test]
    fn ring_capacity_is_twice_the_upper_bound() {
        // 容量等于目标深度会让每次抖动都触发溢出丢帧，
        // 而丢帧正是抖动缓冲要消除的东西。
        assert_eq!(RING_CAPACITY_FRAMES, target_frames(MAX_TARGET_MS) * 2);
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
        // 注意这个带是**不对称**的：漂移项取了倒数，故相对比率落在
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
            let frames = output_frames_for(buffer_frames, rate);
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
        assert!(output_frames_for(1_920, 96_000) < 1_920);
        assert!(output_frames_for(3_840, 192_000) < 3_840);
        assert_eq!(output_frames_for(960, 48_000), 960);
        // 低速设备反向：同时长的 48k 帧数更多，故折算是放大。
        assert!(output_frames_for(882, 44_100) > 882);
    }

    #[test]
    fn prefill_round_reports_silence_in_output_frames_within_the_buffer() {
        // 本条钉的是复审找出的 Critical。96kHz / 20ms 下设备帧数是 1920，
        // 而 staging 按 48k 输入帧分配只有约 1091 帧——照设备帧数索引就是越界 panic，
        // 且此时 render_start 已经返回过 OK，表现为静默无声。
        let mut state = PrefillState::new(target_frames(200));
        let max_input = 1_091;

        let frames = prefill_silence_frames(&mut state, 0, 1_920, 96_000, max_input)
            .expect("未达目标深度时应处于预填充");

        assert_eq!(frames, 960, "1920 个 96k 设备帧等于 960 个 48k 帧");
        assert!(frames <= max_input, "零值帧数不得超过 staging 容量");
    }

    #[test]
    fn prefill_silence_is_clamped_to_the_staging_bound() {
        // 换算结果仍可能超过上界（例如上界自身被算小），故必须夹。
        // 这是最后一道防线：越界发生在实时线程上，panic 会带走整个宿主进程。
        let mut state = PrefillState::new(target_frames(200));

        let frames = prefill_silence_frames(&mut state, 0, 4_800, 48_000, 100).expect("预填充");

        assert_eq!(frames, 100, "超过上界时必须夹到上界");
    }

    #[test]
    fn reaching_target_depth_ends_the_prefill_round() {
        let mut state = PrefillState::new(target_frames(200));

        assert!(prefill_silence_frames(&mut state, 9_600, 1_920, 96_000, 1_091).is_none());
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

        let s = cell.snapshot();
        assert_eq!(s.device_sample_rate, 48_000);
        assert_eq!(s.ring_frames, 9_600);
        assert_eq!(s.resample_ratio_ppm, 1_000_000);
        assert_eq!(s.device_frames_rendered, 960);
        assert_eq!(s.underrun_count, 2, "欠载是累加的");
        assert_eq!(s.hard_reset_count, 1);
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

        cell.reset();

        let s = cell.snapshot();
        assert_eq!(s.ring_frames, 0);
        assert_eq!(s.underrun_count, 0);
        assert_eq!(s.hard_reset_count, 0);
        assert_eq!(s.device_frames_rendered, 0);
        assert_eq!(s.device_sample_rate, 0);
        assert_eq!(s.resample_ratio_ppm, 0);
    }

    #[test]
    fn render_stats_layout_has_no_padding() {
        // 布局判据。托管侧有一条对称的 Marshal.SizeOf 断言，两条都成立才说明两端一致。
        // 错位是静默的：读到的是别的字段的值，表现为「数值不对」，
        // 与「逻辑算错了」无从区分。
        assert_eq!(std::mem::size_of::<crate::RenderStats>(), 48);
        assert_eq!(std::mem::align_of::<crate::RenderStats>(), 8);
    }
}
