using System.Runtime.CompilerServices;

namespace SpanForge.Pipelines;

/// <summary>Successfully parsed a value from a <see cref="System.IO.Pipelines.PipeReader"/>.</summary>
public readonly record struct PipeParsed<T>(T Value);

/// <summary>Cleanly reached end-of-stream with zero unconsumed trailing bytes.</summary>
public readonly record struct PipeEndOfStream;

/// <summary>Reached end-of-stream while an incomplete frame still had buffered bytes.</summary>
public readonly record struct PipeIncompleteAtEof(Needed Needed);

/// <summary>Parsing failed due to malformed bytes or exceeding the configured buffer limit.</summary>
public readonly record struct PipeFailed(ParseError Error);

/// <summary>
/// Zero-boxing C# 15 custom <c>[Union]</c> representing the outcome of reading one frame from a
/// <see cref="System.IO.Pipelines.PipeReader"/> (<see cref="PipeParsed{T}"/> | <see cref="PipeEndOfStream"/> |
/// <see cref="PipeIncompleteAtEof"/> | <see cref="PipeFailed"/>).
/// </summary>
[Union]
public readonly struct PipeParseOutcome<T> : IUnion
{
    private readonly byte _tag; // 1=Parsed, 2=EndOfStream, 3=IncompleteAtEof, 4=Failed
    private readonly T _value;
    private readonly Needed _needed;
    private readonly ParseError _error;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PipeParseOutcome(PipeParsed<T> parsed)
    {
        _tag = 1;
        _value = parsed.Value;
        _needed = default;
        _error = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PipeParseOutcome(PipeEndOfStream _)
    {
        _tag = 2;
        _value = default!;
        _needed = default;
        _error = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PipeParseOutcome(PipeIncompleteAtEof incomplete)
    {
        _tag = 3;
        _value = default!;
        _needed = incomplete.Needed;
        _error = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PipeParseOutcome(PipeFailed failed)
    {
        _tag = 4;
        _value = default!;
        _needed = default;
        _error = failed.Error;
    }

    public object? Value => _tag switch
    {
        1 => new PipeParsed<T>(_value),
        2 => new PipeEndOfStream(),
        3 => new PipeIncompleteAtEof(_needed),
        4 => new PipeFailed(_error),
        _ => null,
    };

    public bool HasValue => _tag != 0;
    public bool IsParsed => _tag == 1;
    public bool IsEndOfStream => _tag == 2;
    public bool IsIncompleteAtEof => _tag == 3;
    public bool IsFailed => _tag == 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out PipeParsed<T> value)
    {
        if (_tag == 1) { value = new PipeParsed<T>(_value); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out PipeEndOfStream value)
    {
        value = default;
        return _tag == 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out PipeIncompleteAtEof value)
    {
        if (_tag == 3) { value = new PipeIncompleteAtEof(_needed); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out PipeFailed value)
    {
        if (_tag == 4) { value = new PipeFailed(_error); return true; }
        value = default; return false;
    }
}
