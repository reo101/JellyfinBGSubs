namespace Jellyfin.Plugin.BulgarianSubs.Providers

open System
open System.Text.RegularExpressions
open System.Xml
open Jellyfin.Plugin.BulgarianSubs

module PodnapisiImpl =

  let private clean (s: string) =
    if String.IsNullOrWhiteSpace s then "" else s.Trim()

  let private tryParseInt (s: string) =
    match Int32.TryParse(clean s) with
    | true, v -> Some v
    | _ -> None

  let private tryParseDate (s: string) =
    if String.IsNullOrWhiteSpace s then
      None
    else
      try
        Some(DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind))
      with _ ->
        None

  /// Parse search results from Podnapisi XML API response
  /// Returns InternalSubtitleInfo for each subtitle found
  let parseSearchResults (xml: string) : InternalSubtitleInfo seq =
    try
      let doc = XmlDocument()
      doc.LoadXml(xml)

      let subtitleNodes = doc.SelectNodes("//subtitle")

      if subtitleNodes = null then
        Seq.empty
      else
        subtitleNodes
        |> Seq.cast<XmlNode>
        |> Seq.choose (fun node ->
          let getNodeText (name: string) =
            match node.SelectSingleNode(name) with
            | null -> None
            | n when String.IsNullOrWhiteSpace n.InnerText -> None
            | n -> Some(clean n.InnerText)

          match getNodeText "pid" with
          | None -> None
          | Some pid ->
            let title =
              getNodeText "release"
              |> Option.orElse (getNodeText "title")
              |> Option.defaultValue "Unknown"

            let downloads = getNodeText "downloads" |> Option.bind tryParseInt
            let rating = getNodeText "rating"
            let urlPath = getNodeText "url"

            let downloadUrl = $"https://www.podnapisi.net/subtitles/{pid}/download"
            let infoPageUrl =
              urlPath
              |> Option.map (fun p -> $"https://www.podnapisi.net{p}")
              |> Option.defaultValue $"https://www.podnapisi.net/subtitles/{pid}"

            Some
              { Id = pid
                Title = title
                ProviderName = "Podnapisi.net"
                Format = Some "srt"
                Author = None
                DownloadCount = downloads
                DownloadStrategy = DirectUrl(downloadUrl, "https://www.podnapisi.net/")
                UploadDate = None
                InfoPageUrl = Some infoPageUrl })
    with _ ->
      Seq.empty

/// Podnapisi.net subtitle provider implementation
/// Uses XML API for search, direct download for subtitles
type Podnapisi() =
  interface IProvider with
    member _.Name = "Podnapisi.net"

    member _.SearchUrl (query: string) (year: int option) : string =
      let yearParam = year |> Option.map (fun y -> $"&sY={y}") |> Option.defaultValue ""
      $"https://www.podnapisi.net/subtitles/search/old?sXML=1&sL=bg&sK={query}{yearParam}"

    member _.ParseResults(xml: string) : InternalSubtitleInfo seq = PodnapisiImpl.parseSearchResults xml
