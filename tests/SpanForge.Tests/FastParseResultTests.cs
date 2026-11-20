using System.Runtime.CompilerServices;
using System.Text;
using SpanForge.Protocols.Png;
using Xunit;

namespace SpanForge.Tests;

public sealed class FastParseResultTests
{
    [Fact]
    public void ParseResult_Fast_Layout_IsRegisterSized_And_GcFree()
    {
        // Zero GC-tracked references in register-sized fast-path results & zero-boxing PngChunk!
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ParseResult<byte, NoError>>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ParseResult<ushort, NoError>>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ParseResult<ulong, NoError>>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ParseResult<ByteSlice, KindError>>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ParseResult<uint, CompactError>>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<PngChunk>());

        // Small results fit in 1-2 x86-64 registers (<= 8 bytes / <= 16 bytes)
        Assert.True(Unsafe.SizeOf<ParseResult<byte, NoError>>() <= 8);
        Assert.True(Unsafe.SizeOf<ParseResult<ushort, NoError>>() <= 8);
        Assert.True(Unsafe.SizeOf<ParseResult<ulong, NoError>>() <= 16);
        Assert.True(Unsafe.SizeOf<ParseResult<double, KindError>>() <= 16);
        Assert.True(Unsafe.SizeOf<ParseResult<ByteSlice, KindError>>() <= 16);
    }

    [Fact]
    public void ParseResult_Fast_ExhaustiveUnion_And_ErrorConversions()
    {
        var ok = FastParseResult.Ok<int, CompactError>(42);
        var incExact = FastParseResult.Need<int, CompactError>(5);
        var incHuge = FastParseResult.Need<int, CompactError>(int.MaxValue);
        var incUnk = FastParseResult.NeedUnknown<int, CompactError>();
        var bt = FastParseResult.Backtrack<int, CompactError>(7, ErrorKind.Tag);
        var fail = FastParseResult.Fail<int, CompactError>(9, ErrorKind.Protocol);
        ParseResult<int, CompactError> uninit = default;

        Assert.True(ok.HasValue && ok.IsParsed && !ok.IsError && !ok.IsIncomplete);
        Assert.True(incExact.HasValue && incExact.IsIncomplete && !incExact.IsParsed);
        Assert.True(incUnk.HasValue && incUnk.IsIncomplete);
        Assert.True(bt.HasValue && bt.IsBacktrack && bt.IsError && !bt.IsFailure);
        Assert.True(fail.HasValue && fail.IsFailure && fail.IsError && !fail.IsBacktrack);
        Assert.False(uninit.HasValue);
        Assert.True(uninit.Value is null);
        Assert.Equal("ParseResult(Uninitialized)", uninit.ToString());

        Assert.Equal(42, ok.Unwrap());
        Assert.Equal("Parsed(42)", ok.ToString());
        Assert.Equal("Incomplete(Needed(5 bytes))", incExact.ToString());
        Assert.Equal("Incomplete(Needed(Unknown))", incUnk.ToString());
        Assert.Contains("Tag at byte offset 7", bt.ToString());
        Assert.Contains("Protocol at byte offset 9", fail.ToString());

        Assert.True(incHuge.TryGetValue(out Incomplete hugeInc) && hugeInc.Needed == Needed.Bytes(0x0FFF_FFFF));
        Assert.Equal(new Parsed<int>(42), ok.Value);
        Assert.Equal(Incomplete.AtLeast(5), incExact.Value);
        Assert.Equal(new Backtrack<CompactError>(new CompactError(7, ErrorKind.Tag)), bt.Value);
        Assert.Equal(new Failure<CompactError>(new CompactError(9, ErrorKind.Protocol)), fail.Value);

        // Cut escalates Backtrack -> Failure and leaves Parsed/Incomplete/Failure untouched
        Assert.True(bt.Cut().TryGetValue(out Failure<CompactError> cutF) && cutF.Error == new CompactError(7, ErrorKind.Tag));
        Assert.True(ok.Cut().IsParsed);
        Assert.True(incExact.Cut().IsIncomplete);
        Assert.True(fail.Cut().IsFailure);

        // Map & stateful Map & Verify
        Assert.Equal(84, ok.Map(static x => x * 2).Unwrap());
        Assert.Equal(52, ok.Map(10, static (s, x) => x + s).Unwrap());
        Assert.True(bt.Map(static x => x * 2).IsBacktrack);
        Assert.True(bt.Map(10, static (s, x) => x + s).IsBacktrack);
        Assert.True(ok.Verify(static x => x > 0, 3).IsParsed);
        Assert.True(ok.Verify(static x => x < 0, 3).TryGetValue(out Backtrack<CompactError> verBt) && verBt.Error == new CompactError(3, ErrorKind.Verify));
        Assert.True(bt.Verify(static _ => false, 3).TryGetValue(out Backtrack<CompactError> origBt) && origBt.Error.Kind == ErrorKind.Tag);

        // ToParseResult materializes rich ParseResult<T>
        Assert.Equal(42, ok.ToParseResult().Unwrap());
        Assert.True(incExact.ToParseResult().TryGetValue(out Incomplete richInc) && richInc.Needed == Needed.Bytes(5));
        Assert.True(bt.ToParseResult().TryGetValue(out Backtrack richBt) && richBt.Error.Offset == 7 && richBt.Error.Kind == ErrorKind.Tag);
        Assert.True(fail.ToParseResult().TryGetValue(out Failure richFail) && richFail.Error.Offset == 9 && richFail.Error.Kind == ErrorKind.Protocol);
        Assert.False(uninit.ToParseResult().HasValue);

        // NoError & KindError conversions and ToString
        Assert.Equal("NoError", default(NoError).ToString());
        Assert.Equal(new ParseError(0, ErrorKind.None), NoError.Create(12, ErrorKind.Tag).ToParseError());
        Assert.Equal("Verify", new KindError(ErrorKind.Verify).ToString());
        Assert.Equal(new ParseError(0, ErrorKind.Verify), KindError.Create(12, ErrorKind.Verify).ToParseError());

        // Unwrap throws InvalidOperationException on non-parsed cases
        Xunit.Assert.Throws<InvalidOperationException>(() => incExact.Unwrap());
        Xunit.Assert.Throws<InvalidOperationException>(() => bt.Unwrap());
        Xunit.Assert.Throws<InvalidOperationException>(() => fail.Unwrap());
    }

    [Fact]
    public void ByteCursor_Fast_AllReaders_And_ErrorPaths()
    {
        byte[] buf =
        [
            0x7F,                                           // u8
            0x12, 0x34,                                     // u16 BE
            0x34, 0x12,                                     // u16 LE
            0x11, 0x22, 0x33, 0x44,                         // u32 BE
            0x44, 0x33, 0x22, 0x11,                         // u32 LE
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, // u64 BE
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01, // u64 LE
            0x40, 0x09, 0x21, 0xFB, 0x54, 0x44, 0x2D, 0x18, // f64 BE (~pi)
            0x18, 0x2D, 0x44, 0x54, 0xFB, 0x21, 0x09, 0x40, // f64 LE (~pi)
            (byte)'P', (byte)'N', (byte)'G',                // tag
            0x2A,                                           // varint 1-byte (42)
            0xAC, 0x02,                                     // varint multi-byte (300)
            0x02, (byte)'h', (byte)'i',                     // u8 length-prefixed
            0x00, 0x02, (byte)'o', (byte)'k',               // u16 length-prefixed
            0x00, 0x00, 0x00, 0x01, (byte)'!',              // u32 length-prefixed
            0x03, (byte)'a', (byte)'b', (byte)'c',          // varint length-prefixed
        ];

        var c = buf.AsCursor();
        Assert.Equal((byte)0x7F, c.ReadU8<KindError>().Unwrap());
        Assert.Equal((ushort)0x1234, c.ReadU16<KindError>().Unwrap());
        c.Endianness = Endianness.Little;
        Assert.Equal((ushort)0x1234, c.ReadU16<KindError>().Unwrap());
        c.Endianness = Endianness.Big;
        Assert.Equal(0x11223344u, c.ReadU32<KindError>().Unwrap());
        c.Endianness = Endianness.Little;
        Assert.Equal(0x11223344u, c.ReadU32<KindError>().Unwrap());
        c.Endianness = Endianness.Big;
        Assert.Equal(0x0102030405060708UL, c.ReadU64<KindError>().Unwrap());
        c.Endianness = Endianness.Little;
        Assert.Equal(0x0102030405060708UL, c.ReadU64<KindError>().Unwrap());
        c.Endianness = Endianness.Big;
        Xunit.Assert.Equal(Math.PI, c.ReadF64<KindError>().Unwrap(), 12);
        c.Endianness = Endianness.Little;
        Xunit.Assert.Equal(Math.PI, c.ReadF64<KindError>().Unwrap(), 12);
        c.Endianness = Endianness.Big;

        Assert.Equal("PNG", c.Tag<KindError>("PNG"u8).Unwrap().AsUtf8String(buf));
        Assert.Equal(42UL, c.ReadVarUInt64<KindError>().Unwrap());
        Assert.Equal(300UL, c.ReadVarUInt64<KindError>().Unwrap());
        Assert.Equal("hi", c.ReadLengthPrefixedBytes<KindError>(LengthPrefix.U8).Unwrap().AsUtf8String(buf));
        Assert.Equal("ok", c.ReadLengthPrefixedBytes<KindError>(LengthPrefix.U16).Unwrap().AsUtf8String(buf));
        Assert.Equal("!", c.ReadLengthPrefixedBytes<KindError>(LengthPrefix.U32).Unwrap().AsUtf8String(buf));
        Assert.Equal("abc", c.ReadLengthPrefixedBytes<KindError>(LengthPrefix.VarUInt64).Unwrap().AsUtf8String(buf));
        Assert.True(c.IsAtEnd);

        // Streaming incomplete & early mismatch & error paths
        ReadOnlySpan<byte> partial = "PN"u8;
        var cs = partial.AsCursor(mode: InputMode.Streaming);
        Assert.True(cs.Tag<KindError>("PNG"u8).TryGetValue(out Incomplete tagInc) && tagInc.Needed == Needed.Bytes(1));
        Assert.True(cs.Tag<KindError>("PXG"u8).IsBacktrack);

        var cc = partial.AsCursor(mode: InputMode.Complete);
        Assert.True(cc.Tag<KindError>("PNG"u8).IsBacktrack);
        Assert.True(cc.Tag<KindError>("XX"u8).IsBacktrack);
        Assert.True(cc.Take<KindError>(-1).IsBacktrack);
        Assert.True(cc.Take<KindError>(10).IsBacktrack);
        Assert.True(cc.ReadLengthPrefixedBytes<KindError>((LengthPrefix)99).IsBacktrack);

        // Varint overflow (> 64 bits) and truncated varint in Complete vs Streaming
        ReadOnlySpan<byte> overflow10 = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02];
        var cov = overflow10.AsCursor();
        Assert.True(cov.ReadVarUInt64<KindError>().TryGetValue(out Backtrack<KindError> ovErr) && ovErr.Error.Kind == ErrorKind.VarInt);
        Assert.Equal(0, cov.Position);

        ReadOnlySpan<byte> tooLong = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01];
        var ctl = tooLong.AsCursor();
        Assert.True(ctl.ReadVarUInt64<KindError>().IsBacktrack);

        ReadOnlySpan<byte> truncVar = [0x80];
        var ctComplete = truncVar.AsCursor(mode: InputMode.Complete);
        Assert.True(ctComplete.ReadVarUInt64<KindError>().IsBacktrack);
        var ctStream = truncVar.AsCursor(mode: InputMode.Streaming);
        Assert.True(ctStream.ReadVarUInt64<KindError>().IsIncomplete);

        // Length prefix overflow (> int.MaxValue)
        ReadOnlySpan<byte> hugeLen = [0xFF, 0xFF, 0xFF, 0xFF];
        var ch = hugeLen.AsCursor();
        Assert.True(ch.ReadLengthPrefixedBytes<KindError>(LengthPrefix.U32).IsBacktrack);
        Assert.Equal(0, ch.Position);
    }

    [Fact]
    public void FastParsers_Combinators_And_ZeroAlloc_PngChunk()
    {
        // Exercise FastParsers combinators: Delimited, Preceded, Pair, Opt, Cut, Alt (2-way & 3-way)
        byte[] data = [(byte)'[', 0x01, 0x02, (byte)']', 0x09, 0x00, 0x03, (byte)'x', (byte)'y', (byte)'z'];
        var c = data.AsCursor();

        var delim = FastParsers.Delimited<
            FastParsers.TagByteParser<NoError>,
            FastParsers.U16Parser<NoError>,
            FastParsers.TagByteParser<NoError>,
            byte, ushort, byte, NoError>(
            FastParsers.TagByte<NoError>((byte)'['),
            FastParsers.U16<NoError>(),
            FastParsers.TagByte<NoError>((byte)']'));
        Assert.Equal((ushort)0x0102, delim.Parse(ref c).Unwrap());

        var optMiss = FastParsers.Opt<FastParsers.TagByteParser<NoError>, byte, NoError>(FastParsers.TagByte<NoError>(0xFF));
        Assert.True(optMiss.Parse(ref c).Unwrap().IsNone);

        var prec = FastParsers.Preceded<FastParsers.TagByteParser<NoError>, FastParsers.LengthPrefixedParser<NoError>, byte, ByteSlice, NoError>(
            FastParsers.TagByte<NoError>(0x09),
            FastParsers.LengthPrefixed<NoError>(LengthPrefix.U16));
        Assert.Equal("xyz", prec.Parse(ref c).Unwrap().AsUtf8String(data));

        // Alt + Cut semantics
        ReadOnlySpan<byte> altData = [0x02, 0x00, 0x00, 0x00, 0x07];
        var ca = altData.AsCursor();
        var alt2 = FastParsers.Alt<
            FastParsers.PrecededParser<FastParsers.TagByteParser<KindError>, FastParsers.U32Parser<KindError>, byte, uint, KindError>,
            FastParsers.PrecededParser<FastParsers.TagByteParser<KindError>, FastParsers.U32Parser<KindError>, byte, uint, KindError>,
            uint, KindError>(
            FastParsers.Preceded<FastParsers.TagByteParser<KindError>, FastParsers.U32Parser<KindError>, byte, uint, KindError>(
                FastParsers.TagByte<KindError>(0x01), FastParsers.U32<KindError>()),
            FastParsers.Preceded<FastParsers.TagByteParser<KindError>, FastParsers.U32Parser<KindError>, byte, uint, KindError>(
                FastParsers.TagByte<KindError>(0x02), FastParsers.U32<KindError>()));
        Assert.Equal(7u, alt2.Parse(ref ca).Unwrap());

        ReadOnlySpan<byte> cutData = [0x01];
        var cc = cutData.AsCursor();
        var cutP = FastParsers.Cut<FastParsers.TagByteParser<KindError>, byte, KindError>(FastParsers.TagByte<KindError>(0x99));
        Assert.True(cutP.Parse(ref cc).IsFailure);

        // Pair parser
        ReadOnlySpan<byte> pairData = [0x0A, 0x0B];
        var cp = pairData.AsCursor();
        var pair = FastParsers.Pair<FastParsers.U8Parser<NoError>, FastParsers.U8Parser<NoError>, byte, byte, NoError>(
            FastParsers.U8<NoError>(), FastParsers.U8<NoError>());
        Assert.Equal(((byte)0x0A, (byte)0x0B), pair.Parse(ref cp).Unwrap());
    }

    [Fact]
    public void PngParser_SlicingBy8_Crc32_MatchesReference_AcrossAllLengths()
    {
        // IEEE 802.3 standard check value for ASCII "123456789" is 0xCBF43926
        Assert.Equal(0xCBF4_3926u, PngParser.ComputeCrc32("123456789"u8));
        Assert.Equal(0x0000_0000u, PngParser.ComputeCrc32([]));

        // Verify exact equivalence against bit-by-bit reference across lengths 0..65
        byte[] sample = new byte[65];
        for (int i = 0; i < sample.Length; i++) sample[i] = (byte)(i * 37 + 13);

        for (int len = 0; len <= sample.Length; len++)
        {
            ReadOnlySpan<byte> slice = sample.AsSpan(0, len);
            Assert.Equal(ReferenceCrc32(slice), PngParser.ComputeCrc32(slice));
        }

        static uint ReferenceCrc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFF_FFFFu;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB8_8320u ^ (crc >> 1) : crc >> 1;
                }
            }

            return ~crc;
        }
    }

    [Fact]
    public void PngParser_ZeroBoxing_PngChunk_AllocatesZeroBytes()
    {
        byte[] ihdrPayload = [0, 0, 0x01, 0x00, 0, 0, 0x00, 0x80, 8, 2, 0, 0, 0];
        byte[] png =
        [
            .. PngParser.Signature,
            .. PngParser.BuildChunk("IHDR"u8, ihdrPayload),
            .. PngParser.BuildChunk("PLTE"u8, [255, 0, 0, 0, 255, 0]),
            .. PngParser.BuildChunk("tEXt"u8, "Author\0SpanForge"u8),
            .. PngParser.BuildChunk("IDAT"u8, [1, 2, 3, 4, 5, 6, 7, 8]),
            .. PngParser.BuildChunk("gAMA"u8, [0, 0, 0xB1, 0x8F]),
            .. PngParser.BuildChunk("IEND"u8, []),
        ];

        // Warmup JIT
        _ = RunPngLoop(png, 100);

        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong checksum = RunPngLoop(png, 2_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(checksum > 0);
        Assert.Equal(0L, allocated);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static ulong RunPngLoop(byte[] buf, int iterations)
        {
            ulong acc = 0;
            ReadOnlySpan<byte> span = buf;
            for (int i = 0; i < iterations; i++)
            {
                var c = span.AsCursor();
                _ = PngParser.ParseSignature(ref c).Unwrap();
                while (true)
                {
                    var chunk = PngParser.ParseChunk(ref c).Unwrap();
                    switch (chunk)
                    {
                        case IhdrChunk h: acc += h.Width + h.Height; break;
                        case PlteChunk p: acc += (ulong)p.EntryCount; break;
                        case TextChunk(var kw, var txt): acc += (ulong)(kw.Length + txt.Length); break;
                        case IdatChunk(var d): acc += (ulong)d.Length + span[d][0]; break;
                        case UnknownChunk u: acc += u.TypeCode; break;
                        case IendChunk: acc += 1; goto done;
                    }
                }
                done:;
            }

            return acc;
        }
    }
}
