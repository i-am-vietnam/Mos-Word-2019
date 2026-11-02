using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace MosWord2019.Server.Security;

public static class KeyStorage
{
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<KeyManagementOptions>().Configure<IConfiguration, IWebHostEnvironment>(
            (options, configuration, environment) =>
            {
                var path = Validate(configuration, environment);
                if (path != null) options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(path), NullLoggerFactory.Instance);
            });
    }

    public static string? Validate(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var path = configuration["DataProtection:KeyPath"];
        if (string.IsNullOrWhiteSpace(path))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("DataProtection:KeyPath is required outside Development.");
            return null;
        }
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Data Protection key path must be absolute and outside the repository.");
        var fullPath = Path.GetFullPath(path);
        for (var directory = new DirectoryInfo(environment.ContentRootPath); directory != null; directory = directory.Parent)
        {
            if (!Directory.Exists(Path.Combine(directory.FullName, ".git")) && !File.Exists(Path.Combine(directory.FullName, ".git"))) continue;
            if (fullPath.Equals(directory.FullName, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(directory.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Data Protection keys must remain outside the repository.");
            break;
        }
        return fullPath;
    }
}
