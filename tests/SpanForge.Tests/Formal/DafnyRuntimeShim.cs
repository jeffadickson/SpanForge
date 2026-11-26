using System.Numerics;

namespace Dafny
{

/// <summary>
/// Minimal, zero-unsafe C# runtime shim for executing the Dafny-translated
/// formally verified reference oracle (<c>SpanForgeOracle.g.cs</c>) in
/// differential tests under <c>AllowUnsafeBlocks=false</c> and
/// <c>TreatWarningsAsErrors=true</c>.
/// </summary>
public interface ISequence<T>
{
    int Count { get; }
    long LongCount { get; }
    T[] Elements { get; }
    T Select(BigInteger index);
    T Select(int index);
    ISequence<T> Take(BigInteger n);
    ISequence<T> Drop(BigInteger n);
    ISequence<T> Subsequence(BigInteger lo, BigInteger hi);
    bool Equals(ISequence<T>? other);
}

public sealed class Sequence<T> : ISequence<T>
{
    private readonly T[] _items;

    private Sequence(T[] items) => _items = items;

    public static ISequence<T> Empty { get; } = new Sequence<T>([]);

    public int Count => _items.Length;
    public long LongCount => _items.LongLength;
    public T[] Elements => _items;

    public T Select(BigInteger index) => _items[(int)index];
    public T Select(int index) => _items[index];

    public ISequence<T> Take(BigInteger n)
    {
        int count = (int)n;
        if (count <= 0) return Empty;
        if (count >= _items.Length) return this;
        var copy = new T[count];
        Array.Copy(_items, 0, copy, 0, count);
        return new Sequence<T>(copy);
    }

    public ISequence<T> Drop(BigInteger n)
    {
        int skip = (int)n;
        if (skip <= 0) return this;
        if (skip >= _items.Length) return Empty;
        int rem = _items.Length - skip;
        var copy = new T[rem];
        Array.Copy(_items, skip, copy, 0, rem);
        return new Sequence<T>(copy);
    }

    public ISequence<T> Subsequence(BigInteger lo, BigInteger hi)
    {
        int start = (int)lo;
        int len = (int)(hi - lo);
        if (len <= 0) return Empty;
        var copy = new T[len];
        Array.Copy(_items, start, copy, 0, len);
        return new Sequence<T>(copy);
    }

    public static ISequence<T> FromArray(T[] values)
    {
        if (values.Length == 0) return Empty;
        var copy = new T[values.Length];
        Array.Copy(values, copy, values.Length);
        return new Sequence<T>(copy);
    }

    public static ISequence<T> FromElements(params T[] values) => FromArray(values);

    public static ISequence<T> Concat(ISequence<T> a, ISequence<T> b)
    {
        if (a.Count == 0) return b;
        if (b.Count == 0) return a;
        var merged = new T[a.Count + b.Count];
        Array.Copy(a.Elements, 0, merged, 0, a.Count);
        Array.Copy(b.Elements, 0, merged, a.Count, b.Count);
        return new Sequence<T>(merged);
    }

    public static ISequence<T> Update(ISequence<T> s, BigInteger index, T value)
    {
        var copy = new T[s.Count];
        Array.Copy(s.Elements, copy, s.Count);
        copy[(int)index] = value;
        return new Sequence<T>(copy);
    }

    public bool Equals(ISequence<T>? other)
    {
        if (other is null || Count != other.Count) return false;
        var cmp = EqualityComparer<T>.Default;
        for (int i = 0; i < _items.Length; i++)
        {
            if (!cmp.Equals(_items[i], other.Select(i))) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ISequence<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        for (int i = 0; i < _items.Length; i++) hc.Add(_items[i]);
        return hc.ToHashCode();
    }
}

public sealed class TypeDescriptor<T>(T defaultValue)
{
    private readonly T _defaultValue = defaultValue;
    public T Default() => _defaultValue;
}

public static class Helpers
{
    public static T Id<T>(T value) => value;

    public static int ToIntChecked(BigInteger value, string message)
    {
        if (value < int.MinValue || value > int.MaxValue)
            throw new OverflowException(message);
        return (int)value;
    }

    public static BigInteger EuclideanDivision(BigInteger a, BigInteger b)
    {
        var q = BigInteger.DivRem(a, b, out var r);
        if (r < 0)
        {
            q += b > 0 ? BigInteger.MinusOne : BigInteger.One;
        }
        return q;
    }

    public static BigInteger EuclideanModulus(BigInteger a, BigInteger b)
    {
        var r = a % b;
        if (r < 0) r += BigInteger.Abs(b);
        return r;
    }

    public static uint Bv32ShiftLeft(uint v, int shift) => shift >= 32 ? 0u : v << shift;
    public static uint Bv32ShiftRight(uint v, int shift) => shift >= 32 ? 0u : v >> shift;
    public static ulong Bv64ShiftLeft(ulong v, int shift) => shift >= 64 ? 0UL : v << shift;
    public static ulong Bv64ShiftRight(ulong v, int shift) => shift >= 64 ? 0UL : v >> shift;

    public static IEnumerable<BigInteger> IntegerRange(BigInteger lo, BigInteger hi)
    {
        for (var i = lo; i < hi; i++) yield return i;
    }

    public static bool Quantifier<T>(IEnumerable<T> domain, bool forall, Func<T, bool> predicate)
    {
        foreach (var item in domain)
        {
            bool holds = predicate(item);
            if (forall && !holds) return false;
            if (!forall && holds) return true;
        }
        return forall;
    }

    public static int GetHashCode<T>(T value) => value is null ? 0 : EqualityComparer<T>.Default.GetHashCode(value);

    public static string ToString<T>(T value) => value?.ToString() ?? "null";
}
}

namespace _System
{
    public interface _ITuple2<T0, T1>
    {
        T0 dtor__0 { get; }
        T1 dtor__1 { get; }
    }

    public sealed class Tuple2<T0, T1>(T0 item0, T1 item1) : _ITuple2<T0, T1>
    {
        public T0 dtor__0 { get; } = item0;
        public T1 dtor__1 { get; } = item1;
        public static _ITuple2<T0, T1> create(T0 item0, T1 item1) => new Tuple2<T0, T1>(item0, item1);
    }
}

