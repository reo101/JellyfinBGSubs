# Tests

Run commands from the repository root.

```console
nix develop -c just test          # Offline F# regression tests
nix develop -c just test-lsp      # Real FSAutocomplete protocol probe
nix build .#tests --no-link       # Packaged runner; regressions run in the sandbox
nix build .#checks.x86_64-linux.jellyfin --no-link -L
```

The NixOS VM runs the pinned Jellyfin server and packaged plugin. A local F#
HTTP fixture supplies Windows-1251 search HTML and a ZIP with a Cyrillic SRT.
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

The fixture/API runner modes (`--fixture`, `--vm VERSION`, `--vm-restart`) are
for the isolated VM, not an existing Jellyfin installation.

Live-site download tests remain optional and are not run in CI:

```console
nix develop -c just test-download
```

See `FSAUTOCOMPLETE_BUGS.md` for measured LSP results and their limitations.
