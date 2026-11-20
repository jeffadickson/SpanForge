use std::hint::black_box;
use std::time::Instant;

// ============================================================================
// Shared deterministic datasets & IEEE 802.3 CRC-32 (matches C# bit-for-bit)
// ============================================================================

const RING_SIZE: usize = 256;

fn build_crc_table() -> [u32; 256] {
    let mut table = [0u32; 256];
    let mut i = 0u32;
    while i < 256 {
        let mut c = i;
        let mut k = 0;
        while k < 8 {
            c = if (c & 1) != 0 {
                0xEDB8_8320u32 ^ (c >> 1)
            } else {
                c >> 1
            };
            k += 1;
        }
        table[i as usize] = c;
        i += 1;
    }
    table
}

static CRC_TABLE: [u32; 256] = {
    let mut table = [0u32; 256];
    let mut i = 0usize;
    while i < 256 {
        let mut c = i as u32;
        let mut k = 0;
        while k < 8 {
            c = if (c & 1) != 0 {
                0xEDB8_8320u32 ^ (c >> 1)
            } else {
                c >> 1
            };
            k += 1;
        }
        table[i] = c;
        i += 1;
    }
    table
};

#[inline]
fn compute_crc32(data: &[u8]) -> u32 {
    let mut crc = 0xFFFF_FFFFu32;
    for &b in data {
        crc = CRC_TABLE[((crc ^ b as u32) & 0xFF) as usize] ^ (crc >> 8);
    }
    !crc
}

fn gen_ws_ring() -> Vec<Vec<u8>> {
    (0..RING_SIZE)
        .map(|i| {
            let opcode: u8 = match i % 3 {
                0 => 1, // Text
                1 => 2, // Binary
                _ => 9, // Ping
            };
            let mask = [
                (0x37u8) ^ (i as u8),
                (0xFAu8) ^ ((i.wrapping_mul(3)) as u8),
                (0x21u8) ^ ((i.wrapping_mul(7)) as u8),
                (0x3Du8) ^ ((i.wrapping_mul(11)) as u8),
            ];
            let mut buf = Vec::with_capacity(19);
            buf.push(0x80 | opcode);
            buf.push(0x80 | 13);
            buf.extend_from_slice(&mask);
            for k in 0..13usize {
                let plain = ((i * 17 + k * 31 + 65) & 0x7F) as u8;
                buf.push(plain ^ mask[k & 3]);
            }
            buf
        })
        .collect()
}

fn gen_delimited_ring() -> Vec<Vec<u8>> {
    (0..RING_SIZE)
        .map(|i| {
            let v = ((i * 257 + 0x1234) & 0xFFFF) as u16;
            vec![b'[', (v >> 8) as u8, (v & 0xFF) as u8, b']']
        })
        .collect()
}

fn encode_leb128(mut v: u64, out: &mut Vec<u8>) {
    loop {
        let mut b = (v & 0x7F) as u8;
        v >>= 7;
        if v != 0 {
            b |= 0x80;
        }
        out.push(b);
        if v == 0 {
            break;
        }
    }
}

fn gen_varint_ring() -> Vec<Vec<u8>> {
    (0..RING_SIZE)
        .map(|i| {
            let mut buf = Vec::with_capacity(160);
            for j in 0..32usize {
                let val: u64 = match j & 3 {
                    0 => ((i + j) & 0x7F) as u64,
                    1 => (((i * 53 + j * 19) & 0x3FFF) + 128) as u64,
                    2 => (((i as u64 * 100_003 + j as u64 * 7_919) & 0x0FFF_FFFF) + 16_384) as u64,
                    _ => (((i as u64) + 1) << 40) | ((j as u64) * 0x1234_5678),
                };
                encode_leb128(val, &mut buf);
            }
            buf
        })
        .collect()
}

fn gen_tlv_ring() -> Vec<Vec<u8>> {
    (0..RING_SIZE)
        .map(|i| {
            let mut buf = Vec::with_capacity(200);
            for j in 0..16usize {
                match j % 3 {
                    0 => {
                        buf.push(0x01);
                        let v = (i * 1000 + j) as u32;
                        buf.extend_from_slice(&v.to_be_bytes());
                    }
                    1 => {
                        buf.push(0x02);
                        let v = ((i + 1) as f64) * 1.5 + (j as f64);
                        buf.extend_from_slice(&v.to_be_bytes());
                    }
                    _ => {
                        buf.push(0x03);
                        buf.extend_from_slice(&8u16.to_be_bytes());
                        for k in 0..8usize {
                            buf.push(((i + j + k) & 0xFF) as u8);
                        }
                    }
                }
            }
            buf
        })
        .collect()
}

fn build_png_chunk(fourcc: &[u8; 4], payload: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(12 + payload.len());
    out.extend_from_slice(&(payload.len() as u32).to_be_bytes());
    out.extend_from_slice(fourcc);
    out.extend_from_slice(payload);
    let crc = compute_crc32(&out[4..]);
    out.extend_from_slice(&crc.to_be_bytes());
    out
}

fn gen_png_ring() -> Vec<Vec<u8>> {
    let _ = build_crc_table();
    (0..RING_SIZE)
        .map(|i| {
            let mut png = Vec::with_capacity(180);
            png.extend_from_slice(&[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            let w = (256 + i) as u32;
            let h = (128 + (i & 63)) as u32;
            let mut ihdr = [0u8; 13];
            ihdr[0..4].copy_from_slice(&w.to_be_bytes());
            ihdr[4..8].copy_from_slice(&h.to_be_bytes());
            ihdr[8] = 8;
            ihdr[9] = 2; // Truecolor
            png.extend_from_slice(&build_png_chunk(b"IHDR", &ihdr));
            png.extend_from_slice(&build_png_chunk(b"PLTE", &[255, 0, 0, 0, 255, 0]));
            png.extend_from_slice(&build_png_chunk(b"tEXt", b"Author\0Bench"));
            let mut idat = [0u8; 32];
            for k in 0..32 {
                idat[k] = ((i * 7 + k * 13) & 0xFF) as u8;
            }
            png.extend_from_slice(&build_png_chunk(b"IDAT", &idat));
            png.extend_from_slice(&build_png_chunk(b"gAMA", &[0x00, 0x00, 0xB1, 0x8F]));
            png.extend_from_slice(&build_png_chunk(b"IEND", &[]));
            png
        })
        .collect()
}

// ============================================================================
// Shared Parsed Domain Enums
// ============================================================================

enum WsFrame<'a> {
    Continuation(bool, Option<u32>, &'a [u8]),
    Text(bool, Option<u32>, &'a [u8]),
    Binary(bool, Option<u32>, &'a [u8]),
    Close(Option<u32>, Option<u16>, &'a [u8]),
    Ping(Option<u32>, &'a [u8]),
    Pong(Option<u32>, &'a [u8]),
}

#[inline]
fn unmask_sum(payload: &[u8], mask_key: Option<u32>, scratch: &mut [u8; 32]) -> u64 {
    let n = payload.len();
    if let Some(mk) = mask_key {
        let m = mk.to_be_bytes();
        for i in 0..n {
            scratch[i] = payload[i] ^ m[i & 3];
        }
    } else {
        scratch[..n].copy_from_slice(payload);
    }
    let mut s = 0u64;
    for i in 0..n {
        s = s.wrapping_add(scratch[i] as u64);
    }
    s
}

enum TlvItem<'a> {
    U32(u32),
    F64(f64),
    Bytes(&'a [u8]),
}

enum PngChunk<'a> {
    Ihdr { width: u32, height: u32 },
    Plte { entry_count: usize },
    Idat(&'a [u8]),
    Text { kw: &'a [u8], txt: &'a [u8] },
    Iend,
    Unknown { type_code: u32, _data: &'a [u8] },
}

// ============================================================================
// 1. WINNOW 1.0.5 implementations
// ============================================================================

mod bench_winnow {
    use super::*;
    use winnow::binary::bits::{bits, take as take_bits};
    use winnow::binary::{be_f64, be_u16, be_u32, be_u64, be_u8, length_take};
    use winnow::combinator::{alt, delimited, preceded};
    use winnow::error::{ContextError, ErrMode};
    use winnow::token::{literal, take, take_until};
    use winnow::Parser;

    type WResult<T> = winnow::ModalResult<T, ContextError>;

    #[inline]
    pub fn parse_ws<'a>(input: &mut &'a [u8]) -> WResult<WsFrame<'a>> {
        let (fin, _rsv, opcode, masked, len7): (u8, u8, u8, u8, u8) = bits::<_, _, ErrMode<ContextError>, _, _>((
            take_bits::<_, u8, _, _>(1usize),
            take_bits::<_, u8, _, _>(3usize),
            take_bits::<_, u8, _, _>(4usize),
            take_bits::<_, u8, _, _>(1usize),
            take_bits::<_, u8, _, _>(7usize),
        ))
        .parse_next(input)?;

        let payload_len: usize = match len7 {
            0..=125 => len7 as usize,
            126 => be_u16.parse_next(input)? as usize,
            _ => be_u64.parse_next(input)? as usize,
        };

        let mask = if masked != 0 {
            Some(be_u32.parse_next(input)?)
        } else {
            None
        };

        let payload = take(payload_len).parse_next(input)?;
        let fin_b = fin != 0;
        let frame = match opcode {
            0 => WsFrame::Continuation(fin_b, mask, payload),
            1 => WsFrame::Text(fin_b, mask, payload),
            2 => WsFrame::Binary(fin_b, mask, payload),
            8 => WsFrame::Close(mask, None, payload),
            9 => WsFrame::Ping(mask, payload),
            10 => WsFrame::Pong(mask, payload),
            _ => return Err(ErrMode::Cut(ContextError::new())),
        };
        Ok(frame)
    }

    #[inline]
    pub fn parse_delimited_u16(input: &mut &[u8]) -> WResult<u16> {
        delimited(b'[', be_u16, b']').parse_next(input)
    }

    #[inline]
    pub fn parse_varint_u64(input: &mut &[u8]) -> WResult<u64> {
        let mut result = 0u64;
        let mut shift = 0u32;
        for i in 0..10 {
            let b = be_u8.parse_next(input)?;
            if i == 9 && b > 1 {
                return Err(ErrMode::Backtrack(ContextError::new()));
            }
            result |= ((b & 0x7F) as u64) << shift;
            if (b & 0x80) == 0 {
                return Ok(result);
            }
            shift += 7;
        }
        Err(ErrMode::Backtrack(ContextError::new()))
    }

    #[inline]
    pub fn parse_tlv_item<'a>(input: &mut &'a [u8]) -> WResult<TlvItem<'a>> {
        alt((
            preceded(0x01u8, be_u32).map(TlvItem::U32),
            preceded(0x02u8, be_f64).map(TlvItem::F64),
            preceded(0x03u8, length_take(be_u16)).map(TlvItem::Bytes),
        ))
        .parse_next(input)
    }

    #[inline]
    pub fn parse_png_chunk<'a>(input: &mut &'a [u8]) -> WResult<PngChunk<'a>> {
        let data_len = be_u32.parse_next(input)? as usize;
        let crc_start = *input;
        let type_code = be_u32.parse_next(input)?;
        let data = take(data_len).parse_next(input)?;
        let expected_crc = be_u32.parse_next(input)?;

        let computed = compute_crc32(&crc_start[..4 + data_len]);
        if computed != expected_crc {
            return Err(ErrMode::Cut(ContextError::new()));
        }

        let mut sub = data;
        let chunk = match type_code {
            0x49484452 => {
                let width = be_u32.parse_next(&mut sub)?;
                let height = be_u32.parse_next(&mut sub)?;
                let _rest = take(5usize).parse_next(&mut sub)?;
                PngChunk::Ihdr { width, height }
            }
            0x504C5445 => PngChunk::Plte { entry_count: data.len() / 3 },
            0x49444154 => PngChunk::Idat(data),
            0x74455874 => {
                let kw = take_until(0.., 0u8).parse_next(&mut sub)?;
                let _nul = be_u8.parse_next(&mut sub)?;
                PngChunk::Text { kw, txt: sub }
            }
            0x49454E44 => PngChunk::Iend,
            _ => PngChunk::Unknown { type_code, _data: data },
        };
        Ok(chunk)
    }

    #[inline]
    pub fn parse_png_doc_checksum(input: &mut &[u8]) -> WResult<u64> {
        literal(&[0x89u8, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A][..]).parse_next(input)?;
        let mut sum = 0u64;
        loop {
            match parse_png_chunk(input)? {
                PngChunk::Ihdr { width, height } => sum = sum.wrapping_add(width as u64 + height as u64),
                PngChunk::Plte { entry_count } => sum = sum.wrapping_add(entry_count as u64),
                PngChunk::Text { kw, txt } => sum = sum.wrapping_add((kw.len() + txt.len()) as u64),
                PngChunk::Idat(d) => sum = sum.wrapping_add(d[0] as u64 + d.len() as u64),
                PngChunk::Unknown { type_code, .. } => sum = sum.wrapping_add(type_code as u64),
                PngChunk::Iend => {
                    sum = sum.wrapping_add(1);
                    break;
                }
            }
        }
        Ok(sum)
    }
}

// ============================================================================
// 2. NOM 8.0.0 implementations
// ============================================================================

mod bench_nom {
    use super::*;
    use nom::bits::bits;
    use nom::bits::complete::take as take_bits;
    use nom::branch::alt;
    use nom::bytes::complete::{tag, take, take_until};
    use nom::combinator::map;
    use nom::error::Error;
    use nom::multi::length_data;
    use nom::number::complete::{be_f64, be_u16, be_u32, be_u64, be_u8};
    use nom::sequence::{delimited, preceded};
    use nom::{IResult, Parser};

    #[inline]
    pub fn parse_ws(input: &[u8]) -> IResult<&[u8], WsFrame<'_>> {
        let (input, (fin, _rsv, opcode, masked, len7)): (&[u8], (u8, u8, u8, u8, u8)) =
            bits::<_, _, Error<(&[u8], usize)>, _, _>((
                take_bits(1usize),
                take_bits(3usize),
                take_bits(4usize),
                take_bits(1usize),
                take_bits(7usize),
            ))
            .parse(input)?;

        let (input, payload_len) = match len7 {
            0..=125 => (input, len7 as usize),
            126 => {
                let (i, l) = be_u16(input)?;
                (i, l as usize)
            }
            _ => {
                let (i, l) = be_u64(input)?;
                (i, l as usize)
            }
        };

        let (input, mask) = if masked != 0 {
            let (i, m) = be_u32(input)?;
            (i, Some(m))
        } else {
            (input, None)
        };

        let (input, payload) = take(payload_len)(input)?;
        let fin_b = fin != 0;
        let frame = match opcode {
            0 => WsFrame::Continuation(fin_b, mask, payload),
            1 => WsFrame::Text(fin_b, mask, payload),
            2 => WsFrame::Binary(fin_b, mask, payload),
            8 => WsFrame::Close(mask, None, payload),
            9 => WsFrame::Ping(mask, payload),
            10 => WsFrame::Pong(mask, payload),
            _ => return Err(nom::Err::Failure(Error::new(input, nom::error::ErrorKind::Tag))),
        };
        Ok((input, frame))
    }

    #[inline]
    pub fn parse_delimited_u16(input: &[u8]) -> IResult<&[u8], u16> {
        delimited(tag(&b"["[..]), be_u16, tag(&b"]"[..])).parse(input)
    }

    #[inline]
    pub fn parse_varint_u64(mut input: &[u8]) -> IResult<&[u8], u64> {
        let mut result = 0u64;
        let mut shift = 0u32;
        for i in 0..10 {
            let (rest, b) = be_u8(input)?;
            input = rest;
            if i == 9 && b > 1 {
                return Err(nom::Err::Error(Error::new(input, nom::error::ErrorKind::Verify)));
            }
            result |= ((b & 0x7F) as u64) << shift;
            if (b & 0x80) == 0 {
                return Ok((input, result));
            }
            shift += 7;
        }
        Err(nom::Err::Error(Error::new(input, nom::error::ErrorKind::Verify)))
    }

    #[inline]
    pub fn parse_tlv_item(input: &[u8]) -> IResult<&[u8], TlvItem<'_>> {
        alt((
            map(preceded(tag(&[0x01u8][..]), be_u32), TlvItem::U32),
            map(preceded(tag(&[0x02u8][..]), be_f64), TlvItem::F64),
            map(preceded(tag(&[0x03u8][..]), length_data(be_u16)), TlvItem::Bytes),
        ))
        .parse(input)
    }

    #[inline]
    pub fn parse_png_chunk(input: &[u8]) -> IResult<&[u8], PngChunk<'_>> {
        let (rest, data_len) = be_u32(input)?;
        let data_len = data_len as usize;
        let crc_start = rest;
        let (rest, type_code) = be_u32(rest)?;
        let (rest, data) = take(data_len)(rest)?;
        let (rest, expected_crc) = be_u32(rest)?;

        let computed = compute_crc32(&crc_start[..4 + data_len]);
        if computed != expected_crc {
            return Err(nom::Err::Failure(Error::new(rest, nom::error::ErrorKind::Verify)));
        }

        let chunk = match type_code {
            0x49484452 => {
                let (s, width) = be_u32(data)?;
                let (s, height) = be_u32(s)?;
                let (_, _) = take(5usize)(s)?;
                PngChunk::Ihdr { width, height }
            }
            0x504C5445 => PngChunk::Plte { entry_count: data.len() / 3 },
            0x49444154 => PngChunk::Idat(data),
            0x74455874 => {
                let (s, kw) = take_until(&[0u8][..])(data)?;
                let (txt, _) = be_u8(s)?;
                PngChunk::Text { kw, txt }
            }
            0x49454E44 => PngChunk::Iend,
            _ => PngChunk::Unknown { type_code, _data: data },
        };
        Ok((rest, chunk))
    }

    #[inline]
    pub fn parse_png_doc_checksum(input: &[u8]) -> IResult<&[u8], u64> {
        let (mut rest, _) = tag(&[0x89u8, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A][..])(input)?;
        let mut sum = 0u64;
        loop {
            let (next, chunk) = parse_png_chunk(rest)?;
            rest = next;
            match chunk {
                PngChunk::Ihdr { width, height } => sum = sum.wrapping_add(width as u64 + height as u64),
                PngChunk::Plte { entry_count } => sum = sum.wrapping_add(entry_count as u64),
                PngChunk::Text { kw, txt } => sum = sum.wrapping_add((kw.len() + txt.len()) as u64),
                PngChunk::Idat(d) => sum = sum.wrapping_add(d[0] as u64 + d.len() as u64),
                PngChunk::Unknown { type_code, .. } => sum = sum.wrapping_add(type_code as u64),
                PngChunk::Iend => {
                    sum = sum.wrapping_add(1);
                    break;
                }
            }
        }
        Ok((rest, sum))
    }
}

// ============================================================================
// Benchmark Harness (7 runs, median ns/op, exact checksum)
// ============================================================================

fn run_bench<F: FnMut() -> u64>(name: &str, engine: &str, ops_per_run: u64, bytes_per_op: usize, mut f: F) {
    // Warmup
    let expected = black_box(f());
    let _ = black_box(f());

    let mut samples_ns = [0.0f64; 7];
    let mut last_chk = 0u64;
    for s in &mut samples_ns {
        let start = Instant::now();
        last_chk = black_box(f());
        let elapsed = start.elapsed();
        assert_eq!(last_chk, expected);
        *s = elapsed.as_nanos() as f64 / ops_per_run as f64;
    }
    samples_ns.sort_by(|a, b| a.total_cmp(b));
    let median_ns = samples_ns[samples_ns.len() / 2];
    let mops = 1_000.0 / median_ns;
    let mib_s = (bytes_per_op as f64 * 1e9 / median_ns) / (1024.0 * 1024.0);
    println!(
        "{:<16} | {:<12} | {:>8.2} ns/op | {:>7.2} M ops/s | {:>8.1} MiB/s | chk={}",
        name, engine, median_ns, mops, mib_s, last_chk
    );
}

fn main() {
    let ws_ring = gen_ws_ring();
    let delim_ring = gen_delimited_ring();
    let varint_ring = gen_varint_ring();
    let tlv_ring = gen_tlv_ring();
    let png_ring = gen_png_ring();

    let varint_avg_bytes = varint_ring.iter().map(|v| v.len()).sum::<usize>() / RING_SIZE;
    let tlv_bytes = tlv_ring[0].len();
    let png_bytes = png_ring[0].len();

    println!("=== Rust Benchmark (rustc 1.100.0, --release, lto=fat, winnow 1.0.5 & nom 8.0.0) ===");

    // 1. WebSocket Frame
    const WS_OPS: u64 = 1_000_000;
    run_bench("ws_frame", "rust/winnow", WS_OPS, 19, || {
        let mut acc = 0u64;
        let mut scratch = [0u8; 32];
        for iter in 0..WS_OPS as usize {
            let mut slice = black_box(ws_ring[iter & 255].as_slice());
            let f = bench_winnow::parse_ws(&mut slice).unwrap();
            let (w, s) = match f {
                WsFrame::Text(_, m, p) => (1u64, unmask_sum(p, m, &mut scratch)),
                WsFrame::Binary(_, m, p) => (2u64, unmask_sum(p, m, &mut scratch)),
                WsFrame::Ping(m, p) => (3u64, unmask_sum(p, m, &mut scratch)),
                WsFrame::Pong(m, p) => (4u64, unmask_sum(p, m, &mut scratch)),
                WsFrame::Close(m, _, p) => (5u64, unmask_sum(p, m, &mut scratch)),
                WsFrame::Continuation(_, m, p) => (6u64, unmask_sum(p, m, &mut scratch)),
            };
            acc = acc.wrapping_add(w.wrapping_add(s));
        }
        acc
    });

    run_bench("ws_frame", "rust/nom", WS_OPS, 19, || {
        let mut acc = 0u64;
        let mut scratch = [0u8; 32];
        for iter in 0..WS_OPS as usize {
            let slice = black_box(ws_ring[iter & 255].as_slice());
            let (_, f) = bench_nom::parse_ws(slice).unwrap();
            let (w, s) = match f {
                WsFrame::Text(_, m, p) => (1u64, unmask_sum(p, m, &mut scratch)),
                WsFrame::Binary(_, m, p) => (2u64, unmask_sum(p, m, &mut scratch)),
                WsFrame::Ping(m, p) => (3u64, unmask_sum(p, m, &mut scratch)),
                WsFrame::Pong(m, p) => (4u64, unmask_sum(p, m, &mut scratch)),
                WsFrame::Close(m, _, p) => (5u64, unmask_sum(p, m, &mut scratch)),
                WsFrame::Continuation(_, m, p) => (6u64, unmask_sum(p, m, &mut scratch)),
            };
            acc = acc.wrapping_add(w.wrapping_add(s));
        }
        acc
    });

    // 2. Delimited u16
    const DELIM_OPS: u64 = 5_000_000;
    run_bench("delimited_u16", "rust/winnow", DELIM_OPS, 4, || {
        let mut acc = 0u64;
        for iter in 0..DELIM_OPS as usize {
            let mut slice = black_box(delim_ring[iter & 255].as_slice());
            acc = acc.wrapping_add(bench_winnow::parse_delimited_u16(&mut slice).unwrap() as u64);
        }
        acc
    });

    run_bench("delimited_u16", "rust/nom", DELIM_OPS, 4, || {
        let mut acc = 0u64;
        for iter in 0..DELIM_OPS as usize {
            let slice = black_box(delim_ring[iter & 255].as_slice());
            let (_, v) = bench_nom::parse_delimited_u16(slice).unwrap();
            acc = acc.wrapping_add(v as u64);
        }
        acc
    });

    // 3. VarInt stream (32 varints per op)
    const VARINT_OPS: u64 = 200_000;
    run_bench("varint_stream32", "rust/winnow", VARINT_OPS, varint_avg_bytes, || {
        let mut acc = 0u64;
        for iter in 0..VARINT_OPS as usize {
            let mut slice = black_box(varint_ring[iter & 255].as_slice());
            while !slice.is_empty() {
                acc = acc.wrapping_add(bench_winnow::parse_varint_u64(&mut slice).unwrap());
            }
        }
        acc
    });

    run_bench("varint_stream32", "rust/nom", VARINT_OPS, varint_avg_bytes, || {
        let mut acc = 0u64;
        for iter in 0..VARINT_OPS as usize {
            let mut slice = black_box(varint_ring[iter & 255].as_slice());
            while !slice.is_empty() {
                let (next, v) = bench_nom::parse_varint_u64(slice).unwrap();
                slice = next;
                acc = acc.wrapping_add(v);
            }
        }
        acc
    });

    // 4. TLV stream (16 mixed-tag TLV items per op with Alt branching)
    const TLV_OPS: u64 = 200_000;
    run_bench("tlv_stream16", "rust/winnow", TLV_OPS, tlv_bytes, || {
        let mut acc = 0u64;
        for iter in 0..TLV_OPS as usize {
            let mut slice = black_box(tlv_ring[iter & 255].as_slice());
            while !slice.is_empty() {
                match bench_winnow::parse_tlv_item(&mut slice).unwrap() {
                    TlvItem::U32(v) => acc = acc.wrapping_add(v as u64),
                    TlvItem::F64(f) => acc = acc.wrapping_add(f.to_bits()),
                    TlvItem::Bytes(b) => {
                        for &x in b {
                            acc = acc.wrapping_add(x as u64);
                        }
                    }
                }
            }
        }
        acc
    });

    run_bench("tlv_stream16", "rust/nom", TLV_OPS, tlv_bytes, || {
        let mut acc = 0u64;
        for iter in 0..TLV_OPS as usize {
            let mut slice = black_box(tlv_ring[iter & 255].as_slice());
            while !slice.is_empty() {
                let (next, item) = bench_nom::parse_tlv_item(slice).unwrap();
                slice = next;
                match item {
                    TlvItem::U32(v) => acc = acc.wrapping_add(v as u64),
                    TlvItem::F64(f) => acc = acc.wrapping_add(f.to_bits()),
                    TlvItem::Bytes(b) => {
                        for &x in b {
                            acc = acc.wrapping_add(x as u64);
                        }
                    }
                }
            }
        }
        acc
    });

    // 5. PNG full document + CRC-32
    const PNG_OPS: u64 = 200_000;
    run_bench("png_chunks_crc", "rust/winnow", PNG_OPS, png_bytes, || {
        let mut acc = 0u64;
        for iter in 0..PNG_OPS as usize {
            let mut slice = black_box(png_ring[iter & 255].as_slice());
            acc = acc.wrapping_add(bench_winnow::parse_png_doc_checksum(&mut slice).unwrap());
        }
        acc
    });

    run_bench("png_chunks_crc", "rust/nom", PNG_OPS, png_bytes, || {
        let mut acc = 0u64;
        for iter in 0..PNG_OPS as usize {
            let slice = black_box(png_ring[iter & 255].as_slice());
            let (_, s) = bench_nom::parse_png_doc_checksum(slice).unwrap();
            acc = acc.wrapping_add(s);
        }
        acc
    });
}
