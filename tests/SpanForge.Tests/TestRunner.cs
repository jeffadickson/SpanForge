using System.Runtime.CompilerServices;

namespace SpanForge.Tests;

internal static class Assert
{
    public static void Equal<T>(T expected, T actual, [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"Expected {Format(expected)}, got {Format(actual)} (for `{expr}`).");
        }
    }

    public static void True(bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition)
        {
            throw new AssertionException(message ?? $"Expected true for `{expr}`.");
        }
    }

    public static void False(bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (condition)
        {
            throw new AssertionException(message ?? $"Expected false for `{expr}`.");
        }
    }

    public static void SequenceEqual<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual) where T : IEquatable<T>
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new AssertionException($"Sequence mismatch: expected length {expected.Length}, actual length {actual.Length}.");
        }
    }

    public static void Contains(string substring, string actual)
    {
        if (!actual.Contains(substring, StringComparison.Ordinal))
        {
            throw new AssertionException($"Expected string to contain \"{substring}\", actual:\n{actual}");
        }
    }

    private static string Format<T>(T value) => value is null ? "<null>" : value.ToString() ?? "<null>";
}

internal sealed class AssertionException(string message) : Exception(message);

internal sealed class TestSuite
{
    private readonly List<(string Name, Action Test)> _tests = [];

    public void Add(string name, Action test) => _tests.Add((name, test));

    public Dictionary<string, Action> ToDictionary() => _tests.ToDictionary(static t => t.Name, static t => t.Test);
}
