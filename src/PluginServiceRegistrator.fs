namespace Jellyfin.Plugin.BulgarianSubs

open System.Net.Http
open MediaBrowser.Controller
open MediaBrowser.Controller.Plugins
open MediaBrowser.Controller.Subtitles
open Microsoft.Extensions.DependencyInjection

type PluginServiceRegistrator() =
  interface IPluginServiceRegistrator with
    member _.RegisterServices(serviceCollection: IServiceCollection, _applicationHost: IServerApplicationHost) =
      serviceCollection
        .AddHttpClient("BulgarianSubs")
        .ConfigurePrimaryHttpMessageHandler(fun () ->
          new HttpClientHandler(AllowAutoRedirect = false) :> HttpMessageHandler)
      |> ignore

      serviceCollection.AddSingleton<ISubtitleProvider, BulgarianSubtitleProvider>()
      |> ignore
