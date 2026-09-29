# RatioMaster.NET 2.2.0

- **Linux AppImages** — fix the glibc startup error on Ubuntu 22.04 and preserve portable settings between launches.
- **Signed Windows builds** — Authenticode signatures and trusted timestamps on x64 and ARM64 downloads.
- **Torrent and session reliability** — validate cached torrents, reload changed files, and keep counters and saved tabs consistent.
- **Tracker robustness** — reject malformed responses and ambiguous client profiles; reduce memory use when processing peer lists.
- **Interface and activation** — prevent actions on closed tabs, respect active-session input locks, and improve single-instance activation.
- **Android 17** — handle local-network permissions and cancelled starts while preserving public tracker access when optional LAN access is denied.
- **Dependencies** — use stable Avalonia 12.1.3 and SkiaSharp 3.119.4 with the official .NET 11 RC1 toolchain; no nightly packages.
- **Build organization** — centralize generated files under `artifacts`, keep release packages in `Installer/Output`, and detect Visual Studio 2026 build tools.
