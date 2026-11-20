# SpanForge

A **zero-allocation, C# 15 union-powered `Span<byte>` and binary parser combinator library** on **.NET 11**, combining the strongest ideas from Rust's binary parsing ecosystem (`nom`, `winnow`, `binrw`, `zerocopy`) with modern C# 15 language features.

## Rust Ecosystem × C# 15 Feature Matrix

| Rust Crate / Concept | SpanForge (C# 15 / .NET 11) | Key Implementation |
| --- | --- | --- |
| `nom::IResult` + `winnow::ModalResult` (`Ok` / `Incomplete` / `Backtrack` / `Cut`) | Zero-boxing `[Union] ParseResult<T>` (`Parsed<T>` \| `Incomplete` \| `Backtrack` \| `Failure`) | `HasValue` + 4 typed `TryGetValue` overloads — exhaustive `switch` with **0 bytes allocated** |
| `nom::Needed` (`Size(n)` / `Unknown`) | Zero-boxing `[Union] Needed` (`ExactBytes` \| `UnknownSize`) | Non-boxing custom `[Union]` struct |
| `Option<T>` (`Some(T)` / `None`) | Zero-boxing `[Union] Option<T>` (`Some<T>` \| `None`) | Used by `Parse.Opt` and protocol headers |
| `winnow` mutable stream + `Checkpoint` | `ref struct ByteCursor` + `Save()` / `Reset(Checkpoint)` | O(1) rollback without tuple-threading |
| `nom::bytes` + `nom::complete` / `streaming` | `InputMode.Complete` & `InputMode.Streaming` + `SearchValues<byte>` SIMD | Early mismatch detection in `Tag`, exact `Needed` hints |
| `nom::bits` / `deku` | `ref struct BitCursor` + `cursor.ParseBits(...)` | MSB-first arbitrary cross-byte bitfields + signed two's complement |
| `binrw` scoped endianness & alignment | `Parse.ScopedEndian(...)` + `cursor.Align(n)` | Restores endianness automatically on checkpoint rollback |
| `zerocopy::FromBytes` | `cursor.ReadBlittable<T>()` + `ByteSlice` + C# 15 extension indexer `span[slice]` | 8-byte non-`ref struct` slice handles live inside unions & ASTs with zero payload copies |
| Rust `trait Parser` monomorphization | `IParser<T>` + `StructParsers` (`Delimited`, `Preceded`, `Alt`, `Opt`, `Cut`, `Pair`) | JIT-inlined value-type combinators with 0 delegate dispatch and 0 allocations |
| Rust `enum` protocol ASTs | C# 15 `public union` (`PngChunk`, `DnsRecordData`), custom `[Union]` (`WebSocketFrame`), and `public closed record class` (`RespValue`) | Compiler-enforced exhaustiveness across flat unions, hot-path zero-boxing unions, and recursive ASTs |
| Pre-sized collections & nested loops | C# 15 `[with(capacity: count)]` + labeled `break` / `continue` | Used in `Parse.Count`, `Many0`, `FoldMany0`, `DnsParser`, `PngParser` |

## Solution Layout

- **`src/SpanForge/`** — Zero-dependency core library (`net11.0`, C# 15, `TreatWarningsAsErrors=true`):
  - `Core/`: `ParseResult<T>`, `Needed`, `Option<T>`, `ByteSlice` (+ C# 15 extension indexer), `ByteCursor`, `BitCursor`, `ParseError` (with hex+ASCII caret diagnostics).
  - `Combinators/`: `Parse` (`Alt`, `Opt`, `Cut`, `Peek`, `Recognize`, `WithSlice`, `Preceded`, `Terminated`, `Delimited`, `Pair`, `SeparatedPair`, `ScopedEndian`, `LengthSlice`, `LengthValue`, `Count`, `Many0`, `Many1`, `FoldMany0`, `RepeatInto`, `Separated0`, `AllConsuming`), `IParser<T>`, and `StructParsers`.
  - `Streaming/`: `StreamingDecoder<T>` incremental chunked stream driver.
- **`src/SpanForge.Protocols/`** — Real-world binary protocol parsers:
  - `PngParser` — PNG magic signature, CRC-32 verification, `public union PngChunk`, streaming chunks.
  - `WebSocketFrameParser` — RFC 6455 bitfield header via `BitCursor`, zero-boxing `[Union] WebSocketFrame`, `UnmaskInto`.
  - `RespParser` — Redis RESP2 with `public closed record class RespValue` and `Alt`+`Cut` diagnostics.
  - `DnsParser` — RFC 1035 DNS packet with bitfield flags, label compression pointers + cycle guard, `public union DnsRecordData`.
- **`tests/SpanForge.Tests/`** — 26 tests covering unions, primitives, varints, bitfields, combinators, byte-by-byte streaming, all 4 protocols, and strict `GC.GetAllocatedBytesForCurrentThread() == 0` assertions.
- **`samples/SpanForge.Demo/`** — End-to-end showcase and WebSocket throughput/allocation micro-benchmark.

## Build & Run

```bash
dotnet build SpanForge.slnx -c Release
dotnet run --project tests/SpanForge.Tests/SpanForge.Tests.csproj -c Release
dotnet run --project samples/SpanForge.Demo/SpanForge.Demo.csproj -c Release
```
