// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
// Test-only child: confirms startup, then stops consuming the input pipe.
use std::io::{self, Read};
use std::time::Duration;

fn main() {
    let mut header = [0; 20];
    io::stdin().read_exact(&mut header).unwrap();
    let path = std::path::PathBuf::from(std::env::var_os("GRAPHREADER_BLOCKING_READY").unwrap());
    let temporary = path.with_extension("tmp");
    std::fs::write(&temporary, std::process::id().to_string()).unwrap();
    std::fs::rename(temporary, path).unwrap();
    std::thread::sleep(Duration::from_secs(60));
}
