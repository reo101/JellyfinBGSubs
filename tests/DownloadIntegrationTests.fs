module Jellyfin.Plugin.BulgarianSubs.DownloadIntegrationTests

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Text
open System.Text.RegularExpressions
open Microsoft.Extensions.Logging
open Jellyfin.Plugin.BulgarianSubs.Providers

// ============================================================================
// Test Infrastructure
// ============================================================================

type TestLogger() =
  interface ILogger with
    member _.BeginScope<'TState>(_state: 'TState) : IDisposable =
      { new IDisposable with
          member _.Dispose() = () }

    member _.IsEnabled(_level) = true

    member _.Log<'TState>(level, _eventId, state: 'TState, _exception, formatter) =
      let message = formatter.Invoke(state, _exception)

      let levelStr =
        match level with
        | LogLevel.Error -> "ERR"
        | LogLevel.Warning -> "WRN"
        | LogLevel.Information -> "INF"
        | LogLevel.Debug -> "DBG"
        | _ -> "???"

      printfn "  [%s] %s" levelStr message

let assert' condition message =
  if not condition then
    failwithf "Assertion failed: %s" message

let assertEqual actual expected message =
  if actual <> expected then
    failwithf "Expected '%A' but got '%A': %s" expected actual message

let httpClient =
  Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
  let handler = new HttpClientHandler(AllowAutoRedirect = false)
  let client = new HttpClient(handler)
  client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36")
  client

let logger = TestLogger() :> ILogger

// ============================================================================
// Download and Extract Tests
// ============================================================================

let private movieContext: SubtitleSelectionContext =
  { Title = "Inception"
    Year = Nullable 2010
    Season = Nullable()
    Episode = Nullable()
    FileName = "Inception.2010.mkv" }

let private episodeContext: SubtitleSelectionContext =
  { Title = "La Casa De Papel"
    Year = Nullable()
    Season = Nullable 1
    Episode = Nullable 1
    FileName = "La.Casa.De.Papel.S01E01.720p.SunshineStudio.mkv" }

let downloadAndExtract context (url: string) (referer: string) =
  task {
    printfn "    Downloading from: %s" url
    use request = new HttpRequestMessage(HttpMethod.Get, url)
    request.Headers.Referrer <- Uri(referer)

    use! response = Common.sendWithRedirects httpClient request CancellationToken.None
    response.EnsureSuccessStatusCode() |> ignore

    assert'
      (response.Content.Headers.ContentType = null
       || response.Content.Headers.ContentType.MediaType <> "text/html")
      "Download returned an HTML page rather than subtitles"

    use! responseStream = response.Content.ReadAsStreamAsync(CancellationToken.None)
    use ms = new MemoryStream()
    do! responseStream.CopyToAsync(ms, CancellationToken.None)
    ms.Position <- 0L

    printfn "    Downloaded %d bytes" ms.Length

    let (subtitleStream, ext) =
      Common.extractSubtitleStreamWithContext logger context ms "srt"

    if String.Equals(ext, "srt", StringComparison.OrdinalIgnoreCase) then
      use reader = new StreamReader(subtitleStream, Encoding.UTF8, true, 1024, true)
      let content = reader.ReadToEnd()
      subtitleStream.Position <- 0L

      if not (Regex.IsMatch(content, @"(?m)^\s*[0-9]{2}:[0-9]{2}:[0-9]{2}[,.][0-9]{3}\s*-->")) then
        subtitleStream.Dispose()
        failwith "Downloaded SRT contains no subtitle timing cues"

    return (subtitleStream, ext, ms.Length)
  }

// ============================================================================
// Subsunacs Tests (7z archives with Zstd compression)
// ============================================================================

let ``test Subsunacs download - 7z archive`` () =
  task {
    printfn "\n  Testing Subsunacs 7z download..."

    // La Casa De Papel S01E01 - known to be a 7z archive
    let url = "https://subsunacs.net/subtitles/La_Casa_De_Papel_01x01-125257/"
    let referer = "https://subsunacs.net/search.php"

    let! (subtitleStream, ext, downloadSize) = downloadAndExtract (Some episodeContext) url referer

    assert' (downloadSize > 0L) "Should download some bytes"
    assert' (subtitleStream.Length > 0L) $"Subtitle stream should not be empty, got {subtitleStream.Length} bytes"
    assertEqual ext "srt" "Extension should be srt"

    // Verify it's valid SRT content
    use reader =
      new StreamReader(subtitleStream, System.Text.Encoding.UTF8, true, 1024, true)

    let firstLine = reader.ReadLine()
    subtitleStream.Position <- 0L

    assert' (not (String.IsNullOrWhiteSpace(firstLine))) "SRT should have content"

    printfn
      "    First line: %s"
      (if firstLine.Length > 50 then
         firstLine.Substring(0, 50) + "..."
       else
         firstLine)

    printfn "    ✓ Subsunacs 7z archive extracted successfully (%d bytes)" subtitleStream.Length
  }

let ``test Subsunacs download - movie subtitle`` () =
  task {
    printfn "\n  Testing Subsunacs movie download..."

    // Inception - a movie subtitle
    let! bytes = httpClient.GetByteArrayAsync("https://subsunacs.net/search.php?m=Inception&y=2010&t=Submit")

    let results =
      Encoding.GetEncoding(1251).GetString(bytes)
      |> SubsunacsImpl.parseSearchResults
      |> Seq.toList

    assert' (not results.IsEmpty) "Subsunacs search returned no movie results"

    let url =
      match results.Head.DownloadStrategy with
      | DirectUrl(url, _) -> url
      | _ -> failwith "Expected direct Subsunacs download"

    let referer = "https://subsunacs.net/search.php"

    let! (subtitleStream, ext, downloadSize) = downloadAndExtract (Some movieContext) url referer

    assert' (downloadSize > 0L) "Should download some bytes"
    assert' (subtitleStream.Length > 0L) $"Subtitle stream should not be empty, got {subtitleStream.Length} bytes"
    assert' (ext = "srt" || ext = "sub") $"Extension should be srt or sub, got {ext}"

    printfn "    ✓ Subsunacs movie subtitle extracted successfully (%d bytes, .%s)" subtitleStream.Length ext
  }

// ============================================================================
// Subs.Sab.Bz Tests (ZIP/RAR archives)
// ============================================================================

let ``test SabBz download - ZIP archive`` () =
  task {
    printfn "\n  Testing Sab.Bz download..."

    // Need to find a known working subtitle ID from Sab.Bz
    // Using a search first to get a valid ID
    let searchUrl = "http://subs.sab.bz/index.php?act=search&movie=Inception"
    let request = new HttpRequestMessage(HttpMethod.Get, searchUrl)
    request.Headers.Add("User-Agent", "Mozilla/5.0")

    use! searchResponse = httpClient.SendAsync(request, CancellationToken.None)
    searchResponse.EnsureSuccessStatusCode() |> ignore
    let! searchHtml = searchResponse.Content.ReadAsStringAsync()

    // Parse to find a download link
    let results = SabBzImpl.parseSearchResults searchHtml |> Seq.toList

    if results.Length = 0 then
      failwith "Sab.Bz search returned no results"
    else
      let firstResult = results.[0]
      printfn "    Found: %s" firstResult.Title

      match firstResult.DownloadStrategy with
      | DirectUrl(url, referer) ->
        let! (subtitleStream, ext, downloadSize) = downloadAndExtract (Some movieContext) url referer

        assert' (downloadSize > 0L) "Should download some bytes"
        assert' (subtitleStream.Length > 0L) $"Subtitle stream should not be empty, got {subtitleStream.Length} bytes"
        assert' (ext = "srt" || ext = "sub") $"Extension should be srt or sub, got {ext}"

        printfn "    ✓ Sab.Bz subtitle extracted successfully (%d bytes, .%s)" subtitleStream.Length ext
      | FormPage _ -> printfn "    ⚠ Sab.Bz returned FormPage strategy, skipping"
  }

// ============================================================================
// Yavka.net Tests (Form-based downloads)
// ============================================================================

let ``test YavkaNet download - form submission`` () =
  task {
    printfn "\n  Testing Yavka.net form-based download..."

    // Search for something on Yavka
    let searchUrl = "https://yavka.net/search"
    let provider = YavkaNet() :> IProvider
    use searchRequest = provider.CreateSearchRequest searchUrl "Inception"

    use! searchResponse = httpClient.SendAsync(searchRequest, CancellationToken.None)
    searchResponse.EnsureSuccessStatusCode() |> ignore
    let! searchBytes = searchResponse.Content.ReadAsByteArrayAsync()
    let encoding = provider.ResponseEncoding
    let searchHtml = encoding.GetString(searchBytes)

    let results = YavkaNetImpl.parseSearchResults searchHtml |> Seq.toList

    if results.Length = 0 then
      failwith "Yavka search returned no results"
    else
      let firstResult = results.[0]
      printfn "    Found: %s" firstResult.Title
      printfn "    Page URL: %s" firstResult.PageUrl

      // Test the form-based download via executeStrategy
      let strategy = FormPage(firstResult.PageUrl, "https://yavka.net/")

      let! (subtitleStream, ext) =
        Common.executeStrategy
          strategy
          httpClient
          CancellationToken.None
          (Common.extractSubtitleStreamWithLogging logger)

      assert' (subtitleStream.Length > 0L) $"Subtitle stream should not be empty, got {subtitleStream.Length} bytes"
      assert' (ext = "srt" || ext = "sub") $"Extension should be srt or sub, got {ext}"

      printfn "    ✓ Yavka.net form-based download extracted successfully (%d bytes, .%s)" subtitleStream.Length ext
  }

// ============================================================================
// Archive Format Detection Tests (with real data)
// ============================================================================

let ``test archive format detection with real 7z`` () =
  task {
    printfn "\n  Testing 7z format detection with real archive..."

    let url = "https://subsunacs.net/subtitles/La_Casa_De_Papel_01x01-125257/"
    let request = new HttpRequestMessage(HttpMethod.Get, url)
    request.Headers.Referrer <- Uri("https://subsunacs.net/search.php")
    request.Headers.Add("User-Agent", "Mozilla/5.0")

    let! response = httpClient.SendAsync(request, CancellationToken.None)
    let! responseStream = response.Content.ReadAsStreamAsync()
    let ms = new MemoryStream()
    do! responseStream.CopyToAsync(ms)
    ms.Position <- 0L

    let format = Common.detectArchiveFormat ms

    match format with
    | Some Common.ArchiveFormat.SevenZ -> printfn "    ✓ Correctly detected as 7z archive"
    | Some other -> failwithf "Expected SevenZ but got %A" other
    | None -> failwith "Expected archive format to be detected"
  }

let ``test plain SRT detection`` () =
  task {
    printfn "\n  Testing plain SRT detection..."

    let srtContent =
      """1
00:00:01,000 --> 00:00:03,000
Test subtitle line

2
00:00:04,000 --> 00:00:06,000
Another line
"""

    let encoding = System.Text.Encoding.UTF8
    let srtBytes = encoding.GetBytes(srtContent)
    use ms = new MemoryStream(srtBytes)

    let format = Common.detectArchiveFormat ms

    match format with
    | None -> printfn "    ✓ Correctly detected as non-archive (plain text)"
    | Some fmt -> failwithf "Plain SRT should not be detected as archive, got %A" fmt
  }

// ============================================================================
// Test Runner
// ============================================================================

let runTests () =
  task {
    printfn "\n=========================================="
    printfn "Download Integration Tests"
    printfn "=========================================="

    let tests =
      [ "plain SRT detection", ``test plain SRT detection``
        "7z detection", ``test archive format detection with real 7z``
        "Subsunacs episode", ``test Subsunacs download - 7z archive``
        "Subsunacs movie", ``test Subsunacs download - movie subtitle``
        "Sab.Bz movie", ``test SabBz download - ZIP archive``
        "Yavka search/download", ``test YavkaNet download - form submission`` ]

    let mutable failures = 0

    for name, run in tests do
      try
        do! run ()
      with ex ->
        failures <- failures + 1
        printfn "Failed %s: %s" name ex.Message

    printfn "Live checks: %d passed, %d failed" (tests.Length - failures) failures
    return if failures = 0 then 0 else 1
  }

// Entry point removed - Tests.fs is the main entry point
