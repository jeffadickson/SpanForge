# SpanForge

A **zero-allocation, C# 15 union-powered `Span<byte>` and binary parser combinator library** on **.NET 11**, combining the strongest ideas from Rust's binary parsing ecosystem (`nom`, `winnow`, `binrw`, `zerocopy`) with modern C# 15 language features.

## Rust Ecosystem × C# 15 Feature Matrix

| Rust Crate / Concept | SpanForge (C# 15 / .NET 11) | Key Implementation |
| --- | --- | --- |
| `nom::IResult` + `winnow::ModalResult` (`Ok` / `Incomplete` / `Backtrack` / `Cut`) | Zero-boxing `[Union] ParseResult<T>` (`Parsed<T>` \| `Incomplete` \| `Backtrack` \| `Failure`) | 4-field packed layout + `HasValue` + 4 typed `TryGetValue` overloads — exhaustive `switch` with **0 bytes allocated** |
| `winnow::ModalResult<O, EmptyError>` | Register-sized `[Union] ParseResult<T, TError>` (`NoError` / `KindError` / `CompactError`) | Zero GC references; `<= 8 B` (1 register) for `byte`/`ushort`, `<= 16 B` (2 registers) for `ulong`/`double`/`ByteSlice` |
| `nom::Needed` (`Size(n)` / `Unknown`) | Zero-boxing `[Union] Needed` (`ExactBytes` \| `UnknownSize`) | Non-boxing custom `[Union]` struct |
| `Option<T>` (`Some(T)` / `None`) | Zero-boxing `[Union] Option<T>` (`Some<T>` \| `None`) | Used by `Parse.Opt` and protocol headers |
| `winnow` mutable stream + `Checkpoint` | `ref partial struct ByteCursor` + `Save()` / `Reset(Checkpoint)` | O(1) rollback without tuple-threading |
| `nom::bytes` + `nom::complete` / `streaming` | `InputMode.Complete` & `InputMode.Streaming` + `SearchValues<byte>` SIMD | Early mismatch detection in `Tag`, exact `Needed` hints |
| `nom::bits` / `deku` | `ref struct BitCursor` + `cursor.ParseBits(...)` | MSB-first arbitrary cross-byte bitfields + signed two's complement |
| `binrw` scoped endianness & alignment | `Parse.ScopedEndian(...)` + `cursor.Align(n)` | Restores endianness automatically on checkpoint rollback |
| `zerocopy::FromBytes` | `cursor.ReadBlittable<T>()` + `ByteSlice` + C# 15 extension indexer `span[slice]` | 8-byte non-`ref struct` slice handles live inside unions & ASTs with zero payload copies |
| Rust `trait Parser` monomorphization | `IParser<T>` / `StructParsers` & `IParser<T, TError>` / `FastParsers` | JIT-inlined value-type combinators with 0 delegate dispatch and 0 allocations |
| Rust `enum` protocol ASTs | Zero-boxing `[Union]` (`PngChunk`, `WebSocketFrame`), `public union` (`DnsRecordData`), and `public closed record class` (`RespValue`) | Compiler-enforced exhaustiveness across zero-boxing hot-path unions, flat unions, and recursive ASTs |
| Pre-sized collections & nested loops | C# 15 `[with(capacity: count)]` + labeled `break` / `continue` | Used in `Parse.Count`, `Many0`, `FoldMany0`, `DnsParser`, `PngParser` |

## Solution Layout

- **`src/SpanForge/`** — Zero-dependency core library (`net11.0`, C# 15, `TreatWarningsAsErrors=true`):
  - `Core/`: `ParseResult<T>`, `ParseResult<T, TError>` (`NoError`, `KindError`, `CompactError`), `Railroad`, `Needed`, `Option<T>`, `ByteSlice` (+ C# 15 extension indexer), `ByteCursor` (+ `ByteCursor.Fast`), `BitCursor`, `ParseError` (with hex+ASCII caret diagnostics).
  - `Combinators/`: `Parse`, `IParser<T>`, `IParser<T, TError>`, `StructParsers`, and `FastParsers`.
  - `Streaming/`: `StreamingDecoder<T>` incremental chunked stream driver.
- **`src/SpanForge.Protocols/`** — Real-world binary protocol parsers:
  - `PngParser` — PNG magic signature, safe slicing-by-8 IEEE CRC-32, zero-boxing `[Union] PngChunk` (0 B/op), streaming chunks.
  - `WebSocketFrameParser` — RFC 6455 bitfield header via `BitCursor`, zero-boxing `[Union] WebSocketFrame` (0 B/op), `UnmaskInto`.
  - `RespParser` — Redis RESP2 with `public closed record class RespValue` and `Alt`+`Cut` diagnostics.
  - `DnsParser` — RFC 1035 DNS packet with bitfield flags, label compression pointers + cycle guard, `public union DnsRecordData`.
- **`tests/SpanForge.Tests/`** — **68 xUnit tests** covering unions, primitives, varints, bitfields, combinators, byte-by-byte streaming, all 4 protocols, strict `0 bytes` allocation assertions, **CsCheck 5.0.1** property/fuzz tests, **Hedgehog 2.0.4** integrated shrinking, and **Stryker.NET 5.1.0** mutation testing (**92.11%** core / **85.51%** protocols).
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
