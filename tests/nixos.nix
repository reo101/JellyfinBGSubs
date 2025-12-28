{
  pkgs,
  plugin,
  tests,
}: let
  testExecutable = "${tests}/bin/Jellyfin.Plugin.BulgarianSubs.Tests";
  media = pkgs.runCommand "subtitle-test-media" {nativeBuildInputs = [pkgs.jellyfin-ffmpeg];} ''
    ffmpeg -f lavfi -i color=c=black:s=32x32:r=1:d=1 -c:v ffv1 -f matroska -y "$out"
  '';
in
  pkgs.testers.runNixOSTest {
    name = "jellyfin-bulgarian-subtitles";
    globalTimeout = 600;

    nodes.machine = {lib, ...}: {
      services.jellyfin.enable = true;
      networking.hosts."127.0.0.1" = ["subs.sab.bz"];
      # No public subtitle sites or metadata services may influence this test.
      networking.firewall.extraCommands = ''
        iptables -A OUTPUT ! -o lo -j REJECT
        ip6tables -A OUTPUT ! -o lo -j REJECT
      '';
      virtualisation.memorySize = 2048;
      virtualisation.diskSize = 8192;
      virtualisation.cores = 2;

      systemd.services.subtitle-fixture = {
        wantedBy = ["multi-user.target"];
        serviceConfig.ExecStart = "${testExecutable} --fixture";
      };

      systemd.services.jellyfin = {
        requires = ["subtitle-fixture.service"];
        after = ["subtitle-fixture.service"];
        preStart = lib.mkBefore ''
          if [ ! -d /var/lib/jellyfin/plugins/BulgarianSubs_${plugin.version} ]; then
            mkdir -p /var/lib/jellyfin/plugins/configurations
            cp -r ${plugin}/BulgarianSubs_${plugin.version} /var/lib/jellyfin/plugins/
            chmod -R u+w /var/lib/jellyfin/plugins/BulgarianSubs_${plugin.version}
            cat > /var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.BulgarianSubs.xml <<'EOF'
          <PluginConfiguration>
            <EnableSabBz>true</EnableSabBz>
            <EnableSubsunacs>false</EnableSubsunacs>
            <EnableYavkaNet>false</EnableYavkaNet>
            <EnablePodnapisi>false</EnablePodnapisi>
          </PluginConfiguration>
          EOF
          fi
          mkdir -p /var/lib/jellyfin/media
          if [ ! -e /var/lib/jellyfin/media/Fixture.Movie.2020.mkv ]; then
            cp ${media} /var/lib/jellyfin/media/Fixture.Movie.2020.mkv
            chmod u+w /var/lib/jellyfin/media/Fixture.Movie.2020.mkv
          fi
        '';
      };
    };

    # Python is only the NixOS driver; application assertions live in F#.
    testScript = ''
      start_all()
      machine.wait_for_unit("subtitle-fixture.service")
      machine.wait_for_unit("jellyfin.service")
      machine.wait_for_open_port(8096)
      machine.succeed("${testExecutable} --vm ${plugin.version}")
      machine.succeed("systemctl restart jellyfin.service")
      machine.wait_for_open_port(8096)
      machine.succeed("${testExecutable} --vm-restart")
    '';
  }
