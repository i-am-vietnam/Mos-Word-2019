using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MosWord2019.Server.Data;
using MosWord2019.Server.Packages;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MosWord2019.Server.Tests;

public sealed class PackageStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mosword-package-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestEnvironment environment;
    private readonly ConfigurationManager configuration = new();
    private readonly PackageStorage storage;

    public PackageStorageTests()
    {
        Directory.CreateDirectory(root);
        environment = new TestEnvironment { ContentRootPath = Path.Combine(root, "repository") };
        Directory.CreateDirectory(environment.ContentRootPath);
        configuration["Packages:StorageRoot"] = Path.Combine(root, "storage");
        configuration["Packages:SigningKeyPath"] = Path.Combine(root, "signing.key");
        File.WriteAllBytes(configuration["Packages:SigningKeyPath"]!, RandomNumberGenerator.GetBytes(32));
        storage = new PackageStorage(configuration, environment);
    }

    [Fact]
    public void BlobUsesAuthenticatedEncryptionAndExpectedEnvelope()
    {
        var package = NewPackage();
        var zip = Encoding.UTF8.GetBytes("test zip content");
        storage.Store(package, zip);
        var blob = storage.ReadBlob(package);
        Assert.Equal("MWPKG001", Encoding.ASCII.GetString(blob, 0, 8));
        Assert.Equal(zip.Length + 36, package.SizeBytes);
        Assert.DoesNotContain("test zip content", Encoding.UTF8.GetString(blob));
        var plaintext = new byte[zip.Length];
        using var aes = new AesGcm(storage.ReadKey(package), 16);
        aes.Decrypt(blob.AsSpan(8, 12), blob.AsSpan(36), blob.AsSpan(20, 16), plaintext, PackageStorage.AdditionalData(package));
        Assert.Equal(zip, plaintext);
        package.Code = "Word2019_P02";
        Assert.Throws<AuthenticationTagMismatchException>(() => aes.Decrypt(blob.AsSpan(8, 12), blob.AsSpan(36), blob.AsSpan(20, 16), plaintext, PackageStorage.AdditionalData(package)));
    }

    [Fact]
    public void EveryPackageGetsIndependentNonceAndKey()
    {
        var first = NewPackage();
        var second = NewPackage();
        storage.Store(first, new byte[] { 1, 2, 3 });
        storage.Store(second, new byte[] { 1, 2, 3 });
        Assert.NotEqual(storage.ReadKey(first), storage.ReadKey(second));
        Assert.NotEqual(storage.ReadBlob(first), storage.ReadBlob(second));
    }

    [Fact]
    public void TamperedBlobAndStorageTraversalAreRejected()
    {
        var package = NewPackage();
        storage.Store(package, new byte[] { 1 });
        var path = Path.Combine(configuration["Packages:StorageRoot"]!, package.StoragePath);
        var blob = File.ReadAllBytes(path);
        blob[^1] ^= 1;
        File.WriteAllBytes(path, blob);
        Assert.Throws<InvalidDataException>(() => storage.ReadBlob(package));
        package.StoragePath = "../signing.key";
        Assert.Throws<InvalidDataException>(() => storage.ReadBlob(package));
    }

    [Fact]
    public void ManifestSignatureVerifiesOnlyExactPayload()
    {
        var payload = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
        var signature = storage.Sign(payload);
        Assert.Equal(64, signature.Length);
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(storage.GetPublicKey()));
        verifier.BlockUpdate(payload, 0, payload.Length);
        Assert.True(verifier.VerifySignature(signature));
        verifier.Reset();
        payload[^1] ^= 1;
        verifier.BlockUpdate(payload, 0, payload.Length);
        Assert.False(verifier.VerifySignature(signature));
    }

    [Fact]
    public void PrivateConfigurationMustBeAbsoluteAndOutsideRepository()
    {
        configuration["Packages:StorageRoot"] = Path.Combine(environment.ContentRootPath, "secrets");
        Assert.Throws<InvalidOperationException>(() => storage.Store(NewPackage(), Array.Empty<byte>()));
        configuration["Packages:SigningKeyPath"] = "relative.key";
        Assert.Throws<InvalidOperationException>(() => storage.Sign(Array.Empty<byte>()));
    }

    [Fact]
    public void InvalidSigningSeedAndOversizedBlobAreRejected()
    {
        File.WriteAllBytes(configuration["Packages:SigningKeyPath"]!, new byte[31]);
        Assert.Throws<InvalidDataException>(() => storage.GetPublicKey());
        Assert.Throws<InvalidDataException>(() => storage.Store(NewPackage(), new byte[PackageStorage.MaximumBlobBytes]));
    }

    [Fact]
    public async Task InvalidImportFailsBeforeDatabaseOrEncryptedWrites()
    {
        var source = Path.Combine(root, "invalid-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "testing-catalog.json"), "[]");
        await using var database = new ServerDbContext(new DbContextOptionsBuilder<ServerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1").Options);
        var importer = new PackageImporter(database, storage, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(source));
        Assert.False(Directory.Exists(configuration["Packages:StorageRoot"]));
    }

    private static Package NewPackage() => new() { Code = "Word2019_P01", Version = new string('a', 64) };

    [Fact]
    public void ReconciliationRemovesOnlyOwnedUnreferencedFilesAndInterruptedTemporaryFiles()
    {
        var live = NewPackage();
        var abandoned = NewPackage();
        storage.Store(live, new byte[] { 1 });
        storage.Store(abandoned, new byte[] { 2 });
        var storageRoot = configuration["Packages:StorageRoot"]!;
        var interrupted = Path.Combine(storageRoot, live.Id.ToString("N") + ".key." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(interrupted, "interrupted write");
        var unrelated = Path.Combine(storageRoot, "owner-notes.txt");
        File.WriteAllText(unrelated, "preserve");
        var ambiguous = Path.Combine(storageRoot, "not-a-guid.key");
        File.WriteAllText(ambiguous, "preserve");
        Assert.Equal(3, storage.RemoveUnreferenced(new HashSet<Guid> { live.Id }));
        Assert.NotEmpty(storage.ReadBlob(live));
        Assert.Equal(32, storage.ReadKey(live).Length);
        Assert.False(File.Exists(Path.Combine(storageRoot, abandoned.StoragePath)));
        Assert.False(File.Exists(Path.Combine(storageRoot, abandoned.Id.ToString("N") + ".key")));
        Assert.False(File.Exists(interrupted));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(ambiguous));
        Assert.Equal(0, storage.RemoveUnreferenced(new HashSet<Guid> { live.Id }));
    }

    public void Dispose()
    {
        // The test owns this exact randomly generated temporary root.
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "PackageStorageTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
