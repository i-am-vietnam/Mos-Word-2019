using System;
using System.Configuration;
using System.Net.Http;
using MosWord2019.Projects.OnlineSessions;

namespace MosWord2019
{
    internal sealed class ServerConnectionSettings
    {
        internal Uri Origin { get; }
        internal string Pin { get; }
        internal ServerConnectionSettings() : this(null, null) { }
        internal ServerConnectionSettings(string developmentUrl, string developmentPin)
        {
            string url = CompiledServerSettings.Origin, pin = CompiledServerSettings.SpkiPin;
#if DEBUG
            url = developmentUrl ?? ConfigurationManager.AppSettings["DevelopmentServerUrl"] ?? url;
            pin = developmentPin ?? ConfigurationManager.AppSettings["DevelopmentServerSpkiPin"] ?? pin;
#endif
            Origin = new Uri(url, UriKind.Absolute); Pin = pin;
        }
        internal HttpClient CreateClient() => PinnedHttpClientFactory.Create(Origin, Pin);
    }
}
