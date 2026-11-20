using System.Runtime.CompilerServices;

namespace SpanForge;

/// <summary>
/// Composable binary parser combinators combining the best ergonomics of Rust's
/// <c>nom</c>, <c>winnow</c>, and <c>binrw</c> with C# 15 zero-boxing <c>[Union]</c> results,
/// collection expression arguments (<c>[with(capacity: ...)]</c>), and labeled loop control.
/// </summary>
public static class Parse
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ParseResult<T> ResetAndReturn<T>(ref ByteCursor cursor, Checkpoint cp, ParseResult<T> result)
    {
        cursor.Reset(cp);
        return result;
    }

    // -------------------------------------------------------------------------
    // Branching, Optionality, Commit (Cut), Context, Peek, Recognize
    // -------------------------------------------------------------------------

    /// <summary>
    /// Escalates a recoverable <see cref="Backtrack"/> into a committed <see cref="Failure"/>,
    /// stopping outer <c>Alt</c> combinators from trying alternative branches.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Cut<T>(ref ByteCursor cursor, SpanParser<T> parser, string? context = null)
    {
        var result = parser(ref cursor);
        return context is null ? result.Cut() : result.Cut(context);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Context<T>(ref ByteCursor cursor, string context, SpanParser<T> parser) =>
        parser(ref cursor).WithContext(context);

    /// <summary>
    /// Optional parser: converts <see cref="Backtrack"/> into <c>Parsed(Option&lt;T&gt;.Nothing)</c>
    /// while preserving <see cref="Incomplete"/> and committed <see cref="Failure"/>.
    /// </summary>
    public static ParseResult<Option<T>> Opt<T>(ref ByteCursor cursor, SpanParser<T> parser)
    {
        var cp = cursor.Save();
        return parser(ref cursor) switch
        {
            Parsed<T>(var value) => new Parsed<Option<T>>(Option<T>.Of(value)),
            Incomplete inc => ResetAndReturn<Option<T>>(ref cursor, cp, inc),
            Backtrack => ResetAndReturn<Option<T>>(ref cursor, cp, new Parsed<Option<T>>(Option<T>.Nothing)),
            Failure fail => ResetAndReturn<Option<T>>(ref cursor, cp, fail),
        };
    }

    /// <summary>
    /// Ordered choice across 2 alternatives with O(1) checkpoint rollback on <see cref="Backtrack"/>.
    /// Committed <see cref="Failure"/> or streaming <see cref="Incomplete"/> short-circuits immediately.
    /// </summary>
    public static ParseResult<T> Alt<T>(ref ByteCursor cursor, SpanParser<T> first, SpanParser<T> second)
    {
        var cp = cursor.Save();
        var r1 = first(ref cursor);
        if (!r1.IsBacktrack)
        {
            if (!r1.IsParsed) cursor.Reset(cp);
            return r1;
        }

        cursor.Reset(cp);
        return second(ref cursor) switch
        {
            Parsed<T> ok => ok,
            Incomplete inc => ResetAndReturn<T>(ref cursor, cp, inc),
            Failure fail => ResetAndReturn<T>(ref cursor, cp, fail),
            Backtrack => ResetAndReturn<T>(ref cursor, cp, Backtrack.At(cp.Position, ErrorKind.Alt)),
        };
    }

    /// <summary>Ordered choice across 3 alternatives.</summary>
    public static ParseResult<T> Alt<T>(
        ref ByteCursor cursor,
        SpanParser<T> p1,
        SpanParser<T> p2,
        SpanParser<T> p3)
    {
        var cp = cursor.Save();

        var r1 = p1(ref cursor);
        if (!r1.IsBacktrack) { if (!r1.IsParsed) cursor.Reset(cp); return r1; }
        cursor.Reset(cp);

        var r2 = p2(ref cursor);
        if (!r2.IsBacktrack) { if (!r2.IsParsed) cursor.Reset(cp); return r2; }
        cursor.Reset(cp);

        return p3(ref cursor) switch
        {
            Parsed<T> ok => ok,
            Incomplete inc => ResetAndReturn<T>(ref cursor, cp, inc),
            Failure fail => ResetAndReturn<T>(ref cursor, cp, fail),
            Backtrack => ResetAndReturn<T>(ref cursor, cp, Backtrack.At(cp.Position, ErrorKind.Alt)),
        };
    }

    /// <summary>Ordered choice across 4 alternatives.</summary>
    public static ParseResult<T> Alt<T>(
        ref ByteCursor cursor,
        SpanParser<T> p1,
        SpanParser<T> p2,
        SpanParser<T> p3,
        SpanParser<T> p4)
    {
        var cp = cursor.Save();

        var r1 = p1(ref cursor);
        if (!r1.IsBacktrack) { if (!r1.IsParsed) cursor.Reset(cp); return r1; }
        cursor.Reset(cp);

        var r2 = p2(ref cursor);
        if (!r2.IsBacktrack) { if (!r2.IsParsed) cursor.Reset(cp); return r2; }
        cursor.Reset(cp);

        var r3 = p3(ref cursor);
        if (!r3.IsBacktrack) { if (!r3.IsParsed) cursor.Reset(cp); return r3; }
        cursor.Reset(cp);

        return p4(ref cursor) switch
        {
            Parsed<T> ok => ok,
            Incomplete inc => ResetAndReturn<T>(ref cursor, cp, inc),
            Failure fail => ResetAndReturn<T>(ref cursor, cp, fail),
            Backtrack => ResetAndReturn<T>(ref cursor, cp, Backtrack.At(cp.Position, ErrorKind.Alt)),
        };
    }

    /// <summary>Ordered choice across an arbitrary <see cref="ReadOnlySpan{T}"/> of parsers (params span in C# 13+).</summary>
    public static ParseResult<T> Alt<T>(ref ByteCursor cursor, params ReadOnlySpan<SpanParser<T>> parsers)
    {
        var cp = cursor.Save();
        for (int i = 0; i < parsers.Length; i++)
        {
            var r = parsers[i](ref cursor);
            if (!r.IsBacktrack)
            {
                if (!r.IsParsed) cursor.Reset(cp);
                return r;
            }

            cursor.Reset(cp);
        }

        return Backtrack.At(cp.Position, ErrorKind.Alt);
    }

    /// <summary>Runs <paramref name="parser"/> without advancing the cursor.</summary>
    public static ParseResult<T> Peek<T>(ref ByteCursor cursor, SpanParser<T> parser)
    {
        var cp = cursor.Save();
        var result = parser(ref cursor);
        cursor.Reset(cp);
        return result;
    }

    /// <summary>
    /// Runs <paramref name="parser"/> and returns the exact <see cref="ByteSlice"/> of input consumed
    /// (equivalent to <c>nom::combinator::recognize</c>).
    /// </summary>
    public static ParseResult<ByteSlice> Recognize<T>(ref ByteCursor cursor, SpanParser<T> parser)
    {
        var cp = cursor.Save();
        return parser(ref cursor) switch
        {
            Parsed<T> => new Parsed<ByteSlice>(new ByteSlice(cp.Position, cursor.Position - cp.Position)),
            Incomplete inc => ResetAndReturn<ByteSlice>(ref cursor, cp, inc),
            Backtrack err => ResetAndReturn<ByteSlice>(ref cursor, cp, err),
            Failure fail => ResetAndReturn<ByteSlice>(ref cursor, cp, fail),
        };
    }

    /// <summary>
    /// Runs <paramref name="parser"/> and returns both its parsed value and the consumed <see cref="ByteSlice"/>
    /// (equivalent to <c>winnow::Parser::with_recognized</c>).
    /// </summary>
    public static ParseResult<(T Value, ByteSlice Slice)> WithSlice<T>(ref ByteCursor cursor, SpanParser<T> parser)
    {
        var cp = cursor.Save();
        return parser(ref cursor) switch
        {
            Parsed<T>(var value) => new Parsed<(T, ByteSlice)>((value, new ByteSlice(cp.Position, cursor.Position - cp.Position))),
            Incomplete inc => ResetAndReturn<(T, ByteSlice)>(ref cursor, cp, inc),
            Backtrack err => ResetAndReturn<(T, ByteSlice)>(ref cursor, cp, err),
            Failure fail => ResetAndReturn<(T, ByteSlice)>(ref cursor, cp, fail),
        };
    }

    /// <summary>
    /// Runs <paramref name="parser"/> and validates the resulting value with <paramref name="predicate"/>,
    /// rolling back the cursor on validation failure.
    /// </summary>
    public static ParseResult<T> Verify<T>(
        ref ByteCursor cursor,
        SpanParser<T> parser,
        Func<T, bool> predicate,
        string? context = null)
    {
        var cp = cursor.Save();
        var result = parser(ref cursor);
        if (result.TryGetValue(out Parsed<T> ok) && !predicate(ok.Value))
        {
            cursor.Reset(cp);
            return Backtrack.At(cp.Position, ErrorKind.Verify, context);
        }

        if (!result.IsParsed)
        {
            cursor.Reset(cp);
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Sequencing Combinators
    // -------------------------------------------------------------------------

    public static ParseResult<TValue> Preceded<TPrefix, TValue>(
        ref ByteCursor cursor,
        SpanParser<TPrefix> prefix,
        SpanParser<TValue> value)
    {
        var cp = cursor.Save();
        var pRes = prefix(ref cursor);
        if (!pRes.IsParsed)
        {
            cursor.Reset(cp);
            return pRes.PropagateNonParsed<TValue>();
        }

        var result = value(ref cursor);
        if (!result.IsParsed) cursor.Reset(cp);
        return result;
    }

    public static ParseResult<TValue> Terminated<TValue, TSuffix>(
        ref ByteCursor cursor,
        SpanParser<TValue> value,
        SpanParser<TSuffix> suffix)
    {
        var cp = cursor.Save();
        var vRes = value(ref cursor);
        if (!vRes.TryGetValue(out Parsed<TValue> ok))
        {
            cursor.Reset(cp);
            return vRes;
        }

        var sRes = suffix(ref cursor);
        if (!sRes.IsParsed)
        {
            cursor.Reset(cp);
            return sRes.PropagateNonParsed<TValue>();
        }

        return ok;
    }

    public static ParseResult<TValue> Delimited<TOpen, TValue, TClose>(
        ref ByteCursor cursor,
        SpanParser<TOpen> open,
        SpanParser<TValue> value,
        SpanParser<TClose> close)
    {
        var cp = cursor.Save();
        var oRes = open(ref cursor);
        if (!oRes.IsParsed)
        {
            cursor.Reset(cp);
            return oRes.PropagateNonParsed<TValue>();
        }

        var vRes = value(ref cursor);
        if (!vRes.TryGetValue(out Parsed<TValue> ok))
        {
            cursor.Reset(cp);
            return vRes;
        }

        var cRes = close(ref cursor);
        if (!cRes.IsParsed)
        {
            cursor.Reset(cp);
            return cRes.PropagateNonParsed<TValue>();
        }

        return ok;
    }

    public static ParseResult<(T1 First, T2 Second)> Pair<T1, T2>(
        ref ByteCursor cursor,
        SpanParser<T1> first,
        SpanParser<T2> second)
    {
        var cp = cursor.Save();
        var r1 = first(ref cursor);
        if (!r1.TryGetValue(out Parsed<T1> p1))
        {
            cursor.Reset(cp);
            return r1.PropagateNonParsed<(T1, T2)>();
        }

        var r2 = second(ref cursor);
        if (!r2.TryGetValue(out Parsed<T2> p2))
        {
            cursor.Reset(cp);
            return r2.PropagateNonParsed<(T1, T2)>();
        }

        return new Parsed<(T1, T2)>((p1.Value, p2.Value));
    }

    public static ParseResult<(T1 First, T2 Second)> SeparatedPair<T1, TSep, T2>(
        ref ByteCursor cursor,
        SpanParser<T1> first,
        SpanParser<TSep> separator,
        SpanParser<T2> second)
    {
        var cp = cursor.Save();
        var r1 = first(ref cursor);
        if (!r1.TryGetValue(out Parsed<T1> p1))
        {
            cursor.Reset(cp);
            return r1.PropagateNonParsed<(T1, T2)>();
        }

        var sepRes = separator(ref cursor);
        if (!sepRes.IsParsed)
        {
            cursor.Reset(cp);
            return sepRes.PropagateNonParsed<(T1, T2)>();
        }

        var r2 = second(ref cursor);
        if (!r2.TryGetValue(out Parsed<T2> p2))
        {
            cursor.Reset(cp);
            return r2.PropagateNonParsed<(T1, T2)>();
        }

        return new Parsed<(T1, T2)>((p1.Value, p2.Value));
    }

    public static ParseResult<T> AllConsuming<T>(ref ByteCursor cursor, SpanParser<T> parser)
    {
        var cp = cursor.Save();
        var res = parser(ref cursor);
        if (!res.TryGetValue(out Parsed<T> ok))
        {
            cursor.Reset(cp);
            return res;
        }

        var eof = cursor.ExpectEof();
        if (!eof.IsParsed)
        {
            cursor.Reset(cp);
            return eof.PropagateNonParsed<T>();
        }

        return ok;
    }

    // -------------------------------------------------------------------------
    // Endianness Scoping & Framed Length-Prefixed Parsing (binrw + nom)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Temporarily overrides <see cref="ByteCursor.Endianness"/> for <paramref name="parser"/>
    /// and restores the previous endianness on exit (inspired by Rust's <c>binrw</c>).
    /// </summary>
    public static ParseResult<T> ScopedEndian<T>(
        ref ByteCursor cursor,
        Endianness endianness,
        SpanParser<T> parser)
    {
        var prev = cursor.Endianness;
        var cp = cursor.Save();
        cursor.Endianness = endianness;
        var result = parser(ref cursor);
        if (!result.IsParsed)
        {
            cursor.Reset(cp);
        }
        else
        {
            cursor.Endianness = prev;
        }

        return result;
    }

    /// <summary>
    /// Parses a byte length with <paramref name="lengthParser"/> and returns a zero-copy
    /// <see cref="ByteSlice"/> of that exact length (equivalent to <c>nom::multi::length_data</c>).
    /// </summary>
    public static ParseResult<ByteSlice> LengthSlice(
        ref ByteCursor cursor,
        SpanParser<int> lengthParser)
    {
        var cp = cursor.Save();
        var lenRes = lengthParser(ref cursor);
        if (!lenRes.TryGetValue(out Parsed<int> lenOk))
        {
            cursor.Reset(cp);
            return lenRes.PropagateNonParsed<ByteSlice>();
        }

        if (lenOk.Value < 0)
        {
            cursor.Reset(cp);
            return Backtrack.At(cp.Position, ErrorKind.LengthValue, "Negative frame length");
        }

        var sliceResult = cursor.Take(lenOk.Value);
        if (!sliceResult.IsParsed) cursor.Reset(cp);
        return sliceResult;
    }

    /// <summary>
    /// Parses a length-framed payload using a bounded sub-<see cref="ByteCursor"/> so the inner
    /// parser cannot read past the frame boundary (equivalent to <c>nom::multi::length_value</c>).
    /// </summary>
    public static ParseResult<T> LengthValue<T>(
        ref ByteCursor cursor,
        SpanParser<int> lengthParser,
        SpanParser<T> valueParser,
        bool requireFullConsumption = true)
    {
        var cp = cursor.Save();
        var sliceRes = LengthSlice(ref cursor, lengthParser);
        if (!sliceRes.TryGetValue(out Parsed<ByteSlice> frameOk))
        {
            cursor.Reset(cp);
            return sliceRes.PropagateNonParsed<T>();
        }

        var sub = cursor.CreateSubCursor(frameOk.Value, InputMode.Complete);
        return valueParser(ref sub) switch
        {
            Parsed<T>(var val) when !requireFullConsumption || sub.IsAtEnd => new Parsed<T>(val),
            Parsed<T> => ResetAndReturn<T>(
                ref cursor,
                cp,
                Backtrack.At(
                    sub.Position,
                    ErrorKind.LengthValue,
                    $"Unconsumed trailing bytes inside length-framed payload ({sub.RemainingLength} left)")),
            Incomplete inc => ResetAndReturn<T>(ref cursor, cp, inc),
            Backtrack err => ResetAndReturn<T>(ref cursor, cp, err),
            Failure fail => ResetAndReturn<T>(ref cursor, cp, fail),
        };
    }

    // -------------------------------------------------------------------------
    // Repetition & Zero-Allocation Fold / Span-Into Combinators
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses exactly <paramref name="count"/> items into a pre-sized <see cref="List{T}"/>
    /// using C# 15 collection expression arguments (<c>[with(capacity: count)]</c>).
    /// </summary>
    public static ParseResult<List<T>> Count<T>(ref ByteCursor cursor, int count, SpanParser<T> itemParser)
    {
        if (count < 0)
        {
            return Backtrack.At(cursor.Position, ErrorKind.Count, "Negative item count");
        }

        var cp = cursor.Save();
        List<T> items = [with(capacity: count)];

        for (int i = 0; i < count; i++)
        {
            var r = itemParser(ref cursor);
            if (!r.TryGetValue(out Parsed<T> ok))
            {
                cursor.Reset(cp);
                return r.PropagateNonParsed<List<T>>();
            }

            items.Add(ok.Value);
        }

        return new Parsed<List<T>>(items);
    }

    /// <summary>
    /// Parses zero or more occurrences of <paramref name="itemParser"/> until it backtracks.
    /// Guards against infinite loops if <paramref name="itemParser"/> succeeds without consuming bytes.
    /// </summary>
    public static ParseResult<List<T>> Many0<T>(ref ByteCursor cursor, SpanParser<T> itemParser, int initialCapacity = 4)
    {
        var cp = cursor.Save();
        List<T> items = [with(capacity: initialCapacity)];

        loop:
        while (true)
        {
            var iterCp = cursor.Save();
            switch (itemParser(ref cursor))
            {
                case Parsed<T>(var item):
                    if (cursor.Position == iterCp.Position)
                    {
                        cursor.Reset(cp);
                        return Failure.At(cursor.Position, ErrorKind.Custom, "Many0 inner parser made zero progress");
                    }

                    items.Add(item);
                    continue loop;

                case Backtrack:
                    cursor.Reset(iterCp);
                    break loop;

                case Incomplete inc:
                    cursor.Reset(cp);
                    return inc;

                case Failure fail:
                    cursor.Reset(cp);
                    return fail;
            }
        }

        return new Parsed<List<T>>(items);
    }

    public static ParseResult<List<T>> Many1<T>(ref ByteCursor cursor, SpanParser<T> itemParser, int initialCapacity = 4)
    {
        var cp = cursor.Save();
        return Many0(ref cursor, itemParser, initialCapacity) switch
        {
            Parsed<List<T>>(var list) when list.Count > 0 => new Parsed<List<T>>(list),
            Parsed<List<T>> => ResetAndReturn<List<T>>(ref cursor, cp, Backtrack.At(cp.Position, ErrorKind.Many1)),
            Incomplete inc => inc,
            Backtrack err => err,
            Failure fail => fail,
        };
    }

    /// <summary>
    /// Zero-allocation streaming fold over repeated parsed items (equivalent to <c>nom::multi::fold_many0</c>).
    /// Accumulates directly into <typeparamref name="TAcc"/> without allocating a <see cref="List{T}"/>.
    /// </summary>
    public static ParseResult<TAcc> FoldMany0<TItem, TAcc>(
        ref ByteCursor cursor,
        SpanParser<TItem> itemParser,
        TAcc seed,
        Func<TAcc, TItem, TAcc> folder)
    {
        var cp = cursor.Save();
        TAcc acc = seed;

        foldLoop:
        while (true)
        {
            var iterCp = cursor.Save();
            switch (itemParser(ref cursor))
            {
                case Parsed<TItem>(var item):
                    if (cursor.Position == iterCp.Position)
                    {
                        cursor.Reset(cp);
                        return Failure.At(cursor.Position, ErrorKind.Custom, "FoldMany0 inner parser made zero progress");
                    }

                    acc = folder(acc, item);
                    continue foldLoop;

                case Backtrack:
                    cursor.Reset(iterCp);
                    break foldLoop;

                case Incomplete inc:
                    cursor.Reset(cp);
                    return inc;

                case Failure fail:
                    cursor.Reset(cp);
                    return fail;
            }
        }

        return new Parsed<TAcc>(acc);
    }

    /// <summary>
    /// Zero-allocation fixed-capacity repetition directly into a caller-provided <see cref="Span{T}"/>
    /// (such as a <c>stackalloc</c> buffer). Returns the number of elements written.
    /// </summary>
    public static ParseResult<int> RepeatInto<T>(
        ref ByteCursor cursor,
        scoped Span<T> destination,
        SpanParser<T> itemParser,
        int minCount = 0)
    {
        var cp = cursor.Save();
        int written = 0;

        fillLoop:
        while (written < destination.Length)
        {
            var iterCp = cursor.Save();
            switch (itemParser(ref cursor))
            {
                case Parsed<T>(var item):
                    if (cursor.Position == iterCp.Position)
                    {
                        cursor.Reset(cp);
                        return Failure.At(cursor.Position, ErrorKind.Custom, "RepeatInto inner parser made zero progress");
                    }

                    destination[written++] = item;
                    continue fillLoop;

                case Backtrack:
                    cursor.Reset(iterCp);
                    break fillLoop;

                case Incomplete inc:
                    cursor.Reset(cp);
                    return inc;

                case Failure fail:
                    cursor.Reset(cp);
                    return fail;
            }
        }

        if (written < minCount)
        {
            cursor.Reset(cp);
            return Backtrack.At(cp.Position, ErrorKind.Count, $"Expected at least {minCount} items, got {written}");
        }

        return new Parsed<int>(written);
    }

    /// <summary>
    /// Parses zero or more items separated by <paramref name="separator"/>
    /// (equivalent to <c>nom::multi::separated_list0</c>).
    /// </summary>
    public static ParseResult<List<TItem>> Separated0<TItem, TSep>(
        ref ByteCursor cursor,
        SpanParser<TItem> itemParser,
        SpanParser<TSep> separator,
        int initialCapacity = 4)
    {
        var cp = cursor.Save();
        List<TItem> items = [with(capacity: initialCapacity)];

        var firstCp = cursor.Save();
        var rFirst = itemParser(ref cursor);
        if (rFirst.TryGetValue(out Parsed<TItem> firstOk))
        {
            items.Add(firstOk.Value);
        }
        else if (rFirst.IsBacktrack)
        {
            cursor.Reset(firstCp);
            return new Parsed<List<TItem>>(items);
        }
        else
        {
            cursor.Reset(cp);
            return rFirst.PropagateNonParsed<List<TItem>>();
        }

        sepLoop:
        while (true)
        {
            var sepCp = cursor.Save();
            var sepRes = separator(ref cursor);
            if (!sepRes.IsParsed)
            {
                if (sepRes.IsBacktrack)
                {
                    cursor.Reset(sepCp);
                    break sepLoop;
                }

                cursor.Reset(cp);
                return sepRes.PropagateNonParsed<List<TItem>>();
            }

            var itemRes = itemParser(ref cursor);
            if (itemRes.TryGetValue(out Parsed<TItem> nextOk))
            {
                items.Add(nextOk.Value);
                continue sepLoop;
            }

            if (itemRes.IsBacktrack)
            {
                cursor.Reset(sepCp);
                break sepLoop;
            }

            cursor.Reset(cp);
            return itemRes.PropagateNonParsed<List<TItem>>();
        }

        return new Parsed<List<TItem>>(items);
    }
}
