using Xunit;

namespace SpanNom.Tests;

public sealed class RailroadTests
{
    [Fact]
    public void Bind_Then_Ensure_HappyPath_And_Rollback()
    {
        // Format: [version=0x01][length=U8][value=U16BE]
        ReadOnlySpan<byte> valid = [0x01, 0x02, 0xCA, 0xFE];
        var c = valid.AsCursor();

        var parsed = c.ReadU8()
            .Ensure(static v => v == 0x01, c.Position, ErrorKind.Verify, "unsupported version")
            .Then(ref c, static (_, ref cur) => cur.ReadU8())
            .EnsureCommitted(static len => len == 2, c.Position, ErrorKind.Protocol, "expected 2-byte value")
            .Then(ref c, static (_, ref cur) => cur.ReadU16BE())
            .Bind(static v => ParseResult.Ok((int)v + 1));

        Assert.True(parsed.TryGetValue(out Parsed<int> ok));
        Assert.Equal(0xCAFF, ok.Value);
        Assert.True(c.IsAtEnd);

        // Violation on EnsureCommitted switches straight to committed Failure and rolls back Then cursor step
        ReadOnlySpan<byte> badLen = [0x01, 0x05, 0xCA, 0xFE];
        var cb = badLen.AsCursor();
        var failed = cb.ReadU8()
            .Ensure(static v => v == 0x01, cb.Position)
            .Then(ref cb, static (_, ref cur) => cur.ReadU8())
            .EnsureCommitted(static len => len == 2, cb.Position, ErrorKind.Protocol, "bad length")
            .Then(ref cb, static (_, ref cur) => cur.ReadU16BE());

        Assert.True(failed.TryGetValue(out Failure f));
        Assert.Equal(ErrorKind.Protocol, f.Error.Kind);
        Assert.Equal("bad length", f.Error.Context);
    }

    [Fact]
    public void Recover_OnlyRecoversBacktrack_NeverSwallowsFailureOrIncomplete()
    {
        ParseResult<int> ok = ParseResult.Ok(10);
        ParseResult<int> bt = Backtrack.At(2, ErrorKind.Tag);
        ParseResult<int> fail = Failure.At(4, ErrorKind.CrcMismatch);
        ParseResult<int> inc = Incomplete.AtLeast(3);

        Assert.Equal(10, ok.Recover(static _ => 99).Unwrap());
        Assert.Equal(99, bt.Recover(static _ => 99).Unwrap());
        Assert.True(fail.Recover(static _ => 99).IsFailure);
        Assert.True(inc.Recover(static _ => 99).IsIncomplete);

        Assert.Equal(77, bt.RecoverWith(static _ => ParseResult.Ok(77)).Unwrap());
        Assert.True(fail.RecoverWith(static _ => ParseResult.Ok(77)).IsFailure);
        Assert.True(inc.RecoverWith(static _ => ParseResult.Ok(77)).IsIncomplete);
    }

    [Fact]
    public void MapError_Tap_TapError_Match_RespectTracks()
    {
        int tappedValue = 0;
        ErrorKind tappedErr = ErrorKind.None;

        var ok = ParseResult.Ok(42)
            .Tap(v => tappedValue = v)
            .TapError(e => tappedErr = e.Kind);

        Assert.Equal(42, tappedValue);
        Assert.Equal(ErrorKind.None, tappedErr);

        var err = ParseResult.Backtrack<int>(5, ErrorKind.Tag)
            .MapError(static e => e.WithContext("wrapped"))
            .Tap(_ => tappedValue = -1)
            .TapError(e => tappedErr = e.Kind);

        Assert.Equal(42, tappedValue);
        Assert.Equal(ErrorKind.Tag, tappedErr);

        string matched = err.Match(
            onParsed: static v => $"ok:{v}",
            onIncomplete: static n => $"inc:{n}",
            onBacktrack: static e => $"bt:{e.Context}",
            onFailure: static e => $"fail:{e.Context}");

        Assert.Equal("bt:wrapped", matched);
        Assert.True(ok.IsParsed);
    }
}
