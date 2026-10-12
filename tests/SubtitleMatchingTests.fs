namespace Jellyfin.Plugin.BulgarianSubs

open System
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open MediaBrowser.Controller.Subtitles
open MediaBrowser.Controller.Library
open MediaBrowser.Controller.Providers
open Jellyfin.Plugin.BulgarianSubs.Providers

module SubtitleMatchingTests =
  let private context title year season episode fileName : SubtitleSelectionContext =
    { Title = title
      Year = Option.toNullable year
      Season = Option.toNullable season
      Episode = Option.toNullable episode
      FileName = fileName }

  let private equal expected actual =
    if expected <> actual then
      failwithf "Expected %A, got %A" expected actual

  let private throws<'T when 'T :> exn> action =
    try
      action ()
      failwithf "Expected %s" typeof<'T>.Name
    with :? 'T ->
      ()

  let private archive (files: (string * string) list) =
    use output = new MemoryStream()
    use zip = new ZipArchive(output, ZipArchiveMode.Create, true)

    for name, text in files do
      let entry = zip.CreateEntry name
      use writer = new StreamWriter(entry.Open())
      writer.Write text

    zip.Dispose()
    output.ToArray()

  let private text (stream: Stream) =
    use reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true)
    reader.ReadToEnd()

  let private extract context files =
    use input = new MemoryStream(archive files)

    let stream, extension =
      Common.extractSubtitleStreamWithContext NullLogger.Instance context input "zip"

    use stream = stream
    extension, text stream

  let private testIdentifiers () =
    let context = context "Сериал" (Some 2020) (Some 1) (Some 2) "Сериал.S01E02.mkv"
    let id = SubtitleIdentifier.encode "provider" "https://example.test/sub.zip" context
    let provider, url, decoded = SubtitleIdentifier.decode id
    equal "provider" provider
    equal "https://example.test/sub.zip" url
    equal (Some context) decoded

    let legacy =
      "provider::"
      + Convert.ToBase64String(Encoding.UTF8.GetBytes "https://example.test/a.srt")

    let legacyProvider, legacyUrl, legacyContext = SubtitleIdentifier.decode legacy
    equal "provider" legacyProvider
    equal "https://example.test/a.srt" legacyUrl
    equal None legacyContext

    for invalid in
      [ "provider::%%%"
        "provider::" + Convert.ToBase64String(Encoding.UTF8.GetBytes "not-json") ] do
      throws<ArgumentException> (fun () -> SubtitleIdentifier.decode invalid |> ignore)

    let rejects (payload: string) =
      throws<ArgumentException> (fun () ->
        SubtitleIdentifier.decode ("provider::" + Convert.ToBase64String(Encoding.UTF8.GetBytes payload))
        |> ignore)

    rejects "{\"version\":2,\"url\":\"https://example.test/a\",\"context\":{}}"
    rejects "{\"version\":1,\"url\":\"https://example.test/a\",\"context\":null}"
    rejects "{\"version\":1,\"url\":\"https://example.test/a\",\"context\":{\"season\":1,\"episode\":null}}"
    rejects "{\"version\":1,\"url\":\"file:///tmp/subtitle\",\"context\":{}}"

    throws<ArgumentException> (fun () ->
      SubtitleIdentifier.decode ("provider::" + Convert.ToBase64String [| 0xffuy |])
      |> ignore)

    throws<ArgumentException> (fun () -> SubtitleIdentifier.decode (String('x', 8193)) |> ignore)

  let private testArchiveSelection () =
    let logger = NullLogger.Instance
    let requested = context "Series" None (Some 1) (Some 2) "Series.S01E02.1080p.mkv"
    let zip = archive [ "Series.S01E01.srt", "wrong"; "Series.S01E02.srt", "right" ]
    use input = new MemoryStream(zip)

    let stream, extension =
      Common.extractSubtitleStreamWithContext logger (Some requested) input "zip"

    use stream = stream
    equal "srt" extension
    equal "right" (text stream)

    let singleton = archive [ "captions.srt", "unlabelled" ]
    use input = new MemoryStream(singleton)

    let stream, _ =
      Common.extractSubtitleStreamWithContext logger (Some requested) input "zip"

    use stream = stream
    equal "unlabelled" (text stream)

    for entries in [ [ "Series.S01E01.srt", "wrong" ]; [ "one.srt", "one"; "two.srt", "two" ] ] do
      use input = new MemoryStream(archive entries)

      throws<InvalidDataException> (fun () ->
        Common.extractSubtitleStreamWithContext logger (Some requested) input "zip"
        |> ignore)

    let ranked =
      archive [ "unrelated.srt", "unknown"; "Series.S01E02.1080p.srt", "filename" ]

    use input = new MemoryStream(ranked)

    let stream, _ =
      Common.extractSubtitleStreamWithContext logger (Some requested) input "zip"

    use stream = stream
    equal "filename" (text stream)

    equal
      ("srt", "release")
      (extract
        (Some requested)
        [ "Series.S01E02.720p.srt", "other release"
          "Series.S01E02.1080p.srt", "release" ])

    equal
      ("sub", "episode")
      (extract
        (Some requested)
        [ "S01E02/Series.S01E01.srt", "wrong filename"
          "folder\\Series.1x2.sub", "episode" ])

    for entries in
      [ [ "SeriesS01E01.srt", "wrong compact episode" ]
        [ "Series1x1.srt", "wrong compact episode" ]
        [ "SeriesS99999999999999999999E01.srt", "invalid episode marker" ]
        [ "SeriesS02.Complete.srt", "wrong compact season" ]
        [ "Series.S01E020.srt", "wrong episode" ]
        [ "Series.S02.Complete.srt", "wrong season" ]
        [ "Series.S01E02.480p.srt", "ambiguous"; "Series.S01E02.720p.srt", "ambiguous" ] ] do
      throws<InvalidDataException> (fun () -> extract (Some requested) entries |> ignore)

    equal
      ("srt", "compact episode")
      (extract (Some requested) [ "SeriesS01E01.srt", "wrong"; "SeriesS01E02.srt", "compact episode" ])

    let longSeries = context "Series" None (Some 1) (Some 1000) "Series.S01E1000.mkv"

    let _, _, decoded =
      SubtitleIdentifier.encode "provider" "https://example.test/a" longSeries
      |> SubtitleIdentifier.decode

    equal (Some longSeries) decoded

    equal
      ("srt", "episode 1000")
      (extract decoded [ "Series.S01E0999.srt", "wrong"; "Series.S01E1000.srt", "episode 1000" ])

    let movie = context "Movie" (Some 2020) None None ""

    equal
      ("srt", "movie")
      (extract
        (Some movie)
        [ "Other.Movie.2020.srt", "other title"
          "Movie.2019.srt", "other year"
          "Movie.2020.srt", "movie" ])

    let numericTitle = context "1917" (Some 2019) None None "1917.2019.mkv"

    equal
      ("srt", "numeric title")
      (extract (Some numericTitle) [ "Other.1917.2019.srt", "other"; "1917.2019.srt", "numeric title" ])

    let large = String('x', 10 * 1024 * 1024 + 1)

    throws<InvalidDataException> (fun () ->
      extract (Some requested) [ "Series.S01E01.srt", "wrong"; "Series.S01E02.srt", large ]
      |> ignore)

    let manyEntries =
      List.init 4096 (fun index -> $"Series.S01E01.{index}.srt", "")
      @ [ "Series.S01E02.srt", "right" ]

    throws<InvalidDataException> (fun () -> extract (Some requested) manyEntries |> ignore)

    use input =
      new MemoryStream(archive [ "first.srt", "first"; "second.srt", "second" ])

    let stream, _ = Common.extractSubtitleStreamWithLogging logger input "zip"
    use stream = stream
    equal "first" (text stream)

  type private Handler(responses: (string * byte[]) list) =
    inherit HttpMessageHandler()

    override _.SendAsync(request, _token) =
      let url = request.RequestUri.ToString()

      let bytes =
        responses
        |> List.tryPick (fun (matchUrl, body) -> if url.Contains matchUrl then Some body else None)
        |> Option.defaultValue [||]

      let response = new HttpResponseMessage(HttpStatusCode.OK)
      response.Content <- new ByteArrayContent(bytes)
      Threading.Tasks.Task.FromResult response

  type private ClientFactory(responses) =
    interface IHttpClientFactory with
      member _.CreateClient _ = new HttpClient(new Handler(responses))

  let private sabRow id title downloads =
    $"<tr class='subs-row'><td></td><td></td><td></td><td><a href='index.php?act=download&amp;attach_id={id}'>{title}</a></td><td>15 Nov 2020</td><td>BG</td><td>1</td><td>25</td><td><a>user</a></td><td>8</td><td>{downloads}</td></tr>"

  let private testSearchAndDownload () =
    let noise =
      [ 1..25 ]
      |> List.map (fun index -> sabRow (string (index + 2)) $"Unrelated {index} S01" "9000")
      |> String.concat ""

    let html =
      "<table>"
      + noise
      + sabRow "1" "Other Series S01 (2020)" "9000"
      + sabRow "2" "Series S01 (2020)" "1"
      + "</table>"

    let archiveBytes =
      archive
        [ "Series.S01E01.srt", "wrong episode"
          "Series.S01E02.srt", "requested episode" ]

    let responses =
      [ "subs.sab.bz/index.php?act=search", Encoding.GetEncoding("windows-1251").GetBytes html
        "attach_id=2", archiveBytes
        "subsunacs.net", Encoding.UTF8.GetBytes "<html></html>"
        "podnapisi.net", Encoding.UTF8.GetBytes "<subs/>" ]

    let factory = ClientFactory responses :> IHttpClientFactory

    let provider =
      BulgarianSubtitleProvider(NullLogger<BulgarianSubtitleProvider>.Instance, factory, null :> ILibraryManager)

    let request = SubtitleSearchRequest()
    request.Language <- "bul"
    request.Name <- "Series"
    request.SeriesName <- "Series"
    request.ParentIndexNumber <- Nullable 1
    request.IndexNumber <- Nullable 2
    request.ProductionYear <- Nullable 2020
    request.MediaPath <- "/media/Series.S01E02.mkv"
    request.ContentType <- VideoContentType.Episode

    let subtitles =
      (provider :> ISubtitleProvider).Search(request, CancellationToken.None).GetAwaiter().GetResult()
      |> Seq.toList

    if subtitles.IsEmpty then
      failwith "Expected season-pack result for episode search"

    let selected =
      subtitles |> List.find (fun result -> result.Name.Contains "Series S01 (2020)")

    equal selected.Id subtitles.Head.Id
    let providerName, _, selectedContext = SubtitleIdentifier.decode selected.Id
    equal "Subs.Sab.Bz" providerName
    equal (Some "Series") (selectedContext |> Option.map _.Title)

    equal
      (Some 2)
      (selectedContext
       |> Option.bind (fun c -> if c.Episode.HasValue then Some c.Episode.Value else None))

    equal (Some "Series.S01E02.mkv") (selectedContext |> Option.map _.FileName)
    request.MediaPath <- "Z:\\media\\Series.S01E02.mkv"

    let repeated =
      (provider :> ISubtitleProvider).Search(request, CancellationToken.None).GetAwaiter().GetResult()
      |> Seq.toList

    equal (subtitles |> List.map _.Id) (repeated |> List.map _.Id)

    let response =
      (provider :> ISubtitleProvider).GetSubtitles(selected.Id, CancellationToken.None).GetAwaiter().GetResult()

    use stream = response.Stream
    equal "requested episode" (text stream)

  let private testProviderRequests () =
    let first = "<table>" + sabRow "123" "Movie" "1" + "</table>"

    let links =
      [ "index.php?act=download&amp;attach_id=123"
        "http://subs.sab.bz/index.php?s=first&amp;act=download&amp;attach_id=123"
        "http://subs.sab.bz/index.php?s=second&amp;act=download&amp;attach_id=123" ]

    for link in links do
      let html = first.Replace("index.php?act=download&amp;attach_id=123", link)
      let result = SabBzImpl.parseSearchResults html |> Seq.exactlyOne

      equal
        (DirectUrl("http://subs.sab.bz/index.php?act=download&attach_id=123", "http://subs.sab.bz/"))
        result.DownloadStrategy

    let provider = YavkaNet() :> IProvider
    equal 65001 provider.ResponseEncoding.CodePage
    use request = provider.CreateSearchRequest "https://yavka.net/search" "Начало"
    let body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult()

    if not (body.Contains("sea=")) || body.StartsWith("s=") then
      failwith "Yavka title field must be sea"

  let runTests () =
    testProviderRequests ()
    testIdentifiers ()
    testArchiveSelection ()
    testSearchAndDownload ()
    printfn "Contextual IDs, episode/release selection, ambiguity and result ordering verified"
