module Jellyfin.Plugin.BulgarianSubs.JellyfinIntegrationTests

open System
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

let private pluginId = "93b5ed36-e282-4d55-9c49-0121203b7293"
let private configPath = $"/Plugins/{pluginId}/Configuration"
let private apiRoot = "http://localhost:8096"
let private statePath = "/tmp/jellyfin-test-state.json"
let private subtitle = "1\n00:00:00,000 --> 00:00:01,000\nЗдравей, свят.\n"

let private searchHtml =
  """<table><tr class="subs-row"><td></td><td></td><td></td><td><a href="index.php?act=download&amp;attach_id=1">Fixture Movie (2020)</a></td><td>15 Nov 2020</td><td>BG</td><td>1</td><td>25.0</td><td><a>fixture</a></td><td>8.5</td><td>42</td></tr></table>"""

let private check condition message =
  if not condition then
    failwith message

let private value (node: JsonNode) (name: string) = node[name].GetValue<string>()
let private json (node: JsonNode) = node.ToJsonString()

let serveFixture () : Task<int> =
  task {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
    use archive = new MemoryStream()
    let zip = new ZipArchive(archive, ZipArchiveMode.Create, true)
    let entry = zip.CreateEntry("subtitles/fixture.srt")

    do
      use writer = new StreamWriter(entry.Open(), Encoding.UTF8)
      writer.Write(subtitle)

    zip.Dispose()
    let archiveBytes = archive.ToArray()
    let requests = ResizeArray<string>()
    use listener = new HttpListener()
    listener.Prefixes.Add("http://*:80/")
    listener.Start()

    while true do
      let! context = listener.GetContextAsync()
      let path = context.Request.Url.AbsolutePath
      let response = context.Response

      try
        let bytes, contentType =
          if path = "/requests" then
            lock requests (fun () -> Encoding.UTF8.GetBytes(JsonSerializer.Serialize(requests)), "application/json")
          elif path = "/index.php" then
            lock requests (fun () -> requests.Add(context.Request.Url.PathAndQuery))

            match context.Request.QueryString["act"] with
            | "search" -> Encoding.GetEncoding(1251).GetBytes(searchHtml), "text/html; charset=windows-1251"
            | "download" ->
              check
                (context.Request.Headers["Referer"] = "http://subs.sab.bz/")
                "Sab.Bz download omitted the expected Referer"

              archiveBytes, "application/zip"
            | _ -> failwith "Unknown fixture action"
          else
            failwith "Unknown fixture path"

        response.StatusCode <- 200
        response.ContentType <- contentType
        response.ContentLength64 <- int64 bytes.Length
        do! response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
      with _ ->
        response.StatusCode <- 404

      response.Close()

    return 0
  }

let private authorization token =
  let baseHeader =
    "MediaBrowser Client=\"VM Test\", Device=\"NixOS\", DeviceId=\"subtitle-test\", Version=\"1\""

  if String.IsNullOrEmpty(token) then
    baseHeader
  else
    $"{baseHeader}, Token=\"{token}\""

let private api (client: HttpClient) token methodName path (body: JsonNode option) =
  task {
    use request = new HttpRequestMessage(HttpMethod(methodName), apiRoot + path)

    request.Headers.TryAddWithoutValidation("Authorization", authorization token)
    |> ignore

    match body with
    | Some value -> request.Content <- new StringContent(json value, Encoding.UTF8, "application/json")
    | None -> ()

    use! response = client.SendAsync(request)
    let! content = response.Content.ReadAsStringAsync()
    check response.IsSuccessStatusCode $"{methodName} {path} failed ({response.StatusCode}): {content}"

    return
      if String.IsNullOrWhiteSpace(content) then
        null
      else
        JsonNode.Parse(content)
  }

let private objectNode (value: string) = JsonNode.Parse(value)
let private get client token path = api client token "GET" path None

let private post client token path body =
  api client token "POST" path (Some body)

let private items client token =
  task {
    let! response = get client token "/Items?recursive=true&includeItemTypes=Movie&fields=Path"
    return response["Items"].AsArray()
  }

let private findMovie client token =
  task {
    let deadline = DateTime.UtcNow.AddSeconds(120.)
    let mutable found = None

    while found.IsNone && DateTime.UtcNow < deadline do
      let! all = items client token

      found <-
        all
        |> Seq.tryFind (fun item -> (value item "Path").EndsWith("Fixture.Movie.2020.mkv", StringComparison.Ordinal))

      if found.IsNone then
        do! Task.Delay(1000)

    if found.IsNone then
      let! all = get client token "/Items?recursive=true&fields=Path"
      let! folders = get client token "/Library/VirtualFolders"
      failwith $"Fixture Movie was not indexed within 120 seconds: {all}; libraries: {folders}"

    return found.Value
  }

let private assertEnabled client token expected =
  task {
    let! config = get client token configPath
    check (config["EnableSabBz"].GetValue<bool>() = expected) $"Unexpected EnableSabBz value: {config}"
    return config
  }

let private search client token movieId language =
  get client token $"/Items/{movieId}/RemoteSearch/Subtitles/{language}"

let private waitForReady (client: HttpClient) token path =
  task {
    let deadline = DateTime.UtcNow.AddSeconds(120.)
    let mutable ready = false

    while not ready && DateTime.UtcNow < deadline do
      try
        use request = new HttpRequestMessage(HttpMethod.Get, apiRoot + path)

        request.Headers.TryAddWithoutValidation("Authorization", authorization token)
        |> ignore

        use! response = client.SendAsync(request)
        ready <- response.IsSuccessStatusCode
      with :? HttpRequestException ->
        ()

      if not ready then
        do! Task.Delay(1000)

    check ready "Jellyfin did not finish startup within 120 seconds"
  }

let runTests (expectedVersion: string) : Task<int> =
  task {
    use client = new HttpClient()
    client.Timeout <- TimeSpan.FromSeconds(30.)
    do! waitForReady client "" "/Startup/User"
    let! _ = get client "" "/Startup/User"

    let! _ =
      post
        client
        ""
        "/Startup/User"
        (objectNode "{\"Name\":\"administrator\",\"Password\":\"integration-test-password\"}")

    // Follow the setup wizard order so its automatic scan sees the complete library.
    let library =
      objectNode
        "{\"LibraryOptions\":{\"PathInfos\":[{\"Path\":\"/var/lib/jellyfin/media\"}],\"SaveSubtitlesWithMedia\":true,\"EnableInternetProviders\":false,\"EnableAutomaticSeriesGrouping\":false,\"TypeOptions\":[{\"Type\":\"Movie\",\"MetadataFetchers\":[],\"ImageFetchers\":[]}]}}"

    let! _ = post client "" "/Library/VirtualFolders?name=Fixtures&collectionType=movies" library
    let! _ = post client "" "/Startup/Complete" (objectNode "{}")

    let! login =
      post
        client
        ""
        "/Users/AuthenticateByName"
        (objectNode "{\"Username\":\"administrator\",\"Pw\":\"integration-test-password\"}")

    let token = value login "AccessToken"
    let! plugins = get client token "/Plugins"

    let plugin =
      plugins.AsArray()
      |> Seq.tryFind (fun item -> Guid.Parse(value item "Id") = Guid.Parse(pluginId))
      |> Option.defaultWith (fun () -> failwith $"Bulgarian subtitles plugin was not registered: {plugins}")

    check (value plugin "Status" = "Active") $"Plugin is not active: {plugin}"
    check (value plugin "Version" = expectedVersion) $"Unexpected plugin version: {plugin}"
    let! config = assertEnabled client token true

    check
      (not (config["EnableSubsunacs"].GetValue<bool>())
       && not (config["EnablePodnapisi"].GetValue<bool>()))
      $"Unexpected subtitle providers enabled: {config}"

    let! movie = findMovie client token
    let movieId = value movie "Id"
    let searchPath = $"/Items/{movieId}/RemoteSearch/Subtitles/bul"
    let! results = get client token searchPath
    check (results.AsArray().Count = 1) $"Expected exactly one Bulgarian subtitle: {results}"
    let result = results[0]
    check (value result "ProviderName" = "Bulgarian Subtitles") $"Unexpected provider: {result}"
    check (value result "Name" = "Fixture Movie (2020)") $"Unexpected subtitle title: {result}"
    let resultId = value result "Id"

    use downloadRequest =
      new HttpRequestMessage(
        HttpMethod.Get,
        apiRoot + $"/Providers/Subtitles/Subtitles/{Uri.EscapeDataString(resultId)}"
      )

    downloadRequest.Headers.TryAddWithoutValidation("Authorization", authorization token)
    |> ignore

    use! downloadResponse = client.SendAsync(downloadRequest)
    let! downloaded = downloadResponse.Content.ReadAsStringAsync()
    check downloadResponse.IsSuccessStatusCode $"Subtitle download failed: {downloadResponse.StatusCode}"
    check (downloaded.Contains("Здравей, свят.")) "Downloaded subtitle does not contain Cyrillic text"
    let! _ = api client token "POST" $"/Items/{movieId}/RemoteSearch/Subtitles/{Uri.EscapeDataString(resultId)}" None
    let savedPath = "/var/lib/jellyfin/media/Fixture.Movie.2020.bul.srt"

    check
      (File.ReadAllText(savedPath).Contains("Здравей, свят."))
      $"Saved subtitle is missing Cyrillic text: {savedPath}"

    let! english = search client token movieId "eng"
    check (english.AsArray().Count = 0) $"English subtitle search should be empty: {english}"
    config["EnableSabBz"] <- JsonValue.Create(false)
    let! _ = post client token configPath config
    let! _ = assertEnabled client token false
    let! disabled = search client token movieId "bul"
    check (disabled.AsArray().Count = 0) $"Sab.Bz search returned results while disabled: {disabled}"
    let state = JsonObject()
    state["Token"] <- JsonValue.Create(token)
    state["MovieId"] <- JsonValue.Create(movieId)
    state["ResultId"] <- JsonValue.Create(resultId)
    File.WriteAllText(statePath, state.ToJsonString())
    return 0
  }

let verifyRestart () : Task<int> =
  task {
    use client = new HttpClient()
    client.Timeout <- TimeSpan.FromSeconds(30.)
    let state = JsonNode.Parse(File.ReadAllText(statePath))

    let token, movieId, resultId =
      value state "Token", value state "MovieId", value state "ResultId"

    do! waitForReady client token "/Plugins"
    let! _ = assertEnabled client token false
    let! disabled = search client token movieId "bul"
    check (disabled.AsArray().Count = 0) $"Sab.Bz search returned results after restart while disabled: {disabled}"
    let! config = get client token configPath
    config["EnableSabBz"] <- JsonValue.Create(true)
    let! _ = post client token configPath config
    let! results = search client token movieId "bul"

    check
      (results.AsArray().Count = 1 && value results[0] "Id" = resultId)
      $"Subtitle ID changed after restart: {results}"

    use! request = client.GetAsync("http://subs.sab.bz/requests")
    request.EnsureSuccessStatusCode() |> ignore
    let! requests = request.Content.ReadAsStringAsync()
    use document = JsonDocument.Parse(requests)

    let paths =
      document.RootElement.EnumerateArray()
      |> Seq.map (fun item -> item.GetString())
      |> Seq.toArray

    check
      (paths
       |> Array.filter (fun path -> path.Contains("act=download"))
       |> Array.length = 2)
      $"Expected exactly two fixture downloads: {requests}"

    return 0
  }
