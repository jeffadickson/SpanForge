using System.Runtime.CompilerServices;

namespace SpanForge;

/// <summary>
/// Monomorphized value-type parser combinators implementing <see cref="IParser{T}"/>.
/// Mirrors Rust's zero-cost trait-based combinator pipelines: the JIT specializes and inlines
/// the entire parser tree with zero delegate dispatch and zero heap allocation.
/// </summary>
public static class StructParsers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ParseResult<T> ResetAndReturn<T>(ref ByteCursor cursor, Checkpoint cp, ParseResult<T> result)
    {
        cursor.Reset(cp);
        return result;
    }

    public readonly struct U8Parser : IParser<byte>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<byte> Parse(ref ByteCursor cursor) => cursor.ReadU8();
    }

    public readonly struct U16Parser : IParser<ushort>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<ushort> Parse(ref ByteCursor cursor) => cursor.ReadU16();
    }

    public readonly struct U32Parser : IParser<uint>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<uint> Parse(ref ByteCursor cursor) => cursor.ReadU32();
    }

    public readonly struct U64Parser : IParser<ulong>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<ulong> Parse(ref ByteCursor cursor) => cursor.ReadU64();
    }

    public readonly struct VarUInt64Parser : IParser<ulong>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<ulong> Parse(ref ByteCursor cursor) => cursor.ReadVarUInt64();
    }

    public readonly struct TagByteParser(byte expected) : IParser<byte>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<byte> Parse(ref ByteCursor cursor) => cursor.TagByte(expected);
    }

    public readonly struct TakeParser(int count) : IParser<ByteSlice>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<ByteSlice> Parse(ref ByteCursor cursor) => cursor.Take(count);
    }

    public readonly struct LengthPrefixedParser(LengthPrefix prefix) : IParser<ByteSlice>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<ByteSlice> Parse(ref ByteCursor cursor) => cursor.ReadLengthPrefixedBytes(prefix);
    }

    public readonly struct CutParser<TParser, T>(TParser inner, string? context = null) : IParser<T>
        where TParser : struct, IParser<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<T> Parse(ref ByteCursor cursor)
        {
            var result = inner.Parse(ref cursor);
            return context is null ? result.Cut() : result.Cut(context);
        }
    }

    public readonly struct OptParser<TParser, T>(TParser inner) : IParser<Option<T>>
        where TParser : struct, IParser<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<Option<T>> Parse(ref ByteCursor cursor)
        {
            var cp = cursor.Save();
            return inner.Parse(ref cursor) switch
            {
                Parsed<T>(var val) => new Parsed<Option<T>>(Option<T>.Of(val)),
                Backtrack => ResetAndReturn<Option<T>>(ref cursor, cp, new Parsed<Option<T>>(Option<T>.Nothing)),
                Incomplete inc => ResetAndReturn<Option<T>>(ref cursor, cp, inc),
                Failure fail => ResetAndReturn<Option<T>>(ref cursor, cp, fail),
            };
        }
    }

    public readonly struct PrecededParser<TPrefix, TValue, T1, T2>(TPrefix prefix, TValue value) : IParser<T2>
        where TPrefix : struct, IParser<T1>
        where TValue : struct, IParser<T2>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<T2> Parse(ref ByteCursor cursor)
        {
            var cp = cursor.Save();
            var pRes = prefix.Parse(ref cursor);
            if (!pRes.IsParsed)
            {
                cursor.Reset(cp);
                return pRes.PropagateNonParsed<T2>();
            }

            var res = value.Parse(ref cursor);
            if (!res.IsParsed) cursor.Reset(cp);
            return res;
        }
    }

    public readonly struct DelimitedParser<TOpen, TValue, TClose, T1, T2, T3>(
        TOpen open,
        TValue value,
        TClose close) : IParser<T2>
        where TOpen : struct, IParser<T1>
        where TValue : struct, IParser<T2>
        where TClose : struct, IParser<T3>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<T2> Parse(ref ByteCursor cursor)
        {
            var cp = cursor.Save();
            var oRes = open.Parse(ref cursor);
            if (!oRes.IsParsed)
            {
                cursor.Reset(cp);
                return oRes.PropagateNonParsed<T2>();
            }

            var vRes = value.Parse(ref cursor);
            if (!vRes.TryGetValue(out Parsed<T2> ok))
            {
                cursor.Reset(cp);
                return vRes;
            }

            var cRes = close.Parse(ref cursor);
            if (!cRes.IsParsed)
            {
                cursor.Reset(cp);
                return cRes.PropagateNonParsed<T2>();
            }

            return ok;
        }
    }

    public readonly struct PairParser<TFirst, TSecond, T1, T2>(TFirst first, TSecond second) : IParser<(T1, T2)>
        where TFirst : struct, IParser<T1>
        where TSecond : struct, IParser<T2>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<(T1, T2)> Parse(ref ByteCursor cursor)
        {
            var cp = cursor.Save();
            var r1 = first.Parse(ref cursor);
            if (!r1.TryGetValue(out Parsed<T1> p1))
            {
                cursor.Reset(cp);
                return r1.PropagateNonParsed<(T1, T2)>();
            }

            var r2 = second.Parse(ref cursor);
            if (!r2.TryGetValue(out Parsed<T2> p2))
            {
                cursor.Reset(cp);
                return r2.PropagateNonParsed<(T1, T2)>();
            }

            return new Parsed<(T1, T2)>((p1.Value, p2.Value));
        }
    }

    public readonly struct AltParser<TFirst, TSecond, T>(TFirst first, TSecond second) : IParser<T>
        where TFirst : struct, IParser<T>
        where TSecond : struct, IParser<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<T> Parse(ref ByteCursor cursor)
        {
            var cp = cursor.Save();
            var r1 = first.Parse(ref cursor);
            if (!r1.IsBacktrack)
            {
                if (!r1.IsParsed) cursor.Reset(cp);
                return r1;
            }

            cursor.Reset(cp);
            return second.Parse(ref cursor) switch
            {
                Parsed<T> ok => ok,
                Incomplete inc => ResetAndReturn<T>(ref cursor, cp, inc),
                Failure fail => ResetAndReturn<T>(ref cursor, cp, fail),
                Backtrack => ResetAndReturn<T>(ref cursor, cp, Backtrack.At(cp.Position, ErrorKind.Alt)),
            };
        }
    }

    public readonly struct F64Parser : IParser<double>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<double> Parse(ref ByteCursor cursor) => cursor.ReadF64();
    }

    public readonly struct Alt3Parser<TFirst, TSecond, TThird, T>(TFirst first, TSecond second, TThird third) : IParser<T>
        where TFirst : struct, IParser<T>
        where TSecond : struct, IParser<T>
        where TThird : struct, IParser<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<T> Parse(ref ByteCursor cursor)
        {
            var cp = cursor.Save();
            var r1 = first.Parse(ref cursor);
            if (!r1.IsBacktrack) { if (!r1.IsParsed) cursor.Reset(cp); return r1; }
            cursor.Reset(cp);

            var r2 = second.Parse(ref cursor);
            if (!r2.IsBacktrack) { if (!r2.IsParsed) cursor.Reset(cp); return r2; }
            cursor.Reset(cp);

            return third.Parse(ref cursor) switch
            {
                Parsed<T> ok => ok,
                Incomplete inc => ResetAndReturn<T>(ref cursor, cp, inc),
                Failure fail => ResetAndReturn<T>(ref cursor, cp, fail),
                Backtrack => ResetAndReturn<T>(ref cursor, cp, Backtrack.At(cp.Position, ErrorKind.Alt)),
            };
        }
    }

    public static U8Parser U8 => default;
    public static U16Parser U16 => default;
    public static U32Parser U32 => default;
    public static U64Parser U64 => default;
    public static F64Parser F64 => default;
    public static VarUInt64Parser VarUInt64 => default;
    public static TagByteParser TagByte(byte b) => new(b);
    public static TakeParser Take(int count) => new(count);
    public static LengthPrefixedParser LengthPrefixed(LengthPrefix prefix) => new(prefix);

    public static CutParser<TParser, T> Cut<TParser, T>(TParser inner, string? context = null)
        where TParser : struct, IParser<T> => new(inner, context);

    public static OptParser<TParser, T> Opt<TParser, T>(TParser inner)
        where TParser : struct, IParser<T> => new(inner);

    public static PrecededParser<TPrefix, TValue, T1, T2> Preceded<TPrefix, TValue, T1, T2>(
        TPrefix prefix,
        TValue value)
        where TPrefix : struct, IParser<T1>
        where TValue : struct, IParser<T2> => new(prefix, value);

    public static DelimitedParser<TOpen, TValue, TClose, T1, T2, T3> Delimited<TOpen, TValue, TClose, T1, T2, T3>(
        TOpen open,
        TValue value,
        TClose close)
        where TOpen : struct, IParser<T1>
        where TValue : struct, IParser<T2>
        where TClose : struct, IParser<T3> => new(open, value, close);

    public static PairParser<TFirst, TSecond, T1, T2> Pair<TFirst, TSecond, T1, T2>(
        TFirst first,
        TSecond second)
        where TFirst : struct, IParser<T1>
        where TSecond : struct, IParser<T2> => new(first, second);

    public static AltParser<TFirst, TSecond, T> Alt<TFirst, TSecond, T>(
        TFirst first,
        TSecond second)
        where TFirst : struct, IParser<T>
        where TSecond : struct, IParser<T> => new(first, second);

    public static Alt3Parser<TFirst, TSecond, TThird, T> Alt<TFirst, TSecond, TThird, T>(
        TFirst first,
        TSecond second,
        TThird third)
        where TFirst : struct, IParser<T>
        where TSecond : struct, IParser<T>
        where TThird : struct, IParser<T> => new(first, second, third);
}
