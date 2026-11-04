using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GreatMaoNet.Updates;

public sealed class ReleaseClient(HttpClient http, UpdateOptions options, string application, string runtime)
{
    public const long MaxPackageBytes = 512L * 1024 * 1024;

    public async Task<AvailableUpdate?> CheckAsync(string currentVersion, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{options.Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd($"{application}/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(await response.Content.ReadAsStreamAsync(token), buffer, 1024 * 1024, token);
        var release = JsonSerializer.Deserialize(buffer.ToArray(), UpdateJson.Default.GitHubRelease)
            ?? throw new InvalidDataException("Empty release response.");
        if (release.Draft || release.Prerelease) return null;
        var version = UpdateOptions.ParseVersion(release.Tag);
        if (version <= UpdateOptions.ParseVersion(currentVersion)) return null;
        string name = $"{application}-{runtime}.zip";
        var package = release.Assets.SingleOrDefault(x => x.Name == name) ?? throw new InvalidDataException($"Release missing {name}.");
        var checksum = release.Assets.SingleOrDefault(x => x.Name == name + ".sha256") ?? throw new InvalidDataException("Release missing SHA-256 checksum.");
        ValidateAsset(package, release.Tag, MaxPackageBytes);
        ValidateAsset(checksum, release.Tag, 1024);
        return new(version.ToString(), package, checksum);
    }

    private void ValidateAsset(ReleaseAsset asset, string tag, long limit)
    {
        string expected = $"https://github.com/{options.Repository}/releases/download/{tag}/{asset.Name}";
        if (!string.Equals(asset.Url, expected, StringComparison.Ordinal) || asset.Size <= 0 || asset.Size > limit)
            throw new InvalidDataException("Invalid release asset URL or size.");
    }

    public async Task DownloadAsync(AvailableUpdate update, string destination, CancellationToken token)
    {
        using var checksumBuffer = new MemoryStream();
        await DownloadToAsync(update.Checksum, checksumBuffer, 1024, token);
        string[] parts = Encoding.UTF8.GetString(checksumBuffer.ToArray()).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit) || parts[1] != update.Package.Name)
            throw new InvalidDataException("Invalid SHA-256 checksum file.");
        if (File.Exists(destination))
        {
            await using var cached = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (cached.Length == update.Package.Size && CryptographicOperations.FixedTimeEquals(
                    await SHA256.HashDataAsync(cached, token), Convert.FromHexString(parts[0]))) return;
        }
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                await DownloadToAsync(update.Package, output, MaxPackageBytes, token);
                output.Position = 0;
                byte[] digest = await SHA256.HashDataAsync(output, token);
                if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(parts[0])))
                    throw new InvalidDataException("Release package SHA-256 mismatch.");
                output.Flush(true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task DownloadToAsync(ReleaseAsset asset, Stream output, long limit, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.UserAgent.ParseAdd($"{application}/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        long size = await CopyBoundedAsync(await response.Content.ReadAsStreamAsync(token), output, limit, token);
        if (size != asset.Size) throw new InvalidDataException("Incomplete release asset download.");
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("Release asset exceeds size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return total;
    }
}
