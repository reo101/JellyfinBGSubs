namespace Jellyfin.Plugin.BulgarianSubs

open System
open System.Text
open System.Text.Json

[<CLIMutable>]
type SubtitleIdentifierPayload =
  { Version: int
    Url: string
    Context: SubtitleSelectionContext }

module SubtitleIdentifier =
  let private options =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

  let private encoding = UTF8Encoding(false, true)
  let private maximumIdentifierLength = 8192

  let private validateContext (context: SubtitleSelectionContext) =
    if
      isNull (box context)
      || context.Season.HasValue <> context.Episode.HasValue
      || (context.Season.HasValue && context.Season.Value < 0)
      || (context.Episode.HasValue && context.Episode.Value < 1)
    then
      invalidArg "identifier" "Invalid subtitle selection context"

  let private validateUrl (url: string) =
    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> ()
    | _ -> invalidArg "identifier" "Invalid subtitle URL"

  let encode (provider: string) (url: string) (context: SubtitleSelectionContext) =
    validateContext context
    validateUrl url

    let payload =
      { Version = 1
        Url = url
        Context = context }

    let bytes = JsonSerializer.SerializeToUtf8Bytes(payload, options)
    let identifier = $"{provider}::{Convert.ToBase64String bytes}"

    if identifier.Length > maximumIdentifierLength then
      invalidArg "identifier" "Subtitle identifier is too long"

    identifier

  /// URL-only IDs remain valid; new IDs carry a versioned, restart-independent request.
  let decode (identifier: string) =
    if String.IsNullOrEmpty identifier || identifier.Length > maximumIdentifierLength then
      invalidArg "identifier" "Invalid subtitle identifier"

    let parts = identifier.Split("::", 2, StringSplitOptions.None)

    if parts.Length <> 2 || String.IsNullOrWhiteSpace parts.[0] then
      invalidArg "identifier" "Invalid subtitle identifier"

    let value =
      try
        encoding.GetString(Convert.FromBase64String parts.[1])
      with
      | :? FormatException
      | :? DecoderFallbackException -> invalidArg "identifier" "Invalid subtitle URL encoding"

    if value.TrimStart().StartsWith("{", StringComparison.Ordinal) then
      let payload =
        try
          JsonSerializer.Deserialize<SubtitleIdentifierPayload>(value, options)
        with :? JsonException ->
          invalidArg "identifier" "Invalid subtitle identifier payload"

      if
        isNull (box payload)
        || payload.Version <> 1
        || String.IsNullOrWhiteSpace payload.Url
      then
        invalidArg "identifier" "Unsupported subtitle identifier payload"

      validateContext payload.Context
      validateUrl payload.Url
      parts.[0], payload.Url, Some payload.Context
    else
      validateUrl value
      parts.[0], value, None
