using System.Net;
using System.Text;

namespace SpanForge.Protocols.Dns;

public readonly record struct DnsFlags(
    bool IsResponse,
    byte Opcode,
    bool Authoritative,
    bool Truncated,
    bool RecursionDesired,
    bool RecursionAvailable,
    byte Z,
    byte ResponseCode);

public readonly record struct DnsHeader(
    ushort TransactionId,
    DnsFlags Flags,
    ushort QuestionCount,
    ushort AnswerCount,
    ushort AuthorityCount,
    ushort AdditionalCount);

public readonly record struct DnsQuestion(string Name, ushort RecordType, ushort RecordClass);

public readonly record struct ARecord(uint IPv4Address)
{
    public IPAddress ToIPAddress() => new([
        (byte)(IPv4Address >> 24),
        (byte)(IPv4Address >> 16),
        (byte)(IPv4Address >> 8),
        (byte)IPv4Address,
    ]);
}

public readonly record struct AaaaRecord(ByteSlice Raw16Bytes);
public readonly record struct CNameRecord(string Target);
public readonly record struct TxtRecord(ByteSlice RawTxt);
public readonly record struct UnknownRecord(ushort RecordType, ByteSlice RawData);

/// <summary>
/// Native C# 15 <c>union</c> representing decoded DNS resource record payload variants.
/// </summary>
public union DnsRecordData(ARecord, AaaaRecord, CNameRecord, TxtRecord, UnknownRecord);

public readonly record struct DnsResourceRecord(
    string Name,
    ushort RecordType,
    ushort RecordClass,
    uint Ttl,
    DnsRecordData Data);

public sealed record class DnsPacket(
    DnsHeader Header,
    IReadOnlyList<DnsQuestion> Questions,
    IReadOnlyList<DnsResourceRecord> Answers);

/// <summary>
/// RFC 1035 DNS wire-format binary packet parser showcasing <see cref="BitCursor"/> header flags,
/// DNS label pointer decompression with cycle protection, C# 15 labeled <c>break</c>,
/// and exhaustive <c>union DnsRecordData</c>.
/// </summary>
public static class DnsParser
{
    public const ushort TypeA = 1;
    public const ushort TypeCName = 5;
    public const ushort TypeTxt = 16;
    public const ushort TypeAaaa = 28;

    public static ParseResult<DnsHeader> ParseHeader(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        cursor.Endianness = Endianness.Big;

        var idRes = cursor.ReadU16BE();
        if (!idRes.TryGetValue(out Parsed<ushort> idOk)) { cursor.Reset(cp); return idRes.PropagateNonParsed<DnsHeader>(); }

        var flagsRes = cursor.ParseBits(static (ref BitCursor bits) =>
        {
            var qr = bits.ReadBool();
            if (!qr.TryGetValue(out Parsed<bool> qrOk)) return qr.PropagateNonParsed<DnsFlags>();
            var op = bits.ReadU8(4);
            if (!op.TryGetValue(out Parsed<byte> opOk)) return op.PropagateNonParsed<DnsFlags>();
            var aa = bits.ReadBool();
            if (!aa.TryGetValue(out Parsed<bool> aaOk)) return aa.PropagateNonParsed<DnsFlags>();
            var tc = bits.ReadBool();
            if (!tc.TryGetValue(out Parsed<bool> tcOk)) return tc.PropagateNonParsed<DnsFlags>();
            var rd = bits.ReadBool();
            if (!rd.TryGetValue(out Parsed<bool> rdOk)) return rd.PropagateNonParsed<DnsFlags>();
            var ra = bits.ReadBool();
            if (!ra.TryGetValue(out Parsed<bool> raOk)) return ra.PropagateNonParsed<DnsFlags>();
            var z = bits.ReadU8(3);
            if (!z.TryGetValue(out Parsed<byte> zOk)) return z.PropagateNonParsed<DnsFlags>();
            var rcode = bits.ReadU8(4);
            if (!rcode.TryGetValue(out Parsed<byte> rcodeOk)) return rcode.PropagateNonParsed<DnsFlags>();

            return new Parsed<DnsFlags>(new DnsFlags(
                qrOk.Value, opOk.Value, aaOk.Value, tcOk.Value, rdOk.Value, raOk.Value, zOk.Value, rcodeOk.Value));
        });

        if (!flagsRes.TryGetValue(out Parsed<DnsFlags> flagsOk)) { cursor.Reset(cp); return flagsRes.PropagateNonParsed<DnsHeader>(); }

        var qd = cursor.ReadU16BE();
        if (!qd.TryGetValue(out Parsed<ushort> qdOk)) { cursor.Reset(cp); return qd.PropagateNonParsed<DnsHeader>(); }
        var an = cursor.ReadU16BE();
        if (!an.TryGetValue(out Parsed<ushort> anOk)) { cursor.Reset(cp); return an.PropagateNonParsed<DnsHeader>(); }
        var ns = cursor.ReadU16BE();
        if (!ns.TryGetValue(out Parsed<ushort> nsOk)) { cursor.Reset(cp); return ns.PropagateNonParsed<DnsHeader>(); }
        var ar = cursor.ReadU16BE();
        if (!ar.TryGetValue(out Parsed<ushort> arOk)) { cursor.Reset(cp); return ar.PropagateNonParsed<DnsHeader>(); }

        return new Parsed<DnsHeader>(new DnsHeader(idOk.Value, flagsOk.Value, qdOk.Value, anOk.Value, nsOk.Value, arOk.Value));
    }

    /// <summary>
    /// Decodes a DNS domain name with RFC 1035 Section 4.1.4 compression pointers (<c>0xC0</c>)
    /// and strict loop/bounds protection.
    /// </summary>
    public static ParseResult<string> ParseName(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        var sb = new StringBuilder(64);
        int pos = cursor.Position;
        int endPosAfterFirstJump = -1;
        int jumps = 0;
        const int maxJumps = 16;

        labelLoop:
        while (true)
        {
            if (pos >= cursor.Length)
            {
                cursor.Reset(cp);
                return cursor.Mode == InputMode.Streaming
                    ? Incomplete.AtLeast(1)
                    : Failure.At(pos, ErrorKind.Eof, "Unexpected EOF inside DNS name");
            }

            byte len = cursor.Buffer[pos];
            if (len == 0)
            {
                pos++;
                break labelLoop;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= cursor.Length)
                {
                    cursor.Reset(cp);
                    return cursor.Mode == InputMode.Streaming
                        ? Incomplete.AtLeast(1)
                        : Failure.At(pos, ErrorKind.Eof, "Truncated DNS compression pointer");
                }

                if (++jumps > maxJumps)
                {
                    cursor.Reset(cp);
                    return Failure.At(pos, ErrorKind.Protocol, "DNS compression pointer loop detected");
                }

                int target = ((len & 0x3F) << 8) | cursor.Buffer[pos + 1];
                if (target >= cursor.Length)
                {
                    cursor.Reset(cp);
                    return Failure.At(pos, ErrorKind.Protocol, $"DNS compression pointer 0x{target:X} out of bounds");
                }

                if (endPosAfterFirstJump < 0)
                {
                    endPosAfterFirstJump = pos + 2;
                }

                pos = target;
                continue labelLoop;
            }

            if ((len & 0xC0) != 0)
            {
                cursor.Reset(cp);
                return Failure.At(pos, ErrorKind.Protocol, $"Invalid DNS label header byte 0x{len:X2}");
            }

            pos++;
            if (pos + len > cursor.Length)
            {
                cursor.Reset(cp);
                return cursor.Mode == InputMode.Streaming
                    ? Incomplete.AtLeast(pos + len - cursor.Length)
                    : Failure.At(pos, ErrorKind.Eof, "Truncated DNS label");
            }

            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(cursor.Buffer.Slice(pos, len)));
            pos += len;
        }

        cursor.Position = endPosAfterFirstJump >= 0 ? endPosAfterFirstJump : pos;
        return new Parsed<string>(sb.ToString());
    }

    public static ParseResult<DnsQuestion> ParseQuestion(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        var nameRes = ParseName(ref cursor);
        if (!nameRes.TryGetValue(out Parsed<string> nameOk)) { cursor.Reset(cp); return nameRes.PropagateNonParsed<DnsQuestion>(); }

        var qType = cursor.ReadU16BE();
        if (!qType.TryGetValue(out Parsed<ushort> typeOk)) { cursor.Reset(cp); return qType.PropagateNonParsed<DnsQuestion>(); }

        var qClass = cursor.ReadU16BE();
        if (!qClass.TryGetValue(out Parsed<ushort> classOk)) { cursor.Reset(cp); return qClass.PropagateNonParsed<DnsQuestion>(); }

        return new Parsed<DnsQuestion>(new DnsQuestion(nameOk.Value, typeOk.Value, classOk.Value));
    }

    public static ParseResult<DnsResourceRecord> ParseResourceRecord(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        var nameRes = ParseName(ref cursor);
        if (!nameRes.TryGetValue(out Parsed<string> nameOk)) { cursor.Reset(cp); return nameRes.PropagateNonParsed<DnsResourceRecord>(); }

        var rType = cursor.ReadU16BE();
        if (!rType.TryGetValue(out Parsed<ushort> typeOk)) { cursor.Reset(cp); return rType.PropagateNonParsed<DnsResourceRecord>(); }

        var rClass = cursor.ReadU16BE();
        if (!rClass.TryGetValue(out Parsed<ushort> classOk)) { cursor.Reset(cp); return rClass.PropagateNonParsed<DnsResourceRecord>(); }

        var ttl = cursor.ReadU32BE();
        if (!ttl.TryGetValue(out Parsed<uint> ttlOk)) { cursor.Reset(cp); return ttl.PropagateNonParsed<DnsResourceRecord>(); }

        var rdLen = cursor.ReadU16BE();
        if (!rdLen.TryGetValue(out Parsed<ushort> rdLenOk)) { cursor.Reset(cp); return rdLen.PropagateNonParsed<DnsResourceRecord>(); }

        var rdataSliceRes = cursor.Take(rdLenOk.Value);
        if (!rdataSliceRes.TryGetValue(out Parsed<ByteSlice> rdataOk)) { cursor.Reset(cp); return rdataSliceRes.PropagateNonParsed<DnsResourceRecord>(); }

        var sub = cursor.CreateSubCursor(rdataOk.Value, InputMode.Complete);
        DnsRecordData recordData;

        switch (typeOk.Value)
        {
            case TypeA when rdataOk.Value.Length == 4:
                var ipRes = sub.ReadU32BE();
                if (!ipRes.TryGetValue(out Parsed<uint> ipOk))
                {
                    cursor.Reset(cp);
                    return ipRes.PropagateNonParsed<DnsResourceRecord>();
                }

                recordData = new ARecord(ipOk.Value);
                break;

            case TypeAaaa when rdataOk.Value.Length == 16:
                recordData = new AaaaRecord(rdataOk.Value);
                break;

            case TypeCName:
                var targetRes = ParseName(ref sub);
                if (!targetRes.TryGetValue(out Parsed<string> targetOk))
                {
                    cursor.Reset(cp);
                    return targetRes.PropagateNonParsed<DnsResourceRecord>();
                }

                recordData = new CNameRecord(targetOk.Value);
                break;

            case TypeTxt:
                recordData = new TxtRecord(rdataOk.Value);
                break;

            default:
                recordData = new UnknownRecord(typeOk.Value, rdataOk.Value);
                break;
        }

        return new Parsed<DnsResourceRecord>(new DnsResourceRecord(
            nameOk.Value, typeOk.Value, classOk.Value, ttlOk.Value, recordData));
    }

    public static ParseResult<DnsPacket> ParsePacket(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        var hdrRes = ParseHeader(ref cursor);
        if (!hdrRes.TryGetValue(out Parsed<DnsHeader> hdrOk))
        {
            cursor.Reset(cp);
            return hdrRes.PropagateNonParsed<DnsPacket>();
        }

        var hdr = hdrOk.Value;
        var qRes = Parse.Count(ref cursor, hdr.QuestionCount, ParseQuestion).Cut("DNS questions");
        if (!qRes.TryGetValue(out Parsed<List<DnsQuestion>> qOk))
        {
            cursor.Reset(cp);
            return qRes.PropagateNonParsed<DnsPacket>();
        }

        var aRes = Parse.Count(ref cursor, hdr.AnswerCount, ParseResourceRecord).Cut("DNS answers");
        if (!aRes.TryGetValue(out Parsed<List<DnsResourceRecord>> aOk))
        {
            cursor.Reset(cp);
            return aRes.PropagateNonParsed<DnsPacket>();
        }

        return new Parsed<DnsPacket>(new DnsPacket(hdr, qOk.Value, aOk.Value));
    }
}
