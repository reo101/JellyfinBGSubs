# === RESTORE ===

# Restore all projects
restore:
    dotnet restore Jellyfin.Plugin.BulgarianSubs.fsproj
    dotnet restore Jellyfin.Plugin.BulgarianSubs.Tests.fsproj
    dotnet restore Jellyfin.Plugin.BulgarianSubs.Debugger.fsproj

# === BUILD ===

# Build the main plugin
build:
    dotnet build Jellyfin.Plugin.BulgarianSubs.fsproj

# Build release
build-release:
    dotnet build -c Release Jellyfin.Plugin.BulgarianSubs.fsproj

# Build all projects (plugin + debugger, excluding tests)
build-all:
    dotnet build Jellyfin.Plugin.BulgarianSubs.fsproj
    dotnet build Jellyfin.Plugin.BulgarianSubs.Debugger.fsproj

# Clean build artifacts
clean:
    dotnet clean
    rm -rf bin obj

# Watch and rebuild on changes
watch:
    dotnet watch build Jellyfin.Plugin.BulgarianSubs.fsproj

# === TESTING ===

# Run unit tests
test:
    dotnet run --project Jellyfin.Plugin.BulgarianSubs.Tests.fsproj

# Run live-site download tests (not part of CI)
test-download:
    dotnet run --project Jellyfin.Plugin.BulgarianSubs.Tests.fsproj -- --download

# Run the offline Jellyfin API integration test in a NixOS VM
test-vm:
    nix build .#checks.x86_64-linux.jellyfin -L

# Probe FSAutocomplete with real diagnostics, edits, hover and completion
test-lsp:
    dotnet run --project Jellyfin.Plugin.BulgarianSubs.Tests.fsproj -- --lsp

# Run unit tests in watch mode
test-watch:
    dotnet watch run --project Jellyfin.Plugin.BulgarianSubs.Tests.fsproj

# === DEBUGGING ===

# Run quick debug tests (search Inception and The Matrix)
debug: build-all
    dotnet run --no-build --project Jellyfin.Plugin.BulgarianSubs.Debugger.fsproj

# Run interactive debugger
debug-interactive: build-all
    dotnet run --no-build --project Jellyfin.Plugin.BulgarianSubs.Debugger.fsproj -- interactive

# === CODE QUALITY ===

# Format code with treefmt
fmt:
    treefmt

# Run pre-commit hooks
check:
    prek run

# Full CI (format + check + build + test)
ci: fmt check build-all test

# === HELP ===

# Show all available targets
help:
    @just --list --unsorted
