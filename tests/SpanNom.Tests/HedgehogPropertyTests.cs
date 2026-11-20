using System.Text;
using Hedgehog.Linq;
using SpanNom.Protocols.Dns;
using SpanNom.Protocols.Resp;
using Xunit;
using HGen = Hedgehog.Linq.Gen;
using HRange = Hedgehog.Linq.Range;

namespace SpanNom.Tests;

/// <summary>
/// Integrated-shrinking property-based tests powered by <c>Hedgehog 2.0.4</c> (<c>Hedgehog.Linq</c>).
/// Exercises structured binary grammars (recursive Redis RESP2 ASTs, DNS wire packets with compression)
/// and algebraic combinator laws (<c>Checkpoint</c>, <c>Cut</c>, <c>LengthValue</c>, <c>BitCursor</c>).
/// </summary>
public sealed class HedgehogPropertyTests
{
    private abstract record RespModel;
    private sealed record SimpleModel(string Text) : RespModel;
    private sealed record ErrorModel(string Text) : RespModel;
    private sealed record IntModel(long Value) : RespModel;
    private sealed record BulkModel(byte[] Bytes) : RespModel;
    private sealed record NullModel : RespModel;
    private sealed record ArrayModel(RespModel[] Items) : RespModel;

    [Fact]
    public void Hedgehog_Resp2_RecursiveAst_Roundtrip()
    {
        var alphaChar = HGen.Char('a', 'z');
        var tokenGen = alphaChar.String(HRange.LinearInt32(0, 16));
        var bytesGen = HGen.Byte(HRange.Constant((byte)0, (byte)255)).Array(HRange.LinearInt32(0, 24));

        Hedgehog.Gen<RespModel> leafGen = HGen.Choice(
        [
            tokenGen.Select(static s => (RespModel)new SimpleModel(s)),
            tokenGen.Select(static s => (RespModel)new ErrorModel(s)),
            HGen.Int64(HRange.LinearInt64(-1_000_000L, 1_000_000L)).Select(static n => (RespModel)new IntModel(n)),
            bytesGen.Select(static b => (RespModel)new BulkModel(b)),
            HGen.Constant<RespModel>(new NullModel()),
        ]);

        Hedgehog.Gen<RespModel> nestedGen = HGen.Choice(
        [
            leafGen,
            leafGen.Array(HRange.LinearInt32(0, 6)).Select(static arr => (RespModel)new ArrayModel(arr)),
        ]);

        var prop =
            from model in nestedGen.ForAll()
            select VerifyRespRoundtrip(model);

        prop.Check();
    }

    [Fact]
    public void Hedgehog_Dns_Packet_WithCompression_Roundtrip()
    {
        var labelGen = HGen.Char('a', 'z').String(HRange.LinearInt32(1, 10));
        var domainGen = labelGen.Array(HRange.LinearInt32(1, 4)).Select(static ls => string.Join('.', ls));
        var ipGen = HGen.UInt32(HRange.Constant(0u, uint.MaxValue));
        var txIdGen = HGen.UInt16(HRange.Constant((ushort)0, ushort.MaxValue));
        var ttlGen = HGen.UInt32(HRange.LinearUInt32(0u, 86_400u));

        var prop =
            from txId in txIdGen.ForAll()
            from domain in domainGen.ForAll()
            from ip in ipGen.ForAll()
            from ttl in ttlGen.ForAll()
            select VerifyDnsRoundtrip(txId, domain, ip, ttl);

        prop.Check();
    }

    [Fact]
    public void Hedgehog_CheckpointRollback_Invariant_OnBacktrack()
    {
        var bytesGen = HGen.Byte(HRange.Constant((byte)0, (byte)255)).Array(HRange.LinearInt32(1, 32));
        var endianGen = HGen.Bool.Select(static b => b ? Endianness.Big : Endianness.Little);

        var prop =
            from data in bytesGen.ForAll()
            from endian in endianGen.ForAll()
            select VerifyRollbackInvariant(data, endian);

        prop.Check();
    }

    [Fact]
    public void Hedgehog_Cut_NeverBacktracks_InAlt()
    {
        var payloadGen = HGen.Byte(HRange.Constant((byte)0, (byte)255)).Array(HRange.LinearInt32(0, 12));

        var prop =
            from tail in payloadGen.ForAll()
            select VerifyCutLaw(tail);

        prop.Check();
    }

    [Fact]
    public void Hedgehog_LengthValue_RejectsUnconsumedTrailingBytes()
    {
        var valGen = HGen.UInt16(HRange.Constant((ushort)0, ushort.MaxValue));
        var trailingGen = HGen.Byte(HRange.Constant((byte)0, (byte)255)).Array(HRange.LinearInt32(1, 8));

        var prop =
            from value in valGen.ForAll()
            from trailing in trailingGen.ForAll()
            select VerifyLengthValueFraming(value, trailing);

        prop.Check();
    }

    [Fact]
    public void Hedgehog_BitCursor_SignedTwoComplement_Roundtrip()
    {
        var widthGen = HGen.Int32(HRange.Constant(2, 16));
        var rawGen = HGen.Int32(HRange.LinearInt32(-32768, 32767));

        var prop =
            from width in widthGen.ForAll()
            from raw in rawGen.ForAll()
            select VerifySignedBits(width, raw);

        prop.Check();
    }

    private static bool VerifyRespRoundtrip(RespModel model)
    {
        List<byte> wire = [];
        SerializeResp(model, wire);
        byte[] bytes = wire.ToArray();

        var c = bytes.AsCursor();
        var res = RespParser.ParseValue(ref c);
        if (!res.TryGetValue(out Parsed<RespValue> ok) || !c.IsAtEnd)
        {
            return false;
        }

        return RespEquals(model, ok.Value, bytes);
    }

    private static void SerializeResp(RespModel model, List<byte> dst)
    {
        switch (model)
        {
            case SimpleModel(var s):
                dst.Add((byte)'+');
                dst.AddRange(Encoding.UTF8.GetBytes(s));
                dst.Add((byte)'\r'); dst.Add((byte)'\n');
                break;
            case ErrorModel(var s):
                dst.Add((byte)'-');
                dst.AddRange(Encoding.UTF8.GetBytes(s));
                dst.Add((byte)'\r'); dst.Add((byte)'\n');
                break;
            case IntModel(var n):
                dst.Add((byte)':');
                dst.AddRange(Encoding.ASCII.GetBytes(n.ToString()));
                dst.Add((byte)'\r'); dst.Add((byte)'\n');
                break;
            case BulkModel(var b):
                dst.Add((byte)'$');
                dst.AddRange(Encoding.ASCII.GetBytes(b.Length.ToString()));
                dst.Add((byte)'\r'); dst.Add((byte)'\n');
                dst.AddRange(b);
                dst.Add((byte)'\r'); dst.Add((byte)'\n');
                break;
            case NullModel:
                dst.AddRange("$-1\r\n"u8);
                break;
            case ArrayModel(var items):
                dst.Add((byte)'*');
                dst.AddRange(Encoding.ASCII.GetBytes(items.Length.ToString()));
                dst.Add((byte)'\r'); dst.Add((byte)'\n');
                foreach (var item in items) SerializeResp(item, dst);
                break;
        }
    }

    private static bool RespEquals(RespModel expected, RespValue actual, ReadOnlySpan<byte> wire) =>
        (expected, actual) switch
        {
            (SimpleModel(var e), RespSimpleString(var a)) => e == a.AsUtf8String(wire),
            (ErrorModel(var e), RespError(var a)) => e == a.AsUtf8String(wire),
            (IntModel(var e), RespInteger(var a)) => e == a,
            (BulkModel(var e), RespBulkString(var a)) => e.AsSpan().SequenceEqual(wire[a]),
            (NullModel, RespNull) => true,
            (ArrayModel(var eItems), RespArray(var aItems)) when eItems.Length == aItems.Count =>
                AllItemsEqual(eItems, aItems, wire),
            _ => false,
        };

    private static bool AllItemsEqual(RespModel[] expected, IReadOnlyList<RespValue> actual, ReadOnlySpan<byte> wire)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            if (!RespEquals(expected[i], actual[i], wire)) return false;
        }

        return true;
    }

    private static bool VerifyDnsRoundtrip(ushort txId, string domain, uint ip, uint ttl)
    {
        List<byte> pkt =
        [
            (byte)(txId >> 8), (byte)(txId & 0xFF),
            0x81, 0x80, // Response, RD=1, RA=1
            0x00, 0x01, // QD=1
            0x00, 0x01, // AN=1
            0x00, 0x00,
            0x00, 0x00,
        ];

        foreach (var label in domain.Split('.'))
        {
            pkt.Add((byte)label.Length);
            pkt.AddRange(Encoding.ASCII.GetBytes(label));
        }

        pkt.Add(0); // Root label
        pkt.AddRange([0x00, 0x01, 0x00, 0x01]); // QTYPE=A, QCLASS=IN

        // Answer uses compression pointer 0xC0 0x0C back to Question name at offset 12
        pkt.AddRange([0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01]);
        pkt.Add((byte)(ttl >> 24));
        pkt.Add((byte)(ttl >> 16));
        pkt.Add((byte)(ttl >> 8));
        pkt.Add((byte)ttl);
        pkt.AddRange([0x00, 0x04]);
        pkt.Add((byte)(ip >> 24));
        pkt.Add((byte)(ip >> 16));
        pkt.Add((byte)(ip >> 8));
        pkt.Add((byte)ip);

        var c = pkt.ToArray().AsCursor();
        var res = DnsParser.ParsePacket(ref c);
        if (!res.TryGetValue(out Parsed<DnsPacket> ok) || !c.IsAtEnd) return false;

        var parsed = ok.Value;
        return parsed.Header.TransactionId == txId
            && parsed.Header.Flags.IsResponse
            && parsed.Questions.Count == 1
            && parsed.Questions[0].Name == domain
            && parsed.Answers.Count == 1
            && parsed.Answers[0].Name == domain
            && parsed.Answers[0].Ttl == ttl
            && parsed.Answers[0].Data is ARecord(var parsedIp) && parsedIp == ip;
    }

    private static bool VerifyRollbackInvariant(byte[] data, Endianness endian)
    {
        var c = data.AsCursor(endian);
        int startPos = c.Position;

        // A failing Pair / ScopedEndian / Verify must ALWAYS restore both Position and Endianness!
        var res = Parse.ScopedEndian(
            ref c,
            endian == Endianness.Big ? Endianness.Little : Endianness.Big,
            static (ref ByteCursor x) => Parse.Verify(
                ref x,
                static (ref ByteCursor y) => y.ReadU8(),
                static _ => false));

        return res.IsBacktrack && c.Position == startPos && c.Endianness == endian;
    }

    private static bool VerifyCutLaw(byte[] tail)
    {
        byte[] input = [0xAA, .. tail];
        var c = input.AsCursor();
        bool fallbackInvoked = false;

        var res = Parse.Alt<int>(
            ref c,
            static (ref ByteCursor x) => Parse.Preceded(
                ref x,
                static (ref ByteCursor a) => a.TagByte(0xAA),
                static (ref ByteCursor b) => ParseResult.Backtrack<int>(b.Position, ErrorKind.Verify).Cut("committed")),
            (ref ByteCursor x) =>
            {
                fallbackInvoked = true;
                return new Parsed<int>(999);
            });

        return res.IsFailure && !fallbackInvoked && c.Position == 0;
    }

    private static bool VerifyLengthValueFraming(ushort value, byte[] trailing)
    {
        // Exact frame succeeds
        byte[] exact = [2, (byte)(value >> 8), (byte)(value & 0xFF)];
        var cExact = exact.AsCursor();
        var okRes = Parse.LengthValue(
            ref cExact,
            static (ref ByteCursor x) => x.ReadU8().Map(static b => (int)b),
            static (ref ByteCursor sub) => sub.ReadU16BE(),
            requireFullConsumption: true);

        if (!okRes.TryGetValue(out Parsed<ushort> ok) || ok.Value != value || !cExact.IsAtEnd)
            return false;

        // Frame with unconsumed trailing bytes must backtrack and restore cursor
        byte[] extra = [(byte)(2 + trailing.Length), (byte)(value >> 8), (byte)(value & 0xFF), .. trailing];
        var cExtra = extra.AsCursor();
        var badRes = Parse.LengthValue(
            ref cExtra,
            static (ref ByteCursor x) => x.ReadU8().Map(static b => (int)b),
            static (ref ByteCursor sub) => sub.ReadU16BE(),
            requireFullConsumption: true);

        return badRes.TryGetValue(out Backtrack bt) && bt.Error.Kind == ErrorKind.LengthValue && cExtra.Position == 0;
    }

    private static bool VerifySignedBits(int width, int raw)
    {
        int min = -(1 << (width - 1));
        int max = (1 << (width - 1)) - 1;
        int clamped = Math.Clamp(raw, min, max);
        uint masked = (uint)clamped & ((1u << width) - 1u);

        // Pack into 3 bytes starting at a 3-bit non-byte-aligned offset!
        byte[] buf = new byte[3];
        int bitPos = 3;
        for (int b = width - 1; b >= 0; b--)
        {
            if (((masked >> b) & 1) != 0)
            {
                buf[bitPos >> 3] |= (byte)(1 << (7 - (bitPos & 7)));
            }

            bitPos++;
        }

        var bits = new BitCursor(buf);
        _ = bits.SkipBits(3);
        var cp = bits.Save();
        int firstRead = bits.ReadI32(width).Unwrap();
        bits.Reset(cp);
        int secondRead = bits.ReadI32(width).Unwrap();
        return firstRead == clamped && secondRead == clamped;
    }
}
