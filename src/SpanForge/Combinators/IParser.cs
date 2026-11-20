namespace SpanForge;

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

/// <summary>
/// Register-friendly zero-cost parser trait parameterized by a value-type error <typeparamref name="TError"/>
/// (analogous to Rust's <c>winnow::Parser&lt;I, O, E&gt;</c> with <c>EmptyError</c> / <c>ContextError</c>).
/// </summary>
public interface IParser<T, TError>
    where TError : struct, IParseError<TError>
{
    ParseResult<T, TError> Parse(ref ByteCursor cursor);
}
