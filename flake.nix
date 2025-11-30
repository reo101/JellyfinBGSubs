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

        packages.default = pkgs.buildDotnetModule {
          pname = "Jellyfin.Plugin.BulgarianSubs";
          version = "1.0.0";

          src = fs.toSource {
            root = ./.;
            fileset = fs.unions [
              (fs.fileFilter (f: f.hasExt "fs" || f.hasExt "fsproj") ./src)
              ./Jellyfin.Plugin.BulgarianSubs.fsproj
            ];
          };

          projectFile = "Jellyfin.Plugin.BulgarianSubs.fsproj";

          nugetDeps = ./deps.json;

          dotnet-sdk = pkgs.dotnet-sdk_9;
          dotnet-runtime = pkgs.dotnet-runtime_9;

          executables = [];

          JELLYFIN_PATH = "${pkgs.jellyfin}/lib/jellyfin";

          installPhase = ''
            mkdir -p $out/BulgarianSubs_1.0.0.0

            cp bin/Release/net9.0/linux-x64/Jellyfin.Plugin.BulgarianSubs.dll $out/BulgarianSubs_1.0.0.0/

            cp ${./meta.json} $out/BulgarianSubs_1.0.0.0/meta.json

            cp ${pkgs.fetchurl {
              url = "https://zamunda.net/pic/pic/z_icons/bgsubs.png";
              hash = "sha256-6WCRVR7KRYBEbPeynkGfIg5IlyTimrvDwINiaIdSMN4=";
            }} $out/BulgarianSubs_1.0.0.0/jellyfin-plugin-bulgariansubs.png

            find $out/BulgarianSubs_1.0.0.0 -exec chmod 600 {} ';'
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
