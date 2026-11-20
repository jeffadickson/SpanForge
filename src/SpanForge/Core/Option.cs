using System.Runtime.CompilerServices;

namespace SpanForge;

/// <summary>Present case of <see cref="Option{T}"/>.</summary>
public readonly record struct Some<T>(T Value);

/// <summary>Absent case of <see cref="Option{T}"/>.</summary>
public readonly record struct None;

/// <summary>
/// Zero-boxing C# 15 custom <c>[Union]</c> equivalent to Rust's <c>Option&lt;T&gt;</c>.
/// Used by <c>Parse.Opt</c> so optional parsed values participate in exhaustive C# 15 pattern matching
/// without boxing value types or conflating nullable reference/value types.
/// </summary>
[Union]
public readonly struct Option<T> : IUnion, IEquatable<Option<T>>
{
    private readonly T _value;
    private readonly byte _tag; // 1 = Some<T>, 2 = None

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Option(Some<T> some)
    {
        _value = some.Value;
        _tag = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Option(None _)
    {
        _value = default!;
        _tag = 2;
    }

    public static Option<T> Nothing => new(default(None));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Option<T> Of(T value) => new(new Some<T>(value));

    public object? Value => _tag switch
    {
        1 => new Some<T>(_value),
        2 => default(None),
        _ => null,
    };

    public bool HasValue => _tag != 0;
    public bool IsSome => _tag == 1;
    public bool IsNone => _tag != 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Some<T> value)
    {
        if (_tag == 1)
        {
            value = new Some<T>(_value);
            return true;
        }

        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out None value)
    {
        value = default;
        return _tag == 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T UnwrapOr(T fallback) => _tag == 1 ? _value : fallback;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Option<TOut> Map<TOut>(Func<T, TOut> mapper) =>
        _tag == 1 ? Option<TOut>.Of(mapper(_value)) : Option<TOut>.Nothing;

    public bool Equals(Option<T> other) =>
        _tag == other._tag && (_tag != 1 || EqualityComparer<T>.Default.Equals(_value, other._value));

    public override bool Equals(object? obj) => obj is Option<T> other && Equals(other);
    public override int GetHashCode() => _tag == 1 ? HashCode.Combine(_tag, _value) : _tag;
    public static bool operator ==(Option<T> left, Option<T> right) => left.Equals(right);
    public static bool operator !=(Option<T> left, Option<T> right) => !left.Equals(right);

    public override string ToString() => _tag == 1 ? $"Some({_value})" : "None";
}
