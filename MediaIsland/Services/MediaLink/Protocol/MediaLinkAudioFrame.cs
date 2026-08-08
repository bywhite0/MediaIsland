using System.Buffers.Binary;
using System.Text;

namespace MediaIsland.Services.MediaLink.Protocol;

[Flags]
public enum MediaLinkAudioFrameFlags : byte
{
    None = 0,

    /// <summary>本帧为静音。接收端可跳过 FFT 计算，但仍须推进时间轴。</summary>
    Silent = 1,

    /// <summary>本帧是该曲目的首帧，接收端可据此重置缓冲。</summary>
    TrackStart = 2
}

public enum MediaLinkAudioFrameDecodeError
{
    None,
    TooShort,
    BadMagic,
    UnsupportedVersion
}

/// <summary>
/// 音频帧头。三个时间戳语义不同，缺一不可：
/// <see cref="StartPositionMs"/> 是曲目轴上的位置，用于对齐歌词；
/// <see cref="CapturedAtMs"/> 与 <see cref="ServerTimeMs"/> 是同一时钟内求差的一对值，
/// 沿用 media.updated 的免对时机制估算传输延迟（见 docs/medialink-protocol.md 进度插值一节）。
/// </summary>
public readonly record struct MediaLinkAudioFrameHeader(
    long StartPositionMs,
    long CapturedAtMs,
    long ServerTimeMs,
    uint Seq,
    string TrackToken,
    MediaLinkAudioFrameFlags Flags);

/// <summary>
/// 音频帧的二进制线格式。音频不走 JSON 信封：一帧 20ms 的 PCM 约 3840 字节，
/// base64 进 JSON 会膨胀三分之一，且每帧都要过一遍序列化器。
///
/// 布局（小端）：
/// <code>
/// 0   2  magic = 0xA1 0x01
/// 2   1  version = 1
/// 3   1  flags
/// 4   8  startPositionMs   i64
/// 12  8  capturedAtMs      i64
/// 20  8  serverTimeMs      i64
/// 28  4  seq               u32
/// 32  2  trackTokenLen     u16
/// 34  N  trackToken        UTF-8
/// 34+N   PCM               i16 交错
/// </code>
/// trackToken 变长置于末尾，使头部定长部分可按固定偏移读取。
/// </summary>
public static class MediaLinkAudioFrame
{
    public const int FixedHeaderBytes = 34;
    public const byte Magic0 = 0xA1;
    public const byte Magic1 = 0x01;
    public const byte CurrentVersion = 1;

    public static byte[] Encode(in MediaLinkAudioFrameHeader header, ReadOnlySpan<byte> pcm)
    {
        var tokenBytes = Encoding.UTF8.GetByteCount(header.TrackToken);
        if (tokenBytes > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"trackToken 的 UTF-8 长度 {tokenBytes} 超出 u16 上限", nameof(header));
        }

        var buffer = new byte[FixedHeaderBytes + tokenBytes + pcm.Length];
        var span = buffer.AsSpan();

        span[0] = Magic0;
        span[1] = Magic1;
        span[2] = CurrentVersion;
        span[3] = (byte)header.Flags;
        BinaryPrimitives.WriteInt64LittleEndian(span[4..], header.StartPositionMs);
        BinaryPrimitives.WriteInt64LittleEndian(span[12..], header.CapturedAtMs);
        BinaryPrimitives.WriteInt64LittleEndian(span[20..], header.ServerTimeMs);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], header.Seq);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)tokenBytes);
        Encoding.UTF8.GetBytes(header.TrackToken, span[FixedHeaderBytes..]);
        pcm.CopyTo(span[(FixedHeaderBytes + tokenBytes)..]);

        return buffer;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> frame,
        out MediaLinkAudioFrameHeader header,
        out ReadOnlySpan<byte> pcm,
        out MediaLinkAudioFrameDecodeError error)
    {
        header = default;
        pcm = default;

        if (frame.Length < FixedHeaderBytes)
        {
            error = MediaLinkAudioFrameDecodeError.TooShort;
            return false;
        }

        if (frame[0] != Magic0 || frame[1] != Magic1)
        {
            error = MediaLinkAudioFrameDecodeError.BadMagic;
            return false;
        }

        if (frame[2] != CurrentVersion)
        {
            error = MediaLinkAudioFrameDecodeError.UnsupportedVersion;
            return false;
        }

        var tokenLength = BinaryPrimitives.ReadUInt16LittleEndian(frame[32..]);
        if (frame.Length < FixedHeaderBytes + tokenLength)
        {
            error = MediaLinkAudioFrameDecodeError.TooShort;
            return false;
        }

        header = new MediaLinkAudioFrameHeader(
            BinaryPrimitives.ReadInt64LittleEndian(frame[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(frame[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(frame[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(frame[28..]),
            Encoding.UTF8.GetString(frame.Slice(FixedHeaderBytes, tokenLength)),
            (MediaLinkAudioFrameFlags)frame[3]);
        pcm = frame[(FixedHeaderBytes + tokenLength)..];
        error = MediaLinkAudioFrameDecodeError.None;
        return true;
    }
}
