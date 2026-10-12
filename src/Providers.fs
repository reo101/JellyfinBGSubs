namespace Jellyfin.Plugin.BulgarianSubs

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.IO
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

/// Jellyfin's single subtitle provider, aggregating the configured subtitle sites.
type BulgarianSubtitleProvider
  (logger: ILogger<BulgarianSubtitleProvider>, httpClientFactory: IHttpClientFactory, libraryManager: ILibraryManager) =

  static do Encoding.RegisterProvider CodePagesEncodingProvider.Instance

  let httpClient = httpClientFactory.CreateClient "BulgarianSubs"
  let requestTimeout = TimeSpan.FromSeconds 10.0

  let providers: (IProvider * (PluginConfiguration -> bool)) list =
    [ SabBz(), (fun config -> config.EnableSabBz)
      Subsunacs(), (fun config -> config.EnableSubsunacs)
      YavkaNet(), (fun config -> config.EnableYavkaNet)
      Podnapisi(), (fun config -> config.EnablePodnapisi) ]

  let getEnabledProviders () =
    let config =
      match Plugin.Instance with
      | Some plugin -> plugin.Configuration
      | None -> PluginConfiguration() // Standalone test/debug executables.

    providers
    |> List.choose (fun (provider, enabled) -> if enabled config then Some provider else None)

  let nonEmpty = Option.ofObj >> Option.filter (not << String.IsNullOrWhiteSpace)

  let getOriginalTitle (mediaPath: string) =
    try
      option {
        let! path = nonEmpty mediaPath
        let item = libraryManager.FindByPath(path, Nullable())

        match item with
        | :? Movie as movie -> return! nonEmpty movie.OriginalTitle
        | :? Episode as episode -> return! nonEmpty episode.Series.OriginalTitle
        | _ -> return! None
      }
    with ex ->
      logger.LogDebug(ex, "Could not read original title from the library")
      None

  let isSupportedLanguage (language: string) =
    match if isNull language then "" else language.ToLowerInvariant() with
    | "bg"
    | "bul"
    | "bulgarian" -> true
    | _ -> false

  interface ISubtitleProvider with
    // Jellyfin derives the provider identifier from this name; configuration must not change it.
    member _.Name = "Bulgarian Subtitles"

    member _.SupportedMediaTypes = [ VideoContentType.Movie; VideoContentType.Episode ]

    member _.Search(request: SubtitleSearchRequest, cancellationToken: CancellationToken) =
      task {
        cancellationToken.ThrowIfCancellationRequested()

        if not (isSupportedLanguage request.Language) then
          return Seq.empty
        else
          let metadata = MetadataExtractor.extract request

          let searchName =
            getOriginalTitle request.MediaPath
            |> Option.orElse (
              if metadata.IsEpisode then
                nonEmpty request.SeriesName
              else
                None
            )
            |> Option.defaultValue request.Name

          let context =
            { Title = searchName
              Year = metadata.Year |> Option.toNullable
              Season = metadata.EpisodeInfo |> Option.map fst |> Option.toNullable
              Episode = metadata.EpisodeInfo |> Option.map snd |> Option.toNullable
              FileName =
                if String.IsNullOrWhiteSpace request.MediaPath then
                  ""
                else
                  Path.GetFileName(request.MediaPath.Replace('\\', '/')) }

          let searchTerms =
            MetadataExtractor.buildEnhancedSearchTerms metadata searchName
            |> List.truncate 3

          logger.LogDebug("Searching for {Title}: {Metadata}", searchName, MetadataExtractor.describeMetadata metadata)
          let results = List<RemoteSubtitleInfo>()
          let identifiers = HashSet<string>()

          for provider in getEnabledProviders () do
            for searchTerm in searchTerms do
              cancellationToken.ThrowIfCancellationRequested()

              try
                let encodedTerm = System.Net.WebUtility.UrlEncode searchTerm
                let url = provider.SearchUrl encodedTerm metadata.Year
                use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                timeout.CancelAfter requestTimeout
                use message = provider.CreateSearchRequest url searchTerm
                message.Headers.UserAgent.ParseAdd "Mozilla/5.0 (compatible; JellyfinBulgarianSubs)"
                use! response = Common.sendWithRedirects httpClient message timeout.Token
                response.EnsureSuccessStatusCode() |> ignore
                let! bytes = Common.readResponseBytes response timeout.Token
                let text = provider.ResponseEncoding.GetString bytes

                for item in
                  provider.ParseResults text
                  |> Seq.filter (fun item -> SubtitleMatching.isEpisodeCandidate context item.Title) do

                  let downloadUrl =
                    match item.DownloadStrategy with
                    | DirectUrl(url, _)
                    | FormPage(url, _) -> url

                  let identifier = SubtitleIdentifier.encode provider.Name downloadUrl context

                  if identifiers.Add identifier then
                    let info = RemoteSubtitleInfo()
                    info.Id <- identifier
                    info.Name <- item.Title
                    info.ProviderName <- "Bulgarian Subtitles"
                    info.ThreeLetterISOLanguageName <- "bul"
                    info.Format <- item.Format |> Option.defaultValue ""
                    info.Author <- item.Author |> Option.defaultValue ""
                    info.DownloadCount <- item.DownloadCount |> Option.defaultValue 0
                    info.FrameRate <- item.FrameRate |> Option.map float32 |> Option.toNullable
                    info.CommunityRating <- item.Rating |> Option.map float32 |> Option.toNullable
                    info.DateCreated <- item.UploadDate |> Option.toNullable

                    info.Comment <-
                      match item.InfoPageUrl with
                      | Some url -> $"[{item.ProviderName}]\n{url}"
                      | None -> $"[{item.ProviderName}]"

                    results.Add info
              with
              | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> raise ex
              | ex -> logger.LogWarning(ex, "Search failed for {Provider}", provider.Name)

          return
            results
            |> Seq.sortByDescending (fun result ->
              SubtitleMatching.rank context result.Name, result.DownloadCount.GetValueOrDefault())
            |> Seq.toList
            :> seq<RemoteSubtitleInfo>
      }

    member _.GetSubtitles(identifier: string, cancellationToken: CancellationToken) =
      task {
        cancellationToken.ThrowIfCancellationRequested()

        let providerName, url, context = SubtitleIdentifier.decode identifier

        let provider =
          getEnabledProviders ()
          |> List.tryFind (fun provider -> provider.Name = providerName)
          |> Option.defaultWith (fun () -> invalidArg "identifier" "Unknown or disabled subtitle provider")

        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, uri -> Common.validateProviderUri (Uri provider.Referer) uri
        | _ -> invalidArg "identifier" "Subtitle URL does not belong to its provider"

        use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
        timeout.CancelAfter requestTimeout

        let! stream, extension =
          Common.executeStrategy
            (provider.CreateDownloadStrategy url)
            httpClient
            timeout.Token
            (Common.extractSubtitleStreamWithContext logger context)

        let response = SubtitleResponse()
        response.Format <- extension
        response.Stream <- stream
        response.Language <- "bul"
        return response
      }
