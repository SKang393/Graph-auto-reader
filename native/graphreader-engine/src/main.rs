// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
use std::io::{self, Read};

const MAX_PIXELS: usize = 16_777_216;

#[derive(Debug, PartialEq)]
struct Proposal {
    left: u32,
    bottom: u32,
    right: u32,
    top: u32,
    confidence: f64,
    ticks: Option<(u32, u32)>,
}

fn tick_endpoints(
    pixels: &[u8],
    width: usize,
    height: usize,
    channels: usize,
    left: usize,
    right: usize,
    bottom: usize,
    rows: &[usize],
) -> Option<(u32, u32)> {
    let mut edge = bottom;
    while edge + 1 < height && rows[edge + 1] * 5 >= rows[bottom] * 4 {
        edge += 1;
    }
    let limit = (height / 20).clamp(3, 32);
    if edge + 2 >= height {
        return None;
    }
    let mut positions = Vec::new();
    let mut x = left.saturating_add(3);
    while x <= right {
        let length = (1..=limit + 1)
            .take_while(|&dy| {
                edge + dy < height
                    && is_dark(pixels, ((edge + dy) * width + x) * channels, channels)
            })
            .count();
        if length < 2 || length > limit {
            x += 1;
            continue;
        }
        let start = x;
        x += 1;
        while x <= right
            && (1..=2).all(|dy| is_dark(pixels, ((edge + dy) * width + x) * channels, channels))
        {
            x += 1;
        }
        if x - start > 6 {
            return None;
        }
        positions.push((start + x - 1) / 2);
    }
    if positions.len() < 3 {
        return None;
    }
    let gaps: Vec<usize> = positions.windows(2).map(|p| p[1] - p[0]).collect();
    let mut sorted = gaps.clone();
    sorted.sort_unstable();
    let typical = sorted[sorted.len() / 2];
    if typical < 4
        || gaps
            .iter()
            .any(|&gap| gap * 2 < typical || gap * 2 > typical * 3)
    {
        return None;
    }
    Some((*positions.first()? as u32, *positions.last()? as u32))
}

fn is_dark(pixels: &[u8], i: usize, channels: usize) -> bool {
    if channels == 1 {
        pixels[i] < 96
    } else {
        (pixels[i] as u32 + pixels[i + 1] as u32 + pixels[i + 2] as u32) < 288
    }
}

fn select(scores: &[usize], minimum: usize) -> Result<usize, &'static str> {
    let best = *scores.iter().max().ok_or("NO_AXIS")?;
    if best < minimum {
        return Err("NO_AXIS");
    }
    let first = scores.iter().position(|&s| s == best).unwrap();
    // A second comparably strong line away from the first is ambiguous.
    if scores
        .iter()
        .enumerate()
        .any(|(i, &s)| i.abs_diff(first) > 3 && s * 10 >= best * 9)
    {
        return Err("AMBIGUOUS_AXIS");
    }
    Ok(first)
}

fn propose(
    width: usize,
    height: usize,
    channels: usize,
    pixels: &[u8],
) -> Result<Proposal, &'static str> {
    if !(32..=4096).contains(&width)
        || !(32..=4096).contains(&height)
        || !matches!(channels, 1 | 3)
        || width
            .checked_mul(height)
            .filter(|&n| n <= MAX_PIXELS)
            .and_then(|n| n.checked_mul(channels))
            != Some(pixels.len())
    {
        return Err("INVALID_INPUT");
    }
    let mut rows = vec![0usize; height];
    let mut cols = vec![0usize; width];
    for y in 0..height {
        for x in 0..width {
            let i = (y * width + x) * channels;
            if is_dark(pixels, i, channels) {
                rows[y] += 1;
                cols[x] += 1;
            }
        }
    }
    let bottom = select(&rows, width * 2 / 5)?;
    let left = select(&cols, height * 2 / 5)?;
    // Require the two proposals to actually meet; avoid reporting independent text strokes.
    if !is_dark(pixels, (bottom * width + left) * channels, channels) {
        return Err("NO_INTERSECTION");
    }
    let row_strength = rows[bottom] as f64 / width as f64;
    let col_strength = cols[left] as f64 / height as f64;
    let right = (left..width)
        .rev()
        .find(|&x| is_dark(pixels, (bottom * width + x) * channels, channels))
        .ok_or("NO_AXIS")?;
    let top = (0..=bottom)
        .find(|&y| is_dark(pixels, (y * width + left) * channels, channels))
        .ok_or("NO_AXIS")?;
    if right <= left || top >= bottom {
        return Err("NO_AXIS");
    }
    Ok(Proposal {
        left: left as u32,
        bottom: bottom as u32,
        right: right as u32,
        top: top as u32,
        confidence: row_strength.min(col_strength),
        ticks: tick_endpoints(pixels, width, height, channels, left, right, bottom, &rows),
    })
}

fn process(mut input: impl Read) -> Result<Proposal, &'static str> {
    let mut header = [0u8; 20];
    input
        .read_exact(&mut header)
        .map_err(|_| "INVALID_PROTOCOL")?;
    if &header[..4] != b"GRA1" {
        return Err("INVALID_PROTOCOL");
    }
    let get = |i| u32::from_le_bytes(header[i..i + 4].try_into().unwrap()) as usize;
    let (w, h, c, len) = (get(4), get(8), get(12), get(16));
    if !(32..=4096).contains(&w)
        || !(32..=4096).contains(&h)
        || !matches!(c, 1 | 3)
        || w.checked_mul(h)
            .filter(|&n| n <= MAX_PIXELS)
            .and_then(|n| n.checked_mul(c))
            != Some(len)
    {
        return Err("INVALID_INPUT");
    }
    let mut data = vec![0; len];
    input
        .read_exact(&mut data)
        .map_err(|_| "INVALID_PROTOCOL")?;
    let mut trailing = [0];
    if input.read(&mut trailing).map_err(|_| "INVALID_PROTOCOL")? != 0 {
        return Err("INVALID_PROTOCOL");
    }
    propose(w, h, c, &data)
}

#[cfg(windows)]
fn set_budget() -> Result<usize, &'static str> {
    use std::ffi::c_void;
    #[repr(C)]
    struct CpuRate {
        flags: u32,
        rate: u32,
    }
    #[link(name = "kernel32")]
    extern "system" {
        fn CreateJobObjectW(attributes: *const c_void, name: *const u16) -> *mut c_void;
        fn SetInformationJobObject(
            job: *mut c_void,
            class: i32,
            info: *const c_void,
            len: u32,
        ) -> i32;
        fn AssignProcessToJobObject(job: *mut c_void, process: *mut c_void) -> i32;
        fn GetCurrentProcess() -> *mut c_void;
        fn GetActiveProcessorCount(group: u16) -> u32;
        fn SetPriorityClass(process: *mut c_void, priority: u32) -> i32;
    }
    let available = std::thread::available_parallelism()
        .map_err(|_| "CPU_BUDGET_UNAVAILABLE")?
        .get();
    // Job CPU rates are scaled against all active system processors, not process affinity.
    let logical = unsafe { GetActiveProcessorCount(0xffff) } as usize;
    if logical == 0 || available == 0 {
        return Err("CPU_BUDGET_UNAVAILABLE");
    }
    let rate = (5000usize / logical).min(10000);
    if rate == 0 {
        return Err("CPU_BUDGET_UNAVAILABLE");
    }
    unsafe {
        let job = CreateJobObjectW(std::ptr::null(), std::ptr::null());
        if job.is_null() {
            return Err("CPU_BUDGET_UNAVAILABLE");
        }
        let info = CpuRate {
            flags: 1 | 4,
            rate: rate as u32,
        };
        if SetInformationJobObject(
            job,
            15,
            &info as *const _ as *const c_void,
            std::mem::size_of::<CpuRate>() as u32,
        ) == 0
            || AssignProcessToJobObject(job, GetCurrentProcess()) == 0
            || SetPriorityClass(GetCurrentProcess(), 0x4000) == 0
        {
            return Err("CPU_BUDGET_UNAVAILABLE");
        }
        // The process retains this job handle until it exits.
    }
    Ok(logical)
}
#[cfg(not(windows))]
fn set_budget() -> Result<usize, &'static str> {
    Err("CPU_BUDGET_UNAVAILABLE")
}

fn main() {
    let result = set_budget().and_then(|_| process(io::stdin().lock()));
    match result {
        Ok(p) => {
            let (first, last) = p
                .ticks
                .map(|(a, b)| (a.to_string(), b.to_string()))
                .unwrap_or_else(|| ("-".to_string(), "-".to_string()));
            println!(
                "OK {} {} {} {} {:.6} {} {}",
                p.left, p.bottom, p.right, p.top, p.confidence, first, last
            );
        }
        Err(e) => {
            println!("ERR {e}");
            std::process::exit(1);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn blank_is_rejected() {
        assert_eq!(propose(64, 64, 1, &[255; 4096]), Err("NO_AXIS"));
    }
    #[test]
    fn axes_are_in_original_pixels() {
        let mut a = vec![255; 4096];
        for x in 8..=55 {
            a[52 * 64 + x] = 0;
        }
        for y in 7..=52 {
            a[y * 64 + 8] = 0;
        }
        let p = propose(64, 64, 1, &a).unwrap();
        assert_eq!((p.left, p.bottom), (8, 52));
        assert_eq!(p.ticks, None);
        assert!((0.0..=1.0).contains(&p.confidence));
    }
    #[test]
    fn ticks_use_outward_strokes_not_axis_extensions_or_above_baseline_marks() {
        let mut a = vec![255; 128 * 128];
        for x in 8..=115 {
            a[100 * 128 + x] = 0;
        }
        for y in 10..=100 {
            a[y * 128 + 8] = 0;
        }
        for x in [20, 40, 60, 80, 96] {
            for y in 101..=104 {
                a[y * 128 + x] = 0;
            }
        }
        for y in 70..100 {
            a[y * 128 + 50] = 0;
        }
        let p = propose(128, 128, 1, &a).unwrap();
        assert_eq!((p.left, p.right, p.ticks), (8, 115, Some((20, 96))));
    }
    #[test]
    fn sparse_and_irregular_strokes_do_not_become_tick_endpoints() {
        let mut a = vec![255; 128 * 128];
        for x in 8..=115 {
            a[100 * 128 + x] = 0;
        }
        for y in 10..=100 {
            a[y * 128 + 8] = 0;
        }
        for x in [20, 40] {
            for y in 101..=104 {
                a[y * 128 + x] = 0;
            }
        }
        assert_eq!(propose(128, 128, 1, &a).unwrap().ticks, None);
        for x in [43, 93] {
            for y in 101..=104 {
                a[y * 128 + x] = 0;
            }
        }
        assert_eq!(propose(128, 128, 1, &a).unwrap().ticks, None);
    }
    #[test]
    fn ambiguous_is_rejected() {
        let mut a = vec![255; 4096];
        for x in 0..64 {
            a[50 * 64 + x] = 0;
            a[56 * 64 + x] = 0;
        }
        assert_eq!(propose(64, 64, 1, &a), Err("AMBIGUOUS_AXIS"));
    }
    #[test]
    fn invalid_protocol_is_rejected() {
        assert_eq!(process(&b"bad"[..]), Err("INVALID_PROTOCOL"));
    }
}
