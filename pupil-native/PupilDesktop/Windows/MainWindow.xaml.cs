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
    private const int PerPage = 20;

    public MainWindow()
    {
        InitializeComponent();
        SortBox.ItemsSource = new[] { "최신 추가순", "발행일순", "오늘 인기", "주간 인기", "월간 인기", "연간 인기", "랜덤" };
        SortBox.SelectedIndex = 0;
        StartupFavoritesCheck.IsChecked = _stateStore.State.OpenFavoritesOnStartup;
        _initializing = false;

        Loaded += async (_, _) =>
        {
            if (_stateStore.State.OpenFavoritesOnStartup)
                await ShowFavoritesAsync();
            else
                await RunSearchAsync();
        };
        Closed += (_, _) => { _cts?.Cancel(); _client.Dispose(); };
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

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await RunSearchAsync(); }

    private async void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
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

    private async void Favorites_Click(object sender, RoutedEventArgs e) => await ShowFavoritesAsync();

    private async Task ShowFavoritesAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _showingFavorites = true;
        _page = 0;
        _results = _stateStore.State.FavoriteGalleryIds.OrderByDescending(x => x).ToList();
        SearchBox.Text = "";
        try
        {
            SetBusy("즐겨찾기 불러오는 중…");
            await RenderPageAsync(ct);
            if (_results.Count == 0)
                StatusText.Text = "즐겨찾기가 없습니다. 작품 카드의 ☆ 버튼을 눌러 추가하세요.";
            else
                StatusText.Text = $"★ 즐겨찾기 {_results.Count:N0}개";
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunSearchAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _showingFavorites = false;
        try
        {
            SetBusy("검색 중…");
            _page = 0;
            _results = await _client.SearchAsync(SearchBox.Text, CurrentSort, ct);
            await RenderPageAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "오류: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Pupil Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SearchFromTagAsync(string query)
    {
        SearchBox.Text = query;
        _showingFavorites = false;
        await RunSearchAsync();
        Activate();
    }

    private async Task RenderPageAsync(CancellationToken ct)
    {
        GalleryPanel.Children.Clear();
        var start = _page * PerPage;
        var ids = _results.Skip(start).Take(PerPage).ToArray();
        PageText.Text = _results.Count == 0 ? "0 / 0" : $"{_page + 1} / {Math.Max(1, (int)Math.Ceiling(_results.Count / (double)PerPage))}";
        StatusText.Text = _showingFavorites
            ? $"★ 즐겨찾기 {_results.Count:N0}개 · {ids.Length}개 불러오는 중…"
            : $"{_results.Count:N0}개 결과 · {ids.Length}개 불러오는 중…";

        using var gate = new SemaphoreSlim(5);
        var tasks = ids.Select(async (id, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var card = await _client.GetGalleryCardAsync(id, ct);
                BitmapImage? bmp = null;
                foreach (var url in new[] { card.CoverUrl, card.FallbackCoverUrl })
                {
                    try
                    {
                        var imageBytes = await _client.DownloadBytesAsync(url, ct);
                        bmp = ImageHelpers.ToBitmap(imageBytes);
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                return (index, card, bmp, error: (string?)null);
            }
            catch (Exception ex) { return (index, card: (GalleryCard?)null, bmp: (BitmapImage?)null, error: ex.Message); }
            finally { gate.Release(); }
        }).ToArray();

        foreach (var task in tasks)
        {
            var item = await task;
            ct.ThrowIfCancellationRequested();
            if (item.card is not null) GalleryPanel.Children.Add(CreateCard(item.card, item.bmp));
        }

        StatusText.Text = _showingFavorites
            ? $"★ 즐겨찾기 {_results.Count:N0}개"
            : $"{_results.Count:N0}개 결과";
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
        {
            if (!string.IsNullOrWhiteSpace(parody.ParodyName))
                yield return ($"작품: {parody.ParodyName}", $"series:{parody.ParodyName}");
        }

        foreach (var artist in info.Artists ?? [])
        {
            if (!string.IsNullOrWhiteSpace(artist.ArtistName))
                yield return ($"작가: {artist.ArtistName}", $"artist:{artist.ArtistName}");
        }

        foreach (var character in info.Characters ?? [])
        {
            if (!string.IsNullOrWhiteSpace(character.CharacterName))
                yield return ($"캐릭터: {character.CharacterName}", $"character:{character.CharacterName}");
        }

        foreach (var group in info.Groups ?? [])
        {
            if (!string.IsNullOrWhiteSpace(group.GroupName))
                yield return ($"그룹: {group.GroupName}", $"group:{group.GroupName}");
        }

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
            reader.SearchRequested += async query =>
            {
                await SearchFromTagAsync(query);
            };
            reader.Show();
            StatusText.Text = $"#{id} 열림";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "갤러리 열기 실패", MessageBoxButton.OK, MessageBoxImage.Error); }
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
        _cts?.Cancel(); _cts = new CancellationTokenSource();
        try { await RenderPageAsync(_cts.Token); } catch (OperationCanceledException) { }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if ((_page + 1) * PerPage >= _results.Count) return;
        _page++;
        _cts?.Cancel(); _cts = new CancellationTokenSource();
        try { await RenderPageAsync(_cts.Token); } catch (OperationCanceledException) { }
    }

    private void SetBusy(string message)
    {
        StatusText.Text = message;
        GalleryPanel.Children.Clear();
        PageText.Text = "";
    }
}
