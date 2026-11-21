namespace SpanForge;

/// <summary>
/// VarInt wire encoding used with <see cref="VarIntAttribute"/>.
/// </summary>
public enum VarIntEncoding : byte
{
    /// <summary>Unsigned LEB128 (Protobuf / WebAssembly / DWARF) — up to 10 bytes for <see cref="ulong"/>.</summary>
    Leb128Unsigned = 0,

    /// <summary>Signed ZigZag-encoded LEB128 (<see cref="long"/>).</summary>
    Leb128SignedZigZag = 1,

    /// <summary>RFC 9000 QUIC 1/2/4/8-byte variable-length integer (<see cref="ulong"/>).</summary>
    Quic = 2,
}

/// <summary>
/// Wire width of the leading discriminant tag for <see cref="BinaryUnionAttribute"/> types.
/// </summary>
public enum TagWidth : byte
{
    /// <summary>1-byte unsigned tag (<see cref="byte"/>).</summary>
    U8 = 0,

    /// <summary>2-byte unsigned tag (<see cref="ushort"/>) using the union's default endianness.</summary>
    U16 = 1,

    /// <summary>4-byte unsigned tag (<see cref="uint"/>) using the union's default endianness.</summary>
    U32 = 2,
}

/// <summary>
/// Marks a <c>partial struct</c>, <c>partial record struct</c>, or <c>partial class</c> for
/// compile-time zero-allocation binary parser and writer generation (<c>Parse(ref ByteCursor)</c>,
/// <c>TryParse(ref ByteCursor, out T)</c>, <c>TryWrite(ref ByteWriter, ReadOnlySpan&lt;byte&gt;)</c>,
/// <c>TryWriteTo(Span&lt;byte&gt;, ReadOnlySpan&lt;byte&gt;)</c>, <c>WriteTo(IBufferWriter&lt;byte&gt;, ...)</c>,
/// <c>Parser : IParser&lt;T&gt;</c>, and <c>TryParser : ITryParser&lt;T&gt;</c>), inspired by Rust's <c>binrw</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class BinaryStructAttribute : Attribute
{
    public BinaryStructAttribute(Endianness defaultEndianness = Endianness.Big)
    {
        DefaultEndianness = defaultEndianness;
    }

    public Endianness DefaultEndianness { get; }
}

/// <summary>
/// Validates an exact leading magic byte sequence before decoding a <see cref="BinaryStructAttribute"/>
/// type or before decoding a specific field/property.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class MagicAttribute : Attribute
{
    public MagicAttribute(params byte[] bytes)
    {
        Bytes = bytes;
    }

    public byte[] Bytes { get; }
}

/// <summary>
/// Overrides byte order for a single numeric field or property inside a <see cref="BinaryStructAttribute"/> type.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class EndianAttribute : Attribute
{
    public EndianAttribute(Endianness endianness)
    {
        Endianness = endianness;
    }

    public Endianness Endianness { get; }
}

/// <summary>
/// Decodes an integer field/property as a variable-length integer (unsigned LEB128, signed ZigZag LEB128, or QUIC).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class VarIntAttribute : Attribute
{
    public VarIntAttribute(VarIntEncoding encoding = VarIntEncoding.Leb128Unsigned)
    {
        Encoding = encoding;
    }

    public VarIntEncoding Encoding { get; }
}

/// <summary>
/// Decodes a <see cref="ByteSlice"/> field/property as a length-prefixed byte slice.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class LengthPrefixedAttribute : Attribute
{
    public LengthPrefixedAttribute(LengthPrefix prefix = LengthPrefix.U16)
    {
        Prefix = prefix;
    }

    public LengthPrefix Prefix { get; }
}

/// <summary>
/// Decodes a <see cref="ByteSlice"/> field/property as an exact fixed-size byte slice.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class FixedBytesAttribute : Attribute
{
    public FixedBytesAttribute(int count)
    {
        Count = count;
    }

    public int Count { get; }
}

/// <summary>
/// Aligns the cursor position to a power-of-two byte boundary before decoding this field/property.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class AlignAttribute : Attribute
{
    public AlignAttribute(int alignment)
    {
        Alignment = alignment;
    }

    public int Alignment { get; }
}

/// <summary>
/// Skips a fixed number of padding bytes before decoding this field/property.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class PadAttribute : Attribute
{
    public PadAttribute(int bytes)
    {
        Bytes = bytes;
    }

    public int Bytes { get; }
}

/// <summary>
/// Decodes a field/property as an MSB-first bitfield of <see cref="Count"/> bits.
/// Consecutive <see cref="BitsAttribute"/> members are automatically coalesced into a single
/// zero-allocation <see cref="BitCursor"/> block and aligned back to a byte boundary on exit.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class BitsAttribute : Attribute
{
    public BitsAttribute(int count)
    {
        Count = count;
    }

    public int Count { get; }
}

/// <summary>
/// Verifies that a decoded integer field/property equals <see cref="Expected"/>;
/// rolls back and backtracks with <see cref="ErrorKind.Verify"/> if it does not match.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class AssertValueAttribute : Attribute
{
    public AssertValueAttribute(ulong expected)
    {
        Expected = expected;
    }

    public ulong Expected { get; }
}

/// <summary>
/// Marks a <c>partial struct</c> (typically <c>public readonly partial struct</c>) for compile-time
/// C# 15 custom <c>[Union]</c> discriminated-union generation with zero-allocation tag-dispatched
/// <c>Parse</c>, <c>TryParse</c>, <c>TryWrite</c>, <c>TryWriteTo</c>, and <c>WriteTo</c>.
/// Pair with one or more <see cref="UnionCaseAttribute"/> attributes.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class BinaryUnionAttribute : Attribute
{
    public BinaryUnionAttribute(
        Endianness defaultEndianness = Endianness.Big,
        TagWidth tagWidth = TagWidth.U8)
    {
        DefaultEndianness = defaultEndianness;
        TagWidth = tagWidth;
    }

    public Endianness DefaultEndianness { get; }
    public TagWidth TagWidth { get; }
}

/// <summary>
/// Declares a tagged variant case on a <see cref="BinaryUnionAttribute"/> type.
/// Each case must specify a distinct discriminant <see cref="Tag"/> and a distinct <see cref="PayloadType"/>
/// (either a primitive numeric/bool type, a <see cref="BinaryStructAttribute"/> type, or another <see cref="BinaryUnionAttribute"/> type).
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false, AllowMultiple = true)]
public sealed class UnionCaseAttribute : Attribute
{
    public UnionCaseAttribute(uint tag, Type payloadType)
    {
        Tag = tag;
        PayloadType = payloadType;
    }

    public uint Tag { get; }
    public Type PayloadType { get; }
}

