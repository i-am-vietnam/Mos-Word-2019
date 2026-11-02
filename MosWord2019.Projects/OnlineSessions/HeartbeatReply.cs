using System;

namespace MosWord2019.Projects.OnlineSessions
{
    public sealed class HeartbeatReply
    {
        public Guid SessionId { get; set; }
        public string Mode { get; set; }
        public DateTimeOffset ServerTime { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public double RemainingSeconds { get; set; }
        public string SessionToken { get; set; }
    }

    public sealed class HeartbeatSessionRejectedException : Exception
    {
        public int StatusCode { get; }
        public HeartbeatSessionRejectedException(int statusCode) : base("The server rejected the session.") { StatusCode = statusCode; }
    }
}
