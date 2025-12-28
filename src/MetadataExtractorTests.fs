module MetadataExtractorTests

open System
open System.Collections.Generic
open MediaBrowser.Controller.Subtitles
open MediaBrowser.Controller.Providers
open Jellyfin.Plugin.BulgarianSubs

let createRequest 
  (name: string) 
  (seriesName: string option)
  (seasonNum: int option) 
  (episodeNum: int option) 
  (year: int option)
  (imdbId: string option)
  (tmdbId: string option) : SubtitleSearchRequest =
  let req = SubtitleSearchRequest()
  req.Name <- name
  if seriesName.IsSome then req.SeriesName <- seriesName.Value
  if seasonNum.IsSome then req.ParentIndexNumber <- Nullable(seasonNum.Value)
  if episodeNum.IsSome then req.IndexNumber <- Nullable(episodeNum.Value)
  if year.IsSome then req.ProductionYear <- Nullable(year.Value)
  
  req.ProviderIds <- Dictionary(StringComparer.OrdinalIgnoreCase)
  if imdbId.IsSome then req.ProviderIds.["Imdb"] <- imdbId.Value
  if tmdbId.IsSome then req.ProviderIds.["Tmdb"] <- tmdbId.Value
  
  req.ContentType <- VideoContentType.Movie
  req

let test_extractMetadata_movie_with_year () =
  let request = createRequest "Inception" None None None (Some 2010) None None
  let metadata = MetadataExtractor.extract request
  
  assert metadata.IsMovie
  assert (not metadata.IsEpisode)
  assert (metadata.Year = Some 2010)
  printfn "✓ Extract movie metadata with year"

let test_extractMetadata_movie_with_imdb () =
  let request = createRequest "The Matrix" None None None (Some 1999) (Some "tt0133093") None
  let metadata = MetadataExtractor.extract request
  
  assert metadata.IsMovie
  assert (metadata.ImdbId = Some "tt0133093")
  assert (metadata.Year = Some 1999)
  printfn "✓ Extract movie metadata with IMDb ID"

let test_extractMetadata_episode () =
  let request = createRequest "Breaking Bad" (Some "Breaking Bad") (Some 1) (Some 1) (Some 2008) None None
  let metadata = MetadataExtractor.extract request
  
  assert (not metadata.IsMovie)
  assert metadata.IsEpisode
  assert (metadata.SeriesName = Some "Breaking Bad")
  assert (metadata.EpisodeInfo = Some (1, 1))
  printfn "✓ Extract episode metadata"

let test_isHighConfidenceMatch () =
  // Movie with IMDb + year = high confidence
  let request1 = createRequest "Inception" None None None (Some 2010) (Some "tt1375666") None
  let meta1 = MetadataExtractor.extract request1
  assert (MetadataExtractor.isHighConfidenceMatch meta1)
  
  // Movie with only name = not high confidence
  let request2 = createRequest "RandomTitle" None None None None None None
  let meta2 = MetadataExtractor.extract request2
  assert (not (MetadataExtractor.isHighConfidenceMatch meta2))
  
  printfn "✓ High confidence matching works correctly"

let test_hasReliableMetadata () =
  // Movie with year = reliable
  let request1 = createRequest "MovieName" None None None (Some 2020) None None
  let meta1 = MetadataExtractor.extract request1
  assert (MetadataExtractor.hasReliableMetadata meta1)
  
  // Episode with series name and episode info = reliable
  let request2 = createRequest "ShowName" (Some "Breaking Bad") (Some 5) (Some 14) None None None
  let meta2 = MetadataExtractor.extract request2
  assert (MetadataExtractor.hasReliableMetadata meta2)
  
  // Episode without season/episode = not reliable
  let request3 = createRequest "ShowName" (Some "Breaking Bad") None None None None None
  let meta3 = MetadataExtractor.extract request3
  assert (not (MetadataExtractor.hasReliableMetadata meta3))
  
  printfn "✓ Reliable metadata detection works"

let test_buildEnhancedSearchTerms () =
  let request = createRequest "Inception" None None None (Some 2010) None None
  let metadata = MetadataExtractor.extract request
  let terms = MetadataExtractor.buildEnhancedSearchTerms metadata "Inception"
  
  // Should include base term and with year
  assert (List.length terms >= 1)
  assert (List.contains "Inception" terms)
  assert (List.contains "Inception 2010" terms)
  printfn "✓ Enhanced search terms include year variations"

let test_describeMetadata () =
  let request = createRequest "Inception" None None None (Some 2010) (Some "tt1375666") None
  let metadata = MetadataExtractor.extract request
  let desc = MetadataExtractor.describeMetadata metadata
  
  assert (desc.Contains "IMDb:tt1375666")
  assert (desc.Contains "2010")
  printfn "✓ Metadata description includes all available info"

let test_metadata_extraction_complete () =
  let request = createRequest "The Dark Knight Rises" None None None (Some 2012) (Some "tt1345836") (Some "49026")
  let metadata = MetadataExtractor.extract request
  
  assert (metadata.ImdbId = Some "tt1345836")
  assert (metadata.TmdbId = Some "49026")
  assert (metadata.Year = Some 2012)
  assert (metadata.IsMovie)
  printfn "✓ Complete metadata extraction for rich request"

let runTests () =
  test_extractMetadata_movie_with_year ()
  test_extractMetadata_movie_with_imdb ()
  test_extractMetadata_episode ()
  test_isHighConfidenceMatch ()
  test_hasReliableMetadata ()
  test_buildEnhancedSearchTerms ()
  test_describeMetadata ()
  test_metadata_extraction_complete ()
