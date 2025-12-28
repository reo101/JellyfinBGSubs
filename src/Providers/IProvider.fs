namespace Jellyfin.Plugin.BulgarianSubs.Providers

open System.Net.Http
open Jellyfin.Plugin.BulgarianSubs

/// Interface for subtitle providers to reduce boilerplate
type IProvider =
  abstract member Name: string
  abstract member Referer: string
  abstract member SearchUrl: query: string -> year: int option -> string
  abstract member CreateSearchRequest: url: string -> searchTerm: string -> HttpRequestMessage
  abstract member CreateDownloadStrategy: url: string -> DownloadStrategy
  abstract member ParseResults: html: string -> InternalSubtitleInfo seq
