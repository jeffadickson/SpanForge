using System.Runtime.CompilerServices;

namespace SpanForge;

/// <summary>
/// Monomorphized value-type parser combinators returning the register-sized <see cref="ParseResult{T, TError}"/>
/// (zero GC references when <typeparamref name="T"/> and <typeparamref name="TError"/> are unmanaged).
/// Mirrors Rust's <c>winnow::Parser&lt;I, O, EmptyError&gt;</c>.
/// </summary>
public static class FastParsers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ParseResult<T, TError> ResetAndReturn<T, TError>(
        ref ByteCursor cursor,
        Checkpoint cp,
        ParseResult<T, TError> result)
        where TError : struct, IParseError<TError>
    {
        cursor.Reset(cp);
        return result;
    }

    public readonly struct U8Parser<TError> : IParser<byte, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<byte, TError> Parse(ref ByteCursor cursor) => cursor.ReadU8<TError>();
    }

    public readonly struct U16Parser<TError> : IParser<ushort, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<ushort, TError> Parse(ref ByteCursor cursor) => cursor.ReadU16<TError>();
    }

    public readonly struct U16BEParser<TError> : IParser<ushort, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<ushort, TError> Parse(ref ByteCursor cursor) => cursor.ReadU16BE<TError>();
    }

    public readonly struct U32Parser<TError> : IParser<uint, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<uint, TError> Parse(ref ByteCursor cursor) => cursor.ReadU32<TError>();
    }

    public readonly struct U64Parser<TError> : IParser<ulong, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<ulong, TError> Parse(ref ByteCursor cursor) => cursor.ReadU64<TError>();
    }

    public readonly struct F64Parser<TError> : IParser<double, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<double, TError> Parse(ref ByteCursor cursor) => cursor.ReadF64<TError>();
    }

    public readonly struct VarUInt64Parser<TError> : IParser<ulong, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<ulong, TError> Parse(ref ByteCursor cursor) => cursor.ReadVarUInt64<TError>();
    }

    public readonly struct TagByteParser<TError>(byte expected) : IParser<byte, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<byte, TError> Parse(ref ByteCursor cursor) => cursor.TagByte<TError>(expected);
    }

    public readonly struct TakeParser<TError>(int count) : IParser<ByteSlice, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<ByteSlice, TError> Parse(ref ByteCursor cursor) => cursor.Take<TError>(count);
    }

    public readonly struct LengthPrefixedParser<TError>(LengthPrefix prefix) : IParser<ByteSlice, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<ByteSlice, TError> Parse(ref ByteCursor cursor) => cursor.ReadLengthPrefixedBytes<TError>(prefix);
    }

    public readonly struct CutParser<TParser, T, TError>(TParser inner) : IParser<T, TError>
        where TParser : struct, IParser<T, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<T, TError> Parse(ref ByteCursor cursor) => inner.Parse(ref cursor).Cut();
    }

    public readonly struct OptParser<TParser, T, TError>(TParser inner) : IParser<Option<T>, TError>
        where TParser : struct, IParser<T, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<Option<T>, TError> Parse(ref ByteCursor cursor)
        {
            var cp = cursor.Save();
            return inner.Parse(ref cursor) switch
            {
                Parsed<T>(var val) => new Parsed<Option<T>>(Option<T>.Of(val)),
                Backtrack<TError> => ResetAndReturn<Option<T>, TError>(ref cursor, cp, new Parsed<Option<T>>(Option<T>.Nothing)),
                Incomplete inc => ResetAndReturn<Option<T>, TError>(ref cursor, cp, inc),
                Failure<TError> fail => ResetAndReturn<Option<T>, TError>(ref cursor, cp, fail),
            };
        }
    }

    public readonly struct PrecededParser<TPrefix, TValue, T1, T2, TError>(TPrefix prefix, TValue value) : IParser<T2, TError>
        where TPrefix : struct, IParser<T1, TError>
        where TValue : struct, IParser<T2, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<T2, TError> Parse(ref ByteCursor cursor)
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

    public readonly struct DelimitedParser<TOpen, TValue, TClose, T1, T2, T3, TError>(
        TOpen open,
        TValue value,
        TClose close) : IParser<T2, TError>
        where TOpen : struct, IParser<T1, TError>
        where TValue : struct, IParser<T2, TError>
        where TClose : struct, IParser<T3, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<T2, TError> Parse(ref ByteCursor cursor)
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

    public readonly struct PairParser<TFirst, TSecond, T1, T2, TError>(TFirst first, TSecond second) : IParser<(T1, T2), TError>
        where TFirst : struct, IParser<T1, TError>
        where TSecond : struct, IParser<T2, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<(T1, T2), TError> Parse(ref ByteCursor cursor)
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

    public readonly struct AltParser<TFirst, TSecond, T, TError>(TFirst first, TSecond second) : IParser<T, TError>
        where TFirst : struct, IParser<T, TError>
        where TSecond : struct, IParser<T, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<T, TError> Parse(ref ByteCursor cursor)
        {
            var cp = cursor.Save();
            var r1 = first.Parse(ref cursor);
            if (!r1.IsBacktrack)
            {
                if (!r1.IsParsed) cursor.Reset(cp);
                return r1;
            }

            cursor.Reset(cp);
            var r2 = second.Parse(ref cursor);
            if (r2.IsParsed) return r2;
            cursor.Reset(cp);
            return r2.IsBacktrack
                ? new Backtrack<TError>(TError.Create(cp.Position, ErrorKind.Alt))
                : r2;
        }
    }

    public readonly struct Alt3Parser<TFirst, TSecond, TThird, T, TError>(TFirst first, TSecond second, TThird third) : IParser<T, TError>
        where TFirst : struct, IParser<T, TError>
        where TSecond : struct, IParser<T, TError>
        where TThird : struct, IParser<T, TError>
        where TError : struct, IParseError<TError>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<T, TError> Parse(ref ByteCursor cursor)
        {
            var cp = cursor.Save();
            var r1 = first.Parse(ref cursor);
            if (!r1.IsBacktrack) { if (!r1.IsParsed) cursor.Reset(cp); return r1; }
            cursor.Reset(cp);

            var r2 = second.Parse(ref cursor);
            if (!r2.IsBacktrack) { if (!r2.IsParsed) cursor.Reset(cp); return r2; }
            cursor.Reset(cp);

            var r3 = third.Parse(ref cursor);
            if (r3.IsParsed) return r3;
            cursor.Reset(cp);
            return r3.IsBacktrack
                ? new Backtrack<TError>(TError.Create(cp.Position, ErrorKind.Alt))
                : r3;
        }
    }

    public static U8Parser<TError> U8<TError>() where TError : struct, IParseError<TError> => default;
    public static U16Parser<TError> U16<TError>() where TError : struct, IParseError<TError> => default;
    public static U16BEParser<TError> U16BE<TError>() where TError : struct, IParseError<TError> => default;
    public static U32Parser<TError> U32<TError>() where TError : struct, IParseError<TError> => default;
    public static U64Parser<TError> U64<TError>() where TError : struct, IParseError<TError> => default;
    public static F64Parser<TError> F64<TError>() where TError : struct, IParseError<TError> => default;
    public static VarUInt64Parser<TError> VarUInt64<TError>() where TError : struct, IParseError<TError> => default;
    public static TagByteParser<TError> TagByte<TError>(byte b) where TError : struct, IParseError<TError> => new(b);
    public static TakeParser<TError> Take<TError>(int count) where TError : struct, IParseError<TError> => new(count);
    public static LengthPrefixedParser<TError> LengthPrefixed<TError>(LengthPrefix prefix) where TError : struct, IParseError<TError> => new(prefix);

    public static CutParser<TParser, T, TError> Cut<TParser, T, TError>(TParser inner)
        where TParser : struct, IParser<T, TError>
        where TError : struct, IParseError<TError> => new(inner);

    public static OptParser<TParser, T, TError> Opt<TParser, T, TError>(TParser inner)
        where TParser : struct, IParser<T, TError>
        where TError : struct, IParseError<TError> => new(inner);

    public static PrecededParser<TPrefix, TValue, T1, T2, TError> Preceded<TPrefix, TValue, T1, T2, TError>(
        TPrefix prefix,
        TValue value)
        where TPrefix : struct, IParser<T1, TError>
        where TValue : struct, IParser<T2, TError>
        where TError : struct, IParseError<TError> => new(prefix, value);

    public static DelimitedParser<TOpen, TValue, TClose, T1, T2, T3, TError> Delimited<TOpen, TValue, TClose, T1, T2, T3, TError>(
        TOpen open,
        TValue value,
        TClose close)
        where TOpen : struct, IParser<T1, TError>
        where TValue : struct, IParser<T2, TError>
        where TClose : struct, IParser<T3, TError>
        where TError : struct, IParseError<TError> => new(open, value, close);

    public static PairParser<TFirst, TSecond, T1, T2, TError> Pair<TFirst, TSecond, T1, T2, TError>(
        TFirst first,
        TSecond second)
        where TFirst : struct, IParser<T1, TError>
        where TSecond : struct, IParser<T2, TError>
        where TError : struct, IParseError<TError> => new(first, second);

    public static AltParser<TFirst, TSecond, T, TError> Alt<TFirst, TSecond, T, TError>(
        TFirst first,
        TSecond second)
        where TFirst : struct, IParser<T, TError>
        where TSecond : struct, IParser<T, TError>
        where TError : struct, IParseError<TError> => new(first, second);

    public static Alt3Parser<TFirst, TSecond, TThird, T, TError> Alt<TFirst, TSecond, TThird, T, TError>(
        TFirst first,
        TSecond second,
        TThird third)
        where TFirst : struct, IParser<T, TError>
        where TSecond : struct, IParser<T, TError>
        where TThird : struct, IParser<T, TError>
        where TError : struct, IParseError<TError> => new(first, second, third);
}
