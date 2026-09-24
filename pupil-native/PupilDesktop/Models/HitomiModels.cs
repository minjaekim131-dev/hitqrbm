using System.Text.Json.Serialization;

namespace PupilDesktop.Models;

public sealed class Artist { [JsonPropertyName("artist")] public string ArtistName { get; set; } = ""; public string Url { get; set; } = ""; }
public sealed class Group { [JsonPropertyName("group")] public string GroupName { get; set; } = ""; public string Url { get; set; } = ""; }
public sealed class Parody { [JsonPropertyName("parody")] public string ParodyName { get; set; } = ""; public string Url { get; set; } = ""; }
public sealed class Character { [JsonPropertyName("character")] public string CharacterName { get; set; } = ""; public string Url { get; set; } = ""; }
public sealed class TagInfo
{
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Female { get; set; }
    public string? Male { get; set; }
}
public sealed class LanguageInfo
{
    public string Galleryid { get; set; } = "";
    public string Url { get; set; } = "";
    public string Language_localname { get; set; } = "";
    public string Name { get; set; } = "";
}
public sealed class GalleryFile
{
    public int Width { get; set; }
    public string Hash { get; set; } = "";
    public int Haswebp { get; set; }
    public string Name { get; set; } = "";
    public int Height { get; set; }
    public int Hasavif { get; set; }
    public int? Hasavifsmalltn { get; set; }
}
public sealed class GalleryInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Japanese_title { get; set; }
    public string? Language { get; set; }
    public string Type { get; set; } = "";
    public string Date { get; set; } = "";
    public List<Artist>? Artists { get; set; }
    public List<Group>? Groups { get; set; }
    public List<Parody>? Parodys { get; set; }
    public List<TagInfo>? Tags { get; set; }
    public List<int> Related { get; set; } = [];
    public List<LanguageInfo> Languages { get; set; } = [];
    public List<Character>? Characters { get; set; }
    public List<int>? Scene_indexes { get; set; }
    public List<GalleryFile> Files { get; set; } = [];
}

public sealed record GalleryCard(int Id, GalleryInfo Info, string CoverUrl, string FallbackCoverUrl);

public enum SortMode
{
    DateAdded,
    DatePublished,
    PopularToday,
    PopularWeek,
    PopularMonth,
    PopularYear,
    Random
}
