namespace Jellyfin.Plugin.BulgarianSubs

open System
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
    EpisodeInfo: (int * int) option }

/// Utilities for extracting and using metadata from SubtitleSearchRequest
module MetadataExtractor =

  /// Extract IMDB ID from ProviderIds dictionary
  let private extractImdbId (providerIds: System.Collections.Generic.Dictionary<string, string>) : string option =
    match providerIds with
    | null -> None
    | dict ->
      try
        match dict.TryGetValue "Imdb" with
        | (true, id) when not (String.IsNullOrEmpty id) -> Some id
        | _ -> None
      with _ -> None

  /// Extract TMDb ID from ProviderIds dictionary
  let private extractTmdbId (providerIds: System.Collections.Generic.Dictionary<string, string>) : string option =
    match providerIds with
    | null -> None
    | dict ->
      try
        match dict.TryGetValue "Tmdb" with
        | (true, id) when not (String.IsNullOrEmpty id) -> Some id
        | _ -> None
      with _ -> None

  /// Determine if request is for a movie
  let isMovie (request: SubtitleSearchRequest) : bool =
    not (request.ParentIndexNumber.HasValue || request.IndexNumber.HasValue)

  /// Determine if request is for an episode
  let isEpisode (request: SubtitleSearchRequest) : bool =
    request.ParentIndexNumber.HasValue || request.IndexNumber.HasValue

  /// Extract season and episode numbers if available
  let extractEpisodeInfo (request: SubtitleSearchRequest) : (int * int) option =
    match (request.ParentIndexNumber, request.IndexNumber) with
    | (season, episode) when season.HasValue && episode.HasValue ->
      Some (season.Value, episode.Value)
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
    let seriesName = if not (String.IsNullOrEmpty request.SeriesName) then Some request.SeriesName else None
    let episodeInfo = extractEpisodeInfo request
    let contentType = request.ContentType.ToString() |> (fun s -> if String.IsNullOrEmpty s then None else Some s)

    { ImdbId = imdbId
      TmdbId = tmdbId
      Year = year
      ContentType = contentType
      IsMovie = isMovie
      IsEpisode = isEpisode
      SeriesName = seriesName
      EpisodeInfo = episodeInfo }

  /// Build enhanced search terms using metadata
  /// For movies with IMDb/TMDb ID, include year to narrow results
  /// For episodes, also include series info if available
  let buildEnhancedSearchTerms (metadata: ExtractedMetadata) (baseSearchTerm: string) : string list =
    [
      // Primary search term
      baseSearchTerm

      // If we have a production year, add it for better matching
      match metadata.Year with
      | Some year -> sprintf "%s %d" baseSearchTerm year
      | None -> baseSearchTerm

      // For episodes, no additional terms needed (already have series name)
      // The base search term should be the series name

      // Fallback to just the base term
      baseSearchTerm
    ]
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
    let parts = match metadata.ContentType with Some ct -> parts @ [$"{ct}"] | None -> parts
    let parts = match metadata.ImdbId with Some id -> parts @ [$"IMDb:{id}"] | None -> parts
    let parts = match metadata.TmdbId with Some id -> parts @ [$"TMDb:{id}"] | None -> parts
    let parts = match metadata.Year with Some year -> parts @ [$"{year}"] | None -> parts
    let parts = match metadata.EpisodeInfo with Some (s, e) -> parts @ [$"S{s:D2}E{e:D2}"] | None -> parts
    
    match parts with
    | [] -> "No metadata"
    | parts -> String.concat ", " parts
