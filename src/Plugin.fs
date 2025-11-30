namespace Jellyfin.Plugin.BulgarianSubs

open System
open MediaBrowser.Common.Configuration
open MediaBrowser.Common.Plugins
open MediaBrowser.Model.Serialization
open Microsoft.Extensions.DependencyInjection
open MediaBrowser.Controller
open MediaBrowser.Controller.Plugins
open MediaBrowser.Controller.Subtitles

type Plugin(applicationPaths: IApplicationPaths, xmlSerializer: IXmlSerializer) =
  inherit BasePlugin<PluginConfiguration>(applicationPaths, xmlSerializer)

  override _.Name = "Bulgarian Subtitles"
  override _.Id = Guid.Parse("93b5ed36-e282-4d55-9c49-0121203b7293")
  override _.Description = "Downloads subtitles from subs.sab.bz and subsunacs.net"

type PluginServiceRegistrator() =
  interface IPluginServiceRegistrator with
    member _.RegisterServices(serviceCollection: IServiceCollection, _applicationHost: IServerApplicationHost) =
      serviceCollection.AddSingleton<ISubtitleProvider, BulgarianSubtitleProvider>()
      |> ignore
