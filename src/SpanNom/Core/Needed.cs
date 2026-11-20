using System.Runtime.CompilerServices;

namespace SpanNom;

/// <summary>Indicates that at least <see cref="Count"/> additional bytes are required.</summary>
public readonly record struct ExactBytes(int Count);

/// <summary>Indicates that more bytes are required, but the exact count is not yet known.</summary>
public readonly record struct UnknownSize;

/// <summary>
/// Zero-boxing C# 15 custom <c>[Union]</c> equivalent to Rust's <c>nom::Needed</c>.
/// Represents either an exact byte count (<see cref="ExactBytes"/>) or an unknown amount (<see cref="UnknownSize"/>).
/// </summary>
[Union]
public readonly struct Needed : IUnion, IEquatable<Needed>
{
    private readonly int _count;
    private readonly byte _tag; // 1 = ExactBytes, 2 = UnknownSize

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Needed(ExactBytes exact)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exact.Count);
        _count = exact.Count;
        _tag = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Needed(UnknownSize _)
    {
        _count = 0;
        _tag = 2;
    }

    public static Needed Unknown => new(default(UnknownSize));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Needed Bytes(int count) => new(new ExactBytes(count));

    public object? Value => _tag switch
    {
        1 => new ExactBytes(_count),
        2 => default(UnknownSize),
        _ => null,
    };

    public bool HasValue => _tag != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out ExactBytes value)
    {
        if (_tag == 1)
        {
            value = new ExactBytes(_count);
            return true;
        }

        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out UnknownSize value)
    {
        value = default;
        return _tag == 2;
    }

    public bool Equals(Needed other) => _tag == other._tag && _count == other._count;
    public override bool Equals(object? obj) => obj is Needed other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_tag, _count);
    public static bool operator ==(Needed left, Needed right) => left.Equals(right);
    public static bool operator !=(Needed left, Needed right) => !left.Equals(right);

    public override string ToString() => this switch
    {
        ExactBytes(var n) => $"Needed({n} bytes)",
        UnknownSize => "Needed(Unknown)",
        null => "Needed(Uninitialized)",
    };
}
