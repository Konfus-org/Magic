using Magic.Files;
using Magic.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Launcher;

internal static class ServiceCollectionExtensions
{
    extension(ServiceCollection serviceContainer)
    {
        internal (App App, IFileSystem FileSystem) RegisterMagicServices()
        {
            var app = new App();
            IFileSystem fileSystem = new FileSystem();

            serviceContainer.AddSingleton(app);
            serviceContainer.AddSingleton(fileSystem);

            return (app, fileSystem);
        }
    }
}
