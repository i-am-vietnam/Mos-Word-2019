using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Server.Packages;

public static class PackageJson
{
    public const int MaximumBytes = 4 * 1024 * 1024;

    public static JObject Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Package JSON exceeds its size limit.");
        // Package files retain their original bytes; tolerate only one leading UTF-8 preamble.
        int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        using var text = new StringReader(new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset));
        using var reader = new JsonTextReader(text) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new InvalidDataException("Trailing package JSON data.");
        return value;
    }
}
