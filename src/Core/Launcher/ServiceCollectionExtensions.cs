using System.Diagnostics.CodeAnalysis;
using Magic.Assets.Pipeline;
using Magic.Caching;
using Magic.Files;
using Magic.GemAPI;
using Magic.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Launcher;

internal static class ServiceCollectionExtensions
{
    extension(ServiceCollection services)
    {
        /// <summary>
        /// Makes and registers what Magic provides a run before it knows the project, all new for it: the app, the file
        /// system and the gems' messages.
        /// </summary>
        internal (App App, IFileSystem Files, Events Events) RegisterRequiredServices()
        {
            App app = new();
            FileSystem files = new();
            Events events = new();
            services.AddSingleton(app);
            services.AddSingleton<IFileSystem>(files);
            services.AddSingleton(events);

            return (app, files, events);
        }

        /// <summary>
        /// Makes and registers what Magic provides a run of <paramref name="project"/>: its caches and the assets, which
        /// the caller ends with the run.
        /// </summary>
        [SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The assets go to the caller, which ends them with the run."
        )]
        internal AssetManager RegisterProjectServices(IFileSystem files, Project project)
        {
            Caches caches = new(files, project.Name, files.GetParent(project.Path));
            AssetManager assets = new(files, caches);
            services.AddSingleton(caches);
            services.AddSingleton(assets);

            return assets;
        }
    }
}
