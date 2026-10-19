# RatioMaster.NET 2.3.0

- **Languages** — the interface is available in 42 languages. It follows the system language and can be changed in the ⓘ panel; Arabic, Hebrew and Persian are laid out right to left.
- **Ports and peers** — tabs of one client share its listening port, drawn from that client's usual range and kept between launches. Incoming connections, including encrypted ones, are answered as that client would.
- **Client profiles** — add qBittorrent 5.2.4 (default), Transmission 4.1.3 and Deluge 2.2.0; µTorrent 3.6.0 moves to build 47254 and BitTorrent to 7.11.0. µTorrent 3.5.5, 3.5.4 and BitTorrent 7.10.3 are retired: their tabs move to the current version of the same client and keep its port.
- **Announces** — escaping, headers, address parameters and announce intervals follow each client more closely. Each session reports its own transfer, and Finished (%) follows the progress reached.
- **Tracker responses** — a refused completed announce is retried, a pending one is sent before stopping, stopped is only sent after an accepted started, and manual updates wait for the tracker's minimum interval.
- **Several torrents** — open several files or a whole folder at once. A torrent already open for the same tracker is detected.
- **Accessibility and saving** — screen-reader names and full texts in tooltips; counters and progress are saved after each accepted announce, and sessions from 2.0 are migrated.
- **Dependencies** — Avalonia 12.1.4.
