// =============================================================================
// SpanForge Formal Verification Suite — Module 2: Codec Bijections
// =============================================================================
// Proves:
//   1. ZigZag bijection over signed 64-bit & 32-bit integers (both exact two's-
//      complement bv64/bv32 AND mathematical int64 in [-2^63 .. 2^63 - 1]):
//      - DecodeZigZag64(EncodeZigZag64(v)) == v
//      - EncodeZigZag64(DecodeZigZag64(u)) == u
//   2. LEB128 unsigned 64-bit codec (0 <= v < 2^64):
//      - Exact 1..10 byte encoding bounds & 10th-byte <= 1 invariant
//      - Roundtrip: DecodeLeb128(EncodeLeb128(v) + suffix, 0) == Parsed(v, |enc|)
//      - Canonical uniqueness: canonical encodings are injective
//      - Overflow rejection: 10-byte sequences with b9 > 1 rejected with Backtrack
//   3. RFC 9000 QUIC 1/2/4/8-byte varints (0 <= v < 2^62):
//      - Roundtrip: DecodeQuicVarInt(EncodeQuicVarInt(v) + suffix, 0) == Parsed(v, |enc|)
//      - Strict 62-bit upper bound on all decoded values
//   4. Arbitrary cross-byte MSB-first BitWriter <-> BitCursor bitpacking:
//      - Frame preservation (bits outside [bitPos .. bitPos + count) untouched)
//      - Exact roundtrip for arbitrary bitPos, arbitrary count in [0..64], and
//        dirty buffers across arbitrary byte boundaries
//      - Sequential adjacent bitfield composition roundtrip
// =============================================================================

module SpanForgeZigZagBijection {
  // Pure bitvector module (kept separate from nonlinear integer arithmetic so Z3
  // uses QF_BV directly without int<->bv bridge overhead).

  function EncodeZigZag64Bv(v: bv64): bv64 {
    (v << 1) ^ (if (v & 0x8000_0000_0000_0000) != 0 then 0xFFFF_FFFF_FFFF_FFFF else 0)
  }

  function DecodeZigZag64Bv(u: bv64): bv64 {
    (u >> 1) ^ (if (u & 1) != 0 then 0xFFFF_FFFF_FFFF_FFFF else 0)
  }

  lemma Bv64ShiftRoundtripZero(v: bv64)
    requires (v & 0x8000_0000_0000_0000) == 0
    ensures ((v << 1) & 1) == 0
    ensures ((v << 1) >> 1) == v
  {}

  lemma Bv64ShiftRoundtripOne(v: bv64)
    requires (v & 0x8000_0000_0000_0000) != 0
    ensures (((v << 1) ^ 0xFFFF_FFFF_FFFF_FFFF) & 1) == 1
    ensures ((((v << 1) ^ 0xFFFF_FFFF_FFFF_FFFF) >> 1) ^ 0xFFFF_FFFF_FFFF_FFFF) == v
  {}

  lemma Bv64InverseZero(u: bv64)
    requires (u & 1) == 0
    ensures ((u >> 1) & 0x8000_0000_0000_0000) == 0
    ensures ((u >> 1) << 1) == u
  {}

  lemma Bv64InverseOne(u: bv64)
    requires (u & 1) != 0
    ensures (((u >> 1) ^ 0xFFFF_FFFF_FFFF_FFFF) & 0x8000_0000_0000_0000) != 0
    ensures ((((u >> 1) ^ 0xFFFF_FFFF_FFFF_FFFF) << 1) ^ 0xFFFF_FFFF_FFFF_FFFF) == u
  {}

  lemma ZigZag64BvRoundtrip(v: bv64)
    ensures DecodeZigZag64Bv(EncodeZigZag64Bv(v)) == v
  {
    if (v & 0x8000_0000_0000_0000) == 0 {
      Bv64ShiftRoundtripZero(v);
      assert EncodeZigZag64Bv(v) == (v << 1) ^ 0 == v << 1;
    } else {
      Bv64ShiftRoundtripOne(v);
      assert EncodeZigZag64Bv(v) == (v << 1) ^ 0xFFFF_FFFF_FFFF_FFFF;
    }
  }

  lemma ZigZag64BvInverse(u: bv64)
    ensures EncodeZigZag64Bv(DecodeZigZag64Bv(u)) == u
  {
    if (u & 1) == 0 {
      Bv64InverseZero(u);
      assert DecodeZigZag64Bv(u) == (u >> 1) ^ 0 == u >> 1;
    } else {
      Bv64InverseOne(u);
      assert DecodeZigZag64Bv(u) == (u >> 1) ^ 0xFFFF_FFFF_FFFF_FFFF;
    }
  }

  function EncodeZigZag32Bv(v: bv32): bv32 {
    (v << 1) ^ (if (v & 0x8000_0000) != 0 then 0xFFFF_FFFF else 0)
  }

  function DecodeZigZag32Bv(u: bv32): bv32 {
    (u >> 1) ^ (if (u & 1) != 0 then 0xFFFF_FFFF else 0)
  }

  lemma ZigZag32BvRoundtripCore(v: bv32)
    ensures var u: bv32 := (v << 1) ^ (if (v & 0x8000_0000) != 0 then 0xFFFF_FFFF else 0);
            ((u >> 1) ^ (if (u & 1) != 0 then 0xFFFF_FFFF else 0)) == v
  {}

  lemma ZigZag32BvInverseCore(u: bv32)
    ensures var v: bv32 := (u >> 1) ^ (if (u & 1) != 0 then 0xFFFF_FFFF else 0);
            ((v << 1) ^ (if (v & 0x8000_0000) != 0 then 0xFFFF_FFFF else 0)) == u
  {}

  lemma ZigZag32BvBijection(v: bv32, u: bv32)
    ensures DecodeZigZag32Bv(EncodeZigZag32Bv(v)) == v
    ensures EncodeZigZag32Bv(DecodeZigZag32Bv(u)) == u
  {
    ZigZag32BvRoundtripCore(v);
    ZigZag32BvInverseCore(u);
  }
}

module SpanForgeCodecBijections {
  type Byte = x: int | 0 <= x < 256 witness 0

  datatype CodecResult<T> =
    | Parsed(value: T, nextPos: nat)
    | Incomplete
    | Backtrack

  const Pow2_64: int := 0x1_0000_0000_0000_0000
  const Pow2_63: int := 0x8000_0000_0000_0000

  // Small reusable Euclidean division helpers (avoid nonlinear Z3 stalls)
  lemma MulMono(u: nat, v: nat, w: nat)
    requires u <= v
    ensures u * w <= v * w
  {
    var d: nat := v - u;
    assert v * w == (u + d) * w == u * w + d * w;
    assert d * w >= 0;
  }

  lemma EuclideanUnique(q1: nat, r1: nat, q2: nat, r2: nat, d: nat)
    requires d >= 1
    requires r1 < d && r2 < d
    requires q1 * d + r1 == q2 * d + r2
    ensures q1 == q2 && r1 == r2
  {
    if q1 > q2 {
      MulMono(q2 + 1, q1, d);
      assert (q2 + 1) * d == q2 * d + d;
      assert q1 * d >= q2 * d + d;
      assert false;
    } else if q2 > q1 {
      MulMono(q1 + 1, q2, d);
      assert (q1 + 1) * d == q1 * d + d;
      assert q2 * d >= q1 * d + d;
      assert false;
    }
  }

  lemma DivModExact(q: nat, r: nat, d: nat)
    requires d >= 1
    requires r < d
    ensures (q * d + r) / d == q
    ensures (q * d + r) % d == r
  {
    var x := q * d + r;
    EuclideanUnique(x / d, x % d, q, r, d);
  }

  lemma ModSmall(x: nat, m: nat)
    requires m >= 1
    requires x < m
    ensures x % m == x
    ensures x / m == 0
  {
    DivModExact(0, x, m);
  }

  lemma DivDiv(x: nat, a: nat, b: nat)
    requires a >= 1 && b >= 1
    ensures (x / a) / b == x / (a * b)
    ensures x == (x / (a * b)) * (a * b) + ((x / a) % b) * a + (x % a)
  {
    var q1 := x / a;
    var r1 := x % a;
    var q2 := q1 / b;
    var r2 := q1 % b;
    calc {
      x;
    == q1 * a + r1;
    == (q2 * b + r2) * a + r1;
    == (q2 * b) * a + r2 * a + r1;
    == q2 * (a * b) + (r2 * a + r1);
    }
    MulMono(r2, b - 1, a);
    assert (b - 1) * a + a == b * a == a * b;
    assert r2 * a + r1 < a * b;
    assert a * b >= 1 by { MulMono(1, b, a); }
    DivModExact(q2, r2 * a + r1, a * b);
  }

  // ===========================================================================
  // 1. Mathematical ZigZag Bijection over signed int64 [-2^63 .. 2^63 - 1]
  // ===========================================================================

  function EncodeZigZag64(v: int): nat
    requires -Pow2_63 <= v < Pow2_63
    ensures EncodeZigZag64(v) < Pow2_64
  {
    if v >= 0 then 2 * v else (-2 * v) - 1
  }

  function DecodeZigZag64(u: nat): int
    requires u < Pow2_64
    ensures -Pow2_63 <= DecodeZigZag64(u) < Pow2_63
  {
    if u % 2 == 0 then u / 2 else -((u + 1) / 2)
  }

  lemma ZigZag64IntBijection(v: int, u: nat)
    requires -Pow2_63 <= v < Pow2_63
    requires u < Pow2_64
    ensures DecodeZigZag64(EncodeZigZag64(v)) == v
    ensures EncodeZigZag64(DecodeZigZag64(u)) == u
  {
  }

  // ===========================================================================
  // 2. Unsigned LEB128 64-bit Codec: 1..10 bytes, overflow & canonical uniqueness
  // ===========================================================================

  function Pow128(k: nat): nat
    ensures Pow128(k) >= 1
  {
    if k == 0 then 1 else 128 * Pow128(k - 1)
  }

  function EncodeLeb128(v: nat): seq<Byte>
    ensures |EncodeLeb128(v)| >= 1
    ensures var s := EncodeLeb128(v);
      s[|s| - 1] < 128 &&
      (forall i :: 0 <= i < |s| - 1 ==> s[i] >= 128) &&
      (|s| > 1 ==> s[|s| - 1] > 0)
    decreases v
  {
    if v < 128 then
      [v]
    else
      [(v % 128) + 128] + EncodeLeb128(v / 128)
  }

  lemma Leb128TopDigitBound(v: nat, k: nat, maxTop: nat)
    requires k >= 1
    requires maxTop < 128
    requires v < (maxTop + 1) * Pow128(k - 1)
    ensures |EncodeLeb128(v)| <= k
    ensures |EncodeLeb128(v)| == k ==> EncodeLeb128(v)[k - 1] <= maxTop
    decreases k
  {
    if k == 1 {
      assert Pow128(0) == 1;
      assert v <= maxTop < 128;
      assert EncodeLeb128(v) == [v];
    } else if v < 128 {
      assert |EncodeLeb128(v)| == 1 < k;
    } else {
      assert Pow128(k - 1) == 128 * Pow128(k - 2);
      assert v / 128 < (maxTop + 1) * Pow128(k - 2);
      Leb128TopDigitBound(v / 128, k - 1, maxTop);
    }
  }

  lemma Leb128LengthAtMost10(v: nat)
    requires v < Pow2_64
    ensures 1 <= |EncodeLeb128(v)| <= 10
    ensures |EncodeLeb128(v)| == 10 ==> EncodeLeb128(v)[9] <= 1
  {
    assert Pow128(9) == Pow2_63 by {
      assert Pow128(0) == 1;
      assert Pow128(1) == 128;
      assert Pow128(2) == 16_384;
      assert Pow128(3) == 2_097_152;
      assert Pow128(4) == 268_435_456;
      assert Pow128(5) == 34_359_738_368;
      assert Pow128(6) == 4_398_046_511_104;
      assert Pow128(7) == 562_949_953_421_312;
      assert Pow128(8) == 72_057_594_037_927_936;
      assert Pow128(9) == 9_223_372_036_854_775_808;
    }
    assert 2 * Pow128(9) == Pow2_64;
    Leb128TopDigitBound(v, 10, 1);
  }

  // Exact model of SpanForge's 10-byte bounded LEB128 decoder
  function DecodeLeb128Step(
    buf: seq<Byte>,
    pos: nat,
    step: nat,
    acc: nat
  ): CodecResult<nat>
    requires pos <= |buf|
    requires step <= 10
    decreases 10 - step
  {
    if step == 10 then
      Backtrack
    else if pos >= |buf| then
      Incomplete
    else
      var b: nat := buf[pos];
      if step == 9 && b > 1 then
        Backtrack
      else
        var low7: nat := b % 128;
        var nextAcc: nat := acc + low7 * Pow128(step);
        if b < 128 then
          Parsed(nextAcc, pos + 1)
        else
          DecodeLeb128Step(buf, pos + 1, step + 1, nextAcc)
  }

  function DecodeLeb128(buf: seq<Byte>, pos: nat): CodecResult<nat>
    requires pos <= |buf|
  {
    DecodeLeb128Step(buf, pos, 0, 0)
  }

  lemma DecodeLeb128StepRoundtrip(
    v: nat,
    suffix: seq<Byte>,
    prefix: seq<Byte>,
    step: nat,
    acc: nat
  )
    requires step < 10
    requires |EncodeLeb128(v)| + step <= 10
    requires |EncodeLeb128(v)| + step == 10 ==> EncodeLeb128(v)[|EncodeLeb128(v)| - 1] <= 1
    ensures var enc := EncodeLeb128(v);
            var buf := prefix + enc + suffix;
            DecodeLeb128Step(buf, |prefix|, step, acc) == Parsed(acc + v * Pow128(step), |prefix| + |enc|)
    decreases v
  {
    var enc := EncodeLeb128(v);
    var buf := prefix + enc + suffix;
    assert buf[|prefix|] == enc[0];
    if v < 128 {
      assert enc == [v];
      if step == 9 {
        assert enc[0] <= 1;
      }
      assert DecodeLeb128Step(buf, |prefix|, step, acc) == Parsed(acc + v * Pow128(step), |prefix| + 1);
    } else {
      var low7: nat := v % 128;
      var q: nat := v / 128;
      var tail := EncodeLeb128(q);
      assert enc == [low7 + 128] + tail;
      assert buf == (prefix + [enc[0]]) + tail + suffix;
      assert step < 9;
      var nextAcc := acc + low7 * Pow128(step);
      DecodeLeb128StepRoundtrip(q, suffix, prefix + [enc[0]], step + 1, nextAcc);
      calc {
        nextAcc + q * Pow128(step + 1);
      ==
        acc + low7 * Pow128(step) + q * (128 * Pow128(step));
      ==
        acc + (low7 + 128 * q) * Pow128(step);
      ==
        acc + v * Pow128(step);
      }
    }
  }

  // Theorem 2a: LEB128 Parse(Write(v) || suffix) == Parsed(v) for every uint64!
  lemma Leb128Roundtrip(v: nat, suffix: seq<Byte>)
    requires v < Pow2_64
    ensures var enc := EncodeLeb128(v);
            DecodeLeb128(enc + suffix, 0) == Parsed(v, |enc|)
  {
    Leb128LengthAtMost10(v);
    DecodeLeb128StepRoundtrip(v, suffix, [], 0, 0);
    assert [] + EncodeLeb128(v) + suffix == EncodeLeb128(v) + suffix;
    assert Pow128(0) == 1;
  }

  // Theorem 2b: Canonical LEB128 Uniqueness (injectivity)
  predicate IsCanonicalLeb128(s: seq<Byte>) {
    1 <= |s| <= 10 &&
    s[|s| - 1] < 128 &&
    (forall i :: 0 <= i < |s| - 1 ==> s[i] >= 128) &&
    (|s| > 1 ==> s[|s| - 1] > 0) &&
    (|s| == 10 ==> s[9] <= 1)
  }

  function EvalLeb128(s: seq<Byte>): nat
    requires |s| >= 1
    decreases |s|
  {
    var low7: nat := s[0] % 128;
    if |s| == 1 then low7 else low7 + 128 * EvalLeb128(s[1..])
  }

  lemma CanonicalTailValid(s: seq<Byte>)
    requires IsCanonicalLeb128(s)
    requires |s| > 1
    ensures IsCanonicalLeb128(s[1..])
  {
    var tail := s[1..];
    assert tail[|tail| - 1] == s[|s| - 1];
    forall i | 0 <= i < |tail| - 1
      ensures tail[i] >= 128
    {
      assert tail[i] == s[i + 1];
    }
  }

  lemma CanonicalLeb128Positive(s: seq<Byte>)
    requires IsCanonicalLeb128(s)
    requires |s| > 1
    ensures EvalLeb128(s) >= 128
    decreases |s|
  {
    if |s| == 2 {
      assert s[1] > 0;
      assert EvalLeb128(s[1..]) == s[1] >= 1;
    } else {
      CanonicalTailValid(s);
      CanonicalLeb128Positive(s[1..]);
    }
  }

  lemma CanonicalLeb128Unique(s: seq<Byte>)
    requires IsCanonicalLeb128(s)
    ensures EncodeLeb128(EvalLeb128(s)) == s
    decreases |s|
  {
    if |s| == 1 {
      var b0: nat := s[0];
      assert b0 < 128;
      ModSmall(b0, 128);
      assert EvalLeb128(s) == b0;
      assert EncodeLeb128(b0) == [b0] == s;
    } else {
      var tail := s[1..];
      CanonicalTailValid(s);
      CanonicalLeb128Unique(tail);
      var q := EvalLeb128(tail);
      if |tail| == 1 {
        var b1: nat := tail[0];
        assert b1 == s[1] > 0 && b1 < 128;
        ModSmall(b1, 128);
        assert q == b1 > 0;
      } else {
        CanonicalLeb128Positive(tail);
        assert q >= 128 > 0;
      }
      var b0: nat := s[0];
      assert 128 <= b0 < 256;
      var low7: nat := b0 % 128;
      assert b0 == low7 + 128;
      var v: nat := low7 + 128 * q;
      assert v == EvalLeb128(s);
      assert v >= 128;
      DivModExact(q, low7, 128);
      assert v % 128 == low7;
      assert v / 128 == q;
      assert EncodeLeb128(v) == [low7 + 128] + EncodeLeb128(q);
      assert [b0] + tail == s;
    }
  }

  lemma Leb128CanonicalEquality(s1: seq<Byte>, s2: seq<Byte>)
    requires IsCanonicalLeb128(s1) && IsCanonicalLeb128(s2)
    requires EvalLeb128(s1) == EvalLeb128(s2)
    ensures s1 == s2
  {
    CanonicalLeb128Unique(s1);
    CanonicalLeb128Unique(s2);
  }

  // Theorem 2c: 10-byte overflow (b9 > 1, including >10 bytes with b9 >= 0x80) is rejected with Backtrack!
  lemma Leb128OverflowRejected(buf: seq<Byte>)
    requires |buf| >= 10
    requires forall i :: 0 <= i < 9 ==> buf[i] >= 128
    requires buf[9] > 1
    ensures DecodeLeb128(buf, 0) == Backtrack
  {
    var a1 := (buf[0] % 128) * Pow128(0);
    var a2 := a1 + (buf[1] % 128) * Pow128(1);
    var a3 := a2 + (buf[2] % 128) * Pow128(2);
    var a4 := a3 + (buf[3] % 128) * Pow128(3);
    var a5 := a4 + (buf[4] % 128) * Pow128(4);
    var a6 := a5 + (buf[5] % 128) * Pow128(5);
    var a7 := a6 + (buf[6] % 128) * Pow128(6);
    var a8 := a7 + (buf[7] % 128) * Pow128(7);
    var a9 := a8 + (buf[8] % 128) * Pow128(8);
    assert DecodeLeb128Step(buf, 9, 9, a9) == Backtrack;
    assert DecodeLeb128Step(buf, 8, 8, a8) == Backtrack;
    assert DecodeLeb128Step(buf, 7, 7, a7) == Backtrack;
    assert DecodeLeb128Step(buf, 6, 6, a6) == Backtrack;
    assert DecodeLeb128Step(buf, 5, 5, a5) == Backtrack;
    assert DecodeLeb128Step(buf, 4, 4, a4) == Backtrack;
    assert DecodeLeb128Step(buf, 3, 3, a3) == Backtrack;
    assert DecodeLeb128Step(buf, 2, 2, a2) == Backtrack;
    assert DecodeLeb128Step(buf, 1, 1, a1) == Backtrack;
    assert DecodeLeb128Step(buf, 0, 0, 0) == Backtrack;
  }

  // ===========================================================================
  // 3. RFC 9000 QUIC Variable-Length Integers (1, 2, 4, 8 bytes; 0 <= v < 2^62)
  // ===========================================================================

  const QuicMax1: nat := 0x3F                     // 2^6 - 1
  const QuicMax2: nat := 0x3FFF                   // 2^14 - 1
  const QuicMax4: nat := 0x3FFF_FFFF              // 2^30 - 1
  const QuicMax8: nat := 0x3FFF_FFFF_FFFF_FFFF    // 2^62 - 1

  function EncodeQuicVarInt(v: nat): seq<Byte>
    requires v <= QuicMax8
    ensures |EncodeQuicVarInt(v)| in {1, 2, 4, 8}
  {
    if v <= QuicMax1 then
      [v]
    else if v <= QuicMax2 then
      [
        0x40 + (v / 0x100),
        v % 0x100
      ]
    else if v <= QuicMax4 then
      [
        0x80 + (v / 0x100_0000),
        (v / 0x1_0000) % 0x100,
        (v / 0x100) % 0x100,
        v % 0x100
      ]
    else
      [
        0xC0 + (v / 0x100_0000_0000_0000),
        (v / 0x1_0000_0000_0000) % 0x100,
        (v / 0x100_0000_0000) % 0x100,
        (v / 0x1_0000_0000) % 0x100,
        (v / 0x100_0000) % 0x100,
        (v / 0x1_0000) % 0x100,
        (v / 0x100) % 0x100,
        v % 0x100
      ]
  }

  function DecodeQuicVarInt(buf: seq<Byte>, pos: nat): CodecResult<nat>
    requires pos <= |buf|
    ensures var res := DecodeQuicVarInt(buf, pos);
      res.Parsed? ==> res.value <= QuicMax8 && res.nextPos <= |buf| && (res.nextPos - pos) in {1, 2, 4, 8}
  {
    if pos >= |buf| then
      Incomplete
    else
      var first: nat := buf[pos];
      var tag: nat := first / 64;
      var low6: nat := first % 64;
      if tag == 0 then
        Parsed(low6, pos + 1)
      else if tag == 1 then
        if |buf| - pos < 2 then Incomplete
        else
          var b1: nat := buf[pos + 1];
          Parsed(low6 * 0x100 + b1, pos + 2)
      else if tag == 2 then
        if |buf| - pos < 4 then Incomplete
        else
          var b1: nat := buf[pos + 1];
          var b2: nat := buf[pos + 2];
          var b3: nat := buf[pos + 3];
          Parsed(
            low6 * 0x100_0000 +
            b1 * 0x1_0000 +
            b2 * 0x100 +
            b3,
            pos + 4)
      else
        if |buf| - pos < 8 then Incomplete
        else
          var b1: nat := buf[pos + 1];
          var b2: nat := buf[pos + 2];
          var b3: nat := buf[pos + 3];
          var b4: nat := buf[pos + 4];
          var b5: nat := buf[pos + 5];
          var b6: nat := buf[pos + 6];
          var b7: nat := buf[pos + 7];
          Parsed(
            low6 * 0x100_0000_0000_0000 +
            b1 * 0x1_0000_0000_0000 +
            b2 * 0x100_0000_0000 +
            b3 * 0x1_0000_0000 +
            b4 * 0x100_0000 +
            b5 * 0x1_0000 +
            b6 * 0x100 +
            b7,
            pos + 8)
  }

  // Theorem 3: RFC 9000 QUIC VarInt exact roundtrip over all 0 <= v < 2^62!
  lemma QuicVarIntRoundtrip(v: nat, suffix: seq<Byte>)
    requires v <= QuicMax8
    ensures var enc := EncodeQuicVarInt(v);
            DecodeQuicVarInt(enc + suffix, 0) == Parsed(v, |enc|)
  {
    var enc := EncodeQuicVarInt(v);
    var buf := enc + suffix;
    if v <= QuicMax1 {
      assert buf[0] == v;
    } else if v <= QuicMax2 {
      var hi: nat := v / 0x100;
      var lo: nat := v % 0x100;
      assert buf[0] == 0x40 + hi;
      assert buf[1] == lo;
      assert hi * 0x100 + lo == v;
    } else if v <= QuicMax4 {
      DivDiv(v, 0x100, 0x100);
      DivDiv(v, 0x1_0000, 0x100);
      var b0: nat := v / 0x100_0000;
      var b1: nat := (v / 0x1_0000) % 0x100;
      var b2: nat := (v / 0x100) % 0x100;
      var b3: nat := v % 0x100;
      assert buf[0] == 0x80 + b0 && buf[1] == b1 && buf[2] == b2 && buf[3] == b3;
      assert b0 * 0x100_0000 + b1 * 0x1_0000 + b2 * 0x100 + b3 == v;
    } else {
      DivDiv(v, 0x100, 0x100);
      DivDiv(v, 0x1_0000, 0x100);
      DivDiv(v, 0x100_0000, 0x100);
      DivDiv(v, 0x1_0000_0000, 0x100);
      DivDiv(v, 0x100_0000_0000, 0x100);
      DivDiv(v, 0x1_0000_0000_0000, 0x100);
      var b0: nat := v / 0x100_0000_0000_0000;
      var b1: nat := (v / 0x1_0000_0000_0000) % 0x100;
      var b2: nat := (v / 0x100_0000_0000) % 0x100;
      var b3: nat := (v / 0x1_0000_0000) % 0x100;
      var b4: nat := (v / 0x100_0000) % 0x100;
      var b5: nat := (v / 0x1_0000) % 0x100;
      var b6: nat := (v / 0x100) % 0x100;
      var b7: nat := v % 0x100;
      assert buf[0] == 0xC0 + b0;
      assert buf[1] == b1 && buf[2] == b2 && buf[3] == b3;
      assert buf[4] == b4 && buf[5] == b5 && buf[6] == b6 && buf[7] == b7;
      assert b0 * 0x100_0000_0000_0000 + b1 * 0x1_0000_0000_0000 +
             b2 * 0x100_0000_0000 + b3 * 0x1_0000_0000 +
             b4 * 0x100_0000 + b5 * 0x1_0000 + b6 * 0x100 + b7 == v;
    }
  }

  // ===========================================================================
  // 4. Arbitrary Cross-Byte MSB-First BitWriter <-> BitCursor Bitpacking
  // ===========================================================================

  function Pow2(k: nat): nat
    ensures Pow2(k) >= 1
  {
    if k == 0 then 1 else 2 * Pow2(k - 1)
  }

  function ByteBitWeight(shift: nat): nat
    requires shift < 8
    ensures ByteBitWeight(shift) == Pow2(shift)
  {
    if shift == 0 then 1
    else if shift == 1 then 2
    else if shift == 2 then 4
    else if shift == 3 then 8
    else if shift == 4 then 16
    else if shift == 5 then 32
    else if shift == 6 then 64
    else 128
  }

  function GetBit(buf: seq<Byte>, bitPos: nat): nat
    requires bitPos < |buf| * 8
    ensures GetBit(buf, bitPos) == 0 || GetBit(buf, bitPos) == 1
  {
    var byteIdx := bitPos / 8;
    var shift := 7 - (bitPos % 8);
    ((buf[byteIdx] as nat) / ByteBitWeight(shift)) % 2
  }

  function UpdateByteBit(oldByte: Byte, shift: nat, bitVal: nat): Byte
    requires shift < 8
    requires bitVal == 0 || bitVal == 1
  {
    var ob: nat := oldByte;
    var w := ByteBitWeight(shift);
    var hi := ob / (2 * w);
    var lo := ob % w;
    hi * (2 * w) + bitVal * w + lo
  }

  lemma Pow2Factor(a: nat, b: nat)
    requires a <= b
    ensures Pow2(b) == Pow2(a) * Pow2(b - a)
    decreases b - a
  {
    if a < b {
      Pow2Factor(a, b - 1);
    }
  }

  lemma UpdateByteBitTarget(oldByte: Byte, shift: nat, bitVal: nat)
    requires shift < 8
    requires bitVal == 0 || bitVal == 1
    ensures ((UpdateByteBit(oldByte, shift, bitVal) as nat) / ByteBitWeight(shift)) % 2 == bitVal
  {
    var ob: nat := oldByte;
    var w := ByteBitWeight(shift);
    var hi := ob / (2 * w);
    var lo := ob % w;
    var nb: nat := hi * (2 * w) + bitVal * w + lo;
    assert nb == (hi * 2 + bitVal) * w + lo;
    DivModExact(hi * 2 + bitVal, lo, w);
    assert nb / w == hi * 2 + bitVal;
    DivModExact(hi, bitVal, 2);
    assert (nb / w) % 2 == bitVal;
  }

  lemma AddEvenMultiplePreservesBit(x: nat, q: nat, ws: nat)
    requires ws >= 1
    ensures ((q * (2 * ws) + x) / ws) % 2 == (x / ws) % 2
  {
    var xQ := x / ws;
    var xR := x % ws;
    calc {
      q * (2 * ws) + x;
    == (q * 2) * ws + (xQ * ws + xR);
    == (q * 2 + xQ) * ws + xR;
    }
    DivModExact(q * 2 + xQ, xR, ws);
    assert q * 2 + xQ == (q + xQ / 2) * 2 + (xQ % 2);
    DivModExact(q + xQ / 2, xQ % 2, 2);
  }

  lemma UpdateByteBitLower(oldByte: Byte, shift: nat, bitVal: nat, s: nat)
    requires s < shift < 8
    requires bitVal == 0 || bitVal == 1
    ensures ((UpdateByteBit(oldByte, shift, bitVal) as nat) / ByteBitWeight(s)) % 2
         == ((oldByte as nat) / ByteBitWeight(s)) % 2
  {
    var ob: nat := oldByte;
    var w := ByteBitWeight(shift);
    var ws := ByteBitWeight(s);
    var hi := ob / (2 * w);
    var oldBit := (ob / w) % 2;
    var lo := ob % w;
    DivDiv(ob, w, 2);
    var nb: nat := hi * (2 * w) + bitVal * w + lo;
    Pow2Factor(s + 1, shift);
    var k: nat := Pow2(shift - s - 1);
    assert Pow2(s + 1) == 2 * ws;
    assert w == k * (2 * ws);
    var qOld: nat := (hi * 2 + oldBit) * k;
    var qNew: nat := (hi * 2 + bitVal) * k;
    calc {
      ob;
    == hi * (2 * w) + oldBit * w + lo;
    == (hi * 2 + oldBit) * w + lo;
    == (hi * 2 + oldBit) * (k * (2 * ws)) + lo;
    == qOld * (2 * ws) + lo;
    }
    calc {
      nb;
    == hi * (2 * w) + bitVal * w + lo;
    == (hi * 2 + bitVal) * w + lo;
    == (hi * 2 + bitVal) * (k * (2 * ws)) + lo;
    == qNew * (2 * ws) + lo;
    }
    AddEvenMultiplePreservesBit(lo, qOld, ws);
    AddEvenMultiplePreservesBit(lo, qNew, ws);
  }

  lemma UpdateByteBitHigher(oldByte: Byte, shift: nat, bitVal: nat, s: nat)
    requires shift < s < 8
    requires bitVal == 0 || bitVal == 1
    ensures ((UpdateByteBit(oldByte, shift, bitVal) as nat) / ByteBitWeight(s)) % 2
         == ((oldByte as nat) / ByteBitWeight(s)) % 2
  {
    var ob: nat := oldByte;
    var w := ByteBitWeight(shift);
    var ws := ByteBitWeight(s);
    var hi := ob / (2 * w);
    var oldBit := (ob / w) % 2;
    var lo := ob % w;
    DivDiv(ob, w, 2);
    var nb: nat := hi * (2 * w) + bitVal * w + lo;
    Pow2Factor(shift + 1, s);
    var m: nat := Pow2(s - shift - 1);
    assert Pow2(shift + 1) == 2 * w;
    assert ws == (2 * w) * m == m * (2 * w);
    var hiQ := hi / m;
    var hiR := hi % m;
    assert hi == hiQ * m + hiR;
    var rOld: nat := hiR * (2 * w) + oldBit * w + lo;
    var rNew: nat := hiR * (2 * w) + bitVal * w + lo;
    assert rOld < ws && rNew < ws by {
      assert oldBit * w + lo < 2 * w;
      assert bitVal * w + lo < 2 * w;
      MulMono(hiR, m - 1, 2 * w);
      assert (m - 1) * (2 * w) + 2 * w == m * (2 * w) == ws;
    }
    calc {
      ob;
    == (hiQ * m + hiR) * (2 * w) + oldBit * w + lo;
    == hiQ * (m * (2 * w)) + rOld;
    == hiQ * ws + rOld;
    }
    calc {
      nb;
    == (hiQ * m + hiR) * (2 * w) + bitVal * w + lo;
    == hiQ * (m * (2 * w)) + rNew;
    == hiQ * ws + rNew;
    }
    DivModExact(hiQ, rOld, ws);
    DivModExact(hiQ, rNew, ws);
  }

  lemma UpdateByteBitOther(oldByte: Byte, shift: nat, bitVal: nat, s: nat)
    requires shift < 8 && s < 8 && s != shift
    requires bitVal == 0 || bitVal == 1
    ensures ((UpdateByteBit(oldByte, shift, bitVal) as nat) / ByteBitWeight(s)) % 2
         == ((oldByte as nat) / ByteBitWeight(s)) % 2
  {
    if s < shift {
      UpdateByteBitLower(oldByte, shift, bitVal, s);
    } else {
      UpdateByteBitHigher(oldByte, shift, bitVal, s);
    }
  }

  lemma UpdateByteBitSpec(oldByte: Byte, shift: nat, bitVal: nat)
    requires shift < 8
    requires bitVal == 0 || bitVal == 1
    ensures var newByte: nat := UpdateByteBit(oldByte, shift, bitVal);
      (newByte / ByteBitWeight(shift)) % 2 == bitVal &&
      (forall s: nat :: s < 8 && s != shift ==>
        (newByte / ByteBitWeight(s)) % 2 == ((oldByte as nat) / ByteBitWeight(s)) % 2)
  {
    UpdateByteBitTarget(oldByte, shift, bitVal);
    forall s: nat | s < 8 && s != shift
      ensures ((UpdateByteBit(oldByte, shift, bitVal) as nat) / ByteBitWeight(s)) % 2
           == ((oldByte as nat) / ByteBitWeight(s)) % 2
    {
      UpdateByteBitOther(oldByte, shift, bitVal, s);
    }
  }

  function SetBit(buf: seq<Byte>, bitPos: nat, bitVal: nat): seq<Byte>
    requires bitPos < |buf| * 8
    requires bitVal == 0 || bitVal == 1
    ensures |SetBit(buf, bitPos, bitVal)| == |buf|
  {
    var byteIdx := bitPos / 8;
    var shift := 7 - (bitPos % 8);
    buf[byteIdx := UpdateByteBit(buf[byteIdx], shift, bitVal)]
  }

  lemma SetBitPreservesAndUpdates(buf: seq<Byte>, bitPos: nat, bitVal: nat)
    requires bitPos < |buf| * 8
    requires bitVal == 0 || bitVal == 1
    ensures var buf' := SetBit(buf, bitPos, bitVal);
      |buf'| == |buf| &&
      GetBit(buf', bitPos) == bitVal &&
      (forall j: nat :: j < |buf| * 8 && j != bitPos ==> GetBit(buf', j) == GetBit(buf, j))
  {
    var byteIdx := bitPos / 8;
    var shift := 7 - (bitPos % 8);
    UpdateByteBitSpec(buf[byteIdx], shift, bitVal);
    var buf' := SetBit(buf, bitPos, bitVal);
    forall j: nat | j < |buf| * 8 && j != bitPos
      ensures GetBit(buf', j) == GetBit(buf, j)
    {
      if j / 8 == byteIdx {
        assert 7 - (j % 8) != shift;
      } else {
        assert buf'[j / 8] == buf[j / 8];
      }
    }
  }

  // MSB-first ReadBits(buf, bitPos, count): reads `count` bits starting at `bitPos`
  function ReadBits(buf: seq<Byte>, bitPos: nat, count: nat): nat
    requires bitPos + count <= |buf| * 8
    ensures ReadBits(buf, bitPos, count) < Pow2(count)
    decreases count
  {
    if count == 0 then
      0
    else
      var msb := GetBit(buf, bitPos);
      msb * Pow2(count - 1) + ReadBits(buf, bitPos + 1, count - 1)
  }

  // MSB-first WriteBits(buf, bitPos, count, value): writes the low `count` bits of `value`
  function WriteBits(buf: seq<Byte>, bitPos: nat, count: nat, value: nat): seq<Byte>
    requires bitPos + count <= |buf| * 8
    ensures |WriteBits(buf, bitPos, count, value)| == |buf|
    decreases count
  {
    if count == 0 then
      buf
    else
      var msb := (value / Pow2(count - 1)) % 2;
      var buf1 := SetBit(buf, bitPos, msb);
      WriteBits(buf1, bitPos + 1, count - 1, value % Pow2(count - 1))
  }

  // Theorem 4a: Frame preservation — writing [bitPos .. bitPos + count) never modifies any other bit!
  lemma WriteBitsFrame(buf: seq<Byte>, bitPos: nat, count: nat, value: nat, j: nat)
    requires bitPos + count <= |buf| * 8
    requires j < |buf| * 8
    requires j < bitPos || j >= bitPos + count
    ensures GetBit(WriteBits(buf, bitPos, count, value), j) == GetBit(buf, j)
    decreases count
  {
    if count > 0 {
      var msb := (value / Pow2(count - 1)) % 2;
      var buf1 := SetBit(buf, bitPos, msb);
      SetBitPreservesAndUpdates(buf, bitPos, msb);
      WriteBitsFrame(buf1, bitPos + 1, count - 1, value % Pow2(count - 1), j);
    }
  }

  lemma ReadBitsExtensional(buf1: seq<Byte>, buf2: seq<Byte>, bitPos: nat, count: nat)
    requires bitPos + count <= |buf1| * 8
    requires |buf1| == |buf2|
    requires forall j: nat :: bitPos <= j < bitPos + count ==> GetBit(buf1, j) == GetBit(buf2, j)
    ensures ReadBits(buf1, bitPos, count) == ReadBits(buf2, bitPos, count)
    decreases count
  {
    if count > 0 {
      ReadBitsExtensional(buf1, buf2, bitPos + 1, count - 1);
    }
  }

  lemma DivModDouble(value: nat, p: nat)
    requires p >= 1
    ensures ((value / p) % 2) * p + (value % p) == value % (2 * p)
  {
    DivDiv(value, p, 2);
    var q2 := value / (2 * p);
    var msb := (value / p) % 2;
    var rem := value % p;
    assert value == q2 * (2 * p) + (msb * p + rem);
    assert msb * p + rem < 2 * p;
    DivModExact(q2, msb * p + rem, 2 * p);
  }

  // Theorem 4b: Arbitrary cross-byte BitWriter <-> BitCursor roundtrip bijection!
  lemma BitPackRoundtrip(buf: seq<Byte>, bitPos: nat, count: nat, value: nat)
    requires bitPos + count <= |buf| * 8
    ensures ReadBits(WriteBits(buf, bitPos, count, value), bitPos, count) == value % Pow2(count)
    decreases count
  {
    if count == 0 {
      assert Pow2(0) == 1;
      assert value % 1 == 0;
    } else {
      var p := Pow2(count - 1);
      assert Pow2(count) == 2 * p;
      var msb := (value / p) % 2;
      var rem := value % p;
      var buf1 := SetBit(buf, bitPos, msb);
      SetBitPreservesAndUpdates(buf, bitPos, msb);
      var buf2 := WriteBits(buf1, bitPos + 1, count - 1, rem);
      WriteBitsFrame(buf1, bitPos + 1, count - 1, rem, bitPos);
      assert GetBit(buf2, bitPos) == msb;
      BitPackRoundtrip(buf1, bitPos + 1, count - 1, rem);
      ModSmall(rem, p);
      DivModDouble(value, p);
    }
  }

  // Theorem 4c: Adjacent bitfield composition (arbitrary cross-byte bitfields never interfere!)
  lemma BitPackSequentialRoundtrip(
    buf: seq<Byte>,
    bitPos: nat,
    c1: nat, v1: nat,
    c2: nat, v2: nat
  )
    requires bitPos + c1 + c2 <= |buf| * 8
    ensures var b1 := WriteBits(buf, bitPos, c1, v1);
            var b2 := WriteBits(b1, bitPos + c1, c2, v2);
            ReadBits(b2, bitPos, c1) == v1 % Pow2(c1) &&
            ReadBits(b2, bitPos + c1, c2) == v2 % Pow2(c2)
  {
    var b1 := WriteBits(buf, bitPos, c1, v1);
    var b2 := WriteBits(b1, bitPos + c1, c2, v2);
    BitPackRoundtrip(buf, bitPos, c1, v1);
    BitPackRoundtrip(b1, bitPos + c1, c2, v2);
    forall j: nat | bitPos <= j < bitPos + c1
      ensures GetBit(b2, j) == GetBit(b1, j)
    {
      WriteBitsFrame(b1, bitPos + c1, c2, v2, j);
    }
    ReadBitsExtensional(b2, b1, bitPos, c1);
  }
}
