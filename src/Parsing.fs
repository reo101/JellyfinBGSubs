namespace Jellyfin.Plugin.BulgarianSubs

open HtmlAgilityPack
open System
open System.Text.RegularExpressions

module Parsing =

  // Helper to clean strings
  let private clean (s: string) =
    if String.IsNullOrWhiteSpace(s) then "" else s.Trim()

  // --- Subs.Sab.Bz Parsing Logic ---
  let parseSabBz (html: string) =
    let doc = HtmlDocument()
    doc.LoadHtml(html)

    // Select rows from the results table.
    // Note: CSS classes might change, this is based on the research of the legacy plugin structure.
    let rows = doc.DocumentNode.SelectNodes("//tr")

    match rows with
    | null -> Seq.empty
    | _ ->
      rows
      |> Seq.map (fun row ->
        // Logic to extract title, ID, and link from table cells (td)
        // This is an approximation based on standard forum structures
        let linkNode = row.SelectSingleNode(".//a[contains(@href, 'act=download')]")
        let titleNode = row.SelectSingleNode(".//td[1]") // Assuming 2nd column is title

        match linkNode, titleNode with
        | null, _
        | _, null -> None
        | l, t ->
          let href = l.GetAttributeValue("href", "")
          // Extract ID from href (e.g., attach_id=12345)
          let matchId = Regex.Match(href, "attach_id=(\d+)")

          if matchId.Success then
            // BUG: `matchId.Groups.[1].Value` causes fsautocomplete type inference infinite loop
            // Split method chain into intermediate assignments to workaround
            let matchGroup = matchId.Groups.[1]
            let idValue = matchGroup.Value

            Some
              { Id = idValue
                Title = clean t.InnerText
                ProviderName = "Subs.Sab.Bz"
                Format = None
                Author = None
                DownloadUrl = "http://subs.sab.bz/" + href }
          else
            None)
      |> Seq.choose id

  // --- Subsunacs.net Parsing Logic ---
  let parseSubsunacs (html: string) =
    let doc = HtmlDocument()
    doc.LoadHtml(html)

    // Similar scraping logic for Subsunacs
    let rows = doc.DocumentNode.SelectNodes("//tr[@class='subs-row']") // Referenced in research

    match rows with
    | null -> Seq.empty
    | _ ->
      rows
      |> Seq.map (fun row ->
        // BUG: `row.SelectSingleNode(".//td[@class='title']").InnerText` causes fsautocomplete type inference infinite loop
        // Split method chain into intermediate assignments to workaround
        let titleNode = row.SelectSingleNode(".//td[@class='title']")
        let title = if titleNode <> null then titleNode.InnerText else ""
        let linkNode = row.SelectSingleNode(".//a[contains(@href, 'download.php')]")

        match linkNode with
        | null -> None
        | l ->
          let href = l.GetAttributeValue("href", "")
          // Usually download.php?id=XXXX
          let idMatch = Regex.Match(href, "id=(\d+)")

          if idMatch.Success then
            // BUG: `idMatch.Groups.[1].Value` causes fsautocomplete type inference infinite loop (same as above)
            // Split method chain into intermediate assignments to workaround
            let idGroup = idMatch.Groups.[1]
            let idValue = idGroup.Value

            Some
              { Id = idValue
                Title = clean title
                ProviderName = "Subsunacs"
                Format = None
                Author = None
                DownloadUrl = "https://subsunacs.net/" + href }
          else
            None)
      |> Seq.choose id
