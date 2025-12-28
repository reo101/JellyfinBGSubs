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

  let private maximumDownloadBytes = 20L * 1024L * 1024L
  let private maximumSubtitleBytes = 10L * 1024L * 1024L

  /// Remote IDs, form targets and redirects may only use the provider's public origin.
  let validateProviderUri (origin: Uri) (target: Uri) =
    if
      not target.IsAbsoluteUri
      || (target.Scheme <> Uri.UriSchemeHttp && target.Scheme <> Uri.UriSchemeHttps)
      || (origin.Scheme = Uri.UriSchemeHttps && target.Scheme <> Uri.UriSchemeHttps)
      || not target.IsDefaultPort
      || not (String.Equals(target.Host, origin.Host, StringComparison.OrdinalIgnoreCase))
      || not (String.IsNullOrEmpty target.UserInfo)
    then
      raise (InvalidDataException "Subtitle URL does not belong to its provider")

  /// The client's handler must disable automatic redirects so each hop is validated before sending.
  let sendWithRedirects (httpClient: HttpClient) (request: HttpRequestMessage) cancellationToken =
    let origin =
      if isNull request.Headers.Referrer then
        request.RequestUri
      else
        request.Headers.Referrer

    let rec send (message: HttpRequestMessage) remaining =
      task {
        validateProviderUri origin message.RequestUri
        let! response = httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)

        if isNull response.RequestMessage then
          response.RequestMessage <- message

        let status = int response.StatusCode

        if List.contains status [ 301; 302; 303; 307; 308 ] then
          use redirectResponse = response

          if remaining = 0 || isNull response.Headers.Location then
            return raise (InvalidDataException "Invalid or excessive subtitle redirects")
          else
            let target = Uri(message.RequestUri, response.Headers.Location)
            validateProviderUri origin target
            validateProviderUri message.RequestUri target

            let method =
              if
                status = 303
                || ((status = 301 || status = 302) && message.Method = HttpMethod.Post)
              then
                HttpMethod.Get
              else
                message.Method

            use nextRequest = new HttpRequestMessage(method, target)

            for header in message.Headers do
              nextRequest.Headers.TryAddWithoutValidation(header.Key, header.Value) |> ignore

            if method = message.Method then
              nextRequest.Content <- message.Content

            return! send nextRequest (remaining - 1)
        else
          return response
      }

    send request 5

  let private copyWithLimit (source: Stream) (destination: Stream) maximumBytes cancellationToken =
    task {
      let buffer = Array.zeroCreate<byte> 81920
      let mutable total = 0L
      let mutable finished = false

      while not finished do
        let! count = source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
        finished <- count = 0
        total <- total + int64 count

        if total > maximumBytes then
          raise (InvalidDataException $"Subtitle data exceeds {maximumBytes} bytes")

        do! destination.WriteAsync(buffer, 0, count, cancellationToken)
    }

  let readResponseBytes (response: HttpResponseMessage) cancellationToken =
    task {
      let length = response.Content.Headers.ContentLength

      if length.HasValue && length.Value > maximumDownloadBytes then
        raise (InvalidDataException "Subtitle download exceeds 20 MiB")

      use! source = response.Content.ReadAsStreamAsync(cancellationToken)
      use destination = new MemoryStream()
      do! copyWithLimit source destination maximumDownloadBytes cancellationToken
      return destination.ToArray()
    }

  let private copySubtitle (source: Stream) =
    let destination = new MemoryStream()

    try
      copyWithLimit source destination maximumSubtitleBytes CancellationToken.None
      |> fun task -> task.GetAwaiter().GetResult()

      destination.Position <- 0L
      destination :> Stream
    with _ ->
      destination.Dispose()
      reraise ()

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
    logger.LogInformation(
      $"extractSubtitleStream: stream length={responseStream.Length}, position={responseStream.Position}"
    )

    // Check if it's a plain text file (uncompressed subtitle) or an archive
    let archiveFormat = detectArchiveFormat responseStream
    logger.LogInformation($"extractSubtitleStream: detected format={archiveFormat}")

    match archiveFormat with
    | Some fmt ->
      try
        logger.LogInformation($"extractSubtitleStream: opening archive with format {fmt}")
        use archive = ArchiveFactory.OpenArchive(responseStream)
        // Find first entry that ends with .srt or .sub
        let entry =
          archive.Entries
          |> Seq.tryFind (fun e -> not e.IsDirectory && isSubtitleFile e.Key)

        match entry with
        | Some e ->
          logger.LogInformation($"extractSubtitleStream: extracting {e.Key}")

          if e.Size > maximumSubtitleBytes then
            raise (InvalidDataException "Extracted subtitle exceeds 10 MiB")

          use entryStream = e.OpenEntryStream()
          let stream = copySubtitle entryStream
          let ext = Path.GetExtension e.Key |> fun s -> s.TrimStart '.'
          logger.LogInformation($"extractSubtitleStream: extracted {stream.Length} bytes, ext={ext}")
          stream, ext
        | None ->
          logger.LogWarning("extractSubtitleStream: no subtitle file found in archive")
          raise (InvalidDataException "Archive contains no subtitle file")
      with ex ->
        logger.LogError(ex, "Could not extract subtitle archive")
        reraise ()
    | None ->
      // Not an archive, return as-is (plain SRT/SUB file)
      // Copy to MemoryStream to ensure it's seekable and won't be disposed
      let stream = copySubtitle responseStream
      // Use detected extension or fallback to "srt"
      stream,
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
            (if isNull hiddenInputs then
               Seq.empty
             else
               hiddenInputs :> seq<_>)
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
        use request = new HttpRequestMessage(HttpMethod.Get, url)
        request.Headers.Referrer <- Uri(referer)
        request.Headers.Add("User-Agent", getUserAgentHeader ())

        use! response = sendWithRedirects httpClient request cancellationToken
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

        let! bytes = readResponseBytes response cancellationToken
        use stream = new MemoryStream(bytes)
        return extractSubtitleStream stream detectedExt

      | FormPage(pageUrl, referer) ->
        // Step 1: GET the subtitle page to extract form parameters
        use pageRequest = new HttpRequestMessage(HttpMethod.Get, pageUrl)
        pageRequest.Headers.Referrer <- Uri(referer)
        pageRequest.Headers.Add("User-Agent", getUserAgentHeader ())

        use! pageResponse = sendWithRedirects httpClient pageRequest cancellationToken
        pageResponse.EnsureSuccessStatusCode() |> ignore

        let! pageBytes = readResponseBytes pageResponse cancellationToken
        let pageEncoding = System.Text.Encoding.GetEncoding("windows-1251")
        let pageHtml = pageEncoding.GetString(pageBytes)

        // Step 2: Extract form action and hidden parameters
        match extractFormParameters pageHtml with
        | None -> return raise (InvalidDataException "Subtitle page contains no download form")
        | Some(formAction, hiddenParams) ->
          // Step 3: POST the form to get the download
          let pageUri = pageResponse.RequestMessage.RequestUri
          let formUrl = Uri(pageUri, formAction)
          validateProviderUri (Uri referer) formUrl
          use postRequest = new HttpRequestMessage(HttpMethod.Post, formUrl)
          postRequest.Content <- new FormUrlEncodedContent(hiddenParams)
          postRequest.Headers.Referrer <- pageUri
          postRequest.Headers.Add("User-Agent", getUserAgentHeader ())

          use! downloadResponse = sendWithRedirects httpClient postRequest cancellationToken
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

          let! bytes = readResponseBytes downloadResponse cancellationToken
          use stream = new MemoryStream(bytes)
          return extractSubtitleStream stream detectedExt
    }
