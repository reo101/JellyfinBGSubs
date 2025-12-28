namespace Jellyfin.Plugin.BulgarianSubs.Providers

open System
open System.IO
open System.Net.Http
open System.Threading
open SharpCompress.Archives
open Microsoft.Extensions.Logging
open Jellyfin.Plugin.BulgarianSubs

/// Common provider utilities
module Common =

  // ============================================================================
  // Archive Detection Utilities
  // ============================================================================

  /// Archive format detected from magic bytes
  type ArchiveFormat =
    | ZIP
    | RAR
    | SevenZ
    | Gzip

  /// Active pattern to match array prefixes
  let (|Prefix|_|) (prefixBytes: byte[]) (arr: byte[]) =
    if
      arr.Length >= prefixBytes.Length
      && Array.forall2 (=) prefixBytes (arr |> Array.take prefixBytes.Length)
    then
      Some()
    else
      None

  /// Detects archive format by checking magic number bytes
  /// Returns Some(format) if the stream starts with a known archive magic number,
  /// or None if it's not a recognized archive format
  let detectArchiveFormat (stream: Stream) : ArchiveFormat option =
    let magic = Array.zeroCreate 4
    let bytesRead = stream.Read(magic, 0, 4)
    stream.Position <- 0L

    if bytesRead < 2 then
      None
    else
      match magic with
      // ZIP: 50 4B (PK..) - bytes 3-4 vary by ZIP type
      | Prefix [| 0x50uy; 0x4Buy |] -> Some ZIP
      // Gzip: 1F 8B (only 2 bytes needed)
      | Prefix [| 0x1Fuy; 0x8Buy |] -> Some Gzip
      // RAR: 52 61 72 (Rar) - full signature is 52 61 72 21 60 90
      | Prefix [| 0x52uy; 0x61uy; 0x72uy |] -> Some RAR
      // 7z: 37 7A BC AF
      | Prefix [| 0x37uy; 0x7Auy; 0xBCuy; 0xAFuy |] -> Some SevenZ
      | _ -> None

  /// Checks if a filename is a subtitle file (.srt or .sub)
  let isSubtitleFile (fileName: string) : bool =
    let name = fileName.ToLowerInvariant()
    name.EndsWith ".srt" || name.EndsWith ".sub"

  /// Extract subtitle stream from response (archive or plain file)
  let extractSubtitleStreamWithLogging
    (logger: ILogger)
    (responseStream: Stream)
    (_fileExtension: string)
    : Stream * string =
    logger.LogInformation($"extractSubtitleStream: stream length={responseStream.Length}, position={responseStream.Position}")

    // Check if it's a plain text file (uncompressed subtitle) or an archive
    let archiveFormat = detectArchiveFormat responseStream
    logger.LogInformation($"extractSubtitleStream: detected format={archiveFormat}")

    match archiveFormat with
    | Some fmt ->
      try
        logger.LogInformation($"extractSubtitleStream: opening archive with format {fmt}")
        use archive = ArchiveFactory.Open(responseStream)
        // Find first entry that ends with .srt or .sub
        let entries = archive.Entries |> Seq.filter (fun e -> not e.IsDirectory) |> Seq.toList
        logger.LogInformation($"extractSubtitleStream: archive has {entries.Length} entries")

        for e in entries do
          logger.LogInformation($"extractSubtitleStream: entry={e.Key}")

        let entry = entries |> List.tryFind (fun e -> isSubtitleFile e.Key)

        match entry with
        | Some e ->
          logger.LogInformation($"extractSubtitleStream: extracting {e.Key}")
          let ms = new MemoryStream()
          e.WriteTo ms
          ms.Position <- 0L
          // HACK: fsautocomplete type inference bug - see FSAUTOCOMPLETE_BUGS.md
          //       `Path.GetExtension(e.Key).TrimStart('.')` causes infinite loop
          let ext = Path.GetExtension e.Key |> fun s -> s.TrimStart '.'
          logger.LogInformation($"extractSubtitleStream: extracted {ms.Length} bytes, ext={ext}")
          ms :> Stream, ext
        | None ->
          logger.LogWarning("extractSubtitleStream: no subtitle file found in archive")
          // If no subtitle found in archive, return empty
          Stream.Null, "srt"
      with ex ->
        logger.LogError(ex, $"extractSubtitleStream: archive error: {ex.Message}")
        Stream.Null, "srt"
    | None ->
      // Not an archive, return as-is (plain SRT/SUB file)
      // Copy to MemoryStream to ensure it's seekable and won't be disposed
      let ms = new MemoryStream()
      responseStream.CopyTo(ms)
      ms.Position <- 0L
      // Use detected extension or fallback to "srt"
      ms :> Stream,
      if String.IsNullOrEmpty(_fileExtension) then
        "srt"
      else
        _fileExtension

  // ============================================================================
  // Download Strategy Execution
  // ============================================================================

  let private getUserAgentHeader () =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"

  /// Extract form action and hidden parameters from subtitle page
  /// Used by Yavka.net for form-based downloads
  let private extractFormParameters (pageHtml: string) : (string * Map<string, string>) option =
    try
      let doc = HtmlAgilityPack.HtmlDocument()
      doc.LoadHtml(pageHtml)

      let form = doc.DocumentNode.SelectSingleNode("//form")

      if form = null then
        None
      else
        let formAction = form.GetAttributeValue("action", "")

        if String.IsNullOrWhiteSpace formAction then
          None
        else
          let hiddenInputs = form.SelectNodes(".//input[@type='hidden']")

          let hiddenParams =
            hiddenInputs
            |> Seq.map (fun inp ->
              let name = inp.GetAttributeValue("name", "")
              let value = inp.GetAttributeValue("value", "")
              (name, value))
            |> Seq.filter (fun (name, _) -> not (String.IsNullOrWhiteSpace name))
            |> Map.ofSeq

          Some(formAction, hiddenParams)
    with _ ->
      None

  let executeStrategy
    (strategy: DownloadStrategy)
    (httpClient: HttpClient)
    (cancellationToken: CancellationToken)
    (extractSubtitleStream: Stream -> string -> Stream * string)
    : System.Threading.Tasks.Task<Stream * string> =
    task {
      match strategy with
      | DirectUrl(url, referer) ->
        let request = new HttpRequestMessage(HttpMethod.Get, url)
        request.Headers.Referrer <- Uri(referer)
        request.Headers.Add("User-Agent", getUserAgentHeader ())

        let! response = httpClient.SendAsync(request, cancellationToken)
        response.EnsureSuccessStatusCode() |> ignore

        let detectedExt =
          try
            (response.Content.Headers.ContentDisposition |> Option.ofObj)
            |> Option.bind (fun cd -> cd.FileName |> Option.ofObj)
            |> Option.map (fun fileName ->
              Path.GetExtension fileName
              |> fun ext -> ext.TrimStart '.' |> fun e -> if String.IsNullOrEmpty e then "srt" else e)
            |> Option.defaultValue "srt"
          with _ ->
            "srt"

        let! responseStream = response.Content.ReadAsStreamAsync(cancellationToken)
        // Copy to MemoryStream to make it seekable (required for archive detection)
        let ms = new MemoryStream()
        do! responseStream.CopyToAsync(ms, cancellationToken)
        ms.Position <- 0L
        return extractSubtitleStream ms detectedExt

      | FormPage(pageUrl, referer) ->
        // Step 1: GET the subtitle page to extract form parameters
        let pageRequest = new HttpRequestMessage(HttpMethod.Get, pageUrl)
        pageRequest.Headers.Referrer <- Uri(referer)
        pageRequest.Headers.Add("User-Agent", getUserAgentHeader ())

        let! pageResponse = httpClient.SendAsync(pageRequest, cancellationToken)
        pageResponse.EnsureSuccessStatusCode() |> ignore

        let! pageBytes = pageResponse.Content.ReadAsByteArrayAsync(cancellationToken)
        let pageEncoding = System.Text.Encoding.GetEncoding("windows-1251")
        let pageHtml = pageEncoding.GetString(pageBytes)

        // Step 2: Extract form action and hidden parameters
        match extractFormParameters pageHtml with
        | None ->
          // If form extraction fails, return empty stream
          return new MemoryStream() :> Stream, "srt"
        | Some(formAction, hiddenParams) ->
          // Step 3: POST the form to get the download
          let formUrl =
            if formAction.StartsWith("http") then
              formAction
            else if formAction.StartsWith("/") then
              // Absolute path
              let baseUri = Uri(pageUrl)
              (new Uri(baseUri, formAction)).ToString()
            else
              // Relative path
              let basePath = pageUrl.Substring(0, pageUrl.LastIndexOf('/') + 1)
              basePath + formAction

          let formContent = new FormUrlEncodedContent(hiddenParams)
          let postRequest = new HttpRequestMessage(HttpMethod.Post, formUrl)
          postRequest.Content <- formContent
          postRequest.Headers.Referrer <- Uri(pageUrl)
          postRequest.Headers.Add("User-Agent", getUserAgentHeader ())

          let! downloadResponse = httpClient.SendAsync(postRequest, cancellationToken)
          downloadResponse.EnsureSuccessStatusCode() |> ignore

          let detectedExt =
            try
              (downloadResponse.Content.Headers.ContentDisposition |> Option.ofObj)
              |> Option.bind (fun cd -> cd.FileName |> Option.ofObj)
              |> Option.map (fun fileName ->
                Path.GetExtension fileName
                |> fun ext -> ext.TrimStart '.' |> fun e -> if String.IsNullOrEmpty e then "srt" else e)
              |> Option.defaultValue "srt"
            with _ ->
              "srt"

          let! responseStream = downloadResponse.Content.ReadAsStreamAsync(cancellationToken)
          // Copy to MemoryStream to make it seekable (required for archive detection)
          let ms = new MemoryStream()
          do! responseStream.CopyToAsync(ms, cancellationToken)
          ms.Position <- 0L
          return extractSubtitleStream ms detectedExt
    }
