namespace Jellyfin.Plugin.BulgarianSubs

open System
open System.Net.Http
open System.Text
open System.Threading
open MediaBrowser.Controller.Subtitles
open MediaBrowser.Controller.Providers
open MediaBrowser.Controller.Library
open MediaBrowser.Controller.Entities.Movies
open MediaBrowser.Controller.Entities.TV
open MediaBrowser.Model.Providers
open Microsoft.Extensions.Logging
open Jellyfin.Plugin.BulgarianSubs.Providers
open ComputationExpressions
open System.Threading.Tasks

/// The main Jellyfin subtitle provider for Bulgarian subtitles
type BulgarianSubtitleProvider
  (logger: ILogger<BulgarianSubtitleProvider>, httpClientFactory: IHttpClientFactory, libraryManager: ILibraryManager) =

  // Register code page encodings on first instantiation
  static do Encoding.RegisterProvider CodePagesEncodingProvider.Instance

  do logger.LogInformation("BulgarianSubtitleProvider instance created.")

  let httpClient = httpClientFactory.CreateClient "BulgarianSubs"

  // Per-provider timeout (10 seconds per request)
  let requestTimeout = TimeSpan.FromSeconds(10.0)

  // User-Agent for requests
  let userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

  // List of all available providers
  let providers: IProvider list = [ SabBz(); Subsunacs(); YavkaNet(); Podnapisi() ]

  // Generate provider name from list
  let providerName =
    let names = providers |> List.map (fun p -> p.Name)
    let joined = String.Join(", ", names)
    $"Bulgarian Subtitles ({joined})"

  // Helper to get the original title from library item
  let getOriginalTitle (mediaPath: string) =
    try
      if String.IsNullOrEmpty(mediaPath) then
        None
      else
        let item = libraryManager.FindByPath(mediaPath, Nullable())

        match item with
        | :? Movie as movie ->
          if not (String.IsNullOrEmpty(movie.OriginalTitle)) then
            Some movie.OriginalTitle
          else
            None
        | :? Episode as episode ->
          if not (String.IsNullOrEmpty(episode.Series.OriginalTitle)) then
            Some episode.Series.OriginalTitle
          else
            None
        | _ -> None
    with ex ->
      logger.LogDebug($"Could not get original title: {ex.Message}")
      None

  // Helper to check if language is supported (Bulgarian)
  let isSupportedLanguage (lang: string) =
    match if isNull lang then "" else lang.ToLowerInvariant() with
    | "bg"
    | "bul"
    | "bulgarian" -> true
    | _ -> false

  // Helper to check if a subtitle title matches the requested season and episode
  let matchesEpisode (season: int) (episode: int) (title: string) : bool =
    let patterns =
      [
        // Format: XXxYY (e.g., "03x02")
        sprintf "%02dx%02d" season episode
        sprintf "%dx%d" season episode
        // Format: SxxEyy (e.g., "S03E02")
        sprintf "s%02de%02d" season episode
        sprintf "s%de%d" season episode ]

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

  interface ISubtitleProvider with
    member _.Name = providerName

    member _.SupportedMediaTypes =
      seq {
        yield VideoContentType.Movie
        yield VideoContentType.Episode
      }

    member _.Search(request: SubtitleSearchRequest, cancellationToken: CancellationToken) =
      task {
        logger.LogInformation($"Search called for {request.Name}, language={request.Language}")

        // Extract metadata from Jellyfin library
        let metadata = MetadataExtractor.extract request

        // Get original title from library (prioritized for non-English content)
        let originalTitle = getOriginalTitle request.MediaPath

        // Build search name - prioritize original title, then series name for TV, then name
        let searchName =
          match originalTitle with
          | Some orig -> orig
          | None ->
            if request.ParentIndexNumber.HasValue || request.IndexNumber.HasValue then
              if String.IsNullOrEmpty(request.SeriesName) then
                request.Name
              else
                request.SeriesName
            else
              request.Name

        let origTitleStr = originalTitle |> Option.defaultValue "(none)"
        logger.LogDebug($"Original title: {origTitleStr}, using searchName: {searchName}")

        // Extract episode info for client-side filtering
        let season =
          if request.ParentIndexNumber.HasValue then
            Some request.ParentIndexNumber.Value
          else
            None

        let episode =
          if request.IndexNumber.HasValue then
            Some request.IndexNumber.Value
          else
            None

        let metadataDesc = MetadataExtractor.describeMetadata metadata

        logger.LogDebug(
          $"Search: language={request.Language}, name={searchName}, season={season}, episode={episode}, metadata={metadataDesc}"
        )

        if not (isSupportedLanguage request.Language) then
          logger.LogDebug($"Language {request.Language} not supported, returning empty")
          return Seq.empty
        else
          logger.LogDebug($"Language supported, starting search for {searchName}")
          let fileTitleStr = metadata.FileBasedTitle |> Option.defaultValue "(none)"
          logger.LogDebug($"File-based title: {fileTitleStr}")
          let results = System.Collections.Generic.List<RemoteSubtitleInfo>()
          let searchTerm = System.Net.WebUtility.UrlEncode searchName

          let year =
            if request.ProductionYear.HasValue then
              Some request.ProductionYear.Value
            else
              None

          // Determine search variations based on available metadata
          // Always use buildEnhancedSearchTerms to prioritize file-based title
          let searchVariations =
            let baseTerms = MetadataExtractor.buildEnhancedSearchTerms metadata searchName
            let encoded = baseTerms |> List.map System.Net.WebUtility.UrlEncode
            // Take up to 3 variations (file title, file+year, display name)
            encoded |> List.truncate 3

          let variationsStr = searchVariations |> String.concat ", "
          logger.LogDebug($"Search variations: {variationsStr}")
          logger.LogDebug($"About to search {providers.Length} providers with {searchVariations.Length} variations")

          for provider in providers do
            try
              logger.LogDebug($"Searching provider {provider.Name}")

              for encodedSearchTerm in searchVariations do
                let url = provider.SearchUrl encodedSearchTerm year
                logger.LogDebug($"[{provider.Name}] URL: {url}")

                try
                  logger.LogDebug($"[{provider.Name}] Making HTTP request...")

                  // Create request with User-Agent and timeout
                  use cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                  cts.CancelAfter(requestTimeout)

                  let request = provider.CreateSearchRequest url encodedSearchTerm
                  request.Headers.Add("User-Agent", userAgent)

                  let! responseOpt =
                    try
                      task {
                        let! resp = httpClient.SendAsync(request, cts.Token)
                        return Some resp
                      }
                    with
                    | :? TaskCanceledException ->
                      logger.LogWarning($"[{provider.Name}] Request timed out after {requestTimeout.TotalSeconds}s")
                      Task.FromResult(None)
                    | :? OperationCanceledException ->
                      logger.LogWarning($"[{provider.Name}] Request cancelled")
                      Task.FromResult(None)
                    | httpEx ->
                      logger.LogDebug($"[{provider.Name}] HTTP Exception: {httpEx.Message}")
                      Task.FromResult(None)

                  match responseOpt with
                  | None -> ()
                  | Some response ->

                  let! bytes = readResponseAsBytes response cancellationToken
                  logger.LogDebug($"[{provider.Name}] Response: {response.StatusCode}, {bytes.Length} bytes")
                  let encoding = System.Text.Encoding.GetEncoding("windows-1251")
                  let html = encoding.GetString(bytes)

                  let parsedItems = provider.ParseResults html |> Seq.toList
                  logger.LogDebug($"[{provider.Name}] Parsed {parsedItems.Length} raw results")

                  parsedItems
                  |> Seq.filter (fun item ->
                    match (season, episode) with
                    | (Some s, Some e) -> matchesEpisode s e item.Title
                    | _ -> true)
                  |> Seq.iter (fun item ->
                    let info = RemoteSubtitleInfo()

                    match item.DownloadStrategy with
                    | DirectUrl(url, _) -> info.Id <- $"{provider.Name}|{url}"
                    | FormPage(pageUrl, _) -> info.Id <- $"{provider.Name}|{pageUrl}"

                    info.Name <- item.Title
                    info.ProviderName <- "Bulgarian Subtitles"
                    info.ThreeLetterISOLanguageName <- "bul"
                    info.Format <- item.Format |> Option.defaultValue ""
                    info.Author <- item.Author |> Option.defaultValue ""
                    info.DownloadCount <- item.DownloadCount |> Option.defaultValue 0
                    info.DateCreated <- item.UploadDate |> Option.toNullable
                    info.Comment <-
                      match item.InfoPageUrl with
                      | Some url -> $"[{item.ProviderName}] {url}"
                      | None -> $"[{item.ProviderName}]"
                    results.Add(info))

                with ex ->
                  logger.LogError(ex, $"Error searching {provider.Name}")
            with ex ->
              logger.LogError(ex, $"Error searching {provider.Name}")

          // Sort by download count descending
          let sortedResults =
            results
            |> Seq.sortByDescending (fun r ->
              if r.DownloadCount.HasValue then r.DownloadCount.Value else 0)
            |> Seq.toList

          logger.LogInformation($"Search complete, found {sortedResults.Length} subtitles")
          return sortedResults :> seq<RemoteSubtitleInfo>
      }

    member _.GetSubtitles(id: string, cancellationToken: CancellationToken) =
      task {
        let parts = id.Split('|')

        if parts.Length < 2 then
          return SubtitleResponse()
        else
          let providerName, url = parts.[0], parts.[1]

          let providerOpt = providers |> List.tryFind (fun p -> p.Name = providerName)
          let strategy =
            match providerOpt with
            | Some provider -> provider.CreateDownloadStrategy url
            | None -> DirectUrl(url, "http://localhost/")

          let! (subStream, ext) =
            Common.executeStrategy strategy httpClient cancellationToken Common.extractSubtitleStream

          let result = SubtitleResponse()
          result.Format <- ext
          result.Stream <- subStream
          result.Language <- "bul"
          return result
      }
