{
  description = "Jellyfin Bulgarian Subtitles Plugin (F#)";

  nixConfig = {
    commit-lockfile-summary = "chore(flake): update `flake.lock`";
  };

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-parts.url = "github:hercules-ci/flake-parts";
    nix-systems.url = "github:nix-systems/default";
    treefmt-nix.url = "github:numtide/treefmt-nix";
    pre-commit-hooks.url = "github:cachix/pre-commit-hooks.nix";
  };

  outputs = inputs:
    inputs.flake-parts.lib.mkFlake {inherit inputs;} ({lib, ...}: let
      fs = lib.fileset;

      # Single source of truth for plugin metadata
      pluginGuid = "93b5ed36-e282-4d55-9c49-0121203b7293";
      pluginVersion = "1.0.0.0";
      targetAbi = "10.11.5.0";
      pluginRepo = "reo101/JellyfinBGSubs";

      pluginSrc = let
        src = fs.toSource {
          root = ./.;
          fileset = fs.unions [
            (fs.fileFilter (f: f.hasExt "fs" || f.hasExt "fsproj") ./src)
            ./Jellyfin.Plugin.BulgarianSubs.fsproj
          ];
        };

        # Verify Plugin.fs contains the expected GUID
        pluginFsContent = builtins.readFile "${src}/src/Plugin.fs";
        pluginFsGuidMatch = builtins.match ''.*Guid\.Parse\("([^"]+)"\).*'' pluginFsContent;
        pluginFsGuid =
          if pluginFsGuidMatch != null
          then builtins.head pluginFsGuidMatch
          else throw "Could not extract GUID from Plugin.fs";
      in
        assert pluginGuid == pluginFsGuid; src;

      # Plugin assemblies - single source of truth for DLL copying and meta.json
      pluginAssemblies = [
        "Jellyfin.Plugin.BulgarianSubs.dll"
        "FSharp.Core.dll"
        "HtmlAgilityPack.dll"
        "SharpCompress.dll"
      ];
    in {
      imports = [
        inputs.treefmt-nix.flakeModule
        inputs.pre-commit-hooks.flakeModule
      ];
      systems = import inputs.nix-systems;

      perSystem = {
        config,
        pkgs,
        lib,
        ...
      }: {
        treefmt.config = {
          projectRootFile = "flake.nix";
          programs = {
            alejandra.enable = true;
            yamlfmt.enable = true;
            fantomas.enable = true;
          };
        };

        pre-commit.settings = {
          hooks = {
            treefmt = {
              enable = true;
              name = "treefmt";
              description = "Format with treefmt";
              entry = lib.getExe pkgs.treefmt;
              language = "system";
              pass_filenames = false;
              stages = ["commit"];
            };
          };
        };

        packages.default = pkgs.buildDotnetModule {
          pname = "Jellyfin.Plugin.BulgarianSubs";
          version = pluginVersion;
          src = pluginSrc;

          projectFile = "Jellyfin.Plugin.BulgarianSubs.fsproj";

          nugetDeps = ./deps.json;

          dotnet-sdk = pkgs.dotnet-sdk_9;
          dotnet-runtime = pkgs.dotnet-runtime_9;

          executables = [];

          nativeBuildInputs = [pkgs.jq];

          env = {
            JELLYFIN_PATH = "${pkgs.jellyfin}/lib/jellyfin";
          };

          installPhase = let
            assembliesJson = builtins.toJSON pluginAssemblies;
            copyAssemblies =
              lib.concatMapStringsSep "\n" (
                dll: "install -m 600 bin/Release/net9.0/linux-x64/${dll} $out/BulgarianSubs_${pluginVersion}/"
              )
              pluginAssemblies;
          in ''
            install -d -m 700 $out/BulgarianSubs_${pluginVersion}

            # Copy plugin DLLs
            ${copyAssemblies}

            # Generate meta.json dynamically
            jq -n \
              --arg category "Subtitles" \
              --arg description "Finds Bulgarian subtitles from multiple providers: Subs.Sab.Bz, Subsunacs, Yavka.net, and Podnapisi.net" \
              --arg guid "${pluginGuid}" \
              --arg name "Bulgarian Subtitles" \
              --arg overview "Bulgarian subtitle provider with multiple sources" \
              --arg owner "reo101" \
              --arg targetAbi "${targetAbi}" \
              --arg version "${pluginVersion}" \
              --argjson assemblies '${assembliesJson}' \
              '{
                category: $category,
                description: $description,
                guid: $guid,
                name: $name,
                overview: $overview,
                owner: $owner,
                targetAbi: $targetAbi,
                version: $version,
                status: "Active",
                autoUpdate: false,
                imagePath: "icon.png",
                assemblies: $assemblies
              }' > $out/BulgarianSubs_${pluginVersion}/meta.json

            install -m 600 ${pkgs.fetchurl {
              url = "https://zamunda.net/pic/pic/z_icons/bgsubs.png";
              hash = "sha256-6WCRVR7KRYBEbPeynkGfIg5IlyTimrvDwINiaIdSMN4=";
            }} $out/BulgarianSubs_${pluginVersion}/icon.png
          '';
        };

        packages.zip =
          pkgs.runCommand "bulgariansubs-${pluginVersion}.zip" {
            nativeBuildInputs = [
              pkgs.zip
              pkgs.coreutils
              pkgs.findutils
            ];
          } ''
            export LC_ALL=C TZ=UTC

            mkdir -p /tmp/build
            cp -r ${config.packages.default}/BulgarianSubs_*/ /tmp/build/
            cd /tmp/build

            # Normalize mtimes (earliest ZIP-safe timestamp)
            find BulgarianSubs_1.0.0.0 -exec touch -t 198001010000.00 {} +

            # Build deterministic zip with sorted files
            zip -X -o -9 "$out" BulgarianSubs_1.0.0.0
            find BulgarianSubs_1.0.0.0 -type f -print0 \
              | sort -z \
              | xargs -0 zip -X -o -9 "$out"
          '';

        devShells.default = pkgs.mkShell {
          buildInputs = with pkgs; [
            just
            prek

            dotnet-sdk_9
            nuget-to-json
            fsautocomplete
            # FIXME: not packaged
            # fsharplint
            fantomas
          ];

          env = {
            JELLYFIN_PATH = "${pkgs.jellyfin}/lib/jellyfin";
          };

          shellHook = ''
            ${config.pre-commit.installationScript}
          '';
        };

        apps.update-deps = {
          type = "app";
          program = lib.getExe (pkgs.writeShellScriptBin "update-deps" ''
            ${lib.getExe pkgs.nuget-to-json} . > ./deps.json
            echo "✓ Updated deps.json"
          '');
        };

        apps.update-manifest = let
          sourceUrl = "https://github.com/${pluginRepo}/releases/download/v${pluginVersion}/bulgariansubs-${pluginVersion}.zip";
        in {
          type = "app";
          program = lib.getExe (pkgs.writeShellScriptBin "update-manifest" ''
            CHECKSUM=$(md5sum ${config.packages.zip} | cut -d ' ' -f 1 | tr 'a-z' 'A-Z')

            ${lib.getExe pkgs.jq} -n \
              --arg guid "${pluginGuid}" \
              --arg version "${pluginVersion}" \
              --arg targetAbi "${targetAbi}" \
              --arg sourceUrl "${sourceUrl}" \
              --arg checksum "$CHECKSUM" \
              '[{
                category: "Subtitles",
                guid: $guid,
                name: "Bulgarian Subtitles",
                description: "Finds Bulgarian subtitles from multiple providers: Subs.Sab.Bz, Subsunacs, Yavka.net, and Podnapisi.net",
                owner: "reo101",
                overview: "Bulgarian subtitle provider with multiple sources",
                versions: [{
                  version: $version,
                  targetAbi: $targetAbi,
                  sourceUrl: $sourceUrl,
                  checksum: $checksum
                }]
              }]' > ./manifest.json
            echo "✓ Updated manifest.json"
            echo "  GUID: ${pluginGuid}"
            echo "  Version: ${pluginVersion}"
            echo "  Checksum: $CHECKSUM"
            echo "  Source URL: ${sourceUrl}"
          '');
        };

        apps.default = config.apps.update-deps;
      };
    });
}
