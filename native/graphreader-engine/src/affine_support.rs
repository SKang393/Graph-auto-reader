// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
use std::collections::VecDeque;

/// Original Gray8 evidence and an original-to-derived pixel-center transform.
pub(super) struct AffineSupport<'a> {
    pixels: &'a [u8], width: usize, height: usize,
    matrix: [f64; 6], inverse: [f64; 6],
}

impl<'a> AffineSupport<'a> {
    pub(super) fn new(pixels: &'a [u8], width: usize, height: usize, matrix: [f64; 6],
        left: usize, bottom: usize, right: usize, top: usize) -> Option<Self>
    {
        if !(32..=4096).contains(&width) || !(32..=4096).contains(&height) ||
            width.checked_mul(height).filter(|&n| n <= super::MAX_PIXELS) != Some(pixels.len()) ||
            matrix.iter().any(|n| !n.is_finite()) { return None; }
        let [a,b,c,d,e,f] = matrix;
        let det = a * e - b * d;
        if !det.is_finite() || det <= 0.0 { return None; }
        let inverse = [e/det, -b/det, (b*f-e*c)/det, -d/det, a/det, (d*c-a*f)/det];
        if inverse.iter().any(|n| !n.is_finite()) || inverse[0] <= 0.0 || inverse[4] <= 0.0 { return None; }
        let support = Self { pixels, width, height, matrix, inverse };
        // Validation only: floating inversion at a border or rounded-length midpoint
        // can differ by a few ulps. Sampling and clipped support remain unmodified.
        const VALIDATION_ROUNDOFF: f64 = 1e-7;
        let corners = [(left,bottom),(right,bottom),(left,top),(right,top)];
        if corners.iter().any(|&(x,y)| { let (sx,sy)=support.original(x as f64,y as f64);
            !sx.is_finite() || !sy.is_finite() || sx < -VALIDATION_ROUNDOFF || sy < -VALIDATION_ROUNDOFF ||
                sx > (width-1) as f64 + VALIDATION_ROUNDOFF || sy > (height-1) as f64 + VALIDATION_ROUNDOFF }) { return None; }
        let (ox,oy) = support.original(left as f64,bottom as f64);
        let (xx,xy) = support.original(right as f64,bottom as f64);
        let (yx,yy) = support.original(left as f64,top as f64);
        let length_matches = |actual: f64, span: usize| actual.is_finite() &&
            actual >= span as f64 - 0.5 - VALIDATION_ROUNDOFF && actual <= span as f64 + 0.5 + VALIDATION_ROUNDOFF;
        if xx <= ox || yy >= oy || !length_matches((xx-ox).hypot(xy-oy),right-left) ||
            !length_matches((yx-ox).hypot(yy-oy),bottom-top) { return None; }
        Some(support)
    }

    fn original(&self, x: f64, y: f64) -> (f64,f64) {
        let [a,b,c,d,e,f]=self.inverse; (a*x+b*y+c,d*x+e*y+f)
    }
    fn inside(&self, x: usize, y: usize, window: (usize,usize,usize,usize)) -> bool {
        let [a,b,c,d,e,f]=self.matrix; let x=x as f64; let y=y as f64;
        let px=a*x+b*y+c; let py=d*x+e*y+f;
        px.is_finite() && py.is_finite() && px >= window.0 as f64 && py >= window.1 as f64 &&
            px <= window.2 as f64 && py <= window.3 as f64
    }
    fn contributors(&self, endpoint: (usize,usize), window: (usize,usize,usize,usize)) -> Option<Vec<usize>> {
        let (x,y)=self.original(endpoint.0 as f64,endpoint.1 as f64);
        if !x.is_finite() || !y.is_finite() || x < 0.0 || y < 0.0 || x > (self.width-1) as f64 || y > (self.height-1) as f64 { return None; }
        let x0=x.floor() as usize; let y0=y.floor() as usize; let fx=x-x0 as f64; let fy=y-y0 as f64;
        let mut out=Vec::with_capacity(4);
        for dy in 0..=1 { for dx in 0..=1 {
            let weight=(if dx==0 {1.0-fx} else {fx})*(if dy==0 {1.0-fy} else {fy});
            if weight <= 0.0 { continue; }
            let xx=x0+dx; let yy=y0+dy;
            if xx>=self.width || yy>=self.height { return None; }
            let index=yy*self.width+xx;
            if self.pixels[index] < 200 {
                if !self.inside(xx,yy,window) { return None; }
                out.push(index);
            }
        } }
        if out.is_empty() { None } else { Some(out) }
    }

    pub(super) fn connects(&self, from: (usize,usize), to: (usize,usize), window: (usize,usize,usize,usize)) -> bool {
        let Some(first)=self.contributors(from,window) else { return false; };
        let Some(last)=self.contributors(to,window) else { return false; };
        let mut visited=vec![false; self.pixels.len()];
        let mut queue=VecDeque::new();
        for index in first { if !visited[index] { visited[index]=true; queue.push_back(index); } }
        while let Some(index)=queue.pop_front() {
            if !super::consume_bridge_work() { return false; }
            if last.contains(&index) { return true; }
            let x=index%self.width; let y=index/self.width;
            for dy in -1isize..=1 { for dx in -1isize..=1 {
                let Some(nx)=x.checked_add_signed(dx) else { continue; };
                let Some(ny)=y.checked_add_signed(dy) else { continue; };
                if nx>=self.width || ny>=self.height { continue; }
                let next=ny*self.width+nx;
                if !visited[next] && self.pixels[next]<200 && self.inside(nx,ny,window) { visited[next]=true; queue.push_back(next); }
            } }
        }
        false
    }
}
