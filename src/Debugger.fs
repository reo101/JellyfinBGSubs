/// Interactive debugging tool for testing the subtitle provider without Jellyfin
module Jellyfin.Plugin.BulgarianSubs.Debugger

open System
open System.IO
open System.Net.Http
open System.Text
open System.Threading
open Microsoft.Extensions.Logging
open MediaBrowser.Controller.Subtitles

// Simple logging implementation for debugging
type DebugLogger<'T>() =
  interface ILogger<'T> with
    member _.BeginScope<'TState>(_state: 'TState) : IDisposable =
      { new IDisposable with
          member _.Dispose() = () }

    member _.IsEnabled(_level) = true

    member _.Log<'TState>(level, _eventId, state: 'TState, ex, _formatter) =
      Console.ForegroundColor <-
        match level with
        | LogLevel.Error -> ConsoleColor.Red
        | LogLevel.Warning -> ConsoleColor.Yellow
        | LogLevel.Information -> ConsoleColor.Green
        | _ -> ConsoleColor.Gray

      printf "[%s] %A" (level.ToString()) state

      if ex <> null then
        printf " %s" ex.Message

      printfn ""
      Console.ResetColor()

// HTTP factory for actual HTTP requests
type RealHttpClientFactory() =
  let client =
    let handler = new HttpClientHandler()
    handler.ServerCertificateCustomValidationCallback <- fun _ _ _ _ -> true // Accept all certificates for testing
    new HttpClient(handler)

  interface IHttpClientFactory with
    member _.CreateClient(_name: string) = client

let private printHeader title =
  printfn "%s" title
  printfn "%s" (String.replicate 60 "-")

let private yearSuffix =
  function
  | Some y -> sprintf " (%d)" y
  | None -> ""

let testSearch (movieName: string) (year: int option) =
  printHeader ($"🔍 Testing Search: {movieName}{yearSuffix year}")

  let logger = DebugLogger<BulgarianSubtitleProvider>()
  let factory = RealHttpClientFactory()
  let provider = BulgarianSubtitleProvider(logger, factory)
  let providerInterface = provider :> ISubtitleProvider

  let request = SubtitleSearchRequest()
  request.Language <- "bg"
  request.Name <- movieName

  if year.IsSome then
    request.ProductionYear <- Nullable(year.Value)

  try
    let results =
      providerInterface.Search(request, CancellationToken.None)
      |> Async.AwaitTask
      |> Async.RunSynchronously
      |> Seq.toList

    // Also fetch raw HTML to extract metadata
    let searchTerm = System.Net.WebUtility.UrlEncode(movieName)
    let yearParam = if year.IsSome then $"&yr={year.Value}" else ""

    let sabUrl =
      $"http://subs.sab.bz/index.php?act=search&movie={searchTerm}{yearParam}"

    let internalResults =
      try
        let handler = new System.Net.Http.HttpClientHandler()
        handler.ServerCertificateCustomValidationCallback <- fun _ _ _ _ -> true
        use client = new System.Net.Http.HttpClient(handler)

        let task =
          async {
            let! response = client.GetAsync sabUrl |> Async.AwaitTask
            let! bytes = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
            let encoding = System.Text.Encoding.GetEncoding("windows-1251")
            let html = encoding.GetString(bytes)
            return Parsing.parseSabBz html |> Seq.toList
          }

        task |> Async.RunSynchronously
      with _ ->
        []

    printfn "\n✅ Found %d results:" results.Length

    for (i, item) in List.indexed results do
      let encoding = System.Text.Encoding.UTF8
      let nameBytes = encoding.GetBytes(item.Name)
      let nameHex = nameBytes |> Array.map (sprintf "%02x") |> String.concat " "
      printfn "  %d. [%s] %s" (i + 1) item.ProviderName item.Name

      // Find matching internal result for metadata
      let internalMatch =
        internalResults
        |> List.tryFind (fun ir -> ir.Title.Contains(item.Name) || item.Name.Contains(ir.Title))

      (match internalMatch with
       | Some ir ->
         let uploaded =
           match ir.UploadDate with
           | Some d -> d.ToString("yyyy-MM-dd")
           | None -> "N/A"

         printfn "     Uploaded: %s" uploaded
       | None -> ())

      printfn "     Raw bytes: [%s]" nameHex
      printfn "     URL: %s" item.Id

    results
  with ex ->
    printfn "\n❌ Search failed: %s" ex.Message
    printfn "   Details: %s" ex.StackTrace
    []

let testSearchSubsunacs (movieName: string) (year: int option) =
  printHeader ($"🔍 Testing Subsunacs Search: {movieName}{yearSuffix year}")

  Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

  try
    let searchTerm = System.Net.WebUtility.UrlEncode(movieName)
    let yearParam = if year.IsSome then $"&y={year.Value}" else ""
    let url = $"https://subsunacs.net/search.php?m={searchTerm}{yearParam}&t=Submit"

    let handler = new System.Net.Http.HttpClientHandler()
    handler.ServerCertificateCustomValidationCallback <- fun _ _ _ _ -> true
    use client = new System.Net.Http.HttpClient(handler)

    let task =
      async {
        let! response = client.GetAsync url |> Async.AwaitTask
        let! bytes = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask

        let encoding = System.Text.Encoding.GetEncoding("windows-1251")
        let html = encoding.GetString(bytes)

        let internalResults = Parsing.parseSubsunacs html |> Seq.toList

        if internalResults.IsEmpty then
          printfn "\n✅ No results found from Subsunacs"
        else
          printfn "\n✅ Found %d results:" internalResults.Length

          for (i, item) in List.indexed internalResults do
            printfn "  %d. [%s] %s" (i + 1) item.ProviderName item.Title

            printfn
              "     Uploaded: %s"
              (match item.UploadDate with
               | Some d -> d.ToString("yyyy-MM-dd")
               | None -> "N/A")

            match item.DownloadStrategy with
            | DirectUrl(url, _) -> printfn "     URL: %s" url
            | FormPage(url, _) -> printfn "     Form Page: %s" url
      }

    task |> Async.RunSynchronously
  with ex ->
    printfn "\n❌ Subsunacs search failed: %s" ex.Message
    printfn "   Details: %s" ex.StackTrace

let inspectHtmlStructure (movieName: string) (provider: string) =
  printHeader ($"🔎 Inspecting HTML structure for: {movieName} ({provider})")

  Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

  try
    let handler = new System.Net.Http.HttpClientHandler()
    handler.ServerCertificateCustomValidationCallback <- fun _ _ _ _ -> true
    use client = new System.Net.Http.HttpClient(handler)

    let searchTerm = System.Net.WebUtility.UrlEncode(movieName)

    let url =
      if provider = "subsunacs" then
        $"https://subsunacs.net/search.php?m={searchTerm}&t=Submit"
      else
        $"http://subs.sab.bz/index.php?act=search&movie={searchTerm}"

    let task =
      async {
        let! response = client.GetAsync url |> Async.AwaitTask
        let! bytes = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask

        let encoding = System.Text.Encoding.GetEncoding("windows-1251")
        let html = encoding.GetString(bytes)

        // Save to file for manual inspection
        let outputFile =
          if provider = "subsunacs" then
            "/tmp/subsunacs_inspect.html"
          else
            "/tmp/sabs_bz_inspect.html"

        IO.File.WriteAllText(outputFile, html)
        printfn "✅ Full HTML saved to: %s" outputFile

        // Try to extract and show the table structure
        let doc = HtmlAgilityPack.HtmlDocument()
        doc.LoadHtml(html)

        let rows =
          if provider = "subsunacs" then
            doc.DocumentNode.SelectNodes("//tbody/tr")
          else
            doc.DocumentNode.SelectNodes("//tr")

        printfn "\n📊 Found %d rows" (if rows = null then 0 else rows.Count)

        if rows <> null && rows.Count > 0 then
          printfn "\n🔍 First 3 rows structure:\n"

          for (i, row) in rows |> Seq.take (min 3 rows.Count) |> List.ofSeq |> List.indexed do
            printfn "Row %d:" i
            let tds = row.SelectNodes(".//td")

            if tds <> null then
              for (j, td) in tds |> List.ofSeq |> List.indexed do
                let preview =
                  td.InnerText.Trim() |> fun s -> if s.Length > 50 then s.[0..49] + "..." else s

                printfn "  TD %d: %s" j preview

            let linkSelector =
              if provider = "subsunacs" then
                ".//a[contains(@href, '/subtitles/')]"
              else
                ".//a[contains(@href, 'act=download')]"

            let link = row.SelectSingleNode(linkSelector)

            if link <> null then
              let href = link.GetAttributeValue("href", "")
              printfn "  Download link found: %s" href

            printfn ""
      }

    task |> Async.RunSynchronously
  with ex ->
    printfn "\n❌ Inspection failed: %s" ex.Message
    printfn "   Details: %s" ex.StackTrace

let testDownload (subtitleId: string) =
  printHeader ($"📥 Testing Download: {subtitleId}")

  let logger = DebugLogger<BulgarianSubtitleProvider>()
  let factory = RealHttpClientFactory()
  let provider = BulgarianSubtitleProvider(logger, factory)
  let providerInterface = provider :> ISubtitleProvider

  try
    let response =
      providerInterface.GetSubtitles(subtitleId, CancellationToken.None)
      |> Async.AwaitTask
      |> Async.RunSynchronously

    if response.Stream <> null && response.Stream <> Stream.Null then
      printfn "\n✅ Download successful!"

      printfn
        "   Format: %s"
        (if response.Format <> null then
           response.Format
         else
           "unknown")

      printfn
        "   Language: %s"
        (if response.Language <> null then
           response.Language
         else
           "unknown")

      printfn "   Stream length: %d bytes" response.Stream.Length

      // Try to read first 200 bytes as preview
      let buffer = Array.zeroCreate 200
      response.Stream.Position <- 0L
      let bytesRead = response.Stream.Read(buffer, 0, 200)
      let encoding = System.Text.Encoding.UTF8
      let preview = encoding.GetString(buffer, 0, bytesRead)
      printfn "   Preview (first %d bytes):" bytesRead
      printfn "   %s" (preview.Replace("\n", "\n   "))

      // Check if it looks like a valid subtitle file
      let previewText = preview.Trim()

      if previewText.StartsWith("1") || previewText.StartsWith("[") then
        printfn "   ✓ Looks like a valid subtitle file"
      else
        printfn "   ⚠️ Might not be a valid subtitle file"
    else
      printfn "\n⚠️ Download returned empty stream"
  with ex ->
    printfn "\n❌ Download failed: %s" ex.Message
    printfn "   Details: %s" ex.StackTrace

let testExtractAndDecompressFlow () =
  printHeader "🗜️ Testing Archive Extraction Flow"

  Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

  try
    // First, search for a subtitle
    printfn "\n1️⃣  Searching for 'Inception 2010'..."
    let results = testSearch "Inception" (Some 2010)

    if not (List.isEmpty results) then
      let subtitleInfo = results.[0]
      printfn "\n2️⃣  Found: %s (ID: %s)" subtitleInfo.Name subtitleInfo.Id

      // Now test the download and extraction
      printfn "\n3️⃣  Downloading and extracting..."
      testDownload subtitleInfo.Id

      printfn "\n✅ Full extraction flow test completed!"
    else
      printfn "\n⚠️ No search results found to test extraction"
  with ex ->
    printfn "\n❌ Extraction flow test failed: %s" ex.Message
    printfn "   Details: %s" ex.StackTrace

let testShowSearch (seriesName: string) =
  printHeader ($"📺 Testing TV Show Search: {seriesName}")

  Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

  let logger = DebugLogger<BulgarianSubtitleProvider>()
  let factory = RealHttpClientFactory()
  let provider = BulgarianSubtitleProvider(logger, factory)
  let providerInterface = provider :> ISubtitleProvider

  let request = SubtitleSearchRequest()
  request.Language <- "bg"
  request.SeriesName <- seriesName
  request.Name <- seriesName
  request.ContentType <- MediaBrowser.Controller.Providers.VideoContentType.Episode

  try
    let results =
      providerInterface.Search(request, CancellationToken.None)
      |> Async.AwaitTask
      |> Async.RunSynchronously
      |> Seq.toList

    printfn "\n✅ Found %d results:" results.Length

    for (i, item) in List.indexed results do
      printfn "  %d. [%s] %s" (i + 1) item.ProviderName item.Name
      printfn "     URL: %s" item.Id

    results
  with ex ->
    printfn "\n❌ Show search failed: %s" ex.Message
    printfn "   Details: %s" ex.StackTrace
    []

let testEpisodeSearch (seriesName: string) (season: int) (episode: int) =
  printHeader ($"📺 Testing TV Episode Search: {seriesName} S{season:D2}E{episode:D2}")

  Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

  let logger = DebugLogger<BulgarianSubtitleProvider>()
  let factory = RealHttpClientFactory()
  let provider = BulgarianSubtitleProvider(logger, factory)
  let providerInterface = provider :> ISubtitleProvider

  let request = SubtitleSearchRequest()
  request.Language <- "bg"
  request.SeriesName <- seriesName
  request.Name <- seriesName
  request.ParentIndexNumber <- Nullable(season)
  request.IndexNumber <- Nullable(episode)
  request.ContentType <- MediaBrowser.Controller.Providers.VideoContentType.Episode

  try
    let results =
      providerInterface.Search(request, CancellationToken.None)
      |> Async.AwaitTask
      |> Async.RunSynchronously
      |> Seq.toList

    printfn "\n✅ Found %d results:" results.Length

    for (i, item) in List.indexed results do
      printfn "  %d. [%s] %s" (i + 1) item.ProviderName item.Name
      printfn "     URL: %s" item.Id

    results
  with ex ->
    printfn "\n❌ Episode search failed: %s" ex.Message
    printfn "   Details: %s" ex.StackTrace
    []

let interactiveTest () =
  printfn "╔════════════════════════════════════════════════════════╗"
  printfn "║   Bulgarian Subtitles Plugin - Debug Tool              ║"
  printfn "╚════════════════════════════════════════════════════════╝"

  printfn "\nUsage:"
  printfn "  search <movie_name> [year]"
  printfn "  show <series_name>"
  printfn "  episode <series> <season> <episode>"
  printfn "  download <subtitle_id>"
  printfn "  extract <movie_name> [year]"
  printfn "  help"
  printfn "  quit"
  printfn ""

  let rec loop () =
    printf "> "

    match Console.ReadLine() with
    | null -> () // EOF
    | input ->
      let parts = input.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)

      if parts.Length = 0 then
        loop ()
      else
        match parts.[0] with
        | "quit" -> printfn "Goodbye!"
        | "help" ->
          printfn "Commands:"
          printfn "  search <movie> [year]       Search for movie subtitles"
          printfn "  show <series>               Search for TV show subtitles"
          printfn "  episode <series> <s> <e>   Search for specific episode"
          printfn "  download <id>               Download subtitles by ID (format: provider|url)"
          printfn "  extract <movie> [year]      Test full search/download/extract flow"
          printfn "  help                        Show this help"
          printfn "  quit                        Exit"
          loop ()
        | "search" ->
          if parts.Length < 2 then
            printfn "Usage: search <movie_name> [year]"
            printfn "Example: search Inception 2010"
          else
            // Try to parse last part as year
            let (movieNameParts, year) =
              if parts.Length > 2 then
                match Int32.TryParse(parts.[parts.Length - 1]) with
                | true, y -> (parts.[1 .. parts.Length - 2], Some y)
                | false, _ -> (parts.[1..], None) // Last part isn't a year, include it
              else
                (parts.[1..], None)

            let movieName = String.concat " " movieNameParts
            testSearch movieName year |> ignore

          loop ()
        | "show" ->
          if parts.Length < 2 then
            printfn "Usage: show <series_name>"
            printfn "Example: show Breaking Bad"
          else
            let seriesName = String.concat " " parts.[1..]
            testShowSearch seriesName |> ignore

          loop ()
        | "episode" ->
          if parts.Length < 4 then
            printfn "Usage: episode <series_name> <season> <episode>"
            printfn "Example: episode Breaking Bad 1 1"
          else
            let seriesName = String.concat " " parts.[1 .. parts.Length - 3]

            match (Int32.TryParse(parts.[parts.Length - 2]), Int32.TryParse(parts.[parts.Length - 1])) with
            | (true, season), (true, episode) -> testEpisodeSearch seriesName season episode |> ignore
            | _ -> printfn "Error: season and episode must be numbers"

          loop ()
        | "download" ->
          if parts.Length < 2 then
            printfn "Usage: download <subtitle_id>"
            printfn "Example: download sab|http://subs.sab.bz/index.php?s=...&attach_id=12345"
          else
            let subtitleId = String.concat " " parts.[1..]
            testDownload subtitleId

          loop ()
        | "extract" ->
          if parts.Length < 2 then
            printfn "Usage: extract <movie_name> [year]"
            printfn "Example: extract Inception 2010"
          else
            // Try to parse last part as year
            let (movieNameParts, year) =
              if parts.Length > 2 then
                match Int32.TryParse(parts.[parts.Length - 1]) with
                | true, y -> (parts.[1 .. parts.Length - 2], Some y)
                | false, _ -> (parts.[1..], None) // Last part isn't a year, include it
              else
                (parts.[1..], None)

            let movieName = String.concat " " movieNameParts

            // Run the full extraction flow test
            let logger = DebugLogger<BulgarianSubtitleProvider>()
            let factory = RealHttpClientFactory()
            let provider = BulgarianSubtitleProvider(logger, factory)
            let providerInterface = provider :> ISubtitleProvider

            let request = SubtitleSearchRequest()
            request.Language <- "bg"
            request.Name <- movieName

            if year.IsSome then
              request.ProductionYear <- Nullable(year.Value)

            try
              let results =
                providerInterface.Search(request, CancellationToken.None)
                |> Async.AwaitTask
                |> Async.RunSynchronously
                |> Seq.toList

              if not (List.isEmpty results) then
                let first = results.[0]
                printfn "\n✅ Found: %s" first.Name
                testDownload first.Id
              else
                printfn "\n⚠️ No results found for '%s'" movieName
            with ex ->
              printfn "\n❌ Error: %s" ex.Message

          loop ()
        | cmd ->
          printfn "Unknown command: '%s'. Type 'help' for usage." cmd
          loop ()

  loop ()

[<EntryPoint>]
let main argv =
  let runDefault () =
    testSearch "Inception" (Some 2010) |> ignore
    testSearch "The Matrix" (Some 1999) |> ignore
    0

  if argv.Length = 0 then
    runDefault ()
  else
    match argv.[0] with
    | "interactive" ->
      interactiveTest ()
      0
    | "inspect" ->
      let isProvider = argv.Length > 1 && (argv.[1] = "subsunacs" || argv.[1] = "sabs")
      let provider = if isProvider then argv.[1] else "sabs"
      let movieStart = if isProvider then 2 else 1

      let movieName =
        if argv.Length > movieStart then
          String.concat " " argv.[movieStart..]
        else
          "Inception"

      inspectHtmlStructure movieName provider
      0
    | "test-download" ->
      let results = testSearch "Inception" (Some 2010)

      if not (List.isEmpty results) then
        testDownload results.[0].Id
      else
        printfn "No results found to download"

      0
    | "test-extraction" ->
      testExtractAndDecompressFlow ()
      0
    | "test-subsunacs" ->
      testSearchSubsunacs "Inception" (Some 2010) |> ignore
      testSearchSubsunacs "The Matrix" (Some 1999) |> ignore
      0
    | "test-show" ->
      let seriesName =
        if argv.Length > 1 then
          String.concat " " argv.[1..]
        else
          "Breaking Bad"

      testShowSearch seriesName |> ignore
      0
    | "test-episode" ->
      let seriesName = if argv.Length > 1 then argv.[1] else "Breaking Bad"
      let season = if argv.Length > 2 then int argv.[2] else 1
      let episode = if argv.Length > 3 then int argv.[3] else 1
      testEpisodeSearch seriesName season episode |> ignore
      0
    | _ -> runDefault ()
