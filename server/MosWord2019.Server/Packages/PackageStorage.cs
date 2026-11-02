using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MosWord2019.Server.Data;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MosWord2019.Server.Packages;

public sealed class PackageStorage(IConfiguration configuration, IHostEnvironment environment)
{
    public const int MaximumBlobBytes = 64 * 1024 * 1024;
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("MWPKG001");

    public void Store(Package package, byte[] zip)
    {
        if (zip.Length > MaximumBlobBytes - 36) throw new InvalidDataException("Package exceeds the download limit.");
        var root = ExternalPath("Packages:StorageRoot");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RejectReparsePoints(root);
        var key = RandomNumberGenerator.GetBytes(32);
        var blob = new byte[36 + zip.Length];
        Header.CopyTo(blob, 0);
        RandomNumberGenerator.Fill(blob.AsSpan(8, 12));
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(blob.AsSpan(8, 12), zip, blob.AsSpan(36), blob.AsSpan(20, 16), AdditionalData(package));
        var name = package.Id.ToString("N");
        var blobPath = Path.Combine(root, name + ".bin");
        var keyPath = Path.Combine(root, name + ".key");
        var keyCreated = false;
        try
        {
            WriteNew(keyPath, key);
            keyCreated = true;
            WriteNew(blobPath, blob);
        }
        catch
        {
            // Identifiers are freshly generated; never remove an existing package on a collision.
            if (keyCreated && !File.Exists(blobPath))
                File.Delete(keyPath);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        package.StoragePath = name + ".bin";
        package.Sha256 = Hash(blob);
        package.SizeBytes = blob.Length;
    }

    public byte[] ReadKey(Package package)
    {
        var key = ReadBounded(Path.Combine(ExternalPath("Packages:StorageRoot"), package.Id.ToString("N") + ".key"), 32);
        if (key.Length != 32) throw new InvalidDataException("Invalid package key.");
        return key;
    }

    // Call only while holding the importer transaction's PostgreSQL advisory lock.
    public int RemoveUnreferenced(ISet<Guid> referencedPackages)
    {
        var root = ExternalPath("Packages:StorageRoot");
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(root))
        {
            var signingKey = configuration["Packages:SigningKeyPath"];
            if (!string.IsNullOrWhiteSpace(signingKey) && Path.IsPathFullyQualified(signingKey) &&
                Path.GetFullPath(signingKey).Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(path);
            var match = Regex.Match(name, @"^(?<id>[0-9a-f]{32})\.(?:bin|key)(?<temporary>\.[0-9a-f]{32}\.tmp)?$", RegexOptions.CultureInvariant);
            if (!match.Success || !Guid.TryParseExact(match.Groups["id"].Value, "N", out var id)) continue;
            if (!match.Groups["temporary"].Success && referencedPackages.Contains(id)) continue;
            RejectReparsePoints(path);
            File.Delete(path);
            removed++;
        }
        return removed;
    }

    public byte[] ReadBlob(Package package)
    {
        using var stream = OpenBlob(package);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public FileStream OpenBlob(Package package)
    {
        if (package.StoragePath != package.Id.ToString("N") + ".bin") throw new InvalidDataException("Invalid package storage identity.");
        var path = Path.Combine(ExternalPath("Packages:StorageRoot"), package.StoragePath);
        RejectReparsePoints(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        try
        {
            if (stream.Length < 36 || stream.Length > MaximumBlobBytes || stream.Length != package.SizeBytes)
                throw new InvalidDataException("Package storage integrity check failed.");
            Span<byte> header = stackalloc byte[8];
            stream.ReadExactly(header);
            if (!header.SequenceEqual(Header)) throw new InvalidDataException("Package storage integrity check failed.");
            stream.Position = 0;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long remaining = package.SizeBytes;
            while (remaining > 0)
            {
                int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) throw new InvalidDataException("Package storage integrity check failed.");
                hasher.AppendData(buffer, 0, count);
                remaining -= count;
            }
            if (stream.ReadByte() != -1) throw new InvalidDataException("Package storage integrity check failed.");
            var hash = hasher.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(package.Sha256)))
                throw new InvalidDataException("Package storage integrity check failed.");
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public byte[] Sign(byte[] payload)
    {
        var seed = ReadBounded(ExternalPath("Packages:SigningKeyPath"), 32);
        try
        {
            if (seed.Length != 32) throw new InvalidDataException("The Ed25519 signing seed must contain 32 bytes.");
            var signer = new Ed25519Signer();
            signer.Init(true, new Ed25519PrivateKeyParameters(seed));
            signer.BlockUpdate(payload, 0, payload.Length);
            return signer.GenerateSignature();
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    public byte[] GetPublicKey()
    {
        var seed = ReadBounded(ExternalPath("Packages:SigningKeyPath"), 32);
        try
        {
            if (seed.Length != 32) throw new InvalidDataException("Invalid Ed25519 signing seed.");
            return new Ed25519PrivateKeyParameters(seed).GeneratePublicKey().GetEncoded();
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    public static byte[] AdditionalData(Package package) => Encoding.UTF8.GetBytes("MosWord2019.Package.v1|" + package.Code + "|" + package.Version);
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string ExternalPath(string setting)
    {
        var configured = configuration[setting];
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
            throw new InvalidOperationException(setting + " must be an absolute path outside the repository.");
        var path = Path.GetFullPath(configured);
        var repository = Path.GetFullPath(environment.ContentRootPath);
        for (var directory = new DirectoryInfo(repository); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
            { repository = directory.FullName; break; }
        if (path.Equals(repository, StringComparison.OrdinalIgnoreCase) || path.StartsWith(repository.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(setting + " must be outside the repository.");
        RejectReparsePoints(path);
        return path;
    }

    internal static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links and reparse points are not permitted in package paths.");
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        RejectReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("Package file exceeds its size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteNew(string destination, byte[] bytes)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
