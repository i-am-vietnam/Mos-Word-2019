using System;
using System.Threading;
using System.Threading.Tasks;

namespace MosWord2019.Projects.OnlineSessions
{
    public interface IHeartbeatTransport
    {
        Task<HeartbeatReply> SendAsync(Guid sessionId, string token, long? clientElapsedMilliseconds, bool wasOffline, CancellationToken cancellationToken);
    }
}
