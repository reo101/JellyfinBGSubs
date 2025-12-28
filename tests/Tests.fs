module Jellyfin.Plugin.BulgarianSubs.Tests

open System
open System.Net.Http
open System.Threading
open System.IO.Compression
open Microsoft.Extensions.Logging
open MediaBrowser.Controller.Subtitles
open MediaBrowser.Controller.Library
open Jellyfin.Plugin.BulgarianSubs
open Jellyfin.Plugin.BulgarianSubs.Providers

// ============================================================================
// Simple test doubles
// ============================================================================

type MockLogger<'T>() =
  interface ILogger<'T> with
    member _.BeginScope<'TState>(_state: 'TState) : IDisposable =
      { new IDisposable with
          member _.Dispose() = () }

    member _.IsEnabled(_level) = false
    member _.Log<'TState>(_level, _eventId, _state: 'TState, _exception, _formatter) = ()

let mockLibraryManager () = null :> ILibraryManager

// Mock HTTP responses and requests without opening a network connection.
type MockHttpMessageHandler(responses: (string * byte[]) list, requests: ResizeArray<string>) =
  inherit HttpMessageHandler()

  override _.SendAsync(request: HttpRequestMessage, _cancellationToken: CancellationToken) =
    task {
      let url = request.RequestUri.ToString()
      requests.Add url
      let response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)

      response.Content <-
        match
          responses
          |> List.tryPick (fun (fragment, bytes) -> if url.Contains(fragment) then Some bytes else None)
        with
        | Some bytes -> new ByteArrayContent(bytes)
        | None -> new ByteArrayContent([||])

      return response
    }

type MockHttpClientFactoryWithResponses(responses: (string * byte[]) list, requests: ResizeArray<string>) =
  interface IHttpClientFactory with
    member _.CreateClient(_name: string) =
      new HttpClient(new MockHttpMessageHandler(responses, requests))

let mockHttpFactory responses requests =
  MockHttpClientFactoryWithResponses(responses, requests) :> IHttpClientFactory

let utf8 (value: string) =
  System.Text.Encoding.UTF8.GetBytes(value)


// ============================================================================
// Test utilities
// ============================================================================

let assert' condition message =
  if not condition then
    failwithf "Assertion failed: %s" message

let assertEmpty seq message =
  if Seq.isEmpty seq |> not then
    failwithf "Expected empty sequence but got items: %s" message

let assertNotEmpty seq message =
  if Seq.isEmpty seq then
    failwithf "Expected non-empty sequence: %s" message

let assertEqual actual expected message =
  if actual <> expected then
    failwithf "Expected '%A' but got '%A': %s" expected actual message

// ============================================================================
// Parsing Tests
// ============================================================================

let ``test parseSabBz extracts subtitle info`` () =
  let html =
    """
      <table>
        <tr class="subs-row">
          <td></td>
          <td></td>
          <td></td>
          <td><a href="index.php?act=download&attach_id=12345" onMouseover="">Test Movie (2020)</a></td>
          <td>15 Nov 2020</td>
          <td>BG</td>
          <td>1</td>
          <td>25.0</td>
          <td><a>admin</a></td>
          <td>8.5</td>
          <td>42</td>
        </tr>
      </table>
    """

  let results = SabBzImpl.parseSearchResults html |> Seq.toList

  assertNotEmpty results "parseSearchResults should find results"
  assertEqual results.[0].Id "12345" "ID should match"
  assertEqual results.[0].ProviderName "Subs.Sab.Bz" "Provider should be Sab.Bz"
  assert' (results.[0].Title.Contains("Test Movie")) "Title should contain movie name"

  // Check DownloadStrategy
  match results.[0].DownloadStrategy with
  | DirectUrl(url, referer) ->
    assert' (url.Contains("http://subs.sab.bz/")) "URL should be from Sab.Bz"
    assertEqual referer "http://subs.sab.bz/" "Referer should be set"
  | FormPage _ -> failwith "Sab.Bz should use DirectUrl"

  printfn "✓ parseSabBz extracts subtitle info"

let ``test parseSabBz handles missing links`` () =
  let html =
    """
    <table>
      <tr class="subs-row">
        <td></td>
        <td></td>
        <td></td>
        <td>Movie Title</td>
        <td>15 Nov 2020</td>
        <td>BG</td>
        <td>1</td>
        <td>25.0</td>
        <td>admin</td>
        <td>8.5</td>
        <td>0</td>
      </tr>
    </table>
  """

  let results = SabBzImpl.parseSearchResults html |> Seq.toList

  assertEmpty results "Should return empty on no download link"
  printfn "✓ parseSabBz handles missing links"

let ``test parseSubsunacs extracts info`` () =
  let html =
    """<table><tbody>
      <tr>
        <td>1</td>
        <td class="tdMovie"><a href="/subtitles/Test-Movie-67890/" title="Дата: &lt;/b&gt;15 Nov 2020">Test Movie</a></td>
        <td>25.00</td>
        <td>8.5</td>
        <td></td>
        <td><a>admin</a></td>
        <td>1234</td>
      </tr>
    </tbody></table>"""

  let results = SubsunacsImpl.parseSearchResults html |> Seq.toList

  assertNotEmpty results "parseSearchResults should find results"
  assertEqual results.[0].Id "67890" "ID should match"
  assertEqual results.[0].ProviderName "Subsunacs" "Provider should be Subsunacs"
  assertEqual results.[0].Title "Test Movie" "Title should match"

  // Check DownloadStrategy
  match results.[0].DownloadStrategy with
  | DirectUrl(url, referer) ->
    assert' (url.Contains("subsunacs.net")) "URL should be from Subsunacs"
    assertEqual referer "https://subsunacs.net/search.php" "Referer should be set"
  | FormPage _ -> failwith "Subsunacs should use DirectUrl"

  printfn "✓ parseSubsunacs extracts info"

let ``test parseSubsunacs returns empty on no matches`` () =
  let html = "<div>No subtitles here</div>"

  let results = SubsunacsImpl.parseSearchResults html |> Seq.toList

  assertEmpty results "Should return empty"
  printfn "✓ parseSubsunacs returns empty on no matches"

let ``test parseSabBz normalizes URLs`` () =
  let html =
    """
    <table>
      <tr class="subs-row">
        <td></td>
        <td></td>
        <td></td>
        <td><a href="index.php?act=download&attach_id=999" onMouseover="">Another Movie (2020)</a></td>
        <td>20 Nov 2020</td>
        <td>BG</td>
        <td>1</td>
        <td>24.0</td>
        <td><a>user123</a></td>
        <td>7.8</td>
        <td>105</td>
      </tr>
    </table>
  """

  let results = SabBzImpl.parseSearchResults html |> Seq.toList

  assertNotEmpty results "Should find results"

  match results.[0].DownloadStrategy with
  | DirectUrl(url, _) -> assert' (url.StartsWith("http://subs.sab.bz/")) "URL should be normalized"
  | FormPage _ -> failwith "Should be DirectUrl"

  printfn "✓ parseSabBz normalizes URLs"

// ============================================================================
// Provider Tests
// ============================================================================

let ``test provider name`` () =
  let logger = MockLogger<BulgarianSubtitleProvider>()
  let factory = mockHttpFactory [] (ResizeArray<string>())
  let libraryManager = mockLibraryManager ()

  let provider = BulgarianSubtitleProvider(logger, factory, libraryManager)

  let providerInterface = provider :> ISubtitleProvider
  assertEqual providerInterface.Name "Bulgarian Subtitles" "Provider name should remain stable"
  printfn "✓ Provider name is correct"

let ``test supported media types`` () =
  let logger = MockLogger<BulgarianSubtitleProvider>()
  let factory = mockHttpFactory [] (ResizeArray<string>())
  let libraryManager = mockLibraryManager ()

  let provider = BulgarianSubtitleProvider(logger, factory, libraryManager)

  let providerInterface = provider :> ISubtitleProvider
  let mediaTypes = providerInterface.SupportedMediaTypes |> Seq.toList

  assertEqual mediaTypes.Length 2 "Should support 2 media types"
  assert' (List.contains MediaBrowser.Controller.Providers.VideoContentType.Movie mediaTypes) "Should support movies"

  assert'
    (List.contains MediaBrowser.Controller.Providers.VideoContentType.Episode mediaTypes)
    "Should support episodes"

  printfn "✓ Supported media types include movies and episodes"

// ============================================================================
// Archive Extraction Integration Tests (Real Archives)
// ============================================================================

open System.IO

let ``test plain text SRT stream returns None`` () =
  // Plain SRT file should pass through as-is, not treated as archive
  let srtContent = "1\n00:00:01,000 --> 00:00:03,000\nTest subtitle\n"
  let srtBytes = System.Text.Encoding.UTF8.GetBytes(srtContent)
  use ms = new MemoryStream(srtBytes)

  // Use the actual library function to detect archives
  let archiveFormat = Common.detectArchiveFormat ms

  match archiveFormat with
  | None ->
    assertEqual ms.Position 0L "Stream position should be reset to 0"
    printfn "✓ Plain text SRT file correctly detected as non-archive"
  | Some fmt -> failwithf "Plain text should not be detected as archive, got %A" fmt

let ``test ZIP magic detection returns ZIP`` () =
  // Create a fake ZIP (just the magic bytes)
  let zipMagic = [| 0x50uy; 0x4Buy; 0x03uy; 0x04uy |]
  use ms = new MemoryStream(zipMagic)

  let archiveFormat = Common.detectArchiveFormat ms

  match archiveFormat with
  | Some Common.ArchiveFormat.ZIP -> printfn "✓ ZIP magic bytes correctly detected as ZIP format"
  | _ -> failwith "ZIP magic bytes should be detected as ZIP"

let ``test RAR magic detection returns RAR`` () =
  let rarMagic = [| 0x52uy; 0x61uy; 0x72uy; 0x21uy |]
  use ms = new MemoryStream(rarMagic)

  let archiveFormat = Common.detectArchiveFormat ms

  match archiveFormat with
  | Some Common.ArchiveFormat.RAR -> printfn "✓ RAR magic bytes correctly detected as RAR format"
  | _ -> failwith "RAR magic bytes should be detected as RAR"

let ``test isSubtitleFile recognizes SRT files`` () =
  let srtFiles = [ "subtitle.srt"; "SUBTITLE.SRT"; "movie.sub"; "MOVIE.SUB" ]

  let allAreSubtitles = srtFiles |> List.forall Common.isSubtitleFile

  assert' allAreSubtitles "All .srt and .sub files should be recognized"
  printfn "✓ isSubtitleFile correctly identifies subtitle extensions"

let ``test isSubtitleFile rejects non-subtitle files`` () =
  let nonSubtitleFiles = [ "readme.txt"; "image.jpg"; "archive.zip"; "script.exe" ]

  let noneAreSubtitles =
    nonSubtitleFiles |> List.forall (fun f -> not (Common.isSubtitleFile f))

  assert' noneAreSubtitles "Non-subtitle files should not be recognized"
  printfn "✓ isSubtitleFile correctly rejects non-subtitle files"

let ``test finding first subtitle file in list`` () =
  // When archive has multiple files, should find first .srt or .sub
  let archiveFiles =
    seq {
      yield "readme.txt"
      yield "image.jpg"
      yield "subtitle.srt"
      yield "another.sub"
    }

  let subtitleFile = archiveFiles |> Seq.tryFind Common.isSubtitleFile

  match subtitleFile with
  | Some file ->
    assertEqual file "subtitle.srt" "Should find first subtitle file"
    printfn "✓ Correctly identifies first subtitle file in archive"
  | None -> failwith "Should have found subtitle file"

let ``test extension extraction from archive entry`` () =
  // Test extracting extension from archive file names
  let fileName = "movie.subtitle.srt"
  let ext = Path.GetExtension(fileName).TrimStart('.')

  assertEqual ext "srt" "Should extract srt extension"
  assert' (not (ext.StartsWith("."))) "Extension should not contain dot"
  printfn "✓ Extension correctly extracted and trimmed from archive entry"

// ============================================================================
// GetSubtitles Method Tests
// ============================================================================

// ============================================================================
// TV Show / Episode Search Tests (Integration)
// ============================================================================

// ============================================================================
// Integration Tests with Mocked HTTP Responses
// ============================================================================

let ``test GetSubtitles downloads plain SRT and ZIP using encoded IDs`` () =
  let zipBytes =
    use buffer = new MemoryStream()

    do
      use archive = new ZipArchive(buffer, ZipArchiveMode.Create, true)
      let entry = archive.CreateEntry("subtitle.srt")
      use writer = new StreamWriter(entry.Open(), System.Text.Encoding.UTF8)
      writer.Write("1\n00:00:01,000 --> 00:00:03,000\nFrom ZIP\n")

    buffer.ToArray()

  let getSubtitle bytes (url: string) =
    let logger = MockLogger<BulgarianSubtitleProvider>()
    let factory = mockHttpFactory [ "subs.sab.bz", bytes ] (ResizeArray<string>())

    let provider =
      BulgarianSubtitleProvider(logger, factory, mockLibraryManager ()) :> ISubtitleProvider

    let encodedUrl = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url))

    provider.GetSubtitles($"Subs.Sab.Bz::{encodedUrl}", CancellationToken.None)
    |> Async.AwaitTask
    |> Async.RunSynchronously

  let plain = "1\n00:00:01,000 --> 00:00:03,000\nPlain SRT\n"
  let plainResponse = getSubtitle (utf8 plain) "http://subs.sab.bz/plain.srt"
  use plainReader = new StreamReader(plainResponse.Stream)
  assertEqual (plainReader.ReadToEnd()) plain "Plain SRT should pass through"
  assertEqual plainResponse.Format "srt" "Plain subtitle format should be returned"

  let zipResponse = getSubtitle zipBytes "http://subs.sab.bz/archive.zip"
  use zipReader = new StreamReader(zipResponse.Stream)
  assert' ((zipReader.ReadToEnd()).Contains("From ZIP")) "ZIP subtitle should be extracted"
  printfn "✓ GetSubtitles downloads plain SRT and extracts ZIP using Name::base64-url IDs"

let ``test invalid subtitle IDs and caller cancellation make no requests`` () =
  let requests = ResizeArray<string>()

  let provider =
    BulgarianSubtitleProvider(
      MockLogger<BulgarianSubtitleProvider>(),
      mockHttpFactory [] requests,
      mockLibraryManager ()
    )
    :> ISubtitleProvider

  let identifier url =
    "Subs.Sab.Bz::" + Convert.ToBase64String(utf8 url)

  for invalid in
    [ null
      ""
      "missing-separator"
      "Unknown::aHR0cDovL3N1YnMuc2FiLmJ6Lw=="
      "Subs.Sab.Bz::not-base64"
      identifier "http://localhost/file"
      identifier "http://subs.sab.bz:8096/file"
      identifier "ftp://subs.sab.bz/file" ] do
    let mutable rejected = false

    try
      let response =
        provider.GetSubtitles(invalid, CancellationToken.None).GetAwaiter().GetResult()

      response.Stream.Dispose()
    with
    | :? ArgumentException
    | :? InvalidDataException -> rejected <- true

    assert' rejected "Invalid subtitle identifier must be rejected"

  use cancellation = new CancellationTokenSource()
  cancellation.Cancel()
  let request = SubtitleSearchRequest(Language = "bul", Name = "Fixture")

  for operation in
    [ (fun () -> provider.Search(request, cancellation.Token).GetAwaiter().GetResult() |> ignore)
      (fun () ->
        provider.GetSubtitles(identifier "http://subs.sab.bz/file", cancellation.Token).GetAwaiter().GetResult()
        |> ignore) ] do
    let mutable canceled = false

    try
      operation ()
    with :? OperationCanceledException ->
      canceled <- true

    assert' canceled "Caller cancellation must propagate"

  assertEqual requests.Count 0 "Invalid IDs and canceled operations must not perform network requests"

let ``test Bulgarian date parsing handles UTC boundaries`` () =
  let cases =
    [ "15 Jan 2020", DateTime(2020, 1, 14, 22, 0, 0, DateTimeKind.Utc)
      "10 Feb 2020", DateTime(2020, 2, 9, 22, 0, 0, DateTimeKind.Utc)
      "15 Nov 2020", DateTime(2020, 11, 14, 22, 0, 0, DateTimeKind.Utc)
      "15 Dec 2020", DateTime(2020, 12, 14, 22, 0, 0, DateTimeKind.Utc)
      "15 Jul 2020", DateTime(2020, 7, 14, 21, 0, 0, DateTimeKind.Utc) ]

  for input, expected in cases do
    assertEqual (Parsing.tryParseBulgarianDate input) (Some expected) $"Date parse for {input}"

  assertEqual (Parsing.tryParseBulgarianDate "not a date") None "Invalid date should return None"
  assertEqual (Parsing.tryParseBulgarianDate "") None "Empty date should return None"
  printfn "✓ Bulgarian local-midnight dates convert to the correct UTC instant"

let ``test Search returns empty for non-Bulgarian language`` () =
  let logger = MockLogger<BulgarianSubtitleProvider>()
  let requests = ResizeArray<string>()
  let factory = mockHttpFactory [] requests
  let libraryManager = mockLibraryManager ()
  let provider = BulgarianSubtitleProvider(logger, factory, libraryManager)
  let providerInterface = provider :> ISubtitleProvider

  let request = SubtitleSearchRequest()
  request.Language <- "en"
  request.Name <- "Inception"

  let results =
    providerInterface.Search(request, CancellationToken.None)
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> Seq.toList

  assertEmpty results "Non-Bulgarian language should return empty"
  assertEqual requests.Count 0 "Unsupported language must not make HTTP requests"
  printfn "✓ Search returns empty without network requests for non-Bulgarian language"

let ``test Search with mocked Sab.Bz response`` () =
  let sabBzHtml =
    """
    <table>
      <tr class="subs-row">
        <td></td>
        <td></td>
        <td></td>
        <td><a href="index.php?act=download&attach_id=12345" onMouseover="">Inception (2010)</a></td>
        <td>14 Nov 2010</td>
        <td>BG</td>
        <td>1</td>
        <td>25.0</td>
        <td><a>user1</a></td>
        <td>8.5</td>
        <td>150</td>
      </tr>
      <tr class="subs-row">
        <td></td>
        <td></td>
        <td></td>
        <td><a href="index.php?act=download&attach_id=67890" onMouseover="">Inception 2 (2012)</a></td>
        <td>20 Jul 2012</td>
        <td>BG</td>
        <td>1</td>
        <td>24.0</td>
        <td><a>user2</a></td>
        <td>7.9</td>
        <td>80</td>
      </tr>
    </table>
    """

  let responses =
    [ "subs.sab.bz", utf8 sabBzHtml
      "subsunacs.net", utf8 "<div></div>"
      "podnapisi.net", utf8 "<subtitles></subtitles>" ]

  let logger = MockLogger<BulgarianSubtitleProvider>()
  let factory = mockHttpFactory responses (ResizeArray<string>())
  let libraryManager = mockLibraryManager ()
  let provider = BulgarianSubtitleProvider(logger, factory, libraryManager)
  let providerInterface = provider :> ISubtitleProvider

  let request = SubtitleSearchRequest()
  request.Language <- "bg"
  request.Name <- "Inception"
  request.ProductionYear <- Nullable(2010)

  let results =
    providerInterface.Search(request, CancellationToken.None)
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> Seq.toList

  assertNotEmpty results "Should find results from Sab.Bz"
  assertEqual results.Length 2 "Should find 2 results"
  assert' (results.[0].Name.Contains("Inception")) "First result should be Inception"
  assert' (results.[0].Id.StartsWith("Subs.Sab.Bz::")) "ID should identify Sab.Bz"
  assertEqual results.[0].ProviderName providerInterface.Name "Jellyfin download routing must use the registered name"
  printfn "✓ Search correctly parses Sab.Bz mocked response"

let ``test Search with mocked Subsunacs response`` () =
  let subsunacHtml =
    """
    <tbody>
      <tr>
        <td>1</td>
        <td class="tdMovie"><a href="/subtitles/The-Matrix-99999/" title="Дата: &lt;/b&gt;15 Nov 2020">The Matrix (1999)</a></td>
        <td>25.00</td>
        <td>8.7</td>
        <td></td>
        <td><a>admin</a></td>
        <td>250</td>
      </tr>
    </tbody>
    """

  let responses =
    [ "subs.sab.bz", utf8 "<div></div>"
      "subsunacs.net", utf8 subsunacHtml
      "podnapisi.net", utf8 "<subtitles></subtitles>" ]

  let logger = MockLogger<BulgarianSubtitleProvider>()
  let factory = mockHttpFactory responses (ResizeArray<string>())
  let libraryManager = mockLibraryManager ()
  let provider = BulgarianSubtitleProvider(logger, factory, libraryManager)
  let providerInterface = provider :> ISubtitleProvider

  let request = SubtitleSearchRequest()
  request.Language <- "bg"
  request.Name <- "The Matrix"
  request.ProductionYear <- Nullable(1999)

  let results =
    providerInterface.Search(request, CancellationToken.None)
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> Seq.toList

  assertNotEmpty results "Should find results from Subsunacs"
  assertEqual results.Length 1 "Should find 1 result"
  assert' (results.[0].Id.StartsWith("Subsunacs::")) "ID should identify Subsunacs"
  printfn "✓ Search correctly parses Subsunacs mocked response"

let ``test Search with both providers returning results`` () =
  let sabBzHtml =
    """
    <table>
      <tr class="subs-row">
        <td></td>
        <td></td>
        <td></td>
        <td><a href="index.php?act=download&attach_id=111" onMouseover="">Movie (2020)</a></td>
        <td>15 Nov 2020</td>
        <td>BG</td>
        <td>1</td>
        <td>25.0</td>
        <td><a>user1</a></td>
        <td>8.5</td>
        <td>95</td>
      </tr>
    </table>
    """

  let subsunacHtml =
    """
    <tbody>
      <tr>
        <td>1</td>
        <td class="tdMovie"><a href="/subtitles/Movie-222/" title="Дата: &lt;/b&gt;16 Nov 2020">Movie (2020)</a></td>
        <td>25.00</td>
        <td>8.6</td>
        <td></td>
        <td><a>admin</a></td>
        <td>120</td>
      </tr>
    </tbody>
    """

  let responses =
    [ "subs.sab.bz", utf8 sabBzHtml
      "subsunacs.net", utf8 subsunacHtml
      "podnapisi.net", utf8 "<subtitles></subtitles>" ]

  let logger = MockLogger<BulgarianSubtitleProvider>()
  let factory = mockHttpFactory responses (ResizeArray<string>())
  let libraryManager = mockLibraryManager ()
  let provider = BulgarianSubtitleProvider(logger, factory, libraryManager)
  let providerInterface = provider :> ISubtitleProvider

  let request = SubtitleSearchRequest()
  request.Language <- "bg"
  request.Name <- "Movie"
  request.ProductionYear <- Nullable(2020)

  let results =
    providerInterface.Search(request, CancellationToken.None)
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> Seq.toList

  assertNotEmpty results "Should find results from both providers"
  assertEqual results.Length 2 "Should find 2 results (1 from each provider)"
  let hasSabBz = results |> List.exists (fun r -> r.Id.StartsWith("Subs.Sab.Bz::"))
  let hasSubsunacs = results |> List.exists (fun r -> r.Id.StartsWith("Subsunacs::"))
  assert' hasSabBz "Should have Sab.Bz result"
  assert' hasSubsunacs "Should have Subsunacs result"
  printfn "✓ Search correctly combines results from both providers"

let ``test Search filters exact episode and deduplicates variations`` () =
  let html =
    """
    <table>
      <tr class="subs-row"><td></td><td></td><td></td><td><a href="index.php?act=download&attach_id=101">Breaking Bad S01E01</a></td><td>15 Nov 2020</td><td>BG</td><td>1</td><td>25.0</td><td><a>a</a></td><td>8.5</td><td>20</td></tr>
      <tr class="subs-row"><td></td><td></td><td></td><td><a href="index.php?act=download&attach_id=102">Breaking Bad 1x1</a></td><td>15 Nov 2020</td><td>BG</td><td>1</td><td>25.0</td><td><a>b</a></td><td>8.5</td><td>90</td></tr>
      <tr class="subs-row"><td></td><td></td><td></td><td><a href="index.php?act=download&attach_id=103">Breaking Bad S01E010</a></td><td>15 Nov 2020</td><td>BG</td><td>1</td><td>25.0</td><td><a>c</a></td><td>8.5</td><td>100</td></tr>
    </table>
    """

  let requests = ResizeArray<string>()

  let responses =
    [ "subs.sab.bz", utf8 html
      "subsunacs.net", utf8 "<div></div>"
      "podnapisi.net", utf8 "<subtitles></subtitles>" ]

  let provider =
    BulgarianSubtitleProvider(
      MockLogger<BulgarianSubtitleProvider>(),
      mockHttpFactory responses requests,
      mockLibraryManager ()
    )
    :> ISubtitleProvider

  let request = SubtitleSearchRequest()
  request.Language <- "bg"
  request.Name <- "Breaking Bad"
  request.SeriesName <- "Breaking Bad"
  request.ParentIndexNumber <- Nullable(1)
  request.IndexNumber <- Nullable(1)

  let results =
    provider.Search(request, CancellationToken.None)
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> Seq.toList

  assertEqual results.Length 2 "Repeated search variations should yield two unique exact-episode results"
  assertEqual results.[0].DownloadCount.Value 90 "Results should sort by download count descending"

  assert'
    (results |> List.forall (fun result -> not (result.Name.Contains("S01E010"))))
    "Episode 10 must not match episode 1"

  assertEqual
    (results |> List.map (fun result -> result.Id) |> List.distinct |> List.length)
    2
    "Duplicate IDs should be removed"

  printfn "✓ Search filters episode boundaries, sorts, and deduplicates search variations"

let ``test Search accepts Bulgarian language codes`` () =
  for language in [ "bg"; "bul"; "Bulgarian" ] do
    let requests = ResizeArray<string>()

    let provider =
      BulgarianSubtitleProvider(
        MockLogger<BulgarianSubtitleProvider>(),
        mockHttpFactory [] requests,
        mockLibraryManager ()
      )
      :> ISubtitleProvider

    let request = SubtitleSearchRequest()
    request.Language <- language
    request.Name <- "Example"

    let results =
      provider.Search(request, CancellationToken.None)
      |> Async.AwaitTask
      |> Async.RunSynchronously

    assertEmpty results $"Empty mock results expected for {language}"
    assert' (requests.Count > 0) $"Bulgarian language code {language} should perform search"

  printfn "✓ Search accepts ISO-639-1, ISO-639-2, and language-name Bulgarian codes"

let ``test Search decodes UTF8 Podnapisi XML and Windows-1251 provider HTML`` () =
  let requests = ResizeArray<string>()

  let podnapisiXml =
    "<subtitles><subtitle><pid>utf8-1</pid><release>Филм</release><downloads>7</downloads><url>/subtitles/utf8-1</url></subtitle></subtitles>"

  let sabHtml =
    """<table><tr class="subs-row"><td></td><td></td><td></td><td><a href="index.php?act=download&attach_id=201">Филм</a></td><td>15 Nov 2020</td><td>BG</td><td>1</td><td>25.0</td><td><a>user</a></td><td>8.5</td><td>8</td></tr></table>"""

  let subsunacsHtml =
    """<tbody><tr><td>1</td><td class="tdMovie"><a href="/subtitles/Film-202/">Филм</a></td><td>25.00</td><td>8.7</td><td></td><td><a>admin</a></td><td>23</td></tr></tbody>"""

  let cp1251 = System.Text.Encoding.GetEncoding(1251)

  let responses =
    [ "subs.sab.bz", cp1251.GetBytes sabHtml
      "subsunacs.net", cp1251.GetBytes subsunacsHtml
      "podnapisi.net", utf8 podnapisiXml ]

  let provider =
    BulgarianSubtitleProvider(
      MockLogger<BulgarianSubtitleProvider>(),
      mockHttpFactory responses requests,
      mockLibraryManager ()
    )
    :> ISubtitleProvider

  let request = SubtitleSearchRequest()
  request.Language <- "bg"
  request.Name <- "Филм"

  let results =
    provider.Search(request, CancellationToken.None)
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> Seq.toList

  assert'
    (results |> List.exists (fun result -> result.Name.Contains("Филм")))
    "UTF-8 XML and cp1251 HTML should retain Cyrillic text"

  assert'
    (results |> List.exists (fun result -> result.Id.StartsWith("Podnapisi.net::")))
    "UTF-8 Podnapisi result should be present"

  assert'
    (results |> List.exists (fun result -> result.Id.StartsWith("Subs.Sab.Bz::")))
    "cp1251 Sab result should be present"

  assert'
    (results |> List.exists (fun result -> result.Id.StartsWith("Subsunacs::")))
    "cp1251 Subsunacs result should be present"

  printfn "✓ Search decodes UTF-8 XML and Windows-1251 Sab/Subsunacs HTML"

// ============================================================================
// Main test runner
// ============================================================================

let runAllTests () =
  try
    // Parsing tests
    ``test parseSabBz extracts subtitle info`` ()
    ``test parseSabBz handles missing links`` ()
    ``test parseSubsunacs extracts info`` ()
    ``test parseSubsunacs returns empty on no matches`` ()
    ``test parseSabBz normalizes URLs`` ()

    // Provider tests
    ``test provider name`` ()
    ``test supported media types`` ()

    // Archive extraction integration tests
    ``test plain text SRT stream returns None`` ()
    ``test ZIP magic detection returns ZIP`` ()
    ``test RAR magic detection returns RAR`` ()
    ``test isSubtitleFile recognizes SRT files`` ()
    ``test isSubtitleFile rejects non-subtitle files`` ()
    ``test finding first subtitle file in list`` ()
    ``test extension extraction from archive entry`` ()

    // GetSubtitles method tests
    ``test GetSubtitles downloads plain SRT and ZIP using encoded IDs`` ()
    ``test invalid subtitle IDs and caller cancellation make no requests`` ()
    ``test Bulgarian date parsing handles UTC boundaries`` ()

    // TV show / episode search tests (integration)
    ``test Search filters exact episode and deduplicates variations`` ()
    ``test Search accepts Bulgarian language codes`` ()

    // Integration tests with mocked HTTP responses
    ``test Search returns empty for non-Bulgarian language`` ()
    ``test Search with mocked Sab.Bz response`` ()
    ``test Search with mocked Subsunacs response`` ()
    ``test Search with both providers returning results`` ()
    ``test Search decodes UTF8 Podnapisi XML and Windows-1251 provider HTML`` ()

    // Download trust boundaries and resource limits
    DownloadSecurityTests.runTests ()

    // Metadata extractor tests
    MetadataExtractorTests.runTests ()

    printfn "\n✅ All unit tests passed!"
    0
  with ex ->
    printfn "\n❌ Test failed: %s" ex.Message
    1

let runDownloadTests () =
  DownloadIntegrationTests.runTests ()
  |> Async.AwaitTask
  |> Async.RunSynchronously

[<EntryPoint>]
let main argv =
  try
    match argv |> Array.toList with
    | [ "--fixture" ] ->
      JellyfinIntegrationTests.serveFixture ()
      |> fun task -> task.GetAwaiter().GetResult()
    | [ "--vm"; version ] ->
      JellyfinIntegrationTests.runTests version
      |> fun task -> task.GetAwaiter().GetResult()
    | [ "--vm-restart" ] ->
      JellyfinIntegrationTests.verifyRestart ()
      |> fun task -> task.GetAwaiter().GetResult()
    | [ "--lsp" ] ->
      FsAutocompleteTests.runTests "fsautocomplete"
      |> fun task -> task.GetAwaiter().GetResult()
    | [ "--lsp"; server ] -> FsAutocompleteTests.runTests server |> fun task -> task.GetAwaiter().GetResult()
    | [ "--download" ] -> runDownloadTests ()
    | [ "--all" ] ->
      let unitResult = runAllTests ()
      if unitResult <> 0 then unitResult else runDownloadTests ()
    | _ -> runAllTests ()
  with ex ->
    eprintfn "Test failed: %s" ex.Message
    1
