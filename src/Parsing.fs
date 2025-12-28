namespace Jellyfin.Plugin.BulgarianSubs

open HtmlAgilityPack
open System
open System.IO
open System.Text.RegularExpressions
open ComputationExpressions

module Parsing =

  // Helper to clean strings
  let private clean (s: string) =
    if String.IsNullOrWhiteSpace s then "" else s.Trim()

  // Bulgarian month names
  let private bulgarianMonths =
    [| ""
       "Jan"
       "Feb"
       "Mar"
       "Apr"
       "May"
       "Jun"
       "Jul"
       "Aug"
       "Sep"
       "Oct"
       "Nov"
       "Dec" |]

  // Parse Bulgarian date strings (e.g., "14 Nov 2010") to DateTime in Bulgarian timezone (UTC+2)
  let tryParseBulgarianDate (dateStr: string) : DateTime option =
    if String.IsNullOrWhiteSpace dateStr then
      None
    else
      try
        dateStr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
        |> fun parts ->
          if parts.Length < 3 then
            None
          else
            option {
              let! day = Int32.TryParse parts.[0] |> function true, v -> Some v | _ -> None
              let! year = Int32.TryParse parts.[2] |> function true, v -> Some v | _ -> None
              let! idx =
                bulgarianMonths
                |> Array.tryFindIndex (fun m -> m.Equals(parts.[1], StringComparison.OrdinalIgnoreCase))
              if idx > 0 then return (day, idx, year) else return! None
            }
            |> Option.map (fun (day, month, year) ->
              DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc).AddHours(2.0))
      with _ ->
        None

  // Helper to load HTML from byte array with proper encoding detection
  let private loadHtmlDocument (html: string) : HtmlDocument =
    let doc = HtmlDocument()
    doc.LoadHtml(html)
    doc

  let loadHtmlDocumentFromBytes (bytes: byte[]) : HtmlDocument =
    let doc = HtmlDocument()

    // HtmlAgilityPack can detect encoding from HTML meta tags
    // Load directly with autodetect enabled
    use ms = new MemoryStream(bytes)
    doc.Load(ms)
    doc

  // --- Subs.Sab.Bz Parsing Logic ---
  let private sabBzDownloadUrl (href: string) =
    if href.StartsWith("http://") || href.StartsWith("https://") then
      href
    else
      "http://subs.sab.bz/" + href

  let parseSabBz (html: string) =
    loadHtmlDocument html
    |> fun doc ->
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
                if dateNode <> null then tryParseBulgarianDate (clean dateNode.InnerText) else None

              Some
                { Id = idValue
                  Title = clean titleNode.InnerText
                  ProviderName = "Subs.Sab.Bz"
                  Format = None
                  Author = None
                  DownloadStrategy = DirectUrl (sabBzDownloadUrl href, "http://subs.sab.bz/")
                  UploadDate = uploadDate })

  // --- Subsunacs.net Parsing Logic ---
  let parseSubsunacs (html: string) =
    let doc = new HtmlDocument()
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
              |> fun m -> if m.Success then tryParseBulgarianDate (clean m.Groups.[1].Value) else None

            let downloadUrl = "https://subsunacs.net/getentry.php?id=" + idValue + "&ei=0"

            Some
              { Id = idValue
                Title = titleNode.InnerText.Trim() |> clean
                ProviderName = "Subsunacs"
                Format = None
                Author = None
                DownloadStrategy = DirectUrl (downloadUrl, "https://subsunacs.net/")
                UploadDate = uploadDate })
