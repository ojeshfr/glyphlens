// Release tool for GlyphLens' updater (a .NET 10 file-based app; nothing to install).
//
//   dotnet run tools/UpdateFeed.cs -- keygen <private-key.pem> <public-key.pem>
//   dotnet run tools/UpdateFeed.cs -- sign <private-key.pem> <installer> <download-url> <version> <notes-file> <out-feed.json>
//
// keygen  makes an ECDSA P-256 key pair. KEEP THE PRIVATE KEY OUT OF THE REPOSITORY (a password manager or offline disk). Put the public
//         key into src/GlyphLens.App/Updates/update-config.json (publicKeyPem) together with the feed's HTTPS URL (feedUrl).
// sign    hashes the installer and writes the signed feed the app reads: { "manifest": base64, "signature": base64 }.
//         Publish the installer at <download-url> (HTTPS, ending in .exe or .msi) and the feed at feedUrl.

#:property PublishAot=false
#:property TreatWarningsAsErrors=false
#:property NuGetAudit=false

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length >= 3 && args[0] == "keygen")
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(args[1], key.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(args[2], key.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine($"Private key: {args[1]} (keep it secret and out of Git)");
    Console.WriteLine($"Public key:  {args[2]} (paste into update-config.json as publicKeyPem)");
    return 0;
}

if (args.Length >= 7 && args[0] == "sign")
{
    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(args[1]));
    string installer = args[2];
    byte[] bytes = File.ReadAllBytes(installer);
    string version = args[4];
    if (!Version.TryParse(version, out _))
    {
        Console.Error.WriteLine("The version must look like 0.2.0.");
        return 1;
    }

    var manifest = JsonSerializer.SerializeToUtf8Bytes(new
    {
        version,
        url = args[3],
        sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        size = (long)bytes.Length,
        published = DateTimeOffset.UtcNow.ToString("o"),
        notes = File.ReadAllText(args[5]).Trim(),
    });
    byte[] signature = key.SignData(manifest, HashAlgorithmName.SHA256);
    File.WriteAllText(args[6], JsonSerializer.Serialize(new { manifest = Convert.ToBase64String(manifest), signature = Convert.ToBase64String(signature) }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    Console.WriteLine($"Signed feed for {version} ({bytes.Length:N0} bytes) written to {args[6]}");
    return 0;
}

Console.Error.WriteLine("Usage: keygen <private.pem> <public.pem>  |  sign <private.pem> <installer> <url> <version> <notes-file> <out.json>");
return 2;
