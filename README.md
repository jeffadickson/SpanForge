# SpanForge

A **zero-allocation, NativeAOT-ready, C# 15 union-powered `Span<byte>` and binary parser/writer combinator library** on **.NET 11**, combining the strongest ideas from Rust's binary parsing ecosystem (`nom`, `winnow`, `binrw`, `zerocopy`) with modern C# 15 language features.

## Rust Ecosystem × C# 15 Feature Matrix

| Rust Crate / Concept | SpanForge (C# 15 / .NET 11) | Key Implementation |
| --- | --- | --- |
| `nom::IResult` + `winnow::ModalResult` (`Ok` / `Incomplete` / `Backtrack` / `Cut`) | Zero-boxing `[Union] ParseResult<T>` (`Parsed<T>` \| `Incomplete` \| `Backtrack` \| `Failure`) | 4-field packed layout + `HasValue` + 4 typed `TryGetValue` overloads — exhaustive `switch` with **0 bytes allocated** |
| `winnow::ModalResult<O, EmptyError>` | Register-sized `[Union] ParseResult<T, TError>` (`NoError` / `KindError` / `CompactError`) & 4-byte `FastStatus` / `[Union] FastResult<T>` | Zero GC references; `FastStatus` (`0 == Parsed`) + `out T` eliminates stack spills across `Delimited` / `Alt` / `Dispatch` trees |
| `nom::Needed` (`Size(n)` / `Unknown`) | Zero-boxing `[Union] Needed` (`ExactBytes` \| `UnknownSize`) | Non-boxing custom `[Union]` struct |
| `Option<T>` (`Some(T)` / `None`) | Zero-boxing `[Union] Option<T>` (`Some<T>` \| `None`) | Used by `Parse.Opt` and protocol headers |
| `winnow` mutable stream + `Checkpoint` | `ref partial struct ByteCursor` + symmetric `ref struct ByteWriter` + `Save()` / `Reset(Checkpoint)` | O(1) rollback without tuple-threading; unaligned safe `MemoryMarshal` + `BinaryPrimitives` readers & writers |
| `nom::bytes` + `nom::complete` / `streaming` | `InputMode.Complete` & `InputMode.Streaming` + `SearchValues<byte>` SIMD | Early mismatch detection in `Tag`, exact `Needed` hints, Complete-mode shortage constant folding |
| `nom::bits` / `deku` | `ref struct BitCursor` + symmetric `ref struct BitWriter` | MSB-first arbitrary cross-byte bitfields + signed two's complement + deterministic bit masking |
| `binrw` scoped endianness & alignment | `Parse.ScopedEndian(...)`, `cursor.Align(n)`, `writer.TryAlign(n)` | Restores endianness automatically on checkpoint rollback |
| `binrw` declarative binary structs & tagged unions | `SpanForge.Generators` (`[BinaryStruct]`, `[BinaryUnion]`, `[UnionCase]`, `[Magic]`, `[Endian]`, `[Bits]`, `[VarInt]`, `[LengthPrefixed]`, `[FixedBytes]`, `[Align]`, `[Pad]`, `[AssertValue]`) | Incremental Roslyn source generators bundled inside `SpanForge` NuGet package; emit 0-alloc `Parse`, `TryParse`, `TryWrite`, `TryWriteTo`, `WriteTo(IBufferWriter<byte>)`, C# 15 `[Union]`, `IParser<T>`, and `ITryParser<T>` with diagnostics `SFG001`–`SFG005` |
| `tokio-util::codec` async framing | `SpanForge.Pipelines` (`PipeParser`, `PipeSequenceAdapter`, `[Union] PipeParseOutcome<T>`) | Zero-copy single-segment `PipeReader` fast path + pooled multi-segment framing with backpressure & DoS caps |
| `zerocopy::FromBytes` / `IntoBytes` | `cursor.ReadBlittable<T>()` + `writer.TryWriteBlittable<T>()` + `ByteSlice` + C# 15 extension indexer `span[slice]` | 8-byte non-`ref struct` slice handles live inside unions & ASTs with zero payload copies |
| Rust `trait Parser` & `winnow::dispatch!` | `IParser<T>` / `StructParsers`, `IParser<T, TError>` / `FastParsers`, & `ITryParser<T>` / `TryParsers` (`Preceded`, `Delimited`, `Alt`, `Dispatch`, `FoldDispatch`) | JIT-inlined value-type combinators with 0 delegate dispatch and 0 allocations |
| Rust `enum` protocol ASTs | Zero-boxing `[Union]` (`PngChunk`, `WebSocketFrame`, `ProtobufField`, `Http2Frame`, `MqttPacket`, generated `[BinaryUnion]`), `public union` (`DnsRecordData`), and `public closed record class` (`RespValue`) | Compiler-enforced exhaustiveness across zero-boxing hot-path unions, flat unions, and recursive ASTs |
| Pre-sized collections & nested loops | C# 15 `[with(capacity: count)]` + labeled `break` / `continue` | Used in `Parse.Count`, `Many0`, `FoldMany0`, `DnsParser`, `PngParser` |

## Head-to-Head Benchmark vs Rust (`winnow 1.0.5` & `nom 8.0.0`)

Measured on Intel Xeon Platinum 8481C (`net11.0`, C# 15, Release + TieredPGO, `AllowUnsafeBlocks=false`) vs `rustc 1.100.0 --release` (`lto=fat`). All 5 head-to-head checksums match bit-for-bit, with **0.0 B/op** heap allocations on every SpanForge hot path:

| Benchmark | SpanForge (C# 15 / .NET 11) | Rust `winnow 1.0.5` | Rust `nom 8.0.0` | Alloc (C#) |
| --- | ---: | ---: | ---: | ---: |
| `png_chunks_crc` (6 chunks + IEEE CRC-32) | **121.03 ns** (`cs15/spanforge`) | 165.77 ns | 151.85 ns | 0.0 B/op |
| `ws_frame` (RFC 6455 header + 64-bit unmask) | **10.52 ns** PGO / **10.22 ns** (`cs15/try`) | 10.74 ns | 10.21 ns | 0.0 B/op |
| `delimited_u16` (`[` + BE `u16` + `]`) | **2.32 ns** (`cs15/try`, 430M ops/s) | 1.61 ns | 2.32 ns | 0.0 B/op |
| `tlv_stream16` (16 mixed-tag TLV items) | **41.90 ns** (`cs15/fold`) / **63.34 ns** (`cs15/dispatch`) | 73.87 ns | 31.69 ns | 0.0 B/op |
| `varint_stream32` (32 LEB128 varints) | **67.39 ns** PGO / **66.99 ns** (`cs15/try`) | 59.16 ns | 64.29 ns | 0.0 B/op |

### Extended Protocol & Source-Generator Benchmarks (`0.0 B/op`)

| Benchmark | Median | Throughput | Bandwidth | Alloc |
| --- | ---: | ---: | ---: | ---: |
| `gen_struct` (`[BinaryStruct]` `TryParse`: magic + assert + primitives + varint + slice) | **6.95 ns/op** | 143.95 M ops/s | 3,844.0 MiB/s | 0.0 B/op |
| `gen_struct_rw` (`[BinaryStruct]` full `TryParse` + `TryWriteTo` roundtrip) | **19.58 ns/op** | 51.08 M ops/s | 1,364.1 MiB/s | 0.0 B/op |
| `mqtt5_publish` (MQTT v5.0 QoS 1 `PUBLISH` + topic + packet ID + payload) | **38.40 ns/op** | 26.04 M ops/s | 1,043.2 MiB/s | 0.0 B/op |
| `protobuf_msg` (6 mixed Protobuf fields: varint, fixed64, fixed32, length-delimited) | **108.56 ns/op** | 9.21 M ops/s | 386.5 MiB/s | 0.0 B/op |
| `http2_frames` (3 HTTP/2 frames: `SETTINGS` + `HEADERS` + `DATA`) | **114.11 ns/op** | 8.76 M ops/s | 493.1 MiB/s | 0.0 B/op |

## NativeAOT, Trimming & NuGet Packaging

All library packages (`SpanForge`, `SpanForge.Pipelines`, `SpanForge.Protocols`) are annotated with `<IsAotCompatible>true</IsAotCompatible>` and `<IsTrimmable>true</IsTrimmable>` with full trim/AOT/single-file analyzers enabled (`0` IL2xxx/IL3xxx warnings). `SpanForge.nupkg` bundles `SpanForge.Generators.dll` under `analyzers/dotnet/cs/` so installing `SpanForge` automatically activates both `[BinaryStruct]` and `[BinaryUnion]` source generators with zero extra package references.

`SpanForge.Demo` publishes to a standalone **1.7 MB** Linux x64 NativeAOT binary (`dotnet publish samples/SpanForge.Demo/SpanForge.Demo.csproj -c Release -p:EnableNativeAot=true`), parsing and unmasking **68.5M WebSocket frames/sec** with **0 bytes allocated**.

## Solution Layout

- **`src/SpanForge/`** — Zero-dependency core library (`net11.0`, C# 15, `TreatWarningsAsErrors=true`, `AllowUnsafeBlocks=false`, NativeAOT-compatible):
  - `Core/`: `ParseResult<T>`, `ParseResult<T, TError>`, `FastStatus`, `FastResult<T>`, `ITryParser<T>` / `TryParsers`, `Railroad`, `Needed`, `Option<T>`, `ByteSlice` (+ C# 15 extension indexer), `ByteCursor` (+ `ByteCursor.Fast`, `ByteCursor.Try`), `ByteWriter`, `BitCursor`, `BitWriter`, `ParseError` (with hex+ASCII caret diagnostics).
  - `Attributes/`: `binrw`-style declarative attributes (`[BinaryStruct]`, `[BinaryUnion]`, `[UnionCase]`, `[Magic]`, `[Endian]`, `[Bits]`, `[VarInt]`, `[LengthPrefixed]`, `[FixedBytes]`, `[Align]`, `[Pad]`, `[AssertValue]`).
  - `Combinators/`: `Parse`, `IParser<T>`, `IParser<T, TError>`, `StructParsers`, and `FastParsers`.
  - `Streaming/`: `StreamingDecoder<T>` incremental chunked stream driver.
- **`src/SpanForge.Generators/`** — `netstandard2.0` Roslyn incremental source generators (`BinaryStructGenerator` & `BinaryUnionGenerator`) emitting zero-alloc `Parse`, `TryParse`, `TryWrite`, `TryWriteTo`, `WriteTo(IBufferWriter<byte>)`, C# 15 `[Union]` implementations, `Parser`, and `TryParser` with compile-time diagnostics (`SFG001`–`SFG005`). Bundled inside `SpanForge.nupkg`.
- **`src/SpanForge.Pipelines/`** — Zero-dependency .NET 11 `System.IO.Pipelines` integration (`PipeParser`, `PipeSequenceAdapter`, `[Union] PipeParseOutcome<T>`) with single-segment zero-copy fast path, pooled multi-segment framing, `SpanFrameProjector`, backpressure, and DoS buffer caps.
- **`src/SpanForge.Protocols/`** — **7 real-world binary protocol parsers** (all zero-copy, streaming-capable, NativeAOT-compatible):
  - `PngParser`, `WebSocketFrameParser`, `RespParser`, `DnsParser`, `ProtobufParser`, `Http2FrameParser`, `MqttV5Parser`.
- **`tests/SpanForge.Tests/`** & **`tests/SpanForge.Generators.Tests/`** — **115 tests** covering unions, readers/writers, bitfields, combinators, byte-by-byte streaming, `[BinaryStruct]`/`[BinaryUnion]` read/write generation, Roslyn `CSharpGeneratorDriver` diagnostic verification (`SFG001`–`SFG005`), `System.IO.Pipelines` async framing, all 7 protocols, strict `0 bytes` allocation assertions, **CsCheck 5.0.1**, **Hedgehog 2.0.4**, and **Stryker.NET 5.1.0** mutation testing across `core`, `protocols`, and `pipelines`.
- **`benchmarks/`** — Head-to-head benchmarks comparing `SpanForge.Benchmarks` (C# 15 / .NET 11) against `rust-bench` (`winnow 1.0.5` & `nom 8.0.0`), plus Protobuf, HTTP/2, MQTT v5, and generated `[BinaryStruct]` benchmarks.
- **`samples/SpanForge.Demo/`** — End-to-end showcase, NativeAOT publish target, and WebSocket throughput/allocation micro-benchmark.

## Build, Test, Pack, NativeAOT & Mutation Testing

```bash
dotnet build SpanForge.slnx -c Release
dotnet test SpanForge.slnx -c Release
dotnet pack SpanForge.slnx -c Release -o ./artifacts/packages
dotnet publish samples/SpanForge.Demo/SpanForge.Demo.csproj -c Release -p:EnableNativeAot=true -o ./artifacts/aot-demo
dotnet run --project benchmarks/SpanForge.Benchmarks/SpanForge.Benchmarks.csproj -c Release

# Stryker.NET 5.1.0 mutation testing (run from tests/SpanForge.Tests)
dotnet tool restore
DOTNET_ROLL_FORWARD=Major dotnet tool run dotnet-stryker -f ../../stryker-config.json
DOTNET_ROLL_FORWARD=Major dotnet tool run dotnet-stryker -f ../../stryker-protocols-config.json
DOTNET_ROLL_FORWARD=Major dotnet tool run dotnet-stryker -f ../../stryker-pipelines-config.json
```
