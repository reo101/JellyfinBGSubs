namespace Jellyfin.Plugin.BulgarianSubs

open System
open System.IO
open System.Text.RegularExpressions

/// Explicit criteria for subtitle result ordering and archive selection.
module SubtitleMatching =
  // Recognize compact S/E tags; two-digit x-style seasons avoid video dimensions.
  let private episodePattern =
    Regex(
      @"(?:s(?<season>[0-9]+)e(?<episode>[0-9]+)|(?<![0-9])(?<season>[0-9]{1,2})x(?<episode>[0-9]+))(?![0-9])",
      RegexOptions.IgnoreCase
    )

  let private seasonPattern =
    Regex(@"(?:(?<![a-z0-9])(?:s|season[ ._-]*)|(?-i:S))(?<season>[0-9]+)(?![a-z0-9])", RegexOptions.IgnoreCase)

  let private yearPattern = Regex(@"(?<![0-9])(?:19|20)[0-9]{2}(?![0-9])")

  let private episodes (value: string) =
    episodePattern.Matches(if isNull value then "" else value)
    |> Seq.cast<Match>
    |> Seq.choose (fun matched ->
      match Int32.TryParse matched.Groups.["season"].Value, Int32.TryParse matched.Groups.["episode"].Value with
      | (true, season), (true, episode) -> Some(season, episode)
      | _ -> None)
    |> Seq.toList

  let isEpisodeCandidate (context: SubtitleSelectionContext) value =
    if not context.Season.HasValue then
      true
    else
      let season, episode = context.Season.Value, context.Episode.Value

      match episodes value with
      | _ :: _ as values -> values |> List.contains (season, episode)
      | [] when episodePattern.IsMatch(if isNull value then "" else value) -> false
      | [] ->
        seasonPattern.Matches(if isNull value then "" else value)
        |> Seq.cast<Match>
        |> Seq.exists (fun matched ->
          match Int32.TryParse matched.Groups.["season"].Value with
          | true, value -> value = season
          | _ -> false)

  let private normalize (value: string) =
    if String.IsNullOrWhiteSpace value then
      ""
    else
      let separated = episodePattern.Replace(value.ToLowerInvariant(), " $0 ")
      Regex.Replace(separated, @"[^\p{L}\p{Nd}]+", " ").Trim()

  let private tokens value =
    (normalize value).Split(' ', StringSplitOptions.RemoveEmptyEntries)
    |> Set.ofArray

  /// Criteria are ordered explicitly. Missing metadata never excludes a search result.
  // ponytail: filename criteria only; provider release metadata can resolve demonstrated ambiguities.
  let rank (context: SubtitleSelectionContext) (candidate: string) =
    let candidateName = normalize candidate

    let sourceName =
      if String.IsNullOrWhiteSpace context.FileName then
        ""
      else
        normalize (Path.GetFileNameWithoutExtension(context.FileName.Replace('\\', '/')))

    let title = normalize context.Title
    let exactRelease = sourceName <> "" && sourceName = candidateName

    let titleRank =
      if title = "" then
        0
      elif candidateName = title then
        2
      elif candidateName.StartsWith(title + " ", StringComparison.Ordinal) then
        let suffix = candidateName.Substring(title.Length)

        let remaining =
          episodePattern.Replace(suffix, " ")
          |> fun text -> seasonPattern.Replace(text, " ") |> fun text -> yearPattern.Replace(text, " ")

        if normalize remaining = "" then 2 else 1
      elif (" " + candidateName + " ").Contains(" " + title + " ", StringComparison.Ordinal) then
        1
      else
        0

    let yearMatches =
      context.Year.HasValue
      && (yearPattern.Matches(if isNull candidate then "" else candidate)
          |> Seq.cast<Match>
          |> Seq.exists (fun matched -> Int32.Parse matched.Value = context.Year.Value))

    let sharedTokens = Set.intersect (tokens sourceName) (tokens candidate) |> Set.count
    exactRelease, titleRank, yearMatches, sharedTokens

  let selectArchiveName (context: SubtitleSelectionContext) (names: string list) =
    let entryEpisodes (name: string) =
      let fileName = Path.GetFileName(name.Replace('\\', '/'))

      if episodePattern.IsMatch fileName then
        episodes fileName
      else
        episodes name

    let compatible =
      if context.Season.HasValue then
        let exact =
          names
          |> List.filter (fun name ->
            entryEpisodes name
            |> List.contains (context.Season.Value, context.Episode.Value))

        if not exact.IsEmpty then
          exact
        else
          // A lone unnamed subtitle can belong to the explicitly selected result.
          match names with
          | [ name ] when
            not (episodePattern.IsMatch name)
            && (seasonPattern.Matches(name).Count = 0 || isEpisodeCandidate context name)
            ->
            [ name ]
          | _ -> []
      else
        names

    match compatible with
    | [] -> raise (InvalidDataException "Archive contains no subtitle matching the requested episode")
    | [ name ] -> name
    | candidates ->
      let ranked =
        candidates
        |> List.map (fun name -> name, rank context (Path.GetFileNameWithoutExtension(name.Replace('\\', '/'))))

      let best = ranked |> List.maxBy snd |> snd
      let matches = ranked |> List.filter (fun (_, score) -> score = best)

      match matches with
      | [ (name, _) ] when best <> (false, 0, false, 0) -> name
      | _ -> raise (InvalidDataException "Archive contains multiple ambiguous subtitles")
