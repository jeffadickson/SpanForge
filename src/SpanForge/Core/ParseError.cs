using System.Text;

namespace SpanForge;

/// <summary>
/// Categorizes binary and combinator parse errors, inspired by <c>nom::error::ErrorKind</c>.
/// </summary>
public enum ErrorKind : byte
{
    None = 0,
    Eof,
    Tag,
    Take,
    TakeWhile,
    TakeUntil,
    Verify,
    Alt,
    Map,
    LengthValue,
    Count,
    Many1,
    VarInt,
    BitRead,
    Alignment,
    CrcMismatch,
    InvalidUtf8,
    ExpectedEof,
    Protocol,
    Custom,
}

/// <summary>
/// Immutable breadcrumb node for chained error context (allocated only on explicit <c>WithContext</c>
/// wrapping, keeping hot-path backtracking completely allocation-free).
/// </summary>
public sealed record class ParseErrorChain(
    int Offset,
    ErrorKind Kind,
    string? Context,
    ParseErrorChain? Cause = null);

/// <summary>
/// Lightweight value-type error payload stored inline inside <see cref="ParseResult{T}"/>.
/// </summary>
public readonly record struct ParseError(
    int Offset,
    ErrorKind Kind,
    string? Context = null,
    ParseErrorChain? Cause = null)
{
    public ParseError WithContext(string context, int? offset = null)
    {
        var chain = new ParseErrorChain(Offset, Kind, Context, Cause);
        return new ParseError(offset ?? Offset, Kind, context, chain);
    }

    public override string ToString() =>
        Context is null
            ? $"{Kind} at byte offset {Offset}"
            : $"{Kind} at byte offset {Offset}: {Context}";

    /// <summary>
    /// Formats a rich diagnostic report including context breadcrumbs and a hex+ASCII dump
    /// with a caret pointing directly to the failing byte offset.
    /// </summary>
    public string FormatDiagnostics(ReadOnlySpan<byte> input, int windowBytes = 16)
    {
        var sb = new StringBuilder(256);
        sb.AppendLine($"Parse error: {this}");

        var current = Cause;
        while (current is not null)
        {
            sb.Append("  caused by: ")
              .Append(current.Kind)
              .Append(" at byte offset ")
              .Append(current.Offset);
            if (current.Context is not null)
            {
                sb.Append(": ").Append(current.Context);
            }

            sb.AppendLine();
            current = current.Cause;
        }

        if (input.Length == 0)
        {
            sb.AppendLine("  <empty input>");
            return sb.ToString();
        }

        int clampedOffset = Math.Clamp(Offset, 0, input.Length);
        int start = Math.Max(0, (clampedOffset / windowBytes) * windowBytes);
        int count = Math.Min(windowBytes, input.Length - start);

        sb.Append($"  0x{start:X4} | ");
        for (int i = 0; i < count; i++)
        {
            sb.Append($"{input[start + i]:X2} ");
        }

        if (count < windowBytes)
        {
            sb.Append(' ', (windowBytes - count) * 3);
        }

        sb.Append("| ");
        for (int i = 0; i < count; i++)
        {
            byte b = input[start + i];
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
        }

        sb.AppendLine();

        int caretColumn = clampedOffset - start;
        sb.Append("         | ");
        sb.Append(' ', caretColumn * 3);
        sb.Append(clampedOffset == input.Length ? "^ (EOF)" : "^^");
        return sb.ToString();
    }
}
