module Jellyfin.Plugin.BulgarianSubs.DownloadSecurityTests

open System
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open System.Text
open Microsoft.Extensions.Logging.Abstractions
open Jellyfin.Plugin.BulgarianSubs
open Jellyfin.Plugin.BulgarianSubs.Providers

let private assert' condition message =
  if not condition then
    failwithf "Assertion failed: %s" message

let private expectFailure (operation: unit -> Task<'a>) =
  task {
    let mutable failed = false

    try
      let! _ = operation ()
      ()
    with _ ->
      failed <- true

    assert' failed "Expected operation to fail"
  }

let private response status content location =
  let result = new HttpResponseMessage(status)
  result.Content <- content

  if not (isNull location) then
    result.Headers.Location <- Uri(location, UriKind.RelativeOrAbsolute)

  result

type private DelegateHandler(send: HttpRequestMessage -> CancellationToken -> Task<HttpResponseMessage>) =
  inherit HttpMessageHandler()
  override _.SendAsync(request, cancellationToken) = send request cancellationToken

type private GeneratedStream(length: int64) as this =
  inherit Stream()
  let mutable position = 0L
  override _.CanRead = true
  override _.CanSeek = false
  override _.CanWrite = false
  override _.Length = length

  override _.Position
    with get () = position
    and set _ = raise (NotSupportedException())

  override _.Flush() = ()
  override _.Seek(_, _) = raise (NotSupportedException())
  override _.SetLength(_) = raise (NotSupportedException())
  override _.Write(_, _, _) = raise (NotSupportedException())

  override _.Read(buffer, offset, count) =
    let count = int (min (int64 count) (length - position))

    if count > 0 then
      Array.Clear(buffer, offset, count)

    position <- position + int64 count
    count

  override _.ReadAsync(buffer, offset, count, cancellationToken) =
    cancellationToken.ThrowIfCancellationRequested()
    Task.FromResult(this.Read(buffer, offset, count))

let private withClient send (action: HttpClient -> Task<'a>) =
  task {
    use client = new HttpClient(new DelegateHandler(send))
    return! action client
  }

let private execute strategy send =
  withClient send (fun client ->
    Common.executeStrategy
      strategy
      client
      CancellationToken.None
      (Common.extractSubtitleStreamWithLogging NullLogger.Instance))

let private redirectTest target =
  task {
    let requests = ResizeArray<Uri>()

    let send (request: HttpRequestMessage) _ =
      requests.Add request.RequestUri
      Task.FromResult(response HttpStatusCode.Found (new ByteArrayContent(Encoding.UTF8.GetBytes("ok"))) target)

    do!
      expectFailure (fun () ->
        withClient send (fun client ->
          use request = new HttpRequestMessage(HttpMethod.Get, "https://subs.example/start")
          request.Headers.Referrer <- Uri("https://subs.example/search")
          Common.sendWithRedirects client request CancellationToken.None))

    assert' (requests.Count = 1) "Rejected redirect must not issue another request"
  }

let private zipWithEntry (name: string) (contents: string) =
  use output = new MemoryStream()

  do
    use archive = new ZipArchive(output, ZipArchiveMode.Create, true)
    use entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open()
    let bytes = Encoding.UTF8.GetBytes(contents)
    entry.Write(bytes, 0, bytes.Length)

  output.ToArray()

let private testRedirectSecurity () =
  task {
    let requests = ResizeArray<string * Uri>()

    let send (request: HttpRequestMessage) _ =
      requests.Add((request.Method.Method, request.RequestUri))

      let result =
        if requests.Count = 1 then
          response HttpStatusCode.Found (new ByteArrayContent([||])) "https://SUBS.EXAMPLE:443/final"
        else
          response HttpStatusCode.OK (new ByteArrayContent(Encoding.UTF8.GetBytes("ok"))) null

      Task.FromResult(result)

    do!
      withClient send (fun client ->
        task {
          use request = new HttpRequestMessage(HttpMethod.Get, "https://subs.example/start")
          request.Headers.Referrer <- Uri("https://subs.example/search")
          use! result = Common.sendWithRedirects client request CancellationToken.None
          let! body = Common.readResponseBytes result CancellationToken.None
          assert' (Encoding.UTF8.GetString(body) = "ok") "Same-origin redirect should succeed"
          assert' (requests.Count = 2) "Expected one redirected request"
        })

    do! redirectTest "https://attacker.example/file"
    do! redirectTest "ftp://subs.example/file"
    do! redirectTest "http://subs.example/file"
    do! redirectTest "https://subs.example:444/file"
    do! redirectTest "https://user:password@subs.example/file"

    let loopRequests = ResizeArray<Uri>()

    let loopSend (request: HttpRequestMessage) _ =
      loopRequests.Add request.RequestUri

      Task.FromResult(
        response HttpStatusCode.Found (new ByteArrayContent([||])) (sprintf "/loop/%d" loopRequests.Count)
      )

    do!
      expectFailure (fun () ->
        withClient loopSend (fun client ->
          use request = new HttpRequestMessage(HttpMethod.Get, "https://subs.example/start")
          request.Headers.Referrer <- Uri("https://subs.example/search")
          Common.sendWithRedirects client request CancellationToken.None))

    assert' (loopRequests.Count <= 6) "Redirect handling must stop after five redirects"

    let postRequests = ResizeArray<HttpMethod>()

    let postSend (request: HttpRequestMessage) _ =
      postRequests.Add request.Method

      Task.FromResult(
        if postRequests.Count = 1 then
          response HttpStatusCode.TemporaryRedirect (new ByteArrayContent([||])) "/after"
        else
          response HttpStatusCode.OK (new ByteArrayContent([||])) null
      )

    do!
      withClient postSend (fun client ->
        task {
          use request = new HttpRequestMessage(HttpMethod.Post, "https://subs.example/before")
          request.Headers.Referrer <- Uri("https://subs.example/search")
          request.Content <- new StringContent("x=1")
          use! _ = Common.sendWithRedirects client request CancellationToken.None

          assert'
            (postRequests.Count = 2 && (postRequests |> Seq.forall ((=) HttpMethod.Post)))
            "307 must preserve POST"
        })
  }

let private testFormActions () =
  task {
    let requests = ResizeArray<string * Uri>()

    let send (request: HttpRequestMessage) _ =
      requests.Add((request.Method.Method, request.RequestUri))

      let result =
        if request.Method = HttpMethod.Get then
          response
            HttpStatusCode.OK
            (new StringContent("<form action='download?key=1'><input type='hidden' name='id' value='7'></form>"))
            null
        else
          response HttpStatusCode.OK (new ByteArrayContent(Encoding.UTF8.GetBytes("subtitle"))) null

      Task.FromResult(result)

    let! (stream, _) = execute (FormPage("https://subs.example/subtitles/page", "https://subs.example/search")) send
    use stream = stream
    assert' (requests.Count = 2) "Form flow should GET the page and POST once"
    assert' (requests.[0] = ("GET", Uri("https://subs.example/subtitles/page"))) "Form page should be fetched first"

    assert'
      (requests.[1] = ("POST", Uri("https://subs.example/subtitles/download?key=1")))
      "Relative form action must resolve against page URL"

    let maliciousRequests = ResizeArray<string>()

    let maliciousSend (request: HttpRequestMessage) _ =
      maliciousRequests.Add request.Method.Method

      Task.FromResult(
        response HttpStatusCode.OK (new StringContent("<form action='https://attacker.example/collect'></form>")) null
      )

    do!
      expectFailure (fun () ->
        execute (FormPage("https://subs.example/page", "https://subs.example/search")) maliciousSend)

    assert'
      (maliciousRequests.Count = 1 && maliciousRequests.[0] = "GET")
      "Untrusted form action must be rejected before POST"
  }

let private testDownloadLimits () =
  task {
    let oversized = 20L * 1024L * 1024L + 1L

    use unknownLengthResponse =
      response HttpStatusCode.OK (new StreamContent(new GeneratedStream(oversized))) null

    do! expectFailure (fun () -> Common.readResponseBytes unknownLengthResponse CancellationToken.None)

    let lyingContent = new ByteArrayContent(Array.zeroCreate<byte> (int oversized))
    lyingContent.Headers.ContentLength <- Nullable 1L
    use lyingResponse = response HttpStatusCode.OK lyingContent null
    do! expectFailure (fun () -> Common.readResponseBytes lyingResponse CancellationToken.None)

    let plain = Encoding.UTF8.GetBytes(String.replicate (10 * 1024 * 1024 + 1) "x")

    let plainSend _ _ =
      Task.FromResult(response HttpStatusCode.OK (new ByteArrayContent(plain)) null)

    do! expectFailure (fun () -> execute (DirectUrl("https://subs.example/file", "https://subs.example/")) plainSend)

    let expandedZip =
      zipWithEntry "subtitle.srt" (String.replicate (10 * 1024 * 1024 + 1) "x")

    let zipSend _ _ =
      Task.FromResult(response HttpStatusCode.OK (new ByteArrayContent(expandedZip)) null)

    do! expectFailure (fun () -> execute (DirectUrl("https://subs.example/file", "https://subs.example/")) zipSend)

    for bytes in
      [ zipWithEntry "readme.txt" "not a subtitle"
        [| 0x50uy; 0x4Buy; 0x03uy; 0x04uy; 0uy |] ] do
      let invalidSend _ _ =
        Task.FromResult(response HttpStatusCode.OK (new ByteArrayContent(bytes)) null)

      do!
        expectFailure (fun () -> execute (DirectUrl("https://subs.example/file", "https://subs.example/")) invalidSend)
  }

let runTests () =
  task {
    do! testRedirectSecurity ()
    do! testFormActions ()
    do! testDownloadLimits ()
  }
  |> fun operation -> operation.GetAwaiter().GetResult()

  printfn "Download origins, redirects, byte limits and invalid archives verified"
