namespace Jellyfin.Plugin.BulgarianSubs.Providers

open Jellyfin.Plugin.BulgarianSubs

/// Interface for subtitle providers to reduce boilerplate
type IProvider =
  abstract member Name: string
  abstract member SearchUrl: query: string -> year: int option -> string
  abstract member ParseResults: html: string -> InternalSubtitleInfo seq
