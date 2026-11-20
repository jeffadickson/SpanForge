using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SpanForge.Protocols.Png;

public enum PngColorType : byte
{
    Grayscale = 0,
    Truecolor = 2,
    Indexed = 3,
    GrayscaleAlpha = 4,
    TruecolorAlpha = 6,
}

public readonly record struct IhdrChunk(
    uint Width,
    uint Height,
    byte BitDepth,
    PngColorType ColorType,
    byte Compression,
    byte Filter,
    byte Interlace);

public readonly record struct PlteChunk(ByteSlice Entries)
{
    public int EntryCount => Entries.Length / 3;
}

public readonly record struct IdatChunk(ByteSlice Data);
public readonly record struct TextChunk(ByteSlice Keyword, ByteSlice Text);
public readonly record struct IendChunk;
public readonly record struct UnknownChunk(uint TypeCode, ByteSlice Data)
{
    public string TypeName => string.Create(4, TypeCode, static (span, code) =>
    {
        span[0] = (char)((code >> 24) & 0xFF);
        span[1] = (char)((code >> 16) & 0xFF);
        span[2] = (char)((code >> 8) & 0xFF);
        span[3] = (char)(code & 0xFF);
    });
}

/// <summary>
/// Zero-boxing C# 15 custom <c>[Union]</c> representing every decoded PNG chunk variant with exhaustive
/// pattern matching and 0 bytes of heap allocation per chunk.
/// </summary>
[Union]
public readonly struct PngChunk : IUnion
{
    private readonly uint _a;
    private readonly uint _b;
    private readonly uint _c;
    private readonly uint _d;
    private readonly byte _tag; // 1=Ihdr, 2=Plte, 3=Idat, 4=Text, 5=Iend, 6=Unknown

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PngChunk(IhdrChunk h)
    {
        _a = h.Width;
        _b = h.Height;
        _c = h.BitDepth
            | ((uint)h.ColorType << 8)
            | ((uint)h.Compression << 16)
            | ((uint)h.Filter << 24);
        _d = h.Interlace;
        _tag = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PngChunk(PlteChunk p) => (_a, _b, _c, _d, _tag) = ((uint)p.Entries.Offset, (uint)p.Entries.Length, 0u, 0u, 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PngChunk(IdatChunk d) => (_a, _b, _c, _d, _tag) = ((uint)d.Data.Offset, (uint)d.Data.Length, 0u, 0u, 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PngChunk(TextChunk t) => (_a, _b, _c, _d, _tag) = ((uint)t.Keyword.Offset, (uint)t.Keyword.Length, (uint)t.Text.Offset, (uint)t.Text.Length, 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PngChunk(IendChunk _) => (_a, _b, _c, _d, _tag) = (0u, 0u, 0u, 0u, 5);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PngChunk(UnknownChunk u) => (_a, _b, _c, _d, _tag) = (u.TypeCode, (uint)u.Data.Offset, (uint)u.Data.Length, 0u, 6);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IhdrChunk UnpackIhdr() => new(
        _a,
        _b,
        (byte)_c,
        (PngColorType)(byte)(_c >> 8),
        (byte)(_c >> 16),
        (byte)(_c >> 24),
        (byte)_d);

    public object? Value => _tag switch
    {
        1 => UnpackIhdr(),
        2 => new PlteChunk(new ByteSlice((int)_a, (int)_b)),
        3 => new IdatChunk(new ByteSlice((int)_a, (int)_b)),
        4 => new TextChunk(new ByteSlice((int)_a, (int)_b), new ByteSlice((int)_c, (int)_d)),
        5 => default(IendChunk),
        6 => new UnknownChunk(_a, new ByteSlice((int)_b, (int)_c)),
        _ => null,
    };

    public bool HasValue => _tag != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out IhdrChunk value)
    {
        if (_tag == 1) { value = UnpackIhdr(); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out PlteChunk value)
    {
        if (_tag == 2) { value = new PlteChunk(new ByteSlice((int)_a, (int)_b)); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out IdatChunk value)
    {
        if (_tag == 3) { value = new IdatChunk(new ByteSlice((int)_a, (int)_b)); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out TextChunk value)
    {
        if (_tag == 4) { value = new TextChunk(new ByteSlice((int)_a, (int)_b), new ByteSlice((int)_c, (int)_d)); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out IendChunk value)
    {
        value = default;
        return _tag == 5;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out UnknownChunk value)
    {
        if (_tag == 6) { value = new UnknownChunk(_a, new ByteSlice((int)_b, (int)_c)); return true; }
        value = default; return false;
    }
}

public sealed record class PngDocument(IhdrChunk Header, IReadOnlyList<PngChunk> Chunks);

/// <summary>
/// Zero-copy PNG binary parser with slicing-by-8 CRC-32 validation, streaming chunk support,
/// and exhaustive zero-boxing C# 15 <c>[Union] PngChunk</c> results.
/// </summary>
public static class PngParser
{
    public static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private const uint TypeIhdr = 0x49484452; // "IHDR"
    private const uint TypePlte = 0x504C5445; // "PLTE"
    private const uint TypeIdat = 0x49444154; // "IDAT"
    private const uint TypeText = 0x74455874; // "tEXt"
    private const uint TypeIend = 0x49454E44; // "IEND"

    private static readonly uint[] CrcTableFlat = BuildCrcTableFlat();

    public static ParseResult<ByteSlice> ParseSignature(ref ByteCursor cursor) =>
        cursor.Tag(Signature).WithContext("PNG 8-byte magic signature");

    /// <summary>
    /// Parses a single PNG chunk (works in both <see cref="InputMode.Complete"/> and <see cref="InputMode.Streaming"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<PngChunk> ParseChunk(ref ByteCursor cursor)
    {
        int start = cursor.Position;
        ReadOnlySpan<byte> buf = cursor.Buffer;
        int rem = buf.Length - start;
        if (rem < 12)
        {
            return ParseChunkShortage(ref cursor, start, rem);
        }

        ref byte ptr = ref Unsafe.Add(ref MemoryMarshal.GetReference(buf), (nint)(uint)start);
        uint rawLen = Unsafe.ReadUnaligned<uint>(ref ptr);
        uint typeCode = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref ptr, 4));
        if (BitConverter.IsLittleEndian)
        {
            rawLen = BinaryPrimitives.ReverseEndianness(rawLen);
            typeCode = BinaryPrimitives.ReverseEndianness(typeCode);
        }

        if (rawLen > int.MaxValue - 12)
        {
            return Failure.At(start, ErrorKind.LengthValue, "PNG chunk length exceeds safe limit");
        }

        int dataLength = (int)rawLen;
        int totalLength = 12 + dataLength;
        if (rem < totalLength)
        {
            return cursor.Mode == InputMode.Streaming
                ? Incomplete.AtLeast(totalLength - rem)
                : Backtrack.At(start + 8, ErrorKind.Take);
        }

        int crcOffset = start + 8 + dataLength;
        uint expectedCrc = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref ptr, (nint)(uint)(8 + dataLength)));
        if (BitConverter.IsLittleEndian)
        {
            expectedCrc = BinaryPrimitives.ReverseEndianness(expectedCrc);
        }

        // Verify IEEE 802.3 CRC-32 over [ChunkType (4 bytes) || ChunkData].
        uint computedCrc = ComputeCrc32(buf.Slice(start + 4, 4 + dataLength));
        if (computedCrc != expectedCrc)
        {
            return CrcMismatchFailure(crcOffset, expectedCrc, computedCrc);
        }

        var dataSlice = new ByteSlice(start + 8, dataLength);
        var decoded = DecodeChunkPayload(typeCode, dataSlice, buf.Slice(start + 8, dataLength));
        if (decoded.IsParsed)
        {
            cursor.Position = start + totalLength;
        }

        return decoded;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<PngChunk> ParseChunkShortage(ref ByteCursor cursor, int start, int rem)
    {
        if (rem >= 4)
        {
            uint rawLen = BinaryPrimitives.ReadUInt32BigEndian(cursor.Buffer.Slice(start, 4));
            if (rawLen > int.MaxValue - 12)
            {
                return Failure.At(start, ErrorKind.LengthValue, "PNG chunk length exceeds safe limit");
            }
        }

        if (cursor.Mode == InputMode.Streaming)
        {
            if (rem < 4) return Incomplete.AtLeast(4 - rem);
            if (rem < 8) return Incomplete.AtLeast(8 - rem);
            return Incomplete.AtLeast(12 - rem);
        }

        return Backtrack.At(start, ErrorKind.Eof);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<PngChunk> CrcMismatchFailure(int crcOffset, uint expectedCrc, uint computedCrc) =>
        Failure.At(
            crcOffset,
            ErrorKind.CrcMismatch,
            $"PNG chunk CRC mismatch: expected 0x{expectedCrc:X8}, computed 0x{computedCrc:X8}");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ParseResult<PngChunk> DecodeChunkPayload(
        uint typeCode,
        ByteSlice dataSlice,
        ReadOnlySpan<byte> payload)
    {
        switch (typeCode)
        {
            case TypeIhdr:
                if (payload.Length != 13)
                {
                    return Failure.At(dataSlice.Offset, ErrorKind.Protocol, $"IHDR chunk must be 13 bytes, got {payload.Length}");
                }

                uint width = BinaryPrimitives.ReadUInt32BigEndian(payload);
                uint height = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4));
                byte bitDepth = payload[8];
                byte colorTypeRaw = payload[9];
                byte compression = payload[10];
                byte filter = payload[11];
                byte interlace = payload[12];

                if (width == 0 || height == 0)
                {
                    return Failure.At(dataSlice.Offset, ErrorKind.Verify, "PNG dimensions must be non-zero");
                }

                if (colorTypeRaw is not (0 or 2 or 3 or 4 or 6))
                {
                    return Failure.At(dataSlice.Offset + 9, ErrorKind.Verify, $"Unknown PNG color type {colorTypeRaw}");
                }

                return new Parsed<PngChunk>(new IhdrChunk(width, height, bitDepth, (PngColorType)colorTypeRaw, compression, filter, interlace));

            case TypePlte:
                if (payload.Length == 0 || payload.Length % 3 != 0)
                {
                    return Failure.At(dataSlice.Offset, ErrorKind.Protocol, "PLTE chunk length must be a positive multiple of 3");
                }

                return new Parsed<PngChunk>(new PlteChunk(dataSlice));

            case TypeIdat:
                return new Parsed<PngChunk>(new IdatChunk(dataSlice));

            case TypeText:
                int nul = payload.IndexOf((byte)0);
                if (nul < 0)
                {
                    return Failure.At(dataSlice.Offset, ErrorKind.Protocol, "tEXt chunk missing null separator after keyword");
                }

                var kw = new ByteSlice(dataSlice.Offset, nul);
                var txt = new ByteSlice(dataSlice.Offset + nul + 1, payload.Length - nul - 1);
                return new Parsed<PngChunk>(new TextChunk(kw, txt));

            case TypeIend:
                if (payload.Length != 0)
                {
                    return Failure.At(dataSlice.Offset, ErrorKind.Protocol, "IEND chunk must have empty payload");
                }

                return new Parsed<PngChunk>(default(IendChunk));

            default:
                return new Parsed<PngChunk>(new UnknownChunk(typeCode, dataSlice));
        }
    }

    /// <summary>
    /// Parses a complete PNG file (signature + IHDR + chunks through IEND).
    /// </summary>
    public static ParseResult<PngDocument> ParseDocument(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        var sigRes = ParseSignature(ref cursor);
        if (!sigRes.IsParsed)
        {
            cursor.Reset(cp);
            return sigRes.PropagateNonParsed<PngDocument>();
        }

        // First chunk MUST be IHDR (committed after valid PNG signature).
        var firstRes = ParseChunk(ref cursor).Cut("PNG first chunk");
        if (!firstRes.TryGetValue(out Parsed<PngChunk> firstOk))
        {
            cursor.Reset(cp);
            return firstRes.PropagateNonParsed<PngDocument>();
        }

        if (firstOk.Value is not IhdrChunk header)
        {
            cursor.Reset(cp);
            return Failure.At(cp.Position + 8, ErrorKind.Protocol, "First PNG chunk must be IHDR");
        }

        List<PngChunk> chunks = [with(capacity: 8), firstOk.Value];

        chunkLoop:
        while (true)
        {
            var nextRes = ParseChunk(ref cursor).Cut("PNG chunk stream");
            if (!nextRes.TryGetValue(out Parsed<PngChunk> nextOk))
            {
                cursor.Reset(cp);
                return nextRes.PropagateNonParsed<PngDocument>();
            }

            chunks.Add(nextOk.Value);
            if (nextOk.Value is IendChunk)
            {
                break chunkLoop;
            }
        }

        return new Parsed<PngDocument>(new PngDocument(header, chunks));
    }

    /// <summary>Computes IEEE 802.3 CRC-32 using flat slicing-by-8+4 lookup tables (zero allocations, zero inner bounds checks).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        ref uint t = ref MemoryMarshal.GetArrayDataReference(CrcTableFlat);
        ref byte src = ref MemoryMarshal.GetReference(data);
        int len = data.Length;

        uint crc = 0xFFFF_FFFFu;
        nuint i = 0;
        nuint uLen = (nuint)(uint)len;

        while (i + 8 <= uLen)
        {
            uint lo = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, i));
            uint hi = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, i + 4));
            if (!BitConverter.IsLittleEndian)
            {
                lo = BinaryPrimitives.ReverseEndianness(lo);
                hi = BinaryPrimitives.ReverseEndianness(hi);
            }

            lo ^= crc;
            crc = Unsafe.Add(ref t, (nint)(7 * 256 + (lo & 0xFF)))
                ^ Unsafe.Add(ref t, (nint)(6 * 256 + ((lo >> 8) & 0xFF)))
                ^ Unsafe.Add(ref t, (nint)(5 * 256 + ((lo >> 16) & 0xFF)))
                ^ Unsafe.Add(ref t, (nint)(4 * 256 + (lo >> 24)))
                ^ Unsafe.Add(ref t, (nint)(3 * 256 + (hi & 0xFF)))
                ^ Unsafe.Add(ref t, (nint)(2 * 256 + ((hi >> 8) & 0xFF)))
                ^ Unsafe.Add(ref t, (nint)(1 * 256 + ((hi >> 16) & 0xFF)))
                ^ Unsafe.Add(ref t, (nint)(hi >> 24));
            i += 8;
        }

        if (i + 4 <= uLen)
        {
            uint w = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, i));
            if (!BitConverter.IsLittleEndian)
            {
                w = BinaryPrimitives.ReverseEndianness(w);
            }

            w ^= crc;
            crc = Unsafe.Add(ref t, (nint)(3 * 256 + (w & 0xFF)))
                ^ Unsafe.Add(ref t, (nint)(2 * 256 + ((w >> 8) & 0xFF)))
                ^ Unsafe.Add(ref t, (nint)(1 * 256 + ((w >> 16) & 0xFF)))
                ^ Unsafe.Add(ref t, (nint)(w >> 24));
            i += 4;
        }

        while (i < uLen)
        {
            crc = Unsafe.Add(ref t, (nint)((crc ^ Unsafe.Add(ref src, i)) & 0xFF)) ^ (crc >> 8);
            i++;
        }

        return ~crc;
    }

    /// <summary>Helper for building valid PNG chunks in tests and demos.</summary>
    public static byte[] BuildChunk(ReadOnlySpan<byte> fourCc, ReadOnlySpan<byte> payload)
    {
        if (fourCc.Length != 4) throw new ArgumentException("PNG chunk type must be 4 bytes.", nameof(fourCc));

        var result = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)payload.Length);
        fourCc.CopyTo(result.AsSpan(4, 4));
        payload.CopyTo(result.AsSpan(8, payload.Length));
        uint crc = ComputeCrc32(result.AsSpan(4, 4 + payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8 + payload.Length, 4), crc);
        return result;
    }

    private static uint[] BuildCrcTableFlat()
    {
        var table = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB8_8320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        for (int s = 1; s < 8; s++)
        {
            int prevBase = (s - 1) * 256;
            int currBase = s * 256;
            for (int i = 0; i < 256; i++)
            {
                uint c = table[prevBase + i];
                table[currBase + i] = (c >> 8) ^ table[c & 0xFF];
            }
        }

        return table;
    }
}
