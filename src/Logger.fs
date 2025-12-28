namespace Jellyfin.Plugin.BulgarianSubs

open System

type IDebugLogger =
  abstract member Debug: msg: string -> unit

module DebugLogger =
  let private timestamp () =
    // HACK: fsautocomplete type inference bug - see FSAUTOCOMPLETE_BUGS.md
    //       `DateTime.Now.ToString(format)` causes infinite loop
    DateTime.Now |> (fun date -> date.ToString("yyyy-MM-dd HH:mm:ss.fff"))

  let fileLogger (path: string) : IDebugLogger =
    { new IDebugLogger with
        member _.Debug msg =
          try
            timestamp () |> fun ts -> System.IO.File.AppendAllText(path, $"[{ts}] {msg}\n")
          with _ ->
            () }

  let noOpLogger: IDebugLogger =
    { new IDebugLogger with
        member _.Debug _ = () }

  let consoleLogger: IDebugLogger =
    { new IDebugLogger with
        member _.Debug msg =
          timestamp () |> fun ts -> printfn $"[{ts}] {msg}" }
