using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SpanForge;
using SpanForge.Protocols.Http2;
using SpanForge.Protocols.Mqtt;
using SpanForge.Protocols.Png;
using SpanForge.Protocols.Protobuf;
using SpanForge.Protocols.WebSocket;

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

static void RunBench(string name, string engine, int opsPerRun, int bytesPerOp, Func<int, ulong> fn)
{
    // Trigger .NET 11 TieredPGO promotion (default call count threshold is 30 + 100ms timer) before full-scale timing!
    for (int w = 0; w < 40; w++) _ = fn(256);
    Thread.Sleep(180);

    ulong expected = fn(opsPerRun);
    for (int w = 0; w < 2; w++)
    {
        ulong check = fn(opsPerRun);
        if (check != expected) throw new InvalidOperationException("Warmup checksum mismatch");
    }

    double[] samplesNs = new double[7];
    ulong lastChk = 0;
    long allocBytes = 0;

    for (int r = 0; r < samplesNs.Length; r++)
    {
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        long startTs = Stopwatch.GetTimestamp();
        lastChk = fn(opsPerRun);
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

Console.WriteLine("=== C# 15 / .NET 11.0.100 Benchmark (SpanForge, Release + TieredPGO) ===");

// 1. WebSocket Frame (zero-boxing [Union] WebSocketFrame + 64-bit word UnmaskInto)
const int WsOps = 1_000_000;
RunBench("ws_frame", "cs15/try", WsOps, 19, ops => BenchWsTry(wsRing, ops));
RunBench("ws_frame", "cs15/spanforge", WsOps, 19, ops => BenchWs(wsRing, ops));

// 2. Delimited u16 (TryParsers + FastParsers<NoError> + StructParsers + Combinators)
const int DelimOps = 5_000_000;
RunBench("delimited_u16", "cs15/try", DelimOps, 4, ops => BenchDelimitedTry(delimRing, ops));
RunBench("delimited_u16", "cs15/fast", DelimOps, 4, ops => BenchDelimitedFast(delimRing, ops));
RunBench("delimited_u16", "cs15/struct", DelimOps, 4, ops => BenchDelimitedStruct(delimRing, ops));
RunBench("delimited_u16", "cs15/comb", DelimOps, 4, ops => BenchDelimitedCombinator(delimRing, ops));

// 3. VarInt stream (32 varints per op)
const int VarIntOps = 200_000;
RunBench("varint_stream32", "cs15/try", VarIntOps, varintAvgBytes, ops => BenchVarIntTry(varintRing, ops));
RunBench("varint_stream32", "cs15/fast", VarIntOps, varintAvgBytes, ops => BenchVarIntFast(varintRing, ops));
RunBench("varint_stream32", "cs15/spanforge", VarIntOps, varintAvgBytes, ops => BenchVarInt(varintRing, ops));

// 4. TLV stream (16 mixed-tag items per op with FoldDispatch / Dispatch / Alt + non-boxing [Union] TlvValue)
const int TlvOps = 200_000;
RunBench("tlv_stream16", "cs15/fold", TlvOps, tlvBytes, ops => BenchTlvFold(tlvRing, ops));
RunBench("tlv_stream16", "cs15/dispatch", TlvOps, tlvBytes, ops => BenchTlvDispatch(tlvRing, ops));
RunBench("tlv_stream16", "cs15/try", TlvOps, tlvBytes, ops => BenchTlvTry(tlvRing, ops));
RunBench("tlv_stream16", "cs15/fast", TlvOps, tlvBytes, ops => BenchTlvFast(tlvRing, ops));
RunBench("tlv_stream16", "cs15/struct", TlvOps, tlvBytes, ops => BenchTlvStruct(tlvRing, ops));
RunBench("tlv_stream16", "cs15/comb", TlvOps, tlvBytes, ops => BenchTlv(tlvRing, ops));

// 5. PNG full document + CRC-32 (zero-boxing custom [Union] PngChunk + PCLMULQDQ/slicing-by-8 CRC-32)
const int PngOps = 200_000;
RunBench("png_chunks_crc", "cs15/try", PngOps, pngBytes, ops => BenchPngTry(pngRing, ops));
RunBench("png_chunks_crc", "cs15/spanforge", PngOps, pngBytes, ops => BenchPng(pngRing, ops));

// 6. Protobuf wire message (6 mixed fields: varints, fixed64, fixed32, length-delimited)
var pbRing = GenProtobufRing();
const int PbOps = 500_000;
RunBench("protobuf_msg", "cs15/try", PbOps, pbRing[0].Length, ops => BenchProtobufTry(pbRing, ops));
RunBench("protobuf_msg", "cs15/spanforge", PbOps, pbRing[0].Length, ops => BenchProtobuf(pbRing, ops));

// 7. HTTP/2 frame sequence (SETTINGS + HEADERS + DATA per op)
var h2Ring = GenHttp2Ring();
const int H2Ops = 500_000;
RunBench("http2_frames", "cs15/try", H2Ops, h2Ring[0].Length, ops => BenchHttp2Try(h2Ring, ops));
RunBench("http2_frames", "cs15/spanforge", H2Ops, h2Ring[0].Length, ops => BenchHttp2(h2Ring, ops));

// 8. MQTT v5.0 PUBLISH packet (QoS 1 + topic + packet ID + properties + payload)
var mqttRing = GenMqttRing();
const int MqttOps = 500_000;
RunBench("mqtt5_publish", "cs15/try", MqttOps, mqttRing[0].Length, ops => BenchMqttTry(mqttRing, ops));
RunBench("mqtt5_publish", "cs15/spanforge", MqttOps, mqttRing[0].Length, ops => BenchMqtt(mqttRing, ops));

// 9. Compile-time generated [BinaryStruct] TryParse + Parse/TryWriteTo roundtrip
var genRing = GenBinaryStructRing();
const int GenOps = 1_000_000;
RunBench("gen_struct", "cs15/tryparse", GenOps, genRing[0].Length, ops => BenchGeneratedStructTryParse(genRing, ops));
RunBench("gen_struct_rw", "cs15/roundtrip", GenOps, genRing[0].Length, ops => BenchGeneratedStructRoundtrip(genRing, ops));

// 10. SIMD payload kernels (1 KiB WebSocket Vector256 unmask + 4 KiB PNG PCLMULQDQ CRC-32)
byte[] wsBig = new byte[1024];
for (int i = 0; i < wsBig.Length; i++) wsBig[i] = (byte)(i * 31 + 17);
RunBench("ws_unmask_1k", "cs15/simd256", 1_000_000, wsBig.Length, ops => BenchWsUnmask1K(wsBig, ops));

byte[] crcBig = new byte[4096];
for (int i = 0; i < crcBig.Length; i++) crcBig[i] = (byte)(i * 13 + 7);
RunBench("png_crc_4k", "cs15/pclmul", 200_000, crcBig.Length, ops => BenchPngCrc4K(crcBig, ops));

return;

static byte[][] GenProtobufRing()
{
    var ring = new byte[RingSize][];
    Span<byte> scratch = stackalloc byte[128];
    for (int i = 0; i < RingSize; i++)
    {
        var w = scratch.AsWriter(Endianness.Little);
        // Field 1 (varint): user id
        w.TryWriteVarUInt64((1UL << 3) | 0);
        w.TryWriteVarUInt64((ulong)(100_000 + i * 37));
        // Field 2 (length-delimited): 12-byte name
        w.TryWriteVarUInt64((2UL << 3) | 2);
        w.TryWriteVarUInt64(12);
        w.TryWriteBytes("spanforge_v1"u8);
        // Field 3 (fixed64): timestamp
        w.TryWriteVarUInt64((3UL << 3) | 1);
        w.TryWriteU64LE(0x0102_0304_0506_0700UL | (uint)i);
        // Field 4 (fixed32): score bits
        w.TryWriteVarUInt64((4UL << 3) | 5);
        w.TryWriteU32LE(0x3F80_0000u + (uint)i);
        // Field 5 (varint): flags
        w.TryWriteVarUInt64((5UL << 3) | 0);
        w.TryWriteVarUInt64((ulong)(i & 0xFF));
        // Field 6 (length-delimited): 8-byte payload
        w.TryWriteVarUInt64((6UL << 3) | 2);
        w.TryWriteVarUInt64(8);
        w.TryWriteU64BE(0xAABB_CCDD_EEFF_0000UL | (uint)i);
        ring[i] = w.Written.ToArray();
    }
    return ring;
}

static byte[][] GenHttp2Ring()
{
    var ring = new byte[RingSize][];
    Span<byte> scratch = stackalloc byte[128];
    for (int i = 0; i < RingSize; i++)
    {
        var w = scratch.AsWriter(Endianness.Big);
        uint streamId = (uint)((i * 2) + 1);
        // Frame 1: SETTINGS (6 bytes = 1 entry)
        w.TryWriteU8(0); w.TryWriteU16BE(6); w.TryWriteU8((byte)Http2FrameType.Settings); w.TryWriteU8(0); w.TryWriteU32BE(0);
        w.TryWriteU16BE(0x03); w.TryWriteU32BE(100u + (uint)i);
        // Frame 2: HEADERS (10 bytes HPACK stub, END_HEADERS)
        w.TryWriteU8(0); w.TryWriteU16BE(10); w.TryWriteU8((byte)Http2FrameType.Headers); w.TryWriteU8((byte)Http2FrameFlags.EndHeaders); w.TryWriteU32BE(streamId);
        w.TryWriteBytes([0x82, 0x86, 0x84, 0x41, 0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o']);
        // Frame 3: DATA (16 bytes payload, END_STREAM)
        w.TryWriteU8(0); w.TryWriteU16BE(16); w.TryWriteU8((byte)Http2FrameType.Data); w.TryWriteU8((byte)Http2FrameFlags.EndStream); w.TryWriteU32BE(streamId);
        w.TryWriteU64BE(0x1122_3344_5566_7700UL | (uint)i);
        w.TryWriteU64BE(0x8899_AABB_CCDD_EE00UL | (uint)i);
        ring[i] = w.Written.ToArray();
    }
    return ring;
}

static byte[][] GenMqttRing()
{
    var ring = new byte[RingSize][];
    Span<byte> scratch = stackalloc byte[128];
    ReadOnlySpan<byte> topic = "sensors/floor1/temp"u8;
    for (int i = 0; i < RingSize; i++)
    {
        // Remaining = 2 + topic(19) + 2(pktId) + 1(propsLen=0) + 16(payload) = 40 bytes
        var w = scratch.AsWriter(Endianness.Big);
        w.TryWriteU8(0x32); // PUBLISH, QoS 1
        w.TryWriteU8(40);   // Remaining length (1-byte VBI)
        w.TryWriteU16BE((ushort)topic.Length);
        w.TryWriteBytes(topic);
        w.TryWriteU16BE((ushort)(i + 1)); // PacketId
        w.TryWriteU8(0); // Properties length = 0
        w.TryWriteU64BE(0x4142_4344_4546_4700UL | (uint)i);
        w.TryWriteU64BE(0x3031_3233_3435_3600UL | (uint)i);
        ring[i] = w.Written.ToArray();
    }
    return ring;
}

static byte[][] GenBinaryStructRing()
{
    var ring = new byte[RingSize][];
    ReadOnlySpan<byte> payload = "bench123"u8;
    Span<byte> scratch = stackalloc byte[64];
    for (int i = 0; i < RingSize; i++)
    {
        var rec = new BenchGeneratedPacket(
            Kind: 7,
            Port: (ushort)(8000 + i),
            Timestamp: (uint)(1_700_000_000 + i),
            Score: 100.5 + i,
            Sequence: (ulong)(10_000 + i * 17),
            Payload: new ByteSlice(0, payload.Length));
        int written = rec.TryWriteTo(scratch, payload);
        ring[i] = scratch[..written].ToArray();
    }
    return ring;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchProtobufTry(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & (RingSize - 1)];
        var c = new ByteCursor(buf);
        while (!c.IsAtEnd)
        {
            if (ProtobufParser.TryParseField(ref c, out ProtobufField f).IsParsed)
            {
                if (f.TryGetValue(out ProtobufVarIntField vi)) acc = unchecked(acc + vi.Value + vi.FieldNumber);
                else if (f.TryGetValue(out ProtobufFixed64Field f64)) acc = unchecked(acc + f64.Bits);
                else if (f.TryGetValue(out ProtobufFixed32Field f32)) acc = unchecked(acc + f32.Bits);
                else if (f.TryGetValue(out ProtobufLengthDelimitedField ld)) acc = unchecked(acc + (ulong)ld.Payload.Length + buf[ld.Payload.Offset]);
            }
            else break;
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchProtobuf(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & (RingSize - 1)];
        var c = new ByteCursor(buf);
        while (!c.IsAtEnd)
        {
            if (ProtobufParser.ParseField(ref c).TryGetValue(out Parsed<ProtobufField> ok))
            {
                var f = ok.Value;
                if (f.TryGetValue(out ProtobufVarIntField vi)) acc = unchecked(acc + vi.Value + vi.FieldNumber);
                else if (f.TryGetValue(out ProtobufFixed64Field f64)) acc = unchecked(acc + f64.Bits);
                else if (f.TryGetValue(out ProtobufFixed32Field f32)) acc = unchecked(acc + f32.Bits);
                else if (f.TryGetValue(out ProtobufLengthDelimitedField ld)) acc = unchecked(acc + (ulong)ld.Payload.Length + buf[ld.Payload.Offset]);
            }
            else break;
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchHttp2Try(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & (RingSize - 1)];
        var c = new ByteCursor(buf);
        while (!c.IsAtEnd)
        {
            if (Http2FrameParser.TryParseFrame(ref c, out Http2Frame f).IsParsed)
            {
                if (f.TryGetValue(out Http2SettingsFrame s)) acc = unchecked(acc + (ulong)s.Entries.Length);
                else if (f.TryGetValue(out Http2HeadersFrame h)) acc = unchecked(acc + h.StreamId + (ulong)h.HeaderBlockFragment.Length);
                else if (f.TryGetValue(out Http2DataFrame d)) acc = unchecked(acc + d.StreamId + (ulong)d.Data.Length + buf[d.Data.Offset]);
            }
            else break;
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchHttp2(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & (RingSize - 1)];
        var c = new ByteCursor(buf);
        while (!c.IsAtEnd)
        {
            if (Http2FrameParser.ParseFrame(ref c).TryGetValue(out Parsed<Http2Frame> ok))
            {
                var f = ok.Value;
                if (f.TryGetValue(out Http2SettingsFrame s)) acc = unchecked(acc + (ulong)s.Entries.Length);
                else if (f.TryGetValue(out Http2HeadersFrame h)) acc = unchecked(acc + h.StreamId + (ulong)h.HeaderBlockFragment.Length);
                else if (f.TryGetValue(out Http2DataFrame d)) acc = unchecked(acc + d.StreamId + (ulong)d.Data.Length + buf[d.Data.Offset]);
            }
            else break;
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchMqttTry(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & (RingSize - 1)];
        var c = new ByteCursor(buf);
        if (MqttV5Parser.TryParsePacket(ref c, out MqttPacket pkt).IsParsed &&
            pkt.TryGetValue(out MqttPublishPacket pub))
        {
            acc = unchecked(acc + (ulong)pub.TopicName.Length + (ulong)pub.Payload.Length + buf[pub.Payload.Offset]);
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchMqtt(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & (RingSize - 1)];
        var c = new ByteCursor(buf);
        if (MqttV5Parser.ParsePacket(ref c).TryGetValue(out Parsed<MqttPacket> ok) &&
            ok.Value.TryGetValue(out MqttPublishPacket pub))
        {
            acc = unchecked(acc + (ulong)pub.TopicName.Length + (ulong)pub.Payload.Length + buf[pub.Payload.Offset]);
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchPngTry(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & 255];
        ReadOnlySpan<byte> span = buf;
        var c = span.AsCursor();
        if (!PngParser.TryParseSignature(ref c).IsParsed) continue;

        chunkLoop:
        while (PngParser.TryParseChunk(ref c, out PngChunk chunk).IsParsed)
        {
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

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchWsUnmask1K(byte[] payload, int ops)
{
    Span<byte> dst = stackalloc byte[1024];
    var slice = new ByteSlice(0, payload.Length);
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        var mask = Option<uint>.Of(0x37FA213Du ^ (uint)iter);
        WebSocketFrameParser.UnmaskInto(payload, slice, mask, dst);
        acc = unchecked(acc + dst[0] + dst[1023]);
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchPngCrc4K(byte[] data, int ops)
{
    ulong acc = 0;
    ReadOnlySpan<byte> span = data;
    for (int iter = 0; iter < ops; iter++)
    {
        acc = unchecked(acc + PngParser.ComputeCrc32(span));
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchGeneratedStructTryParse(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & (RingSize - 1)];
        var c = new ByteCursor(buf);
        if (BenchGeneratedPacket.TryParse(ref c, out var pkt).IsParsed)
        {
            acc = unchecked(acc + pkt.Port + pkt.Timestamp + pkt.Sequence + (ulong)pkt.Payload.Length);
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchGeneratedStructRoundtrip(byte[][] ring, int ops)
{
    Span<byte> outBuf = stackalloc byte[64];
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & (RingSize - 1)];
        var c = new ByteCursor(buf);
        if (BenchGeneratedPacket.TryParse(ref c, out var pkt).IsParsed)
        {
            int n = pkt.TryWriteTo(outBuf, buf);
            acc = unchecked(acc + (ulong)n + outBuf[n - 1]);
        }
    }
    return acc;
}

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
        ReadOnlySpan<byte> span = ring[iter & 255];
        var cursor = span.AsCursor();
        var frame = WebSocketFrameParser.ParseFrame(ref cursor).Unwrap();
        (ulong w, Option<uint> m, ByteSlice p) = frame switch
        {
            TextFrame(_, var mk, var pl) => (1UL, mk, pl),
            BinaryFrame(_, var mk, var pl) => (2UL, mk, pl),
            PingFrame(var mk, var pl) => (3UL, mk, pl),
            PongFrame(var mk, var pl) => (4UL, mk, pl),
            CloseFrame(var mk, _, var pl) => (5UL, mk, pl),
            ContinuationFrame(_, var mk, var pl) => (6UL, mk, pl),
        };
        acc = unchecked(acc + w + UnmaskSum(span, p, m, scratch));
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
    ref byte r = ref MemoryMarshal.GetReference(s);
    nuint i = 0;
    nuint len = (nuint)(uint)s.Length;
    while (i + 8 <= len)
    {
        sum += (ulong)(Unsafe.Add(ref r, i) + Unsafe.Add(ref r, i + 1) + Unsafe.Add(ref r, i + 2) + Unsafe.Add(ref r, i + 3)
             + Unsafe.Add(ref r, i + 4) + Unsafe.Add(ref r, i + 5) + Unsafe.Add(ref r, i + 6) + Unsafe.Add(ref r, i + 7));
        i += 8;
    }
    while (i < len)
    {
        sum += Unsafe.Add(ref r, i);
        i++;
    }
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

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchDelimitedFast(byte[][] ring, int ops)
{
    var parser = FastParsers.Delimited<
        FastParsers.TagByteParser<NoError>,
        FastParsers.U16BEParser<NoError>,
        FastParsers.TagByteParser<NoError>,
        byte, ushort, byte, NoError>(
        FastParsers.TagByte<NoError>((byte)'['),
        FastParsers.U16BE<NoError>(),
        FastParsers.TagByte<NoError>((byte)']'));

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
static ulong BenchVarIntFast(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        var c = ring[iter & 255].AsCursor();
        while (!c.IsAtEnd)
        {
            if (c.ReadVarUInt64<NoError>().TryGetValue(out Parsed<ulong> ok))
            {
                acc = unchecked(acc + ok.Value);
            }
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchTlvFast(byte[][] ring, int ops)
{
    var parser = FastParsers.Alt<FastTlvU32Parser, FastTlvF64Parser, FastTlvBytesParser, TlvValue, NoError>(default, default, default);
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
static ulong BenchWsTry(byte[][] ring, int ops)
{
    Span<byte> scratch = stackalloc byte[32];
    ulong acc = 0;

    for (int iter = 0; iter < ops; iter++)
    {
        ReadOnlySpan<byte> span = ring[iter & 255];
        var cursor = span.AsCursor();
        if (WebSocketFrameParser.TryParseFrame(ref cursor, out var frame).IsParsed)
        {
            (ulong w, Option<uint> m, ByteSlice p) = frame switch
            {
                TextFrame(_, var mk, var pl) => (1UL, mk, pl),
                BinaryFrame(_, var mk, var pl) => (2UL, mk, pl),
                PingFrame(var mk, var pl) => (3UL, mk, pl),
                PongFrame(var mk, var pl) => (4UL, mk, pl),
                CloseFrame(var mk, _, var pl) => (5UL, mk, pl),
                ContinuationFrame(_, var mk, var pl) => (6UL, mk, pl),
            };
            acc = unchecked(acc + w + UnmaskSum(span, p, m, scratch));
        }
    }

    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchDelimitedTry(byte[][] ring, int ops)
{
    var parser = TryParsers.Delimited<
        TryParsers.TagByteParser,
        TryParsers.U16BEParser,
        TryParsers.TagByteParser,
        byte, ushort, byte>(
        TryParsers.TagByte((byte)'['),
        TryParsers.U16BE,
        TryParsers.TagByte((byte)']'));

    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        var c = ring[iter & 255].AsCursor();
        if (parser.TryParse(ref c, out ushort v).IsParsed)
        {
            acc = unchecked(acc + v);
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchVarIntTry(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        var c = ring[iter & 255].AsCursor();
        while (!c.IsAtEnd)
        {
            if (c.TryReadVarUInt64(out ulong v).IsParsed)
            {
                acc = unchecked(acc + v);
            }
        }
    }
    return acc;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong BenchTlvTry(byte[][] ring, int ops)
{
    var parser = TryParsers.Alt<TryTlvU32Parser, TryTlvF64Parser, TryTlvBytesParser, TlvValue>(default, default, default);
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & 255];
        ReadOnlySpan<byte> span = buf;
        var c = span.AsCursor();
        while (!c.IsAtEnd)
        {
            if (parser.TryParse(ref c, out TlvValue val).IsParsed)
            {
                ulong add = val switch
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
static ulong BenchTlvDispatch(byte[][] ring, int ops)
{
    var parser = TryParsers.Dispatch<byte, TryParsers.U8Parser, TryTlvDispatchHandler, TlvValue>(TryParsers.U8, default);
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        byte[] buf = ring[iter & 255];
        ReadOnlySpan<byte> span = buf;
        var c = span.AsCursor();
        while (!c.IsAtEnd)
        {
            if (parser.TryParse(ref c, out TlvValue val).IsParsed)
            {
                ulong add = val switch
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
static ulong BenchTlvFold(byte[][] ring, int ops)
{
    ulong acc = 0;
    for (int iter = 0; iter < ops; iter++)
    {
        var c = ring[iter & 255].AsCursor();
        _ = TryParsers.FoldDispatch<byte, TryParsers.U8Parser, TryTlvFolder, ulong>(ref c, TryParsers.U8, default, ref acc);
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
        var t = c.TagByte(0x01);
        if (!t.IsParsed) return t.PropagateNonParsed<TlvValue>();
        var v = c.ReadU32BE();
        if (!v.TryGetValue(out Parsed<uint> ok)) return v.PropagateNonParsed<TlvValue>();
        return new Parsed<TlvValue>(new TlvU32(ok.Value));
    }
}

public readonly struct TlvF64Parser : IParser<TlvValue>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TlvValue> Parse(ref ByteCursor c)
    {
        var t = c.TagByte(0x02);
        if (!t.IsParsed) return t.PropagateNonParsed<TlvValue>();
        var v = c.ReadF64BE();
        if (!v.TryGetValue(out Parsed<double> ok)) return v.PropagateNonParsed<TlvValue>();
        return new Parsed<TlvValue>(new TlvF64(ok.Value));
    }
}

public readonly struct TlvBytesParser : IParser<TlvValue>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TlvValue> Parse(ref ByteCursor c)
    {
        var t = c.TagByte(0x03);
        if (!t.IsParsed) return t.PropagateNonParsed<TlvValue>();
        var v = c.ReadLengthPrefixedBytes(LengthPrefix.U16);
        if (!v.TryGetValue(out Parsed<ByteSlice> ok)) return v.PropagateNonParsed<TlvValue>();
        return new Parsed<TlvValue>(new TlvBytes(ok.Value));
    }
}

public readonly struct FastTlvU32Parser : IParser<TlvValue, NoError>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TlvValue, NoError> Parse(ref ByteCursor c)
    {
        var t = c.TagByte<NoError>(0x01);
        if (!t.IsParsed) return t.PropagateNonParsed<TlvValue>();
        var v = c.ReadU32BE<NoError>();
        if (!v.TryGetValue(out Parsed<uint> ok)) return v.PropagateNonParsed<TlvValue>();
        return new Parsed<TlvValue>(new TlvU32(ok.Value));
    }
}

public readonly struct FastTlvF64Parser : IParser<TlvValue, NoError>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TlvValue, NoError> Parse(ref ByteCursor c)
    {
        var t = c.TagByte<NoError>(0x02);
        if (!t.IsParsed) return t.PropagateNonParsed<TlvValue>();
        var v = c.ReadF64BE<NoError>();
        if (!v.TryGetValue(out Parsed<double> ok)) return v.PropagateNonParsed<TlvValue>();
        return new Parsed<TlvValue>(new TlvF64(ok.Value));
    }
}

public readonly struct FastTlvBytesParser : IParser<TlvValue, NoError>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TlvValue, NoError> Parse(ref ByteCursor c)
    {
        var t = c.TagByte<NoError>(0x03);
        if (!t.IsParsed) return t.PropagateNonParsed<TlvValue>();
        var v = c.ReadLengthPrefixedBytes<NoError>(LengthPrefix.U16);
        if (!v.TryGetValue(out Parsed<ByteSlice> ok)) return v.PropagateNonParsed<TlvValue>();
        return new Parsed<TlvValue>(new TlvBytes(ok.Value));
    }
}

public readonly struct TryTlvU32Parser : ITryParser<TlvValue>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryParse(ref ByteCursor c, out TlvValue value)
    {
        var t = c.TryTagByte(0x01, out _);
        if (!t.IsParsed) { Unsafe.SkipInit(out value); return t; }
        var v = c.TryReadU32BE(out uint u);
        if (!v.IsParsed) { Unsafe.SkipInit(out value); return v; }
        value = new TlvU32(u);
        return FastStatus.Parsed;
    }
}

public readonly struct TryTlvF64Parser : ITryParser<TlvValue>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryParse(ref ByteCursor c, out TlvValue value)
    {
        var t = c.TryTagByte(0x02, out _);
        if (!t.IsParsed) { Unsafe.SkipInit(out value); return t; }
        var v = c.TryReadF64BE(out double d);
        if (!v.IsParsed) { Unsafe.SkipInit(out value); return v; }
        value = new TlvF64(d);
        return FastStatus.Parsed;
    }
}

public readonly struct TryTlvBytesParser : ITryParser<TlvValue>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryParse(ref ByteCursor c, out TlvValue value)
    {
        var t = c.TryTagByte(0x03, out _);
        if (!t.IsParsed) { Unsafe.SkipInit(out value); return t; }
        var v = c.TryReadLengthPrefixedU16BE(out ByteSlice s);
        if (!v.IsParsed) { Unsafe.SkipInit(out value); return v; }
        value = new TlvBytes(s);
        return FastStatus.Parsed;
    }
}

public readonly struct TryTlvDispatchHandler : ITryDispatchHandler<byte, TlvValue>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryParse(byte tag, ref ByteCursor c, out TlvValue value)
    {
        if (tag == 0x01)
        {
            var u = c.TryReadU32BE(out uint u32);
            if (!u.IsParsed) { Unsafe.SkipInit(out value); return u; }
            value = new TlvU32(u32);
            return FastStatus.Parsed;
        }
        if (tag == 0x02)
        {
            var f = c.TryReadF64BE(out double f64);
            if (!f.IsParsed) { Unsafe.SkipInit(out value); return f; }
            value = new TlvF64(f64);
            return FastStatus.Parsed;
        }
        if (tag == 0x03)
        {
            var s = c.TryReadLengthPrefixedU16BE(out ByteSlice slice);
            if (!s.IsParsed) { Unsafe.SkipInit(out value); return s; }
            value = new TlvBytes(slice);
            return FastStatus.Parsed;
        }
        Unsafe.SkipInit(out value);
        return FastStatus.Backtrack;
    }
}

public readonly struct TryTlvFolder : ITryDispatchFolder<byte, ulong>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryFold(byte tag, ref ByteCursor c, ref ulong acc)
    {
        if (tag == 0x01)
        {
            var u = c.TryReadU32BE(out uint u32);
            if (!u.IsParsed) return u;
            acc = unchecked(acc + u32);
            return FastStatus.Parsed;
        }
        if (tag == 0x02)
        {
            var f = c.TryReadF64BE(out double f64);
            if (!f.IsParsed) return f;
            acc = unchecked(acc + BitConverter.DoubleToUInt64Bits(f64));
            return FastStatus.Parsed;
        }
        if (tag == 0x03)
        {
            var s = c.TryReadLengthPrefixedU16BE(out ByteSlice slice);
            if (!s.IsParsed) return s;
            ref byte r = ref Unsafe.Add(ref MemoryMarshal.GetReference(c.Buffer), (nint)(uint)slice.Offset);
            ulong sum = 0;
            nuint i = 0;
            nuint len = (nuint)(uint)slice.Length;
            while (i + 8 <= len)
            {
                sum += (ulong)(Unsafe.Add(ref r, i) + Unsafe.Add(ref r, i + 1) + Unsafe.Add(ref r, i + 2) + Unsafe.Add(ref r, i + 3)
                     + Unsafe.Add(ref r, i + 4) + Unsafe.Add(ref r, i + 5) + Unsafe.Add(ref r, i + 6) + Unsafe.Add(ref r, i + 7));
                i += 8;
            }
            while (i < len) { sum += Unsafe.Add(ref r, i); i++; }
            acc = unchecked(acc + sum);
            return FastStatus.Parsed;
        }
        return FastStatus.Backtrack;
    }
}

[BinaryStruct(Endianness.Big)]
[Magic(0xAA)]
public readonly partial record struct BenchGeneratedPacket(
    [property: AssertValue(7)] byte Kind,
    ushort Port,
    uint Timestamp,
    double Score,
    [property: VarInt] ulong Sequence,
    [property: LengthPrefixed(LengthPrefix.U16)] ByteSlice Payload);

