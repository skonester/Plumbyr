$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$manifest = Join-Path $repoRoot 'plumbyr/Reclaim/Cargo.toml'
$target = Join-Path $repoRoot '.build/recovery'
# The portable app must work without a separately installed VC++ runtime.
$previousFlags = $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUSTFLAGS
try {
    $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUSTFLAGS = ($previousFlags + ' -C target-feature=+crt-static').Trim()
    & cargo build --release --locked --target x86_64-pc-windows-msvc --manifest-path $manifest --target-dir $target
    if ($LASTEXITCODE -ne 0) { throw 'Recovery worker build failed. Install stable Rust for Windows x64 and the Visual C++ build tools.' }
} finally {
    $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUSTFLAGS = $previousFlags
}
