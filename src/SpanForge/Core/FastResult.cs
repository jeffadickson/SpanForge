using System.Runtime.CompilerServices;

namespace SpanForge;

/// <summary>
/// 4-byte register-sized modal parse status (zero GC references, zero nested structs)
/// encoding <c>Parsed</c>, <c>Incomplete(Needed)</c>, <c>Backtrack</c>, or <c>Failure</c>.
/// Used by <see cref="ITryParser{T}"/> to eliminate intermediate struct copies in <c>Alt</c>/<c>Delimited</c> pipelines.
/// </summary>
public readonly struct FastStatus : IEquatable<FastStatus>
{
    // 0 = Parsed, 1 = Backtrack, 2 = Failure, < 0 = Incomplete (-bytes, or int.MinValue = Unknown)
    internal readonly int Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FastStatus(int raw) => Raw = raw;

    public static FastStatus Parsed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(0);
    }

    public static FastStatus Backtrack
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(1);
    }

    public static FastStatus Failure
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(2);
    }

    public static FastStatus IncompleteUnknown
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(int.MinValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FastStatus IncompleteBytes(int bytes) =>
        new(bytes > 0 ? -bytes : -1);

    public bool IsParsed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Raw == 0;
    }

    public bool IsIncomplete
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Raw < 0;
    }

    public bool IsBacktrack
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Raw == 1;
    }

    public bool IsFailure
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Raw == 2;
    }

    public bool IsError
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Raw > 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Needed UnpackNeeded() =>
        Raw == int.MinValue ? Needed.Unknown : Needed.Bytes(-Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus Cut() => Raw == 1 ? Failure : this;

    public bool Equals(FastStatus other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is FastStatus other && Raw == other.Raw;
    public override int GetHashCode() => Raw;
    public static bool operator ==(FastStatus left, FastStatus right) => left.Raw == right.Raw;
    public static bool operator !=(FastStatus left, FastStatus right) => left.Raw != right.Raw;
}

/// <summary>
/// Ultra-lean 2-field C# 15 custom <c>[Union]</c> (<see cref="Parsed{T}"/> | <see cref="Incomplete"/> |
/// <see cref="Backtrack{NoError}"/> | <see cref="Failure{NoError}"/>) with ZERO nested structs and ZERO GC references,
/// enabling full RyuJIT scalar replacement into 1–2 CPU registers.
/// </summary>
[Union]
public readonly struct FastResult<T> : IUnion
{
    // 0 = unset (default), 1 = Parsed<T>, 2 = Backtrack<NoError>, 3 = Failure<NoError>, < 0 = Incomplete (-bytes, or int.MinValue = Unknown)
    private readonly int _raw;
    private readonly T _value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastResult(Parsed<T> parsed)
    {
        _raw = 1;
        _value = parsed.Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastResult(Incomplete incomplete)
    {
        _raw = incomplete.Needed.TryGetValue(out ExactBytes exact)
            ? FastStatus.IncompleteBytes(exact.Count).Raw
            : int.MinValue;
        _value = default!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastResult(Backtrack<NoError> _)
    {
        _raw = 2;
        _value = default!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastResult(Failure<NoError> _)
    {
        _raw = 3;
        _value = default!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastResult(FastStatus status, T value = default!)
    {
        _raw = status.Raw < 0 ? status.Raw : status.Raw + 1;
        _value = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Needed UnpackNeeded() =>
        _raw == int.MinValue ? Needed.Unknown : Needed.Bytes(-_raw);

    public object? Value => _raw switch
    {
        1 => new Parsed<T>(_value),
        2 => new Backtrack<NoError>(default),
        3 => new Failure<NoError>(default),
        < 0 => new Incomplete(UnpackNeeded()),
        _ => null,
    };

    public bool HasValue => _raw != 0;
    public bool IsParsed => _raw == 1;
    public bool IsIncomplete => _raw < 0;
    public bool IsBacktrack => _raw == 2;
    public bool IsFailure => _raw == 3;
    public bool IsError => _raw is 2 or 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Parsed<T> value)
    {
        if (_raw == 1) { value = new Parsed<T>(_value); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Incomplete value)
    {
        if (_raw < 0) { value = new Incomplete(UnpackNeeded()); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Backtrack<NoError> value)
    {
        value = default;
        return _raw == 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Failure<NoError> value)
    {
        value = default;
        return _raw == 3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Unwrap() => _raw == 1 ? _value : ThrowUnwrapFailed();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T ThrowUnwrapFailed() => this switch
    {
        Parsed<T>(var value) => value,
        Incomplete(var needed) => throw new InvalidOperationException($"Parse was incomplete: {needed}"),
        Backtrack<NoError> => throw new InvalidOperationException("Parse backtracked: NoError"),
        Failure<NoError> => throw new InvalidOperationException("Parse failed: NoError"),
    };
}

/// <summary>
/// Maximum-throughput monomorphized parser trait using a 4-byte <see cref="FastStatus"/> return
/// plus <c>out T</c> to avoid intermediate multi-register struct copies across <c>Alt</c> / <c>Delimited</c> trees.
/// </summary>
public interface ITryParser<T>
{
    FastStatus TryParse(ref ByteCursor cursor, out T value);
}

/// <summary>
/// Monomorphized key-dispatch handler trait paired with <see cref="TryParsers.Dispatch{TKey, TKeyParser, THandler, T}"/>
/// (analogous to Rust <c>winnow::combinator::dispatch!</c>) for O(1) tag-discriminated parsing.
/// </summary>
public interface ITryDispatchHandler<TKey, T>
{
    FastStatus TryParse(TKey key, ref ByteCursor cursor, out T value);
}

/// <summary>
/// Monomorphized streaming fold handler paired with <see cref="TryParsers.FoldDispatch{TKey, TKeyParser, TFolder, TAcc}"/>
/// to fold tagged binary streams directly into an accumulator without intermediate union materialization.
/// </summary>
public interface ITryDispatchFolder<TKey, TAcc>
{
    FastStatus TryFold(TKey key, ref ByteCursor cursor, ref TAcc accumulator);
}

/// <summary>
/// Monomorphized <see cref="ITryParser{T}"/> combinators designed for zero-overhead RyuJIT scalar promotion.
/// </summary>
public static class TryParsers
{
    public readonly struct TagByteParser(byte expected) : ITryParser<byte>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastStatus TryParse(ref ByteCursor cursor, out byte value) => cursor.TryTagByte(expected, out value);
    }

    public readonly struct U8Parser : ITryParser<byte>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastStatus TryParse(ref ByteCursor cursor, out byte value) => cursor.TryReadU8(out value);
    }

    public readonly struct U16BEParser : ITryParser<ushort>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastStatus TryParse(ref ByteCursor cursor, out ushort value) => cursor.TryReadU16BE(out value);
    }

    public readonly struct U32BEParser : ITryParser<uint>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastStatus TryParse(ref ByteCursor cursor, out uint value) => cursor.TryReadU32BE(out value);
    }

    public readonly struct F64BEParser : ITryParser<double>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastStatus TryParse(ref ByteCursor cursor, out double value) => cursor.TryReadF64BE(out value);
    }

    public readonly struct PrecededParser<TPrefix, TValue, T1, T2>(TPrefix prefix, TValue value) : ITryParser<T2>
        where TPrefix : struct, ITryParser<T1>
        where TValue : struct, ITryParser<T2>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastStatus TryParse(ref ByteCursor cursor, out T2 result)
        {
            int start = cursor.Position;
            var s1 = prefix.TryParse(ref cursor, out _);
            if (!s1.IsParsed) { Unsafe.SkipInit(out result); return s1; }

            var s2 = value.TryParse(ref cursor, out result);
            if (!s2.IsParsed) { cursor.Rewind(start); return s2; }

            return FastStatus.Parsed;
        }
    }

    public readonly struct DelimitedParser<TOpen, TValue, TClose, T1, T2, T3>(
        TOpen open,
        TValue value,
        TClose close) : ITryParser<T2>
        where TOpen : struct, ITryParser<T1>
        where TValue : struct, ITryParser<T2>
        where TClose : struct, ITryParser<T3>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastStatus TryParse(ref ByteCursor cursor, out T2 result)
        {
            int start = cursor.Position;
            var s1 = open.TryParse(ref cursor, out _);
            if (!s1.IsParsed) { Unsafe.SkipInit(out result); return s1; }

            var s2 = value.TryParse(ref cursor, out result);
            if (!s2.IsParsed) { cursor.Rewind(start); return s2; }

            var s3 = close.TryParse(ref cursor, out _);
            if (!s3.IsParsed) { cursor.Rewind(start); return s3; }

            return FastStatus.Parsed;
        }
    }

    public readonly struct Alt3Parser<T1, T2, T3, T>(T1 first, T2 second, T3 third) : ITryParser<T>
        where T1 : struct, ITryParser<T>
        where T2 : struct, ITryParser<T>
        where T3 : struct, ITryParser<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastStatus TryParse(ref ByteCursor cursor, out T value)
        {
            int start = cursor.Position;
            var s1 = first.TryParse(ref cursor, out value);
            if (s1.IsParsed) return FastStatus.Parsed;
            if (!s1.IsBacktrack) { cursor.Rewind(start); return s1; }
            cursor.Rewind(start);

            var s2 = second.TryParse(ref cursor, out value);
            if (s2.IsParsed) return FastStatus.Parsed;
            if (!s2.IsBacktrack) { cursor.Rewind(start); return s2; }
            cursor.Rewind(start);

            var s3 = third.TryParse(ref cursor, out value);
            if (s3.IsParsed) return FastStatus.Parsed;
            cursor.Rewind(start);
            return s3;
        }
    }

    public readonly struct DispatchParser<TKey, TKeyParser, THandler, T>(TKeyParser keyParser, THandler handler)
        : ITryParser<T>
        where TKeyParser : struct, ITryParser<TKey>
        where THandler : struct, ITryDispatchHandler<TKey, T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastStatus TryParse(ref ByteCursor cursor, out T value)
        {
            int start = cursor.Position;
            var ks = keyParser.TryParse(ref cursor, out TKey key);
            if (!ks.IsParsed) { Unsafe.SkipInit(out value); return ks; }

            var vs = handler.TryParse(key, ref cursor, out value);
            if (!vs.IsParsed) { cursor.Rewind(start); return vs; }

            return FastStatus.Parsed;
        }
    }

    public static TagByteParser TagByte(byte b) => new(b);
    public static U8Parser U8 => default;
    public static U16BEParser U16BE => default;
    public static U32BEParser U32BE => default;
    public static F64BEParser F64BE => default;

    public static PrecededParser<TPrefix, TValue, T1, T2> Preceded<TPrefix, TValue, T1, T2>(
        TPrefix prefix, TValue value)
        where TPrefix : struct, ITryParser<T1>
        where TValue : struct, ITryParser<T2> => new(prefix, value);

    public static DelimitedParser<TOpen, TValue, TClose, T1, T2, T3> Delimited<TOpen, TValue, TClose, T1, T2, T3>(
        TOpen open, TValue value, TClose close)
        where TOpen : struct, ITryParser<T1>
        where TValue : struct, ITryParser<T2>
        where TClose : struct, ITryParser<T3> => new(open, value, close);

    public static Alt3Parser<T1, T2, T3, T> Alt<T1, T2, T3, T>(T1 first, T2 second, T3 third)
        where T1 : struct, ITryParser<T>
        where T2 : struct, ITryParser<T>
        where T3 : struct, ITryParser<T> => new(first, second, third);

    public static DispatchParser<TKey, TKeyParser, THandler, T> Dispatch<TKey, TKeyParser, THandler, T>(
        TKeyParser keyParser, THandler handler)
        where TKeyParser : struct, ITryParser<TKey>
        where THandler : struct, ITryDispatchHandler<TKey, T> => new(keyParser, handler);

    /// <summary>
    /// Repeatedly parses a key with <paramref name="keyParser"/> and folds each element into
    /// <paramref name="accumulator"/> via <paramref name="folder"/> until backtracking cleanly at item boundary.
    /// Propagates <see cref="FastStatus.IsIncomplete"/> and <see cref="FastStatus.IsFailure"/> immediately with rollback.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FastStatus FoldDispatch<TKey, TKeyParser, TFolder, TAcc>(
        ref ByteCursor cursor,
        TKeyParser keyParser,
        TFolder folder,
        ref TAcc accumulator)
        where TKeyParser : struct, ITryParser<TKey>
        where TFolder : struct, ITryDispatchFolder<TKey, TAcc>
    {
        while (true)
        {
            int start = cursor.Position;
            var ks = keyParser.TryParse(ref cursor, out TKey key);
            if (!ks.IsParsed)
            {
                if (ks.IsBacktrack) return FastStatus.Parsed;
                return ks;
            }

            var fs = folder.TryFold(key, ref cursor, ref accumulator);
            if (!fs.IsParsed)
            {
                cursor.Rewind(start);
                if (fs.IsBacktrack) return FastStatus.Parsed;
                return fs;
            }
        }
    }
}
