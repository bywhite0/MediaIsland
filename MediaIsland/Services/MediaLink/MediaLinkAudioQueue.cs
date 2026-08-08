namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 每会话音频出站有界队列。与 <see cref="MediaLinkOutboundQueue"/> 的策略刻意相反：
/// 满时无条件丢最旧，且入队永不失败（不返回 bool，调用方无需处理背压失败）。
///
/// 丢最旧对音频是正确策略，这与"连续采样流不应丢帧"的直觉相反，理由是消费场景不同：
/// 可视化只关心"现在在响什么"，积压的历史帧已经过期，保留最新帧才对；
/// 播放侧的连续性由接收端的环形缓冲负责，不由发送队列负责。
///
/// 因此音频队列满**不触发** rate_limited 关闭会话——那是 JSON 队列面对
/// 不可丢帧时才需要的处置。
/// </summary>
internal sealed class MediaLinkAudioQueue
{
    private readonly int _capacity;
    private readonly Queue<byte[]> _items;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private bool _completed;
    private long _dropped;

    public MediaLinkAudioQueue(int capacity)
    {
        _capacity = capacity;
        _items = new Queue<byte[]>(capacity);
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>累计丢弃帧数。用于诊断背压，不参与协议行为。</summary>
    public long DroppedCount
    {
        get { lock (_gate) return _dropped; }
    }

    public void Enqueue(byte[] frame)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            while (_items.Count >= _capacity)
            {
                _items.Dequeue();
                _dropped++;
            }

            _items.Enqueue(frame);
        }

        // 丢弃+新增时净数量不变，此处会多释放一次信号量。
        // 多余的唤醒是良性的：出队方拿不到帧就继续等待；少释放才会导致帧滞留。
        _signal.Release();
    }

    public async Task<byte[]?> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_items.Count > 0)
                {
                    return _items.Dequeue();
                }

                if (_completed)
                {
                    return null;
                }
            }

            await _signal.WaitAsync(cancellationToken);
        }
    }

    public bool TryDequeue(out byte[] frame)
    {
        lock (_gate)
        {
            if (_items.Count > 0)
            {
                frame = _items.Dequeue();
                return true;
            }
        }

        frame = [];
        return false;
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
        }

        _signal.Release();
    }
}
