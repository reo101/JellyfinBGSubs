namespace Jellyfin.Plugin.BulgarianSubs

module ComputationExpressions =
  type OptionBuilder() =
    member _.Bind(x, f) = Option.bind f x
    member _.Return x = Some x
    member _.ReturnFrom x = x
    member _.Delay f = f
    member _.Run f = f ()

  let option = OptionBuilder()
