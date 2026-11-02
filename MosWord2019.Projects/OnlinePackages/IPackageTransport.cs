using System.Threading;
using System.Threading.Tasks;

namespace MosWord2019.Projects.OnlinePackages
{
    public interface IPackageTransport
    {
        Task<byte[]> GetManifest(string sessionToken, CancellationToken cancellationToken);
        Task<byte[]> Download(string downloadPath, string sessionToken, CancellationToken cancellationToken);
        Task ReportDefinition(string definitionHash, string sessionToken, CancellationToken cancellationToken);
    }
}
