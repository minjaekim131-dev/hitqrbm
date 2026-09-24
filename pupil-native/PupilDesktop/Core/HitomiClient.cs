using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PupilDesktop.Models;

namespace PupilDesktop.Core;

public sealed partial class HitomiClient : IDisposable
{
    private const string Domain = "ltn.gold-usergeneratedcontent.net";
    private const string TagIndexDomain = "tagindex.hitomi.la";
    private const int MaxNodeSize = 464;
    private const int B = 16;

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _ggLock = new(1, 1);
    private DateTimeOffset _ggLast = DateTimeOffset.MinValue;
    private int _ggDefault;
    private readonly Dictionary<int, int> _ggMap = [];
    private string _ggB = "";
    private string? _tagIndexVersion;
    private string? _galleriesIndexVersion;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    public HitomiClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 12
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://hitomi.la/");
    }

    public void Dispose()
    {
        _http.Dispose();
        _ggLock.Dispose();
    }

    public async Task<GalleryInfo> GetGalleryInfoAsync(int galleryId, CancellationToken ct = default)
    {
        var raw = await GetStringAsync($"https://{Domain}/galleries/{galleryId}.js", ct);
        raw = raw.Replace("var galleryinfo = ", "", StringComparison.Ordinal).Trim();
        raw = raw.TrimEnd(';', '\r', '\n', ' ');
        return JsonSerializer.Deserialize<GalleryInfo>(raw, _jsonOptions)
               ?? throw new InvalidDataException("Gallery metadata could not be parsed.");
    }

    public async Task<GalleryCard> GetGalleryCardAsync(int galleryId, CancellationToken ct = default)
    {
        var info = await GetGalleryInfoAsync(galleryId, ct);
        if (info.Files.Count == 0) throw new InvalidDataException("Gallery has no files.");
        var cover = await UrlFromUrlFromHashAsync(info.Files[0], "webpbigtn", "webp", "tn", ct);
        var fallback = await UrlFromUrlFromHashAsync(info.Files[0], "images", null, null, ct);
        return new GalleryCard(galleryId, info, cover, fallback);
    }

    public async Task<List<string>> GetReaderImageUrlsAsync(GalleryInfo info, CancellationToken ct = default)
    {
        var urls = new List<string>(info.Files.Count);
        foreach (var file in info.Files)
            urls.Add(await UrlFromUrlFromHashAsync(file, "images", null, null, ct));
        return urls;
    }

    public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<List<int>> SearchAsync(string query, SortMode sortMode, CancellationToken ct = default)
    {
        var terms = Regex.Split(query.Trim().TrimStart('?').ToLowerInvariant(), @"\s+")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Replace('_', ' '))
            .ToList();

        var positive = terms.Where(x => !x.StartsWith('-')).ToList();
        var negative = terms.Where(x => x.StartsWith('-') && x.Length > 1).Select(x => x[1..]).ToList();

        List<int> result;
        if (positive.Count == 0)
        {
            result = await GetGalleryIdsFromNozomiAsync(new SearchArgs("all", "index", "all"), sortMode, ct);
        }
        else
        {
            result = await GetGalleryIdsForQueryAsync(positive[0], sortMode, ct);
            for (var i = 1; i < positive.Count; i++)
            {
                var next = (await GetGalleryIdsForQueryAsync(positive[i], sortMode, ct)).ToHashSet();
                result = result.Where(next.Contains).ToList();
            }
        }

        foreach (var term in negative)
        {
            var excluded = (await GetGalleryIdsForQueryAsync(term, sortMode, ct)).ToHashSet();
            result = result.Where(x => !excluded.Contains(x)).ToList();
        }

        if (sortMode == SortMode.Random)
        {
            var rng = Random.Shared;
            result = result.OrderBy(_ => rng.Next()).ToList();
        }
        return result;
    }

    private async Task<List<int>> GetGalleryIdsForQueryAsync(string query, SortMode sortMode, CancellationToken ct)
    {
        var sanitized = query.Replace('_', ' ');
        var args = SearchArgs.FromQuery(sanitized);
        if (args is not null)
            return await GetGalleryIdsFromNozomiAsync(args, sortMode, ct);

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(sanitized))[..4];
        var node = await GetNodeAtAddressAsync("galleries", 0, ct);
        var data = await BSearchAsync("galleries", key, node, ct);
        return data is null ? [] : await GetGalleryIdsFromDataAsync(data.Value, ct);
    }

    private async Task<List<int>> GetGalleryIdsFromNozomiAsync(SearchArgs args, SortMode sortMode, CancellationToken ct)
    {
        var url = NozomiAddress(args, sortMode);
        var bytes = await DownloadBytesAsync(url, ct);
        var ids = new List<int>(bytes.Length / 4);
        for (var i = 0; i + 4 <= bytes.Length; i += 4)
            ids.Add(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i, 4)));
        return ids;
    }

    private string NozomiAddress(SearchArgs a, SortMode sortMode)
    {
        var (orderBy, key) = SortParts(sortMode);
        if (sortMode is not SortMode.DateAdded and not SortMode.Random)
            return a.Area == "all"
                ? $"https://{Domain}/n/{orderBy}/{key}-{a.Language}.nozomi"
                : $"https://{Domain}/n/{a.Area}/{orderBy}/{key}/{a.Tag}-{a.Language}.nozomi";
        return a.Area == "all"
            ? $"https://{Domain}/n/{a.Tag}-{a.Language}.nozomi"
            : $"https://{Domain}/n/{a.Area}/{a.Tag}-{a.Language}.nozomi";
    }

    private static (string orderBy, string key) SortParts(SortMode mode) => mode switch
    {
        SortMode.DateAdded => ("date", "added"),
        SortMode.DatePublished => ("date", "published"),
        SortMode.PopularToday => ("popular", "today"),
        SortMode.PopularWeek => ("popular", "week"),
        SortMode.PopularMonth => ("popular", "month"),
        SortMode.PopularYear => ("popular", "year"),
        SortMode.Random => ("date", "added"),
        _ => ("date", "added")
    };

    private async Task<string> GetTagIndexVersionAsync(CancellationToken ct) =>
        _tagIndexVersion ??= (await GetStringAsync($"https://{Domain}/tagindex/version?_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", ct)).Trim();

    private async Task<string> GetGalleriesIndexVersionAsync(CancellationToken ct) =>
        _galleriesIndexVersion ??= (await GetStringAsync($"https://{Domain}/galleriesindex/version?_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", ct)).Trim();

    private async Task<Node> GetNodeAtAddressAsync(string field, long address, CancellationToken ct)
    {
        string url;
        if (field == "galleries")
            url = $"https://{Domain}/galleriesindex/galleries.{await GetGalleriesIndexVersionAsync(ct)}.index";
        else if (field == "languages")
            url = $"https://{Domain}/galleriesindex/languages.{await GetGalleriesIndexVersionAsync(ct)}.index";
        else if (field == "nozomiurl")
            url = $"https://{Domain}/galleriesindex/nozomiurl.{await GetGalleriesIndexVersionAsync(ct)}.index";
        else
            url = $"https://{Domain}/tagindex/{field}.{await GetTagIndexVersionAsync(ct)}.index";

        var bytes = await GetRangeAsync(url, address, address + MaxNodeSize - 1, ct);
        return DecodeNode(bytes);
    }

    private async Task<(long offset, int length)?> BSearchAsync(string field, byte[] key, Node node, CancellationToken ct)
    {
        while (true)
        {
            if (node.Keys.Count == 0) return null;
            var (there, where) = LocateKey(key, node.Keys);
            if (there) return node.Datas[where];
            if (node.SubNodeAddresses.All(x => x == 0)) return null;
            if (where >= node.SubNodeAddresses.Count || node.SubNodeAddresses[where] == 0) return null;
            node = await GetNodeAtAddressAsync(field, node.SubNodeAddresses[where], ct);
        }
    }

    private static (bool there, int where) LocateKey(byte[] key, List<byte[]> keys)
    {
        for (var i = 0; i < keys.Count; i++)
        {
            var cmp = CompareBytes(key, keys[i]);
            if (cmp <= 0) return (cmp == 0, i);
        }
        return (false, keys.Count);
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        var top = Math.Min(a.Length, b.Length);
        for (var i = 0; i < top; i++)
        {
            if (a[i] < b[i]) return -1;
            if (a[i] > b[i]) return 1;
        }
        return 0;
    }

    private async Task<List<int>> GetGalleryIdsFromDataAsync((long offset, int length) data, CancellationToken ct)
    {
        if (data.length <= 0 || data.length > 100_000_000) throw new InvalidDataException("Invalid index data length.");
        var version = await GetGalleriesIndexVersionAsync(ct);
        var url = $"https://{Domain}/galleriesindex/galleries.{version}.data";
        var bytes = await GetRangeAsync(url, data.offset, data.offset + data.length - 1, ct);
        if (bytes.Length < 4) return [];
        var count = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0, 4));
        if (count <= 0 || count > 10_000_000 || bytes.Length != count * 4 + 4) return [];
        var ids = new List<int>(count);
        for (var i = 0; i < count; i++) ids.Add(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4 + i * 4, 4)));
        return ids;
    }

    private static Node DecodeNode(byte[] data)
    {
        var offset = 0;
        int ReadInt()
        {
            if (offset + 4 > data.Length) throw new EndOfStreamException();
            var v = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
            offset += 4;
            return v;
        }
        long ReadLong()
        {
            if (offset + 8 > data.Length) throw new EndOfStreamException();
            var v = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(offset, 8));
            offset += 8;
            return v;
        }

        var keyCount = ReadInt();
        if (keyCount < 0 || keyCount > B) throw new InvalidDataException("Invalid node key count.");
        var keys = new List<byte[]>(keyCount);
        for (var i = 0; i < keyCount; i++)
        {
            var size = ReadInt();
            if (size <= 0 || size > 32 || offset + size > data.Length) throw new InvalidDataException("Invalid node key size.");
            keys.Add(data.AsSpan(offset, size).ToArray());
            offset += size;
        }

        var dataCount = ReadInt();
        if (dataCount < 0 || dataCount > B) throw new InvalidDataException("Invalid node data count.");
        var datas = new List<(long offset, int length)>(dataCount);
        for (var i = 0; i < dataCount; i++) datas.Add((ReadLong(), ReadInt()));

        var sub = new List<long>(B + 1);
        for (var i = 0; i < B + 1; i++) sub.Add(ReadLong());
        return new Node(keys, datas, sub);
    }

    private async Task<byte[]> GetRangeAsync(string url, long first, long last, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(first, last);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var res = await _http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    private async Task EnsureGgAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _ggLast < TimeSpan.FromMinutes(1)) return;
        await _ggLock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _ggLast < TimeSpan.FromMinutes(1)) return;
            var js = await GetStringAsync($"https://{Domain}/gg.js", ct);
            _ggDefault = int.Parse(Regex.Match(js, @"var o = (\d)").Groups[1].Value);
            var common = int.Parse(Regex.Match(js, @"o = (\d); break;").Groups[1].Value);
            _ggMap.Clear();
            foreach (Match m in Regex.Matches(js, @"case (\d+):")) _ggMap[int.Parse(m.Groups[1].Value)] = common;
            _ggB = Regex.Match(js, @"b: '(.+)'", RegexOptions.Multiline).Groups[1].Value;
            _ggLast = DateTimeOffset.UtcNow;
        }
        finally { _ggLock.Release(); }
    }

    private async Task<int> GgMAsync(int g, CancellationToken ct)
    {
        await EnsureGgAsync(ct);
        return _ggMap.TryGetValue(g, out var v) ? v : _ggDefault;
    }

    private async Task<string> GgBAsync(CancellationToken ct)
    {
        await EnsureGgAsync(ct);
        return _ggB;
    }

    private static string GgS(string hash)
    {
        if (hash.Length < 3) throw new ArgumentException("Invalid hash.");
        var hex = hash[^1] + hash.Substring(hash.Length - 3, 2);
        return Convert.ToInt32(hex, 16).ToString();
    }

    private async Task<string> SubdomainFromUrlAsync(string url, string? baseName, string? dir, CancellationToken ct)
    {
        var prefix = string.IsNullOrWhiteSpace(baseName) ? (dir == "webp" ? "w" : dir == "avif" ? "a" : "") : "";
        var match = Regex.Match(url, @"/[0-9a-f]{61}([0-9a-f]{2})([0-9a-f])", RegexOptions.IgnoreCase);
        if (!match.Success) return "";
        var g = Convert.ToInt32(match.Groups[2].Value + match.Groups[1].Value, 16);
        var m = await GgMAsync(g, ct);
        return string.IsNullOrEmpty(baseName) ? prefix + (1 + m) : ((char)(97 + m)) + baseName;
    }

    private async Task<string> UrlFromUrlAsync(string url, string? baseName, string? dir, CancellationToken ct)
    {
        var sub = await SubdomainFromUrlAsync(url, baseName, dir, ct);
        return Regex.Replace(url, @"//..?\.(?:gold-usergeneratedcontent\.net|hitomi\.la)/", $"//{sub}.gold-usergeneratedcontent.net/", RegexOptions.IgnoreCase);
    }

    private async Task<string> FullPathFromHashAsync(string hash, CancellationToken ct) => $"{await GgBAsync(ct)}{GgS(hash)}/{hash}";

    private static string RealFullPathFromHash(string hash)
    {
        if (hash.Length < 3) throw new ArgumentException("Invalid hash.");
        return $"{hash[^1]}/{hash.Substring(hash.Length - 3, 2)}/{hash}";
    }

    private async Task<string> UrlFromHashAsync(GalleryFile image, string? dir, string? ext, CancellationToken ct)
    {
        ext ??= dir ?? Path.GetExtension(image.Name).TrimStart('.');
        var sb = new StringBuilder("https://a.gold-usergeneratedcontent.net/");
        if (dir is not "webp" and not "avif" && !string.IsNullOrEmpty(dir)) sb.Append(dir).Append('/');
        sb.Append(await FullPathFromHashAsync(image.Hash, ct)).Append('.').Append(ext);
        return sb.ToString();
    }

    private async Task<string> UrlFromUrlFromHashAsync(GalleryFile image, string? dir, string? ext, string? baseName, CancellationToken ct)
    {
        if (baseName == "tn")
        {
            var e = ext ?? dir ?? Path.GetExtension(image.Name).TrimStart('.');
            return await UrlFromUrlAsync($"https://a.gold-usergeneratedcontent.net/{dir}/{RealFullPathFromHash(image.Hash)}.{e}", baseName, null, ct);
        }
        return await UrlFromUrlAsync(await UrlFromHashAsync(image, dir, ext, ct), baseName, dir, ct);
    }

    private sealed record SearchArgs(string? Area, string Tag, string Language)
    {
        public static SearchArgs? FromQuery(string query)
        {
            var idx = query.IndexOf(':');
            if (idx < 0) return null;
            var left = query[..idx];
            var right = query[(idx + 1)..];
            return left switch
            {
                "male" or "female" => new SearchArgs("tag", query, "all"),
                "language" => new SearchArgs("all", "index", right),
                _ => new SearchArgs(left, right, "all")
            };
        }
    }

    private sealed record Node(List<byte[]> Keys, List<(long offset, int length)> Datas, List<long> SubNodeAddresses);
}
