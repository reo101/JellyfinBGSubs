namespace Jellyfin.Plugin.BulgarianSubs

open System
open System.IO
open System.Net.Http
open System.Text
open System.Threading
open MediaBrowser.Controller.Subtitles
open MediaBrowser.Controller.Providers
open MediaBrowser.Model.Providers
open Microsoft.Extensions.Logging
open SharpCompress.Archives
open ComputationExpressions

// ============================================================================
// Archive Detection Utilities
// ============================================================================

module ArchiveDetection =
  /// Archive format detected from magic bytes
  type ArchiveFormat =
    | ZIP
    | RAR
    | SevenZ
    | Gzip

  /// Active pattern to match array prefixes
  let (|Prefix|_|) (prefixBytes: byte[]) (arr: byte[]) =
    if arr.Length >= prefixBytes.Length &&
       Array.forall2 (=) prefixBytes (arr |> Array.take prefixBytes.Length) then
      Some ()
    else
      None

  /// Detects archive format by checking magic number bytes
  /// Returns Some(format) if the stream starts with a known archive magic number,
  /// or None if it's not a recognized archive format
  let detectArchiveFormat (stream: Stream) : ArchiveFormat option =
    let magic = Array.zeroCreate 4
    let bytesRead = stream.Read(magic, 0, 4)
    stream.Position <- 0L

    if bytesRead < 2 then
      None
    else
      match magic with
      // ZIP: 50 4B (PK..) - bytes 3-4 vary by ZIP type
      | Prefix [| 0x50uy; 0x4Buy |] -> Some ZIP
      // Gzip: 1F 8B (only 2 bytes needed)
      | Prefix [| 0x1Fuy; 0x8Buy |] -> Some Gzip
      // RAR: 52 61 72 (Rar) - full signature is 52 61 72 21 60 90
      | Prefix [| 0x52uy; 0x61uy; 0x72uy |] -> Some RAR
      // 7z: 37 7A BC AF
      | Prefix [| 0x37uy; 0x7Auy; 0xBCuy; 0xAFuy |] -> Some SevenZ
      | _ -> None

  /// Checks if a filename is a subtitle file (.srt or .sub)
  let isSubtitleFile (fileName: string) : bool =
    let name = fileName.ToLowerInvariant()
    name.EndsWith ".srt" || name.EndsWith ".sub"

// The main provider class
type BulgarianSubtitleProvider(logger: ILogger<BulgarianSubtitleProvider>, httpClientFactory: IHttpClientFactory) =

  // Register code page encodings on first instantiation
  static do Encoding.RegisterProvider CodePagesEncodingProvider.Instance

  let httpClient = httpClientFactory.CreateClient "BulgarianSubs"

  // Initialize debug logger
  let debugLogger = DebugLogger.fileLogger "/tmp/bulgariansubs.log"
  // let debugLogger = DebugLogger.consoleLogger
  // let debugLogger = DebugLogger.noOpLogger

  // Helper to check if language is supported (Bulgarian)
  let isSupportedLanguage (lang: string) =
    match if isNull lang then "" else lang.ToLowerInvariant() with
    | "bg" | "bul" | "bulgarian" -> true
    | _ -> false

  // Helper to check if a subtitle title matches the requested season and episode
  // Matches formats like: "03x02", "3x2", "S03E02", "S3E2", etc.
  let matchesEpisode (season: int) (episode: int) (title: string) : bool =
    let patterns =
      [
        // Format: XXxYY (e.g., "03x02")
        sprintf "%02dx%02d" season episode
        sprintf "%dx%d" season episode
        // Format: SxxEyy (e.g., "S03E02")
        sprintf "s%02de%02d" season episode
        sprintf "s%de%d" season episode
      ]
    
    let titleLower = title.ToLowerInvariant()
    patterns |> List.exists (fun pattern -> titleLower.Contains pattern)

  // Helper to read HTTP response as bytes (for proper encoding handling)
  let readResponseAsBytes (response: HttpResponseMessage) (cancellationToken: CancellationToken) =
    task {
      try
        return! response.Content.ReadAsByteArrayAsync cancellationToken
      with ex ->
        logger.LogWarning(ex, "Error reading response body, returning empty array")
        return [||]
    }

  // Helper to build year parameter if present
  let yearParam (hasYear: bool) (year: int) paramName =
    if hasYear then sprintf "&%s=%d" paramName year else ""

  // Helper to unzip/unrar and find the best subtitle file, or return uncompressed if needed
  let extractSubtitleStream (responseStream: Stream) (_fileExtension: string) : Stream * string =
    // Check if it's a plain text file (uncompressed subtitle) or an archive
    let archiveFormat = ArchiveDetection.detectArchiveFormat responseStream

    match archiveFormat with
    | Some _ ->
      try
        use archive = ArchiveFactory.Open(responseStream)
        // Find first entry that ends with .srt or .sub
        let entry =
          archive.Entries
          |> Seq.filter (fun e -> not e.IsDirectory)
          |> Seq.tryFind (fun e -> ArchiveDetection.isSubtitleFile e.Key)

        match entry with
        | Some e ->
          let ms = new MemoryStream()
          e.WriteTo ms
          ms.Position <- 0L
          // HACK: fsautocomplete type inference bug - see FSAUTOCOMPLETE_BUGS.md
          //       `Path.GetExtension(e.Key).TrimStart('.')` causes infinite loop
          let ext = Path.GetExtension e.Key |> fun s -> s.TrimStart '.'
          ms :> Stream, ext
        | None ->
          // If no subtitle found in archive, return empty
          Stream.Null, "srt"
      with ex ->
        logger.LogError(ex, "Failed to extract archive")
        Stream.Null, "srt"
    | None ->
      // Not an archive, return as-is (plain SRT/SUB file)
      // Use detected extension or fallback to "srt"
      responseStream,
      if String.IsNullOrEmpty(_fileExtension) then
        "srt"
      else
        _fileExtension

  interface ISubtitleProvider with
    member _.Name = "Bulgarian Subtitles (Sab.bz & Unacs)"

    member _.SupportedMediaTypes =
      seq {
        yield VideoContentType.Movie
        yield VideoContentType.Episode
      }

    member _.Search(request: SubtitleSearchRequest, cancellationToken: CancellationToken) =
       task {
         // Build search name - use series name for TV shows, just name for movies
         let searchName =
           if request.ParentIndexNumber.HasValue || request.IndexNumber.HasValue then
             // TV show: search by series name only (will filter episodes client-side)
             if String.IsNullOrEmpty(request.SeriesName) then request.Name else request.SeriesName
           else
             // Movie: just use the name
             request.Name

         // Extract episode info for client-side filtering
         let season = if request.ParentIndexNumber.HasValue then Some request.ParentIndexNumber.Value else None
         let episode = if request.IndexNumber.HasValue then Some request.IndexNumber.Value else None

         debugLogger.Debug $"Search called with language: {request.Language}, name: {searchName}, season: {season}, episode: {episode}, contentType: {request.ContentType}"

         if not (isSupportedLanguage request.Language) then
           return Seq.empty
         else
           let results = System.Collections.Generic.List<RemoteSubtitleInfo>()
           let searchTerm = System.Net.WebUtility.UrlEncode searchName
           let hasYear = request.ProductionYear.HasValue
           let year = request.ProductionYear.GetValueOrDefault()

           // 1. Search Subs.Sab.Bz
           try
            let yearParam = yearParam hasYear year "yr"
            let url = $"http://subs.sab.bz/index.php?act=search&movie={searchTerm}{yearParam}"
            let! response = httpClient.GetAsync(url, cancellationToken)
            let! bytes = readResponseAsBytes response cancellationToken
            let encoding = System.Text.Encoding.GetEncoding("windows-1251")
            let html = encoding.GetString(bytes)

            Parsing.parseSabBz html
            |> Seq.filter (fun item ->
              // Filter by episode if episode search
              match (season, episode) with
              | (Some s, Some e) -> matchesEpisode s e item.Title
              | _ -> true)
            |> Seq.iter (fun item ->
              let info = RemoteSubtitleInfo()
              info.Id <- $"sab|{item.DownloadUrl}"
              info.Name <- item.Title
              info.ProviderName <- item.ProviderName
              info.ThreeLetterISOLanguageName <- "bul"
              results.Add(info))
           with ex ->
            logger.LogError(ex, "Error searching Subs.Sab.Bz")

           // 2. Search Subsunacs
           try
            let yearParam = yearParam hasYear year "y"
            let url = $"https://subsunacs.net/search.php?m={searchTerm}{yearParam}&t=Submit"
            let! response = httpClient.GetAsync(url, cancellationToken)
            let! bytes = readResponseAsBytes response cancellationToken
            let encoding = System.Text.Encoding.GetEncoding("windows-1251")
            let html = encoding.GetString(bytes)

            Parsing.parseSubsunacs html
            |> Seq.filter (fun item ->
              // Filter by episode if episode search
              match (season, episode) with
              | (Some s, Some e) -> matchesEpisode s e item.Title
              | _ -> true)
            |> Seq.iter (fun item ->
              let downloadUrl = $"https://subsunacs.net/getentry.php?id={item.Id}&ei=0"
              let info = RemoteSubtitleInfo()
              info.Id <- $"unacs|{downloadUrl}"
              info.Name <- item.Title
              info.ProviderName <- item.ProviderName
              info.ThreeLetterISOLanguageName <- "bul"
              results.Add(info))
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
          let provider, url = parts.[0], parts.[1]

          let request = new HttpRequestMessage(HttpMethod.Get, url)
          request.Headers.Referrer <-
            match provider with
            | "sab" -> Uri("http://subs.sab.bz/")
            | "unacs" -> Uri("https://subsunacs.net/")
            | _ -> Uri("http://localhost/")

          request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36")

          let! response = httpClient.SendAsync(request, cancellationToken)
          response.EnsureSuccessStatusCode() |> ignore

          let detectedExt =
            try
              option {
                let! cd = response.Content.Headers.ContentDisposition |> Option.ofObj
                let! fileName = cd.FileName |> Option.ofObj
                return Path.GetExtension fileName |> fun ext -> ext.TrimStart '.' |> fun e ->
                  if String.IsNullOrEmpty e then "srt" else e
              }
              |> Option.defaultValue "srt"
            with _ -> "srt"

          let! stream = response.Content.ReadAsStreamAsync(cancellationToken)
          let (subStream, ext) = extractSubtitleStream stream detectedExt

          let result = SubtitleResponse()
          result.Format <- ext
          result.Stream <- subStream
          result.Language <- "bul"
          return result
      }
