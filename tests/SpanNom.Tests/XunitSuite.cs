using Xunit;

namespace SpanNom.Tests;

/// <summary>
/// xUnit bridge exposing every existing unit, streaming, protocol, and zero-allocation test
/// as an individual xUnit <see cref="FactAttribute"/> so both <c>dotnet test</c> and
/// <c>dotnet stryker</c> track per-test coverage and kill mutants accurately.
/// </summary>
public sealed class UnitTestBridge
{
    [Fact] public void Core_ParseResult_ExhaustiveUnion() => RunNamed("Core/ParseResult exhaustive C# 15 union switch & conversions");
    [Fact] public void Core_NeededAndOption() => RunNamed("Core/Needed & Option non-boxing unions");
    [Fact] public void Core_ByteSlice_ExtensionIndexer() => RunNamed("Core/ByteSlice & C# 15 extension indexer");
    [Fact] public void Core_ParseError_Diagnostics() => RunNamed("Core/ParseError hex-dump diagnostics & breadcrumbs");

    [Fact] public void ByteCursor_FixedWidthPrimitives() => RunNamed("ByteCursor/Fixed-width integers & floats (BE & LE)");
    [Fact] public void ByteCursor_VarInts() => RunNamed("ByteCursor/LEB128, ZigZag, and QUIC RFC 9000 varints");
    [Fact] public void ByteCursor_TagStreamingSemantics() => RunNamed("ByteCursor/Tag complete vs streaming early mismatch");
    [Fact] public void ByteCursor_SimdAndFraming() => RunNamed("ByteCursor/SIMD TakeWhile, TakeUntil, CString, CRLF, Align");
    [Fact] public void ByteCursor_Utf8AndBlittable() => RunNamed("ByteCursor/ReadUtf8String validation & ReadBlittable zero-copy");

    [Fact] public void BitCursor_CrossByteBitfields() => RunNamed("BitCursor/Cross-byte MSB bitfields & signed extension");
    [Fact] public void BitCursor_ByteCursorBridgeAndStreaming() => RunNamed("BitCursor/ByteCursor.ParseBits bridge & streaming Incomplete");

    [Fact] public void Combinators_AltAndCut() => RunNamed("Combinators/Alt backtracking vs Cut commit semantics");
    [Fact] public void Combinators_CoreCombinators() => RunNamed("Combinators/Opt, Peek, Recognize, WithSlice, Delimited, Pair");
    [Fact] public void Combinators_ScopedEndianAndLengthValue() => RunNamed("Combinators/ScopedEndian & LengthValue bounded sub-cursor");
    [Fact] public void Combinators_RepetitionCombinators() => RunNamed("Combinators/Count, Many0, FoldMany0, RepeatInto, Separated0");
    [Fact] public void Combinators_StructParsers() => RunNamed("Combinators/Monomorphized StructParsers pipelines");

    [Fact] public void Streaming_WebSocketTrickle() => RunNamed("Streaming/Byte-by-byte WebSocket frame trickle decode");
    [Fact] public void Streaming_PngChunkTrickle() => RunNamed("Streaming/Byte-by-byte PNG chunk stream decode");
    [Fact] public void Streaming_RespTrickle() => RunNamed("Streaming/Byte-by-byte Redis RESP array trickle decode");

    [Fact] public void Protocols_PngDocument() => RunNamed("Protocols/PNG full document + CRC-32 validation + corruption detection");
    [Fact] public void Protocols_WebSocketFrames() => RunNamed("Protocols/WebSocket RFC 6455 frames + unmasking + RFC violation checks");
    [Fact] public void Protocols_Resp2() => RunNamed("Protocols/Redis RESP2 closed hierarchy + committed Cut errors");
    [Fact] public void Protocols_DnsPacket() => RunNamed("Protocols/DNS RFC 1035 flags, pointer compression, CNAME/A/TXT & cycle guard");

    [Fact] public void ZeroAlloc_StructParsers() => RunNamed("ZeroAlloc/Non-boxing ParseResult<T> + StructParsers (0 bytes)");
    [Fact] public void ZeroAlloc_Repetition() => RunNamed("ZeroAlloc/FoldMany0 + RepeatInto stackalloc (0 bytes)");
    [Fact] public void ZeroAlloc_WebSocketFrameParser() => RunNamed("ZeroAlloc/Full RFC 6455 WebSocketFrameParser + UnmaskInto (0 bytes)");

    private static readonly Dictionary<string, Action> Tests = BuildLookup();

    private static Dictionary<string, Action> BuildLookup()
    {
        var suite = new TestSuite();
        CoreUnionTests.Register(suite);
        ByteCursorTests.Register(suite);
        BitCursorTests.Register(suite);
        CombinatorTests.Register(suite);
        StreamingTests.Register(suite);
        ProtocolTests.Register(suite);
        ZeroAllocationTests.Register(suite);
        return suite.ToDictionary();
    }

    private static void RunNamed(string name) => Tests[name]();
}
