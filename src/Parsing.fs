namespace Jellyfin.Plugin.BulgarianSubs

open System
open ComputationExpressions


module Parsing =

  // Bulgarian month names
  let private bulgarianMonths =
    [| "Jan"
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
        let trimmed = dateStr.Trim()
        trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)
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

              let! month =
                bulgarianMonths
                |> Array.tryFindIndex (fun m -> m.Equals(parts.[1], StringComparison.OrdinalIgnoreCase))
                |> Option.map (fun idx -> idx - 1)

              return (day, month, year)
            }
            |> Option.map (fun (day, month, year) ->
              let baseDate = DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc)
              // NOTE: Bulgarian timezone
              baseDate.AddHours(2.0))
      with _ ->
        None
