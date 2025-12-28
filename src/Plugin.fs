namespace Jellyfin.Plugin.BulgarianSubs

open System
open MediaBrowser.Common.Configuration
open MediaBrowser.Common.Plugins
open MediaBrowser.Model.Serialization

[<Sealed>]
type public Plugin(applicationPaths: IApplicationPaths, xmlSerializer: IXmlSerializer) as this =
  inherit BasePlugin<PluginConfiguration>(applicationPaths, xmlSerializer)

  static let mutable instance: Plugin option = None

  do instance <- Some this

  override _.Name = "Bulgarian Subtitles"

  override _.Id = Guid.Parse("93b5ed36-e282-4d55-9c49-0121203b7293")

  static member Instance = instance
