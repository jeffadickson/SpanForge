namespace SpanNom;

/// <summary>
/// Functional parser delegate operating on a mutable <see cref="ByteCursor"/>.
/// Non-capturing <c>static</c> lambdas are cached by the C# compiler for zero-allocation execution.
/// </summary>
public delegate ParseResult<T> SpanParser<T>(ref ByteCursor cursor);

/// <summary>
/// Zero-cost parser trait (analogous to Rust's <c>winnow::Parser</c> / <c>nom::Parser</c>).
/// Implement on <c>readonly struct</c> types so generic combinators monomorphize and inline cleanly.
/// </summary>
public interface IParser<T>
{
    ParseResult<T> Parse(ref ByteCursor cursor);
}
