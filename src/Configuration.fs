namespace Jellyfin.Plugin.BulgarianSubs

open MediaBrowser.Model.Plugins

type PluginConfiguration() =
  inherit BasePluginConfiguration()

  /// Enable/disable specific providers
  member val EnableSabBz = true with get, set
  member val EnableSubsunacs = true with get, set
  member val EnableYavkaNet = false with get, set  // Disabled by default - returns 403 Forbidden
  member val EnablePodnapisi = true with get, set
