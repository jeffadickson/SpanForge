# SpanForge

A **zero-allocation, NativeAOT-ready, formally verified, C# 15 union-powered `Span<byte>` and binary parser/writer combinator library** on **.NET 11**, combining the strongest ideas from Rust's binary parsing ecosystem (`nom`, `winnow`, `binrw`, `zerocopy`) with modern C# 15 language features, safe hardware SIMD/PCLMULQDQ intrinsics, and a full **Dafny** formal verification suite.

## Rust Ecosystem × C# 15 Feature Matrix

| Rust Crate / Concept | SpanForge (C# 15 / .NET 11) | Key Implementation |
| --- | --- | --- |
| `nom::IResult` + `winnow::ModalResult` (`Ok` / `Incomplete` / `Backtrack` / `Cut`) | Zero-boxing `[Union] ParseResult<T>` (`Parsed<T>` \| `Incomplete` \| `Backtrack` \| `Failure`) | 4-field packed layout + `HasValue` + 4 typed `TryGetValue` overloads — exhaustive `switch` with **0 bytes allocated** |
| `winnow::ModalResult<O, EmptyError>` | Register-sized `[Union] ParseResult<T, TError>` (`NoError` / `KindError` / `CompactError`) & 4-byte `FastStatus` / `[Union] FastResult<T>` | Zero GC references; `FastStatus` (`0 == Parsed`) + `out T` across `ByteCursor.Try`, `Delimited`/`Alt`/`Dispatch`, and protocol `TryParse*` fast paths |
| `nom::Needed` (`Size(n)` / `Unknown`) | Zero-boxing `[Union] Needed` (`ExactBytes` \| `UnknownSize`) | Non-boxing custom `[Union]` struct |
| `Option<T>` (`Some(T)` / `None`) | Zero-boxing `[Union] Option<T>` (`Some<T>` \| `None`) | Used by `Parse.Opt` and protocol headers |
| `winnow` mutable stream + `Checkpoint` | `ref partial struct ByteCursor` + symmetric `ref struct ByteWriter` + `Save()` / `Reset(Checkpoint)` | O(1) rollback without tuple-threading; unaligned safe `MemoryMarshal` + `BinaryPrimitives` readers & writers |
| `nom::bytes` + `nom::complete` / `streaming` | `InputMode.Complete` & `InputMode.Streaming` + `SearchValues<byte>` SIMD | Early mismatch detection in `Tag`, exact `Needed` hints, Complete-mode shortage constant folding |
| `nom::bits` / `deku` | `ref struct BitCursor` + symmetric `ref struct BitWriter` | MSB-first arbitrary cross-byte bitfields + 1-byte `TryWriteBool` / 1–2 byte `TryWriteU8` fast paths + signed two's complement |
| `binrw` scoped endianness & alignment | `Parse.ScopedEndian(...)`, `cursor.Align(n)`, `writer.TryAlign(n)` | Restores endianness automatically on checkpoint rollback |
| `binrw` declarative binary structs & tagged unions | `SpanForge.Generators` (`[BinaryStruct]`, `[BinaryUnion]`, `[UnionCase]`, `[Magic]`, `[Endian]`, `[Bits]`, `[VarInt]`, `[LengthPrefixed]`, `[FixedBytes]`, `[Align]`, `[Pad]`, `[AssertValue]`) | Incremental Roslyn source generators bundled inside `SpanForge` NuGet package; emit 0-alloc `Parse`, direct tag-dispatched `TryParse`, compile-time bounds-check-coalesced `TryWrite`/`TryWriteTo`, `WriteTo(IBufferWriter<byte>)`, C# 15 `[Union]`, `IParser<T>`, and `ITryParser<T>` with diagnostics `SFG001`–`SFG005` |
| `tokio-util::codec` async framing | `SpanForge.Pipelines` (`PipeParser`, `PipeSequenceAdapter`, `[Union] PipeParseOutcome<T>`) | Zero-copy single-segment `PipeReader` fast path + `stackalloc byte[256]` small multi-segment fast path + `ArrayPool<byte>` fallback with backpressure & DoS caps |
| `zerocopy::FromBytes` / `IntoBytes` | `cursor.ReadBlittable<T>()` + `writer.TryWriteBlittable<T>()` + `ByteSlice` + C# 15 extension indexer `span[slice]` | 8-byte non-`ref struct` slice handles live inside unions & ASTs with zero payload copies |
| Safe SIMD & Hardware Intrinsics | `Vector256<byte>` / `Vector128<byte>` XOR (`WebSocketFrameParser.UnmaskInto`) + `Pclmulqdq`/`Sse41` IEEE 802.3 CRC-32 (`PngParser.ComputeCrc32`) | 100% safe (`AllowUnsafeBlocks=false`) with portable scalar/slicing-by-8 fallbacks |
| Rust `trait Parser` & `winnow::dispatch!` | `IParser<T>` / `StructParsers`, `IParser<T, TError>` / `FastParsers`, & `ITryParser<T>` / `TryParsers` (`Preceded`, `Delimited`, `Alt`, `Dispatch`, `FoldDispatch`) | JIT-inlined value-type combinators with 0 delegate dispatch and 0 allocations |
| Rust `enum` protocol ASTs | Zero-boxing `[Union]` (`PngChunk`, `WebSocketFrame`, `ProtobufField`, `Http2Frame`, `MqttPacket`, generated `[BinaryUnion]`), `public union` (`DnsRecordData`), and `public closed record class` (`RespValue`) | Compiler-enforced exhaustiveness across zero-boxing hot-path unions, flat unions, and recursive ASTs |
| Formal Verification | Dafny 4.11 (`formal/`) + verified Dafny-to-C# differential oracle (`tests/SpanForge.Tests/Formal/`) | **147 verified lemmas/assertions** proving cursor rollback soundness, LEB128/ZigZag/QUIC/BitPack bijections, DNS compression termination, and streaming–complete equivalence |

## Benchmarks: Before / After Optimizations & Rust Comparison

Measured sequentially and uncontended on Intel Xeon @ 2.30 GHz (x64, AVX2/BMI2/PCLMULQDQ, `.NET 11.0.100`, C# 15, Release + TieredPGO, `AllowUnsafeBlocks=false`) vs `rustc 1.100.0 --release` (`lto=fat`, `winnow 1.0.5` & `nom 8.0.0`). All head-to-head checksums match bit-for-bit with **0.0 B/op** heap allocations on every SpanForge hot path:

### Head-to-Head vs Rust (`winnow 1.0.5` & `nom 8.0.0`)

| Benchmark | SpanForge Before (`8b9c64f`) | SpanForge After | Rust `winnow 1.0.5` | Rust `nom 8.0.0` | Alloc (C#) |
| --- | ---: | ---: | ---: | ---: | ---: |
| `png_chunks_crc` (6 chunks + IEEE CRC-32) | 188.62 ns (`cs15/spanforge`) | **146.12 ns** (`cs15/try`, **1.29× faster**) | 246.04 ns | 231.67 ns | 0.0 B/op |
| `ws_frame` (RFC 6455 header + unmask) | 22.41 ns (`cs15/try`) | **21.33–23.92 ns** (`cs15/try`) | 25.59 ns | 20.70 ns | 0.0 B/op |
| `delimited_u16` (`[` + BE `u16` + `]`) | 5.86 ns (`cs15/try`) | **5.60 ns** (`cs15/try`, 178.7M ops/s) | 3.75 ns | 5.49 ns | 0.0 B/op |
| `tlv_stream16` (16 mixed-tag TLV items) | 102.44 ns (`cs15/fold`) | **88.34–96.54 ns** (`cs15/fold`) | 123.14 ns | 63.32 ns | 0.0 B/op |
| `varint_stream32` (32 LEB128 varints) | 136.47 ns (`cs15/try`) | **126.18 ns** (`cs15/try`, **1.08× faster**) | 120.32 ns | 126.24 ns | 0.0 B/op |

### Protocol `TryParse*`, Source-Generator & Safe SIMD Kernels (`0.0 B/op`)

| Benchmark | Before (`8b9c64f`) | After | Speedup | Throughput / Bandwidth | Alloc |
| --- | ---: | ---: | ---: | ---: | ---: |
| `http2_frames` (3 HTTP/2 frames: `SETTINGS` + `HEADERS` + `DATA`) | 270.72 ns | **28.22 ns** (`cs15/try`) | **9.59×** | 35.44 M ops/s · 1,994.0 MiB/s | 0.0 B/op |
| `mqtt5_publish` (MQTT v5.0 QoS 1 `PUBLISH` + topic + packet ID + payload) | 76.25 ns | **25.16 ns** (`cs15/try`) | **3.03×** | 39.74 M ops/s · 1,591.9 MiB/s | 0.0 B/op |
| `protobuf_msg` (6 mixed Protobuf fields: varint, fixed64, fixed32, length-delimited) | 165.25 ns | **72.46 ns** (`cs15/try`) | **2.28×** | 13.80 M ops/s · 579.1 MiB/s | 0.0 B/op |
| `gen_struct` (`[BinaryStruct]` coalesced `TryParse`) | 17.29 ns | **17.10 ns** | 1.01× | 58.48 M ops/s · 1,561.6 MiB/s | 0.0 B/op |
| `gen_struct_rw` (`[BinaryStruct]` coalesced `TryParse` + `TryWriteTo` roundtrip) | 41.67 ns | **30.53–35.73 ns** | **1.17–1.36×** | 27.99 M ops/s · 747.3 MiB/s | 0.0 B/op |
| `ws_unmask_1k` (1 KiB RFC 6455 XOR via `Vector256<byte>` AVX2) | — | **23.73–43.03 ns** | — | **22.7–40.2 GiB/s** | 0.0 B/op |
| `png_crc_4k` (4 KiB IEEE 802.3 CRC-32 via `Pclmulqdq`/`Sse41`) | — | **253.19–271.40 ns** | — | **14.4–15.1 GiB/s** | 0.0 B/op |

### RyuJIT Profiling Notes (`DOTNET_JitDisasm`)

- **Zero-GC-ref `FastStatus` + `out T` protocol fast paths**: Outlining cold `ParseError` construction and eliminating `ParseResult<T>` GC-ref fields in `Http2FrameParser.TryParseFrame`, `MqttV5Parser.TryParsePacket`, `ProtobufParser.TryParseField`, and `PngParser.TryParseSignature`/`TryParseChunk` cut `BenchPng`'s Tier1-OSR stack frame from **600 B** (with a 384 B zero-init loop) down to **176 B** and delivered **2.3×–9.6×** speedups on HTTP/2, MQTT v5, and Protobuf.
- **Compile-time fixed-size bounds-check coalescing**: `BinaryStructGenerator` now coalesces adjacent fixed-size field reads (`TryTakeSpan`) and writes (`TryReserve`) into a single range check + constant-offset `BinaryPrimitives` calls, eliminating `ByteWriter` stack spills in `gen_struct_rw`.
- **BMI2 `PEXT` vs. mask-free unrolled LEB128**: We benchmarked `Bmi2.X64.ParallelBitExtract` + `BitOperations.TrailingZeroCount` against a single `rem >= 10` bounds check + mask-free `(b - 1UL) << shift` unrolled cascade. On back-to-back streams (`varint_stream32`), branchless `PEXT` measured **190.73 ns** vs. **126.18 ns** for the mask-free unrolled cascade because `load -> not -> and -> tzcnt -> shr -> add` creates a ~9-cycle non-speculatable loop-carried dependency on `pos`, whereas the unrolled branch cascade lets the CPU branch predictor speculate `pos + k` in ~2 cycles.

## Formal Verification (`formal/`) & Differential Oracle Testing

SpanForge ships a mechanically checked **Dafny 4.11** formal verification suite (**147 verified lemmas and assertions, 0 errors**, enforced in CI via `dafny-verify`):

- **[`formal/CursorSoundness.dfy`](formal/CursorSoundness.dfy)** — Proves `ByteCursor` bounds safety (`0 <= pos <= |buf|`), monotonic position progress on `Parsed`, and exact `Checkpoint` rollback invariance (`pos`, `endian`, `mode`) on `Backtrack`, `Incomplete`, and `Failure` across primitives and `ScopedEndian`.
- **[`formal/CodecBijections.dfy`](formal/CodecBijections.dfy)** — Proves exact codec bijections:
  - **ZigZag 64-bit & 32-bit**: both hardware two's-complement bitvectors (`bv64`/`bv32`) and mathematical `int64` (`[-2^63 .. 2^63 - 1]`).
  - **LEB128 64-bit**: `1..10` byte length bound with `b[9] <= 1`, `Decode(Encode(v) || suffix) == Parsed(v)`, canonical encoding uniqueness (injectivity), and 10-byte overflow rejection (`b[9] > 1 => Backtrack`).
  - **RFC 9000 QUIC VarInts**: 1/2/4/8-byte roundtrip bijection over all `0 <= v < 2^62`.
  - **MSB-first `BitWriter` <-> `BitCursor`**: bit-level frame preservation (untouched surrounding bits on dirty buffers), arbitrary cross-byte roundtrip (`ReadBits(WriteBits(buf, bitPos, count, v)) == v mod 2^count`), and adjacent bitfield non-interference.
- **[`formal/DnsTermination.dfy`](formal/DnsTermination.dfy)** — Proves RFC 1035 DNS compression-pointer loop termination via a strict lexicographic `decreases (MaxJumps - jumps, |buf| - pos)` measure, guaranteeing cyclic pointers (`0xC0 0x00`, 2-cycles, self-loops) always terminate with `NameFailure` and restore `startPos`.
- **[`formal/StreamingAndProtocols.dfy`](formal/StreamingAndProtocols.dfy)** — Proves Streaming–Complete chunk-splitting equivalence (`FeedChunks` across arbitrary byte splits matches one-shot `Complete` parsing), HTTP/2 frame padding & priority non-underflow invariants (all 10 RFC 7540 frame types), MQTT v5 1–4 byte Variable Byte Integer bijection and `PUBLISH` slice bounds, and `PipeParser` DoS `MaxBufferSize` & rollback invariants.
- **Verified Dafny-to-C# Differential Oracle**: [`formal/generate-oracle.sh`](formal/generate-oracle.sh) compiles our verified Dafny modules via `dafny translate cs` into `tests/SpanForge.Tests/Formal/SpanForgeOracle.g.cs` (checked for drift in CI via `--check`), and `tests/SpanForge.Tests/Formal/DafnyDifferentialTests.cs` runs CsCheck differential property tests comparing SpanForge's zero-alloc `Span<byte>` implementation directly against the compiled verified oracle.

## NativeAOT, Trimming & NuGet Packaging

All library packages (`SpanForge`, `SpanForge.Pipelines`, `SpanForge.Protocols`) are annotated with `<IsAotCompatible>true</IsAotCompatible>` and `<IsTrimmable>true</IsTrimmable>` with full trim/AOT/single-file analyzers enabled (`0` IL2xxx/IL3xxx warnings). `SpanForge.nupkg` bundles `SpanForge.Generators.dll` under `analyzers/dotnet/cs/` so installing `SpanForge` automatically activates both `[BinaryStruct]` and `[BinaryUnion]` source generators with zero extra package references.

`SpanForge.Demo` publishes to a standalone **~1.7 MB** Linux x64 NativeAOT binary (`dotnet publish samples/SpanForge.Demo/SpanForge.Demo.csproj -c Release -p:EnableNativeAot=true`) with **0 bytes allocated** on the hot path.

## Solution Layout

- **`src/SpanForge/`** — Zero-dependency core library (`net11.0`, C# 15, `TreatWarningsAsErrors=true`, `AllowUnsafeBlocks=false`, NativeAOT-compatible):
  - `Core/`: `ParseResult<T>`, `ParseResult<T, TError>`, `FastStatus`, `FastResult<T>`, `ITryParser<T>` / `TryParsers`, `Railroad`, `Needed`, `Option<T>`, `ByteSlice` (+ C# 15 extension indexer), `ByteCursor` (+ `ByteCursor.Fast`, `ByteCursor.Try`), `ByteWriter`, `BitCursor`, `BitWriter`, `ParseError` (with hex+ASCII caret diagnostics).
  - `Attributes/`: `binrw`-style declarative attributes (`[BinaryStruct]`, `[BinaryUnion]`, `[UnionCase]`, `[Magic]`, `[Endian]`, `[Bits]`, `[VarInt]`, `[LengthPrefixed]`, `[FixedBytes]`, `[Align]`, `[Pad]`, `[AssertValue]`).
  - `Combinators/`: `Parse`, `IParser<T>`, `IParser<T, TError>`, `StructParsers`, and `FastParsers`.
  - `Streaming/`: `StreamingDecoder<T>` incremental chunked stream driver.
- **`src/SpanForge.Generators/`** — `netstandard2.0` Roslyn incremental source generators (`BinaryStructGenerator` & `BinaryUnionGenerator`) emitting zero-alloc `Parse`, direct tag-dispatched `TryParse`, coalesced `TryWrite`/`TryWriteTo`, `WriteTo(IBufferWriter<byte>)`, C# 15 `[Union]` implementations, `Parser`, and `TryParser` with compile-time diagnostics (`SFG001`–`SFG005`). Bundled inside `SpanForge.nupkg`.
- **`src/SpanForge.Pipelines/`** — Zero-dependency .NET 11 `System.IO.Pipelines` integration (`PipeParser`, `PipeSequenceAdapter`, `[Union] PipeParseOutcome<T>`) with single-segment zero-copy fast path, `stackalloc byte[256]` small multi-segment fast path, pooled large multi-segment framing, `SpanFrameProjector`, backpressure, and DoS buffer caps.
- **`src/SpanForge.Protocols/`** — **7 real-world binary protocol parsers** (all zero-copy, streaming-capable, NativeAOT-compatible):
  - `PngParser` (with `Pclmulqdq` hardware CRC-32 & `TryParse*`), `WebSocketFrameParser` (with `Vector256`/`Vector128` SIMD unmasking & `TryParseFrame`), `RespParser`, `DnsParser`, `ProtobufParser` (with `TryParseField`), `Http2FrameParser` (with `TryParseFrame`), `MqttV5Parser` (with unrolled 1–4 byte VBI & `TryParsePacket`).
- **`formal/`** — Dafny 4.11 formal verification suite (`CursorSoundness.dfy`, `CodecBijections.dfy`, `DnsTermination.dfy`, `StreamingAndProtocols.dfy`, `generate-oracle.sh`).
- **`tests/SpanForge.Tests/`** & **`tests/SpanForge.Generators.Tests/`** — **132 tests** (`115` + `17`) covering unions, readers/writers, bitfields, combinators, byte-by-byte streaming, `[BinaryStruct]`/`[BinaryUnion]` read/write generation, Roslyn `CSharpGeneratorDriver` diagnostic verification (`SFG001`–`SFG005`), `System.IO.Pipelines` async framing, all 7 protocols, safe SIMD/PCLMULQDQ bit-exactness, strict `0 bytes` allocation assertions, **CsCheck 5.0.1**, **Hedgehog 2.0.4**, **Dafny-to-C# differential oracle tests**, and **Stryker.NET 5.1.0** mutation testing (**88.48%** `core`, **86.18%** `protocols`, **86.63%** `pipelines`).
- **`benchmarks/`** — Head-to-head benchmarks comparing `SpanForge.Benchmarks` (C# 15 / .NET 11) against `rust-bench` (`winnow 1.0.5` & `nom 8.0.0`), plus Protobuf, HTTP/2, MQTT v5, generated `[BinaryStruct]`, `Vector256` WebSocket unmask, and `Pclmulqdq` PNG CRC-32 benchmarks.
- **`samples/SpanForge.Demo/`** — End-to-end showcase, NativeAOT publish target, and WebSocket throughput/allocation micro-benchmark.

## Build, Test, Pack, NativeAOT, Formal Verification & Mutation Testing

```bash
dotnet build SpanForge.slnx -c Release
dotnet test SpanForge.slnx -c Release
dotnet pack SpanForge.slnx -c Release -o ./artifacts/packages
dotnet publish samples/SpanForge.Demo/SpanForge.Demo.csproj -c Release -p:EnableNativeAot=true -o ./artifacts/aot-demo
dotnet run --project benchmarks/SpanForge.Benchmarks/SpanForge.Benchmarks.csproj -c Release

# Dafny 4.11 formal verification & differential oracle sync check
dafny verify --verification-time-limit=30 formal/CursorSoundness.dfy formal/CodecBijections.dfy formal/DnsTermination.dfy formal/StreamingAndProtocols.dfy
./formal/generate-oracle.sh --check

# Stryker.NET 5.1.0 mutation testing (run from tests/SpanForge.Tests)
dotnet tool restore
DOTNET_ROLL_FORWARD=Major dotnet tool run dotnet-stryker -f ../../stryker-config.json
DOTNET_ROLL_FORWARD=Major dotnet tool run dotnet-stryker -f ../../stryker-protocols-config.json
DOTNET_ROLL_FORWARD=Major dotnet tool run dotnet-stryker -f ../../stryker-pipelines-config.json
```
