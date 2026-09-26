using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PupilDesktop.Core;

public sealed partial class HitomiClient
{
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
                // One suggestion index can fail independently; keep suggestions from the others.
            }
        }

        return output
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
    }

    private async Task<List<string>> GetTagSuggestionsForFieldAsync(string field, string term, bool throwIfMissing, CancellationToken ct)
    {
        var firstNode = await GetNodeAtAddressAsync(field, 0, ct);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(term))[..4];
        var data = await BSearchAsync(field, key, firstNode, ct);
        if (data is null)
        {
            if (throwIfMissing)
                throw new InvalidDataException($"No tag suggestion index entry for {field}:{term}");
            return [];
        }

        var version = await GetTagIndexVersionAsync(ct);
        var url = $"https://{Domain}/tagindex/{field}.{version}.data";
        var bytes = await GetRangeAsync(url, data.Value.offset, data.Value.offset + data.Value.length - 1, ct);
        return DecodeTagSuggestionData(bytes, field);
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
