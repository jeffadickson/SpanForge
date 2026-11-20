using System.Runtime.CompilerServices;

namespace SpanForge;

/// <summary>
/// Trait for lightweight value-type parse errors used by the register-sized fast-path
/// <see cref="ParseResult{T, TError}"/> (analogous to Rust <c>winnow::error::ParserError</c>).
/// </summary>
public interface IParseError<TSelf>
    where TSelf : struct, IParseError<TSelf>
{
    static abstract TSelf Create(int offset, ErrorKind kind);
    ParseError ToParseError();
}

/// <summary>
/// Zero-payload error type for maximum throughput when only success vs. failure matters
/// (analogous to Rust <c>winnow::error::EmptyError</c>). Contains zero GC references.
/// </summary>
public readonly struct NoError : IParseError<NoError>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NoError Create(int offset, ErrorKind kind) => default;

    public ParseError ToParseError() => new(0, ErrorKind.None);

    public override string ToString() => nameof(NoError);
}

/// <summary>
/// 1-byte error type retaining <see cref="ErrorKind"/> with zero GC references, allowing
/// <see cref="ParseResult{T, KindError}"/> for up to 8-byte values to fit in 2 x86-64 registers.
/// </summary>
public readonly record struct KindError(ErrorKind Kind) : IParseError<KindError>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KindError Create(int offset, ErrorKind kind) => new(kind);

    public ParseError ToParseError() => new(0, Kind);

    public override string ToString() => Kind.ToString();
}

/// <summary>
/// 8-byte unmanaged error type retaining exact byte offset and <see cref="ErrorKind"/>
/// with zero GC references (analogous to <c>nom::error::Error</c> without heap strings).
/// </summary>
public readonly record struct CompactError(int Offset, ErrorKind Kind) : IParseError<CompactError>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CompactError Create(int offset, ErrorKind kind) => new(offset, kind);

    public ParseError ToParseError() => new(Offset, Kind);

    public override string ToString() => $"{Kind} at byte offset {Offset}";
}

/// <summary>Recoverable parse mismatch carrying a custom value-type error <typeparamref name="TError"/>.</summary>
public readonly record struct Backtrack<TError>(TError Error)
    where TError : struct;

/// <summary>Unrecoverable committed parse failure carrying a custom value-type error <typeparamref name="TError"/>.</summary>
public readonly record struct Failure<TError>(TError Error)
    where TError : struct;

/// <summary>
/// Register-friendly, zero-boxing C# 15 custom <c>[Union]</c> representing the 4 modal outcomes
/// (<see cref="Parsed{T}"/> | <see cref="Incomplete"/> | <see cref="Backtrack{TError}"/> | <see cref="Failure{TError}"/>)
/// without any GC-tracked reference fields when <typeparamref name="T"/> and <typeparamref name="TError"/> are unmanaged.
/// <para>
/// With <see cref="NoError"/> or <see cref="KindError"/>, <c>ParseResult&lt;ushort, NoError&gt;</c> is 8 bytes
/// (1 x86-64 register) and <c>ParseResult&lt;ulong, NoError&gt;</c> / <c>ParseResult&lt;ByteSlice, NoError&gt;</c>
/// is 16 bytes (2 x86-64 registers), eliminating hidden stack returns (<c>sret</c>) and GC pointer tracking.
/// </para>
/// </summary>
[Union]
public readonly struct ParseResult<T, TError> : IUnion
    where TError : struct, IParseError<TError>
{
    private readonly int _tagAndNeeded; // low 3 bits = tag (1..4); for tag 2, upper 29 bits = signed needed
    private readonly TError _error;
    private readonly T _value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PackIncomplete(Needed needed)
    {
        if (needed.TryGetValue(out ExactBytes exact))
        {
            int clamped = exact.Count > 0x0FFF_FFFF ? 0x0FFF_FFFF : exact.Count;
            return (clamped << 3) | 2;
        }

        return (-1 << 3) | 2;
    }

    private int Tag
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _tagAndNeeded & 0x7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult(Parsed<T> parsed)
    {
        _value = parsed.Value;
        _tagAndNeeded = 1;
        _error = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult(Incomplete incomplete)
    {
        _value = default!;
        _tagAndNeeded = PackIncomplete(incomplete.Needed);
        _error = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult(Backtrack<TError> backtrack)
    {
        _value = default!;
        _tagAndNeeded = 3;
        _error = backtrack.Error;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult(Failure<TError> failure)
    {
        _value = default!;
        _tagAndNeeded = 4;
        _error = failure.Error;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ParseResult(int tagAndNeeded, TError error)
    {
        _value = default!;
        _tagAndNeeded = tagAndNeeded;
        _error = error;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Needed UnpackNeeded()
    {
        int n = _tagAndNeeded >> 3;
        return n > 0 ? Needed.Bytes(n) : Needed.Unknown;
    }

    public object? Value => Tag switch
    {
        1 => new Parsed<T>(_value),
        2 => new Incomplete(UnpackNeeded()),
        3 => new Backtrack<TError>(_error),
        4 => new Failure<TError>(_error),
        _ => null,
    };

    public bool HasValue => _tagAndNeeded != 0;
    public bool IsParsed => _tagAndNeeded == 1;
    public bool IsIncomplete => Tag == 2;
    public bool IsBacktrack => _tagAndNeeded == 3;
    public bool IsFailure => _tagAndNeeded == 4;
    public bool IsError => _tagAndNeeded is 3 or 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Parsed<T> value)
    {
        if (_tagAndNeeded == 1)
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
        if (Tag == 2)
        {
            value = new Incomplete(UnpackNeeded());
            return true;
        }

        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Backtrack<TError> value)
    {
        if (_tagAndNeeded == 3)
        {
            value = new Backtrack<TError>(_error);
            return true;
        }

        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Failure<TError> value)
    {
        if (_tagAndNeeded == 4)
        {
            value = new Failure<TError>(_error);
            return true;
        }

        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<T, TError> Cut() => _tagAndNeeded == 3 ? new ParseResult<T, TError>(4, _error) : this;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TOut, TError> Map<TOut>(Func<T, TOut> mapper) => _tagAndNeeded == 1
        ? new Parsed<TOut>(mapper(_value))
        : new ParseResult<TOut, TError>(_tagAndNeeded, _error);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TOut, TError> Map<TState, TOut>(TState state, Func<TState, T, TOut> mapper) => _tagAndNeeded == 1
        ? new Parsed<TOut>(mapper(state, _value))
        : new ParseResult<TOut, TError>(_tagAndNeeded, _error);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<T, TError> Verify(Func<T, bool> predicate, int offset) =>
        _tagAndNeeded == 1 && !predicate(_value)
            ? new Backtrack<TError>(TError.Create(offset, ErrorKind.Verify))
            : this;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<TOut, TError> PropagateNonParsed<TOut>() =>
        new(_tagAndNeeded, _error);

    /// <summary>
    /// Materializes a full diagnostic <see cref="ParseResult{T}"/> (zero overhead on success;
    /// converts <typeparamref name="TError"/> via <see cref="IParseError{TSelf}.ToParseError"/> on error).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<T> ToParseResult() => Tag switch
    {
        1 => new Parsed<T>(_value),
        2 => new Incomplete(UnpackNeeded()),
        3 => new Backtrack(_error.ToParseError()),
        4 => new Failure(_error.ToParseError()),
        _ => default,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Unwrap() => _tagAndNeeded == 1 ? _value : ThrowUnwrapFailed();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T ThrowUnwrapFailed() => this switch
    {
        Parsed<T>(var value) => value,
        Incomplete(var needed) => throw new InvalidOperationException($"Parse was incomplete: {needed}"),
        Backtrack<TError>(var error) => throw new InvalidOperationException($"Parse backtracked: {error}"),
        Failure<TError>(var error) => throw new InvalidOperationException($"Parse failed: {error}"),
    };

    public override string ToString() => this switch
    {
        Parsed<T>(var value) => $"Parsed({value})",
        Incomplete(var needed) => $"Incomplete({needed})",
        Backtrack<TError>(var error) => $"Backtrack({error})",
        Failure<TError>(var error) => $"Failure({error})",
        null => "ParseResult(Uninitialized)",
    };
}

/// <summary>
/// Convenience factory helpers for <see cref="ParseResult{T, TError}"/>.
/// </summary>
public static class FastParseResult
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T, TError> Ok<T, TError>(T value)
        where TError : struct, IParseError<TError> =>
        new Parsed<T>(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T, TError> Need<T, TError>(int bytes)
        where TError : struct, IParseError<TError> =>
        Incomplete.AtLeast(bytes);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T, TError> NeedUnknown<T, TError>()
        where TError : struct, IParseError<TError> =>
        Incomplete.Unknown;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T, TError> Backtrack<T, TError>(int offset, ErrorKind kind)
        where TError : struct, IParseError<TError> =>
        new Backtrack<TError>(TError.Create(offset, kind));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T, TError> Fail<T, TError>(int offset, ErrorKind kind)
        where TError : struct, IParseError<TError> =>
        new Failure<TError>(TError.Create(offset, kind));
}
