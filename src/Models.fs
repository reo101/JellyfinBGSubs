namespace Jellyfin.Plugin.BulgarianSubs

// Internal representation of a search result before converting to Jellyfin's format
type InternalSubtitleInfo =
  { Id: string
    Title: string
    ProviderName: string
    Format: string option
    Author: string option
    DownloadUrl: string }
