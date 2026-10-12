namespace Jellyfin.Plugin.BulgarianSubs

// How to download the subtitle file
type DownloadStrategy =
  | DirectUrl of url: string * referer: string
  | FormPage of pageUrl: string * referer: string

/// Media context carried by new subtitle IDs for stateless archive selection.
[<CLIMutable>]
type SubtitleSelectionContext =
  { Title: string
    Year: System.Nullable<int>
    Season: System.Nullable<int>
    Episode: System.Nullable<int>
    FileName: string }

// Internal representation of a search result before converting to Jellyfin's format
type InternalSubtitleInfo =
  { Id: string
    Title: string
    ProviderName: string
    Format: string option
    Author: string option
    DownloadCount: int option
    FrameRate: float option
    Rating: float option
    DownloadStrategy: DownloadStrategy
    UploadDate: System.DateTime option
    InfoPageUrl: string option }
