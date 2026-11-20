namespace SpanForge;

/// <summary>
/// Byte order used by endian-aware numeric readers on <see cref="ByteCursor"/>.
/// Inspired by Rust's <c>binrw</c> scoped endianness and <c>nom::number::Endianness</c>.
/// </summary>
public enum Endianness : byte
{
    /// <summary>Most significant byte first (network byte order).</summary>
    Big = 0,

    /// <summary>Least significant byte first (x86 / ARM64 native order).</summary>
    Little = 1,
}

/// <summary>
/// Controls how parsers handle running out of bytes, unifying Rust <c>nom::complete</c>
/// and <c>nom::streaming</c> (and <c>winnow::Partial</c>) into a single cursor abstraction.
/// </summary>
public enum InputMode : byte
{
    /// <summary>
    /// The buffer contains the entire input; running out of bytes produces a recoverable
    /// <see cref="Backtrack"/> with <see cref="ErrorKind.Eof"/>.
    /// </summary>
    Complete = 0,

    /// <summary>
    /// More bytes may arrive later; running out of bytes produces <see cref="Incomplete"/>
    /// with a <see cref="Needed"/> byte hint so callers can buffer more data and retry.
    /// </summary>
    Streaming = 1,
}

/// <summary>
/// Standard integer prefix encodings for length-prefixed binary frames.
/// </summary>
public enum LengthPrefix : byte
{
    U8,
    U16,
    U32,
    VarUInt64,
}
