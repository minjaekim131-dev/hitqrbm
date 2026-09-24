using System.Text.Json;

namespace PupilDesktop.Core;

public sealed class AppState
{
    public HashSet<int> FavoriteGalleryIds { get; set; } = [];
    public bool OpenFavoritesOnStartup { get; set; }
}

public sealed class AppStateStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public AppState State { get; private set; }

    public AppStateStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PupilDesktop");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "settings.json");
        State = Load();
    }

    private AppState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppState();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
        }
        catch
        {
            return new AppState();
        }
    }

    public bool IsFavorite(int galleryId) => State.FavoriteGalleryIds.Contains(galleryId);

    public void ToggleFavorite(int galleryId)
    {
        if (!State.FavoriteGalleryIds.Add(galleryId))
            State.FavoriteGalleryIds.Remove(galleryId);
        Save();
    }

    public void SetOpenFavoritesOnStartup(bool value)
    {
        State.OpenFavoritesOnStartup = value;
        Save();
    }

    public void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(State, _jsonOptions));
        File.Move(temp, _path, true);
    }
}
