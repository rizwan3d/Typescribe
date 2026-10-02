using System.Net.Http.Headers;

namespace Typescribe.Desktop;

internal static class PublicDictionaryBootstrapper
{
    private const string LibreOfficeRevision = "32b006a2c22a4ac7e8ed3f03346f7b3d85a970a4";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(4);

    private static readonly string[] DictionaryFiles =
    [
        "en_US.dic",
        "en_GB.dic",
        "en_CA.dic",
        "en_AU.dic"
    ];

    public static void EnsureInstalled()
    {
        try
        {
            var dictionaryDirectory = GetDictionaryDirectory();
            Directory.CreateDirectory(dictionaryDirectory);

            var missing = DictionaryFiles
                .Where(fileName => !IsUsableDictionary(Path.Combine(dictionaryDirectory, fileName)))
                .ToArray();
            if (missing.Length == 0) return;

            using var cancellation = new CancellationTokenSource(DownloadTimeout);
            using var client = CreateHttpClient();
            var downloads = missing
                .Select(fileName => TryDownloadAsync(client, dictionaryDirectory, fileName, cancellation.Token))
                .ToArray();

            Task.WhenAll(downloads).GetAwaiter().GetResult();
        }
        catch
        {
            // Spell checking already has local/system dictionary discovery and a small fallback list.
            // A network failure must never prevent Typescribe from starting.
        }
    }

    private static async Task TryDownloadAsync(
        HttpClient client,
        string dictionaryDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(dictionaryDirectory, fileName);
        var temporary = destination + ".download";

        try
        {
            var source = $"https://raw.githubusercontent.com/LibreOffice/dictionaries/{LibreOfficeRevision}/en/{fileName}";
            using var response = await client.GetAsync(
                source,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            if (!IsUsableDictionary(temporary))
                throw new InvalidDataException($"Downloaded dictionary '{fileName}' is not valid.");

            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Typescribe", "1.0"));
        return client;
    }

    private static string GetDictionaryDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        return Path.Combine(root, "Typescribe", "dictionaries");
    }

    private static bool IsUsableDictionary(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 64 * 1024) return false;

            var firstLine = File.ReadLines(path).FirstOrDefault()?.Trim().TrimStart('\uFEFF');
            return int.TryParse(firstLine, out var entryCount) && entryCount > 1_000;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
