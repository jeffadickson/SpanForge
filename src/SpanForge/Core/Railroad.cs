using System.Runtime.CompilerServices;

namespace SpanForge;

/// <summary>
/// Continuation delegate for railway-oriented chaining over a mutable <see cref="ByteCursor"/>.
/// </summary>
public delegate ParseResult<TOut> ParseContinuation<in TIn, TOut>(TIn value, ref ByteCursor cursor);

/// <summary>
/// Railway-Oriented Programming (ROP) extensions for <see cref="ParseResult{T}"/>.
/// <para>
/// Enforces an errors-as-values discipline across all four modal tracks:
/// <list type="bullet">
///   <item><description><see cref="Parsed{T}"/> — happy track; continues through <c>Map</c>, <c>Bind</c>, <c>Then</c>, <c>Ensure</c>.</description></item>
///   <item><description><see cref="Backtrack"/> — recoverable off-track; short-circuits forward combinators, but can rejoin via <c>Recover</c>, <c>Parse.Alt</c>, or <c>Parse.Opt</c>.</description></item>
///   <item><description><see cref="Failure"/> — committed off-track (after <c>Cut</c>); bypasses <c>Recover</c> and <c>Parse.Alt</c>.</description></item>
///   <item><description><see cref="Incomplete"/> — streaming suspension track; bypasses <c>Recover</c> and <c>Parse.Alt</c> until more bytes arrive.</description></item>
/// </list>
/// </para>
/// </summary>
public static class RailroadExtensions
{
    /// <summary>
    /// Chains a pure value-level railway step (<c>T -&gt; ParseResult&lt;TOut&gt;</c>) without exceptions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<TOut> Bind<T, TOut>(
        this ParseResult<T> result,
        Func<T, ParseResult<TOut>> next) =>
        result.TryGetValue(out Parsed<T> ok)
            ? next(ok.Value)
            : result.PropagateNonParsed<TOut>();

    /// <summary>
    /// Closure-free <see cref="Bind{T, TOut}(ParseResult{T}, Func{T, ParseResult{TOut}})"/> taking explicit state.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<TOut> Bind<T, TState, TOut>(
        this ParseResult<T> result,
        TState state,
        Func<TState, T, ParseResult<TOut>> next) =>
        result.TryGetValue(out Parsed<T> ok)
            ? next(state, ok.Value)
            : result.PropagateNonParsed<TOut>();

    /// <summary>
    /// Chains a subsequent <see cref="ByteCursor"/> parsing step on the happy track,
    /// rolling back <paramref name="cursor"/> automatically if the continuation leaves the happy track.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<TOut> Then<T, TOut>(
        this ParseResult<T> result,
        ref ByteCursor cursor,
        ParseContinuation<T, TOut> next)
    {
        if (!result.TryGetValue(out Parsed<T> ok))
        {
            return result.PropagateNonParsed<TOut>();
        }

        var cp = cursor.Save();
        var nextRes = next(ok.Value, ref cursor);
        if (!nextRes.IsParsed)
        {
            cursor.Reset(cp);
        }

        return nextRes;
    }

    /// <summary>
    /// Validates a parsed value on the happy track; switches to <see cref="Backtrack"/> on failure.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Ensure<T>(
        this ParseResult<T> result,
        Func<T, bool> predicate,
        int offset,
        ErrorKind kind = ErrorKind.Verify,
        string? context = null) =>
        result.TryGetValue(out Parsed<T> ok) && !predicate(ok.Value)
            ? Backtrack.At(offset, kind, context)
            : result;

    /// <summary>
    /// Validates a parsed value on the happy track; switches directly to committed <see cref="Failure"/> on violation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> EnsureCommitted<T>(
        this ParseResult<T> result,
        Func<T, bool> predicate,
        int offset,
        ErrorKind kind = ErrorKind.Protocol,
        string? context = null) =>
        result.TryGetValue(out Parsed<T> ok) && !predicate(ok.Value)
            ? Failure.At(offset, kind, context)
            : result;

    /// <summary>
    /// Transforms the <see cref="ParseError"/> on both <see cref="Backtrack"/> and <see cref="Failure"/> tracks
    /// while leaving <see cref="Parsed{T}"/> and <see cref="Incomplete"/> untouched.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> MapError<T>(
        this ParseResult<T> result,
        Func<ParseError, ParseError> mapError) => result switch
    {
        Parsed<T> ok => ok,
        Incomplete inc => inc,
        Backtrack(var err) => new Backtrack(mapError(err)),
        Failure(var err) => new Failure(mapError(err)),
    };

    /// <summary>
    /// Recovers from a recoverable <see cref="Backtrack"/> onto the happy track with a fallback value.
    /// Committed <see cref="Failure"/> and streaming <see cref="Incomplete"/> are NEVER swallowed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Recover<T>(
        this ParseResult<T> result,
        Func<ParseError, T> fallback) => result switch
    {
        Parsed<T> ok => ok,
        Incomplete inc => inc,
        Backtrack(var err) => new Parsed<T>(fallback(err)),
        Failure fail => fail,
    };

    /// <summary>
    /// Recovers from a recoverable <see cref="Backtrack"/> using a fallback railway step.
    /// Committed <see cref="Failure"/> and streaming <see cref="Incomplete"/> are NEVER swallowed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> RecoverWith<T>(
        this ParseResult<T> result,
        Func<ParseError, ParseResult<T>> fallback) => result switch
    {
        Parsed<T> ok => ok,
        Incomplete inc => inc,
        Backtrack(var err) => fallback(err),
        Failure fail => fail,
    };

    /// <summary>
    /// Side-effect observer on the happy track without altering the result.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Tap<T>(this ParseResult<T> result, Action<T> onParsed)
    {
        if (result.TryGetValue(out Parsed<T> ok))
        {
            onParsed(ok.Value);
        }

        return result;
    }

    /// <summary>
    /// Side-effect observer on the error tracks (<see cref="Backtrack"/> or <see cref="Failure"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> TapError<T>(this ParseResult<T> result, Action<ParseError> onError)
    {
        if (result.TryGetValue(out Backtrack bt)) onError(bt.Error);
        else if (result.TryGetValue(out Failure fail)) onError(fail.Error);
        return result;
    }

    /// <summary>
    /// Total, exception-free exhaustive fold over all 4 railroad tracks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TOut Match<T, TOut>(
        this ParseResult<T> result,
        Func<T, TOut> onParsed,
        Func<Needed, TOut> onIncomplete,
        Func<ParseError, TOut> onBacktrack,
        Func<ParseError, TOut> onFailure) => result switch
    {
        Parsed<T>(var value) => onParsed(value),
        Incomplete(var needed) => onIncomplete(needed),
        Backtrack(var err) => onBacktrack(err),
        Failure(var err) => onFailure(err),
    };
}
