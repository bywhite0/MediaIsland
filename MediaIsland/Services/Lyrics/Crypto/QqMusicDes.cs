namespace MediaIsland.Services.Lyrics.Crypto;

/// <summary>
/// Triple-DES core used by QQ Music QRC payloads.
/// </summary>
/// <remarks>
/// Ported from Lyricify.Lyrics.Helper (Apache-2.0, Lyricify.Lyrics.Decrypter.Qrc.DESHelper),
/// which mirrors the QQ Music reference C implementation. Two S-box entries deviate from
/// FIPS 46-3 DES: S2[23] is 15 instead of 14, and S4[53] is 10 instead of 1. QQ Music
/// encrypts with those exact tables, so they are preserved verbatim - "fixing" them
/// produces garbage output.
/// </remarks>
internal sealed class QqMusicTripleDes
{
    private const int Rounds = 16;
    private const int RoundKeyLength = 6;

    private static readonly byte[] SBox1 =
    [
        14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7,
        0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
        4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0,
        15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13
    ];

    private static readonly byte[] SBox2 =
    [
        15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10,
        // Index 23 is 15 here; standard DES uses 14.
        3, 13, 4, 7, 15, 2, 8, 15, 12, 0, 1, 10, 6, 9, 11, 5,
        0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15,
        13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9
    ];

    private static readonly byte[] SBox3 =
    [
        10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8,
        13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
        13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7,
        1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12
    ];

    private static readonly byte[] SBox4 =
    [
        7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15,
        13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
        10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4,
        // Index 53 is 10 here; standard DES uses 1.
        3, 15, 0, 6, 10, 10, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14
    ];

    private static readonly byte[] SBox5 =
    [
        2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9,
        14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
        4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14,
        11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3
    ];

    private static readonly byte[] SBox6 =
    [
        12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11,
        10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
        9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6,
        4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13
    ];

    private static readonly byte[] SBox7 =
    [
        4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1,
        13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
        1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2,
        6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12
    ];

    private static readonly byte[] SBox8 =
    [
        13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7,
        1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
        7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8,
        2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11
    ];

    /// <summary>Three DES key schedules, applied in order to each block.</summary>
    private readonly byte[][][] _schedules;

    private QqMusicTripleDes(byte[][][] schedules) => _schedules = schedules;

    public const int BlockSize = 8;

    public const int KeySize = 24;

    /// <summary>
    /// Builds the decrypting schedule for a 24-byte key: D(K3) then E(K2) then D(K1).
    /// </summary>
    public static QqMusicTripleDes CreateDecryptor(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Triple DES key must be {KeySize} bytes.", nameof(key));
        }

        var schedules = new byte[3][][];
        for (var i = 0; i < schedules.Length; i++)
        {
            schedules[i] = new byte[Rounds][];
            for (var round = 0; round < Rounds; round++)
            {
                schedules[i][round] = new byte[RoundKeyLength];
            }
        }

        BuildKeySchedule(key[..8], schedules[2], encrypt: false);
        BuildKeySchedule(key.Slice(8, 8), schedules[1], encrypt: true);
        BuildKeySchedule(key.Slice(16, 8), schedules[0], encrypt: false);
        return new QqMusicTripleDes(schedules);
    }

    /// <summary>Transforms one 8-byte block. <paramref name="output"/> may alias <paramref name="input"/>.</summary>
    public void TransformBlock(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Crypt(input, output, _schedules[0]);
        Crypt(output, output, _schedules[1]);
        Crypt(output, output, _schedules[2]);
    }

    private static void BuildKeySchedule(ReadOnlySpan<byte> key, byte[][] schedule, bool encrypt)
    {
        ReadOnlySpan<int> rotations = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];
        ReadOnlySpan<int> permutedChoiceLeft =
        [
            56, 48, 40, 32, 24, 16, 8, 0, 57, 49, 41, 33, 25, 17,
            9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35
        ];
        ReadOnlySpan<int> permutedChoiceRight =
        [
            62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21,
            13, 5, 60, 52, 44, 36, 28, 20, 12, 4, 27, 19, 11, 3
        ];
        ReadOnlySpan<int> compression =
        [
            13, 16, 10, 23, 0, 4, 2, 27, 14, 5, 20, 9, 22, 18, 11, 3,
            25, 7, 15, 6, 26, 19, 12, 1, 40, 51, 30, 36, 46, 54, 29, 39,
            50, 44, 32, 47, 43, 48, 38, 55, 33, 52, 45, 41, 49, 35, 28, 31
        ];

        uint left = 0;
        uint right = 0;
        for (int i = 0, shift = 31; i < permutedChoiceLeft.Length; i++, shift--)
        {
            left |= BitNum(key, permutedChoiceLeft[i], shift);
        }

        for (int i = 0, shift = 31; i < permutedChoiceRight.Length; i++, shift--)
        {
            right |= BitNum(key, permutedChoiceRight[i], shift);
        }

        for (var round = 0; round < Rounds; round++)
        {
            var rotation = rotations[round];
            left = ((left << rotation) | (left >> (28 - rotation))) & 0xFFFFFFF0u;
            right = ((right << rotation) | (right >> (28 - rotation))) & 0xFFFFFFF0u;

            var roundKey = schedule[encrypt ? round : Rounds - 1 - round];
            Array.Clear(roundKey);

            var bit = 0;
            for (; bit < 24; bit++)
            {
                roundKey[bit / 8] |= BitNumIntR(left, compression[bit], 7 - bit % 8);
            }

            for (; bit < compression.Length; bit++)
            {
                roundKey[bit / 8] |= BitNumIntR(right, compression[bit] - 27, 7 - bit % 8);
            }
        }
    }

    private static void Crypt(ReadOnlySpan<byte> input, Span<byte> output, byte[][] schedule)
    {
        // InitialPermutation fully consumes input before output is written, so aliasing is safe.
        Span<uint> state = stackalloc uint[2];
        InitialPermutation(state, input);
        for (var round = 0; round < Rounds - 1; round++)
        {
            var previous = state[1];
            state[1] = Feistel(state[1], schedule[round]) ^ state[0];
            state[0] = previous;
        }

        state[0] = Feistel(state[1], schedule[Rounds - 1]) ^ state[0];
        InversePermutation(state, output);
    }

    private static void InitialPermutation(Span<uint> state, ReadOnlySpan<byte> input)
    {
        state[0] = BitNum(input, 57, 31) | BitNum(input, 49, 30) | BitNum(input, 41, 29) | BitNum(input, 33, 28) |
                   BitNum(input, 25, 27) | BitNum(input, 17, 26) | BitNum(input, 9, 25) | BitNum(input, 1, 24) |
                   BitNum(input, 59, 23) | BitNum(input, 51, 22) | BitNum(input, 43, 21) | BitNum(input, 35, 20) |
                   BitNum(input, 27, 19) | BitNum(input, 19, 18) | BitNum(input, 11, 17) | BitNum(input, 3, 16) |
                   BitNum(input, 61, 15) | BitNum(input, 53, 14) | BitNum(input, 45, 13) | BitNum(input, 37, 12) |
                   BitNum(input, 29, 11) | BitNum(input, 21, 10) | BitNum(input, 13, 9) | BitNum(input, 5, 8) |
                   BitNum(input, 63, 7) | BitNum(input, 55, 6) | BitNum(input, 47, 5) | BitNum(input, 39, 4) |
                   BitNum(input, 31, 3) | BitNum(input, 23, 2) | BitNum(input, 15, 1) | BitNum(input, 7, 0);
        state[1] = BitNum(input, 56, 31) | BitNum(input, 48, 30) | BitNum(input, 40, 29) | BitNum(input, 32, 28) |
                   BitNum(input, 24, 27) | BitNum(input, 16, 26) | BitNum(input, 8, 25) | BitNum(input, 0, 24) |
                   BitNum(input, 58, 23) | BitNum(input, 50, 22) | BitNum(input, 42, 21) | BitNum(input, 34, 20) |
                   BitNum(input, 26, 19) | BitNum(input, 18, 18) | BitNum(input, 10, 17) | BitNum(input, 2, 16) |
                   BitNum(input, 60, 15) | BitNum(input, 52, 14) | BitNum(input, 44, 13) | BitNum(input, 36, 12) |
                   BitNum(input, 28, 11) | BitNum(input, 20, 10) | BitNum(input, 12, 9) | BitNum(input, 4, 8) |
                   BitNum(input, 62, 7) | BitNum(input, 54, 6) | BitNum(input, 46, 5) | BitNum(input, 38, 4) |
                   BitNum(input, 30, 3) | BitNum(input, 22, 2) | BitNum(input, 14, 1) | BitNum(input, 6, 0);
    }

    private static void InversePermutation(ReadOnlySpan<uint> state, Span<byte> output)
    {
        output[3] = (byte)(BitNumIntR(state[1], 7, 7) | BitNumIntR(state[0], 7, 6) | BitNumIntR(state[1], 15, 5) |
                           BitNumIntR(state[0], 15, 4) | BitNumIntR(state[1], 23, 3) | BitNumIntR(state[0], 23, 2) |
                           BitNumIntR(state[1], 31, 1) | BitNumIntR(state[0], 31, 0));
        output[2] = (byte)(BitNumIntR(state[1], 6, 7) | BitNumIntR(state[0], 6, 6) | BitNumIntR(state[1], 14, 5) |
                           BitNumIntR(state[0], 14, 4) | BitNumIntR(state[1], 22, 3) | BitNumIntR(state[0], 22, 2) |
                           BitNumIntR(state[1], 30, 1) | BitNumIntR(state[0], 30, 0));
        output[1] = (byte)(BitNumIntR(state[1], 5, 7) | BitNumIntR(state[0], 5, 6) | BitNumIntR(state[1], 13, 5) |
                           BitNumIntR(state[0], 13, 4) | BitNumIntR(state[1], 21, 3) | BitNumIntR(state[0], 21, 2) |
                           BitNumIntR(state[1], 29, 1) | BitNumIntR(state[0], 29, 0));
        output[0] = (byte)(BitNumIntR(state[1], 4, 7) | BitNumIntR(state[0], 4, 6) | BitNumIntR(state[1], 12, 5) |
                           BitNumIntR(state[0], 12, 4) | BitNumIntR(state[1], 20, 3) | BitNumIntR(state[0], 20, 2) |
                           BitNumIntR(state[1], 28, 1) | BitNumIntR(state[0], 28, 0));
        output[7] = (byte)(BitNumIntR(state[1], 3, 7) | BitNumIntR(state[0], 3, 6) | BitNumIntR(state[1], 11, 5) |
                           BitNumIntR(state[0], 11, 4) | BitNumIntR(state[1], 19, 3) | BitNumIntR(state[0], 19, 2) |
                           BitNumIntR(state[1], 27, 1) | BitNumIntR(state[0], 27, 0));
        output[6] = (byte)(BitNumIntR(state[1], 2, 7) | BitNumIntR(state[0], 2, 6) | BitNumIntR(state[1], 10, 5) |
                           BitNumIntR(state[0], 10, 4) | BitNumIntR(state[1], 18, 3) | BitNumIntR(state[0], 18, 2) |
                           BitNumIntR(state[1], 26, 1) | BitNumIntR(state[0], 26, 0));
        output[5] = (byte)(BitNumIntR(state[1], 1, 7) | BitNumIntR(state[0], 1, 6) | BitNumIntR(state[1], 9, 5) |
                           BitNumIntR(state[0], 9, 4) | BitNumIntR(state[1], 17, 3) | BitNumIntR(state[0], 17, 2) |
                           BitNumIntR(state[1], 25, 1) | BitNumIntR(state[0], 25, 0));
        output[4] = (byte)(BitNumIntR(state[1], 0, 7) | BitNumIntR(state[0], 0, 6) | BitNumIntR(state[1], 8, 5) |
                           BitNumIntR(state[0], 8, 4) | BitNumIntR(state[1], 16, 3) | BitNumIntR(state[0], 16, 2) |
                           BitNumIntR(state[1], 24, 1) | BitNumIntR(state[0], 24, 0));
    }

    private static uint Feistel(uint state, byte[] roundKey)
    {
        var high = BitNumIntL(state, 31, 0) | ((state & 0xF0000000u) >> 1) | BitNumIntL(state, 4, 5) |
                   BitNumIntL(state, 3, 6) | ((state & 0xF000000u) >> 3) | BitNumIntL(state, 8, 11) |
                   BitNumIntL(state, 7, 12) | ((state & 0xF00000u) >> 5) | BitNumIntL(state, 12, 17) |
                   BitNumIntL(state, 11, 18) | ((state & 0xF0000u) >> 7) | BitNumIntL(state, 16, 23);
        var low = BitNumIntL(state, 15, 0) | ((state & 0xF000u) << 15) | BitNumIntL(state, 20, 5) |
                  BitNumIntL(state, 19, 6) | ((state & 0xF00u) << 13) | BitNumIntL(state, 24, 11) |
                  BitNumIntL(state, 23, 12) | ((state & 0xF0u) << 11) | BitNumIntL(state, 28, 17) |
                  BitNumIntL(state, 27, 18) | ((state & 0xFu) << 9) | BitNumIntL(state, 0, 23);

        Span<byte> expanded = stackalloc byte[RoundKeyLength];
        expanded[0] = (byte)((byte)((high >> 24) & 0xFF) ^ roundKey[0]);
        expanded[1] = (byte)((byte)((high >> 16) & 0xFF) ^ roundKey[1]);
        expanded[2] = (byte)((byte)((high >> 8) & 0xFF) ^ roundKey[2]);
        expanded[3] = (byte)((byte)((low >> 24) & 0xFF) ^ roundKey[3]);
        expanded[4] = (byte)((byte)((low >> 16) & 0xFF) ^ roundKey[4]);
        expanded[5] = (byte)((byte)((low >> 8) & 0xFF) ^ roundKey[5]);

        state = (uint)((SBox1[SBoxBit((byte)(expanded[0] >> 2))] << 28) |
                       (SBox2[SBoxBit((byte)(((expanded[0] & 3) << 4) | (expanded[1] >> 4)))] << 24) |
                       (SBox3[SBoxBit((byte)(((expanded[1] & 0xF) << 2) | (expanded[2] >> 6)))] << 20) |
                       (SBox4[SBoxBit((byte)(expanded[2] & 0x3F))] << 16) |
                       (SBox5[SBoxBit((byte)(expanded[3] >> 2))] << 12) |
                       (SBox6[SBoxBit((byte)(((expanded[3] & 3) << 4) | (expanded[4] >> 4)))] << 8) |
                       (SBox7[SBoxBit((byte)(((expanded[4] & 0xF) << 2) | (expanded[5] >> 6)))] << 4) |
                       SBox8[SBoxBit((byte)(expanded[5] & 0x3F))]);

        return BitNumIntL(state, 15, 0) | BitNumIntL(state, 6, 1) | BitNumIntL(state, 19, 2) |
               BitNumIntL(state, 20, 3) | BitNumIntL(state, 28, 4) | BitNumIntL(state, 11, 5) |
               BitNumIntL(state, 27, 6) | BitNumIntL(state, 16, 7) | BitNumIntL(state, 0, 8) |
               BitNumIntL(state, 14, 9) | BitNumIntL(state, 22, 10) | BitNumIntL(state, 25, 11) |
               BitNumIntL(state, 4, 12) | BitNumIntL(state, 17, 13) | BitNumIntL(state, 30, 14) |
               BitNumIntL(state, 9, 15) | BitNumIntL(state, 1, 16) | BitNumIntL(state, 7, 17) |
               BitNumIntL(state, 23, 18) | BitNumIntL(state, 13, 19) | BitNumIntL(state, 31, 20) |
               BitNumIntL(state, 26, 21) | BitNumIntL(state, 2, 22) | BitNumIntL(state, 8, 23) |
               BitNumIntL(state, 18, 24) | BitNumIntL(state, 12, 25) | BitNumIntL(state, 29, 26) |
               BitNumIntL(state, 5, 27) | BitNumIntL(state, 21, 28) | BitNumIntL(state, 10, 29) |
               BitNumIntL(state, 3, 30) | BitNumIntL(state, 24, 31);
    }

    private static uint BitNum(ReadOnlySpan<byte> source, int bit, int shift) =>
        (uint)(((source[bit / 32 * 4 + 3 - bit % 32 / 8] >> (7 - bit % 8)) & 1) << shift);

    private static byte BitNumIntR(uint value, int bit, int shift) =>
        (byte)(((value >> (31 - bit)) & 1) << shift);

    private static uint BitNumIntL(uint value, int bit, int shift) =>
        ((value << bit) & 0x80000000u) >> shift;

    private static uint SBoxBit(byte value) =>
        (uint)((value & 0x20) | ((value & 0x1F) >> 1) | ((value & 1) << 4));
}
