#if !DEBUG
namespace MosWord2019
{
    // Release test seam lives only in the test assembly; the distributed executable has no offline authenticator.
    public sealed class OfflineAuthenticationService : IAuthenticationService
    {
        public bool Authenticate(string username, string password) =>
            !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
    }
}
#endif
