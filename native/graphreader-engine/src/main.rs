// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
use std::io::{self, Read};
mod affine_support;

const MAX_PIXELS: usize = 16_777_216;
// Resource budget only: never search a truncated candidate subset.
const MAX_GRID_PAIR_CHECKS: usize = 128;
const MAX_REVIEW_RESPONSE_BYTES: usize = 64 * 1024;
// Internal measured-span outcome, normalized before any public proposal result.
const DISCONNECTED_AXIS_SPAN: &str = "DISCONNECTED_AXIS_SPAN";

#[derive(Clone, Copy)]
struct Trace {
    route: &'static str, reason: &'static str,
    row_bands: usize, col_bands: usize, pairs_checked: usize,
    span_rejections: usize, connection_rejections: usize,
    x_tick_rejections: usize, y_tick_rejections: usize, qualified_pairs: usize,
}
impl Default for Trace {
    fn default() -> Self { Self { route: "NONE", reason: "NO_AXIS", row_bands: 0, col_bands: 0,
        pairs_checked: 0, span_rejections: 0, connection_rejections: 0,
        x_tick_rejections: 0, y_tick_rejections: 0, qualified_pairs: 0 } }
}
impl Trace {
    fn line(&self) -> String { format!("TRACE1 {} {} {} {} {} {} {} {} {} {}", self.route, self.reason,
        self.row_bands, self.col_bands, self.pairs_checked, self.span_rejections,
        self.connection_rejections, self.x_tick_rejections, self.y_tick_rejections, self.qualified_pairs) }
}
thread_local! {
    static TRACE: std::cell::Cell<Option<Trace>> = const { std::cell::Cell::new(None) };
    // One request-wide budget shared across all candidate pairs; fail closed.
    static BRIDGE_WORK: std::cell::Cell<usize> = const { std::cell::Cell::new(0) };
    static BRIDGE_EXHAUSTED: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
}
fn trace(f: impl FnOnce(&mut Trace)) { TRACE.with(|cell| { if let Some(mut value) = cell.get() { f(&mut value); cell.set(Some(value)); } }); }

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
    ignored: &[(usize, usize)],
    scan_bounds: Option<(usize, usize)>,
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
        y.filter(|&y| y < height && scan_bounds.is_none_or(|(lo, hi)| (lo..=hi).contains(&y)))
            .is_some_and(|y| is_dark(pixels, (y * width + x) * channels, channels))
    };
    let mut positions = Vec::new();
    let mut x = if legacy { left.saturating_add(3) } else { left };
    while x <= right {
        if ignored.iter().any(|&(lo, hi)| (lo..=hi).contains(&x)) { x += 1; continue; }
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
            && !ignored.iter().any(|&(lo, hi)| (lo..=hi).contains(&x))
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
    validate_positions(positions, legacy)
}

fn validate_positions(positions: Vec<usize>, legacy: bool) -> TickScan {
    if positions.is_empty() { return TickScan::Absent; }
    if positions.len() < 3 || positions.len() > 128 {
        return TickScan::Rejected;
    }
    let gaps: Vec<usize> = positions.windows(2).map(|p| p[1] - p[0]).collect();
    if legacy {
        let mut sorted = gaps.clone();
        sorted.sort_unstable();
        let typical = sorted[sorted.len() / 2];
        if typical < 4 || gaps.iter().any(|&gap| gap * 2 < typical || gap * 2 > typical * 3) {
            return TickScan::Rejected;
        }
    } else if gaps.iter().any(|&gap| gap < 4) {
        return TickScan::Rejected;
    }
    TickScan::Positions(positions.into_iter().map(|p| p as u32).collect())
}

fn y_tick_positions(
    pixels: &[u8], width: usize, channels: usize,
    left: usize, top: usize, bottom: usize, x_axis_top: usize, cols: &[usize], inward: bool, limit: usize,
    ignored: &[(usize, usize)], scan_bounds: Option<(usize, usize)>,
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
        x.filter(|&x| x < width && scan_bounds.is_none_or(|(lo, hi)| (lo..=hi).contains(&x)))
            .is_some_and(|x| is_dark(pixels, (y * width + x) * channels, channels))
    };
    let mut positions = Vec::new();
    let mut y = top;
    while y <= bottom {
        if ignored.iter().any(|&(lo, hi)| (lo..=hi).contains(&y)) { y += 1; continue; }
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
            && !ignored.iter().any(|&(lo, hi)| (lo..=hi).contains(&y))
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
    // Black ink covering at least half of a white pixel can decode to 127.
    // Use a fixed majority-coverage boundary for both supported input formats.
    if channels == 1 {
        pixels[i] < 128
    } else {
        (pixels[i] as u32 + pixels[i + 1] as u32 + pixels[i + 2] as u32) < 384
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

fn select_pair(rows: &[usize], cols: &[usize], row_minimum: usize, col_minimum: usize)
    -> Result<(usize, usize), &'static str>
{
    // A missing perpendicular projection must reach existing extent/run recovery,
    // even if the first projection is ambiguous. Neither ambiguity is resolved here.
    match (select(rows, row_minimum), select(cols, col_minimum)) {
        (Err("NO_AXIS"), _) | (_, Err("NO_AXIS")) => Err("NO_AXIS"),
        (Ok(bottom), Ok(left)) => Ok((bottom, left)),
        (Err(reason), _) | (_, Err(reason)) => Err(reason),
    }
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
    BRIDGE_WORK.with(|work| work.set(0));
    BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
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
    let selection = match select_pair(&rows, &cols, width * 2 / 5, height * 2 / 5)
    {
        Ok(axes) => { trace(|t| t.route = "PRIMARY"); Ok(axes) },
        Err("NO_AXIS") => {
            trace(|t| t.route = "CONTENT_EXTENT");
            // Only a missing primary axis can use content-relative geometry.
            // Reuse projections without copying, cropping or resampling pixels.
            let first_row = rows.iter().position(|&n| n > 0).ok_or("NO_AXIS")?;
            let last_row = rows.iter().rposition(|&n| n > 0).ok_or("NO_AXIS")?;
            let first_col = cols.iter().position(|&n| n > 0).ok_or("NO_AXIS")?;
            let last_col = cols.iter().rposition(|&n| n > 0).ok_or("NO_AXIS")?;
            tick_width = last_col - first_col + 1;
            tick_height = last_row - first_row + 1;
            fallback = true;
            select_pair(&rows, &cols, tick_width * 2 / 5, tick_height * 2 / 5)
        }
        Err(reason) => Err(reason),
    };
    // Primary success retains canvas-relative limits. Content fallback uses its
    // own extents; neither path claims universal padding invariance.
    let x_limit = (tick_height / 20).clamp(3, 32);
    let y_limit = (tick_width / 20).clamp(3, 32);
    match selection {
        Ok((bottom, left)) => {
            let measured = measure_axes(width, height, channels, pixels, &rows, &cols,
                bottom, left, x_limit, y_limit, fallback, false);
            recover_content_span(width, height, channels, pixels, measured, fallback)
        },
        Err("AMBIGUOUS_AXIS") => { trace(|t| t.route = "GRID"); resolve_grid(width, height, channels, pixels, &rows, &cols,
            tick_width * 2 / 5, tick_height * 2 / 5, x_limit, y_limit) },
        Err("NO_AXIS") => { trace(|t| t.route = "CONNECTED_RUNS"); resolve_connected_runs(width, height, channels, pixels) },
        Err(reason) => Err(reason),
    }
}

fn recover_content_span(width: usize, height: usize, channels: usize, pixels: &[u8],
    measured: Result<Proposal, &'static str>, content_extent: bool) -> Result<Proposal, &'static str>
{
    match measured {
        Err(DISCONNECTED_AXIS_SPAN) if content_extent && !BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()) => {
            trace(|t| t.route = "CONNECTED_RUNS");
            // Reuse the request-wide bridge budget and the existing unique,
            // outward-tick qualifier, including its ambiguity/resource refusal.
            resolve_connected_runs(width, height, channels, pixels)
        },
        Err(DISCONNECTED_AXIS_SPAN) => Err("NO_AXIS"),
        result => result,
    }
}

// Disconnected caption glyphs do not increase an axis run. This runs only
// after missing content projections or a disconnected measured content span.
fn resolve_connected_runs(width: usize, height: usize, channels: usize, pixels: &[u8])
    -> Result<Proposal, &'static str>
{
    let (rows, cols) = connected_run_scores(width, height, channels, pixels);
    let row_max = *rows.iter().max().ok_or("NO_AXIS")?;
    let col_max = *cols.iter().max().ok_or("NO_AXIS")?;
    if row_max == 0 || col_max == 0 { return Err("NO_AXIS"); }
    resolve_grid(width, height, channels, pixels, &rows, &cols,
        row_max * 2 / 5, col_max * 2 / 5,
        (col_max / 20).clamp(3, 32), (row_max / 20).clamp(3, 32))
}

fn connected_run_scores(width: usize, height: usize, channels: usize, pixels: &[u8])
    -> (Vec<usize>, Vec<usize>)
{
    let mut rows = vec![0usize; height];
    let mut cols = vec![0usize; width];
    for y in 0..height {
        let mut run = 0;
        for x in 0..width {
            if is_dark(pixels, (y * width + x) * channels, channels) { run += 1; rows[y] = rows[y].max(run); }
            else { run = 0; }
        }
    }
    for x in 0..width {
        let mut run = 0;
        for y in 0..height {
            if is_dark(pixels, (y * width + x) * channels, channels) { run += 1; cols[x] = cols[x].max(run); }
            else { run = 0; }
        }
    }
    (rows, cols)
}

fn strong_line_bands(scores: &[usize], minimum: usize, row: bool) -> Result<Vec<(usize, usize)>, &'static str> {
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
        if index - start > 3 { trace(|t| t.reason = if row { "THICK_ROW_BAND" } else { "THICK_COL_BAND" }); return Err("AMBIGUOUS_AXIS"); }
        if bands.len() >= MAX_GRID_PAIR_CHECKS { trace(|t| t.reason = "BAND_LIMIT"); return Err("AMBIGUOUS_AXIS"); }
        bands.push((start, index));
        trace(|t| if row { t.row_bands += 1 } else { t.col_bands += 1 });
        index += 1;
    }
    Ok(bands)
}

fn resolve_grid(width: usize, height: usize, channels: usize, pixels: &[u8],
    rows: &[usize], cols: &[usize], row_minimum: usize, col_minimum: usize,
    x_limit: usize, y_limit: usize) -> Result<Proposal, &'static str>
{
    let row_bands = strong_line_bands(rows, row_minimum, true)?;
    let col_bands = strong_line_bands(cols, col_minimum, false)?;
    if row_bands.len() * col_bands.len() > MAX_GRID_PAIR_CHECKS { trace(|t| t.reason = "PAIR_LIMIT"); return Err("AMBIGUOUS_AXIS"); }
    let mut unique = None;
    for &(bottom, band_bottom) in &row_bands {
        for &(left, _) in &col_bands {
            trace(|t| t.pairs_checked += 1);
            let measured = measure_axes(width, height, channels, pixels, rows, cols,
                bottom, left, x_limit, y_limit, true, true);
            // Never accept a partial candidate search after exhausting bridge work.
            if BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()) {
                trace(|t| t.reason = "NO_AXIS");
                return Err("AMBIGUOUS_AXIS");
            }
            if let Ok(proposal) = measured
            {
                trace(|t| t.qualified_pairs += 1);
                if unique.is_some() { trace(|t| t.reason = "MULTIPLE_PAIRS"); return Err("AMBIGUOUS_AXIS"); }
                unique = Some((proposal, band_bottom));
            }
        }
    }
    let (proposal, band_bottom) = unique.ok_or_else(|| { trace(|t| t.reason = "NO_VALID_PAIR"); "AMBIGUOUS_AXIS" })?;
    // Strong unexplained strokes outside this plot may belong to another panel.
    // Include the selected horizontal band's thickness without shifting pixels.
    if rows.iter().enumerate().any(|(y, &score)| score >= row_minimum &&
        (y < proposal.top as usize || y > band_bottom)) ||
        cols.iter().enumerate().any(|(x, &score)| score >= col_minimum &&
            (x < proposal.left as usize || x > proposal.right as usize))
    {
        trace(|t| t.reason = "OUTSIDE_BANDS");
        return Err("AMBIGUOUS_AXIS");
    }
    Ok(proposal)
}

// A missing axis run is supported only by a bounded, observed 8-connected ink
// path between the exact dark endpoints. Bounds mirror the existing C# ink
// bridge; unlike native Hough endpoints, these endpoints need no tolerance.
fn observed_axis_span(pixels: &[u8], width: usize, height: usize, channels: usize,
    fixed: usize, start: usize, end: usize, horizontal: bool) -> Option<Vec<(usize, usize)>>
{
    observed_axis_span_clipped(pixels, width, height, channels, fixed, start, end, horizontal, None)
}

fn observed_axis_span_clipped(pixels: &[u8], width: usize, height: usize, channels: usize,
    fixed: usize, start: usize, end: usize, horizontal: bool, clip: Option<(usize, usize, usize, usize)>) -> Option<Vec<(usize, usize)>>
{
    observed_axis_span_with_support(pixels, width, height, channels, fixed, start, end, horizontal, clip, None)
}

fn consume_bridge_work() -> bool {
    if BRIDGE_WORK.with(|work| { let used = work.get(); if used >= MAX_PIXELS { false } else { work.set(used + 1); true } }) { true }
    else { BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(true)); false }
}

fn observed_axis_span_with_support(pixels: &[u8], width: usize, height: usize, channels: usize,
    fixed: usize, start: usize, end: usize, horizontal: bool, clip: Option<(usize, usize, usize, usize)>,
    support: Option<&affine_support::AffineSupport<'_>>) -> Option<Vec<(usize, usize)>>
{
    let dark = |along: usize, across: usize| {
        let (x, y) = if horizontal { (along, across) } else { (across, along) };
        is_dark(pixels, (y * width + x) * channels, channels)
    };
    let mut bridges = Vec::new();
    let mut i = start;
    let max_span = 24usize.max(((width.min(height) as f64 * 0.08).min(80.0)).ceil() as usize);
    while i <= end {
        if dark(i, fixed) { i += 1; continue; }
        let gap_start = i;
        while i <= end && !dark(i, fixed) { i += 1; }
        if gap_start == start || i > end || i - gap_start > max_span + 4 { return None; }
        let from = gap_start - 1;
        let to = i;
        let radius = max_span.min(24usize.max((to - from + 1) / 2 + 2));
        let along_max = if horizontal { width - 1 } else { height - 1 };
        let across_max = if horizontal { height - 1 } else { width - 1 };
        let (along_lo, across_lo, along_hi, across_hi) = match (horizontal, clip) {
            (true, Some((left, top, right, bottom))) => (left, top, right, bottom),
            (false, Some((left, top, right, bottom))) => (top, left, bottom, right),
            _ => (0, 0, along_max, across_max),
        };
        let lo = from.saturating_sub(radius).max(along_lo); let hi = to.saturating_add(radius).min(along_hi);
        let near = fixed.saturating_sub(radius).max(across_lo); let far = fixed.saturating_add(radius).min(across_hi);
        let across_len = far - near + 1;
        let count = (hi - lo + 1) * across_len;
        let mut visited = vec![false; count];
        let index = |along: usize, across: usize| (along - lo) * across_len + across - near;
        let first = index(from, fixed); let target = index(to, fixed);
        visited[first] = true;
        let mut queue = std::collections::VecDeque::from([first]);
        while let Some(current) = queue.pop_front() {
            if !consume_bridge_work() { return None; }
            if current == target { break; }
            let along = lo + current / across_len; let across = near + current % across_len;
            for da in -1isize..=1 { for db in -1isize..=1 {
                let Some(a) = along.checked_add_signed(da) else { continue; };
                let Some(b) = across.checked_add_signed(db) else { continue; };
                if a < lo || a > hi || b < near || b > far { continue; }
                let next = index(a, b);
                if !visited[next] && dark(a, b) { visited[next] = true; queue.push_back(next); }
            } }
        }
        if !visited[target] {
            let window = if horizontal { (lo, near, hi, far) } else { (near, lo, far, hi) };
            let a = if horizontal { (from, fixed) } else { (fixed, from) };
            let b = if horizontal { (to, fixed) } else { (fixed, to) };
            if !support.is_some_and(|s| s.connects(a, b, window)) { return None; }
        }
        // Omit only the missing run and its adjacent dark endpoints, never the search window.
        bridges.push((from, to));
    }
    Some(bridges)
}

fn measure_axes(width: usize, height: usize, channels: usize, pixels: &[u8], rows: &[usize], cols: &[usize],
    bottom: usize, left: usize, x_limit: usize, y_limit: usize, require_ticks: bool, outward_only: bool)
    -> Result<Proposal, &'static str>
{
    // Require the two proposals to actually meet; avoid reporting independent text strokes.
    if !is_dark(pixels, (bottom * width + left) * channels, channels) {
        trace(|t| { t.reason = "DISCONNECTED"; if outward_only { t.connection_rejections += 1; } });
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
        trace(|t| { t.reason = "SHORT_SPAN"; if outward_only { t.span_rejections += 1; } });
        return Err("NO_AXIS");
    }
    let (x_bridges, y_bridges) = if require_ticks {
        let x = observed_axis_span(pixels, width, height, channels, bottom, left, right, true);
        let y = observed_axis_span(pixels, width, height, channels, left, top, bottom, false);
        match (x, y) {
            (Some(x), Some(y)) => (x, y),
            _ => {
                trace(|t| { t.reason = "DISCONNECTED"; if outward_only { t.connection_rejections += 1; } });
                return Err(DISCONNECTED_AXIS_SPAN);
            }
        }
    } else { (Vec::new(), Vec::new()) };
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
    let x_ticks = match x_tick_positions(pixels, width, height, channels, left, y_axis_right, right, bottom, &rows, false, false, x_limit, &x_bridges, None) {
        TickScan::Absent if !outward_only => x_tick_positions(pixels, width, height, channels, left, y_axis_right, right, bottom, &rows, false, true, x_limit, &x_bridges, None).positions(),
        scan => scan.positions(),
    };
    let y_ticks = match y_tick_positions(pixels, width, channels, left, top, bottom, x_axis_top, &cols, false, y_limit, &y_bridges, None) {
        TickScan::Absent if !outward_only => y_tick_positions(pixels, width, channels, left, top, bottom, x_axis_top, &cols, true, y_limit, &y_bridges, None).positions(),
        scan => scan.positions(),
    };
    if require_ticks && (x_ticks.len() < 3 || y_ticks.len() < 3) {
        trace(|t| { t.reason = "INSUFFICIENT_TICKS"; if outward_only {
            if x_ticks.len() < 3 { t.x_tick_rejections += 1; }
            if y_ticks.len() < 3 { t.y_tick_rejections += 1; }
        } });
        return Err("NO_AXIS");
    }
    let legacy_ticks = if !require_ticks && x_ticks.is_empty() {
        x_tick_positions(pixels, width, height, channels, left, y_axis_right, right, bottom, &rows, true, false, x_limit, &[], None).positions()
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

// Explicit review enumerates the same strict observed grid hypotheses completely.
// It does not change the normal selector's unique-plot/outside-band decision.
fn review_proposals(width: usize, height: usize, channels: usize, pixels: &[u8])
    -> Result<Vec<Proposal>, &'static str>
{
    if !(32..=4096).contains(&width) || !(32..=4096).contains(&height)
        || !matches!(channels, 1 | 3)
        || width.checked_mul(height).filter(|&n| n <= MAX_PIXELS)
            .and_then(|n| n.checked_mul(channels)) != Some(pixels.len()) {
        return Err("INVALID_INPUT");
    }
    BRIDGE_WORK.with(|work| work.set(0));
    BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
    let mut rows = vec![0usize; height];
    let mut cols = vec![0usize; width];
    for y in 0..height { for x in 0..width {
        if is_dark(pixels, (y * width + x) * channels, channels) { rows[y] += 1; cols[x] += 1; }
    } }
    let mut tick_width = width;
    let mut tick_height = height;
    let mut content_extent = false;
    let mut used_runs = false;
    if matches!(select_pair(&rows, &cols, width * 2 / 5, height * 2 / 5), Err("NO_AXIS")) {
        content_extent = true;
        let first_row = rows.iter().position(|&n| n > 0).ok_or("NO_AXIS")?;
        let last_row = rows.iter().rposition(|&n| n > 0).ok_or("NO_AXIS")?;
        let first_col = cols.iter().position(|&n| n > 0).ok_or("NO_AXIS")?;
        let last_col = cols.iter().rposition(|&n| n > 0).ok_or("NO_AXIS")?;
        tick_width = last_col - first_col + 1;
        tick_height = last_row - first_row + 1;
        if matches!(select_pair(&rows, &cols, tick_width * 2 / 5, tick_height * 2 / 5), Err("NO_AXIS")) {
            used_runs = true;
            (rows, cols) = connected_run_scores(width, height, channels, pixels);
            tick_width = *rows.iter().max().ok_or("NO_AXIS")?;
            tick_height = *cols.iter().max().ok_or("NO_AXIS")?;
            if tick_width == 0 || tick_height == 0 { return Err("NO_AXIS"); }
        }
    }
    let result = review_grid(width, height, channels, pixels, &rows, &cols,
        tick_width * 2 / 5, tick_height * 2 / 5,
        (tick_height / 20).clamp(3, 32), (tick_width / 20).clamp(3, 32));
    // A content projection can be dominated by disconnected caption ink.
    // If it yields no review candidate, enumerate the same strict connected-run
    // hypotheses as automatic recovery, retaining the request-wide bridge budget.
    // Never retry an incomplete/ambiguous search or an already-used run route.
    if matches!(result, Err(DISCONNECTED_AXIS_SPAN)) && content_extent && !used_runs &&
        !BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()) {
        let (run_rows, run_cols) = connected_run_scores(width, height, channels, pixels);
        let row_max = *run_rows.iter().max().ok_or("NO_AXIS")?;
        let col_max = *run_cols.iter().max().ok_or("NO_AXIS")?;
        if row_max == 0 || col_max == 0 { return Err("NO_AXIS"); }
        return review_grid(width, height, channels, pixels, &run_rows, &run_cols,
            row_max * 2 / 5, col_max * 2 / 5,
            (col_max / 20).clamp(3, 32), (row_max / 20).clamp(3, 32))
            .map_err(|reason| if reason == DISCONNECTED_AXIS_SPAN { "NO_AXIS" } else { reason });
    }
    result.map_err(|reason| if reason == DISCONNECTED_AXIS_SPAN { "NO_AXIS" } else { reason })
}

fn review_grid(width: usize, height: usize, channels: usize, pixels: &[u8],
    rows: &[usize], cols: &[usize], row_minimum: usize, col_minimum: usize,
    x_limit: usize, y_limit: usize) -> Result<Vec<Proposal>, &'static str>
{
    let row_bands = strong_line_bands(rows, row_minimum, true)?;
    let col_bands = strong_line_bands(cols, col_minimum, false)?;
    if row_bands.len() * col_bands.len() > MAX_GRID_PAIR_CHECKS { return Err("AMBIGUOUS_AXIS"); }
    let mut proposals = Vec::new();
    let mut disconnected = false;
    for &(bottom, _) in &row_bands { for &(left, _) in &col_bands {
        let measured = measure_axes(width, height, channels, pixels, rows, cols,
            bottom, left, x_limit, y_limit, true, true);
        if BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()) { return Err("AMBIGUOUS_AXIS"); }
        disconnected |= matches!(measured, Err(DISCONNECTED_AXIS_SPAN));
        if let Ok(proposal) = measured { proposals.push(proposal); }
    } }
    if proposals.is_empty() { return Err(if disconnected { DISCONNECTED_AXIS_SPAN } else { "NO_AXIS" }); }
    Ok(proposals)
}

// Review wire is ordered and complete: REVIEW1 N, then N BOX left bottom
// right top xTickCsv yTickCsv lines. Tick lists are strictly observed pixels;
// no legacy endpoints or numeric labels are substituted. Format before writing
// so response overflow emits only ERR, never a partial header/list.
fn review_response(proposals: &[Proposal]) -> Result<String, &'static str> {
    let mut response = format!("REVIEW1 {}\n", proposals.len());
    for proposal in proposals {
        let list = |values: &[u32]| values.iter().map(u32::to_string).collect::<Vec<_>>().join(",");
        response.push_str(&format!("BOX {} {} {} {} {} {}\n", proposal.left, proposal.bottom, proposal.right, proposal.top,
            list(&proposal.x_ticks), list(&proposal.y_ticks)));
        if response.len() > MAX_REVIEW_RESPONSE_BYTES { return Err("AMBIGUOUS_AXIS"); }
    }
    Ok(response)
}

// A caller-supplied box is never shifted or expanded to another candidate.
// Only pixels within its spans establish axes and band thickness; the small
// region outside the box is sampled solely for outward tick strokes.
#[cfg(test)]
fn measure_box_ticks(width: usize, height: usize, channels: usize, pixels: &[u8],
    left: usize, bottom: usize, right: usize, top: usize) -> Result<(Vec<u32>, Vec<u32>), &'static str>
{
    measure_box_ticks_with_diagnostic(width, height, channels, pixels, left, bottom, right, top, None)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct TickMeasureDiagnostic {
    stage: &'static str,
    seed_gray_sum: Option<u32>,
    channels: Option<usize>,
    bridge_work: Option<usize>,
}
impl Default for TickMeasureDiagnostic {
    fn default() -> Self { Self { stage: "UNATTEMPTED", seed_gray_sum: None, channels: None, bridge_work: None } }
}
impl TickMeasureDiagnostic {
    fn line(&self) -> String {
        let seed = self.seed_gray_sum.map_or_else(|| "-".to_string(), |v| v.to_string());
        let channels = self.channels.map_or_else(|| "-".to_string(), |v| v.to_string());
        let work = self.bridge_work.map_or_else(|| "-".to_string(), |v| v.to_string());
        format!("TICKDIAG1 {} seed_gray_sum={seed} channels={channels} bridge_work={work}", self.stage)
    }
}

fn measure_box_ticks_with_diagnostic(width: usize, height: usize, channels: usize, pixels: &[u8],
    left: usize, bottom: usize, right: usize, top: usize,
    diagnostic: Option<&mut TickMeasureDiagnostic>) -> Result<(Vec<u32>, Vec<u32>), &'static str>
{
    measure_box_ticks_mode(width, height, channels, pixels, left, bottom, right, top, diagnostic, false, None)
}

fn measure_box_ticks_mode(width: usize, height: usize, channels: usize, pixels: &[u8],
    left: usize, bottom: usize, right: usize, top: usize,
    mut diagnostic: Option<&mut TickMeasureDiagnostic>, manual_partial: bool,
    support: Option<&affine_support::AffineSupport<'_>>) -> Result<(Vec<u32>, Vec<u32>), &'static str>
{
    if !(32..=4096).contains(&width) || !(32..=4096).contains(&height) || !matches!(channels, 1 | 3)
        || width.checked_mul(height).filter(|&n| n <= MAX_PIXELS).and_then(|n| n.checked_mul(channels)) != Some(pixels.len())
        || left >= right || top >= bottom || right >= width || bottom >= height { return Err("INVALID_INPUT"); }
    let input_channels = channels;
    let seed_gray_sum = diagnostic.as_ref().map(|_| {
        let seed_start = (bottom * width + left) * channels;
        pixels[seed_start..seed_start + channels].iter().map(|&value| u32::from(value)).sum()
    });
    // Match the existing native line provider's strong-ink criterion only for
    // this explicitly supplied box. Keep global proposal/review pixels intact.
    const MAXIMUM_INK_GRAY: u32 = 200;
    let mask: Vec<u8> = pixels.chunks_exact(channels).map(|pixel| {
        let sum: u32 = pixel.iter().map(|&value| u32::from(value)).sum();
        if sum < MAXIMUM_INK_GRAY * channels as u32 { 0 } else { 255 }
    }).collect();
    let pixels = mask.as_slice();
    let channels = 1;
    BRIDGE_WORK.with(|work| work.set(0));
    BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
    let dark = |x: usize, y: usize| is_dark(pixels, (y * width + x) * channels, channels);
    if let Some(trace) = diagnostic.as_deref_mut() {
        trace.seed_gray_sum = seed_gray_sum;
        trace.channels = Some(input_channels);
        trace.stage = "ORIGIN_CHECK";
    }
    if !dark(left, bottom) {
        if let Some(trace) = diagnostic.as_deref_mut() { trace.stage = "ORIGIN_NOT_INK"; }
        return Err("AXIS_BOX_NOT_OBSERVED");
    }
    if let Some(trace) = diagnostic.as_deref_mut() { trace.stage = "ORIGIN_OBSERVED"; }
    let (x_bridges, y_bridges) = measure_supplied_spans(pixels, width, height, channels,
        left, bottom, right, top, &mut diagnostic, manual_partial, support)?;

    let mut rows = vec![0usize; height];
    let mut cols = vec![0usize; width];
    let row_length = right - left + 1; let col_length = bottom - top + 1;
    rows[bottom] = row_length; cols[left] = col_length;
    let mut axis_top = bottom; let mut axis_bottom = bottom;
    while axis_top > top && (left..=right).all(|x| dark(x, axis_top - 1)) { axis_top -= 1; rows[axis_top] = row_length; }
    while axis_bottom + 1 < height && (left..=right).all(|x| dark(x, axis_bottom + 1)) { axis_bottom += 1; rows[axis_bottom] = row_length; }
    let mut axis_left = left; let mut axis_right = left;
    while axis_left > 0 && (top..=bottom).all(|y| dark(axis_left - 1, y)) { axis_left -= 1; cols[axis_left] = col_length; }
    while axis_right < right && (top..=bottom).all(|y| dark(axis_right + 1, y)) { axis_right += 1; cols[axis_right] = col_length; }
    let x_limit = ((bottom - top + 1) / 20).clamp(3, 32);
    let y_limit = ((right - left + 1) / 20).clamp(3, 32);
    let x_bounds = Some((top, axis_bottom.saturating_add(x_limit + 1).min(height - 1)));
    let y_bounds = Some((axis_left.saturating_sub(y_limit + 1), right));
    // A failed span contributes no tick positions, even if isolated strokes look like ticks.
    let x = x_bridges.map_or_else(Vec::new, |bridges| match x_tick_positions(pixels, width, height, channels, left, axis_right, right, bottom, &rows, false, false, x_limit, &bridges, x_bounds) {
        TickScan::Absent => x_tick_positions(pixels, width, height, channels, left, axis_right, right, bottom, &rows, false, true, x_limit, &bridges, x_bounds).positions(),
        scan => scan.positions(),
    });
    let y = y_bridges.map_or_else(Vec::new, |bridges| match y_tick_positions(pixels, width, channels, left, top, bottom, axis_top, &cols, false, y_limit, &bridges, y_bounds) {
        TickScan::Absent => y_tick_positions(pixels, width, channels, left, top, bottom, axis_top, &cols, true, y_limit, &bridges, y_bounds).positions(),
        scan => scan.positions(),
    });
    if let Some(trace) = diagnostic.as_deref_mut() {
        trace.bridge_work = Some(BRIDGE_WORK.with(|work| work.get()));
        trace.stage = "TICKS_MEASURED";
    }
    Ok((x, y))
}


// Shared bridge work is never reset between axes. A budget error cannot become
// partial evidence, including when the other full span already passed.
fn measure_supplied_spans(pixels: &[u8], width: usize, height: usize, channels: usize,
    left: usize, bottom: usize, right: usize, top: usize,
    diagnostic: &mut Option<&mut TickMeasureDiagnostic>, manual_partial: bool,
    support: Option<&affine_support::AffineSupport<'_>>)
    -> Result<(Option<Vec<(usize, usize)>>, Option<Vec<(usize, usize)>>), &'static str>
{
    let clip = Some((left, top, right, bottom));
    let x_bridges = match observed_axis_span_with_support(pixels, width, height, channels, bottom, left, right, true, clip, support) {
        Some(bridges) => Some(bridges),
        None => {
            if let Some(trace) = diagnostic.as_deref_mut() {
                trace.bridge_work = Some(BRIDGE_WORK.with(|work| work.get()));
                trace.stage = if BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()) { "X_BRIDGE_BUDGET" } else { "X_SPAN_UNOBSERVED" };
            }
            if !manual_partial || BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()) {
                return Err("AXIS_BOX_NOT_OBSERVED");
            }
            None
        }
    };
    if let Some(trace) = diagnostic.as_deref_mut() {
        trace.bridge_work = Some(BRIDGE_WORK.with(|work| work.get()));
        trace.stage = "X_SPAN_OBSERVED";
    }
    let y_bridges = match observed_axis_span_with_support(pixels, width, height, channels, left, top, bottom, false, clip, support) {
        Some(bridges) => Some(bridges),
        None => {
            if let Some(trace) = diagnostic.as_deref_mut() {
                trace.bridge_work = Some(BRIDGE_WORK.with(|work| work.get()));
                trace.stage = if BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()) { "Y_BRIDGE_BUDGET" } else { "Y_SPAN_UNOBSERVED" };
            }
            if !manual_partial || BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()) {
                return Err("AXIS_BOX_NOT_OBSERVED");
            }
            None
        }
    };
    if x_bridges.is_none() && y_bridges.is_none() { return Err("AXIS_BOX_NOT_OBSERVED"); }
    if let Some(trace) = diagnostic.as_deref_mut() {
        trace.bridge_work = Some(BRIDGE_WORK.with(|work| work.get()));
        trace.stage = "SPANS_OBSERVED";
    }

    Ok((x_bridges, y_bridges))
}

fn process_measure_ticks(input: impl Read) -> Result<String, &'static str> {
    process_measure_ticks_inner(input, None, false)
}

fn process_manual_partial_ticks(input: impl Read) -> Result<String, &'static str> {
    process_measure_ticks_inner(input, None, true)
}

fn process_measure_ticks_diagnostic(input: impl Read) -> (Result<String, &'static str>, TickMeasureDiagnostic) {
    let mut diagnostic = TickMeasureDiagnostic::default();
    let result = process_measure_ticks_inner(input, Some(&mut diagnostic), false);
    (result, diagnostic)
}

fn process_measure_ticks_inner(mut input: impl Read, diagnostic: Option<&mut TickMeasureDiagnostic>, manual_partial: bool) -> Result<String, &'static str> {
    let mut header = [0u8; 36];
    input.read_exact(&mut header).map_err(|_| "INVALID_PROTOCOL")?;
    if &header[..4] != b"GRT1" { return Err("INVALID_PROTOCOL"); }
    let get = |i| u32::from_le_bytes(header[i..i + 4].try_into().unwrap()) as usize;
    let (w, h, c, len, left, bottom, right, top) = (get(4), get(8), get(12), get(16), get(20), get(24), get(28), get(32));
    if !(32..=4096).contains(&w) || !(32..=4096).contains(&h) || !matches!(c, 1 | 3)
        || w.checked_mul(h).filter(|&n| n <= MAX_PIXELS).and_then(|n| n.checked_mul(c)) != Some(len)
        || left >= right || top >= bottom || right >= w || bottom >= h { return Err("INVALID_INPUT"); }
    let mut pixels = vec![0; len];
    input.read_exact(&mut pixels).map_err(|_| "INVALID_PROTOCOL")?;
    let mut trailing = [0];
    if input.read(&mut trailing).map_err(|_| "INVALID_PROTOCOL")? != 0 { return Err("INVALID_PROTOCOL"); }
    let (x, y) = if manual_partial {
        measure_box_ticks_mode(w, h, c, &pixels, left, bottom, right, top, diagnostic, true, None)?
    } else {
        measure_box_ticks_with_diagnostic(w, h, c, &pixels, left, bottom, right, top, diagnostic)?
    };
    let list = |values: &[u32]| if values.is_empty() { "-".to_string() } else { values.iter().map(u32::to_string).collect::<Vec<_>>().join(",") };
    Ok(format!("TICKS1 {left} {bottom} {right} {top} {} {}\n", list(&x), list(&y)))
}

fn process_affine_ticks(mut input: impl Read, manual_partial: bool) -> Result<String, &'static str> {
    let mut header = [0u8; 96];
    input.read_exact(&mut header).map_err(|_| "INVALID_PROTOCOL")?;
    if &header[..4] != b"GRA1" { return Err("INVALID_PROTOCOL"); }
    let get = |i| u32::from_le_bytes(header[i..i + 4].try_into().unwrap()) as usize;
    let (w,h,c,len,left,bottom,right,top) = (get(4),get(8),get(12),get(16),get(20),get(24),get(28),get(32));
    let (sw,sh,slen) = (get(36),get(40),get(44));
    if !(32..=4096).contains(&w) || !(32..=4096).contains(&h) || c != 1 ||
        w.checked_mul(h).filter(|&n| n <= MAX_PIXELS) != Some(len) ||
        !(32..=4096).contains(&sw) || !(32..=4096).contains(&sh) ||
        sw.checked_mul(sh).filter(|&n| n <= MAX_PIXELS) != Some(slen) ||
        left >= right || top >= bottom || right >= w || bottom >= h { return Err("INVALID_INPUT"); }
    let mut matrix = [0f64; 6];
    for (index, value) in matrix.iter_mut().enumerate() {
        let offset = 48 + index * 8;
        *value = f64::from_le_bytes(header[offset..offset + 8].try_into().unwrap());
    }
    let mut pixels = vec![0; len]; let mut original = vec![0; slen];
    input.read_exact(&mut pixels).map_err(|_| "INVALID_PROTOCOL")?;
    input.read_exact(&mut original).map_err(|_| "INVALID_PROTOCOL")?;
    let mut trailing = [0];
    if input.read(&mut trailing).map_err(|_| "INVALID_PROTOCOL")? != 0 { return Err("INVALID_PROTOCOL"); }
    let support = affine_support::AffineSupport::new(&original,sw,sh,matrix,left,bottom,right,top).ok_or("INVALID_INPUT")?;
    let (x,y) = measure_box_ticks_mode(w,h,c,&pixels,left,bottom,right,top,None,manual_partial,Some(&support))?;
    let list = |values: &[u32]| if values.is_empty() { "-".to_string() } else { values.iter().map(u32::to_string).collect::<Vec<_>>().join(",") };
    Ok(format!("TICKS1 {left} {bottom} {right} {top} {} {}\n",list(&x),list(&y)))
}

fn read_frame(mut input: impl Read) -> Result<(usize, usize, usize, Vec<u8>), &'static str> {
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
    Ok((w, h, c, data))
}

fn process(input: impl Read) -> Result<Proposal, &'static str> {
    let (w, h, c, data) = read_frame(input)?;
    propose(w, h, c, &data)
}

fn process_review(input: impl Read) -> Result<Vec<Proposal>, &'static str> {
    let (w, h, c, data) = read_frame(input)?;
    review_proposals(w, h, c, &data)
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
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    let measure = match measure_mode_requested(&args) {
        Ok(value) => value,
        Err(reason) => { println!("ERR {reason}"); std::process::exit(1); }
    };
    if measure {
        if measure_diagnostics_requested(&args) {
            let (result, diagnostic) = match set_budget() {
                Ok(_) => process_measure_ticks_diagnostic(io::stdin().lock()),
                Err(reason) => (Err(reason), TickMeasureDiagnostic::default()),
            };
            let failed = result.is_err();
            match result {
                Ok(response) => print!("{response}"),
                Err(reason) => println!("ERR {reason}"),
            }
            println!("{}", diagnostic.line());
            if failed { std::process::exit(1); }
        } else {
            match set_budget().and_then(|_| if args[0] == "--measure-affine-ticks" || args[0] == "--measure-affine-ticks-manual-partial" {
                process_affine_ticks(io::stdin().lock(), args[0] == "--measure-affine-ticks-manual-partial")
            } else if args[0] == "--measure-ticks-manual-partial" {
                process_manual_partial_ticks(io::stdin().lock())
            } else { process_measure_ticks(io::stdin().lock()) }) {
                Ok(response) => print!("{response}"),
                Err(reason) => { println!("ERR {reason}"); std::process::exit(1); }
            }
        }
        return;
    }
    let diagnostics = std::env::args_os().skip(1).any(|arg| arg == "--diagnostics");
    if std::env::args_os().skip(1).any(|arg| arg == "--review-candidates") {
        if diagnostics { println!("ERR INVALID_PROTOCOL"); std::process::exit(1); }
        match set_budget().and_then(|_| process_review(io::stdin().lock())).and_then(|proposals| review_response(&proposals)) {
            Ok(response) => print!("{response}"),
            Err(reason) => { println!("ERR {reason}"); std::process::exit(1); }
        }
        return;
    }
    if diagnostics { TRACE.with(|cell| cell.set(Some(Trace::default()))); }
    let result = set_budget().and_then(|_| process(io::stdin().lock()));
    match result {
        Ok(p) => {
            trace(|t| t.reason = "OK");
            let (first, last) = p
                .ticks
                .map(|(a, b)| (a.to_string(), b.to_string()))
                .unwrap_or_else(|| ("-".to_string(), "-".to_string()));
            let list = |values: &[u32]| if values.is_empty() { "-".to_string() }
                else { values.iter().map(u32::to_string).collect::<Vec<_>>().join(",") };
            println!("OK {} {} {} {} {:.6} {} {} {} {}",
                p.left, p.bottom, p.right, p.top, p.confidence, first, last,
                list(&p.x_ticks), list(&p.y_ticks));
            TRACE.with(|cell| if let Some(t) = cell.get() { println!("{}", t.line()); });
        }
        Err(e) => {
            trace(|t| if t.route == "NONE" { t.reason = e; });
            println!("ERR {e}");
            TRACE.with(|cell| if let Some(t) = cell.get() { println!("{}", t.line()); });
            std::process::exit(1);
        }
    }
}

fn measure_mode_requested(args: &[std::ffi::OsString]) -> Result<bool, &'static str> {
    if args.iter().any(|arg| arg == "--measure-affine-ticks" || arg == "--measure-affine-ticks-manual-partial") {
        return if args.len() == 1 { Ok(true) } else { Err("INVALID_PROTOCOL") };
    }
    if args.iter().any(|arg| arg == "--measure-ticks-manual-partial") {
        return if args.len() == 1 { Ok(true) } else { Err("INVALID_PROTOCOL") };
    }
    if args.iter().any(|arg| arg == "--measure-ticks") {
        if args.len() == 1 { return Ok(true); }
        if args.len() == 2 && args[1] == "--measure-ticks-diagnostics" { return Ok(true); }
        return Err("INVALID_PROTOCOL");
    }
    if args.iter().any(|arg| arg == "--measure-ticks-diagnostics") {
        return Err("INVALID_PROTOCOL");
    }
    Ok(false)
}

fn measure_diagnostics_requested(args: &[std::ffi::OsString]) -> bool {
    if args.len() == 2 && args[0] == "--measure-ticks" && args[1] == "--measure-ticks-diagnostics" {
        return true;
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn supplied_box_measures_only_observed_outward_and_inward_ticks() {
        let inward = inward_tick_pixels();
        let result = measure_box_ticks(128, 128, 1, &inward, 20, 100, 115, 10).unwrap();
        assert_eq!(result.0, vec![30, 50, 70, 90, 110]);
        assert_eq!(result.1, vec![20, 40, 60, 80]);
        let outward = grid_pixels();
        let result = measure_box_ticks(128, 128, 1, &outward, 20, 100, 115, 10).unwrap();
        assert_eq!(result.0, vec![30, 70, 110]);
        assert_eq!(result.1, vec![20, 50, 80]);
        let color: Vec<_> = outward.iter().flat_map(|&pixel| [pixel; 3]).collect();
        assert_eq!(measure_box_ticks(128, 128, 3, &color, 20, 100, 115, 10).unwrap(), result);
    }

    #[test]
    fn supplied_observed_box_supports_thick_axis_bands_without_snapping() {
        for thickness in [5, 6, 7, 8] {
            let mut pixels = vec![255; 128 * 128];
            for y in 100..100 + thickness { for x in 20..=115 { pixels[y * 128 + x] = 0; } }
            for x in 20..20 + thickness { for y in 10..=100 { pixels[y * 128 + x] = 0; } }
            for x in [40, 70, 100] { for y in 100 + thickness..104 + thickness { pixels[y * 128 + x] = 0; } }
            for y in [20, 50, 80] { for x in 16..20 { pixels[y * 128 + x] = 0; } }
            let before = pixels.clone();
            assert_eq!(measure_box_ticks(128, 128, 1, &pixels, 20, 100, 115, 10).unwrap(),
                (vec![40, 70, 100], vec![20, 50, 80]), "thickness={thickness}");
            assert_eq!(measure_box_ticks(128, 128, 1, &pixels, 19, 100, 115, 10), Err("AXIS_BOX_NOT_OBSERVED"));
            assert_eq!(pixels, before);
        }
    }

    #[test]
    fn supplied_box_isolates_panels_and_rejects_unobserved_geometry() {
        let image = review_two_panels();
        let first = measure_box_ticks(256, 256, 1, &image, 20, 100, 115, 10).unwrap();
        let second = measure_box_ticks(256, 256, 1, &image, 148, 228, 243, 138).unwrap();
        assert_eq!(first, (vec![30, 70, 110], vec![20, 50, 80]));
        assert_eq!(second, (vec![158, 198, 238], vec![148, 178, 208]));
        assert_eq!(measure_box_ticks(256, 256, 1, &image, 21, 100, 115, 10), Err("AXIS_BOX_NOT_OBSERVED"));
        let mut gray = grid_pixels(); for value in &mut gray { if *value == 0 { *value = 128; } }
        assert_eq!(measure_box_ticks(128, 128, 1, &gray, 20, 100, 115, 10).unwrap(), (vec![30, 70, 110], vec![20, 50, 80]));
        let mut disconnected = inward_tick_pixels(); disconnected[100 * 128 + 20] = 255;
        assert_eq!(measure_box_ticks(128, 128, 1, &disconnected, 20, 100, 115, 10), Err("AXIS_BOX_NOT_OBSERVED"));
    }

    #[test]
    fn supplied_box_strong_gray_ink_preserves_observed_evidence_and_input() {
        for ink in [0, 128, 192] {
            let mut image = grid_pixels();
            for value in &mut image { if *value == 0 { *value = ink; } }
            for y in 58..=62 { for x in 58..=62 { image[y * 128 + x] = 0; } }
            let before = image.clone();
            let expected = (vec![30, 70, 110], vec![20, 50, 80]);
            assert_eq!(measure_box_ticks(128, 128, 1, &image, 20, 100, 115, 10).unwrap(), expected);
            let rgb: Vec<_> = image.iter().flat_map(|&p| [p; 3]).collect();
            assert_eq!(measure_box_ticks(128, 128, 3, &rgb, 20, 100, 115, 10).unwrap(), expected);
            assert_eq!(image, before);
        }
        let mut two = review_two_panels();
        for value in &mut two { if *value == 0 { *value = 192; } }
        assert_eq!(measure_box_ticks(256, 256, 1, &two, 20, 100, 115, 10).unwrap(), (vec![30, 70, 110], vec![20, 50, 80]));
        assert_eq!(measure_box_ticks(256, 256, 1, &two, 148, 228, 243, 138).unwrap(), (vec![158, 198, 238], vec![148, 178, 208]));
        assert_eq!(review_proposals(256, 256, 1, &two), Err("NO_AXIS"));
    }

    #[test]
    fn supplied_box_gray_background_and_nontick_shapes_do_not_invent_ticks() {
        for ink in [128, 192, 255] {
            let image = vec![ink; 128 * 128];
            let result = measure_box_ticks(128, 128, 1, &image, 20, 100, 115, 10);
            assert!(matches!(result, Err("AXIS_BOX_NOT_OBSERVED") | Ok((_, _))));
            if let Ok((x, y)) = result { assert!(x.is_empty() && y.is_empty()); }
        }
        let mut image = vec![255; 128 * 128];
        for x in 20..=115 { image[100 * 128 + x] = 192; }
        for y in 10..=100 { image[y * 128 + 20] = 192; }
        // A no-tick axis plus a disconnected glyph-like outline is not tick evidence.
        for x in 50..=60 { image[40 * 128 + x] = 128; image[55 * 128 + x] = 128; }
        for y in 40..=55 { image[y * 128 + 50] = 128; image[y * 128 + 60] = 128; }
        assert_eq!(measure_box_ticks(128, 128, 1, &image, 20, 100, 115, 10).unwrap(), (vec![], vec![]));
        image[100 * 128 + 20] = 255;
        assert_eq!(measure_box_ticks(128, 128, 1, &image, 20, 100, 115, 10), Err("AXIS_BOX_NOT_OBSERVED"));
    }

    #[test]
    fn supplied_box_preserves_tick_rejections_and_missing_evidence() {
        let mut plain = inward_tick_pixels();
        for x in [30, 50, 70, 90, 110] { for y in 96..100 { plain[y * 128 + x] = 255; } }
        for y in [20, 40, 60, 80] { for x in 21..=24 { plain[y * 128 + x] = 255; } }
        assert_eq!(measure_box_ticks(128, 128, 1, &plain, 20, 100, 115, 10).unwrap(), (vec![], vec![]));
        let mut partial = inward_tick_pixels();
        for x in [30, 50] { for y in 101..=104 { partial[y * 128 + x] = 0; } }
        for y in [20, 40] { for x in 16..20 { partial[y * 128 + x] = 0; } }
        assert_eq!(measure_box_ticks(128, 128, 1, &partial, 20, 100, 115, 10).unwrap(), (vec![], vec![]));
        let mut long = inward_tick_pixels();
        for y in 80..100 { long[y * 128 + 70] = 0; }
        for x in 21..=40 { long[40 * 128 + x] = 0; }
        assert_eq!(measure_box_ticks(128, 128, 1, &long, 20, 100, 115, 10).unwrap(), (vec![], vec![]));
        let mut adjacent = inward_tick_pixels();
        for y in 96..100 { adjacent[y * 128 + 71] = 0; }
        for x in 21..=24 { adjacent[41 * 128 + x] = 0; }
        for y in 90..100 { adjacent[y * 128 + 71] = 0; }
        for x in 21..=30 { adjacent[41 * 128 + x] = 0; }
        assert_eq!(measure_box_ticks(128, 128, 1, &adjacent, 20, 100, 115, 10).unwrap(), (vec![], vec![]));
    }

    #[test]
    fn supplied_box_uses_observed_outline_bridge_without_inventing_ticks() {
        let image = owned_outline_axis_fixture(true);
        let result = measure_box_ticks(768, 768, 1, &image, 230, 330, 425, 220).unwrap();
        assert_eq!(result, (vec![260, 320, 400], vec![240, 270, 320]));
        let empty = owned_outline_axis_fixture(false);
        assert_eq!(measure_box_ticks(768, 768, 1, &empty, 230, 330, 425, 220), Err("AXIS_BOX_NOT_OBSERVED"));
        BRIDGE_WORK.with(|work| work.set(MAX_PIXELS));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
        assert!(observed_axis_span_clipped(&image, 768, 768, 1, 230, 220, 330, false,
            Some((230, 220, 425, 330))).is_none());
        assert!(BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()));
        BRIDGE_WORK.with(|work| work.set(0));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
    }

    #[test]
    fn measured_tick_protocol_echoes_box_and_rejects_bad_frames_and_flags() {
        let pixels = grid_pixels();
        let mut frame = Vec::from(*b"GRT1");
        for value in [128u32, 128, 1, pixels.len() as u32, 20, 100, 115, 10] { frame.extend_from_slice(&value.to_le_bytes()); }
        frame.extend_from_slice(&pixels);
        assert_eq!(process_measure_ticks(&frame[..]).unwrap(), "TICKS1 20 100 115 10 30,70,110 20,50,80\n");
        assert_eq!(process_measure_ticks(&frame[..35]), Err("INVALID_PROTOCOL"));
        assert_eq!(process_measure_ticks(&frame[..frame.len() - 1]), Err("INVALID_PROTOCOL"));
        let mut trailing = frame.clone(); trailing.push(0);
        assert_eq!(process_measure_ticks(&trailing[..]), Err("INVALID_PROTOCOL"));
        let mut bad = frame.clone(); bad[0] = b'X';
        assert_eq!(process_measure_ticks(&bad[..]), Err("INVALID_PROTOCOL"));
        let mut bad_box = frame.clone(); bad_box[20..24].copy_from_slice(&127u32.to_le_bytes());
        assert_eq!(process_measure_ticks(&bad_box[..]), Err("INVALID_INPUT"));
        assert_eq!(measure_mode_requested(&["--measure-ticks".into()]), Ok(true));
        assert_eq!(measure_mode_requested(&["--measure-ticks".into(), "--diagnostics".into()]), Err("INVALID_PROTOCOL"));
        assert_eq!(measure_mode_requested(&["--measure-ticks".into(), "--review-candidates".into()]), Err("INVALID_PROTOCOL"));
        assert_eq!(measure_mode_requested(&["--measure-ticks".into(), "--measure-ticks-diagnostics".into()]), Ok(true));
        assert!(measure_diagnostics_requested(&["--measure-ticks".into(), "--measure-ticks-diagnostics".into()]));
        assert_eq!(measure_mode_requested(&["--measure-ticks-diagnostics".into()]), Err("INVALID_PROTOCOL"));
        assert_eq!(measure_mode_requested(&["--measure-ticks".into(), "--measure-ticks-diagnostics".into(), "--diagnostics".into()]), Err("INVALID_PROTOCOL"));
    }

    fn supplied_box_frame(pixels: &[u8], width: u32, height: u32, channels: u32,
        left: u32, bottom: u32, right: u32, top: u32) -> Vec<u8> {
        let mut frame = Vec::from(*b"GRT1");
        for value in [width, height, channels, pixels.len() as u32, left, bottom, right, top] {
            frame.extend_from_slice(&value.to_le_bytes());
        }
        frame.extend_from_slice(pixels);
        frame
    }

    fn simple_supplied_box_pixels() -> Vec<u8> {
        let mut image = vec![255; 128 * 128];
        for x in 20..=115 { image[100 * 128 + x] = 0; }
        for y in 10..=100 { image[y * 128 + 20] = 0; }
        for x in [40, 70, 100] { for y in 101..105 { image[y * 128 + x] = 0; } }
        for y in [20, 50, 80] { for x in 16..20 { image[y * 128 + x] = 0; } }
        image
    }

    fn affine_tick_frame(derived: &[u8], source: &[u8], matrix: [f64; 6]) -> Vec<u8> {
        let mut frame = Vec::from(*b"GRA1");
        for value in [128u32,128,1,derived.len() as u32,20,100,115,10,128,128,source.len() as u32] {
            frame.extend_from_slice(&value.to_le_bytes());
        }
        for value in matrix { frame.extend_from_slice(&value.to_le_bytes()); }
        frame.extend_from_slice(derived); frame.extend_from_slice(source); frame
    }

    #[test]
    fn affine_support_recovers_only_connected_original_evidence_on_each_axis() {
        let original = simple_supplied_box_pixels();
        let identity = [1.0,0.0,0.0,0.0,1.0,0.0];
        let expected = process_measure_ticks(&supplied_box_frame(&original,128,128,1,20,100,115,10)[..]).unwrap();
        for horizontal in [true,false] {
            let mut derived = original.clone();
            let missing = if horizontal { 100 * 128 + 55 } else { 60 * 128 + 20 };
            derived[missing] = 255;
            assert_eq!(process_measure_ticks(&supplied_box_frame(&derived,128,128,1,20,100,115,10)[..]), Err("AXIS_BOX_NOT_OBSERVED"));
            let frame = affine_tick_frame(&derived,&original,identity);
            assert_eq!(process_affine_ticks(&frame[..],false).unwrap(),expected);
            assert_eq!(process_affine_ticks(&frame[..],true).unwrap(),expected);
            let mut disconnected = original.clone(); disconnected[missing] = 255;
            let refused = affine_tick_frame(&derived,&disconnected,identity);
            assert_eq!(process_affine_ticks(&refused[..],false),Err("AXIS_BOX_NOT_OBSERVED"));
            let partial = process_affine_ticks(&refused[..],true).unwrap();
            assert_eq!(partial, if horizontal { "TICKS1 20 100 115 10 - 20,50,80\n" } else { "TICKS1 20 100 115 10 40,70,100 -\n" });
        }
        assert_eq!(process_affine_ticks(&affine_tick_frame(&original,&original,identity)[..],false).unwrap(),expected);
    }

    #[test]
    fn affine_tick_protocol_rejects_invalid_frames_and_preserves_empty_ticks() {
        let mut pixels = vec![255;128*128];
        for x in 20..=115 { pixels[100*128+x]=0; }
        for y in 10..=100 { pixels[y*128+20]=0; }
        let frame=affine_tick_frame(&pixels,&pixels,[1.0,0.0,0.0,0.0,1.0,0.0]);
        assert_eq!(process_affine_ticks(&frame[..],false).unwrap(),"TICKS1 20 100 115 10 - -\n");
        assert_eq!(process_affine_ticks(&frame[..95],false),Err("INVALID_PROTOCOL"));
        assert_eq!(process_affine_ticks(&frame[..frame.len()-1],false),Err("INVALID_PROTOCOL"));
        let mut extra=frame.clone(); extra.push(0); assert_eq!(process_affine_ticks(&extra[..],false),Err("INVALID_PROTOCOL"));
        for (offset,value) in [(48,f64::NAN),(48,0.0),(48,-1.0),(64,500.0)] {
            let mut bad=frame.clone(); bad[offset..offset+8].copy_from_slice(&value.to_le_bytes());
            assert_eq!(process_affine_ticks(&bad[..],false),Err("INVALID_INPUT"));
        }
        let mut wrong=frame.clone(); wrong[36..40].copy_from_slice(&4097u32.to_le_bytes());
        assert_eq!(process_affine_ticks(&wrong[..],false),Err("INVALID_INPUT"));
        for flag in ["--measure-affine-ticks","--measure-affine-ticks-manual-partial"] {
            assert_eq!(measure_mode_requested(&[flag.into()]),Ok(true));
            for other in ["--measure-ticks","--diagnostics","--measure-ticks-diagnostics","--review-candidates"] {
                assert_eq!(measure_mode_requested(&[flag.into(),other.into()]),Err("INVALID_PROTOCOL"));
            }
        }
    }

    #[test]
    fn affine_validation_allows_only_bounded_numerical_roundoff() {
        let pixels=vec![255;128*128];
        let build=|m| affine_support::AffineSupport::new(&pixels,128,128,m,0,100,95,10).is_some();
        assert!(build([1.0,0.0,1e-8,0.0,1.0,0.0]));
        assert!(!build([1.0,0.0,1e-4,0.0,1.0,0.0]));
        assert!(build([95.0/95.50000001,0.0,0.0,0.0,1.0,0.0]));
        assert!(!build([95.0/95.500001,0.0,0.0,0.0,1.0,0.0]));
    }

    #[test]
    fn affine_fallback_shares_work_budget_and_does_not_invent_ticks() {
        let mut source=vec![255;128*128];
        for x in 20..=115 { source[100*128+x]=0; }
        for y in 10..=100 { source[y*128+20]=0; }
        let mut derived=source.clone(); derived[100*128+55]=255;
        let matrix=[1.0,0.0,0.0,0.0,1.0,0.0];
        let frame=affine_tick_frame(&derived,&source,matrix);
        assert_eq!(process_affine_ticks(&frame[..],false).unwrap(),"TICKS1 20 100 115 10 - -\n");
        let support=affine_support::AffineSupport::new(&source,128,128,matrix,20,100,115,10).unwrap();
        BRIDGE_WORK.with(|work| work.set(MAX_PIXELS));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
        assert!(!support.connects((54,100),(56,100),(20,76,80,100)));
        assert!(BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()));
        // A derived search consuming the final unit leaves no fresh source budget,
        // and partial mode must not publish the other axis after exhaustion.
        let mut isolated=vec![255;128*128];
        isolated[100*128+54]=0; isolated[100*128+56]=0;
        BRIDGE_WORK.with(|work| work.set(MAX_PIXELS-1));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
        assert_eq!(measure_supplied_spans(&isolated,128,128,1,54,100,56,10,
            &mut None,true,Some(&support)),Err("AXIS_BOX_NOT_OBSERVED"));
        assert!(BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()));
        BRIDGE_WORK.with(|work| work.set(0));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
    }

    #[test]
    fn affine_fixed_interior_shear_distinguishes_connected_and_disconnected_source() {
        let matrix: [f64;6]=[1.0,0.0,0.0,-1.0/30.0,1.0,65.0/6.0];
        for disconnected in [false,true] {
            let mut source=vec![255u8;320*240];
            for x in 55..=265 { if !disconnected || !(15..=23).contains(&(x-55)) { source[(185+(x-55)/30)*320+x]=192; } }
            for y in 45..=185 { source[y*320+55]=192; }
            // Same inverse Gray8 bilinear sampling and positive rounding as the owned rectifier.
            let mut derived=vec![255u8;320*251];
            for y in 0..251 { for x in 0..320 {
                let sy=y as f64+x as f64/30.0-65.0/6.0;
                let y0=sy.floor() as isize; let frac=sy-y0 as f64;
                let sample=|row: isize| if (0..240).contains(&row) { source[row as usize*320+x] as f64 } else { 255.0 };
                derived[y*320+x]=(sample(y0)*(1.0-frac)+sample(y0+1)*frac).round() as u8;
            } }
            let mut frame=Vec::from(*b"GRA1");
            for value in [320u32,251,1,derived.len() as u32,55,194,265,54,320,240,source.len() as u32] { frame.extend_from_slice(&value.to_le_bytes()); }
            for value in matrix { frame.extend_from_slice(&value.to_le_bytes()); }
            frame.extend_from_slice(&derived); frame.extend_from_slice(&source);
            assert_eq!(process_measure_ticks(&supplied_box_frame(&derived,320,251,1,55,194,265,54)[..]),Err("AXIS_BOX_NOT_OBSERVED"));
            if disconnected { assert_eq!(process_affine_ticks(&frame[..],false),Err("AXIS_BOX_NOT_OBSERVED")); }
            else { assert_eq!(process_affine_ticks(&frame[..],false).unwrap(),"TICKS1 55 194 265 54 - -\n"); }
        }
    }

    #[test]
    fn manual_partial_retains_only_ticks_on_independently_observed_spans() {
        for horizontal in [true, false] {
            let mut pixels = simple_supplied_box_pixels();
            if horizontal {
                for y in 40..=80 { pixels[y * 128 + 20] = 255; }
            } else {
                for x in 40..=80 { pixels[100 * 128 + x] = 255; }
            }
            let unchanged = pixels.clone();
            let frame = supplied_box_frame(&pixels, 128, 128, 1, 20, 100, 115, 10);
            assert_eq!(process_measure_ticks(&frame[..]), Err("AXIS_BOX_NOT_OBSERVED"));
            let expected = if horizontal { "TICKS1 20 100 115 10 40,70,100 -\n" }
                else { "TICKS1 20 100 115 10 - 20,50,80\n" };
            assert_eq!(process_manual_partial_ticks(&frame[..]).unwrap(), expected);
            assert_eq!(pixels, unchanged);
        }
    }

    #[test]
    fn manual_partial_refuses_origin_and_both_unobserved_spans() {
        let mut pixels = simple_supplied_box_pixels();
        pixels[100 * 128 + 20] = 255;
        let frame = supplied_box_frame(&pixels, 128, 128, 1, 20, 100, 115, 10);
        assert_eq!(process_manual_partial_ticks(&frame[..]), Err("AXIS_BOX_NOT_OBSERVED"));
        pixels[100 * 128 + 20] = 0;
        for x in 40..=80 { pixels[100 * 128 + x] = 255; }
        for y in 40..=80 { pixels[y * 128 + 20] = 255; }
        let frame = supplied_box_frame(&pixels, 128, 128, 1, 20, 100, 115, 10);
        assert_eq!(process_manual_partial_ticks(&frame[..]), Err("AXIS_BOX_NOT_OBSERVED"));
    }

    #[test]
    fn manual_partial_full_observation_preserves_strict_wire_and_rejections() {
        for gray in [0, 128, 192] {
            let mut pixels = simple_supplied_box_pixels();
            for pixel in &mut pixels { if *pixel == 0 { *pixel = gray; } }
            for channels in [1, 3] {
                let input = if channels == 1 { pixels.clone() } else { pixels.iter().flat_map(|&v| [v, v, v]).collect() };
                let frame = supplied_box_frame(&input, 128, 128, channels, 20, 100, 115, 10);
                assert_eq!(process_manual_partial_ticks(&frame[..]), process_measure_ticks(&frame[..]));
                assert_eq!(process_manual_partial_ticks(&frame[..35]), Err("INVALID_PROTOCOL"));
                let mut extra = frame.clone(); extra.push(0);
                assert_eq!(process_manual_partial_ticks(&extra[..]), Err("INVALID_PROTOCOL"));
            }
        }
        let mut plain = vec![255; 128 * 128];
        for x in 20..=115 { plain[100 * 128 + x] = 0; }
        for y in 10..=100 { plain[y * 128 + 20] = 0; }
        let frame = supplied_box_frame(&plain, 128, 128, 1, 20, 100, 115, 10);
        assert_eq!(process_manual_partial_ticks(&frame[..]).unwrap(), "TICKS1 20 100 115 10 - -\n");
        assert_eq!(process_manual_partial_ticks(&frame[..]), process_measure_ticks(&frame[..]));
    }

    #[test]
    fn manual_partial_never_publishes_after_shared_bridge_budget_exhaustion() {
        for horizontal in [true, false] {
            let mut pixels = simple_supplied_box_pixels();
            // One small observed bridge triggers work after a valid origin. The
            // other axis remains fully observed with three genuine tick strokes.
            if horizontal { pixels[100 * 128 + 60] = 255; pixels[99 * 128 + 60] = 0; }
            else { pixels[60 * 128 + 20] = 255; pixels[60 * 128 + 21] = 0; }
            BRIDGE_WORK.with(|work| work.set(MAX_PIXELS));
            BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
            assert_eq!(measure_supplied_spans(&pixels, 128, 128, 1, 20, 100, 115, 10,
                &mut None, true, None), Err("AXIS_BOX_NOT_OBSERVED"));
            assert!(BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()));
            assert_eq!(BRIDGE_WORK.with(|work| work.get()), MAX_PIXELS);
            BRIDGE_WORK.with(|work| work.set(0));
            BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
        }
    }

    #[test]
    fn manual_partial_flag_is_explicit_and_cannot_mix_with_other_modes() {
        assert_eq!(measure_mode_requested(&["--measure-ticks-manual-partial".into()]), Ok(true));
        for other in ["--measure-ticks", "--measure-ticks-diagnostics", "--diagnostics", "--review-candidates"] {
            assert_eq!(measure_mode_requested(&["--measure-ticks-manual-partial".into(), other.into()]), Err("INVALID_PROTOCOL"));
            assert_eq!(measure_mode_requested(&[other.into(), "--measure-ticks-manual-partial".into()]), Err("INVALID_PROTOCOL"));
        }
    }

    #[test]
    fn explicit_diagnostic_mode_preserves_valid_measurement_and_reports_stage() {
        let pixels = simple_supplied_box_pixels();
        let frame = supplied_box_frame(&pixels, 128, 128, 1, 20, 100, 115, 10);
        let normal = process_measure_ticks(&frame[..]).unwrap();
        let (diagnosed, trace) = process_measure_ticks_diagnostic(&frame[..]);
        assert_eq!(diagnosed.unwrap(), normal);
        assert_eq!(normal, "TICKS1 20 100 115 10 40,70,100 20,50,80\n");
        assert_eq!(trace.stage, "TICKS_MEASURED");
        assert_eq!(trace.seed_gray_sum, Some(0));
        assert_eq!(trace.channels, Some(1));
        assert!(trace.bridge_work.is_some());
        assert!(trace.line().len() < 128);
    }

    #[test]
    fn supplied_box_diagnostic_distinguishes_seed_and_axis_span_failures() {
        let mut blank_origin = simple_supplied_box_pixels();
        blank_origin[100 * 128 + 20] = 255;
        let frame = supplied_box_frame(&blank_origin, 128, 128, 1, 20, 100, 115, 10);
        let (result, trace) = process_measure_ticks_diagnostic(&frame[..]);
        assert_eq!(result, Err("AXIS_BOX_NOT_OBSERVED"));
        assert_eq!(trace.stage, "ORIGIN_NOT_INK");
        assert_eq!(trace.seed_gray_sum, Some(255));

        let mut disconnected_x = simple_supplied_box_pixels();
        for x in 40..=80 { disconnected_x[100 * 128 + x] = 255; }
        let frame = supplied_box_frame(&disconnected_x, 128, 128, 1, 20, 100, 115, 10);
        let (result, trace) = process_measure_ticks_diagnostic(&frame[..]);
        assert_eq!(result, Err("AXIS_BOX_NOT_OBSERVED"));
        assert_eq!(trace.stage, "X_SPAN_UNOBSERVED");

        let mut disconnected_y = simple_supplied_box_pixels();
        for y in 40..=80 { disconnected_y[y * 128 + 20] = 255; }
        let frame = supplied_box_frame(&disconnected_y, 128, 128, 1, 20, 100, 115, 10);
        let (result, trace) = process_measure_ticks_diagnostic(&frame[..]);
        assert_eq!(result, Err("AXIS_BOX_NOT_OBSERVED"));
        assert_eq!(trace.stage, "Y_SPAN_UNOBSERVED");
    }

    #[test]
    fn malformed_diagnostic_request_is_unattempted() {
        let (result, trace) = process_measure_ticks_diagnostic(&b"bad"[..]);
        assert_eq!(result, Err("INVALID_PROTOCOL"));
        assert_eq!(trace, TickMeasureDiagnostic::default());
        assert!(trace.line().contains("UNATTEMPTED"));
    }
    #[test]
    fn diagnostic_trace_preserves_result_and_has_bounded_fields() {
        let pixels = grid_pixels();
        TRACE.with(|cell| cell.set(None));
        let ordinary = propose(128, 128, 1, &pixels);
        TRACE.with(|cell| cell.set(Some(Trace::default())));
        let diagnosed = propose(128, 128, 1, &pixels);
        assert_eq!(ordinary, diagnosed);
        let trace = TRACE.with(|cell| cell.replace(None)).unwrap();
        assert_eq!(trace.route, "GRID");
        assert!(trace.pairs_checked <= MAX_GRID_PAIR_CHECKS);
        assert_eq!(trace.qualified_pairs, 1);
        assert_eq!(trace.line().split_whitespace().count(), 11);
    }
    #[test]
    fn diagnostic_trace_records_grid_limits_and_rejections() {
        let mut image = grid_pixels();
        for x in [30, 70, 110] { for y in 101..=104 { image[y * 128 + x] = 255; } }
        TRACE.with(|cell| cell.set(Some(Trace::default())));
        assert_eq!(propose(128, 128, 1, &image), Err("AMBIGUOUS_AXIS"));
        let trace = TRACE.with(|cell| cell.replace(None)).unwrap();
        assert_eq!(trace.reason, "NO_VALID_PAIR");
        assert!(trace.pairs_checked > 0 && trace.pairs_checked <= MAX_GRID_PAIR_CHECKS);
        assert!(trace.x_tick_rejections > 0);
        let mut scores = vec![0; 256];
        for i in (10..=230).step_by(20) { scores[i] = 100; }
        TRACE.with(|cell| cell.set(Some(Trace::default())));
        assert_eq!(resolve_grid(256, 256, 1, &vec![255; 256*256], &scores, &scores, 40, 40, 4, 4), Err("AMBIGUOUS_AXIS"));
        let trace = TRACE.with(|cell| cell.replace(None)).unwrap();
        assert_eq!(trace.reason, "PAIR_LIMIT");
        assert_eq!(trace.pairs_checked, 0);
    }
    #[test]
    fn diagnostic_trace_records_thick_band_without_changing_rejection() {
        let mut scores = vec![0; 64];
        scores[10..15].fill(50);
        TRACE.with(|cell| cell.set(Some(Trace::default())));
        assert!(strong_line_bands(&scores, 20, true).is_err());
        assert_eq!(TRACE.with(|cell| cell.replace(None)).unwrap().reason, "THICK_ROW_BAND");
    }
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
    // Port of OpenCvLineCandidateProviderTests.CreateOccludedAxisFrame:
    // two-pixel axes, erased x=30..31/y=100..104 and optional 10x7 outline.
    // Padding forces the Rust content-extent path; ticks are additional observed ink.
    fn owned_outline_axis_fixture(connected: bool) -> Vec<u8> {
        const SIDE: usize = 768;
        const OX: usize = 200;
        const OY: usize = 200;
        let mut image = vec![255; SIDE * SIDE];
        for x in 30..=225 { for y in 130..=131 { image[(OY + y) * SIDE + OX + x] = 0; } }
        for y in 20..=131 { for x in 30..=31 { image[(OY + y) * SIDE + OX + x] = 0; } }
        for y in 100..=104 { for x in 30..=31 { image[(OY + y) * SIDE + OX + x] = 255; } }
        if connected {
            for x in 26..=35 { for y in [99, 105] { image[(OY + y) * SIDE + OX + x] = 0; } }
            for y in 99..=105 { for x in [26, 35] { image[(OY + y) * SIDE + OX + x] = 0; } }
        }
        for x in [60, 120, 200] { for y in 132..=135 { image[(OY + y) * SIDE + OX + x] = 0; } }
        for y in [40, 70, 120] { for x in 26..30 { image[(OY + y) * SIDE + OX + x] = 0; } }
        image
    }
    #[test]
    fn owned_outline_connected_axis_fallback_reproduction() {
        let image = owned_outline_axis_fixture(true);
        let proposal = propose(768, 768, 1, &image).expect("Observed marker outline connects the original axis ink");
        assert_eq!((proposal.left, proposal.bottom, proposal.right, proposal.top), (230, 330, 425, 220));
        assert_eq!(proposal.x_ticks, vec![260, 320, 400]);
        assert_eq!(proposal.y_ticks, vec![240, 270, 320], "outline edges are not ticks");
    }
    #[test]
    fn horizontal_outline_bridge_preserves_ticks_and_rejects_empty_gap() {
        for connected in [true, false] {
            let mut image = owned_outline_axis_fixture(false);
            const SIDE: usize = 768; const OX: usize = 200; const OY: usize = 200;
            for y in 100..=104 { for x in 30..=31 { image[(OY + y) * SIDE + OX + x] = 0; } }
            for y in 132..=135 {
                image[(OY + y) * SIDE + OX + 120] = 255; image[(OY + y) * SIDE + OX + 200] = 255;
                image[(OY + y) * SIDE + OX + 100] = 0; image[(OY + y) * SIDE + OX + 140] = 0;
            }
            for x in 120..=124 { for y in 130..=131 { image[(OY + y) * SIDE + OX + x] = 255; } }
            if connected {
                for x in 119..=125 { for y in [126, 135] { image[(OY + y) * SIDE + OX + x] = 0; } }
                for y in 126..=135 { for x in [119, 125] { image[(OY + y) * SIDE + OX + x] = 0; } }
            }
            let result = propose(SIDE, SIDE, 1, &image);
            if connected {
                let proposal = result.expect("horizontal outline joins original axis ink");
                assert_eq!(proposal.x_ticks, vec![260, 300, 340], "outline edges are not ticks");
                assert_eq!(proposal.y_ticks, vec![240, 270, 320]);
            } else { assert!(result.is_err()); }
        }
    }
    #[test]
    fn observed_ink_bridge_work_limit_fails_closed() {
        let image = owned_outline_axis_fixture(true);
        BRIDGE_WORK.with(|work| work.set(MAX_PIXELS));
        assert!(observed_axis_span(&image, 768, 768, 1, 230, 220, 330, false).is_none());
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
        let mut rows = vec![0; 768]; let mut cols = vec![0; 768];
        for y in 0..768 { for x in 0..768 { if image[y * 768 + x] == 0 { rows[y] += 1; cols[x] += 1; } } }
        assert_eq!(resolve_grid(768, 768, 1, &image, &rows, &cols, 40, 40, 5, 9), Err("AMBIGUOUS_AXIS"));
        assert!(BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()), "candidate search must fail closed on work exhaustion");
        BRIDGE_WORK.with(|work| work.set(0));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
    }
    #[test]
    fn owned_outline_empty_gap_fallback_negative() {
        let image = owned_outline_axis_fixture(false);
        assert!(propose(768, 768, 1, &image).is_err(), "Empty gap must not invent connected axis ink");
    }
    #[test]
    fn fallback_requires_connected_spans_without_changing_primary_success() {
        let mut source = inward_tick_pixels();
        source[100 * 128 + 60] = 255;
        source[55 * 128 + 20] = 255;
        // Primary success intentionally retains its historical behavior.
        assert!(propose(128, 128, 1, &source).is_ok());
        let padded = padded_pixels(&source, 256, 64, 64);
        // Strict connected recovery still refuses the padded broken spans.
        assert_eq!(propose(256, 256, 1, &padded), Err("AMBIGUOUS_AXIS"));
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
                    for x in [30, 33, 110] { for y in 101..=104 { a[y * 128 + x] = 0; } }
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
    #[test]
    fn owned_majority_coverage_axes_preserve_original_coordinates() {
        let width = 640;
        let height = 480;
        for (first, second) in [(0, 255), (63, 191), (127, 127), (191, 63)] {
            let mut gray = vec![255; width * height];
            for x in 99..=540 { gray[379 * width + x] = first; gray[380 * width + x] = second; }
            for y in 80..=380 {
                gray[y * width + 99] = gray[y * width + 99].min(first);
                gray[y * width + 100] = gray[y * width + 100].min(second);
            }
            for channels in [1, 3] {
                let pixels = if channels == 1 { gray.clone() } else { gray.iter().flat_map(|&v| [v, v, v]).collect() };
                let p = propose(width, height, channels, &pixels).expect("owned black axis coverage");
                assert!(p.left.abs_diff(100) <= 1 && p.bottom.abs_diff(380) <= 1);
                assert!(p.right.abs_diff(540) <= 1 && p.top.abs_diff(80) <= 1);
                assert!(p.x_ticks.is_empty() && p.y_ticks.is_empty());
            }
        }
    }
    #[test]
    fn owned_gray_axes_with_black_marker_remain_unsupported() {
        for ink in [0, 192] {
            let mut pixels = vec![255; 128 * 128];
            for x in 20..=115 { pixels[100 * 128 + x] = ink; }
            for y in 10..=100 { pixels[y * 128 + 20] = ink; }
            for x in [30, 70, 110] { for y in 101..=104 { pixels[y * 128 + x] = ink; } }
            for y in [20, 50, 80] { for x in 16..20 { pixels[y * 128 + x] = ink; } }
            for y in 58..=62 { for x in 58..=62 { pixels[y * 128 + x] = 0; } }
            TRACE.with(|cell| cell.set(Some(Trace::default())));
            let result = propose(128, 128, 1, &pixels);
            let diagnostic = TRACE.with(|cell| cell.replace(None)).unwrap();
            if ink == 192 {
                assert_eq!(result, Err("AMBIGUOUS_AXIS"));
                assert_eq!(diagnostic.reason, "THICK_ROW_BAND");
                continue;
            }
            let p = result.unwrap_or_else(|error| panic!("owned ink={ink}: {error}; {}", diagnostic.line()));
            assert_eq!((p.left, p.bottom, p.right, p.top), (20, 100, 115, 10));
            assert_eq!(p.x_ticks, vec![30, 70, 110]);
            assert_eq!(p.y_ticks, vec![20, 50, 80]);
        }
    }
    #[test]
    fn antialiased_axis_bands_preserve_observed_outward_ticks() {
        let (width, height) = (640, 480);
        for (first, second) in [(0, 255), (63, 191), (127, 127), (191, 63)] {
            let mut pixels = vec![255; width * height];
            for x in 99..=540 {
                pixels[379 * width + x] = first;
                pixels[380 * width + x] = second;
            }
            for y in 80..=380 {
                pixels[y * width + 99] = pixels[y * width + 99].min(first);
                pixels[y * width + 100] = pixels[y * width + 100].min(second);
            }
            for x in [140, 340, 540] {
                for y in 380..=385 {
                    pixels[y * width + x] = pixels[y * width + x].min(first);
                    pixels[y * width + x + 1] = pixels[y * width + x + 1].min(second);
                }
            }
            for y in [100, 230, 360] {
                for x in 94..=100 {
                    pixels[y * width + x] = pixels[y * width + x].min(first);
                    pixels[(y + 1) * width + x] = pixels[(y + 1) * width + x].min(second);
                }
            }
            let proposal = propose(width, height, 1, &pixels).expect("antialiased axes");
            for (actual, expected) in [(&proposal.x_ticks, [140, 340, 540]), (&proposal.y_ticks, [100, 230, 360])] {
                assert_eq!(actual.len(), expected.len(), "phase {first}/{second}: {actual:?}");
                for (&position, expected) in actual.iter().zip(expected) {
                    assert!(position.abs_diff(expected) <= 1, "phase {first}/{second}: {actual:?}");
                }
            }
        }
    }
    #[test]
    fn faint_strokes_and_gray_competing_axes_remain_rejected() {
        assert!(is_dark(&[127], 0, 1));
        assert!(!is_dark(&[128], 0, 1));
        assert!(is_dark(&[127, 128, 128], 0, 3));
        assert!(!is_dark(&[128, 128, 128], 0, 3));
        let mut faint = vec![255; 128 * 128];
        for x in 20..=115 { faint[100 * 128 + x] = 128; }
        for y in 10..=100 { faint[y * 128 + 20] = 128; }
        assert!(propose(128, 128, 1, &faint).is_err());
        let mut competing = vec![255; 64 * 64];
        for x in 0..64 { competing[50 * 64 + x] = 127; competing[56 * 64 + x] = 127; }
        assert_eq!(propose(64, 64, 1, &competing), Err("AMBIGUOUS_AXIS"));
        let grid = grid_pixels();
        let mut panels = padded_pixels(&grid, 256, 0, 0);
        for y in 0..128 {
            let start = (y + 128) * 256 + 128;
            panels[start..start + 128].copy_from_slice(&grid[y * 128..(y + 1) * 128]);
        }
        for pixel in &mut panels { if *pixel == 0 { *pixel = 127; } }
        assert_eq!(propose(256, 256, 1, &panels), Err("AMBIGUOUS_AXIS"));
    }
    #[test]
    fn irregular_measured_x_ticks_preserve_original_positions() {
        let mut a = vec![255; 128 * 128];
        for x in 8..=115 { a[100 * 128 + x] = 0; }
        for y in 10..=100 { a[y * 128 + 8] = 0; }
        for x in [20, 40, 100] { for y in 101..=104 { a[y * 128 + x] = 0; } }
        let p = propose(128, 128, 1, &a).unwrap();
        assert_eq!(p.x_ticks, vec![20, 40, 100]);
        assert_eq!(p.ticks, Some((20, 100)));
        assert!(matches!(validate_positions(vec![20, 40, 100], true), TickScan::Rejected));
        assert!(matches!(validate_positions(vec![20, 40, 43, 93], false), TickScan::Rejected));
        assert!(matches!(validate_positions(vec![20, 40, 60, 100], false), TickScan::Positions(_)));
    }
    fn small_plot_with_dotted_caption() -> Vec<u8> {
        let mut image = vec![255; 512 * 512];
        for x in 100..=195 { image[190 * 512 + x] = 0; }
        for y in 100..=190 { image[y * 512 + 100] = 0; }
        for x in [110, 140, 180] { for y in 191..=194 { image[y * 512 + x] = 0; } }
        for y in [110, 140, 180] { for x in 96..100 { image[y * 512 + x] = 0; } }
        for x in (0..512).step_by(2) { image[400 * 512 + x] = 0; }
        image
    }
    #[test]
    fn owned_review_retains_automatic_content_recovery() {
        let image = content_projection_decoy_pixels();
        for channels in [1, 3] {
            let pixels = if channels == 1 { image.clone() } else { image.iter().flat_map(|&p| [p; 3]).collect() };
            let unchanged = pixels.clone();
            let auto = propose(512,512,channels,&pixels).expect("automatic control");
            let mut frame = Vec::from(*b"GRA1");
            for value in [512u32,512,channels as u32,pixels.len() as u32] { frame.extend_from_slice(&value.to_le_bytes()); }
            frame.extend_from_slice(&pixels);
            let review = process_review(&frame[..]).expect("review must offer same strict owned plot");
            assert_eq!(review.len(),1);
            assert_eq!((review[0].left,review[0].bottom,review[0].right,review[0].top), (auto.left,auto.bottom,auto.right,auto.top));
            assert_eq!(review[0].x_ticks,auto.x_ticks); assert_eq!(review[0].y_ticks,auto.y_ticks);
            assert_eq!(review_response(&review).unwrap(), "REVIEW1 1\nBOX 80 420 440 250 140,260,380 280,330,380\n");
            assert_eq!(pixels,unchanged);
            assert!(BRIDGE_WORK.with(|work| work.get()) > 0);
        }
    }

    #[test]
    fn owned_review_content_recovery_keeps_missing_ink_and_ticks_refused() {
        let mut broken = content_projection_decoy_pixels();
        for x in 180..=240 { broken[420 * 512 + x] = 255; }
        assert!(review_proposals(512,512,1,&broken).is_err());
        let mut missing = content_projection_decoy_pixels();
        for x in [140,260,380] { for y in 421..=425 { missing[y*512+x]=255; } }
        assert!(review_proposals(512,512,1,&missing).is_err());
    }

    fn content_projection_decoy_pixels() -> Vec<u8> {
        let mut image = vec![255; 512 * 512];
        for x in 80..=440 { image[420 * 512 + x] = 0; }
        for y in 250..=420 { image[y * 512 + 80] = 0; }
        for x in [140, 260, 380] { for y in 421..=425 { image[y * 512 + x] = 0; } }
        for y in [280, 330, 380] { for x in 74..=79 { image[y * 512 + x] = 0; } }
        for y in (0..=398).step_by(2) { image[y * 512 + 20] = 0; }
        image[420 * 512 + 20] = 0;
        image
    }
    #[test]
    fn content_projection_disconnected_decoy_recovers_observed_plot() {
        let image = content_projection_decoy_pixels();
        BRIDGE_WORK.with(|work| work.set(0));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
        let expected = resolve_connected_runs(512, 512, 1, &image).expect("unique observed L with outward ticks");
        assert_eq!((expected.left, expected.bottom, expected.right, expected.top), (80, 420, 440, 250));
        assert_eq!(expected.x_ticks, vec![140, 260, 380]);
        assert_eq!(expected.y_ticks, vec![280, 330, 380]);
        TRACE.with(|cell| cell.set(Some(Trace::default())));
        assert_eq!(propose(512, 512, 1, &image), Ok(expected));
        let observed = TRACE.with(|cell| cell.replace(None)).unwrap();
        assert_eq!(observed.route, "CONNECTED_RUNS");
        assert_eq!(observed.qualified_pairs, 1);
        assert!(BRIDGE_WORK.with(|work| work.get()) > 0, "failed content measurement must not reset its charged bridge work");
    }
    #[test]
    fn content_span_recovery_keeps_real_gaps_and_multiple_plots_refused() {
        let mut broken = content_projection_decoy_pixels();
        for x in 180..=240 { broken[420 * 512 + x] = 255; }
        assert_eq!(propose(512, 512, 1, &broken), Err("AMBIGUOUS_AXIS"));

        let mut multiple = vec![255; 512 * 512];
        for (left, top, bottom) in [(80, 30, 200), (300, 250, 420)] {
            for x in left..=left + 140 { multiple[bottom * 512 + x] = 0; }
            for y in top..=bottom { multiple[y * 512 + left] = 0; }
            for x in [left + 20, left + 70, left + 120] {
                for y in bottom + 1..=bottom + 5 { multiple[y * 512 + x] = 0; }
            }
            for y in [top + 30, top + 80, top + 130] {
                for x in left - 6..left { multiple[y * 512 + x] = 0; }
            }
        }
        BRIDGE_WORK.with(|work| work.set(0));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
        TRACE.with(|cell| cell.set(Some(Trace::default())));
        assert_eq!(recover_content_span(512, 512, 1, &multiple, Err(DISCONNECTED_AXIS_SPAN), true), Err("AMBIGUOUS_AXIS"));
        let observed = TRACE.with(|cell| cell.replace(None)).unwrap();
        assert_eq!(observed.reason, "MULTIPLE_PAIRS");
        assert_eq!(observed.qualified_pairs, 2);
    }
    #[test]
    fn content_span_recovery_preserves_shared_budget_and_never_retries_other_failures() {
        let image = content_projection_decoy_pixels();
        BRIDGE_WORK.with(|work| work.set(17));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
        assert!(recover_content_span(512, 512, 1, &image, Err(DISCONNECTED_AXIS_SPAN), true).is_ok());
        assert_eq!(BRIDGE_WORK.with(|work| work.get()), 17);

        BRIDGE_WORK.with(|work| work.set(MAX_PIXELS));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(true));
        TRACE.with(|cell| cell.set(Some(Trace::default())));
        assert_eq!(recover_content_span(512, 512, 1, &image, Err(DISCONNECTED_AXIS_SPAN), true), Err("NO_AXIS"));
        assert_eq!(BRIDGE_WORK.with(|work| work.get()), MAX_PIXELS);
        assert!(BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()));
        assert_eq!(TRACE.with(|cell| cell.replace(None)).unwrap().pairs_checked, 0);
        BRIDGE_WORK.with(|work| work.set(0));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
        for reason in ["NO_INTERSECTION", "NO_AXIS", "AMBIGUOUS_AXIS"] {
            assert_eq!(recover_content_span(512, 512, 1, &image, Err(reason), true), Err(reason));
        }
        assert_eq!(recover_content_span(512, 512, 1, &image, Err(DISCONNECTED_AXIS_SPAN), false), Err("NO_AXIS"));
        assert_eq!(BRIDGE_WORK.with(|work| work.get()), 0);
    }
    #[test]
    fn content_span_recovery_refuses_when_remaining_bridge_work_exhausts() {
        let image = owned_outline_axis_fixture(true);
        BRIDGE_WORK.with(|work| work.set(MAX_PIXELS - 1));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
        assert_eq!(recover_content_span(768, 768, 1, &image, Err(DISCONNECTED_AXIS_SPAN), true), Err("AMBIGUOUS_AXIS"));
        assert_eq!(BRIDGE_WORK.with(|work| work.get()), MAX_PIXELS);
        assert!(BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()));
        BRIDGE_WORK.with(|work| work.set(0));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
    }
    #[test]
    fn dotted_caption_small_plot_recovers_original_pixels() {
        let image = small_plot_with_dotted_caption();
        let p = propose(512, 512, 1, &image).unwrap();
        assert_eq!((p.left, p.bottom, p.right, p.top), (100, 190, 195, 100));
        assert_eq!(p.x_ticks, vec![110, 140, 180]);
        assert_eq!(p.y_ticks, vec![110, 140, 180]);
    }
    #[test]
    fn connected_run_recovery_requires_ticks_and_rejects_crossing_caption() {
        let mut image = small_plot_with_dotted_caption();
        for x in [110, 140, 180] { for y in 191..=194 { image[y * 512 + x] = 255; } }
        assert!(propose(512, 512, 1, &image).is_err());
        let mut image = small_plot_with_dotted_caption();
        // A long continuous caption-like rule is competing geometry, not ignorable ink.
        for x in 0..512 { image[400 * 512 + x] = 0; }
        assert!(propose(512, 512, 1, &image).is_err());
    }
    #[test]
    fn connected_run_recovery_rejects_caption_only_and_two_plots() {
        let mut caption = vec![255; 512 * 512];
        for x in (0..512).step_by(2) { caption[400 * 512 + x] = 0; }
        assert!(propose(512, 512, 1, &caption).is_err());
        let mut image = small_plot_with_dotted_caption();
        for x in 300..=395 { image[190 * 512 + x] = 0; }
        for y in 100..=190 { image[y * 512 + 300] = 0; }
        for x in [310, 340, 380] { for y in 191..=194 { image[y * 512 + x] = 0; } }
        for y in [110, 140, 180] { for x in 296..300 { image[y * 512 + x] = 0; } }
        assert!(propose(512, 512, 1, &image).is_err());
    }
    #[test]
    fn connected_run_recovery_rejects_beyond_pair_budget() {
        let mut image = vec![255; 512 * 512];
        for y in (20..=240).step_by(20) { for x in 20..=240 { image[y * 512 + x] = 0; } }
        for x in (20..=240).step_by(20) { for y in 20..=240 { image[y * 512 + x] = 0; } }
        assert!(resolve_connected_runs(512, 512, 1, &image).is_err());
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
    fn antialiased_grid_pixels() -> Vec<u8> {
        let original = grid_pixels();
        let mut pixels = vec![255; 128 * 128];
        for y in 0..127 {
            for x in 0..127 {
                if original[y * 128 + x] == 0 {
                    for dy in 0..=1 {
                        for dx in 0..=1 { pixels[(y + dy) * 128 + x + dx] = 127; }
                    }
                }
            }
        }
        pixels
    }
    #[test]
    fn antialiased_two_pixel_grid_preserves_unique_axes_and_measured_ticks() {
        let p = propose(128, 128, 1, &antialiased_grid_pixels()).expect("unique antialiased grid");
        for (actual, expected) in [(p.left, 20), (p.bottom, 100), (p.right, 115), (p.top, 10)] {
            assert!(actual.abs_diff(expected) <= 1, "actual {actual}, expected {expected}");
        }
        for (actual, expected) in [(&p.x_ticks, [30, 70, 110]), (&p.y_ticks, [20, 50, 80])] {
            assert_eq!(actual.len(), expected.len(), "unexpected tick set {actual:?}");
            for (&position, expected) in actual.iter().zip(expected) {
                assert!(position.abs_diff(expected) <= 1, "unexpected tick set {actual:?}");
            }
        }
    }
    #[test]
    fn two_antialiased_tick_supported_grids_remain_ambiguous() {
        let source = antialiased_grid_pixels();
        let mut pixels = padded_pixels(&source, 256, 0, 0);
        for y in 0..128 {
            let start = (y + 128) * 256 + 128;
            pixels[start..start + 128].copy_from_slice(&source[y * 128..(y + 1) * 128]);
        }
        assert_eq!(propose(256, 256, 1, &pixels), Err("AMBIGUOUS_AXIS"));
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

    fn review_two_panels() -> Vec<u8> {
        let source = grid_pixels();
        let mut pixels = padded_pixels(&source, 256, 0, 0);
        for y in 0..128 {
            let start = (y + 128) * 256 + 128;
            pixels[start..start + 128].copy_from_slice(&source[y * 128..(y + 1) * 128]);
        }
        pixels
    }

    #[test]
    fn review_two_inward_panels_retains_existing_outward_only_guard() {
        let source = inward_tick_pixels();
        let single = propose(128, 128, 1, &source).unwrap();
        assert_eq!(single.x_ticks, vec![30, 50, 70, 90, 110]);
        assert_eq!(single.y_ticks, vec![20, 40, 60, 80]);
        let mut inward = padded_pixels(&source, 256, 0, 0);
        for y in 0..128 {
            let start = (y + 128) * 256 + 128;
            inward[start..start + 128].copy_from_slice(&source[y * 128..(y + 1) * 128]);
        }
        assert_eq!(propose(256, 256, 1, &inward), Err("AMBIGUOUS_AXIS"));
        assert_eq!(review_proposals(256, 256, 1, &inward), Err("NO_AXIS"));

        let outward = review_two_panels();
        assert_eq!(propose(256, 256, 1, &outward), Err("AMBIGUOUS_AXIS"));
        let choices = review_proposals(256, 256, 1, &outward).unwrap();
        assert_eq!(choices.len(), 2);
        assert_eq!(choices[0].x_ticks, vec![30, 70, 110]);
        assert_eq!(choices[0].y_ticks, vec![20, 50, 80]);
        assert_eq!(choices[1].x_ticks, vec![158, 198, 238]);
        assert_eq!(choices[1].y_ticks, vec![148, 178, 208]);
    }

    #[test]
    fn ambiguous_rows_cannot_hide_missing_column_extent_before_run_recovery() {
        let mut pixels = vec![255; 1280 * 960];
        for (ox, oy) in [(0, 0), (640, 480)] {
            for y in [80, 230, 380] { for x in 95..=580 { pixels[(oy + y) * 1280 + ox + x] = 0; } }
            for x in [100, 140, 340, 540] { for y in 80..=380 { pixels[(oy + y) * 1280 + ox + x] = 0; } }
            for x in [140, 340, 540] { for y in 381..=385 { pixels[(oy + y) * 1280 + ox + x] = 0; } }
        }
        assert_eq!(propose(1280, 960, 1, &pixels), Err("AMBIGUOUS_AXIS"));
        let choices = review_proposals(1280, 960, 1, &pixels).expect("missing column evidence must reach existing connected-run recovery");
        assert_eq!(choices.iter().map(|p| (p.left, p.bottom, p.right, p.top)).collect::<Vec<_>>(),
            vec![(100, 380, 580, 80), (740, 860, 1220, 560)]);
        assert_eq!(choices[0].x_ticks, vec![140, 340, 540]);
        assert_eq!(choices[0].y_ticks, vec![80, 230, 380]);
    }

    #[test]
    fn independent_projection_selection_preserves_qualified_ambiguity() {
        let mut ambiguous = vec![0; 32]; ambiguous[10] = 40; ambiguous[20] = 40;
        let mut missing = vec![0; 32]; missing[5] = 30;
        let mut unique = vec![0; 32]; unique[5] = 40;
        assert_eq!(select_pair(&ambiguous, &missing, 40, 40), Err("NO_AXIS"));
        assert_eq!(select_pair(&missing, &ambiguous, 40, 40), Err("NO_AXIS"));
        assert_eq!(select_pair(&ambiguous, &unique, 40, 40), Err("AMBIGUOUS_AXIS"));
        assert_eq!(select_pair(&unique, &ambiguous, 40, 40), Err("AMBIGUOUS_AXIS"));
        assert_eq!(select_pair(&unique, &unique, 40, 40), Ok((5, 5)));
    }

    #[test]
    fn complete_review_retains_both_observed_panels_without_changing_suggest() {
        let pixels = review_two_panels();
        let original = pixels.clone();
        assert_eq!(propose(256, 256, 1, &pixels), Err("AMBIGUOUS_AXIS"));
        let choices = review_proposals(256, 256, 1, &pixels).unwrap();
        assert_eq!(choices.iter().map(|p| (p.left, p.bottom, p.right, p.top)).collect::<Vec<_>>(),
            vec![(20, 100, 115, 10), (148, 228, 243, 138)]);
        assert_eq!(choices[0].x_ticks, vec![30, 70, 110]);
        assert_eq!(choices[1].y_ticks, vec![148, 178, 208]);
        assert_eq!(choices[0].y_ticks, vec![20, 50, 80]);
        assert_eq!(choices[1].x_ticks, vec![158, 198, 238]);
        assert_eq!(review_response(&choices).unwrap(), "REVIEW1 2\nBOX 20 100 115 10 30,70,110 20,50,80\nBOX 148 228 243 138 158,198,238 148,178,208\n");
        assert_eq!(pixels, original);
        assert_eq!(propose(256, 256, 1, &pixels), Err("AMBIGUOUS_AXIS"));
    }

    #[test]
    fn complete_review_unique_and_gray_controls_preserve_original_geometry() {
        let source = grid_pixels();
        let ordinary = propose(128, 128, 1, &source).unwrap();
        assert_eq!(review_proposals(128, 128, 1, &source).unwrap(), vec![ordinary]);
        let mut gray = review_two_panels();
        for pixel in &mut gray { if *pixel == 0 { *pixel = 127; } }
        let choices = review_proposals(256, 256, 1, &gray).unwrap();
        assert_eq!(choices.len(), 2);
        let rgb = gray.iter().flat_map(|&pixel| [pixel; 3]).collect::<Vec<_>>();
        assert_eq!(review_proposals(256, 256, 3, &rgb).unwrap(), choices);
        for pixel in &mut gray { if *pixel == 127 { *pixel = 128; } }
        assert_eq!(review_proposals(256, 256, 1, &gray), Err("NO_AXIS"));
    }

    #[test]
    fn complete_review_rejects_blank_sparse_and_inward_only_ticks() {
        assert_eq!(review_proposals(128, 128, 1, &vec![255; 128 * 128]), Err("NO_AXIS"));
        for retained in [0, 2] {
            let mut pixels = grid_pixels();
            for &x in &[30, 70, 110][retained..] {
                for y in 101..=104 { pixels[y * 128 + x] = 255; }
            }
            assert_eq!(review_proposals(128, 128, 1, &pixels), Err("NO_AXIS"));
        }
        let mut inward = grid_pixels();
        for x in [30, 70, 110] {
            for y in 101..=104 { inward[y * 128 + x] = 255; }
            for y in 96..100 { inward[y * 128 + x] = 0; }
        }
        assert_eq!(review_proposals(128, 128, 1, &inward), Err("NO_AXIS"));
    }

    #[test]
    fn complete_review_resource_and_thick_band_failures_return_no_prefix() {
        let mut pixels = vec![255; 256 * 256];
        for y in (10..=230).step_by(20) { for x in 10..=230 { pixels[y * 256 + x] = 0; } }
        for x in (10..=230).step_by(20) { for y in 10..=230 { pixels[y * 256 + x] = 0; } }
        assert_eq!(review_proposals(256, 256, 1, &pixels), Err("AMBIGUOUS_AXIS"));
        assert_eq!(BRIDGE_WORK.with(|work| work.get()), 0, "pair overflow must precede measurement");
        let mut thick = grid_pixels();
        for y in 96..=100 { for x in 20..=115 { thick[y * 128 + x] = 0; } }
        assert_eq!(review_proposals(128, 128, 1, &thick), Err("AMBIGUOUS_AXIS"));
        let mut rows = vec![0; 512];
        for y in (10..400).step_by(3) { rows[y] = 100; }
        assert_eq!(review_grid(512, 512, 1, &vec![255; 512 * 512], &rows, &rows, 40, 40, 4, 4), Err("AMBIGUOUS_AXIS"));
    }

    #[test]
    fn complete_review_discards_an_earlier_choice_if_later_bridge_work_exhausts() {
        let mut pixels = review_two_panels();
        for x in 180..=184 { pixels[228 * 256 + x] = 255; }
        for x in 179..=185 { pixels[229 * 256 + x] = 0; }
        assert_eq!(review_proposals(256, 256, 1, &pixels).unwrap().len(), 2);
        let mut rows = vec![0usize; 256];
        let mut cols = vec![0usize; 256];
        for y in 0..256 { for x in 0..256 {
            if pixels[y * 256 + x] == 0 { rows[y] += 1; cols[x] += 1; }
        } }
        BRIDGE_WORK.with(|work| work.set(MAX_PIXELS));
        BRIDGE_EXHAUSTED.with(|exhausted| exhausted.set(false));
        let row_minimum = (cols.iter().rposition(|&n| n > 0).unwrap() - cols.iter().position(|&n| n > 0).unwrap() + 1) * 2 / 5;
        let col_minimum = (rows.iter().rposition(|&n| n > 0).unwrap() - rows.iter().position(|&n| n > 0).unwrap() + 1) * 2 / 5;
        assert_eq!(review_grid(256, 256, 1, &pixels, &rows, &cols, row_minimum, col_minimum, 11, 11), Err("AMBIGUOUS_AXIS"));
        assert!(BRIDGE_EXHAUSTED.with(|exhausted| exhausted.get()));
    }

    #[test]
    fn complete_review_reuses_bounded_frame_protocol_and_rejects_response_overflow() {
        let pixels = grid_pixels();
        let mut frame = Vec::from(*b"GRA1");
        for value in [128u32, 128, 1, pixels.len() as u32] { frame.extend_from_slice(&value.to_le_bytes()); }
        frame.extend_from_slice(&pixels);
        assert_eq!(process(&frame[..]).unwrap(), propose(128, 128, 1, &pixels).unwrap());
        assert_eq!(process_review(&frame[..]).unwrap(), review_proposals(128, 128, 1, &pixels).unwrap());
        frame.push(0);
        assert_eq!(process_review(&frame[..]), Err("INVALID_PROTOCOL"));
        assert_eq!(process(&frame[..]), Err("INVALID_PROTOCOL"));
        assert_eq!(process_review(&b"bad"[..]), Err("INVALID_PROTOCOL"));
        let maximum = (0..128).map(|_| Proposal { left: 3000, bottom: 4000, right: 4000, top: 3000,
            confidence: 0., ticks: None, x_ticks: (0..128).map(|i| 3000 + 4*i).collect(),
            y_ticks: (0..128).map(|i| 3000 + 4*i).collect() }).collect::<Vec<_>>();
        assert_eq!(review_response(&maximum), Err("AMBIGUOUS_AXIS"));
        assert!(review_response(&maximum[..1]).unwrap().len() < MAX_REVIEW_RESPONSE_BYTES);
    }
}
