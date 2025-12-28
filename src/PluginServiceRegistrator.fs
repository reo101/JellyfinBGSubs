namespace Jellyfin.Plugin.BulgarianSubs

open System
open MediaBrowser.Controller
open MediaBrowser.Controller.Plugins
open MediaBrowser.Controller.Subtitles
open Microsoft.Extensions.DependencyInjection

type PluginServiceRegistrator() =
    interface IPluginServiceRegistrator with
        member _.RegisterServices(serviceCollection: IServiceCollection, _applicationHost: IServerApplicationHost) =
            Console.WriteLine("BANICA: PluginServiceRegistrator.RegisterServices() called!")
            serviceCollection.AddSingleton<ISubtitleProvider, BulgarianSubtitleProvider>()
            |> ignore
            Console.WriteLine("BANICA: BulgarianSubtitleProvider registered as ISubtitleProvider")
