namespace Jellyfin.Plugin.BulgarianSubs

open System
open System.IO
open System.Text.RegularExpressions
open MediaBrowser.Controller.Subtitles

/// Extracted metadata from Jellyfin library for improved searching
type ExtractedMetadata =
  { ImdbId: string option
    TmdbId: string option
    Year: int option
    ContentType: string option
    IsMovie: bool
    IsEpisode: bool
    SeriesName: string option
    EpisodeInfo: (int * int) option
    FileBasedTitle: string option }

/// Utilities for extracting and using metadata from SubtitleSearchRequest
module MetadataExtractor =

  /// Extract title from media file path
  /// Parses common naming patterns like "Movie.Title.2020.1080p.BluRay.mkv" or "Movie Title (2020)/Movie Title (2020).mkv"
  let private extractTitleFromPath (mediaPath: string) : string option =
    if String.IsNullOrEmpty mediaPath then
      None
    else
      try
        // Get the filename without extension
        let fileName = Path.GetFileNameWithoutExtension(mediaPath)

        if String.IsNullOrEmpty fileName then
          None
        else
          // Try to extract title using common patterns

          // Pattern 1: "Title.Year.Quality..." or "Title Year Quality..."
          // Match: everything before a 4-digit year (19xx or 20xx)
          let yearPattern =
            Regex(@"^(.+?)[.\s_-]*((?:19|20)\d{2})[.\s_-]*", RegexOptions.IgnoreCase)

          let yearMatch = yearPattern.Match(fileName)

          if yearMatch.Success then
            let title = yearMatch.Groups.[1].Value
            // Replace dots, underscores with spaces and clean up
            let cleanTitle = Regex.Replace(title, @"[._]", " ").Trim()

            if not (String.IsNullOrWhiteSpace cleanTitle) then
              Some cleanTitle
            else
              None
          else
            // Pattern 2: Just use the filename, replace dots/underscores
            let cleanTitle = Regex.Replace(fileName, @"[._]", " ")
            // Remove common quality/codec tags at the end
            let withoutTags =
              Regex.Replace(
                cleanTitle,
                @"\s*(720p|1080p|2160p|4K|BluRay|BRRip|WEBRip|HDTV|DVDRip|x264|x265|HEVC|AAC|AC3|DTS).*$",
                "",
                RegexOptions.IgnoreCase
              )

            let finalTitle = withoutTags.Trim()

            if not (String.IsNullOrWhiteSpace finalTitle) then
              Some finalTitle
            else
              None
      with _ ->
        None

  /// Extract IMDB ID from ProviderIds dictionary
  let private extractImdbId (providerIds: System.Collections.Generic.Dictionary<string, string>) : string option =
    match providerIds with
    | null -> None
    | dict ->
      try
        match dict.TryGetValue "Imdb" with
        | (true, id) when not (String.IsNullOrEmpty id) -> Some id
        | _ -> None
      with _ ->
        None

  /// Extract TMDb ID from ProviderIds dictionary
  let private extractTmdbId (providerIds: System.Collections.Generic.Dictionary<string, string>) : string option =
    match providerIds with
    | null -> None
    | dict ->
      try
        match dict.TryGetValue "Tmdb" with
        | (true, id) when not (String.IsNullOrEmpty id) -> Some id
        | _ -> None
      with _ ->
        None

  /// Determine if request is for a movie
  let isMovie (request: SubtitleSearchRequest) : bool =
    not (request.ParentIndexNumber.HasValue || request.IndexNumber.HasValue)

  /// Determine if request is for an episode
  let isEpisode (request: SubtitleSearchRequest) : bool =
    request.ParentIndexNumber.HasValue || request.IndexNumber.HasValue

  /// Extract season and episode numbers if available
  let extractEpisodeInfo (request: SubtitleSearchRequest) : (int * int) option =
    match (request.ParentIndexNumber, request.IndexNumber) with
    | (season, episode) when season.HasValue && episode.HasValue -> Some(season.Value, episode.Value)
    | _ -> None

  /// Extract year from ProductionYear if available
  let extractYear (request: SubtitleSearchRequest) : int option =
    match request.ProductionYear with
    | year when year.HasValue -> Some year.Value
    | _ -> None

  /// Extract all available metadata from the subtitle search request
  let extract (request: SubtitleSearchRequest) : ExtractedMetadata =
    let imdbId = extractImdbId request.ProviderIds
    let tmdbId = extractTmdbId request.ProviderIds
    let year = extractYear request
    let isMovie = isMovie request
    let isEpisode = isEpisode request

    let seriesName =
      if not (String.IsNullOrEmpty request.SeriesName) then
        Some request.SeriesName
      else
        None

    let episodeInfo = extractEpisodeInfo request

    let contentType =
      request.ContentType.ToString()
      |> (fun s -> if String.IsNullOrEmpty s then None else Some s)

    let fileBasedTitle = extractTitleFromPath request.MediaPath

    { ImdbId = imdbId
      TmdbId = tmdbId
      Year = year
      ContentType = contentType
      IsMovie = isMovie
      IsEpisode = isEpisode
      SeriesName = seriesName
      EpisodeInfo = episodeInfo
      FileBasedTitle = fileBasedTitle }

  /// Build enhanced search terms using metadata
  /// Prioritizes file-based title (usually English) over display name (may be localized)
  /// For movies with IMDb/TMDb ID, include year to narrow results
  /// For episodes, also include series info if available
  let buildEnhancedSearchTerms (metadata: ExtractedMetadata) (baseSearchTerm: string) : string list =
    [
      // Priority 1: File-based title (usually the original English title)
      match metadata.FileBasedTitle with
      | Some fileTitle -> yield fileTitle
      | None -> ()

      // Priority 2: File-based title with year
      match (metadata.FileBasedTitle, metadata.Year) with
      | (Some fileTitle, Some year) -> yield sprintf "%s %d" fileTitle year
      | _ -> ()

      // Priority 3: Display name (may be localized but fallback)
      yield baseSearchTerm

      // Priority 4: Display name with year
      match metadata.Year with
      | Some year -> yield sprintf "%s %d" baseSearchTerm year
      | None -> () ]
    |> List.distinct

  /// Check if metadata indicates high-confidence match
  /// High confidence: has IMDb/TMDb ID + year
  let isHighConfidenceMatch (metadata: ExtractedMetadata) : bool =
    (metadata.ImdbId.IsSome || metadata.TmdbId.IsSome) && metadata.Year.IsSome

  /// Check if metadata is sufficient for reliable searching
  let hasReliableMetadata (metadata: ExtractedMetadata) : bool =
    // Movies need either IMDb/TMDb ID or at least a year
    if metadata.IsMovie then
      metadata.ImdbId.IsSome || metadata.TmdbId.IsSome || metadata.Year.IsSome
    // Episodes need series name and episode info
    elif metadata.IsEpisode then
      metadata.SeriesName.IsSome && metadata.EpisodeInfo.IsSome
    else
      false

  /// Get a human-readable description of available metadata
  let describeMetadata (metadata: ExtractedMetadata) : string =
    let parts = []

    let parts =
      match metadata.ContentType with
      | Some ct -> parts @ [ $"{ct}" ]
      | None -> parts

    let parts =
      match metadata.ImdbId with
      | Some id -> parts @ [ $"IMDb:{id}" ]
      | None -> parts

    let parts =
      match metadata.TmdbId with
      | Some id -> parts @ [ $"TMDb:{id}" ]
      | None -> parts

    let parts =
      match metadata.Year with
      | Some year -> parts @ [ $"{year}" ]
      | None -> parts

    let parts =
      match metadata.EpisodeInfo with
      | Some(s, e) -> parts @ [ $"S{s:D2}E{e:D2}" ]
      | None -> parts

    let parts =
      match metadata.FileBasedTitle with
      | Some title -> parts @ [ $"File:\"{title}\"" ]
      | None -> parts

    match parts with
    | [] -> "No metadata"
    | parts -> String.concat ", " parts
