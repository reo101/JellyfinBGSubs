# Build the project
build:
    dotnet build

# Build release
build-release:
    dotnet build -c Release

# Clean build artifacts
clean:
    dotnet clean
    rm -rf bin obj

# Format code with treefmt
fmt:
    treefmt

# Run pre-commit hooks
check:
    prek run

# Full CI (format + check + build)
ci: fmt check build

# Watch and rebuild on changes
watch:
    dotnet watch build
