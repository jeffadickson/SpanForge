using System.Runtime.CompilerServices;

namespace SpanNom;

/// <summary>Successful parse outcome holding the decoded value.</summary>
public readonly record struct Parsed<T>(T Value);

/// <summary>Streaming parse outcome indicating more bytes are needed before deciding.</summary>
public readonly record struct Incomplete(Needed Needed)
{
    public static Incomplete Unknown => new(Needed.Unknown);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Incomplete AtLeast(int bytes) => new(Needed.Bytes(bytes));
}

/// <summary>
/// Recoverable parse mismatch (equivalent to Rust's <c>nom::Err::Error</c> / <c>winnow::ErrMode::Backtrack</c>).
/// Combinators such as <c>Parse.Alt</c> and <c>Parse.Opt</c> rewind the cursor checkpoint and continue.
/// </summary>
public readonly record struct Backtrack(ParseError Error)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Backtrack At(int offset, ErrorKind kind, string? context = null) =>
        new(new ParseError(offset, kind, context));
}

/// <summary>
/// Unrecoverable committed parse failure (equivalent to Rust's <c>nom::Err::Failure</c> / <c>winnow::ErrMode::Cut</c>).
/// Immediately halts <c>Parse.Alt</c> backtracking and propagates the diagnostic error outward.
/// </summary>
public readonly record struct Failure(ParseError Error)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Failure At(int offset, ErrorKind kind, string? context = null) =>
        new(new ParseError(offset, kind, context));
}

internal sealed record class ParseErrorDetail(string? Context, ParseErrorChain Cause);

/// <summary>
/// Compact, zero-boxing C# 15 custom <c>[Union]</c> representing the 4 modal outcomes of any binary parser:
/// <see cref="Parsed{T}"/>, <see cref="Incomplete"/>, <see cref="Backtrack"/>, or <see cref="Failure"/>.
/// <para>
/// Packs error offset / needed-bytes, <see cref="ErrorKind"/>, and tag tightly alongside a single optional
/// detail object (<see langword="null"/> on the fast path), and implements the C# 15 non-boxing access
/// pattern (<see cref="HasValue"/> + typed <c>TryGetValue</c> overloads) for zero-allocation exhaustive matching.
/// </para>
/// </summary>
[Union]
public readonly struct ParseResult<T> : IUnion
{
    private readonly T _value;
    private readonly object? _detail; // null | string (context) | ParseErrorDetail (context + cause chain)
    private readonly int _offsetOrNeeded; // offset for Backtrack/Failure; >=0 exact bytes or -1 unknown for Incomplete
    private readonly ErrorKind _kind;
    private readonly byte _tag; // 1 = Parsed<T>, 2 = Incomplete, 3 = Backtrack, 4 = Failure

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult(Parsed<T> parsed)
    {
        _value = parsed.Value;
        _detail = null;
        _offsetOrNeeded = 0;
        _kind = ErrorKind.None;
        _tag = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult(Incomplete incomplete)
    {
        _value = default!;
        _detail = null;
        _offsetOrNeeded = incomplete.Needed.TryGetValue(out ExactBytes exact) ? exact.Count : -1;
        _kind = ErrorKind.None;
        _tag = 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult(Backtrack backtrack)
    {
        _value = default!;
        _detail = PackDetail(backtrack.Error.Context, backtrack.Error.Cause);
        _offsetOrNeeded = backtrack.Error.Offset;
        _kind = backtrack.Error.Kind;
        _tag = 3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult(Failure failure)
    {
        _value = default!;
        _detail = PackDetail(failure.Error.Context, failure.Error.Cause);
        _offsetOrNeeded = failure.Error.Offset;
        _kind = failure.Error.Kind;
        _tag = 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ParseResult(object? detail, int offsetOrNeeded, ErrorKind kind, byte tag)
    {
        _value = default!;
        _detail = detail;
        _offsetOrNeeded = offsetOrNeeded;
        _kind = kind;
        _tag = tag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object? PackDetail(string? context, ParseErrorChain? cause) =>
        cause is null ? context : new ParseErrorDetail(context, cause);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ParseError UnpackError() => _detail switch
    {
        null => new ParseError(_offsetOrNeeded, _kind),
        string ctx => new ParseError(_offsetOrNeeded, _kind, ctx),
        ParseErrorDetail d => new ParseError(_offsetOrNeeded, _kind, d.Context, d.Cause),
        _ => new ParseError(_offsetOrNeeded, _kind),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Needed UnpackNeeded() =>
        _offsetOrNeeded > 0 ? Needed.Bytes(_offsetOrNeeded) : Needed.Unknown;

    public object? Value => _tag switch
    {
        1 => new Parsed<T>(_value),
        2 => new Incomplete(UnpackNeeded()),
        3 => new Backtrack(UnpackError()),
        4 => new Failure(UnpackError()),
        _ => null,
    };

    public bool HasValue => _tag != 0;
    public bool IsParsed => _tag == 1;
    public bool IsIncomplete => _tag == 2;
    public bool IsBacktrack => _tag == 3;
    public bool IsFailure => _tag == 4;
    public bool IsError => _tag is 3 or 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Parsed<T> value)
    {
        if (_tag == 1)
        {
            value = new Parsed<T>(_value);
            return true;
        }

        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Incomplete value)
    {
        if (_tag == 2)
        {
            value = new Incomplete(UnpackNeeded());
            return true;
        }

        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Backtrack value)
    {
        if (_tag == 3)
        {
            value = new Backtrack(UnpackError());
            return true;
        }

        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Failure value)
    {
        if (_tag == 4)
        {
            value = new Failure(UnpackError());
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Escalates a recoverable <see cref="Backtrack"/> into a committed <see cref="Failure"/>
    /// (equivalent to Rust's <c>nom::combinator::cut</c> / <c>winnow::combinator::cut_err</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<T> Cut() => _tag == 3 ? new ParseResult<T>(_detail, _offsetOrNeeded, _kind, 4) : this;

    /// <summary>
    /// Escalates a <see cref="Backtrack"/> into a <see cref="Failure"/> and attaches context.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<T> Cut(string context) => _tag is 3 or 4
        ? new Failure(UnpackError().WithContext(context))
        : this;

    /// <summary>
    /// Attaches a diagnostic context label to a <see cref="Backtrack"/> or <see cref="Failure"/>.
    /// No-op with zero allocations on <see cref="Parsed{T}"/> and <see cref="Incomplete"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<T> WithContext(string context) => _tag switch
    {
        3 => new Backtrack(UnpackError().WithContext(context)),
        4 => new Failure(UnpackError().WithContext(context)),
        _ => this,
    };

    /// <summary>
    /// Transforms the parsed value while preserving <see cref="Incomplete"/>, <see cref="Backtrack"/>,
    /// and <see cref="Failure"/> states unchanged.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TOut> Map<TOut>(Func<T, TOut> mapper) => _tag == 1
        ? new Parsed<TOut>(mapper(_value))
        : new ParseResult<TOut>(_detail, _offsetOrNeeded, _kind, _tag);

    /// <summary>
    /// Closure-free <see cref="Map{TOut}(Func{T, TOut})"/> overload taking an explicit state parameter.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TOut> Map<TState, TOut>(TState state, Func<TState, T, TOut> mapper) => _tag == 1
        ? new Parsed<TOut>(mapper(state, _value))
        : new ParseResult<TOut>(_detail, _offsetOrNeeded, _kind, _tag);

    /// <summary>
    /// Validates a parsed value with a predicate, converting failed checks into <see cref="Backtrack"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<T> Verify(Func<T, bool> predicate, int offset, string? context = null) =>
        _tag == 1 && !predicate(_value)
            ? Backtrack.At(offset, ErrorKind.Verify, context)
            : this;

    /// <summary>
    /// Converts a non-parsed outcome (<see cref="Incomplete"/>, <see cref="Backtrack"/>, or <see cref="Failure"/>)
    /// to another result type <typeparamref name="TOut"/> — ergonomic Rust <c>?</c> propagation helper.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TOut> PropagateNonParsed<TOut>() =>
        new(_detail, _offsetOrNeeded, _kind, _tag);

    /// <summary>
    /// Extracts the parsed value or throws <see cref="InvalidOperationException"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Unwrap() => _tag == 1 ? _value : ThrowUnwrapFailed();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T ThrowUnwrapFailed() => this switch
    {
        Parsed<T>(var value) => value,
        Incomplete(var needed) => throw new InvalidOperationException($"Parse was incomplete: {needed}"),
        Backtrack(var error) => throw new InvalidOperationException($"Parse backtracked: {error}"),
        Failure(var error) => throw new InvalidOperationException($"Parse failed: {error}"),
    };

    public override string ToString() => this switch
    {
        Parsed<T>(var value) => $"Parsed({value})",
        Incomplete(var needed) => $"Incomplete({needed})",
        Backtrack(var error) => $"Backtrack({error})",
        Failure(var error) => $"Failure({error})",
        null => "ParseResult(Uninitialized)",
    };
}

/// <summary>
/// Convenience factory helpers for constructing <see cref="ParseResult{T}"/> cases concisely.
/// </summary>
public static class ParseResult
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Ok<T>(T value) => new Parsed<T>(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Need<T>(int bytes) => Incomplete.AtLeast(bytes);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> NeedUnknown<T>() => Incomplete.Unknown;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Backtrack<T>(int offset, ErrorKind kind, string? context = null) =>
        SpanNom.Backtrack.At(offset, kind, context);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Fail<T>(int offset, ErrorKind kind, string? context = null) =>
        Failure.At(offset, kind, context);
}
