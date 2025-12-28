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

# Run unit tests in watch mode
test-watch:
    dotnet watch run --project Jellyfin.Plugin.BulgarianSubs.Tests.fsproj

# === DEBUGGING ===

# Run quick debug tests (search Inception and The Matrix)
debug: build-all
    dotnet ./bin/Debug/net9.0/Jellyfin.Plugin.BulgarianSubs.Debugger.dll

# Run interactive debugger
debug-interactive: build-all
    dotnet ./bin/Debug/net9.0/Jellyfin.Plugin.BulgarianSubs.Debugger.dll interactive

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
