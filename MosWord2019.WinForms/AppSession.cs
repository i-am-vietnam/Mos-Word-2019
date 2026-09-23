using System;

namespace MosWord2019
{
    // Shell selection only; not an exam session or authentication record.
    public sealed class AppSession
    {
        public AppSession(AppMode mode, string language)
        {
            if (!Enum.IsDefined(typeof(AppMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            if (language != "en" && language != "vi") throw new ArgumentException("Choose en or vi.", nameof(language));
            Mode = mode;
            Language = language;
        }
        public AppMode Mode { get; }
        public string Language { get; }
    }
}
