using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SpanForge.Protocols.Protobuf;

/// <summary>Protocol Buffers binary wire types (proto2/proto3).</summary>
public enum ProtobufWireType : byte
{
    VarInt = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    StartGroup = 3,
    EndGroup = 4,
    Fixed32 = 5,
}

public readonly record struct ProtobufVarIntField(uint FieldNumber, ulong Value);
public readonly record struct ProtobufFixed64Field(uint FieldNumber, ulong Bits);
public readonly record struct ProtobufLengthDelimitedField(uint FieldNumber, ByteSlice Payload);
public readonly record struct ProtobufFixed32Field(uint FieldNumber, uint Bits);

/// <summary>
/// Compact 16-byte zero-boxing C# 15 custom <c>[Union]</c> representing any decoded Protocol Buffers
/// wire-format field (<see cref="ProtobufVarIntField"/> | <see cref="ProtobufFixed64Field"/> |
/// <see cref="ProtobufLengthDelimitedField"/> | <see cref="ProtobufFixed32Field"/>).
/// </summary>
[Union]
public readonly struct ProtobufField : IUnion
{
    private readonly ulong _data;       // VarInt value, Fixed64 bits, packed ByteSlice, or Fixed32 bits
    private readonly uint _fieldNumber;
    private readonly byte _tag;         // 1=VarInt, 2=Fixed64, 3=LengthDelimited, 4=Fixed32

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtobufField(ProtobufVarIntField field)
    {
        _tag = 1;
        _fieldNumber = field.FieldNumber;
        _data = field.Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtobufField(ProtobufFixed64Field field)
    {
        _tag = 2;
        _fieldNumber = field.FieldNumber;
        _data = field.Bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtobufField(ProtobufLengthDelimitedField field)
    {
        _tag = 3;
        _fieldNumber = field.FieldNumber;
        _data = ((ulong)(uint)field.Payload.Offset << 32) | (uint)field.Payload.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtobufField(ProtobufFixed32Field field)
    {
        _tag = 4;
        _fieldNumber = field.FieldNumber;
        _data = field.Bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ByteSlice UnpackSlice() => new((int)(_data >> 32), (int)(uint)_data);

    public uint FieldNumber => _fieldNumber;

    public object? Value => _tag switch
    {
        1 => new ProtobufVarIntField(_fieldNumber, _data),
        2 => new ProtobufFixed64Field(_fieldNumber, _data),
        3 => new ProtobufLengthDelimitedField(_fieldNumber, UnpackSlice()),
        4 => new ProtobufFixed32Field(_fieldNumber, (uint)_data),
        _ => null,
    };

    public bool HasValue => _tag != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out ProtobufVarIntField value)
    {
        if (_tag == 1) { value = new ProtobufVarIntField(_fieldNumber, _data); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out ProtobufFixed64Field value)
    {
        if (_tag == 2) { value = new ProtobufFixed64Field(_fieldNumber, _data); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out ProtobufLengthDelimitedField value)
    {
        if (_tag == 3) { value = new ProtobufLengthDelimitedField(_fieldNumber, UnpackSlice()); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out ProtobufFixed32Field value)
    {
        if (_tag == 4) { value = new ProtobufFixed32Field(_fieldNumber, (uint)_data); return true; }
        Unsafe.SkipInit(out value); return false;
    }
}

/// <summary>
/// Zero-allocation Google Protocol Buffers binary wire-format parser supporting streaming
/// <see cref="Incomplete"/> decoding, ZigZag decoding, packed repeated fields, and both
/// <see cref="ParseField"/> (<see cref="ParseResult{T}"/>) and <see cref="TryParseField"/> (<see cref="FastStatus"/>).
/// </summary>
public static class ProtobufParser
{
    public const uint MaxFieldNumber = 536_870_911u; // (1 << 29) - 1 per Protobuf spec

    /// <summary>Decodes a 32-bit ZigZag-encoded signed integer (<c>sint32</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeZigZag32(uint raw) => (int)((raw >> 1) ^ (uint)-(int)(raw & 1u));

    /// <summary>Encodes a 32-bit signed integer with ZigZag (<c>sint32</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint EncodeZigZag32(int value) => (uint)((value << 1) ^ (value >> 31));

    /// <summary>Decodes a 64-bit ZigZag-encoded signed integer (<c>sint64</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long DecodeZigZag64(ulong raw) => (long)((raw >> 1) ^ (ulong)-(long)(raw & 1UL));

    /// <summary>Encodes a 64-bit signed integer with ZigZag (<c>sint64</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong EncodeZigZag64(long value) => (ulong)((value << 1) ^ (value >> 63));

    /// <summary>
    /// Parses one Protocol Buffers wire-format field into a zero-boxing <see cref="ProtobufField"/> union.
    /// Rolls back the cursor automatically on incomplete or invalid data.
    /// </summary>
    public static ParseResult<ProtobufField> ParseField(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        var tagRes = cursor.ReadVarUInt64();
        if (!tagRes.TryGetValue(out Parsed<ulong> tagOk))
        {
            cursor.Reset(cp);
            return tagRes.PropagateNonParsed<ProtobufField>();
        }

        ulong rawTag = tagOk.Value;
        ulong fieldNum64 = rawTag >> 3;
        if (fieldNum64 == 0 || fieldNum64 > MaxFieldNumber)
        {
            cursor.Reset(cp);
            return Failure.At(cp.Position, ErrorKind.Protocol, $"Invalid Protobuf field number {fieldNum64}");
        }

        uint fieldNumber = (uint)fieldNum64;
        var wireType = (ProtobufWireType)(rawTag & 0x7UL);

        switch (wireType)
        {
            case ProtobufWireType.VarInt:
            {
                var vRes = cursor.ReadVarUInt64();
                if (!vRes.TryGetValue(out Parsed<ulong> vOk))
                {
                    cursor.Reset(cp);
                    return vRes.PropagateNonParsed<ProtobufField>();
                }

                return new Parsed<ProtobufField>(new ProtobufVarIntField(fieldNumber, vOk.Value));
            }

            case ProtobufWireType.Fixed64:
            {
                var fRes = cursor.ReadU64LE();
                if (!fRes.TryGetValue(out Parsed<ulong> fOk))
                {
                    cursor.Reset(cp);
                    return fRes.PropagateNonParsed<ProtobufField>();
                }

                return new Parsed<ProtobufField>(new ProtobufFixed64Field(fieldNumber, fOk.Value));
            }

            case ProtobufWireType.LengthDelimited:
            {
                var lRes = cursor.ReadVarUInt64();
                if (!lRes.TryGetValue(out Parsed<ulong> lOk))
                {
                    cursor.Reset(cp);
                    return lRes.PropagateNonParsed<ProtobufField>();
                }

                if (lOk.Value > int.MaxValue)
                {
                    cursor.Reset(cp);
                    return Failure.At(cp.Position, ErrorKind.LengthValue, "Protobuf length-delimited field exceeds 2 GiB");
                }

                var sliceRes = cursor.Take((int)lOk.Value);
                if (!sliceRes.TryGetValue(out Parsed<ByteSlice> sOk))
                {
                    cursor.Reset(cp);
                    return sliceRes.PropagateNonParsed<ProtobufField>();
                }

                return new Parsed<ProtobufField>(new ProtobufLengthDelimitedField(fieldNumber, sOk.Value));
            }

            case ProtobufWireType.Fixed32:
            {
                var fRes = cursor.ReadU32LE();
                if (!fRes.TryGetValue(out Parsed<uint> fOk))
                {
                    cursor.Reset(cp);
                    return fRes.PropagateNonParsed<ProtobufField>();
                }

                return new Parsed<ProtobufField>(new ProtobufFixed32Field(fieldNumber, fOk.Value));
            }

            default:
                cursor.Reset(cp);
                return Failure.At(cp.Position, ErrorKind.Protocol, $"Unsupported/invalid Protobuf wire type {(byte)wireType}");
        }
    }

    /// <summary>
    /// High-throughput zero-spill <see cref="FastStatus"/> entry point for decoding one Protobuf wire field.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FastStatus TryParseField(ref ByteCursor cursor, out ProtobufField field)
    {
        int start = cursor.Position;
        var tagSt = cursor.TryReadVarUInt64(out ulong rawTag);
        if (!tagSt.IsParsed)
        {
            Unsafe.SkipInit(out field);
            return tagSt;
        }

        ulong fieldNum64 = rawTag >> 3;
        if (fieldNum64 == 0 || fieldNum64 > MaxFieldNumber)
        {
            cursor.Position = start;
            Unsafe.SkipInit(out field);
            return FastStatus.Failure;
        }

        uint fieldNumber = (uint)fieldNum64;
        uint wireType = (uint)(rawTag & 0x7UL);

        if (wireType == (uint)ProtobufWireType.VarInt)
        {
            var vSt = cursor.TryReadVarUInt64(out ulong val);
            if (!vSt.IsParsed) { cursor.Position = start; Unsafe.SkipInit(out field); return vSt; }
            field = new ProtobufVarIntField(fieldNumber, val);
            return FastStatus.Parsed;
        }

        if (wireType == (uint)ProtobufWireType.LengthDelimited)
        {
            var lSt = cursor.TryReadVarUInt64(out ulong len64);
            if (!lSt.IsParsed) { cursor.Position = start; Unsafe.SkipInit(out field); return lSt; }
            if (len64 > int.MaxValue) { cursor.Position = start; Unsafe.SkipInit(out field); return FastStatus.Failure; }
            int len = (int)len64;
            int pos = cursor.Position;
            int rem = cursor.Buffer.Length - pos;
            if (rem < len)
            {
                cursor.Position = start;
                Unsafe.SkipInit(out field);
                return cursor.Mode == InputMode.Streaming
                    ? FastStatus.IncompleteBytes(len - rem)
                    : FastStatus.Backtrack;
            }

            cursor.Position = pos + len;
            field = new ProtobufLengthDelimitedField(fieldNumber, new ByteSlice(pos, len));
            return FastStatus.Parsed;
        }

        if (wireType == (uint)ProtobufWireType.Fixed64)
        {
            var fRes = cursor.ReadU64LE();
            if (fRes.TryGetValue(out Parsed<ulong> ok))
            {
                field = new ProtobufFixed64Field(fieldNumber, ok.Value);
                return FastStatus.Parsed;
            }

            cursor.Position = start;
            Unsafe.SkipInit(out field);
            return fRes.TryGetValue(out Incomplete inc) && inc.Needed.TryGetValue(out ExactBytes ex)
                ? FastStatus.IncompleteBytes(ex.Count)
                : FastStatus.Backtrack;
        }

        if (wireType == (uint)ProtobufWireType.Fixed32)
        {
            var fRes = cursor.ReadU32LE();
            if (fRes.TryGetValue(out Parsed<uint> ok))
            {
                field = new ProtobufFixed32Field(fieldNumber, ok.Value);
                return FastStatus.Parsed;
            }

            cursor.Position = start;
            Unsafe.SkipInit(out field);
            return fRes.TryGetValue(out Incomplete inc) && inc.Needed.TryGetValue(out ExactBytes ex)
                ? FastStatus.IncompleteBytes(ex.Count)
                : FastStatus.Backtrack;
        }

        cursor.Position = start;
        Unsafe.SkipInit(out field);
        return FastStatus.Failure;
    }

    /// <summary>
    /// Decodes a packed repeated varint payload directly into <paramref name="destination"/> with zero heap allocations.
    /// Returns the number of elements decoded, or a <see cref="Failure"/> if the payload is truncated/overflows or destination is too small.
    /// </summary>
    public static ParseResult<int> ParsePackedVarInts(ReadOnlySpan<byte> payload, Span<ulong> destination)
    {
        var cursor = payload.AsCursor();
        int count = 0;
        while (!cursor.IsAtEnd)
        {
            var res = cursor.ReadVarUInt64();
            if (!res.TryGetValue(out Parsed<ulong> ok))
            {
                return res.PropagateNonParsed<int>();
            }

            if (count >= destination.Length)
            {
                return Failure.At(cursor.Position, ErrorKind.Count, "Destination span too small for packed varints");
            }

            destination[count++] = ok.Value;
        }

        return new Parsed<int>(count);
    }

    public static void WriteVarInt(List<byte> output, ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7FUL);
            value >>= 7;
            if (value != 0) b |= 0x80;
            output.Add(b);
        }
        while (value != 0);
    }

    public static byte[] EncodeVarIntField(uint fieldNumber, ulong value)
    {
        List<byte> buf = [with(capacity: 16)];
        WriteVarInt(buf, ((ulong)fieldNumber << 3) | (ulong)ProtobufWireType.VarInt);
        WriteVarInt(buf, value);
        return buf.ToArray();
    }

    public static byte[] EncodeFixed64Field(uint fieldNumber, ulong bits)
    {
        List<byte> buf = [with(capacity: 16)];
        WriteVarInt(buf, ((ulong)fieldNumber << 3) | (ulong)ProtobufWireType.Fixed64);
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(tmp, bits);
        foreach (byte b in tmp) buf.Add(b);
        return buf.ToArray();
    }

    public static byte[] EncodeFixed32Field(uint fieldNumber, uint bits)
    {
        List<byte> buf = [with(capacity: 12)];
        WriteVarInt(buf, ((ulong)fieldNumber << 3) | (ulong)ProtobufWireType.Fixed32);
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, bits);
        foreach (byte b in tmp) buf.Add(b);
        return buf.ToArray();
    }

    public static byte[] EncodeLengthDelimitedField(uint fieldNumber, ReadOnlySpan<byte> payload)
    {
        List<byte> buf = [with(capacity: 16 + payload.Length)];
        WriteVarInt(buf, ((ulong)fieldNumber << 3) | (ulong)ProtobufWireType.LengthDelimited);
        WriteVarInt(buf, (ulong)payload.Length);
        foreach (byte b in payload) buf.Add(b);
        return buf.ToArray();
    }
}
