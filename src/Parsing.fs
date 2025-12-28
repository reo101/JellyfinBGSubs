namespace Jellyfin.Plugin.BulgarianSubs

open System
open ComputationExpressions

module Parsing =

  // Bulgarian month names
  let private bulgarianMonths =
    [| ""
       "Jan"
       "Feb"
       "Mar"
       "Apr"
       "May"
       "Jun"
       "Jul"
       "Aug"
       "Sep"
       "Oct"
       "Nov"
       "Dec" |]

  // Parse Bulgarian date strings (e.g., "14 Nov 2010") to DateTime in Bulgarian timezone (UTC+2)
  let tryParseBulgarianDate (dateStr: string) : DateTime option =
    if String.IsNullOrWhiteSpace dateStr then
      None
    else
      try
        dateStr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
        |> fun parts ->
          if parts.Length < 3 then
            None
          else
            option {
              let! day =
                Int32.TryParse parts.[0]
                |> function
                  | true, v -> Some v
                  | _ -> None

              let! year =
                Int32.TryParse parts.[2]
                |> function
                  | true, v -> Some v
                  | _ -> None

              let! idx =
                bulgarianMonths
                |> Array.tryFindIndex (fun m -> m.Equals(parts.[1], StringComparison.OrdinalIgnoreCase))

              if idx > 0 then return (day, idx, year) else return! None
            }
            |> Option.map (fun (day, month, year) ->
              DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc).AddHours(2.0))
      with _ ->
        None
