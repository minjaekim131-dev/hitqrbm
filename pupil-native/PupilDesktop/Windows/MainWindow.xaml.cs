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
    private CancellationTokenSource? _cts;
    private List<int> _results = [];
    private int _page;
    private const int PerPage = 20;

    public MainWindow()
    {
        InitializeComponent();
        SortBox.ItemsSource = new[] { "최신 추가순", "발행일순", "오늘 인기", "주간 인기", "월간 인기", "연간 인기", "랜덤" };
        SortBox.SelectedIndex = 0;
        Loaded += async (_, _) => await RunSearchAsync();
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
    private async void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await RunSearchAsync(); }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await RunSearchAsync(); }

    private async Task RunSearchAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
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

    private async Task RenderPageAsync(CancellationToken ct)
    {
        GalleryPanel.Children.Clear();
        var start = _page * PerPage;
        var ids = _results.Skip(start).Take(PerPage).ToArray();
        PageText.Text = _results.Count == 0 ? "0 / 0" : $"{_page + 1} / {Math.Max(1, (int)Math.Ceiling(_results.Count / (double)PerPage))}";
        StatusText.Text = $"{_results.Count:N0}개 결과 · {ids.Length}개 불러오는 중…";

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
        StatusText.Text = $"{_results.Count:N0}개 결과";
    }

    private UIElement CreateCard(GalleryCard card, BitmapImage? bitmap)
    {
        var image = new Image { Height = 280, Width = 205, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 0, 7) };
        if (bitmap is not null) image.Source = bitmap;
        var title = new TextBlock { Text = card.Info.Title, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, MaxHeight = 43 };
        var meta = new TextBlock { Text = $"#{card.Id} · {card.Info.Type} · {card.Info.Language}", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 5, 0, 0) };
        var stack = new StackPanel(); stack.Children.Add(image); stack.Children.Add(title); stack.Children.Add(meta);
        var button = new Button { Width = 220, MinHeight = 355, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, Content = stack, Tag = card.Id };
        button.Click += async (_, _) => await OpenGalleryAsync(card.Id);
        return button;
    }

    private async Task OpenGalleryAsync(int id)
    {
        try
        {
            StatusText.Text = $"#{id} 여는 중…";
            var info = await _client.GetGalleryInfoAsync(id);
            var reader = new ReaderWindow(_client, id, info) { Owner = this };
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

    private void SetBusy(string message) { StatusText.Text = message; GalleryPanel.Children.Clear(); PageText.Text = ""; }
}
