namespace Jellyfin.Plugin.BulgarianSubs.Providers

open System
open System.Text.RegularExpressions
open HtmlAgilityPack
open Jellyfin.Plugin.BulgarianSubs

module SubsunuacsImpl =

  // Helper to clean strings
  let private clean (s: string) =
    if String.IsNullOrWhiteSpace s then "" else s.Trim()

  /// Parse search results from Subsunacs HTML response
  let parseSearchResults (html: string) : InternalSubtitleInfo seq =
    try
      let doc = HtmlDocument()
      doc.LoadHtml(html)

      match doc.DocumentNode.SelectNodes("//tbody/tr") with
      | null -> Seq.empty
      | rows ->
        rows
        |> Seq.choose (fun row ->
          let titleNode = row.SelectSingleNode(".//td[@class='tdMovie']//a[1]")
          let linkNode = row.SelectSingleNode(".//a[contains(@href, '/subtitles/')]")

          if titleNode = null || linkNode = null then
            None
          else
            let href = linkNode.GetAttributeValue("href", "")
            let idMatch = Regex.Match(href, "-(\d+)/?")

            if not idMatch.Success then
              None
            else
              let idValue = idMatch.Groups.[1].Value
              let tooltipStr = linkNode.GetAttributeValue("title", "")
              let uploadDate =
                Regex.Match(tooltipStr, "Дата: &lt;/b&gt;([^&<]+)")
                |> fun m ->
                  if m.Success then
                    Parsing.tryParseBulgarianDate (clean m.Groups.[1].Value)
                  else
                    None

              let downloadUrl = "https://subsunacs.net/getentry.php?id=" + idValue + "&ei=0"

              Some
                { Id = idValue
                  Title = titleNode.InnerText.Trim() |> clean
                  ProviderName = "Subsunacs"
                  Format = None
                  Author = None
                  DownloadStrategy = DirectUrl (downloadUrl, "https://subsunacs.net/")
                  UploadDate = uploadDate }
        )
    with _ ->
      Seq.empty

/// Subsunacs.net subtitle provider
type Subsunacs () =
  interface IProvider with
    member _.Name = "Subsunacs"
    
    member _.SearchUrl (query: string) (year: int option) : string =
      let yearParam = year |> Option.map (fun y -> $"&y={y}") |> Option.defaultValue ""
      $"https://subsunacs.net/search.php?m={query}{yearParam}&t=Submit"
    
    member _.ParseResults (html: string) : InternalSubtitleInfo seq =
      SubsunuacsImpl.parseSearchResults html
