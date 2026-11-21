using System.Buffers.Text;

namespace SpanForge.Protocols.Resp;

/// <summary>
/// C# 15 <c>closed</c> record hierarchy for recursive Redis RESP2 values.
/// Demonstrates the idiomatic C# 15 pairing: <c>union</c> for flat sum types, and <c>closed</c>
/// hierarchies for recursive ASTs — both with compiler-enforced exhaustive <c>switch</c> expressions.
/// </summary>
public closed record class RespValue;

public sealed record class RespSimpleString(ByteSlice Value) : RespValue;
public sealed record class RespError(ByteSlice Message) : RespValue;
public sealed record class RespInteger(long Value) : RespValue;
public sealed record class RespBulkString(ByteSlice Value) : RespValue;
public sealed record class RespNull : RespValue
{
    public static RespNull Instance { get; } = new();
}

public sealed record class RespArray(IReadOnlyList<RespValue> Items) : RespValue;

/// <summary>
/// Redis RESP2 protocol parser built with <c>Parse.Alt</c>, <see cref="Parse.Cut{T}(ref ByteCursor, SpanParser{T}, string?)"/>,
/// zero-copy <see cref="ByteSlice"/> strings, and C# 15 collection expression capacity arguments.
/// </summary>
public static class RespParser
{
    public static ParseResult<RespValue> ParseValue(ref ByteCursor cursor) =>
        Parse.Alt(
            ref cursor,
            ParseSimpleString,
            ParseError,
            ParseInteger,
            ParseBulkString,
            ParseArray);

    private static ParseResult<RespValue> ParseSimpleString(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        var tag = cursor.TagByte((byte)'+');
        if (!tag.IsParsed) return tag.PropagateNonParsed<RespValue>();

        // Once '+' matched, we are committed to SimpleString (Cut!).
        return cursor.TakeCrlfLine().Cut("RESP simple string").Map(static s => (RespValue)new RespSimpleString(s)) switch
        {
            Parsed<RespValue> ok => ok,
            Incomplete inc => ResetAndReturn(ref cursor, cp, inc),
            Backtrack err => ResetAndReturn(ref cursor, cp, err),
            Failure fail => ResetAndReturn(ref cursor, cp, fail),
        };
    }

    private static ParseResult<RespValue> ParseError(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        var tag = cursor.TagByte((byte)'-');
        if (!tag.IsParsed) return tag.PropagateNonParsed<RespValue>();

        return cursor.TakeCrlfLine().Cut("RESP error").Map(static s => (RespValue)new RespError(s)) switch
        {
            Parsed<RespValue> ok => ok,
            Incomplete inc => ResetAndReturn(ref cursor, cp, inc),
            Backtrack err => ResetAndReturn(ref cursor, cp, err),
            Failure fail => ResetAndReturn(ref cursor, cp, fail),
        };
    }

    private static ParseResult<RespValue> ParseInteger(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        var tag = cursor.TagByte((byte)':');
        if (!tag.IsParsed) return tag.PropagateNonParsed<RespValue>();

        var lineRes = cursor.TakeCrlfLine().Cut("RESP integer line");
        if (!lineRes.TryGetValue(out Parsed<ByteSlice> lineOk))
        {
            cursor.Reset(cp);
            return lineRes.PropagateNonParsed<RespValue>();
        }

        ReadOnlySpan<byte> digits = cursor[lineOk.Value];
        if (!Utf8Parser.TryParse(digits, out long value, out int consumed) || consumed != digits.Length)
        {
            cursor.Reset(cp);
            return Failure.At(lineOk.Value.Offset, ErrorKind.Protocol, "Invalid RESP ASCII integer");
        }

        return new Parsed<RespValue>(new RespInteger(value));
    }

    private static ParseResult<RespValue> ParseBulkString(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        var tag = cursor.TagByte((byte)'$');
        if (!tag.IsParsed) return tag.PropagateNonParsed<RespValue>();

        var lenLineRes = cursor.TakeCrlfLine().Cut("RESP bulk string length");
        if (!lenLineRes.TryGetValue(out Parsed<ByteSlice> lenLineOk))
        {
            cursor.Reset(cp);
            return lenLineRes.PropagateNonParsed<RespValue>();
        }

        ReadOnlySpan<byte> lenBytes = cursor[lenLineOk.Value];
        if (!Utf8Parser.TryParse(lenBytes, out int length, out int consumed) || consumed != lenBytes.Length)
        {
            cursor.Reset(cp);
            return Failure.At(lenLineOk.Value.Offset, ErrorKind.Protocol, "Invalid RESP bulk string length");
        }

        if (length == -1)
        {
            return new Parsed<RespValue>(RespNull.Instance);
        }

        if (length < 0)
        {
            cursor.Reset(cp);
            return Failure.At(lenLineOk.Value.Offset, ErrorKind.LengthValue, $"Invalid negative RESP bulk length {length}");
        }

        var dataRes = cursor.Take(length).Cut("RESP bulk string payload");
        if (!dataRes.TryGetValue(out Parsed<ByteSlice> dataOk))
        {
            cursor.Reset(cp);
            return dataRes.PropagateNonParsed<RespValue>();
        }

        var crlfRes = cursor.Tag("\r\n"u8).Cut("RESP bulk string CRLF terminator");
        if (!crlfRes.IsParsed)
        {
            cursor.Reset(cp);
            return crlfRes.PropagateNonParsed<RespValue>();
        }

        return new Parsed<RespValue>(new RespBulkString(dataOk.Value));
    }

    private static ParseResult<RespValue> ParseArray(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        var tag = cursor.TagByte((byte)'*');
        if (!tag.IsParsed) return tag.PropagateNonParsed<RespValue>();

        var countLineRes = cursor.TakeCrlfLine().Cut("RESP array count");
        if (!countLineRes.TryGetValue(out Parsed<ByteSlice> countLineOk))
        {
            cursor.Reset(cp);
            return countLineRes.PropagateNonParsed<RespValue>();
        }

        ReadOnlySpan<byte> countBytes = cursor[countLineOk.Value];
        if (!Utf8Parser.TryParse(countBytes, out int count, out int consumed) || consumed != countBytes.Length)
        {
            cursor.Reset(cp);
            return Failure.At(countLineOk.Value.Offset, ErrorKind.Protocol, "Invalid RESP array length");
        }

        if (count == -1)
        {
            return new Parsed<RespValue>(RespNull.Instance);
        }

        if (count < 0)
        {
            cursor.Reset(cp);
            return Failure.At(countLineOk.Value.Offset, ErrorKind.Count, $"Invalid negative RESP array count {count}");
        }

        // Pre-sized List using C# 15 collection expression arguments via Parse.Count!
        var itemsRes = Parse.Count(ref cursor, count, ParseValue).Cut("RESP array elements");
        if (!itemsRes.TryGetValue(out Parsed<List<RespValue>> itemsOk))
        {
            cursor.Reset(cp);
            return itemsRes.PropagateNonParsed<RespValue>();
        }

        return new Parsed<RespValue>(new RespArray(itemsOk.Value));
    }

    private static ParseResult<RespValue> ResetAndReturn(
        ref ByteCursor cursor,
        Checkpoint cp,
        ParseResult<RespValue> result)
    {
        cursor.Reset(cp);
        return result;
    }

    /// <summary>
    /// Exhaustively formats any <see cref="RespValue"/> — no default arm required thanks to C# 15 <c>closed</c>!
    /// </summary>
    public static string Format(RespValue value, ReadOnlySpan<byte> source) => value switch
    {
        RespSimpleString(var s) => $"+{s.AsUtf8String(source)}",
        RespError(var e) => $"-{e.AsUtf8String(source)}",
        RespInteger(var n) => $":{n}",
        RespBulkString(var b) => $"\"{b.AsUtf8String(source)}\"",
        RespNull => "null",
        RespArray(var items) => FormatArray(items, source),
    };

    private static string FormatArray(IReadOnlyList<RespValue> items, ReadOnlySpan<byte> source)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Format(items[i], source));
        }

        sb.Append(']');
        return sb.ToString();
    }
}
