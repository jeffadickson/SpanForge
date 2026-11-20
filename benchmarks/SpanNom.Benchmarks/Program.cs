using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SpanNom;
using SpanNom.Protocols.Png;
using SpanNom.Protocols.WebSocket;

const int RingSize = 256;

// ============================================================================
// Shared deterministic datasets (bit-for-bit identical to Rust benchmark)
// ============================================================================

static byte[][] GenWsRing()
{
    var ring = new byte[RingSize][];
    for (int i = 0; i < RingSize; i++)
    {
        byte opcode = (i % 3) switch { 0 => 1, 1 => 2, _ => 9 };
        Span<byte> mask =
        [
            (byte)(0x37 ^ i),
            (byte)(0xFA ^ (i * 3)),
            (byte)(0x21 ^ (i * 7)),
            (byte)(0x3D ^ (i * 11)),
        ];
        var buf = new byte[19];
        buf[0] = (byte)(0x80 | opcode);
        buf[1] = 0x80 | 13;
        mask.CopyTo(buf.AsSpan(2, 4));
        for (int k = 0; k < 13; k++)
        {
            byte plain = (byte)((i * 17 + k * 31 + 65) & 0x7F);
            buf[6 + k] = (byte)(plain ^ mask[k & 3]);
        }
        ring[i] = buf;
    }
    return ring;
}

static byte[][] GenDelimitedRing()
{
    var ring = new byte[RingSize][];
    for (int i = 0; i < RingSize; i++)
    {
        ushort v = (ushort)((i * 257 + 0x1234) & 0xFFFF);
        ring[i] = [(byte)'[', (byte)(v >> 8), (byte)(v & 0xFF), (byte)']'];
    }
    return ring;
}

static void EncodeLeb128(ulong v, List<byte> outBuf)
{
    while (true)
    {
        byte b = (byte)(v & 0x7F);
        v >>= 7;
        if (v != 0) b |= 0x80;
        outBuf.Add(b);
        if (v == 0) break;
    }
}

static byte[][] GenVarIntRing()
{
    var ring = new byte[RingSize][];
    for (int i = 0; i < RingSize; i++)
    {
        List<byte> buf = [with(capacity: 160)];
        for (int j = 0; j < 32; j++)
        {
            ulong val = (j & 3) switch
            {
                0 => (ulong)((i + j) & 0x7F),
                1 => (ulong)(((i * 53 + j * 19) & 0x3FFF) + 128),
                2 => ((((ulong)i * 100_003UL + (ulong)j * 7_919UL) & 0x0FFF_FFFFUL) + 16_384UL),
                _ => (((ulong)i + 1UL) << 40) | ((ulong)j * 0x1234_5678UL),
            };
            EncodeLeb128(val, buf);
        }
        ring[i] = buf.ToArray();
    }
    return ring;
}

static byte[][] GenTlvRing()
{
    var ring = new byte[RingSize][];
    Span<byte> tmp = stackalloc byte[8];
    for (int i = 0; i < RingSize; i++)
    {
        List<byte> buf = [with(capacity: 200)];
        for (int j = 0; j < 16; j++)
        {
            switch (j % 3)
            {
                case 0:
                    buf.Add(0x01);
                    BinaryPrimitives.WriteUInt32BigEndian(tmp, (uint)(i * 1000 + j));
                    for (int k = 0; k < 4; k++) buf.Add(tmp[k]);
                    break;
                case 1:
                    buf.Add(0x02);
                    BinaryPrimitives.WriteDoubleBigEndian(tmp, (i + 1) * 1.5 + j);
                    for (int k = 0; k < 8; k++) buf.Add(tmp[k]);
                    break;
                default:
                    buf.Add(0x03);
                    BinaryPrimitives.WriteUInt16BigEndian(tmp, 8);
                    buf.Add(tmp[0]);
                    buf.Add(tmp[1]);
                    for (int k = 0; k < 8; k++) buf.Add((byte)((i + j + k) & 0xFF));
                    break;
            }
        }
        ring[i] = buf.ToArray();
    }
    return ring;
}

static byte[][] GenPngRing()
{
    var ring = new byte[RingSize][];
    for (int i = 0; i < RingSize; i++)
    {
        uint w = (uint)(256 + i);
        uint h = (uint)(128 + (i & 63));
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), h);
        ihdr[8] = 8;
        ihdr[9] = 2;

        byte[] idat = new byte[32];
        for (int k = 0; k < 32; k++) idat[k] = (byte)((i * 7 + k * 13) & 0xFF);

        ring[i] =
        [
            .. PngParser.Signature,
            .. PngParser.BuildChunk("IHDR"u8, ihdr),
            .. PngParser.BuildChunk("PLTE"u8, [255, 0, 0, 0, 255, 0]),
            .. PngParser.BuildChunk("tEXt"u8, "Author\0Bench"u8),
            .. PngParser.BuildChunk("IDAT"u8, idat),
            .. PngParser.BuildChunk("gAMA"u8, [0x00, 0x00, 0xB1, 0x8F]),
            .. PngParser.BuildChunk("IEND"u8, []),
        ];
    }
    return ring;
}

// ============================================================================
// Benchmark Harness (TieredPGO warmup + 7 runs, median ns/op + alloc bytes/op)
// ============================================================================

static void RunBench(string name, string engine, int opsPerRun, int bytesPerOp, Func<ulong> fn)
{
    // Generous warmup so .NET 11 TieredPGO promotes hot loops to Tier 1 optimized code!
    ulong expected = fn();
    for (int w = 0; w < 4; w++)
    {
        ulong check = fn();
        if (check != expected) throw new InvalidOperationException("Warmup checksum mismatch");
    }

    double[] samplesNs = new double[7];
    ulong lastChk = 0;
    long allocBytes = 0;

    for (int r = 0; r < samplesNs.Length; r++)
    {
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        long startTs = Stopwatch.GetTimestamp();
        lastChk = fn();
        var elapsed = Stopwatch.GetElapsedTime(startTs);
        allocBytes = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;

        if (lastChk != expected) throw new InvalidOperationException("Checksum mismatch");
        samplesNs[r] = elapsed.TotalNanoseconds / opsPerRun;
    }

    Array.Sort(samplesNs);
    double medianNs = samplesNs[samplesNs.Length / 2];
    double mops = 1_000.0 / medianNs;
    double mibS = (bytesPerOp * 1e9 / medianNs) / (1024.0 * 1024.0);
    double allocPerOp = (double)allocBytes / opsPerRun;
    Console.WriteLine(
        $"{name,-16} | {engine,-12} | {medianNs,8:F2} ns/op | {mops,7:F2} M ops/s | {mibS,8:F1} MiB/s | alloc={allocPerOp:F1} B/op | chk={lastChk}");
}

var wsRing = GenWsRing();
var delimRing = GenDelimitedRing();
var varintRing = GenVarIntRing();
var tlvRing = GenTlvRing();
var pngRing = GenPngRing();

int varintAvgBytes = (int)varintRing.Average(static b => b.Length);
int tlvBytes = tlvRing[0].Length;
int pngBytes = pngRing[0].Length;

Console.WriteLine("=== C# 15 / .NET 11.0.100 Benchmark (SpanNom, Release + TieredPGO) ===");

// 1. WebSocket Frame (BitCursor + zero-boxing [Union] WebSocketFrame + UnmaskInto)
const int WsOps = 1_000_000;
RunBench("ws_frame", "cs15/spannom", WsOps, 19, () => BenchWs(wsRing, WsOps));

// 2. Delimited u16 (Monomorphized StructParsers)
const int DelimOps = 5_000_000;
RunBench("delimited_u16", "cs15/struct", DelimOps, 4, () => BenchDelimitedStruct(delimRing, DelimOps));
RunBench("delimited_u16", "cs15/comb", DelimOps, 4, () => BenchDelimitedCombinator(delimRing, DelimOps));

// 3. VarInt stream (32 varints per op)
const int VarIntOps = 200_000;
RunBench("varint_stream32", "cs15/spannom", VarIntOps, varintAvgBytes, () => BenchVarInt(varintRing, VarIntOps));

// 4. TLV stream (16 mixed-tag items per op with Alt + non-boxing [Union] TlvValue)
const int TlvOps = 200_000;
RunBench("tlv_stream16", "cs15/struct", TlvOps, tlvBytes, () => BenchTlvStruct(tlvRing, TlvOps));
RunBench("tlv_stream16", "cs15/comb", TlvOps, tlvBytes, () => BenchTlv(tlvRing, TlvOps));

// 5. PNG full document + CRC-32 (native C# 15 `public union PngChunk`)
const int PngOps = 200_000;
RunBench("png_chunks_crc", "cs15/spannom", PngOps, pngBytes, () => BenchPng(pngRing, PngOps));

return;

// ============================================================================
// Benchmark Kernels
// ============================================================================

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchWs(byte[][] ring, int ops)
{
    Span<byte> scratch = stackalloc byte[32];
    ulong acc = 0;

    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & 255];
        var cursor = buf.AsCursor();
        var frame = WebSocketFrameParser.ParseFrame(ref cursor).Unwrap();
        ulong contrib = frame switch
        {
            TextFrame(_, var m, var p) => 1UL + UnmaskSum(buf, p, m, scratch),
            BinaryFrame(_, var m, var p) => 2UL + UnmaskSum(buf, p, m, scratch),
            PingFrame(var m, var p) => 3UL + UnmaskSum(buf, p, m, scratch),
            PongFrame(var m, var p) => 4UL + UnmaskSum(buf, p, m, scratch),
            CloseFrame(var m, _, var p) => 5UL + UnmaskSum(buf, p, m, scratch),
            ContinuationFrame(_, var m, var p) => 6UL + UnmaskSum(buf, p, m, scratch),
        };
        acc = unchecked(acc + contrib);
    }

    return acc;
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
static ulong UnmaskSum(ReadOnlySpan<byte> src, ByteSlice payload, Option<uint> mask, Span<byte> scratch)
{
    WebSocketFrameParser.UnmaskInto(src, payload, mask, scratch);
    ulong s = 0;
    for (int i = 0; i < payload.Length; i++) s += scratch[i];
    return s;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchDelimitedStruct(byte[][] ring, int ops)
{
    var parser = StructParsers.Delimited<
        StructParsers.TagByteParser,
        StructParsers.U16Parser,
        StructParsers.TagByteParser,
        byte, ushort, byte>(
        StructParsers.TagByte((byte)'['),
        StructParsers.U16,
        StructParsers.TagByte((byte)']'));

    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        var c = ring[iter & 255].AsCursor();
        if (parser.Parse(ref c).TryGetValue(out Parsed<ushort> ok))
        {
            acc = unchecked(acc + ok.Value);
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchDelimitedCombinator(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        var c = ring[iter & 255].AsCursor();
        var res = Parse.Delimited(
            ref c,
            static (ref ByteCursor x) => x.TagByte((byte)'['),
            static (ref ByteCursor x) => x.ReadU16BE(),
            static (ref ByteCursor x) => x.TagByte((byte)']'));
        if (res.TryGetValue(out Parsed<ushort> ok))
        {
            acc = unchecked(acc + ok.Value);
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchVarInt(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        var c = ring[iter & 255].AsCursor();
        while (!c.IsAtEnd)
        {
            if (c.ReadVarUInt64().TryGetValue(out Parsed<ulong> ok))
            {
                acc = unchecked(acc + ok.Value);
            }
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchTlvStruct(byte[][] ring, int ops)
{
    var parser = StructParsers.Alt<TlvU32Parser, TlvF64Parser, TlvBytesParser, TlvValue>(default, default, default);
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & 255];
        ReadOnlySpan<byte> span = buf;
        var c = span.AsCursor();
        while (!c.IsAtEnd)
        {
            if (parser.Parse(ref c).TryGetValue(out Parsed<TlvValue> ok))
            {
                ulong add = ok.Value switch
                {
                    TlvU32(var v) => v,
                    TlvF64(var d) => BitConverter.DoubleToUInt64Bits(d),
                    TlvBytes(var slice) => SumBytes(span[slice]),
                };
                acc = unchecked(acc + add);
            }
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchTlv(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & 255];
        ReadOnlySpan<byte> span = buf;
        var c = span.AsCursor();
        while (!c.IsAtEnd)
        {
            var res = Parse.Alt(ref c, ParseTlvU32, ParseTlvF64, ParseTlvBytes);
            if (res.TryGetValue(out Parsed<TlvValue> ok))
            {
                ulong add = ok.Value switch
                {
                    TlvU32(var v) => v,
                    TlvF64(var d) => BitConverter.DoubleToUInt64Bits(d),
                    TlvBytes(var slice) => SumBytes(span[slice]),
                };
                acc = unchecked(acc + add);
            }
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
static ulong SumBytes(ReadOnlySpan<byte> s)
{
    ulong sum = 0;
    for (int i = 0; i < s.Length; i++) sum += s[i];
    return sum;
}

static ParseResult<TlvValue> ParseTlvU32(ref ByteCursor c)
{
    var cp = c.Save();
    var t = c.TagByte(0x01);
    if (!t.IsParsed) return t.PropagateNonParsed<TlvValue>();
    var v = c.ReadU32BE();
    if (!v.TryGetValue(out Parsed<uint> ok)) { c.Reset(cp); return v.PropagateNonParsed<TlvValue>(); }
    return new Parsed<TlvValue>(new TlvU32(ok.Value));
}

static ParseResult<TlvValue> ParseTlvF64(ref ByteCursor c)
{
    var cp = c.Save();
    var t = c.TagByte(0x02);
    if (!t.IsParsed) return t.PropagateNonParsed<TlvValue>();
    var v = c.ReadF64BE();
    if (!v.TryGetValue(out Parsed<double> ok)) { c.Reset(cp); return v.PropagateNonParsed<TlvValue>(); }
    return new Parsed<TlvValue>(new TlvF64(ok.Value));
}

static ParseResult<TlvValue> ParseTlvBytes(ref ByteCursor c)
{
    var cp = c.Save();
    var t = c.TagByte(0x03);
    if (!t.IsParsed) return t.PropagateNonParsed<TlvValue>();
    var v = c.ReadLengthPrefixedBytes(LengthPrefix.U16);
    if (!v.TryGetValue(out Parsed<ByteSlice> ok)) { c.Reset(cp); return v.PropagateNonParsed<TlvValue>(); }
    return new Parsed<TlvValue>(new TlvBytes(ok.Value));
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchPng(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & 255];
        ReadOnlySpan<byte> span = buf;
        var c = span.AsCursor();
        _ = PngParser.ParseSignature(ref c).Unwrap();

        chunkLoop:
        while (true)
        {
            var chunk = PngParser.ParseChunk(ref c).Unwrap();
            switch (chunk)
            {
                case IhdrChunk h:
                    acc = unchecked(acc + h.Width + h.Height);
                    break;
                case PlteChunk p:
                    acc = unchecked(acc + (ulong)p.EntryCount);
                    break;
                case TextChunk(var kw, var txt):
                    acc = unchecked(acc + (ulong)(kw.Length + txt.Length));
                    break;
                case IdatChunk(var d):
                    acc = unchecked(acc + span[d][0] + (ulong)d.Length);
                    break;
                case UnknownChunk u:
                    acc = unchecked(acc + u.TypeCode);
                    break;
                case IendChunk:
                    acc = unchecked(acc + 1UL);
                    break chunkLoop;
            }
        }
    }
    return acc;
}

// Non-boxing custom C# 15 [Union] for TLV hot-path comparison
public readonly record struct TlvU32(uint Value);
public readonly record struct TlvF64(double Value);
public readonly record struct TlvBytes(ByteSlice Slice);

[Union]
public readonly struct TlvValue : IUnion
{
    private readonly ulong _bits; // u32 or f64 bits or packed ByteSlice
    private readonly byte _tag;   // 1=U32, 2=F64, 3=Bytes

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TlvValue(TlvU32 v) => (_tag, _bits) = (1, v.Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TlvValue(TlvF64 v) => (_tag, _bits) = (2, BitConverter.DoubleToUInt64Bits(v.Value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TlvValue(TlvBytes v) => (_tag, _bits) = (3, ((ulong)(uint)v.Slice.Offset << 32) | (uint)v.Slice.Length);

    public object? Value => _tag switch
    {
        1 => new TlvU32((uint)_bits),
        2 => new TlvF64(BitConverter.UInt64BitsToDouble(_bits)),
        3 => new TlvBytes(new ByteSlice((int)(_bits >> 32), (int)(uint)_bits)),
        _ => null,
    };

    public bool HasValue => _tag != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out TlvU32 value)
    {
        if (_tag == 1) { value = new TlvU32((uint)_bits); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out TlvF64 value)
    {
        if (_tag == 2) { value = new TlvF64(BitConverter.UInt64BitsToDouble(_bits)); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out TlvBytes value)
    {
        if (_tag == 3) { value = new TlvBytes(new ByteSlice((int)(_bits >> 32), (int)(uint)_bits)); return true; }
        value = default; return false;
    }
}

public readonly struct TlvU32Parser : IParser<TlvValue>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TlvValue> Parse(ref ByteCursor c)
    {
        var cp = c.Save();
        var t = c.TagByte(0x01);
        if (!t.IsParsed) return t.PropagateNonParsed<TlvValue>();
        var v = c.ReadU32BE();
        if (!v.TryGetValue(out Parsed<uint> ok)) { c.Reset(cp); return v.PropagateNonParsed<TlvValue>(); }
        return new Parsed<TlvValue>(new TlvU32(ok.Value));
    }
}

public readonly struct TlvF64Parser : IParser<TlvValue>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TlvValue> Parse(ref ByteCursor c)
    {
        var cp = c.Save();
        var t = c.TagByte(0x02);
        if (!t.IsParsed) return t.PropagateNonParsed<TlvValue>();
        var v = c.ReadF64BE();
        if (!v.TryGetValue(out Parsed<double> ok)) { c.Reset(cp); return v.PropagateNonParsed<TlvValue>(); }
        return new Parsed<TlvValue>(new TlvF64(ok.Value));
    }
}

public readonly struct TlvBytesParser : IParser<TlvValue>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TlvValue> Parse(ref ByteCursor c)
    {
        var cp = c.Save();
        var t = c.TagByte(0x03);
        if (!t.IsParsed) return t.PropagateNonParsed<TlvValue>();
        var v = c.ReadLengthPrefixedBytes(LengthPrefix.U16);
        if (!v.TryGetValue(out Parsed<ByteSlice> ok)) { c.Reset(cp); return v.PropagateNonParsed<TlvValue>(); }
        return new Parsed<TlvValue>(new TlvBytes(ok.Value));
    }
}
