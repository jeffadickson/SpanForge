# SpanForge

A **zero-allocation, C# 15 union-powered `Span<byte>` and binary parser combinator library** on **.NET 11**, combining the strongest ideas from Rust's binary parsing ecosystem (`nom`, `winnow`, `binrw`, `zerocopy`) with modern C# 15 language features.

## Rust Ecosystem × C# 15 Feature Matrix

| Rust Crate / Concept | SpanForge (C# 15 / .NET 11) | Key Implementation |
| --- | --- | --- |
| `nom::IResult` + `winnow::ModalResult` (`Ok` / `Incomplete` / `Backtrack` / `Cut`) | Zero-boxing `[Union] ParseResult<T>` (`Parsed<T>` \| `Incomplete` \| `Backtrack` \| `Failure`) | 4-field packed layout + `HasValue` + 4 typed `TryGetValue` overloads — exhaustive `switch` with **0 bytes allocated** |
| `winnow::ModalResult<O, EmptyError>` | Register-sized `[Union] ParseResult<T, TError>` (`NoError` / `KindError` / `CompactError`) & 4-byte `FastStatus` / `[Union] FastResult<T>` | Zero GC references; `FastStatus` (`0 == Parsed`) + `out T` eliminates stack spills across `Delimited` / `Alt` / `Dispatch` trees |
| `nom::Needed` (`Size(n)` / `Unknown`) | Zero-boxing `[Union] Needed` (`ExactBytes` \| `UnknownSize`) | Non-boxing custom `[Union]` struct |
| `Option<T>` (`Some(T)` / `None`) | Zero-boxing `[Union] Option<T>` (`Some<T>` \| `None`) | Used by `Parse.Opt` and protocol headers |
| `winnow` mutable stream + `Checkpoint` | `ref partial struct ByteCursor` + `Save()` / `Reset(Checkpoint)` | O(1) rollback without tuple-threading; unaligned safe `MemoryMarshal` + `BinaryPrimitives` readers |
| `nom::bytes` + `nom::complete` / `streaming` | `InputMode.Complete` & `InputMode.Streaming` + `SearchValues<byte>` SIMD | Early mismatch detection in `Tag`, exact `Needed` hints, Complete-mode shortage constant folding |
| `nom::bits` / `deku` | `ref struct BitCursor` + `cursor.ParseBits(...)` | MSB-first arbitrary cross-byte bitfields + signed two's complement |
| `binrw` scoped endianness & alignment | `Parse.ScopedEndian(...)` + `cursor.Align(n)` | Restores endianness automatically on checkpoint rollback |
| `zerocopy::FromBytes` | `cursor.ReadBlittable<T>()` + `ByteSlice` + C# 15 extension indexer `span[slice]` | 8-byte non-`ref struct` slice handles live inside unions & ASTs with zero payload copies |
| Rust `trait Parser` & `winnow::dispatch!` | `IParser<T>` / `StructParsers`, `IParser<T, TError>` / `FastParsers`, & `ITryParser<T>` / `TryParsers` (`Preceded`, `Delimited`, `Alt`, `Dispatch`, `FoldDispatch`) | JIT-inlined value-type combinators with 0 delegate dispatch and 0 allocations |
| Rust `enum` protocol ASTs | Zero-boxing `[Union]` (`PngChunk`, 16-byte `WebSocketFrame`), `public union` (`DnsRecordData`), and `public closed record class` (`RespValue`) | Compiler-enforced exhaustiveness across zero-boxing hot-path unions, flat unions, and recursive ASTs |
| Pre-sized collections & nested loops | C# 15 `[with(capacity: count)]` + labeled `break` / `continue` | Used in `Parse.Count`, `Many0`, `FoldMany0`, `DnsParser`, `PngParser` |

## Head-to-Head Benchmark vs Rust (`winnow 1.0.5` & `nom 8.0.0`)

Measured on Intel Xeon Platinum 8481C (`net11.0`, C# 15, Release + TieredPGO, `AllowUnsafeBlocks=false`) vs `rustc 1.100.0 --release` (`lto=fat`). All 5 checksums match bit-for-bit, with **0.0 B/op** heap allocations on every SpanForge hot path:

| Benchmark | SpanForge (C# 15 / .NET 11) | Rust `winnow 1.0.5` | Rust `nom 8.0.0` | Alloc (C#) |
| --- | ---: | ---: | ---: | ---: |
| `png_chunks_crc` (6 chunks + IEEE CRC-32) | **121.03 ns** (`cs15/spanforge`) | 165.77 ns | 151.85 ns | 0.0 B/op |
| `ws_frame` (RFC 6455 header + 64-bit unmask) | **10.52 ns** PGO / **10.22 ns** (`cs15/try`) | 10.74 ns | 10.21 ns | 0.0 B/op |
| `delimited_u16` (`[` + BE `u16` + `]`) | **2.32 ns** (`cs15/try`, 430M ops/s) | 1.61 ns | 2.32 ns | 0.0 B/op |
| `tlv_stream16` (16 mixed-tag TLV items) | **46.16 ns** (`cs15/fold`) / **63.34 ns** (`cs15/dispatch`) / **81.04 ns** (`cs15/try`) | 73.87 ns | 31.69 ns | 0.0 B/op |
| `varint_stream32` (32 LEB128 varints) | **67.39 ns** PGO / **66.99 ns** (`cs15/try`) | 59.16 ns | 64.29 ns | 0.0 B/op |

## Solution Layout

- **`src/SpanForge/`** — Zero-dependency core library (`net11.0`, C# 15, `TreatWarningsAsErrors=true`, `AllowUnsafeBlocks=false`):
  - `Core/`: `ParseResult<T>`, `ParseResult<T, TError>` (`NoError`, `KindError`, `CompactError`), `FastStatus`, `FastResult<T>`, `ITryParser<T>` / `TryParsers`, `Railroad`, `Needed`, `Option<T>`, `ByteSlice` (+ C# 15 extension indexer), `ByteCursor` (+ `ByteCursor.Fast`, `ByteCursor.Try`), `BitCursor`, `ParseError` (with hex+ASCII caret diagnostics).
  - `Combinators/`: `Parse`, `IParser<T>`, `IParser<T, TError>`, `StructParsers`, and `FastParsers`.
  - `Streaming/`: `StreamingDecoder<T>` incremental chunked stream driver.
- **`src/SpanForge.Protocols/`** — Real-world binary protocol parsers:
  - `PngParser` — PNG magic signature, flat slicing-by-8+4 IEEE CRC-32, zero-boxing `[Union] PngChunk` (0 B/op), streaming chunks.
  - `WebSocketFrameParser` — RFC 6455 `ParseFrame` & `TryParseFrame`, 16-byte zero-boxing `[Union] WebSocketFrame` (0 B/op), 64-bit/32-bit word-at-a-time `UnmaskInto`.
  - `RespParser` — Redis RESP2 with `public closed record class RespValue` and `Alt`+`Cut` diagnostics.
  - `DnsParser` — RFC 1035 DNS packet with bitfield flags, label compression pointers + cycle guard, `public union DnsRecordData`.
- **`tests/SpanForge.Tests/`** — **77 xUnit tests** covering unions, primitives, varints, bitfields, combinators, byte-by-byte streaming, all 4 protocols, strict `0 bytes` allocation assertions, **CsCheck 5.0.1** property/fuzz tests, **Hedgehog 2.0.4** integrated shrinking, and **Stryker.NET 5.1.0** mutation testing (**88.01%** core / **84.38%** protocols).
- **`benchmarks/`** — Head-to-head benchmarks comparing `SpanForge.Benchmarks` (C# 15 / .NET 11) against `rust-bench` (`winnow 1.0.5` & `nom 8.0.0`).
- **`samples/SpanForge.Demo/`** — End-to-end showcase and WebSocket throughput/allocation micro-benchmark.

## Build, Test, & Mutation Testing

```bash
dotnet build SpanForge.slnx -c Release
dotnet test tests/SpanForge.Tests/SpanForge.Tests.csproj -c Release
dotnet run --project samples/SpanForge.Demo/SpanForge.Demo.csproj -c Release
dotnet run --project benchmarks/SpanForge.Benchmarks/SpanForge.Benchmarks.csproj -c Release

# Stryker.NET 5.1.0 mutation testing (run from tests/SpanForge.Tests)
dotnet tool restore
DOTNET_ROLL_FORWARD=Major dotnet tool run dotnet-stryker -f ../../stryker-config.json
DOTNET_ROLL_FORWARD=Major dotnet tool run dotnet-stryker -f ../../stryker-protocols-config.json
```
