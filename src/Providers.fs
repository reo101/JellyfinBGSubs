namespace Jellyfin.Plugin.BulgarianSubs

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Threading
open MediaBrowser.Controller.Subtitles
open MediaBrowser.Controller.Providers
open MediaBrowser.Model.Providers
open Microsoft.Extensions.Logging
open Jellyfin.Plugin.BulgarianSubs.Providers
open ComputationExpressions

/// The main Jellyfin subtitle provider for Bulgarian subtitles
type BulgarianSubtitleProvider(logger: ILogger<BulgarianSubtitleProvider>, httpClientFactory: IHttpClientFactory) =

  // Register code page encodings on first instantiation
  static do Encoding.RegisterProvider CodePagesEncodingProvider.Instance

  let httpClient = httpClientFactory.CreateClient "BulgarianSubs"

  // Initialize debug logger
  let debugLogger = DebugLogger.fileLogger "/tmp/bulgariansubs.log"

  // List of all available providers
  let providers: IProvider list =
    [ SabBz ()
      Subsunacs ()
      YavkaNet () ]

  // Helper to check if language is supported (Bulgarian)
  let isSupportedLanguage (lang: string) =
    match if isNull lang then "" else lang.ToLowerInvariant() with
    | "bg" | "bul" | "bulgarian" -> true
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

  interface ISubtitleProvider with
    member _.Name = "Bulgarian Subtitles (Sab.bz, Subsunacs & Yavka)"

    member _.SupportedMediaTypes =
      seq {
        yield VideoContentType.Movie
        yield VideoContentType.Episode
      }

    member _.Search(request: SubtitleSearchRequest, cancellationToken: CancellationToken) =
      task {
        // Extract metadata from Jellyfin library
        let metadata = MetadataExtractor.extract request

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

        let metadataDesc = MetadataExtractor.describeMetadata metadata
        debugLogger.Debug $"Search called with language: {request.Language}, name: {searchName}, season: {season}, episode: {episode}, contentType: {request.ContentType}, metadata: {metadataDesc}"

        if not (isSupportedLanguage request.Language) then
          return Seq.empty
        else
          let results = System.Collections.Generic.List<RemoteSubtitleInfo>()
          let searchTerm = System.Net.WebUtility.UrlEncode searchName
          let year = if request.ProductionYear.HasValue then Some request.ProductionYear.Value else None

          // Determine search variations based on available metadata
          let searchVariations =
            if MetadataExtractor.hasReliableMetadata metadata then
              // Build multiple search attempts with metadata
              let baseTerms = MetadataExtractor.buildEnhancedSearchTerms metadata searchName
              baseTerms 
              |> List.map System.Net.WebUtility.UrlEncode
              |> List.take 2  // Limit to first 2 variations to avoid redundant requests
            else
              // Fallback to single search term
              [searchTerm]

          // Search using all available providers with potentially multiple search variations
          for provider in providers do
            try
              // Try each search variation (usually just one)
              for encodedSearchTerm in searchVariations do
                let url = provider.SearchUrl encodedSearchTerm year
                debugLogger.Debug $"[{provider.Name}] Searching with query: {encodedSearchTerm}, year: {year}"
                
                try
                  let! response =
                    match provider.Name with
                    | "Yavka.net" ->
                      // Yavka uses POST
                      debugLogger.Debug $"[{provider.Name}] POST request to {url}"
                      let content = new FormUrlEncodedContent([
                        new KeyValuePair<string, string>("s", encodedSearchTerm)
                        new KeyValuePair<string, string>("l", "BG")
                      ])
                      httpClient.PostAsync(url, content, cancellationToken)
                    | _ ->
                      // Others use GET
                      debugLogger.Debug $"[{provider.Name}] GET request to {url}"
                      httpClient.GetAsync(url, cancellationToken)

                  let! bytes = readResponseAsBytes response cancellationToken
                  debugLogger.Debug $"[{provider.Name}] Response: {response.StatusCode}, {bytes.Length} bytes"
                  let encoding = System.Text.Encoding.GetEncoding("windows-1251")
                  let html = encoding.GetString(bytes)

                  let parsedItems = provider.ParseResults html |> Seq.toList
                  debugLogger.Debug $"[{provider.Name}] Parsed {parsedItems.Length} raw results"

                  parsedItems
                  |> Seq.filter (fun item ->
                    // Filter by episode if episode search
                    match (season, episode) with
                    | (Some s, Some e) -> matchesEpisode s e item.Title
                    | _ -> true)
                  |> Seq.iter (fun item ->
                    let info = RemoteSubtitleInfo()
                    // Encode both provider and download URL/page in ID
                    match item.DownloadStrategy with
                    | DirectUrl (url, _) -> info.Id <- $"{provider.Name}|{url}"
                    | FormPage (pageUrl, _) -> info.Id <- $"{provider.Name}|{pageUrl}"
                    info.Name <- item.Title
                    info.ProviderName <- item.ProviderName
                    info.ThreeLetterISOLanguageName <- "bul"
                    results.Add(info))
                  
                  let filteredCount = results.Count
                  debugLogger.Debug $"[{provider.Name}] Added {results.Count - (parsedItems.Length)} filtered results to total"
                with ex ->
                  logger.LogError(ex, $"Error searching {provider.Name}")
                  debugLogger.Debug $"[{provider.Name}] Exception: {ex.Message}"
            with ex ->
              logger.LogError(ex, $"Error searching {provider.Name}")
              debugLogger.Debug $"[{provider.Name}] Exception: {ex.Message}"

          return results :> seq<RemoteSubtitleInfo>
      }

    member _.GetSubtitles(id: string, cancellationToken: CancellationToken) =
      task {
        let parts = id.Split('|')

        if parts.Length < 2 then
          return SubtitleResponse()
        else
          let providerName, url = parts.[0], parts.[1]

          // Reconstruct the download strategy based on provider
          let referer =
            match providerName with
            | "Subs.Sab.Bz" -> "http://subs.sab.bz/"
            | "Subsunacs" -> "https://subsunacs.net/"
            | "Yavka.net" -> "https://yavka.net/"
            | _ -> "http://localhost/"

          // Determine strategy - FormPage for Yavka, DirectUrl for others
          let strategy =
            if providerName = "Yavka.net" then
              FormPage (url, referer)
            else
              DirectUrl (url, referer)

          let! (subStream, ext) = Common.executeStrategy strategy httpClient cancellationToken Common.extractSubtitleStream

          let result = SubtitleResponse()
          result.Format <- ext
          result.Stream <- subStream
          result.Language <- "bul"
          return result
      }
