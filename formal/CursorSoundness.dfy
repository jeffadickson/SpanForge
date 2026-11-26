// =============================================================================
// SpanForge Formal Verification Suite — Module 1: Cursor & Checkpoint Soundness
// =============================================================================
// Proves:
//   1. Cursor invariant preservation (0 <= pos <= |buf|) across all primitives.
//   2. ByteSlice bounds soundness (0 <= offset && 0 <= len && offset + len <= |buf|).
//   3. Exact O(1) Checkpoint Save/Reset rollback soundness on Backtrack/Incomplete/Failure.
//   4. Scoped endianness restoration on rollback.
//   5. SubCursor offset preservation and boundedness relative to root buffer.
// =============================================================================

module SpanForgeCursorSoundness {
  datatype Endianness = Big | Little
  datatype InputMode = Complete | Streaming

  datatype Needed =
    | ExactBytes(count: nat)
    | UnknownSize

  datatype ParseResult<T> =
    | Parsed(value: T)
    | Incomplete(needed: Needed)
    | Backtrack
    | Failure

  datatype Checkpoint = Checkpoint(pos: nat, endianness: Endianness)

  datatype ByteSlice = ByteSlice(offset: nat, len: nat)

  datatype ByteCursor = ByteCursor(
    buf: seq<bv8>,
    pos: nat,
    endianness: Endianness,
    mode: InputMode
  )

  predicate ValidCursor(c: ByteCursor) {
    c.pos <= |c.buf|
  }

  predicate ValidSlice(buf: seq<bv8>, s: ByteSlice) {
    s.offset + s.len <= |buf|
  }

  function RemainingLength(c: ByteCursor): nat
    requires ValidCursor(c)
  {
    |c.buf| - c.pos
  }

  function Save(c: ByteCursor): Checkpoint {
    Checkpoint(c.pos, c.endianness)
  }

  function Reset(c: ByteCursor, cp: Checkpoint): ByteCursor
    requires cp.pos <= |c.buf|
    ensures ValidCursor(Reset(c, cp))
  {
    c.(pos := cp.pos, endianness := cp.endianness)
  }

  function Shortage<T>(c: ByteCursor, requiredBytes: nat): ParseResult<T>
    requires ValidCursor(c)
  {
    if c.mode.Streaming? then
      var rem := RemainingLength(c);
      var missing := if requiredBytes > rem then requiredBytes - rem else 1;
      Incomplete(ExactBytes(missing))
    else
      Backtrack
  }

  // ---------------------------------------------------------------------------
  // Core cursor operations
  // ---------------------------------------------------------------------------

  function ReadU8(c: ByteCursor): (ParseResult<bv8>, ByteCursor)
    requires ValidCursor(c)
    ensures var (res, c') := ReadU8(c);
      ValidCursor(c') &&
      c'.buf == c.buf &&
      c'.endianness == c.endianness &&
      c'.mode == c.mode &&
      (res.Parsed? ==> c'.pos == c.pos + 1 && res.value == c.buf[c.pos]) &&
      (!res.Parsed? ==> c' == c)
  {
    if RemainingLength(c) < 1 then
      (Shortage<bv8>(c, 1), c)
    else
      (Parsed(c.buf[c.pos]), c.(pos := c.pos + 1))
  }

  function Take(c: ByteCursor, count: nat): (ParseResult<ByteSlice>, ByteCursor)
    requires ValidCursor(c)
    ensures var (res, c') := Take(c, count);
      ValidCursor(c') &&
      c'.buf == c.buf &&
      c'.endianness == c.endianness &&
      c'.mode == c.mode &&
      (res.Parsed? ==>
        ValidSlice(c.buf, res.value) &&
        res.value == ByteSlice(c.pos, count) &&
        c'.pos == c.pos + count) &&
      (!res.Parsed? ==> c' == c)
  {
    if RemainingLength(c) < count then
      (Shortage<ByteSlice>(c, count), c)
    else
      (Parsed(ByteSlice(c.pos, count)), c.(pos := c.pos + count))
  }

  function Skip(c: ByteCursor, count: nat): (ParseResult<nat>, ByteCursor)
    requires ValidCursor(c)
    ensures var (res, c') := Skip(c, count);
      ValidCursor(c') &&
      c'.buf == c.buf &&
      (res.Parsed? ==> c'.pos == c.pos + count && res.value == count) &&
      (!res.Parsed? ==> c' == c)
  {
    if RemainingLength(c) < count then
      (Shortage<nat>(c, count), c)
    else
      (Parsed(count), c.(pos := c.pos + count))
  }

  function Tag(c: ByteCursor, expected: seq<bv8>): (ParseResult<ByteSlice>, ByteCursor)
    requires ValidCursor(c)
    ensures var (res, c') := Tag(c, expected);
      ValidCursor(c') &&
      c'.buf == c.buf &&
      (res.Parsed? ==>
        ValidSlice(c.buf, res.value) &&
        c.buf[res.value.offset .. res.value.offset + res.value.len] == expected &&
        c'.pos == c.pos + |expected|) &&
      (!res.Parsed? ==> c' == c)
  {
    var rem := RemainingLength(c);
    if rem < |expected| then
      if c.mode.Streaming? && c.buf[c.pos .. |c.buf|] == expected[..rem] then
        (Incomplete(ExactBytes(|expected| - rem)), c)
      else
        (Backtrack, c)
    else if c.buf[c.pos .. c.pos + |expected|] == expected then
      (Parsed(ByteSlice(c.pos, |expected|)), c.(pos := c.pos + |expected|))
    else
      (Backtrack, c)
  }

  function CreateSubCursor(c: ByteCursor, slice: ByteSlice, mode: InputMode): ByteCursor
    requires ValidCursor(c)
    requires ValidSlice(c.buf, slice)
    ensures var sub := CreateSubCursor(c, slice, mode);
      ValidCursor(sub) &&
      |sub.buf| == slice.offset + slice.len &&
      sub.pos == slice.offset &&
      RemainingLength(sub) == slice.len &&
      sub.buf[sub.pos .. |sub.buf|] == c.buf[slice.offset .. slice.offset + slice.len]
  {
    ByteCursor(c.buf[.. slice.offset + slice.len], slice.offset, c.endianness, mode)
  }

  // ---------------------------------------------------------------------------
  // Theorems: Checkpoint Rollback & Scoped Endianness Soundness
  // ---------------------------------------------------------------------------

  lemma CheckpointResetRestoresExactState(c0: ByteCursor, c1: ByteCursor)
    requires ValidCursor(c0)
    requires c1.buf == c0.buf && c1.mode == c0.mode
    ensures Reset(c1, Save(c0)) == c0
  {
    // Direct by structural equality on datatype ByteCursor
  }

  // Combinator: Transactional Pair with scoped endianness rollback
  function ScopedTakePair(
    c0: ByteCursor,
    innerEndian: Endianness,
    n1: nat,
    n2: nat
  ): (ParseResult<(ByteSlice, ByteSlice)>, ByteCursor)
    requires ValidCursor(c0)
  {
    var cp := Save(c0);
    var cScoped := c0.(endianness := innerEndian);
    var (r1, c1) := Take(cScoped, n1);
    match r1 {
      case Parsed(s1) =>
        var (r2, c2) := Take(c1, n2);
        match r2 {
          case Parsed(s2) =>
            (Parsed((s1, s2)), c2.(endianness := cp.endianness))
          case Incomplete(need) =>
            (Incomplete(need), Reset(c2, cp))
          case Backtrack =>
            (Backtrack, Reset(c2, cp))
          case Failure =>
            (Failure, Reset(c2, cp))
        }
      case Incomplete(need) =>
        (Incomplete(need), Reset(c1, cp))
      case Backtrack =>
        (Backtrack, Reset(c1, cp))
      case Failure =>
        (Failure, Reset(c1, cp))
    }
  }

  lemma ScopedTakePairSoundness(c0: ByteCursor, innerEndian: Endianness, n1: nat, n2: nat)
    requires ValidCursor(c0)
    ensures var (res, cOut) := ScopedTakePair(c0, innerEndian, n1, n2);
      ValidCursor(cOut) &&
      cOut.endianness == c0.endianness &&
      (res.Parsed? ==>
        cOut.pos == c0.pos + n1 + n2 &&
        ValidSlice(c0.buf, res.value.0) &&
        ValidSlice(c0.buf, res.value.1) &&
        res.value.0.offset == c0.pos &&
        res.value.1.offset == c0.pos + n1) &&
      (!res.Parsed? ==> cOut == c0)
  {
    // Proved automatically by Dafny from Take & Reset postconditions
  }

  // Early mismatch detection theorem for Tag in Streaming mode:
  // If any available prefix byte disagrees with `expected`, Streaming Tag returns Backtrack (not Incomplete).
  lemma TagEarlyMismatchSoundness(c: ByteCursor, expected: seq<bv8>, k: nat)
    requires ValidCursor(c)
    requires c.mode.Streaming?
    requires k < RemainingLength(c) && k < |expected|
    requires c.buf[c.pos + k] != expected[k]
    ensures var (res, c') := Tag(c, expected);
      res.Backtrack? && c' == c
  {
    var rem := RemainingLength(c);
    if rem < |expected| {
      assert c.buf[c.pos .. |c.buf|][k] == c.buf[c.pos + k];
      assert expected[..rem][k] == expected[k];
      assert c.buf[c.pos .. |c.buf|] != expected[..rem];
    } else {
      assert c.buf[c.pos .. c.pos + |expected|][k] == c.buf[c.pos + k];
      assert c.buf[c.pos .. c.pos + |expected|] != expected;
    }
  }
}
