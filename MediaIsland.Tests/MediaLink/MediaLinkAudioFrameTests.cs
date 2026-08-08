using System.Text;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 音频帧二进制编解码。头部定长 34 字节，之后是变长 trackToken 与裸 i16 PCM。
/// 这里锁定的是线格式本身——它一旦发布就是跨版本兼容契约，不能随实现漂移。
/// </summary>
public class MediaLinkAudioFrameTests
{
    private static MediaLinkAudioFrameHeader Header(
        string trackToken = "abc123",
        MediaLinkAudioFrameFlags flags = MediaLinkAudioFrameFlags.None) =>
        new(
            StartPositionMs: 30000,
            CapturedAtMs: 1710000030000,
            ServerTimeMs: 1710000030050,
            Seq: 42,
            TrackToken: trackToken,
            Flags: flags);

    [Fact]
    public void Roundtrip_PreservesHeaderAndPcm()
    {
        var pcm = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var encoded = MediaLinkAudioFrame.Encode(Header(), pcm);

        Assert.True(MediaLinkAudioFrame.TryDecode(encoded, out var header, out var decodedPcm, out var error));
        Assert.Equal(MediaLinkAudioFrameDecodeError.None, error);
        Assert.Equal(30000, header.StartPositionMs);
        Assert.Equal(1710000030000, header.CapturedAtMs);
        Assert.Equal(1710000030050, header.ServerTimeMs);
        Assert.Equal(42u, header.Seq);
        Assert.Equal("abc123", header.TrackToken);
        Assert.Equal(MediaLinkAudioFrameFlags.None, header.Flags);
        Assert.Equal(pcm, decodedPcm.ToArray());
    }

    [Fact]
    public void Roundtrip_MultiByteTrackToken()
    {
        // trackToken 按 UTF-8 编码，长度字段是字节数而非字符数。
        var encoded = MediaLinkAudioFrame.Encode(Header("曲目标识-日本語"), []);

        Assert.True(MediaLinkAudioFrame.TryDecode(encoded, out var header, out _, out _));
        Assert.Equal("曲目标识-日本語", header.TrackToken);
    }

    [Fact]
    public void Roundtrip_EmptyPcm()
    {
        var encoded = MediaLinkAudioFrame.Encode(Header(), []);

        Assert.True(MediaLinkAudioFrame.TryDecode(encoded, out _, out var pcm, out _));
        Assert.Equal(0, pcm.Length);
    }

    [Fact]
    public void Roundtrip_SingleSample()
    {
        var pcm = new byte[] { 0xFF, 0x7F };
        var encoded = MediaLinkAudioFrame.Encode(Header(), pcm);

        Assert.True(MediaLinkAudioFrame.TryDecode(encoded, out _, out var decoded, out _));
        Assert.Equal(pcm, decoded.ToArray());
    }

    [Fact]
    public void Roundtrip_EmptyTrackToken()
    {
        var encoded = MediaLinkAudioFrame.Encode(Header(""), [0x01, 0x02]);

        Assert.True(MediaLinkAudioFrame.TryDecode(encoded, out var header, out var pcm, out _));
        Assert.Equal("", header.TrackToken);
        Assert.Equal(new byte[] { 0x01, 0x02 }, pcm.ToArray());
    }

    [Fact]
    public void Roundtrip_Flags()
    {
        var flags = MediaLinkAudioFrameFlags.Silent | MediaLinkAudioFrameFlags.TrackStart;
        var encoded = MediaLinkAudioFrame.Encode(Header(flags: flags), []);

        Assert.True(MediaLinkAudioFrame.TryDecode(encoded, out var header, out _, out _));
        Assert.Equal(flags, header.Flags);
    }

    [Fact]
    public void Encode_TrackTokenExceedingUInt16_Throws()
    {
        // 长度字段是 u16，超限必须在编码期炸而不是静默截断成错误的帧。
        var oversized = new string('a', ushort.MaxValue + 1);

        Assert.Throws<ArgumentException>(() =>
            MediaLinkAudioFrame.Encode(Header(oversized), []));
    }

    [Fact]
    public void Decode_BadMagic_ReportsBadMagic()
    {
        var encoded = MediaLinkAudioFrame.Encode(Header(), []);
        encoded[0] = 0xFF;

        Assert.False(MediaLinkAudioFrame.TryDecode(encoded, out _, out _, out var error));
        Assert.Equal(MediaLinkAudioFrameDecodeError.BadMagic, error);
    }

    [Fact]
    public void Decode_FutureVersion_ReportsUnsupportedVersion()
    {
        // 未来版本的帧要能被识别为"不认识"而非"损坏"，两者的处置不同。
        var encoded = MediaLinkAudioFrame.Encode(Header(), []);
        encoded[2] = 99;

        Assert.False(MediaLinkAudioFrame.TryDecode(encoded, out _, out _, out var error));
        Assert.Equal(MediaLinkAudioFrameDecodeError.UnsupportedVersion, error);
    }

    [Fact]
    public void Decode_TruncatedHeader_ReportsTooShort()
    {
        var encoded = MediaLinkAudioFrame.Encode(Header(), []);

        Assert.False(MediaLinkAudioFrame.TryDecode(
            encoded.AsSpan(0, MediaLinkAudioFrame.FixedHeaderBytes - 1), out _, out _, out var error));
        Assert.Equal(MediaLinkAudioFrameDecodeError.TooShort, error);
    }

    [Fact]
    public void Decode_TrackTokenLengthExceedsBuffer_ReportsTooShort()
    {
        // 声明的 trackToken 长度超出实际字节：必须拒绝，不能越界读。
        var encoded = MediaLinkAudioFrame.Encode(Header("abc"), []);
        encoded[32] = 0xFF;
        encoded[33] = 0xFF;

        Assert.False(MediaLinkAudioFrame.TryDecode(encoded, out _, out _, out var error));
        Assert.Equal(MediaLinkAudioFrameDecodeError.TooShort, error);
    }

    [Fact]
    public void Decode_EmptyBuffer_ReportsTooShort()
    {
        Assert.False(MediaLinkAudioFrame.TryDecode([], out _, out _, out var error));
        Assert.Equal(MediaLinkAudioFrameDecodeError.TooShort, error);
    }

    [Fact]
    public void Encode_WritesExpectedWireLayout()
    {
        // 线格式是跨版本契约，逐字节锁定，防止字段顺序或字节序被无意改动。
        var header = new MediaLinkAudioFrameHeader(
            StartPositionMs: 1, CapturedAtMs: 2, ServerTimeMs: 3,
            Seq: 4, TrackToken: "ab", Flags: MediaLinkAudioFrameFlags.Silent);
        var encoded = MediaLinkAudioFrame.Encode(header, [0xAA, 0xBB]);

        Assert.Equal(0xA1, encoded[0]);
        Assert.Equal(0x01, encoded[1]);
        Assert.Equal(1, encoded[2]);
        Assert.Equal(0x01, encoded[3]);
        Assert.Equal(1, BitConverter.ToInt64(encoded, 4));
        Assert.Equal(2, BitConverter.ToInt64(encoded, 12));
        Assert.Equal(3, BitConverter.ToInt64(encoded, 20));
        Assert.Equal(4u, BitConverter.ToUInt32(encoded, 28));
        Assert.Equal(2, BitConverter.ToUInt16(encoded, 32));
        Assert.Equal("ab", Encoding.UTF8.GetString(encoded, 34, 2));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, encoded[36..]);
        Assert.Equal(38, encoded.Length);
    }
}
