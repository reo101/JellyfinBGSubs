namespace Jellyfin.Plugin.BulgarianSubs

open MediaBrowser.Controller
open MediaBrowser.Controller.Plugins
open MediaBrowser.Controller.Subtitles
open Microsoft.Extensions.DependencyInjection

type PluginServiceRegistrator() =
  interface IPluginServiceRegistrator with
    member _.RegisterServices(serviceCollection: IServiceCollection, _applicationHost: IServerApplicationHost) =
      serviceCollection.AddSingleton<ISubtitleProvider, BulgarianSubtitleProvider>()
      |> ignore
