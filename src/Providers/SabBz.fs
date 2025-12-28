namespace Jellyfin.Plugin.BulgarianSubs.Providers

open System
open System.Text.RegularExpressions
open HtmlAgilityPack
open Jellyfin.Plugin.BulgarianSubs

module SabBzImpl =

  let private clean (s: string) =
    if String.IsNullOrWhiteSpace s then "" else s.Trim()

  let private downloadUrl (href: string) =
    if href.StartsWith("http://") || href.StartsWith("https://") then
      href
    else
      "http://subs.sab.bz/" + href

  let private tryParseInt (s: string) =
    match Int32.TryParse(clean s) with
    | true, v -> Some v
    | _ -> None

  let private extractFormat (tooltip: string) =
    let m = Regex.Match(tooltip, @"<b>Формат</b>:\s*(\w+)", RegexOptions.IgnoreCase)

    if m.Success then
      Some(m.Groups.[1].Value.ToLowerInvariant())
    else
      None

  /// Parse search results from Sab.Bz HTML response
  /// Row structure: td[1-3]=icons, td[4]=title+link, td[5]=date, td[6]=lang, td[7]=CDs, td[8]=FPS, td[9]=uploader, td[10]=imdb, td[11]=downloads
  let parseSearchResults (html: string) : InternalSubtitleInfo seq =
    try
      let doc = HtmlDocument()
      doc.LoadHtml(html)

      match doc.DocumentNode.SelectNodes("//tr[@class='subs-row']") with
      | null -> Seq.empty
      | rows ->
        rows
        |> Seq.choose (fun row ->
          let cells = row.SelectNodes(".//td")
          let linkNode = row.SelectSingleNode(".//a[contains(@href, 'act=download')]")

          if linkNode = null || cells = null || cells.Count < 11 then
            None
          else
            let href = linkNode.GetAttributeValue("href", "")
            let matchId = Regex.Match(href, @"attach_id=(\d+)")

            if not matchId.Success then
              None
            else
              let idValue = matchId.Groups.[1].Value
              let titleText = clean linkNode.InnerText
              let tooltip = linkNode.GetAttributeValue("onMouseover", "")

              let uploadDate = Parsing.tryParseBulgarianDate (clean cells.[4].InnerText)
              let format = extractFormat tooltip
              let uploaderNode = cells.[8].SelectSingleNode(".//a")

              let author =
                if uploaderNode <> null then
                  Some(clean uploaderNode.InnerText)
                else
                  None

              let downloads = tryParseInt cells.[10].InnerText

              Some
                { Id = idValue
                  Title = titleText
                  ProviderName = "Subs.Sab.Bz"
                  Format = format
                  Author = author
                  DownloadCount = downloads
                  DownloadStrategy = DirectUrl(downloadUrl href, "http://subs.sab.bz/")
                  UploadDate = uploadDate })
    with _ ->
      Seq.empty

/// Subs.Sab.Bz subtitle provider
type SabBz() =
  interface IProvider with
    member _.Name = "Subs.Sab.Bz"

    member _.SearchUrl (query: string) (year: int option) : string =
      let yearParam = year |> Option.map (fun y -> $"&yr={y}") |> Option.defaultValue ""
      $"http://subs.sab.bz/index.php?act=search&movie={query}{yearParam}"

    member _.ParseResults(html: string) : InternalSubtitleInfo seq = SabBzImpl.parseSearchResults html
