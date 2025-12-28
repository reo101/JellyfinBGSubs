module FsAutocompleteTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading

let private sourceText =
  """module MethodChains
open System
open System.IO
open System.Text.RegularExpressions
open HtmlAgilityPack
let timestamp () = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
let extension (fileName: string) = Path.GetExtension(fileName).TrimStart('.')
let cell (row: HtmlNode) = row.SelectSingleNode(".//td[4]").InnerText
let groupValue (idMatch: Match) = idMatch.Groups.[1].Value
"""

/// Exercises real project checking and editor requests, not merely compiler acceptance.
let runTests (server: string) =
  task {
    let directory = Directory.CreateTempSubdirectory "fsautocomplete-probe-"
    use serverProcess = new Process()
    let mutable serverStarted = false
    let mutable serverLog = None
    let mutable succeeded = false

    try
      let project = Path.Combine(directory.FullName, "MethodChains.fsproj")
      let source = Path.Combine(directory.FullName, "MethodChains.fs")
      let uri = Uri(source).AbsoluteUri

      File.WriteAllText(
        project,
        """<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
<ItemGroup><PackageReference Include="HtmlAgilityPack" Version="1.11.54" /></ItemGroup>
<ItemGroup><Compile Include="MethodChains.fs" /></ItemGroup></Project>"""
      )

      File.WriteAllText(source, sourceText + "let diagnosticSentinel: int = \"invalid\"\n")
      let restoreInfo = ProcessStartInfo("dotnet")
      restoreInfo.ArgumentList.Add "restore"
      restoreInfo.ArgumentList.Add project
      use restore = Process.Start restoreInfo
      do! restore.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 60.0)

      if restore.ExitCode <> 0 then
        failwith "Probe project restore failed"

      let executable =
        if server.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then
          "dotnet"
        else
          server

      serverProcess.StartInfo <-
        ProcessStartInfo(
          executable,
          WorkingDirectory = directory.FullName,
          RedirectStandardInput = true,
          RedirectStandardOutput = true,
          RedirectStandardError = true
        )

      if executable = "dotnet" then
        serverProcess.StartInfo.ArgumentList.Add server

      serverStarted <- serverProcess.Start()
      serverLog <- Some(serverProcess.StandardError.ReadToEndAsync())
      let input = serverProcess.StandardInput.BaseStream
      let output = serverProcess.StandardOutput.BaseStream
      let json value = JsonSerializer.SerializeToNode value

      let sendNode (message: JsonNode) =
        let payload = JsonSerializer.SerializeToUtf8Bytes message
        input.Write(Encoding.ASCII.GetBytes $"Content-Length: {payload.Length}\r\n\r\n")
        input.Write payload
        input.Flush()

      let send (methodName: string) (parameters: JsonNode) (identifier: int option) =
        printfn "LSP send %s" methodName
        let message = JsonObject()
        message.["jsonrpc"] <- json "2.0"
        message.["method"] <- json methodName
        message.["params"] <- parameters
        identifier |> Option.iter (fun id -> message.["id"] <- json id)
        sendNode message

      let mutable diagnostics: JsonNode option = None
      let mutable analyzedVersion = 0

      let receive cancellationToken =
        task {
          use header = new MemoryStream()
          let buffer = Array.zeroCreate<byte> 1
          let mutable complete = false

          while not complete do
            do! output.ReadExactlyAsync(buffer.AsMemory(), cancellationToken)
            header.WriteByte buffer.[0]

            complete <-
              header.Length >= 4L
              && Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n")

          let length =
            Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            |> Array.find (fun line -> line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            |> fun line -> Int32.Parse(line.Split(':').[1].Trim())

          let payload = Array.zeroCreate<byte> length
          do! output.ReadExactlyAsync(payload.AsMemory(), cancellationToken)
          let message = JsonNode.Parse payload

          if not (isNull message.["method"]) then
            let methodName = message.["method"].GetValue<string>()

            if not (isNull message.["id"]) then
              let response = JsonObject()
              response.["jsonrpc"] <- json "2.0"
              response.["id"] <- message.["id"].DeepClone()

              response.["result"] <-
                if methodName = "workspace/configuration" then
                  json (Array.init (message.["params"].["items"].AsArray().Count) (fun _ -> JsonObject()))
                else
                  null

              sendNode response

            if
              methodName = "textDocument/publishDiagnostics"
              && message.["params"].["uri"].GetValue<string>() = uri
            then
              diagnostics <- Some message.["params"].["diagnostics"]

            if
              methodName = "fsharp/documentAnalyzed"
              && message.["params"].["textDocument"].["uri"].GetValue<string>() = uri
            then
              analyzedVersion <- message.["params"].["textDocument"].["version"].GetValue<int>()

          return message
        }

      let mutable nextIdentifier = 0

      let request methodName parameters =
        task {
          nextIdentifier <- nextIdentifier + 1
          let identifier = nextIdentifier
          send methodName parameters (Some identifier)
          use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
          let mutable result = None

          while result.IsNone do
            let! message = receive timeout.Token

            if
              isNull message.["method"]
              && not (isNull message.["id"])
              && message.["id"].GetValue<int>() = identifier
            then
              if not (isNull message.["error"]) then
                failwith (message.["error"].ToJsonString())

              result <- Some message.["result"]

          return result.Value
        }

      let awaitDiagnostics version expectedError =
        task {
          use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 60.0)

          let complete () =
            analyzedVersion >= version
            && (diagnostics
                |> Option.exists (fun values ->
                  (values.AsArray()
                   |> Seq.exists (fun diagnostic -> diagnostic.["severity"].GetValue<int>() = 1)) = expectedError))

          while not (complete ()) do
            let! _ = receive timeout.Token
            ()

          return diagnostics.Value.AsArray()
        }

      let! _ =
        request
          "initialize"
          (json
            {| processId = Environment.ProcessId
               rootUri = Uri(directory.FullName + "/").AbsoluteUri
               capabilities =
                {| textDocument =
                    {| publishDiagnostics =
                        {| relatedInformation = true
                           versionSupport = true |} |} |}
               initializationOptions = {| AutomaticWorkspaceInit = true |} |})

      send "initialized" (JsonObject()) None

      send
        "textDocument/didOpen"
        (json
          {| textDocument =
              {| uri = uri
                 languageId = "fsharp"
                 version = 1
                 text = File.ReadAllText source |} |})
        None

      let! initial = awaitDiagnostics 1 true

      if
        not (
          initial
          |> Seq.exists (fun diagnostic -> diagnostic.["message"].GetValue<string>().Contains "string")
        )
      then
        failwith "The server did not report the intentional project type error"

      printfn "Project checking confirmed by intentional diagnostic"

      for version in 2..4 do
        diagnostics <- None
        let text = sourceText + String.replicate version "\n"
        File.WriteAllText(source, text)

        send
          "textDocument/didChange"
          (json
            {| textDocument = {| uri = uri; version = version |}
               contentChanges = [| {| text = text |} |] |})
          None

        let! updated = awaitDiagnostics version false

        if
          updated
          |> Seq.exists (fun diagnostic -> diagnostic.["severity"].GetValue<int>() = 1)
        then
          failwith (updated.ToJsonString())

        for line, token in [ 5, "ToString"; 6, "TrimStart"; 7, "InnerText"; 8, "Value" ] do
          let position =
            {| line = line
               character = sourceText.Split('\n').[line].IndexOf(token, StringComparison.Ordinal) + 2 |}

          let clock = Stopwatch.StartNew()

          let! hover =
            request
              "textDocument/hover"
              (json
                {| textDocument = {| uri = uri |}
                   position = position |})

          if isNull hover || isNull hover.["contents"] then
            failwith $"No hover information for {token}"

          printfn "revision %d: %s hover %.3fs" version token clock.Elapsed.TotalSeconds

        let! completion =
          request
            "textDocument/completion"
            (json
              {| textDocument = {| uri = uri |}
                 position = {| line = 5; character = 31 |} |})

        if isNull completion then
          failwith "Completion did not respond"

      let! _ = request "shutdown" null
      send "exit" null None
      do! serverProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 10.0)

      if serverProcess.ExitCode <> 0 then
        failwith (serverLog.Value.GetAwaiter().GetResult())

      printfn "All four chains: diagnostics, edits, hover and completion responsive"
      succeeded <- true
      return 0
    finally
      if serverStarted && not serverProcess.HasExited then
        serverProcess.Kill true
        serverProcess.WaitForExit()

      if not succeeded then
        serverLog
        |> Option.iter (fun output -> eprintfn "%s" (output.GetAwaiter().GetResult()))

      Directory.Delete(directory.FullName, true)
  }
