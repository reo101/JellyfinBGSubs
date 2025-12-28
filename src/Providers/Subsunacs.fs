namespace Jellyfin.Plugin.BulgarianSubs.Providers

open System
open System.Net.Http
open System.Text.RegularExpressions
open HtmlAgilityPack
open Jellyfin.Plugin.BulgarianSubs

module SubsunuacsImpl =

  let private clean (s: string) =
    if String.IsNullOrWhiteSpace s then "" else s.Trim()

  let private tryParseInt (s: string) =
    match Int32.TryParse(clean s) with
    | true, v -> Some v
    | _ -> None

  let private tryParseFloat (s: string) =
    match System.Double.TryParse(clean s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

  let private extractFormat (tooltip: string) =
    let m = Regex.Match(tooltip, @"Формат:\s*&lt;/b&gt;(\w+)", RegexOptions.IgnoreCase)
    if m.Success then Some(m.Groups.[1].Value.ToLowerInvariant()) else None

  /// Parse search results from Subsunacs HTML response
  /// Row: td[0]=title(tdMovie), td[1]=CDs, td[2]=FPS, td[3]=rating, td[4]=comments, td[5]=uploader, td[6]=downloads
  let parseSearchResults (html: string) : InternalSubtitleInfo seq =
    try
      let doc = HtmlDocument()
      doc.LoadHtml(html)

      match doc.DocumentNode.SelectNodes("//tbody/tr") with
      | null -> Seq.empty
      | rows ->
        rows
        |> Seq.choose (fun row ->
          let cells = row.SelectNodes(".//td")
          let titleNode = row.SelectSingleNode(".//td[@class='tdMovie']//a[1]")

          if titleNode = null || cells = null || cells.Count < 7 then
            None
          else
            let href = titleNode.GetAttributeValue("href", "")
            let idMatch = Regex.Match(href, @"-(\d+)/?")

            if not idMatch.Success then
              None
            else
              let idValue = idMatch.Groups.[1].Value
              let tooltipStr = titleNode.GetAttributeValue("title", "")

              let uploadDate =
                Regex.Match(tooltipStr, @"Дата:\s*&lt;/b&gt;([^&<]+)")
                |> fun m ->
                  if m.Success then
                    Parsing.tryParseBulgarianDate (clean m.Groups.[1].Value)
                  else
                    None

              let format = extractFormat tooltipStr
              let fps = tryParseFloat cells.[2].InnerText
              let rating = tryParseFloat cells.[3].InnerText
              let uploaderNode = cells.[5].SelectSingleNode(".//a")
              let author = if uploaderNode <> null then Some(clean uploaderNode.InnerText) else None
              let downloads = tryParseInt cells.[6].InnerText

              let downloadUrl = "https://subsunacs.net/getentry.php?id=" + idValue + "&ei=0"
              let infoPageUrl = href

              Some
                { Id = idValue
                  Title = titleNode.InnerText.Trim() |> clean
                  ProviderName = "Subsunacs"
                  Format = format
                  Author = author
                  DownloadCount = downloads
                  FrameRate = fps
                  Rating = rating
                  DownloadStrategy = DirectUrl(downloadUrl, "https://subsunacs.net/")
                  UploadDate = uploadDate
                  InfoPageUrl = Some $"https://subsunacs.net{infoPageUrl}" })
    with _ ->
      Seq.empty

/// Subsunacs.net subtitle provider
type Subsunacs() =
  static let referer = "https://subsunacs.net/"

  interface IProvider with
    member _.Name = "Subsunacs"
    member _.Referer = referer

    member _.SearchUrl (query: string) (year: int option) : string =
      let yearParam = year |> Option.map (fun y -> $"&y={y}") |> Option.defaultValue ""
      $"https://subsunacs.net/search.php?m={query}{yearParam}&t=Submit"

    member _.CreateSearchRequest (url: string) (_searchTerm: string) : HttpRequestMessage =
      new HttpRequestMessage(HttpMethod.Get, url)

    member _.CreateDownloadStrategy (url: string) : DownloadStrategy =
      DirectUrl(url, referer)

    member _.ParseResults(html: string) : InternalSubtitleInfo seq = SubsunuacsImpl.parseSearchResults html
