# Tests

Run commands from the repository root.

```console
nix develop -c just test          # Offline F# regression tests
nix develop -c just test-lsp      # Real FSAutocomplete protocol probe
nix build .#tests --no-link       # Packaged runner; regressions run in the sandbox
nix build .#checks.x86_64-linux.jellyfin --no-link -L
```

The NixOS VM runs the pinned Jellyfin server and packaged plugin. A local F#
HTTP fixture supplies Windows-1251 search HTML and a ZIP with an unrelated
subtitle first and the requested movie's Cyrillic SRT second.
IPv4 and IPv6 traffic outside loopback is rejected. F# assertions exercise
plugin discovery/version, persisted configuration, movie indexing, Bulgarian
search, subtitle download/extraction/saving, unsupported languages, disabling
an adapter, restart persistence, and stable IDs after re-enabling it.
Python is only the NixOS VM driver's boot/wait/restart orchestration.
The VM requires Linux with KVM; it has an 8 GiB writable disk and 2 GiB RAM.

`DownloadSecurityTests.fs` checks form targets and redirect destinations before
requests, redirect count/method handling, streamed byte limits despite missing
or dishonest Content-Length headers, oversized ZIP entries, and invalid archives.
Downloads/search pages are limited to 20 MiB; extracted/plain subtitles to
10 MiB. Only the provider host on default HTTP(S) ports is allowed, with no
HTTPS-to-HTTP downgrades. Cross-host
CDN redirects are deliberately rejected. The production named HTTP client
turns off automatic redirects so every hop is checked.

`SubtitleMatchingTests.fs` checks versioned context-bearing IDs and legacy
URL-only IDs, season-pack search results, episode boundaries, release/title/year
ordering, wrong-first-entry archives, `.sub` selection, ambiguous packs, and
limits on the selected entry. New IDs carry only the media filename, not its
path, and need no in-memory lookup to survive restart. Context-aware extraction
rejects archives with more than 4096 entries. Old URL-only IDs retain first-entry
selection because they have no media context. New multi-file packs with no
unique compatible match fail explicitly.

The fixture/API runner modes (`--fixture`, `--vm VERSION`, `--vm-restart`) are
for the isolated VM, not an existing Jellyfin installation.

Live-site download tests remain optional and are not run in CI:

```console
nix develop -c just test-download
```

## Manual provider verification (2026-10-12)

- Sab.Bz title search and contextual ZIP extraction succeeded. GET
  `index.php?act=search&imdb=1375666` and `imdb=tt1375666` returned five
  Inception results; `imdb=0133093` returned Matrix results. IMDb querying
  is confirmed for this provider but not yet used by the plugin.
- Sab.Bz embeds session IDs and HTML-escaped separators in download links.
  Parsed attachment URLs now use only `act=download&attach_id=ID`, preserving
  valid downloads and stable identifiers across sessions.
- Subsunacs title/year search returned ten Inception results. The real
  episode 7z extracted successfully; the first movie ZIP had ambiguous
  releases and was explicitly rejected with generic movie context.
  Its `imdbcheck=1` checkbox exists, but GET searches for both IMDb-ID forms
  returned no rows; IMDb-ID support remains unverified.
- Yavka's current form uses `sea` for the title and UTF-8. Both are reflected
  in the adapter. Normal title and IMDb POST searches returned Cloudflare
  challenge responses (`403`), so downloads remain unverified and the
  adapter stays disabled by default. No challenge bypass was attempted.
- Podnapisi's XML endpoint could not resolve; an independent public DNS
  query returned NXDOMAIN for `www.podnapisi.net`. Neither its search nor
  IMDb support could be verified.

The optional live runner reports each failure separately, checks HTTP status
and SRT timing cues instead of accepting error pages, and exercises contextual
selection. This run reported four passing checks and two failures (ambiguous
Subsunacs movie pack and blocked Yavka search), not an all-provider pass.

See `FSAUTOCOMPLETE_BUGS.md` for measured LSP results and their limitations.
