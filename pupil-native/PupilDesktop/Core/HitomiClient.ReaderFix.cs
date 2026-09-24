using PupilDesktop.Models;

namespace PupilDesktop.Core;

public sealed partial class HitomiClient
{
    public async Task<List<string>> GetReaderWebpImageUrlsAsync(GalleryInfo info, CancellationToken ct = default)
    {
        var urls = new List<string>(info.Files.Count);
        foreach (var file in info.Files)
            urls.Add(await UrlFromUrlFromHashAsync(file, "webp", null, null, ct));
        return urls;
    }
}
