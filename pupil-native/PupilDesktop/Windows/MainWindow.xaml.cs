using Microsoft.VisualBasic;
using PupilDesktop.Core;
using PupilDesktop.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PupilDesktop.Windows;

public partial class MainWindow : Window
{
    private readonly HitomiClient _client = new();
    private readonly AppStateStore _stateStore = new();
    private CancellationTokenSource? _cts;
    private List<int> _results = [];
    private int _page;
    private bool _showingFavorites;
    private bool _initializing = true;
    private string _searchBeforeFavorites = "";
    private const int PerPage = 20;

    private readonly Dictionary<int, int> _pageStartIndices = new() { [0] = 0 };
    private readonly Dictionary<int, (GalleryCard Card, BitmapImage? Bitmap)> _cardCache = [];
    private readonly HashSet<int> _failedCardIds = [];
    private bool _currentPageHasNext;

    private static readonly (string Label, string Slug)[] LanguageOptions =
    [
        ("모든 언어", ""),
        ("일본어", "japanese"),
        ("영어", "english"),
        ("한국어", "korean"),
        ("중국어", "chinese"),
        ("스페인어", "spanish"),
        ("프랑스어", "french"),
        ("독일어", "german"),
        ("러시아어", "russian"),
        ("포르투갈어", "portuguese"),
        ("이탈리아어", "italian"),
        ("태국어", "thai"),
        ("베트남어", "vietnamese"),
        ("인도네시아어", "indonesian"),
        ("폴란드어", "polish"),
        ("네덜란드어", "dutch"),
        ("헝가리어", "hungarian"),
        ("체코어", "czech")
    ];

    public MainWindow()
    {
        InitializeComponent();
        SortBox.ItemsSource = new[] { "최신 추가순", "발행일순", "오늘 인기", "주간 인기", "월간 인기", "연간 인기", "랜덤" };
        SortBox.SelectedIndex = 0;
        LanguageBox.ItemsSource = LanguageOptions.Select(x => x.Label).ToArray();
        LanguageBox.SelectedIndex = 0;
        StartupFavoritesCheck.IsChecked = _stateStore.State.OpenFavoritesOnStartup;
        _initializing = false;

        Loaded += async (_, _) =>
        {
            if (_stateStore.State.OpenFavoritesOnStartup)
                await ShowFavoritesAsync();
            else
                await RunSearchAsync();
        };
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _includeSuggestCts?.Cancel();
            _excludeSuggestCts?.Cancel();
            _client.Dispose();
        };
    }

    private SortMode CurrentSort => SortBox.SelectedIndex switch
    {
        1 => SortMode.DatePublished,
        2 => SortMode.PopularToday,
        3 => SortMode.PopularWeek,
        4 => SortMode.PopularMonth,
        5 => SortMode.PopularYear,
        6 => SortMode.Random,
        _ => SortMode.DateAdded
    };

    private string CurrentLanguage => LanguageBox.SelectedIndex >= 0 && LanguageBox.SelectedIndex < LanguageOptions.Length
        ? LanguageOptions[LanguageBox.SelectedIndex].Slug
        : "";

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await RunSearchAsync();
    }

    private async void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await RefreshCurrentViewAsync();
    }

    private async void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || !IsLoaded) return;
        await RefreshCurrentViewAsync();
    }

    private async void ApplyFilters_Click(object sender, RoutedEventArgs e) => await RefreshCurrentViewAsync();

    private async void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        _initializing = true;
        DoujinshiCheck.IsChecked = true;
        MangaCheck.IsChecked = true;
        ArtistCgCheck.IsChecked = true;
        GameCgCheck.IsChecked = true;
        ImageSetCheck.IsChecked = true;
        AnimeCheck.IsChecked = true;
        LanguageBox.SelectedIndex = 0;
        IncludeTagsBox.Text = "";
        ExcludeTagsBox.Text = "";
        IncludeTagInput.Text = "";
        ExcludeTagInput.Text = "";
        IncludeSuggestionList.Visibility = Visibility.Collapsed;
        ExcludeSuggestionList.Visibility = Visibility.Collapsed;
        _initializing = false;
        RenderFilterTagChips();
        await RefreshCurrentViewAsync();
    }

    private async Task RefreshCurrentViewAsync()
    {
        if (_showingFavorites) await ShowFavoritesAsync();
        else await RunSearchAsync();
    }

    private void StartupFavoritesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _stateStore.SetOpenFavoritesOnStartup(StartupFavoritesCheck.IsChecked == true);
        StatusText.Text = StartupFavoritesCheck.IsChecked == true
            ? "다음 실행부터 즐겨찾기 화면으로 시작합니다."
            : "다음 실행부터 일반 목록으로 시작합니다.";
    }

    private async void Favorites_Click(object sender, RoutedEventArgs e)
    {
        if (_showingFavorites)
        {
            SearchBox.Text = _searchBeforeFavorites;
            await RunSearchAsync();
        }
        else
        {
            _searchBeforeFavorites = SearchBox.Text;
            await ShowFavoritesAsync();
        }
    }

    private void ResetPagingState()
    {
        _page = 0;
        _pageStartIndices.Clear();
        _pageStartIndices[0] = 0;
        _cardCache.Clear();
        _failedCardIds.Clear();
        _currentPageHasNext = false;
    }

    private async Task ShowFavoritesAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _showingFavorites = true;
        FavoritesButton.Content = "← 일반 목록";
        FavoritesButton.ToolTip = "즐겨찾기 모드 종료";
        var allFavoriteIds = _stateStore.State.FavoriteGalleryIds.OrderByDescending(x => x).ToList();
        SearchBox.Text = "";

        try
        {
            SetBusy("즐겨찾기 필터 적용 중…");
            _results = await ApplyUiFiltersAsync(allFavoriteIds, ct);
            ResetPagingState();
            await RenderPageAsync(ct);
            if (allFavoriteIds.Count == 0)
                StatusText.Text = "즐겨찾기가 없습니다. 작품 카드의 ☆ 버튼을 눌러 추가하세요.";
            else if (_results.Count == 0)
                StatusText.Text = $"★ 즐겨찾기 {allFavoriteIds.Count:N0}개 중 현재 필터에 맞는 작품이 없습니다.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "필터 오류: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Pupil Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RunSearchAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _showingFavorites = false;
        FavoritesButton.Content = "★ 즐겨찾기";
        FavoritesButton.ToolTip = "즐겨찾기 보기";

        try
        {
            SetBusy("검색 및 필터 적용 중…");
            var query = NormalizeStructuredSearch(SearchBox.Text);
            var baseResults = await _client.SearchAsync(query, CurrentSort, ct);
            _results = await ApplyUiFiltersAsync(baseResults, ct);
            ResetPagingState();
            await RenderPageAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "오류: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Pupil Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<List<int>> ApplyUiFiltersAsync(IEnumerable<int> source, CancellationToken ct)
    {
        var result = source.ToList();
        var selectedTypes = GetSelectedTypes();
        if (selectedTypes.Count == 0) return [];

        if (selectedTypes.Count < 6)
        {
            var allowedTypes = new HashSet<int>();
            foreach (var type in selectedTypes)
            {
                foreach (var id in await _client.SearchAsync($"type:{type}", CurrentSort, ct))
                    allowedTypes.Add(id);
            }
            result = result.Where(allowedTypes.Contains).ToList();
        }

        if (!string.IsNullOrWhiteSpace(CurrentLanguage))
        {
            var allowedLanguage = (await _client.SearchAsync($"language:{CurrentLanguage}", CurrentSort, ct)).ToHashSet();
            result = result.Where(allowedLanguage.Contains).ToList();
        }

        foreach (var include in ParseFilterTerms(IncludeTagsBox.Text))
        {
            var allowed = (await _client.SearchAsync(include, CurrentSort, ct)).ToHashSet();
            result = result.Where(allowed.Contains).ToList();
        }

        foreach (var exclude in ParseFilterTerms(ExcludeTagsBox.Text))
        {
            var blocked = (await _client.SearchAsync(exclude, CurrentSort, ct)).ToHashSet();
            result = result.Where(id => !blocked.Contains(id)).ToList();
        }

        return result;
    }

    private List<string> GetSelectedTypes()
    {
        var selected = new List<string>();
        if (DoujinshiCheck.IsChecked == true) selected.Add("doujinshi");
        if (MangaCheck.IsChecked == true) selected.Add("manga");
        if (ArtistCgCheck.IsChecked == true) selected.Add("artistcg");
        if (GameCgCheck.IsChecked == true) selected.Add("gamecg");
        if (ImageSetCheck.IsChecked == true) selected.Add("imageset");
        if (AnimeCheck.IsChecked == true) selected.Add("anime");
        return selected;
    }

    private static IEnumerable<string> ParseFilterTerms(string text)
    {
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var term = raw.Trim();
            if (string.IsNullOrWhiteSpace(term)) continue;
            if (!term.Contains(':')) term = "tag:" + term;
            term = NormalizeStructuredSearch(term);
            if (term.StartsWith('-')) term = term[1..];
            if (!string.IsNullOrWhiteSpace(term)) yield return term;
        }
    }

    private async Task SearchFromTagAsync(string query)
    {
        SearchBox.Text = NormalizeStructuredSearch(query);
        _showingFavorites = false;
        await RunSearchAsync();
        Activate();
    }

    private static string NormalizeStructuredSearch(string query)
    {
        var text = query.Trim();
        if (string.IsNullOrEmpty(text)) return text;

        var negative = text.StartsWith('-');
        var body = negative ? text[1..] : text;
        var idx = body.IndexOf(':');
        if (idx <= 0) return text;

        var prefix = body[..idx].Trim().ToLowerInvariant();
        var known = prefix is "male" or "female" or "language" or "artist" or "group" or "parody" or "series" or "character" or "tag" or "type";
        if (!known) return text;

        if (prefix == "series") prefix = "parody";
        var value = body[(idx + 1)..].Trim();
        if (string.IsNullOrEmpty(value)) return text;

        var normalizedValue = string.Join("_", value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return $"{(negative ? "-" : "")}{prefix}:{normalizedValue}";
    }

    private async Task<(GalleryCard? Card, BitmapImage? Bitmap, string? Error)> LoadCardForDisplayAsync(int id, CancellationToken ct)
    {
        if (_cardCache.TryGetValue(id, out var cached))
            return (cached.Card, cached.Bitmap, null);
        if (_failedCardIds.Contains(id))
            return (null, null, "이 검색에서 이미 로딩 실패한 작품");

        string? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var card = await _client.GetGalleryCardAsync(id, ct);
                BitmapImage? bmp = null;

                foreach (var url in new[] { card.CoverUrl, card.FallbackCoverUrl })
                {
                    for (var imageAttempt = 1; imageAttempt <= 2; imageAttempt++)
                    {
                        try
                        {
                            var bytes = await _client.DownloadBytesAsync(url, ct);
                            bmp = ImageHelpers.ToBitmap(bytes);
                            break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch
                        {
                            if (imageAttempt == 1) await Task.Delay(120, ct);
                        }
                    }
                    if (bmp is not null) break;
                }

                _cardCache[id] = (card, bmp);
                return (card, bmp, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex.Message;
                if (attempt == 1) await Task.Delay(250, ct);
            }
        }

        _failedCardIds.Add(id);
        return (null, null, lastError);
    }

    private async Task RenderPageAsync(CancellationToken ct)
    {
        GalleryPanel.Children.Clear();
        if (_results.Count == 0)
        {
            PageText.Text = "0 / 0";
            StatusText.Text = _showingFavorites ? "★ 즐겨찾기 · 필터 결과 0개" : "필터 결과 0개";
            _currentPageHasNext = false;
            return;
        }

        if (!_pageStartIndices.TryGetValue(_page, out var cursor))
            cursor = Math.Min(_page * PerPage, _results.Count);

        var display = new List<(GalleryCard Card, BitmapImage? Bitmap)>();
        var skipped = 0;
        StatusText.Text = _showingFavorites
            ? $"★ 즐겨찾기 · {_page + 1}페이지 불러오는 중…"
            : $"{_page + 1}페이지 불러오는 중…";

        while (display.Count < PerPage && cursor < _results.Count)
        {
            ct.ThrowIfCancellationRequested();
            var need = PerPage - display.Count;
            var batchIds = _results.Skip(cursor).Take(need).ToArray();
            cursor += batchIds.Length;

            var loaded = await Task.WhenAll(batchIds.Select(async id =>
            {
                var item = await LoadCardForDisplayAsync(id, ct);
                return (Id: id, item.Card, item.Bitmap, item.Error);
            }));

            foreach (var item in loaded)
            {
                if (item.Card is not null)
                    display.Add((item.Card, item.Bitmap));
                else
                    skipped++;
            }
        }

        foreach (var item in display)
            GalleryPanel.Children.Add(CreateCard(item.Card, item.Bitmap));

        _currentPageHasNext = cursor < _results.Count;
        if (_currentPageHasNext)
            _pageStartIndices[_page + 1] = cursor;
        else
            _pageStartIndices.Remove(_page + 1);

        PageText.Text = $"{_page + 1}페이지 · {display.Count}개";
        var skippedText = skipped > 0 ? $" · 로딩 실패 {skipped}개 건너뜀" : "";
        StatusText.Text = _showingFavorites
            ? $"★ 즐겨찾기 · 필터 결과 {_results.Count:N0}개 · {display.Count}개 표시{skippedText}"
            : $"필터 결과 {_results.Count:N0}개 · {display.Count}개 표시{skippedText}";
    }

    private UIElement CreateCard(GalleryCard card, BitmapImage? bitmap)
    {
        var image = new Image
        {
            Height = 250,
            Width = 205,
            Stretch = Stretch.UniformToFill,
            Margin = new Thickness(0, 0, 0, 7),
            Cursor = Cursors.Hand
        };
        if (bitmap is not null) image.Source = bitmap;
        image.MouseLeftButtonUp += async (_, _) => await OpenGalleryAsync(card.Id);

        var title = new TextBlock
        {
            Text = card.Info.Title,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            MaxHeight = 43
        };
        var meta = new TextBlock
        {
            Text = $"#{card.Id} · {card.Info.Type} · {card.Info.Language}",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 5, 0, 4)
        };

        var tagPanel = new WrapPanel { Margin = new Thickness(0, 1, 0, 6) };
        foreach (var item in EnumerateSearchTags(card.Info).Take(10))
            tagPanel.Children.Add(CreateTagChip(item.Label, item.Query));

        var openButton = new Button { Content = "열기", MinWidth = 130, Margin = new Thickness(0, 0, 6, 0) };
        openButton.Click += async (_, _) => await OpenGalleryAsync(card.Id);

        var favoriteButton = new Button
        {
            Content = _stateStore.IsFavorite(card.Id) ? "★" : "☆",
            FontSize = 18,
            Width = 46,
            ToolTip = _stateStore.IsFavorite(card.Id) ? "즐겨찾기 해제" : "즐겨찾기 추가"
        };
        favoriteButton.Click += async (_, _) =>
        {
            _stateStore.ToggleFavorite(card.Id);
            var isFavorite = _stateStore.IsFavorite(card.Id);
            favoriteButton.Content = isFavorite ? "★" : "☆";
            favoriteButton.ToolTip = isFavorite ? "즐겨찾기 해제" : "즐겨찾기 추가";
            StatusText.Text = isFavorite ? $"#{card.Id} 즐겨찾기에 추가했습니다." : $"#{card.Id} 즐겨찾기에서 제거했습니다.";
            if (_showingFavorites && !isFavorite)
                await ShowFavoritesAsync();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(openButton);
        buttons.Children.Add(favoriteButton);

        var stack = new StackPanel();
        stack.Children.Add(image);
        stack.Children.Add(title);
        stack.Children.Add(meta);
        if (tagPanel.Children.Count > 0) stack.Children.Add(tagPanel);
        stack.Children.Add(buttons);

        return new Border
        {
            Width = 220,
            MinHeight = 455,
            Margin = new Thickness(4),
            Padding = new Thickness(6),
            BorderBrush = new SolidColorBrush(Color.FromArgb(32, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = stack
        };
    }

    private Button CreateTagChip(string label, string query)
    {
        var button = new Button
        {
            Content = label,
            FontSize = 10,
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 0, 4, 4),
            ToolTip = $"{query} 검색"
        };
        button.Click += async (_, _) => await SearchFromTagAsync(query);
        return button;
    }

    private static IEnumerable<(string Label, string Query)> EnumerateSearchTags(GalleryInfo info)
    {
        foreach (var parody in info.Parodys ?? [])
            if (!string.IsNullOrWhiteSpace(parody.ParodyName))
                yield return ($"작품: {parody.ParodyName}", $"parody:{parody.ParodyName}");

        foreach (var artist in info.Artists ?? [])
            if (!string.IsNullOrWhiteSpace(artist.ArtistName))
                yield return ($"작가: {artist.ArtistName}", $"artist:{artist.ArtistName}");

        foreach (var character in info.Characters ?? [])
            if (!string.IsNullOrWhiteSpace(character.CharacterName))
                yield return ($"캐릭터: {character.CharacterName}", $"character:{character.CharacterName}");

        foreach (var group in info.Groups ?? [])
            if (!string.IsNullOrWhiteSpace(group.GroupName))
                yield return ($"그룹: {group.GroupName}", $"group:{group.GroupName}");

        foreach (var tag in info.Tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag.Tag)) continue;
            if (!string.IsNullOrWhiteSpace(tag.Female))
                yield return ($"♀ {tag.Tag}", $"female:{tag.Tag}");
            else if (!string.IsNullOrWhiteSpace(tag.Male))
                yield return ($"♂ {tag.Tag}", $"male:{tag.Tag}");
            else
                yield return (tag.Tag, $"tag:{tag.Tag}");
        }
    }

    private async Task OpenGalleryAsync(int id)
    {
        try
        {
            StatusText.Text = $"#{id} 여는 중…";
            var info = await _client.GetGalleryInfoAsync(id);
            var reader = new ReaderWindow(_client, id, info) { Owner = this };
            reader.SearchRequested += async query => await SearchFromTagAsync(query);
            reader.Show();
            StatusText.Text = $"#{id} 열림";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "갤러리 열기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OpenId_Click(object sender, RoutedEventArgs e)
    {
        var text = Interaction.InputBox("갤러리 ID를 입력하세요.", "ID로 열기", "");
        if (int.TryParse(text, out var id)) await OpenGalleryAsync(id);
    }

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_page <= 0) return;
        _page--;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        try { await RenderPageAsync(_cts.Token); }
        catch (OperationCanceledException) { }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!_currentPageHasNext) return;
        var previousPage = _page;
        _page++;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        try
        {
            await RenderPageAsync(_cts.Token);
            if (GalleryPanel.Children.Count == 0)
            {
                _page = previousPage;
                await RenderPageAsync(_cts.Token);
                StatusText.Text = "마지막 페이지입니다.";
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SetBusy(string message)
    {
        StatusText.Text = message;
        GalleryPanel.Children.Clear();
        PageText.Text = "";
    }
}
