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
    inputs.flake-parts.lib.mkFlake {inherit inputs;} {
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
      }: let
        fs = lib.fileset;
      in {
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

        packages.default = let
          version = "1.0.0.0";
          src = fs.toSource {
            root = ./.;
            fileset = fs.unions [
              (fs.fileFilter (f: f.hasExt "fs" || f.hasExt "fsproj") ./src)
              ./Jellyfin.Plugin.BulgarianSubs.fsproj
            ];
          };
          pluginGuid = let
            pluginFs = builtins.readFile "${src}/src/Plugin.fs";
            match = builtins.match ''.*Guid\.Parse\("([^"]+)"\).*'' pluginFs;
          in
            if match != null then builtins.head match else throw "Could not extract GUID from Plugin.fs";
          targetAbi = "10.11.5.0";
        in pkgs.buildDotnetModule {
          pname = "Jellyfin.Plugin.BulgarianSubs";
          inherit version src;

          projectFile = "Jellyfin.Plugin.BulgarianSubs.fsproj";

          nugetDeps = ./deps.json;

          dotnet-sdk = pkgs.dotnet-sdk_9;
          dotnet-runtime = pkgs.dotnet-runtime_9;

          executables = [];

          nativeBuildInputs = [ pkgs.jq ];

          env = {
            JELLYFIN_PATH = "${pkgs.jellyfin}/lib/jellyfin";
          };

          installPhase = ''
            install -d -m 700 $out/BulgarianSubs_${version}

            # Copy only the plugin DLL and its actual dependencies (not Jellyfin's own DLLs)
            install -m 600 bin/Release/net9.0/linux-x64/Jellyfin.Plugin.BulgarianSubs.dll $out/BulgarianSubs_${version}/
            install -m 600 bin/Release/net9.0/linux-x64/FSharp.Core.dll $out/BulgarianSubs_${version}/
            install -m 600 bin/Release/net9.0/linux-x64/HtmlAgilityPack.dll $out/BulgarianSubs_${version}/
            install -m 600 bin/Release/net9.0/linux-x64/SharpCompress.dll $out/BulgarianSubs_${version}/

            # Generate meta.json dynamically
            jq -n \
              --arg category "Subtitles" \
              --arg description "Finds Bulgarian subtitles from multiple providers: Subs.Sab.Bz, Subsunacs, Yavka.net, and Podnapisi.net" \
              --arg guid "${pluginGuid}" \
              --arg name "Bulgarian Subtitles" \
              --arg overview "Bulgarian subtitle provider with multiple sources" \
              --arg owner "reo101" \
              --arg targetAbi "${targetAbi}" \
              --arg version "${version}" \
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
                assemblies: [
                  "Jellyfin.Plugin.BulgarianSubs.dll",
                  "FSharp.Core.dll",
                  "HtmlAgilityPack.dll",
                  "SharpCompress.dll"
                ]
              }' > $out/BulgarianSubs_${version}/meta.json

            install -m 600 ${pkgs.fetchurl {
              url = "https://zamunda.net/pic/pic/z_icons/bgsubs.png";
              hash = "sha256-6WCRVR7KRYBEbPeynkGfIg5IlyTimrvDwINiaIdSMN4=";
            }} $out/BulgarianSubs_${version}/icon.png
          '';
        };

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
          program = pkgs.writeShellScriptBin "update-deps" ''
            ${lib.getExe pkgs.nuget-to-json} . > ./deps.json
            echo "✓ Updated deps.json"
          '';
        };

        apps.default = config.apps.update-deps;
      };
    };
}
