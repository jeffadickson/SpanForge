using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using SpanForge.Pipelines;
using SpanForge.Protocols.Http2;
using SpanForge.Protocols.WebSocket;
using Xunit;

namespace SpanForge.Tests;

public sealed class PipelinesTests
{
    private sealed class ChunkedSegment : ReadOnlySequenceSegment<byte>
    {
        public ChunkedSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public ChunkedSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new ChunkedSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private static ReadOnlySequence<byte> BuildMultiSegment(byte[] data, int chunkSize)
    {
        if (data.Length == 0) return ReadOnlySequence<byte>.Empty;
        var first = new ChunkedSegment(data.AsMemory(0, Math.Min(chunkSize, data.Length)));
        var last = first;
        for (int offset = chunkSize; offset < data.Length; offset += chunkSize)
        {
            int take = Math.Min(chunkSize, data.Length - offset);
            last = last.Append(data.AsMemory(offset, take));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    [Fact]
    public void PipeParseOutcome_ExhaustiveUnion_AllVariants()
    {
        PipeParseOutcome<int> empty = default;
        Assert.False(empty.HasValue);
        Assert.True(empty.Value is null);

        PipeParseOutcome<int> parsed = new PipeParsed<int>(7);
        PipeParseOutcome<int> eof = new PipeEndOfStream();
        PipeParseOutcome<int> inc = new PipeIncompleteAtEof(Needed.Bytes(3));
        PipeParseOutcome<int> fail = new PipeFailed(new ParseError(2, ErrorKind.Protocol, "bad"));

        Assert.True(parsed.HasValue && parsed.IsParsed && parsed.Value is PipeParsed<int>(7));
        Assert.True(eof.HasValue && eof.IsEndOfStream && eof.Value is PipeEndOfStream);
        Assert.True(inc.HasValue && inc.IsIncompleteAtEof && inc.Value is PipeIncompleteAtEof);
        Assert.True(fail.HasValue && fail.IsFailed && fail.Value is PipeFailed);

        static string Describe(PipeParseOutcome<int> o) => o switch
        {
            PipeParsed<int>(var v) => $"ok:{v}",
            PipeEndOfStream => "eof",
            PipeIncompleteAtEof(var n) => $"inc:{n}",
            PipeFailed(var e) => $"fail:{e.Kind}",
        };

        Assert.Equal("ok:7", Describe(parsed));
        Assert.Equal("eof", Describe(eof));
        Assert.True(Describe(inc).StartsWith("inc:", StringComparison.Ordinal));
        Assert.Equal("fail:Protocol", Describe(fail));
    }

    [Fact]
    public void PipeSequenceAdapter_SingleAndMultiSegment_AllOverloads()
    {
        // Two length-prefixed U16 big-endian frames: "Hi" + "SpanForge"
        byte[] wire =
        [
            0x00, 0x02, (byte)'H', (byte)'i',
            0x00, 0x09, (byte)'S', (byte)'p', (byte)'a', (byte)'n', (byte)'F', (byte)'o', (byte)'r', (byte)'g', (byte)'e',
        ];

        // Single-segment projector path
        var single = new ReadOnlySequence<byte>(wire);
        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in single,
            isCompleted: false,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            static (slice, frame) => Encoding.UTF8.GetString(frame[slice]),
            out SequencePosition consumed1,
            out ParseResult<string> r1));
        Assert.Equal("Hi", r1.Unwrap());

        var restSingle = single.Slice(consumed1);
        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in restSingle,
            isCompleted: true,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            static (slice, frame) => Encoding.UTF8.GetString(frame[slice]),
            out SequencePosition consumed2,
            out ParseResult<string> r2));
        Assert.Equal("SpanForge", r2.Unwrap());
        Assert.True(restSingle.Slice(consumed2).IsEmpty);

        // Multi-segment (2 bytes per segment!) spanning boundaries
        var multi = BuildMultiSegment(wire, chunkSize: 2);
        Assert.False(multi.IsSingleSegment);
        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in multi,
            isCompleted: false,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            static (slice, frame) => Encoding.UTF8.GetString(frame[slice]),
            out SequencePosition mConsumed1,
            out ParseResult<string> mr1));
        Assert.Equal("Hi", mr1.Unwrap());

        var restMulti = multi.Slice(mConsumed1);
        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in restMulti,
            isCompleted: true,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            static (slice, frame) => Encoding.UTF8.GetString(frame[slice]),
            out SequencePosition mConsumed2,
            out ParseResult<string> mr2));
        Assert.Equal("SpanForge", mr2.Unwrap());
        Assert.True(restMulti.Slice(mConsumed2).IsEmpty);

        // Monomorphized ITryParser<T> overload over multi-segment sequence
        byte[] delimBytes = [(byte)'[', 0x12, 0x34, (byte)']'];
        var delimMulti = BuildMultiSegment(delimBytes, chunkSize: 1);
        var delimParser = TryParsers.Delimited<TryParsers.TagByteParser, TryParsers.U16BEParser, TryParsers.TagByteParser, byte, ushort, byte>(
            TryParsers.TagByte((byte)'['),
            TryParsers.U16BE,
            TryParsers.TagByte((byte)']'));
        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in delimMulti,
            isCompleted: true,
            delimParser,
            out SequencePosition dConsumed,
            out FastStatus dStatus,
            out ushort dVal));
        Assert.True(dStatus.IsParsed);
        Assert.Equal((ushort)0x1234, dVal);
        Assert.True(delimMulti.Slice(dConsumed).IsEmpty);
    }

    [Fact]
    public async Task PipeParser_StreamsWebSocketAndHttp2Frames_ByteByByte_WithBackpressure()
    {
        // Build 3 masked WebSocket Text frames ("Hello", "Pipe", "World")
        byte[] wsStream =
        [
            .. BuildMaskedTextFrame("Hello"u8, 0x37FA213Du),
            .. BuildMaskedTextFrame("Pipe"u8, 0x11223344u),
            .. BuildMaskedTextFrame("World"u8, 0xAABBCCDDu),
        ];

        var pipe = new Pipe();
        Task writerTask = Task.Run(async () =>
        {
            // Trickle 2 bytes at a time to exercise PipeReader backpressure & Incomplete loops
            for (int i = 0; i < wsStream.Length; i += 2)
            {
                int count = Math.Min(2, wsStream.Length - i);
                wsStream.AsSpan(i, count).CopyTo(pipe.Writer.GetSpan(count));
                pipe.Writer.Advance(count);
                await pipe.Writer.FlushAsync();
            }
            await pipe.Writer.CompleteAsync();
        });

        List<string> messages = [];
        await foreach (var outcome in PipeParser.ReadAllAsync(
            pipe.Reader,
            static (ref ByteCursor c) => WebSocketFrameParser.ParseFrame(ref c),
            static (WebSocketFrame frame, ReadOnlySpan<byte> frameBytes) =>
            {
                if (frame.TryGetValue(out TextFrame tf))
                {
                    Span<byte> unmasked = stackalloc byte[tf.Payload.Length];
                    WebSocketFrameParser.UnmaskInto(frameBytes, tf.Payload, tf.MaskKey, unmasked);
                    return Encoding.UTF8.GetString(unmasked);
                }
                return string.Empty;
            }))
        {
            Assert.True(outcome.TryGetValue(out PipeParsed<string> parsed));
            messages.Add(parsed.Value);
        }

        await writerTask;
        await pipe.Reader.CompleteAsync();
        Assert.Equal(3, messages.Count);
        Assert.Equal("Hello", messages[0]);
        Assert.Equal("Pipe", messages[1]);
        Assert.Equal("World", messages[2]);

        // Also test HTTP/2 frames over PipeParser.ReadAllAsync
        byte[] h2Stream =
        [
            .. Http2FrameParser.BuildFrame(Http2FrameType.Settings, Http2FrameFlags.Ack, 0, []),
            .. Http2FrameParser.BuildFrame(Http2FrameType.Ping, Http2FrameFlags.None, 0, [1, 2, 3, 4, 5, 6, 7, 8]),
            .. Http2FrameParser.BuildFrame(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 1, [0, 0, 0x04, 0x00]),
        ];

        var h2Pipe = new Pipe();
        h2Stream.CopyTo(h2Pipe.Writer.GetSpan(h2Stream.Length));
        h2Pipe.Writer.Advance(h2Stream.Length);
        await h2Pipe.Writer.FlushAsync();
        await h2Pipe.Writer.CompleteAsync();

        List<string> h2Kinds = [];
        await foreach (var outcome in PipeParser.ReadAllAsync(
            h2Pipe.Reader,
            static (ref ByteCursor c) => Http2FrameParser.ParseFrame(ref c)))
        {
            Assert.True(outcome.TryGetValue(out PipeParsed<Http2Frame> ok));
            h2Kinds.Add(ok.Value switch
            {
                Http2SettingsFrame => "SETTINGS",
                Http2PingFrame(_, var opaque) => $"PING:{opaque:X16}",
                Http2WindowUpdateFrame(var sid, var inc) => $"WU:{sid}:{inc}",
                _ => "OTHER",
            });
        }
        await h2Pipe.Reader.CompleteAsync();
        Assert.Equal(3, h2Kinds.Count);
        Assert.Equal("SETTINGS", h2Kinds[0]);
        Assert.Equal("PING:0102030405060708", h2Kinds[1]);
        Assert.Equal("WU:1:1024", h2Kinds[2]);
    }

    [Fact]
    public async Task PipeParser_Handles_EndOfStream_IncompleteAtEof_Malformed_MaxBufferSize_And_TryParser()
    {
        // 1. Empty completed pipe -> PipeEndOfStream
        var emptyPipe = new Pipe();
        await emptyPipe.Writer.CompleteAsync();
        var oEmpty = await PipeParser.ReadOneAsync(emptyPipe.Reader, static (ref ByteCursor c) => c.ReadU32BE());
        Assert.True(oEmpty.IsEndOfStream);
        await emptyPipe.Reader.CompleteAsync();

        // 2. Truncated frame at completed EOF -> PipeFailed (Complete mode on final buffer) or PipeIncompleteAtEof
        var truncPipe = new Pipe();
        byte[] twoBytes = [0x01, 0x02];
        twoBytes.CopyTo(truncPipe.Writer.GetSpan(2));
        truncPipe.Writer.Advance(2);
        await truncPipe.Writer.FlushAsync();
        await truncPipe.Writer.CompleteAsync();
        var oTrunc = await PipeParser.ReadOneAsync(truncPipe.Reader, static (ref ByteCursor c) => c.ReadU32BE());
        Assert.True(oTrunc.IsFailed || oTrunc.IsIncompleteAtEof);
        await truncPipe.Reader.CompleteAsync();

        // 3. DoS maxBufferSize guard (unfinished 100-byte frame when maxBufferSize = 16)
        var dosPipe = new Pipe();
        byte[] hugeHeader = new byte[20];
        hugeHeader[0] = 0x00;
        hugeHeader[1] = 100; // promises 100 bytes, only 18 follow
        hugeHeader.CopyTo(dosPipe.Writer.GetSpan(hugeHeader.Length));
        dosPipe.Writer.Advance(hugeHeader.Length);
        await dosPipe.Writer.FlushAsync(); // NOT completed!
        var oDos = await PipeParser.ReadOneAsync(
            dosPipe.Reader,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            maxBufferSize: 16);
        Assert.True(oDos.TryGetValue(out PipeFailed dosFail) && dosFail.Error.Kind == ErrorKind.LengthValue);
        await dosPipe.Writer.CompleteAsync();
        await dosPipe.Reader.CompleteAsync();

        // 4. Monomorphized ITryParser<T> async ReadOneAsync with generated [BinaryStruct] TryParser
        var tryPipe = new Pipe();
        byte[] delimited = [(byte)'[', 0xAB, 0xCD, (byte)']'];
        delimited.CopyTo(tryPipe.Writer.GetSpan(delimited.Length));
        tryPipe.Writer.Advance(delimited.Length);
        await tryPipe.Writer.FlushAsync();
        await tryPipe.Writer.CompleteAsync();

        var parser = TryParsers.Delimited<TryParsers.TagByteParser, TryParsers.U16BEParser, TryParsers.TagByteParser, byte, ushort, byte>(
            TryParsers.TagByte((byte)'['),
            TryParsers.U16BE,
            TryParsers.TagByte((byte)']'));

        var oTry = await PipeParser.ReadOneAsync<
            TryParsers.DelimitedParser<TryParsers.TagByteParser, TryParsers.U16BEParser, TryParsers.TagByteParser, byte, ushort, byte>,
            ushort>(tryPipe.Reader, parser);
        Assert.True(oTry.TryGetValue(out PipeParsed<ushort> okTry) && okTry.Value == 0xABCD);
        var oTryEof = await PipeParser.ReadOneAsync<
            TryParsers.DelimitedParser<TryParsers.TagByteParser, TryParsers.U16BEParser, TryParsers.TagByteParser, byte, ushort, byte>,
            ushort>(tryPipe.Reader, parser);
        Assert.True(oTryEof.IsEndOfStream);
        await tryPipe.Reader.CompleteAsync();
    }

    private readonly struct AlwaysIncompleteTryParser : ITryParser<uint>
    {
        public FastStatus TryParse(ref ByteCursor cursor, out uint value)
        {
            value = 0;
            return FastStatus.IncompleteBytes(4);
        }
    }

    private readonly struct FailureOrBacktrackTryParser(bool fail) : ITryParser<uint>
    {
        public FastStatus TryParse(ref ByteCursor cursor, out uint value)
        {
            value = 0;
            return fail ? FastStatus.Failure : FastStatus.Backtrack;
        }
    }

    [Fact]
    public async Task PipeParser_MutationKiller_AllBranchesAndGuards()
    {
        // 1. PipeParseOutcome TryGetValue mismatch branches
        PipeParseOutcome<int> parsed = new PipeParsed<int>(99);
        Assert.False(parsed.TryGetValue(out PipeEndOfStream _));
        Assert.False(parsed.TryGetValue(out PipeIncompleteAtEof _));
        Assert.False(parsed.TryGetValue(out PipeFailed _));
        Assert.False(parsed.IsEndOfStream);
        Assert.False(parsed.IsIncompleteAtEof);
        Assert.False(parsed.IsFailed);

        PipeParseOutcome<int> eof = new PipeEndOfStream();
        Assert.False(eof.TryGetValue(out PipeParsed<int> _));
        Assert.True(eof.TryGetValue(out PipeEndOfStream _));

        PipeParseOutcome<int> inc = new PipeIncompleteAtEof(Needed.Bytes(5));
        Assert.True(inc.TryGetValue(out PipeIncompleteAtEof incVal) && incVal.Needed.TryGetValue(out ExactBytes ex5) && ex5.Count == 5);
        Assert.False(inc.TryGetValue(out PipeFailed _));

        PipeParseOutcome<int> fail = new PipeFailed(new ParseError(4, ErrorKind.Tag, "bad"));
        Assert.True(fail.TryGetValue(out PipeFailed fVal) && fVal.Error.Offset == 4 && fVal.Error.Kind == ErrorKind.Tag);

        // 2. PipeSequenceAdapter argument guards & non-parsed paths across all 3 overloads (single & multi segment)
        byte[] fourBytes = [0x00, 0x05, 0xAA, 0xBB]; // U16 len=5, only 2 bytes follow -> Incomplete in Streaming
        var single = new ReadOnlySequence<byte>(fourBytes);
        var multi = BuildMultiSegment(fourBytes, chunkSize: 2);

        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() =>
            PipeSequenceAdapter.TryParseSequence(in single, false, static (ref ByteCursor c) => c.ReadU32BE(), out _, out _, maxBufferSize: 0));
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() =>
            PipeSequenceAdapter.TryParseSequence(in single, false, static (ref ByteCursor c) => c.ReadU32BE(), static (v, _) => v, out _, out _, maxBufferSize: 0));
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() =>
            PipeSequenceAdapter.TryParseSequence<TryParsers.U32BEParser, uint>(in single, false, TryParsers.U32BE, out _, out _, out _, maxBufferSize: 0));

        // Overload 1 (SpanParser<T>): single parsed, single non-parsed, multi parsed, multi non-parsed, multi completed-short -> Backtrack, multi truncated by maxBufferSize -> Incomplete
        Assert.True(PipeSequenceAdapter.TryParseSequence(in single, true, static (ref ByteCursor c) => c.ReadU16BE(), out var c1, out var r1));
        Assert.Equal((ushort)5, r1.Unwrap());
        Assert.Equal(2L, single.Slice(0, c1).Length);

        Assert.False(PipeSequenceAdapter.TryParseSequence(in single, false, static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16), out var c1Fail, out var r1Fail));
        Assert.Equal(single.Start, c1Fail);
        Assert.True(r1Fail.IsIncomplete);

        Assert.True(PipeSequenceAdapter.TryParseSequence(in multi, true, static (ref ByteCursor c) => c.ReadU16BE(), out var cm1, out var rm1));
        Assert.Equal((ushort)5, rm1.Unwrap());
        Assert.Equal(2L, multi.Slice(0, cm1).Length);

        Assert.False(PipeSequenceAdapter.TryParseSequence(in multi, false, static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16), out var cmFail, out var rmFail));
        Assert.Equal(multi.Start, cmFail);
        Assert.True(rmFail.IsIncomplete);

        Assert.False(PipeSequenceAdapter.TryParseSequence(in multi, isCompleted: true, static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16), out var cmCompFail, out var rmCompFail));
        Assert.Equal(multi.Start, cmCompFail);
        Assert.True(rmCompFail.IsBacktrack);

        // Multi-segment truncated by maxBufferSize=2 with isCompleted=true stays in Streaming mode -> Incomplete!
        Assert.False(PipeSequenceAdapter.TryParseSequence(in multi, isCompleted: true, static (ref ByteCursor c) => c.ReadU32BE(), out var cmTrunc, out var rmTrunc, maxBufferSize: 2));
        Assert.Equal(multi.Start, cmTrunc);
        Assert.True(rmTrunc.IsIncomplete);

        // Overload 2 (Projector): single non-parsed, multi non-parsed, multi completed-short -> Backtrack, multi truncated -> Incomplete
        Assert.False(PipeSequenceAdapter.TryParseSequence(in single, false, static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16), static (s, _) => s.Length, out var cpFail, out var rpFail));
        Assert.Equal(single.Start, cpFail);
        Assert.True(rpFail.IsIncomplete);

        Assert.False(PipeSequenceAdapter.TryParseSequence(in multi, false, static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16), static (s, _) => s.Length, out var cpmFail, out var rpmFail));
        Assert.Equal(multi.Start, cpmFail);
        Assert.True(rpmFail.IsIncomplete);

        Assert.False(PipeSequenceAdapter.TryParseSequence(in multi, isCompleted: true, static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16), static (s, _) => s.Length, out _, out var rpmComp));
        Assert.True(rpmComp.IsBacktrack);

        Assert.False(PipeSequenceAdapter.TryParseSequence(in multi, isCompleted: true, static (ref ByteCursor c) => c.ReadU32BE(), static (v, _) => v, out _, out var rpmTrunc, maxBufferSize: 2));
        Assert.True(rpmTrunc.IsIncomplete);

        // Overload 3 (ITryParser<T>): single parsed, single non-parsed, single completed-short, multi non-parsed, multi completed-short, multi truncated
        Assert.True(PipeSequenceAdapter.TryParseSequence(in single, true, TryParsers.U16BE, out var ct1, out var st1, out ushort v1));
        Assert.True(st1.IsParsed && v1 == 5);
        Assert.Equal(2L, single.Slice(0, ct1).Length);

        Assert.False(PipeSequenceAdapter.TryParseSequence(in single, false, TryParsers.F64BE, out var ctFail, out var stFail, out double _));
        Assert.Equal(single.Start, ctFail);
        Assert.True(stFail.IsIncomplete);

        Assert.False(PipeSequenceAdapter.TryParseSequence(in single, true, TryParsers.F64BE, out _, out var stCompFail, out double _));
        Assert.True(stCompFail.IsBacktrack);

        Assert.False(PipeSequenceAdapter.TryParseSequence(in multi, false, TryParsers.F64BE, out var ctmFail, out var stmFail, out double _));
        Assert.Equal(multi.Start, ctmFail);
        Assert.True(stmFail.IsIncomplete);

        Assert.False(PipeSequenceAdapter.TryParseSequence(in multi, true, TryParsers.F64BE, out _, out var stmCompFail, out double _));
        Assert.True(stmCompFail.IsBacktrack);

        Assert.False(PipeSequenceAdapter.TryParseSequence(in multi, true, TryParsers.U32BE, out _, out var stmTrunc, out uint _, maxBufferSize: 2));
        Assert.True(stmTrunc.IsIncomplete);

        // 3. PipeParser argument validation
        var guardPipe = new Pipe();
        await Xunit.Assert.ThrowsAsync<ArgumentNullException>(() => PipeParser.ReadOneAsync<uint>(null!, static (ref ByteCursor c) => c.ReadU32BE()).AsTask());
        await Xunit.Assert.ThrowsAsync<ArgumentNullException>(() => PipeParser.ReadOneAsync<uint>(guardPipe.Reader, (SpanParser<uint>)null!).AsTask());
        await Xunit.Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PipeParser.ReadOneAsync(guardPipe.Reader, static (ref ByteCursor c) => c.ReadU32BE(), maxBufferSize: 0).AsTask());

        await Xunit.Assert.ThrowsAsync<ArgumentNullException>(() => PipeParser.ReadOneAsync<uint, uint>(null!, static (ref ByteCursor c) => c.ReadU32BE(), static (v, _) => v).AsTask());
        await Xunit.Assert.ThrowsAsync<ArgumentNullException>(() => PipeParser.ReadOneAsync<uint, uint>(guardPipe.Reader, null!, static (v, _) => v).AsTask());
        await Xunit.Assert.ThrowsAsync<ArgumentNullException>(() => PipeParser.ReadOneAsync<uint, uint>(guardPipe.Reader, static (ref ByteCursor c) => c.ReadU32BE(), null!).AsTask());
        await Xunit.Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PipeParser.ReadOneAsync(guardPipe.Reader, static (ref ByteCursor c) => c.ReadU32BE(), static (v, _) => v, maxBufferSize: 0).AsTask());

        await Xunit.Assert.ThrowsAsync<ArgumentNullException>(() => PipeParser.ReadOneAsync<TryParsers.U32BEParser, uint>(null!, TryParsers.U32BE).AsTask());
        await Xunit.Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PipeParser.ReadOneAsync<TryParsers.U32BEParser, uint>(guardPipe.Reader, TryParsers.U32BE, maxBufferSize: 0).AsTask());
        await guardPipe.Writer.CompleteAsync();
        await guardPipe.Reader.CompleteAsync();

        // 4. ReadOneAsync<T>: Failure, Backtrack, IncompleteAtEof, and exact maxBufferSize boundary (==)
        static ParseResult<uint> AlwaysFailure(ref ByteCursor _) => Failure.At(0, ErrorKind.Protocol, "boom");
        static ParseResult<uint> AlwaysIncomplete(ref ByteCursor _) => Incomplete.AtLeast(7);

        // Verify AdvanceTo was properly called before returning by issuing TryRead on the reader afterwards!
        async Task<PipeParseOutcome<T>> RunSingleChunk<T>(byte[] data, bool complete, Func<PipeReader, ValueTask<PipeParseOutcome<T>>> fn)
        {
            var p = new Pipe();
            if (data.Length > 0)
            {
                data.CopyTo(p.Writer.GetSpan(data.Length));
                p.Writer.Advance(data.Length);
                await p.Writer.FlushAsync();
            }
            if (complete) await p.Writer.CompleteAsync();
            var outcome = await fn(p.Reader);
            // If ReadOneAsync omitted reader.AdvanceTo(...), TryRead throws InvalidOperationException("Reading is already in progress")!
            if (p.Reader.TryRead(out var after))
            {
                p.Reader.AdvanceTo(after.Buffer.Start);
            }
            if (!complete) await p.Writer.CompleteAsync();
            await p.Reader.CompleteAsync();
            return outcome;
        }

        // Empty-at-eof, Failure, Backtrack, IncompleteAtEof, and maxBufferSize on ReadOneAsync<T>
        var rEmpty1 = await RunSingleChunk([], true, r => PipeParser.ReadOneAsync(r, static (ref ByteCursor c) => c.ReadU32BE()));
        Assert.True(rEmpty1.IsEndOfStream);

        var rf1 = await RunSingleChunk([1, 2], true, r => PipeParser.ReadOneAsync(r, AlwaysFailure));
        Assert.True(rf1.TryGetValue(out PipeFailed pf1) && pf1.Error.Kind == ErrorKind.Protocol && pf1.Error.Context == "boom");

        var rbt1 = await RunSingleChunk([1, 2], true, r => PipeParser.ReadOneAsync(r, static (ref ByteCursor c) => c.ReadU32BE()));
        Assert.True(rbt1.TryGetValue(out PipeFailed pbt1) && pbt1.Error.Kind == ErrorKind.Eof);

        var ri1 = await RunSingleChunk([1, 2], true, r => PipeParser.ReadOneAsync(r, AlwaysIncomplete));
        Assert.True(ri1.TryGetValue(out PipeIncompleteAtEof pi1) && pi1.Needed.TryGetValue(out ExactBytes ex7) && ex7.Count == 7);

        // Exact maxBufferSize boundary (buffer.Length == maxBufferSize)
        var rMax1 = await RunSingleChunk([0x00, 0x0A, 0x01, 0x02], false, r => PipeParser.ReadOneAsync(r, static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16), maxBufferSize: 4));
        Assert.True(rMax1.TryGetValue(out PipeFailed pMax1) && pMax1.Error.Kind == ErrorKind.LengthValue && pMax1.Error.Context is not null && pMax1.Error.Context.Contains("maxBufferSize"));

        // 5. ReadOneAsync<T, TOut> (projector): Failure, Backtrack, IncompleteAtEof, exact maxBufferSize, empty-at-eof
        var rpEmpty = await RunSingleChunk([], true, r => PipeParser.ReadOneAsync(r, static (ref ByteCursor c) => c.ReadU32BE(), static (v, _) => v));
        Assert.True(rpEmpty.IsEndOfStream);

        var rpFailOut = await RunSingleChunk([1, 2], true, r => PipeParser.ReadOneAsync(r, AlwaysFailure, static (v, _) => v));
        Assert.True(rpFailOut.TryGetValue(out PipeFailed ppf) && ppf.Error.Kind == ErrorKind.Protocol);

        var rpBt = await RunSingleChunk([1, 2], true, r => PipeParser.ReadOneAsync(r, static (ref ByteCursor c) => c.ReadU32BE(), static (v, _) => v));
        Assert.True(rpBt.TryGetValue(out PipeFailed ppbt) && ppbt.Error.Kind == ErrorKind.Eof);

        var rpInc = await RunSingleChunk([1, 2], true, r => PipeParser.ReadOneAsync(r, AlwaysIncomplete, static (v, _) => v));
        Assert.True(rpInc.TryGetValue(out PipeIncompleteAtEof ppi) && ppi.Needed.TryGetValue(out ExactBytes ex7p) && ex7p.Count == 7);

        var rpMax = await RunSingleChunk([0x00, 0x0A, 0x01, 0x02], false, r => PipeParser.ReadOneAsync(r, static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16), static (s, _) => s.Length, maxBufferSize: 4));
        Assert.True(rpMax.TryGetValue(out PipeFailed ppMax) && ppMax.Error.Kind == ErrorKind.LengthValue && ppMax.Error.Context is not null && ppMax.Error.Context.Contains("maxBufferSize"));

        // 6. ReadOneAsync<TParser, T>: empty-at-eof, trickled Incomplete -> Parsed, maxBufferSize, IncompleteAtEof, Failure, Backtrack
        var rtEmpty = await RunSingleChunk([], true, r => PipeParser.ReadOneAsync<TryParsers.U32BEParser, uint>(r, TryParsers.U32BE));
        Assert.True(rtEmpty.IsEndOfStream);

        // Deterministic two-chunk trickle guaranteeing ReadOneAsync hits Incomplete -> AdvanceTo(Start, End) -> continue!
        async Task<PipeParseOutcome<uint>> RunDeterministicTrickle(Func<PipeReader, ValueTask<PipeParseOutcome<uint>>> fn)
        {
            var p = new Pipe();
            p.Writer.GetSpan(2)[0] = 0x11;
            p.Writer.GetSpan(2)[1] = 0x22;
            p.Writer.Advance(2);
            await p.Writer.FlushAsync();

            var readTask = fn(p.Reader).AsTask();
            await Task.Delay(10); // let ReadOneAsync observe the first 2 bytes, return Incomplete, and suspend on ReadAsync
            p.Writer.GetSpan(2)[0] = 0x33;
            p.Writer.GetSpan(2)[1] = 0x44;
            p.Writer.Advance(2);
            await p.Writer.FlushAsync();
            await p.Writer.CompleteAsync();

            var res = await readTask;
            await p.Reader.CompleteAsync();
            return res;
        }

        var rTrickle1 = await RunDeterministicTrickle(r => PipeParser.ReadOneAsync(r, static (ref ByteCursor c) => c.ReadU32BE()));
        Assert.True(rTrickle1.TryGetValue(out PipeParsed<uint> okT1) && okT1.Value == 0x11223344u);

        var rTrickle2 = await RunDeterministicTrickle(r => PipeParser.ReadOneAsync(r, static (ref ByteCursor c) => c.ReadU32BE(), static (v, _) => v));
        Assert.True(rTrickle2.TryGetValue(out PipeParsed<uint> okT2) && okT2.Value == 0x11223344u);

        var rtTrickle = await RunDeterministicTrickle(r => PipeParser.ReadOneAsync<TryParsers.U32BEParser, uint>(r, TryParsers.U32BE));
        Assert.True(rtTrickle.TryGetValue(out PipeParsed<uint> okU32) && okU32.Value == 0x11223344u);

        var rtMax = await RunSingleChunk([1, 2, 3, 4], false, r => PipeParser.ReadOneAsync<AlwaysIncompleteTryParser, uint>(r, default, maxBufferSize: 4));
        Assert.True(rtMax.TryGetValue(out PipeFailed ptm) && ptm.Error.Kind == ErrorKind.LengthValue && ptm.Error.Context is not null && ptm.Error.Context.Contains("maxBufferSize"));

        var rtInc = await RunSingleChunk([1, 2], true, r => PipeParser.ReadOneAsync<AlwaysIncompleteTryParser, uint>(r, default));
        Assert.True(rtInc.TryGetValue(out PipeIncompleteAtEof pti) && pti.Needed.TryGetValue(out ExactBytes ex4) && ex4.Count == 4);

        var rtFail = await RunSingleChunk([1, 2], true, r => PipeParser.ReadOneAsync<FailureOrBacktrackTryParser, uint>(r, new FailureOrBacktrackTryParser(fail: true)));
        Assert.True(rtFail.TryGetValue(out PipeFailed ptf) && ptf.Error.Kind == ErrorKind.Protocol);

        var rtBt = await RunSingleChunk([1, 2], true, r => PipeParser.ReadOneAsync<FailureOrBacktrackTryParser, uint>(r, new FailureOrBacktrackTryParser(fail: false)));
        Assert.True(rtBt.TryGetValue(out PipeFailed ptb) && ptb.Error.Kind == ErrorKind.Eof);

        // 7. ReadAllAsync<T> and ReadAllAsync<T, TOut> yield terminal PipeFailed and stop cleanly!
        var errPipe1 = new Pipe();
        errPipe1.Writer.GetSpan(2)[0] = 0xAA;
        errPipe1.Writer.Advance(2);
        await errPipe1.Writer.FlushAsync();
        await errPipe1.Writer.CompleteAsync();
        int errCount1 = 0;
        await foreach (var o in PipeParser.ReadAllAsync(errPipe1.Reader, static (ref ByteCursor c) => c.ReadU32BE()))
        {
            Assert.True(o.IsFailed);
            errCount1++;
        }
        await errPipe1.Reader.CompleteAsync();
        Assert.Equal(1, errCount1);

        var errPipe2 = new Pipe();
        errPipe2.Writer.GetSpan(2)[0] = 0xBB;
        errPipe2.Writer.Advance(2);
        await errPipe2.Writer.FlushAsync();
        await errPipe2.Writer.CompleteAsync();
        int errCount2 = 0;
        await foreach (var o in PipeParser.ReadAllAsync(errPipe2.Reader, static (ref ByteCursor c) => c.ReadU32BE(), static (v, _) => v))
        {
            Assert.True(o.IsFailed);
            errCount2++;
        }
        await errPipe2.Reader.CompleteAsync();
        Assert.Equal(1, errCount2);
    }

    private static byte[] BuildMaskedTextFrame(ReadOnlySpan<byte> payload, uint maskKey)
    {
        byte[] mask = [(byte)(maskKey >> 24), (byte)(maskKey >> 16), (byte)(maskKey >> 8), (byte)maskKey];
        byte[] frame = new byte[6 + payload.Length];
        frame[0] = 0x81;
        frame[1] = (byte)(0x80 | payload.Length);
        mask.CopyTo(frame.AsSpan(2, 4));
        for (int i = 0; i < payload.Length; i++) frame[6 + i] = (byte)(payload[i] ^ mask[i & 3]);
        return frame;
    }
}
