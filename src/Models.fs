namespace Jellyfin.Plugin.BulgarianSubs

// How to download the subtitle file
type DownloadStrategy =
  | DirectUrl of url: string * referer: string
  | FormPage of pageUrl: string * referer: string

// Internal representation of a search result before converting to Jellyfin's format
type InternalSubtitleInfo =
  { Id: string
    Title: string
    ProviderName: string
    Format: string option
    Author: string option
    DownloadCount: int option
    DownloadStrategy: DownloadStrategy
    UploadDate: System.DateTime option }
