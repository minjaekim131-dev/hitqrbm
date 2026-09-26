using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace PupilDesktop.Core;

public sealed partial class HitomiClient
{
    private const string SuggestionDomain = "ltn.hitomi.la";
    private string? _suggestionTagIndexVersion;

    public async Task<List<string>> GetTagSuggestionsAsync(string input, CancellationToken ct = default)
    {
        var text = input.Trim().ToLowerInvariant().Replace('_', ' ');
        if (string.IsNullOrWhiteSpace(text)) return [];

        string term = text;
        string[] fields = ["tag", "female", "male"];
        var explicitField = false;

        var idx = text.IndexOf(':');
        if (idx > 0)
        {
            var requestedField = text[..idx].Trim();
            term = text[(idx + 1)..].Trim();
            if (requestedField is "tag" or "female" or "male")
            {
                fields = [requestedField];
                explicitField = true;
            }
        }

        if (string.IsNullOrWhiteSpace(term)) return [];

        var output = new List<string>();
        foreach (var field in fields)
        {
            try
            {
                foreach (var item in await GetTagSuggestionsForFieldAsync(field, term, explicitField, ct))
                    output.Add(item);
            }
            catch (OperationCanceledException) { throw; }
            catch when (!explicitField)
            {
                // Keep suggestions from the other indexes when one category has no match.
            }
        }

        return output
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
    }

    private async Task<List<string>> GetTagSuggestionsForFieldAsync(string field, string term, bool throwIfMissing, CancellationToken ct)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(term))[..4];
        var data = await FindSuggestionDataAsync(field, key, ct);
        if (data is null)
        {
            if (throwIfMissing)
                throw new InvalidDataException($"No tag suggestion index entry for {field}:{term}");
            return [];
        }

        var version = await GetSuggestionTagIndexVersionAsync(ct);
        var url = $"https://{SuggestionDomain}/tagindex/{field}.{version}.data";
        var bytes = await GetSuggestionRangeAsync(url, data.Value.offset, data.Value.offset + data.Value.length, ct);
        return DecodeTagSuggestionData(bytes, field);
    }

    private async Task<(long offset, int length)?> FindSuggestionDataAsync(string field, byte[] key, CancellationToken ct)
    {
        var node = await GetSuggestionNodeAsync(field, 0, ct);
        while (true)
        {
            if (node.Keys.Count == 0) return null;
            var (there, where) = LocateKey(key, node.Keys);
            if (there) return node.Datas[where];
            if (node.SubNodeAddresses.All(x => x == 0)) return null;
            if (where >= node.SubNodeAddresses.Count || node.SubNodeAddresses[where] == 0) return null;
            node = await GetSuggestionNodeAsync(field, node.SubNodeAddresses[where], ct);
        }
    }

    private async Task<Node> GetSuggestionNodeAsync(string field, long address, CancellationToken ct)
    {
        var version = await GetSuggestionTagIndexVersionAsync(ct);
        var url = $"https://{SuggestionDomain}/tagindex/{field}.{version}.index";
        var bytes = await GetSuggestionRangeAsync(url, address, address + MaxNodeSize - 1, ct);
        return DecodeNode(bytes);
    }

    private async Task<string> GetSuggestionTagIndexVersionAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_suggestionTagIndexVersion)) return _suggestionTagIndexVersion;
        using var res = await _http.GetAsync($"https://{SuggestionDomain}/tagindex/version?_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", ct);
        res.EnsureSuccessStatusCode();
        _suggestionTagIndexVersion = (await res.Content.ReadAsStringAsync(ct)).Trim();
        return _suggestionTagIndexVersion;
    }

    private async Task<byte[]> GetSuggestionRangeAsync(string url, long first, long last, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(first, last);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    private static List<string> DecodeTagSuggestionData(byte[] data, string fallbackField)
    {
        if (data.Length < 4) throw new InvalidDataException("Tag suggestion data is too short.");
        var offset = 0;

        int ReadInt()
        {
            if (offset + 4 > data.Length) throw new EndOfStreamException();
            var value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
            offset += 4;
            return value;
        }

        string ReadString(int length)
        {
            if (length < 0 || offset + length > data.Length) throw new EndOfStreamException();
            var value = Encoding.UTF8.GetString(data, offset, length);
            offset += length;
            return value;
        }

        var count = ReadInt();
        if (count < 0 || count > 500)
            throw new InvalidDataException($"Invalid tag suggestion count: {count}");

        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var headerLength = ReadInt();
            var header = ReadString(headerLength).Trim().ToLowerInvariant();
            var tagLength = ReadInt();
            var tag = ReadString(tagLength).Trim();
            _ = ReadInt();

            if (string.IsNullOrWhiteSpace(tag)) continue;
            var prefix = header is "tag" or "female" or "male" ? header : fallbackField;
            result.Add($"{prefix}:{tag.Replace(' ', '_')}");
        }
        return result;
    }
}
