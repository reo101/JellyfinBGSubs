namespace Jellyfin.Plugin.BulgarianSubs

open System
open System.Globalization

module Parsing =

  let private bulgarianTimeZone =
    lazy (TimeZoneInfo.FindSystemTimeZoneById "Europe/Sofia")

  /// Site upload dates represent midnight in Bulgaria, including daylight saving time.
  let tryParseBulgarianDate (dateStr: string) : DateTime option =
    match
      DateTime.TryParseExact(dateStr, "d MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces)
    with
    | true, date -> Some(TimeZoneInfo.ConvertTimeToUtc(date, bulgarianTimeZone.Value))
    | _ -> None
