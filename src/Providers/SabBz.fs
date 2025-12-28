namespace Jellyfin.Plugin.BulgarianSubs.Providers

open System
open System.Text.RegularExpressions
open HtmlAgilityPack
open Jellyfin.Plugin.BulgarianSubs

module SabBzImpl =

  // Helper to clean strings
  let private clean (s: string) =
    if String.IsNullOrWhiteSpace s then "" else s.Trim()

  // Convert relative URLs to absolute
  let private downloadUrl (href: string) =
    if href.StartsWith("http://") || href.StartsWith("https://") then
      href
    else
      "http://subs.sab.bz/" + href

  /// Parse search results from Sab.Bz HTML response
  let parseSearchResults (html: string) : InternalSubtitleInfo seq =
    try
      let doc = HtmlDocument()
      doc.LoadHtml(html)

      match doc.DocumentNode.SelectNodes("//tr") with
      | null -> Seq.empty
      | rows ->
        rows
        |> Seq.choose (fun row ->
          let linkNode = row.SelectSingleNode(".//a[contains(@href, 'act=download')]")
          let titleNode = row.SelectSingleNode(".//td[4]")
          let dateNode = row.SelectSingleNode(".//td[5]")

          if linkNode = null || titleNode = null then
            None
          else
            let href = linkNode.GetAttributeValue("href", "")
            let matchId = Regex.Match(href, "attach_id=(\d+)")

            if not matchId.Success then
              None
            else
              let idValue = matchId.Groups.[1].Value
              let uploadDate =
                if dateNode <> null then
                  Parsing.tryParseBulgarianDate (clean dateNode.InnerText)
                else
                  None

              Some
                { Id = idValue
                  Title = clean titleNode.InnerText
                  ProviderName = "Subs.Sab.Bz"
                  Format = None
                  Author = None
                  DownloadStrategy = DirectUrl (downloadUrl href, "http://subs.sab.bz/")
                  UploadDate = uploadDate })
    with _ ->
      Seq.empty

/// Subs.Sab.Bz subtitle provider
type SabBz () =
  interface IProvider with
    member _.Name = "Subs.Sab.Bz"
    
    member _.SearchUrl (query: string) (year: int option) : string =
      let yearParam = year |> Option.map (fun y -> $"&yr={y}") |> Option.defaultValue ""
      $"http://subs.sab.bz/index.php?act=search&movie={query}{yearParam}"
    
    member _.ParseResults (html: string) : InternalSubtitleInfo seq =
      SabBzImpl.parseSearchResults html
