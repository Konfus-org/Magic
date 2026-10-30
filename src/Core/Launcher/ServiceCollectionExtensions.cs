using System.Diagnostics.CodeAnalysis;
using Magic.Assets.Pipeline;
using Magic.Files;
using Magic.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Launcher;

internal static class ServiceCollectionExtensions
{
    extension(ServiceCollection services)
    {
        /// <summary>
        /// Makes and registers what Magic provides a run, all new for it: the app, the file system and the assets, which
        /// the caller ends with the run.
        /// </summary>
        [SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The assets go to the caller, which ends them with the run."
        )]
        internal (App App, IFileSystem Files, AssetManager Assets) RegisterMagicServices()
        {
            App app = new();
            FileSystem files = new();
            AssetManager assets = new(files);
            services.AddSingleton(app);
            services.AddSingleton<IFileSystem>(files);
            services.AddSingleton(assets);

            return (app, files, assets);
        }
    }
}
