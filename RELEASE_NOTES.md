# RatioMaster.NET 2.1.0

- **UDP trackers** — announce and scrape over UDP with IPv4/IPv6 support, cancellation and bounded
  retransmissions. UDP trackers require a direct connection; the proxy options apply to HTTP/HTTPS.
- **v2 and hybrid torrents** — load v1, v2 and hybrid metadata with size, hash and integrity checks.
  The selected client profile is checked before starting a pure v2 torrent; hybrids retain their v1
  identity where required. Transmission 4.0.6, µTorrent and BitTorrent profiles exclude pure v2.
- **Pause and automatic recovery** — pause and resume a session without changing its identity.
  Upload pauses when the tracker reports no leechers and resumes when they return through normal
  announces; a manual pause remains independent. Upload and download keep separate Random controls.
- **Reliable counters and completion** — resume the counters belonging to the loaded torrent, keep
  them isolated between tabs, and freeze elapsed time when a session stops. Queued display updates use coherent current state; graph samples account for elapsed time. When completion and an
  automatic stop coincide, completion is announced first; a successful finish is recorded only after
  the final notifications succeed.
- **Tracker retries and feedback** — temporary connection failures preserve pending announce events
  for retry. An explicit tracker refusal stops the affected session, including an empty failure
  message. Tracker warnings remain visible without stopping the session, and valid announce intervals
  longer than a day are respected.
- **HTTP compatibility** — read complete replies without waiting for the server to close the
  connection, validate compressed responses within size limits, and keep redirects inside a common
  timeout. Escaped tracker URLs, existing query parameters and per-session tracker IDs are preserved.
- **Proxy and IPv6 connections** — improve HTTP CONNECT and SOCKS4/4a/5 negotiation, authenticated
  proxy handling, IPv4 fallback and IPv6 proxy endpoints. Listener collisions and cancellation stay
  isolated to the affected session.
- **Updated client profiles** — add qBittorrent 5.2.3 and 5.1.4, µTorrent 3.5.4 and BitTorrent
  7.10.3 (44429). Correct build-specific µTorrent identities, Transmission 4.0.6 key and encoding
  rules, and libtorrent-based identity generation.
- **Custom identities and profiles** — keep raw peer IDs consistent between HTTP, UDP and the peer
  handshake, with a readable `hex:` form for binary IDs. Generated keys follow the selected profile's
  refresh policy while custom keys remain untouched. A validated `clients.json` can extend or override
  the catalogue; invalid files leave the built-in profiles available.
- **Safer saved sessions** — save setting changes, edited tracker URLs and torrent counters with
  atomic replacement and a previous-snapshot backup. Recovery keeps the healthy backup intact. Fallback storage is isolated per installation;
  resetting one tab's settings preserves its counters and the other tabs. Missing profiles and older
  settings receive explicit migration handling.
- **Clearer controls and status** — shorten stop-condition labels, colour the seeder/leecher counts,
  and keep specific session statuses in one line below the terminal. A taller status bar and matching default window height provide
  more breathing room. The leechers/seeders ratio is available as an automatic stop condition.
- **Desktop and phone layouts** — retain the tabbed landscape/portrait interface, align the main
  action buttons, and shorten button captions only when space is limited. Thin scrollbars have a
  wider interaction area and a clean focus outline; the changelog viewer adapts to the available screen size.
- **Terminal tools** — filter, copy or save displayed log lines and choose whether to follow new
  output. Bounded queues and batched text updates reduce repeated text rebuilding while keeping the
  terminal selectable and independently scrollable. Clear resets both the log and graph; Set defaults uses the same cleanup without erasing torrent counters.
- **Manual updates and desktop closing** — confirm a manual tracker update with a live countdown,
  remember the choice to keep active sessions in the tray or quit, and save final counters before
  exiting. Window position and size are restored, with keyboard shortcuts for common actions.
- **Android lifecycle** — handle Back by dismissing an open dialog or backgrounding active sessions,
  avoid repeated notification-permission prompts, and coordinate the foreground service and its
  notification with running or paused sessions. Safe-area insets, the 125% scale and touch controls
  are retained.
- **Diagnostics and release history** — open local version/platform details, read the changelog and
  check GitHub for a newer stable release on request. Opening the information dialog does not start
  an update check or contact a separate statistics service.
- **Single-file Windows ARM64** — statically link Skia and HarfBuzz into the Native AOT executable,
  matching the single-EXE distribution used on x64. No graphics DLLs are shipped alongside it or
  extracted at startup; Windows rendering remains software-based.
- **Build and validation tools** — add headless build commands, clearer process failures, protected
  output handling and repeatable local validation. Windows/Linux/Android packaging keeps the existing
  output locations; a macOS packaging script and a Windows/Linux/macOS validation workflow are included.

Recent upstream development by [NikolayIT](https://github.com/NikolayIT/RatioMaster.NET) was reviewed
and adapted for this release. This fork retains its tabbed interface and standalone emulation: no
other torrent client needs to be installed, running or inspected.
