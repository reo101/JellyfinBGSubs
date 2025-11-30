namespace Jellyfin.Plugin.BulgarianSubs

open System
open System.IO
open System.Net.Http
open System.Threading
open MediaBrowser.Controller.Subtitles
open MediaBrowser.Controller.Providers
open MediaBrowser.Model.Providers
open Microsoft.Extensions.Logging
open SharpCompress.Archives

// The main provider class
type BulgarianSubtitleProvider(logger: ILogger<BulgarianSubtitleProvider>, httpClientFactory: IHttpClientFactory) =

  let httpClient = httpClientFactory.CreateClient("BulgarianSubs")

  // Helper to check if language is supported (Bulgarian)
  let isSupportedLanguage (lang: string) =
    let normalized = if isNull lang then "" else lang.ToLowerInvariant()
    normalized = "bg" || normalized = "bul" || normalized = "bulgarian"

  // Helper to unzip/unrar and find the best subtitle file
  let extractSubtitleStream (archiveStream: Stream) (_fileExtension: string) : Stream * string =
    try
      use archive = ArchiveFactory.Open(archiveStream)
      // Find first entry that ends with.srt or.sub
      let entry =
        archive.Entries
        |> Seq.filter (fun e -> not e.IsDirectory)
        |> Seq.tryFind (fun e ->
          let name = e.Key.ToLowerInvariant()
          name.EndsWith(".srt") || name.EndsWith(".sub"))

      match entry with
      | Some e ->
        let ms = new MemoryStream()
        e.WriteTo(ms)
        ms.Position <- 0L
        // BUG: `Path.GetExtension(e.Key).TrimStart('.')` causes fsautocomplete type inference infinite loop
        // Split method chain into intermediate assignments to workaround
        let extName = Path.GetExtension(e.Key)
        let ext = extName.TrimStart('.')
        (ms :> Stream, ext)
      | None ->
        // If no subtitle found in archive, return empty or throw
        (Stream.Null, "srt")
    with ex ->
      logger.LogError(ex, "Failed to extract archive")
      (Stream.Null, "srt")

  interface ISubtitleProvider with
    member _.Name = "Bulgarian Subtitles (Sab.bz & Unacs)"

    member _.SupportedMediaTypes =
      seq {
        yield VideoContentType.Movie
        yield VideoContentType.Episode
      }

    member _.Search(request: SubtitleSearchRequest, cancellationToken: CancellationToken) =
      task {
        if not (isSupportedLanguage request.Language) then
          return Seq.empty
        else
          let results = System.Collections.Generic.List<RemoteSubtitleInfo>()

          // 1. Search Subs.Sab.Bz
          try
            // Construct Search URL (based on research pattern)
            let searchTerm = System.Net.WebUtility.UrlEncode(request.Name)

            let yearParam =
              if request.ProductionYear.HasValue then
                $"&yr={request.ProductionYear}"
              else
                ""

            let url = $"http://subs.sab.bz/index.php?act=search&movie={searchTerm}{yearParam}"

            let! html = httpClient.GetStringAsync(url, cancellationToken)
            let parsed = Parsing.parseSabBz html

            for item in parsed do
              let info = RemoteSubtitleInfo()
              info.Id <- $"sab|{item.DownloadUrl}" // Encode provider in ID for Download step
              info.Name <- item.Title
              info.ProviderName <- item.ProviderName
              info.ThreeLetterISOLanguageName <- "bul"
              results.Add(info)
          with ex ->
            logger.LogError(ex, "Error searching Subs.Sab.Bz")

          // 2. Search Subsunacs
          try
            let searchTerm = System.Net.WebUtility.UrlEncode(request.Name)

            let yearParam =
              if request.ProductionYear.HasValue then
                $"&y={request.ProductionYear}"
              else
                ""

            let url = $"https://subsunacs.net/search.php?m={searchTerm}{yearParam}&t=Submit"

            let! html = httpClient.GetStringAsync(url, cancellationToken)
            let parsed = Parsing.parseSubsunacs html

            for item in parsed do
              let info = RemoteSubtitleInfo()
              info.Id <- $"unacs|{item.DownloadUrl}"
              info.Name <- item.Title
              info.ProviderName <- item.ProviderName
              info.ThreeLetterISOLanguageName <- "bul"
              results.Add(info)
          with ex ->
            logger.LogError(ex, "Error searching Subsunacs")

          return results :> seq<RemoteSubtitleInfo>
      }

    member _.GetSubtitles(id: string, cancellationToken: CancellationToken) =
      task {
        let parts = id.Split('|')

        if parts.Length < 2 then
          return SubtitleResponse()
        else
          let provider = parts.[0]
          let url = parts.[1]

          let request = new HttpRequestMessage(HttpMethod.Get, url)

          // CRITICAL: Subs.Sab.Bz requires Referer header or download fails
          if provider = "sab" then
            request.Headers.Referrer <- Uri("http://subs.sab.bz/")

          // Standard User-Agent to avoid basic bot blocking
          request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36")

          let! response = httpClient.SendAsync(request, cancellationToken)
          response.EnsureSuccessStatusCode() |> ignore

          let! stream = response.Content.ReadAsStreamAsync(cancellationToken)

          // Most Bulgarian subs are zipped/rar'd. We must extract.
          // We check magic headers or just try extract.
          let (subStream, ext) = extractSubtitleStream stream "srt"

          let result = SubtitleResponse()
          result.Format <- ext
          result.Stream <- subStream
          result.Language <- "bul"
          return result
      }
