// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
use std::io::{self, Read};

const MAX_PIXELS: usize = 16_777_216;
// Resource budget only: never search a truncated candidate subset.
const MAX_GRID_PAIR_CHECKS: usize = 128;

#[derive(Debug, PartialEq)]
struct Proposal {
    left: u32,
    bottom: u32,
    right: u32,
    top: u32,
    confidence: f64,
    ticks: Option<(u32, u32)>,
    x_ticks: Vec<u32>,
    y_ticks: Vec<u32>,
}

enum TickScan {
    Absent,
    Rejected,
    Positions(Vec<u32>),
}

impl TickScan {
    fn positions(self) -> Vec<u32> {
        match self {
            Self::Positions(positions) => positions,
            Self::Absent | Self::Rejected => Vec::new(),
        }
    }
}

fn x_tick_positions(
    pixels: &[u8],
    width: usize,
    height: usize,
    channels: usize,
    left: usize,
    y_axis_right: usize,
    right: usize,
    bottom: usize,
    rows: &[usize],
    legacy: bool,
    inward: bool,
    limit: usize,
) -> TickScan {
    let mut edge = bottom;
    if inward {
        while edge > 0 && rows[edge - 1] * 5 >= rows[bottom] * 4 { edge -= 1; }
    } else {
        while edge + 1 < height && rows[edge + 1] * 5 >= rows[bottom] * 4 { edge += 1; }
    }
    if (!inward && edge + 2 >= height) || (inward && edge < 2) {
        return TickScan::Absent;
    }
    let dark = |x: usize, dy: usize| {
        let y = if inward { edge.checked_sub(dy) } else { edge.checked_add(dy) };
        y.filter(|&y| y < height)
            .is_some_and(|y| is_dark(pixels, (y * width + x) * channels, channels))
    };
    let mut positions = Vec::new();
    let mut x = if legacy { left.saturating_add(3) } else { left };
    while x <= right {
        // On the inward side the Y axis itself is not evidence of an X tick.
        if inward && x <= y_axis_right { x = y_axis_right + 1; continue; }
        let length = (1..=limit + 1)
            .take_while(|&dy| dark(x, dy))
            .count();
        if length > limit {
            // The identified Y-axis can continue below the horizontal axis.
            // Its long downward run is not a tick and must not erase other measured ticks.
            if !inward && (legacy || x <= y_axis_right) {
                x += 1;
                continue;
            }
            return TickScan::Rejected;
        }
        if length < 2 {
            x += 1;
            continue;
        }
        let start = x;
        x += 1;
        while x <= right
            && (1..=2).all(|dy| dark(x, dy))
        {
            // A long adjacent grid stroke must not hide behind a short group
            // member. The historical endpoint-only scan deliberately retains
            // its previous grouping behavior; full tick evidence stays strict.
            if !legacy && (1..=limit + 1).take_while(|&dy| dark(x, dy)).count() > limit {
                return TickScan::Rejected;
            }
            x += 1;
        }
        if x - start > 6 {
            return TickScan::Rejected;
        }
        positions.push((start + x - 1) / 2);
    }
    validate_positions(positions)
}

fn validate_positions(positions: Vec<usize>) -> TickScan {
    if positions.is_empty() { return TickScan::Absent; }
    if positions.len() < 3 || positions.len() > 128 {
        return TickScan::Rejected;
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
        return TickScan::Rejected;
    }
    TickScan::Positions(positions.into_iter().map(|p| p as u32).collect())
}

fn y_tick_positions(
    pixels: &[u8], width: usize, channels: usize,
    left: usize, top: usize, bottom: usize, x_axis_top: usize, cols: &[usize], inward: bool, limit: usize,
) -> TickScan {
    let mut edge = left;
    if inward {
        while edge + 1 < width && cols[edge + 1] * 5 >= cols[left] * 4 { edge += 1; }
    } else {
        while edge > 0 && cols[edge - 1] * 5 >= cols[left] * 4 { edge -= 1; }
    }
    if (!inward && edge < 2) || (inward && edge + 2 >= width) { return TickScan::Absent; }
    let dark = |y: usize, dx: usize| {
        let x = if inward { edge.checked_add(dx) } else { edge.checked_sub(dx) };
        x.filter(|&x| x < width)
            .is_some_and(|x| is_dark(pixels, (y * width + x) * channels, channels))
    };
    let mut positions = Vec::new();
    let mut y = top;
    while y <= bottom {
        // Only the fully observed horizontal axis band is excluded. It cannot
        // establish a Y tick at the intersection without a separate stroke.
        if inward && y >= x_axis_top { break; }
        let length = (1..=limit + 1).take_while(|&dx| dark(y, dx)).count();
        if length > limit {
            // The observed horizontal axis may continue left of the Y axis.
            // Its long run is not a Y tick and must not erase separate ticks.
            if !inward && y >= x_axis_top { y += 1; continue; }
            return TickScan::Rejected;
        }
        if length < 2 { y += 1; continue; }
        let start = y;
        y += 1;
        while y <= bottom && (!inward || y < x_axis_top)
            && (1..=2).all(|dx| dark(y, dx))
        {
            if (1..=limit + 1).take_while(|&dx| dark(y, dx)).count() > limit {
                // Finish the real short group before the known axis band;
                // the outer scan will skip that band without inventing a tick.
                if !inward && y >= x_axis_top { break; }
                return TickScan::Rejected;
            }
            y += 1;
        }
        if y - start > 6 { return TickScan::Rejected; }
        positions.push((start + y - 1) / 2);
    }
    if positions.is_empty() { return TickScan::Absent; }
    if positions.len() < 3 || positions.len() > 128 ||
        positions.windows(2).any(|pair| pair[1] - pair[0] < 4) {
        return TickScan::Rejected;
    }
    TickScan::Positions(positions.into_iter().map(|position| position as u32).collect())
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
    let mut tick_width = width;
    let mut tick_height = height;
    let mut fallback = false;
    let selection = match select(&rows, width * 2 / 5)
        .and_then(|bottom| select(&cols, height * 2 / 5).map(|left| (bottom, left)))
    {
        Ok(axes) => Ok(axes),
        Err("NO_AXIS") => {
            // Only a missing primary axis can use content-relative geometry.
            // Reuse projections without copying, cropping or resampling pixels.
            let first_row = rows.iter().position(|&n| n > 0).ok_or("NO_AXIS")?;
            let last_row = rows.iter().rposition(|&n| n > 0).ok_or("NO_AXIS")?;
            let first_col = cols.iter().position(|&n| n > 0).ok_or("NO_AXIS")?;
            let last_col = cols.iter().rposition(|&n| n > 0).ok_or("NO_AXIS")?;
            tick_width = last_col - first_col + 1;
            tick_height = last_row - first_row + 1;
            fallback = true;
            select(&rows, tick_width * 2 / 5)
                .and_then(|bottom| select(&cols, tick_height * 2 / 5).map(|left| (bottom, left)))
        }
        Err(reason) => Err(reason),
    };
    // Primary success retains canvas-relative limits. Content fallback uses its
    // own extents; neither path claims universal padding invariance.
    let x_limit = (tick_height / 20).clamp(3, 32);
    let y_limit = (tick_width / 20).clamp(3, 32);
    match selection {
        Ok((bottom, left)) => measure_axes(width, height, channels, pixels, &rows, &cols,
            bottom, left, x_limit, y_limit, fallback, false),
        Err("AMBIGUOUS_AXIS") => resolve_grid(width, height, channels, pixels, &rows, &cols,
            tick_width * 2 / 5, tick_height * 2 / 5, x_limit, y_limit),
        Err(reason) => Err(reason),
    }
}

fn strong_line_bands(scores: &[usize], minimum: usize) -> Result<Vec<(usize, usize)>, &'static str> {
    let best = *scores.iter().max().ok_or("AMBIGUOUS_AXIS")?;
    let strong = |index: usize| scores[index] >= minimum && scores[index] * 10 >= best * 9;
    let mut bands = Vec::new();
    let mut index = 0;
    while index < scores.len() {
        if !strong(index) { index += 1; continue; }
        let start = index;
        while index + 1 < scores.len() && strong(index + 1) { index += 1; }
        // The existing selector rejects comparable strokes more than 3 pixels
        // from the first; grid recovery must not admit an excessively thick band.
        if index - start > 3 || bands.len() >= MAX_GRID_PAIR_CHECKS { return Err("AMBIGUOUS_AXIS"); }
        bands.push((start, index));
        index += 1;
    }
    Ok(bands)
}

fn resolve_grid(width: usize, height: usize, channels: usize, pixels: &[u8],
    rows: &[usize], cols: &[usize], row_minimum: usize, col_minimum: usize,
    x_limit: usize, y_limit: usize) -> Result<Proposal, &'static str>
{
    let row_bands = strong_line_bands(rows, row_minimum)?;
    let col_bands = strong_line_bands(cols, col_minimum)?;
    if row_bands.len() * col_bands.len() > MAX_GRID_PAIR_CHECKS { return Err("AMBIGUOUS_AXIS"); }
    let mut unique = None;
    for &(bottom, band_bottom) in &row_bands {
        for &(left, _) in &col_bands {
            if let Ok(proposal) = measure_axes(width, height, channels, pixels, rows, cols,
                bottom, left, x_limit, y_limit, true, true)
            {
                if unique.is_some() { return Err("AMBIGUOUS_AXIS"); }
                unique = Some((proposal, band_bottom));
            }
        }
    }
    let (proposal, band_bottom) = unique.ok_or("AMBIGUOUS_AXIS")?;
    // Strong unexplained strokes outside this plot may belong to another panel.
    // Include the selected horizontal band's thickness without shifting pixels.
    if rows.iter().enumerate().any(|(y, &score)| score >= row_minimum &&
        (y < proposal.top as usize || y > band_bottom)) ||
        cols.iter().enumerate().any(|(x, &score)| score >= col_minimum &&
            (x < proposal.left as usize || x > proposal.right as usize))
    {
        return Err("AMBIGUOUS_AXIS");
    }
    Ok(proposal)
}

fn measure_axes(width: usize, height: usize, channels: usize, pixels: &[u8], rows: &[usize], cols: &[usize],
    bottom: usize, left: usize, x_limit: usize, y_limit: usize, require_ticks: bool, outward_only: bool)
    -> Result<Proposal, &'static str>
{
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
    if require_ticks && (!(left..=right).all(|x| is_dark(pixels, (bottom * width + x) * channels, channels))
        || !(top..=bottom).all(|y| is_dark(pixels, (y * width + left) * channels, channels)))
    {
        return Err("NO_AXIS");
    }
    // Only include immediately adjacent columns with a fully observed vertical
    // stroke through the detected axis. Do not skip unrelated interior lines.
    let mut y_axis_right = left;
    while y_axis_right < right
        && (top..=bottom).all(|y| is_dark(pixels, (y * width + y_axis_right + 1) * channels, channels))
    {
        y_axis_right += 1;
    }
    let mut x_axis_top = bottom;
    while x_axis_top > top
        && (left..=right).all(|x| is_dark(pixels, ((x_axis_top - 1) * width + x) * channels, channels))
    {
        x_axis_top -= 1;
    }
    // Prefer outward ticks. Inward strokes are considered only if that side
    // has no evidence, never to rescue rejected or conflicting outward marks.
    let x_ticks = match x_tick_positions(pixels, width, height, channels, left, y_axis_right, right, bottom, &rows, false, false, x_limit) {
        TickScan::Absent if !outward_only => x_tick_positions(pixels, width, height, channels, left, y_axis_right, right, bottom, &rows, false, true, x_limit).positions(),
        scan => scan.positions(),
    };
    let y_ticks = match y_tick_positions(pixels, width, channels, left, top, bottom, x_axis_top, &cols, false, y_limit) {
        TickScan::Absent if !outward_only => y_tick_positions(pixels, width, channels, left, top, bottom, x_axis_top, &cols, true, y_limit).positions(),
        scan => scan.positions(),
    };
    if require_ticks && (x_ticks.len() < 3 || y_ticks.len() < 3) { return Err("NO_AXIS"); }
    let legacy_ticks = if !require_ticks && x_ticks.is_empty() {
        x_tick_positions(pixels, width, height, channels, left, y_axis_right, right, bottom, &rows, true, false, x_limit).positions()
    } else { Vec::new() };
    let endpoints = if x_ticks.is_empty() { &legacy_ticks } else { &x_ticks };
    let ticks = endpoints.first().zip(endpoints.last()).map(|(&a, &b)| (a, b));
    Ok(Proposal {
        left: left as u32,
        bottom: bottom as u32,
        right: right as u32,
        top: top as u32,
        confidence: row_strength.min(col_strength),
        ticks,
        x_ticks,
        y_ticks,
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
            let list = |values: &[u32]| if values.is_empty() { "-".to_string() }
                else { values.iter().map(u32::to_string).collect::<Vec<_>>().join(",") };
            println!("OK {} {} {} {} {:.6} {} {} {} {}",
                p.left, p.bottom, p.right, p.top, p.confidence, first, last,
                list(&p.x_ticks), list(&p.y_ticks));
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
    fn short_inward_ticks_are_measured_without_inventing_axis_intersections() {
        let a = inward_tick_pixels();
        let p = propose(128, 128, 1, &a).unwrap();
        assert_eq!((p.left, p.bottom, p.right, p.top), (20, 100, 115, 10));
        assert_eq!(p.x_ticks, vec![30, 50, 70, 90, 110]);
        assert_eq!(p.y_ticks, vec![20, 40, 60, 80]);
        assert_eq!(p.ticks, Some((30, 110)));
        assert!(!p.x_ticks.contains(&20));
        assert!(!p.y_ticks.contains(&100));
    }
    fn inward_tick_pixels() -> Vec<u8> {
        let mut a = vec![255; 128 * 128];
        for x in 20..=115 { a[100 * 128 + x] = 0; }
        for y in 10..=100 { a[y * 128 + 20] = 0; }
        for x in [30, 50, 70, 90, 110] {
            for y in 96..100 { a[y * 128 + x] = 0; }
        }
        for y in [20, 40, 60, 80] {
            for x in 21..=24 { a[y * 128 + x] = 0; }
        }
        a
    }
    fn padded_pixels(source: &[u8], side: usize, offset_x: usize, offset_y: usize) -> Vec<u8> {
        let mut padded = vec![255; side * side];
        for y in 0..128 {
            let start = (y + offset_y) * side + offset_x;
            padded[start..start + 128].copy_from_slice(&source[y * 128..(y + 1) * 128]);
        }
        padded
    }
    #[test]
    fn white_margin_fallback_retains_measured_original_pixel_geometry() {
        let source = inward_tick_pixels();
        let original = propose(128, 128, 1, &source).unwrap();
        for (side, dx, dy) in [(256, 64, 64), (512, 150, 200)] {
            let padded = padded_pixels(&source, side, dx, dy);
            let p = propose(side, side, 1, &padded).unwrap();
            assert_eq!((p.left, p.bottom, p.right, p.top),
                (original.left + dx as u32, original.bottom + dy as u32,
                 original.right + dx as u32, original.top + dy as u32));
            assert_eq!(p.x_ticks, original.x_ticks.iter().map(|x| x + dx as u32).collect::<Vec<_>>());
            assert_eq!(p.y_ticks, original.y_ticks.iter().map(|y| y + dy as u32).collect::<Vec<_>>());
            assert_eq!(p.ticks, Some((30 + dx as u32, 110 + dx as u32)));
        }
    }
    #[test]
    fn white_margin_fallback_does_not_promote_tiny_l_or_text_strokes() {
        for text in [false, true] {
            let mut source = vec![255; 128 * 128];
            if text {
                for left in [20, 40, 60] {
                    for x in left..left + 9 { source[20 * 128 + x] = 0; }
                    for y in 20..32 { source[y * 128 + left + 4] = 0; }
                }
            } else {
                for x in 60..72 { source[71 * 128 + x] = 0; }
                for y in 60..72 { source[y * 128 + 60] = 0; }
            }
            let padded = padded_pixels(&source, 256, 64, 64);
            assert!(propose(256, 256, 1, &padded).is_err());
        }
    }
    #[test]
    fn fallback_requires_connected_spans_without_changing_primary_success() {
        let mut source = inward_tick_pixels();
        source[100 * 128 + 60] = 255;
        source[55 * 128 + 20] = 255;
        // Primary success intentionally retains its historical behavior.
        assert!(propose(128, 128, 1, &source).is_ok());
        let padded = padded_pixels(&source, 256, 64, 64);
        assert_eq!(propose(256, 256, 1, &padded), Err("NO_AXIS"));
    }
    #[test]
    fn fallback_rejects_competing_panels_without_searching_for_an_alternative() {
        let source = inward_tick_pixels();
        let mut padded = padded_pixels(&source, 512, 100, 100);
        for y in 0..128 {
            let start = (y + 228) * 512 + 100;
            padded[start..start + 128].copy_from_slice(&source[y * 128..(y + 1) * 128]);
        }
        assert_eq!(propose(512, 512, 1, &padded), Err("AMBIGUOUS_AXIS"));
    }
    #[test]
    fn fallback_needs_three_actual_ticks_on_each_axis() {
        for sparse_x in [false, true] {
            let mut source = inward_tick_pixels();
            if sparse_x {
                for x in [70, 90, 110] { for y in 96..100 { source[y * 128 + x] = 255; } }
            } else {
                for y in [60, 80] { for x in 21..=24 { source[y * 128 + x] = 255; } }
            }
            let padded = padded_pixels(&source, 256, 64, 64);
            assert_eq!(propose(256, 256, 1, &padded), Err("NO_AXIS"));
        }
    }
    #[test]
    fn fallback_run_limits_reject_long_and_adjacent_long_strokes_after_padding() {
        for inward in [false, true] {
            for x_axis in [false, true] {
                for adjacent in [false, true] {
                    let mut source = if inward { inward_tick_pixels() } else { left_extended_axis_pixels(1) };
                    for d in 1..=10 {
                        if x_axis {
                            let x = if adjacent { 71 } else { 70 };
                            let y = if inward { 100 - d } else { 100 + d };
                            source[y * 128 + x] = 0;
                        } else {
                            let y = if inward { 40 } else { 50 } + usize::from(adjacent);
                            let x = if inward { 20 + d } else { 20 - d };
                            source[y * 128 + x] = 0;
                        }
                    }
                    // Ten pixels would fit a 256/512 canvas-derived run cap,
                    // but must still exceed the unchanged content-relative cap.
                    for side in [256, 512] {
                        let padded = padded_pixels(&source, side, 64, 64);
                        assert_eq!(propose(side, side, 1, &padded), Err("NO_AXIS"),
                            "inward={inward}, x_axis={x_axis}, adjacent={adjacent}, side={side}");
                    }
                }
            }
        }
    }
    #[test]
    fn primary_no_intersection_does_not_enter_fallback() {
        let mut source = inward_tick_pixels();
        source[100 * 128 + 20] = 255;
        assert_eq!(propose(128, 128, 1, &source), Err("NO_INTERSECTION"));
    }
    #[test]
    fn axes_without_tick_strokes_remain_empty_on_both_sides() {
        let mut a = vec![255; 128 * 128];
        for x in 20..=115 { a[100 * 128 + x] = 0; }
        for y in 10..=100 { a[y * 128 + 20] = 0; }
        let p = propose(128, 128, 1, &a).unwrap();
        assert!(p.x_ticks.is_empty());
        assert!(p.y_ticks.is_empty());
        assert_eq!(p.ticks, None);
    }
    #[test]
    fn inward_x_ticks_work_with_ordinary_outward_y_ticks() {
        let mut a = inward_tick_pixels();
        // Remove inward Y ticks, retaining the actual vertical axis.
        for y in [20, 40, 60, 80] {
            for x in 21..=24 { a[y * 128 + x] = 255; }
        }
        for y in [20, 50, 80] {
            for x in 16..20 { a[y * 128 + x] = 0; }
        }
        let p = propose(128, 128, 1, &a).unwrap();
        assert_eq!(p.x_ticks, vec![30, 50, 70, 90, 110]);
        assert_eq!(p.y_ticks, vec![20, 50, 80]);
    }
    #[test]
    fn long_inward_grid_strokes_reject_instead_of_becoming_ticks() {
        let mut a = inward_tick_pixels();
        for y in 80..100 { a[y * 128 + 70] = 0; }
        for x in 21..=40 { a[40 * 128 + x] = 0; }
        let p = propose(128, 128, 1, &a).unwrap();
        assert!(p.x_ticks.is_empty());
        assert!(p.y_ticks.is_empty());
        assert_eq!(p.ticks, None);
    }
    #[test]
    fn adjacent_long_group_members_reject_on_both_axes_and_directions() {
        for inward in [true, false] {
            let mut a = vec![255; 128 * 128];
            for x in 20..=115 { a[100 * 128 + x] = 0; }
            for y in 10..=100 { a[y * 128 + 20] = 0; }
            for x in [30, 50, 70, 71, 90, 110] {
                for d in 1..=4 {
                    let y = if inward { 100 - d } else { 100 + d };
                    a[y * 128 + x] = 0;
                }
            }
            for y in [20, 40, 41, 60, 80] {
                for d in 1..=4 {
                    let x = if inward { 20 + d } else { 20 - d };
                    a[y * 128 + x] = 0;
                }
            }
            let short = propose(128, 128, 1, &a).unwrap();
            assert_eq!(short.x_ticks, vec![30, 50, 70, 90, 110]);
            assert_eq!(short.y_ticks, vec![20, 40, 60, 80]);
            // Only the second member of each two-pixel group becomes long.
            for d in 1..=10 {
                let y = if inward { 100 - d } else { 100 + d };
                let x = if inward { 20 + d } else { 20 - d };
                a[y * 128 + 71] = 0;
                a[41 * 128 + x] = 0;
            }
            let long = propose(128, 128, 1, &a).unwrap();
            assert!(long.x_ticks.is_empty(), "inward={inward}");
            assert!(long.y_ticks.is_empty(), "inward={inward}");
            // Preserve the old outward endpoint-only fallback, without exposing
            // that legacy grouping as full measured tick evidence.
            assert_eq!(long.ticks, if inward { None } else { Some((30, 110)) });
        }
    }
    #[test]
    fn rejected_outward_strokes_cannot_be_rescued_by_inward_ticks() {
        for kind in 0..4 {
            let mut a = inward_tick_pixels();
            match kind {
                0 => {
                    for x in [30, 50] { for y in 101..=104 { a[y * 128 + x] = 0; } }
                    for y in [20, 40] { for x in 16..20 { a[y * 128 + x] = 0; } }
                }
                1 => {
                    for x in 30..=36 { for y in 101..=104 { a[y * 128 + x] = 0; } }
                    for y in 20..=26 { for x in 16..20 { a[y * 128 + x] = 0; } }
                }
                2 => {
                    for y in 101..=110 { a[y * 128 + 70] = 0; }
                    for x in 10..20 { a[40 * 128 + x] = 0; }
                }
                _ => {
                    for x in [30, 40, 110] { for y in 101..=104 { a[y * 128 + x] = 0; } }
                    for y in [20, 22, 60] { for x in 16..20 { a[y * 128 + x] = 0; } }
                }
            }
            let p = propose(128, 128, 1, &a).unwrap();
            assert!(p.x_ticks.is_empty(), "outward case {kind}");
            assert!(p.y_ticks.is_empty(), "outward case {kind}");
            assert_eq!(p.ticks, None, "outward case {kind}");
        }
    }
    #[test]
    fn valid_outward_ticks_are_not_merged_with_conflicting_inward_ticks() {
        let mut a = inward_tick_pixels();
        for x in [40, 60, 80] { for y in 101..=104 { a[y * 128 + x] = 0; } }
        for y in [30, 50, 70] { for x in 16..20 { a[y * 128 + x] = 0; } }
        let p = propose(128, 128, 1, &a).unwrap();
        assert_eq!(p.x_ticks, vec![40, 60, 80]);
        assert_eq!(p.y_ticks, vec![30, 50, 70]);
        assert_eq!(p.ticks, Some((40, 80)));
    }
    #[test]
    fn inward_ticks_skip_observed_axis_bands_without_inventing_endpoints() {
        for thickness in 1..=3 {
            let mut a = vec![255; 128 * 128];
            let axis_top = 101 - thickness;
            for y in axis_top..=100 { for x in 20..=115 { a[y * 128 + x] = 0; } }
            for x in 20..20 + thickness { for y in 10..=100 { a[y * 128 + x] = 0; } }
            for x in [30, 50, 70, 90, 110] {
                for y in axis_top - 4..axis_top { a[y * 128 + x] = 0; }
            }
            for y in [20, 40, 60, 80] {
                for x in 20 + thickness..24 + thickness { a[y * 128 + x] = 0; }
            }
            let p = propose(128, 128, 1, &a).unwrap();
            assert_eq!(p.x_ticks, vec![30, 50, 70, 90, 110]);
            assert_eq!(p.y_ticks, vec![20, 40, 60, 80]);
            assert!(!p.x_ticks.contains(&p.left));
            assert!(!p.y_ticks.contains(&p.bottom));
        }
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
        assert_eq!(p.x_ticks, vec![20, 40, 60, 80, 96]);
        assert!(p.y_ticks.is_empty());
    }
    #[test]
    fn measured_ticks_include_axis_intersection_and_reject_wide_strokes() {
        let mut a = vec![255; 128 * 128];
        for x in 20..=115 { a[100 * 128 + x] = 0; }
        for y in 10..=100 { a[y * 128 + 20] = 0; }
        for x in [20, 40, 60, 80, 100] {
            for y in 101..=104 { a[y * 128 + x] = 0; }
        }
        for y in [20, 40, 60, 80, 100] {
            for x in 16..20 { a[y * 128 + x] = 0; }
        }
        let p = propose(128, 128, 1, &a).unwrap();
        assert_eq!(p.x_ticks, vec![20, 40, 60, 80, 100]);
        assert_eq!(p.y_ticks, vec![20, 40, 60, 80, 100]);
        for y in 50..58 { for x in 16..20 { a[y * 128 + x] = 0; } }
        assert!(propose(128, 128, 1, &a).unwrap().y_ticks.is_empty());
    }
    #[test]
    fn irregularly_spaced_observed_y_ticks_remain_pixel_proposals() {
        let mut a = vec![255; 128 * 128];
        for x in 20..=115 { a[100 * 128 + x] = 0; }
        for y in 10..=100 { a[y * 128 + 20] = 0; }
        for y in [12, 28, 62] {
            for x in 16..20 { a[y * 128 + x] = 0; }
        }
        let p = propose(128, 128, 1, &a).unwrap();
        assert_eq!(p.y_ticks, vec![12, 28, 62]);
        assert!(!p.y_ticks.contains(&100));
    }
    #[test]
    fn downward_y_axis_preserves_measured_ticks_without_inventing_intersection_tick() {
        let mut a = vec![255; 128 * 128];
        for x in 20..=115 { a[100 * 128 + x] = 0; }
        for y in 10..=110 { a[y * 128 + 20] = 0; }
        for x in [30, 50, 70, 90, 110] {
            for y in 101..=104 { a[y * 128 + x] = 0; }
        }
        let p = propose(128, 128, 1, &a).unwrap();
        assert_eq!(p.x_ticks, vec![30, 50, 70, 90, 110]);
        assert!(!p.x_ticks.contains(&20));
        assert_eq!(p.ticks, Some((30, 110)));
    }
    fn left_extended_axis_pixels(thickness: usize) -> Vec<u8> {
        let mut a = vec![255; 128 * 128];
        for y in 101 - thickness..=100 {
            for x in 8..=115 { a[y * 128 + x] = 0; }
        }
        for y in 10..=100 { a[y * 128 + 20] = 0; }
        for x in [30, 70, 110] {
            for y in 101..=104 { a[y * 128 + x] = 0; }
        }
        for y in [20, 50, 80] {
            for x in 16..20 { a[y * 128 + x] = 0; }
        }
        a
    }
    #[test]
    fn leftward_horizontal_axis_continuation_preserves_measured_y_ticks() {
        for thickness in 1..=3 {
            let a = left_extended_axis_pixels(thickness);
            let p = propose(128, 128, 1, &a).unwrap();
            assert_eq!((p.left, p.bottom, p.right, p.top), (20, (101 - thickness) as u32, 115, 10));
            assert_eq!(p.x_ticks, vec![30, 70, 110]);
            assert_eq!(p.y_ticks, vec![20, 50, 80]);
            assert!(!p.y_ticks.contains(&p.bottom));
            assert_eq!(p.ticks, Some((30, 110)));
        }
    }
    #[test]
    fn horizontal_axis_band_does_not_hide_unrelated_long_y_strokes() {
        for long_row in [50, 51] {
            let mut a = left_extended_axis_pixels(1);
            // Row 50 is a tick's first member; row 51 is an adjacent member.
            // Neither belongs to the observed horizontal axis band.
            for x in 8..20 { a[long_row * 128 + x] = 0; }
            let p = propose(128, 128, 1, &a).unwrap();
            assert!(p.y_ticks.is_empty(), "long row {long_row}");
            assert_eq!(p.x_ticks, vec![30, 70, 110]);
        }
    }
    #[test]
    fn horizontal_axis_continuation_cannot_supply_a_missing_third_y_tick() {
        let mut a = left_extended_axis_pixels(1);
        for x in 16..20 { a[80 * 128 + x] = 255; }
        let p = propose(128, 128, 1, &a).unwrap();
        assert!(p.y_ticks.is_empty());
        assert_eq!(p.x_ticks, vec![30, 70, 110]);
    }
    #[test]
    fn short_y_tick_next_to_long_axis_band_retains_its_actual_position() {
        for thickness in 1..=3 {
            let mut a = left_extended_axis_pixels(thickness);
            let tick_y = 100 - thickness;
            for x in 16..20 { a[tick_y * 128 + x] = 0; }
            let p = propose(128, 128, 1, &a).unwrap();
            assert_eq!(p.y_ticks, vec![20, 50, 80, tick_y as u32]);
            assert!(!p.y_ticks.contains(&p.bottom));
        }
    }
    #[test]
    fn contiguous_y_axis_stroke_columns_do_not_erase_observed_x_ticks() {
        for stroke_width in 1..=3 {
            let mut a = vec![255; 128 * 128];
            for x in 20..=115 { a[100 * 128 + x] = 0; }
            for y in 10..=110 {
                for x in 20..20 + stroke_width { a[y * 128 + x] = 0; }
            }
            for x in [30, 50, 70, 90, 110] {
                for y in 101..=104 { a[y * 128 + x] = 0; }
            }
            let p = propose(128, 128, 1, &a).unwrap();
            assert_eq!(p.left, 20);
            assert_eq!(p.x_ticks, vec![30, 50, 70, 90, 110]);
            assert_eq!(p.ticks, Some((30, 110)));
        }
    }
    #[test]
    fn downward_axis_is_not_an_extra_tick_or_permission_to_ignore_other_long_strokes() {
        let mut a = vec![255; 128 * 128];
        for x in 20..=115 { a[100 * 128 + x] = 0; }
        for y in 10..=110 { a[y * 128 + 20] = 0; }
        for x in [30, 50] {
            for y in 101..=104 { a[y * 128 + x] = 0; }
        }
        let sparse = propose(128, 128, 1, &a).unwrap();
        assert!(sparse.x_ticks.is_empty());
        assert_eq!(sparse.ticks, None);
        for x in [70, 90, 110] {
            for y in 101..=104 { a[y * 128 + x] = 0; }
        }
        for y in 101..=110 { a[y * 128 + 70] = 0; }
        assert!(propose(128, 128, 1, &a).unwrap().x_ticks.is_empty());
        for y in 10..=110 { a[y * 128 + 80] = 0; }
        assert_eq!(propose(128, 128, 1, &a), Err("AMBIGUOUS_AXIS"));
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
    fn grid_pixels() -> Vec<u8> {
        let mut a = vec![255; 128 * 128];
        for y in [20, 40, 60, 80, 100] {
            for x in 20..=115 { a[y * 128 + x] = 0; }
        }
        for x in [20, 40, 60, 80, 100] {
            for y in 10..=100 { a[y * 128 + x] = 0; }
        }
        // Only the real bottom and left axes carry short outward tick strokes.
        for x in [30, 70, 110] {
            for y in 101..=104 { a[y * 128 + x] = 0; }
        }
        for y in [20, 50, 80] {
            for x in 16..20 { a[y * 128 + x] = 0; }
        }
        a
    }
    #[test]
    fn full_grid_recovers_only_uniquely_outward_ticked_axes() {
        let p = propose(128, 128, 1, &grid_pixels()).unwrap();
        assert_eq!((p.left, p.bottom, p.right, p.top), (20, 100, 115, 10));
        assert_eq!(p.x_ticks, vec![30, 70, 110]);
        assert_eq!(p.y_ticks, vec![20, 50, 80]);
        assert_eq!(p.ticks, Some((30, 110)));
    }
    #[test]
    fn grid_recovery_requires_strict_ticks_on_both_axes() {
        for sparse_x in [false, true] {
            let mut a = grid_pixels();
            if sparse_x {
                for y in 101..=104 { a[y * 128 + 110] = 255; }
            } else {
                for x in 16..20 { a[80 * 128 + x] = 255; }
            }
            assert_eq!(propose(128, 128, 1, &a), Err("AMBIGUOUS_AXIS"));
        }
    }
    #[test]
    fn grid_recovery_does_not_substitute_inward_or_legacy_evidence() {
        let mut a = grid_pixels();
        for x in [30, 70, 110] {
            for y in 101..=104 { a[y * 128 + x] = 255; }
            for y in 96..100 { a[y * 128 + x] = 0; }
        }
        assert_eq!(propose(128, 128, 1, &a), Err("AMBIGUOUS_AXIS"));
    }
    #[test]
    fn two_tick_supported_panels_remain_ambiguous() {
        let source = grid_pixels();
        let mut a = padded_pixels(&source, 256, 0, 0);
        for y in 0..128 {
            let start = (y + 128) * 256 + 128;
            a[start..start + 128].copy_from_slice(&source[y * 128..(y + 1) * 128]);
        }
        assert_eq!(propose(256, 256, 1, &a), Err("AMBIGUOUS_AXIS"));
    }
    #[test]
    fn unexplained_strong_band_outside_unique_plot_remains_ambiguous() {
        let mut a = grid_pixels();
        for x in 20..=115 { a[110 * 128 + x] = 0; }
        assert_eq!(propose(128, 128, 1, &a), Err("AMBIGUOUS_AXIS"));
    }
    #[test]
    fn grid_pair_budget_rejects_without_searching_a_truncated_subset() {
        let mut a = vec![255; 256 * 256];
        for y in (10..=230).step_by(20) {
            for x in 10..=230 { a[y * 256 + x] = 0; }
        }
        for x in (10..=230).step_by(20) {
            for y in 10..=230 { a[y * 256 + x] = 0; }
        }
        for x in [30, 110, 210] { for y in 231..=234 { a[y * 256 + x] = 0; } }
        for y in [40, 100, 200] { for x in 6..10 { a[y * 256 + x] = 0; } }
        assert_eq!(propose(256, 256, 1, &a), Err("AMBIGUOUS_AXIS"));
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
    fn excessively_thick_axis_is_ambiguous() {
        let mut a = vec![255; 128 * 128];
        for x in 20..=115 { for y in 96..=100 { a[y * 128 + x] = 0; } }
        for y in 10..=100 { for x in 20..=24 { a[y * 128 + x] = 0; } }
        assert_eq!(propose(128, 128, 1, &a), Err("AMBIGUOUS_AXIS"));
    }
    #[test]
    fn invalid_protocol_is_rejected() {
        assert_eq!(process(&b"bad"[..]), Err("INVALID_PROTOCOL"));
    }
}
