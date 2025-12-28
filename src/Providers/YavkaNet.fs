namespace Jellyfin.Plugin.BulgarianSubs.Providers

open HtmlAgilityPack
open System
open System.Collections.Generic
open System.Net.Http
open System.Text.RegularExpressions
open Jellyfin.Plugin.BulgarianSubs

module YavkaNetImpl =

  /// Represents a subtitle search result from yavka.net
  type YavkaSearchResult =
    { PageUrl: string
      Title: string
      Year: string
      Fps: string
      Uploader: string
      Downloads: string }

  /// Parse search results from yavka.net search response HTML
  /// Yavka returns results in an HTML table with rows for each subtitle
  let parseSearchResults (html: string) : YavkaSearchResult list =
    try
      let doc = HtmlDocument()
      doc.LoadHtml(html)

      // Find all rows in the results table
      match doc.DocumentNode.SelectNodes("//tr") with
      | null -> []
      | rows ->
        rows
        |> Seq.choose (fun row ->
          // Each result row has specific structure
          let linkNode = row.SelectSingleNode(".//a[@class='balon' or @class='selector']")

          if linkNode = null then
            None
          else
            try
              let href = linkNode.GetAttributeValue("href", "")

              if String.IsNullOrWhiteSpace href then
                None
              else
                let pageUrl = "https://yavka.net/" + href.TrimStart('/')
                let title = linkNode.InnerText.Trim()

                // Extract additional info from row cells
                let cells = row.SelectNodes(".//td")

                let fps =
                  cells
                  |> Seq.tryFind (fun td ->
                    let title = td.GetAttributeValue("title", "")
                    title.Contains("Кадри") || title.Contains("fps"))
                  |> Option.map (fun td -> td.InnerText.Trim())
                  |> Option.defaultValue ""

                let uploader =
                  cells
                  |> Seq.tryFind (fun td -> td.SelectSingleNode(".//a[@class='click']") <> null)
                  |> Option.bind (fun td ->
                    let a = td.SelectSingleNode(".//a[@class='click']")
                    if a <> null then Some(a.InnerText.Trim()) else None)
                  |> Option.defaultValue ""

                let downloads =
                  cells
                  |> Seq.tryFind (fun td -> td.SelectSingleNode(".//div/strong") <> null)
                  |> Option.bind (fun td ->
                    let strong = td.SelectSingleNode(".//div/strong")

                    if strong <> null then
                      Some(strong.InnerText.Trim())
                    else
                      None)
                  |> Option.defaultValue "0"

                Some
                  { PageUrl = pageUrl
                    Title = title
                    Year = "" // Could parse from cells if needed
                    Fps = fps
                    Uploader = uploader
                    Downloads = downloads }
            with _ ->
              None)
        |> Seq.toList
    with _ ->
      []

  let private tryParseInt (s: string) =
    match Int32.TryParse(s.Trim()) with
    | true, v -> Some v
    | _ -> None

  let private tryParseFloat (s: string) =
    match
      System.Double.TryParse(
        s.Trim(),
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture
      )
    with
    | true, v -> Some v
    | _ -> None

  /// Convert search results to internal subtitle info format
  /// Stores page URL in FormPage strategy for later extraction of form params
  let resultsToSubtitleInfo (results: YavkaSearchResult list) : InternalSubtitleInfo list =
    results
    |> List.map (fun r ->
      { Id = $"yavka|{r.PageUrl}"
        Title = r.Title
        ProviderName = "Yavka.net"
        Format = None
        Author =
          if String.IsNullOrWhiteSpace r.Uploader then
            None
          else
            Some r.Uploader
        DownloadCount = tryParseInt r.Downloads
        FrameRate = tryParseFloat r.Fps
        Rating = None
        DownloadStrategy = FormPage(r.PageUrl, "https://yavka.net/")
        UploadDate = None
        InfoPageUrl = Some r.PageUrl })

/// Yavka.net subtitle provider implementation
/// Handles 3-step flow: POST search → GET page → extract form → POST download
type YavkaNet() =
  static let referer = "https://yavka.net/"

  interface IProvider with
    member _.Name = "Yavka.net"
    member _.Referer = referer

    member _.SearchUrl (_query: string) (_year: int option) : string = "https://yavka.net/search"

    member _.CreateSearchRequest (url: string) (searchTerm: string) : HttpRequestMessage =
      let req = new HttpRequestMessage(HttpMethod.Post, url)
      req.Headers.Add("Referer", "https://yavka.net/subtitles/")
      req.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8")
      req.Headers.Add("Accept-Language", "bg,en-US;q=0.7,en;q=0.3")

      req.Content <-
        new FormUrlEncodedContent(
          [ new KeyValuePair<string, string>("s", searchTerm)
            new KeyValuePair<string, string>("y", "")
            new KeyValuePair<string, string>("c", "")
            new KeyValuePair<string, string>("u", "")
            new KeyValuePair<string, string>("l", "BG")
            new KeyValuePair<string, string>("g", "")
            new KeyValuePair<string, string>("i", "")
            new KeyValuePair<string, string>("search", "\uf002 Търсене") ]
        )

      req

    member _.CreateDownloadStrategy(url: string) : DownloadStrategy = FormPage(url, referer)

    member _.ParseResults(html: string) : InternalSubtitleInfo seq =
      YavkaNetImpl.parseSearchResults html
      |> YavkaNetImpl.resultsToSubtitleInfo
      |> Seq.ofList
