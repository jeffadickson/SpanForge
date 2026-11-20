using System.Buffers.Binary;
using System.Runtime.CompilerServices;

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

    private static readonly uint[][] CrcTables = BuildCrcTables();

    public static ParseResult<ByteSlice> ParseSignature(ref ByteCursor cursor) =>
        cursor.Tag(Signature).WithContext("PNG 8-byte magic signature");

    /// <summary>
    /// Parses a single PNG chunk (works in both <see cref="InputMode.Complete"/> and <see cref="InputMode.Streaming"/>).
    /// </summary>
    public static ParseResult<PngChunk> ParseChunk(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        cursor.Endianness = Endianness.Big;

        var lenRes = cursor.ReadU32BE();
        if (!lenRes.TryGetValue(out Parsed<uint> lenOk))
        {
            cursor.Reset(cp);
            return lenRes.PropagateNonParsed<PngChunk>();
        }

        if (lenOk.Value > int.MaxValue - 12)
        {
            cursor.Reset(cp);
            return Failure.At(cp.Position, ErrorKind.LengthValue, "PNG chunk length exceeds safe limit");
        }

        int dataLength = (int)lenOk.Value;
        int typeStart = cursor.Position;

        var typeRes = cursor.ReadU32BE();
        if (!typeRes.TryGetValue(out Parsed<uint> typeOk))
        {
            cursor.Reset(cp);
            return typeRes.PropagateNonParsed<PngChunk>();
        }

        var dataRes = cursor.Take(dataLength);
        if (!dataRes.TryGetValue(out Parsed<ByteSlice> dataOk))
        {
            cursor.Reset(cp);
            return dataRes.PropagateNonParsed<PngChunk>();
        }

        int crcOffset = cursor.Position;
        var crcRes = cursor.ReadU32BE();
        if (!crcRes.TryGetValue(out Parsed<uint> crcOk))
        {
            cursor.Reset(cp);
            return crcRes.PropagateNonParsed<PngChunk>();
        }

        // Verify IEEE 802.3 CRC-32 over [ChunkType (4 bytes) || ChunkData].
        ReadOnlySpan<byte> crcSpan = cursor.Buffer.Slice(typeStart, 4 + dataLength);
        uint computedCrc = ComputeCrc32(crcSpan);
        if (computedCrc != crcOk.Value)
        {
            cursor.Reset(cp);
            return Failure.At(
                crcOffset,
                ErrorKind.CrcMismatch,
                $"PNG chunk CRC mismatch: expected 0x{crcOk.Value:X8}, computed 0x{computedCrc:X8}");
        }

        var sub = cursor.CreateSubCursor(dataOk.Value, InputMode.Complete);
        return DecodeChunkPayload(typeOk.Value, dataOk.Value, ref sub) switch
        {
            Parsed<PngChunk> ok => ok,
            Incomplete inc => ResetAndReturn(ref cursor, cp, inc),
            Backtrack err => ResetAndReturn(ref cursor, cp, new Failure(err.Error)),
            Failure fail => ResetAndReturn(ref cursor, cp, fail),
        };

        static ParseResult<PngChunk> ResetAndReturn(ref ByteCursor c, Checkpoint checkpoint, ParseResult<PngChunk> res)
        {
            c.Reset(checkpoint);
            return res;
        }
    }

    private static ParseResult<PngChunk> DecodeChunkPayload(
        uint typeCode,
        ByteSlice dataSlice,
        ref ByteCursor sub)
    {
        switch (typeCode)
        {
            case TypeIhdr:
                if (dataSlice.Length != 13)
                {
                    return Failure.At(sub.Position, ErrorKind.Protocol, $"IHDR chunk must be 13 bytes, got {dataSlice.Length}");
                }

                if (!sub.ReadU32BE().TryGetValue(out Parsed<uint> wOk)
                    || !sub.ReadU32BE().TryGetValue(out Parsed<uint> hOk)
                    || !sub.ReadU8().TryGetValue(out Parsed<byte> bdOk)
                    || !sub.ReadU8().TryGetValue(out Parsed<byte> ctOk)
                    || !sub.ReadU8().TryGetValue(out Parsed<byte> compOk)
                    || !sub.ReadU8().TryGetValue(out Parsed<byte> filtOk)
                    || !sub.ReadU8().TryGetValue(out Parsed<byte> intOk))
                {
                    return Failure.At(sub.Position, ErrorKind.Protocol, "Truncated IHDR payload");
                }

                uint width = wOk.Value;
                uint height = hOk.Value;
                byte bitDepth = bdOk.Value;
                byte colorTypeRaw = ctOk.Value;
                byte compression = compOk.Value;
                byte filter = filtOk.Value;
                byte interlace = intOk.Value;

                if (width == 0 || height == 0)
                {
                    return Failure.At(dataSlice.Offset, ErrorKind.Verify, "PNG dimensions must be non-zero");
                }

                if (!Enum.IsDefined((PngColorType)colorTypeRaw))
                {
                    return Failure.At(dataSlice.Offset + 9, ErrorKind.Verify, $"Unknown PNG color type {colorTypeRaw}");
                }

                PngChunk ihdr = new IhdrChunk(width, height, bitDepth, (PngColorType)colorTypeRaw, compression, filter, interlace);
                return new Parsed<PngChunk>(ihdr);

            case TypePlte:
                if (dataSlice.Length == 0 || dataSlice.Length % 3 != 0)
                {
                    return Failure.At(dataSlice.Offset, ErrorKind.Protocol, "PLTE chunk length must be a positive multiple of 3");
                }

                PngChunk plte = new PlteChunk(dataSlice);
                return new Parsed<PngChunk>(plte);

            case TypeIdat:
                PngChunk idat = new IdatChunk(dataSlice);
                return new Parsed<PngChunk>(idat);

            case TypeText:
                var kwRes = sub.ReadCString();
                if (!kwRes.TryGetValue(out Parsed<ByteSlice> kwOk))
                {
                    return Failure.At(sub.Position, ErrorKind.Protocol, "tEXt chunk missing null separator after keyword");
                }

                var restRes = sub.TakeRest();
                if (!restRes.TryGetValue(out Parsed<ByteSlice> restOk))
                {
                    return restRes.PropagateNonParsed<PngChunk>();
                }

                PngChunk text = new TextChunk(kwOk.Value, restOk.Value);
                return new Parsed<PngChunk>(text);

            case TypeIend:
                if (dataSlice.Length != 0)
                {
                    return Failure.At(dataSlice.Offset, ErrorKind.Protocol, "IEND chunk must have empty payload");
                }

                PngChunk iend = default(IendChunk);
                return new Parsed<PngChunk>(iend);

            default:
                PngChunk unknown = new UnknownChunk(typeCode, dataSlice);
                return new Parsed<PngChunk>(unknown);
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

    /// <summary>Computes IEEE 802.3 CRC-32 using safe slicing-by-8 lookup tables (zero allocations).</summary>
    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint[][] tables = CrcTables;
        uint[] t0 = tables[0];
        uint[] t1 = tables[1];
        uint[] t2 = tables[2];
        uint[] t3 = tables[3];
        uint[] t4 = tables[4];
        uint[] t5 = tables[5];
        uint[] t6 = tables[6];
        uint[] t7 = tables[7];

        uint crc = 0xFFFF_FFFFu;
        int i = 0;
        int end8 = data.Length - 7;
        while (i < end8)
        {
            uint lo = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4)) ^ crc;
            uint hi = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4, 4));
            crc = t7[lo & 0xFF]
                ^ t6[(lo >> 8) & 0xFF]
                ^ t5[(lo >> 16) & 0xFF]
                ^ t4[lo >> 24]
                ^ t3[hi & 0xFF]
                ^ t2[(hi >> 8) & 0xFF]
                ^ t1[(hi >> 16) & 0xFF]
                ^ t0[hi >> 24];
            i += 8;
        }

        for (; i < data.Length; i++)
        {
            crc = t0[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
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

    private static uint[][] BuildCrcTables()
    {
        var tables = new uint[8][];
        for (int t = 0; t < 8; t++)
        {
            tables[t] = new uint[256];
        }

        uint[] t0 = tables[0];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB8_8320u ^ (c >> 1) : c >> 1;
            }

            t0[i] = c;
        }

        for (int t = 1; t < 8; t++)
        {
            uint[] prev = tables[t - 1];
            uint[] curr = tables[t];
            for (int i = 0; i < 256; i++)
            {
                uint c = prev[i];
                curr[i] = (c >> 8) ^ t0[c & 0xFF];
            }
        }

        return tables;
    }
}
