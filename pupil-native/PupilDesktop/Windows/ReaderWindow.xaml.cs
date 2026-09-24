using Microsoft.Win32;
using PupilDesktop.Core;
using PupilDesktop.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PupilDesktop.Windows;

public partial class ReaderWindow : Window
{
    private readonly HitomiClient _client;
    private readonly int _galleryId;
    private readonly GalleryInfo _info;
    private List<string> _urls = [];
    private int _page;
    private byte[]? _currentBytes;
    private CancellationTokenSource? _pageCts;

    public event Func<string, Task>? SearchRequested;

    public ReaderWindow(HitomiClient client, int galleryId, GalleryInfo info)
    {
        InitializeComponent();
        _client = client; _galleryId = galleryId; _info = info;
        Title = $"Pupil Desktop - {info.Title}";
        TitleText.Text = info.Title;
        MetaText.Text = $"#{galleryId} · {info.Type} · {info.Language} · {info.Files.Count} pages";
        BuildTagPanel();

        Loaded += async (_, _) =>
        {
            _urls = await _client.GetReaderWebpImageUrlsAsync(_info);
            await LoadPageAsync(0);
        };
        Closed += (_, _) => _pageCts?.Cancel();
    }

    private void BuildTagPanel()
    {
        var allItems = EnumerateSearchTags(_info).ToList();
        var items = allItems.Take(28).ToList();
        foreach (var item in items)
        {
            var button = new Button
            {
                Content = item.Label,
                FontSize = 10,
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 4, 4),
                ToolTip = $"{item.Query} 검색"
            };
            button.Click += async (_, _) =>
            {
                if (SearchRequested is not null)
                {
                    await SearchRequested(item.Query);
                    Close();
                }
            };
            TagPanel.Children.Add(button);
        }

        if (allItems.Count > items.Count)
        {
            TagPanel.Children.Add(new TextBlock
            {
                Text = $"+{allItems.Count - items.Count}",
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 4)
            });
        }
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

    private async Task LoadPageAsync(int page)
    {
        if (_urls.Count == 0 || page < 0 || page >= _urls.Count) return;
        _pageCts?.Cancel(); _pageCts = new CancellationTokenSource();
        var ct = _pageCts.Token;
        try
        {
            LoadingText.Visibility = Visibility.Visible; PageImage.Source = null; _currentBytes = null;
            _page = page; PageText.Text = $"{_page + 1} / {_urls.Count}";
            var bytes = await _client.DownloadBytesAsync(_urls[_page], ct);
            ct.ThrowIfCancellationRequested();
            _currentBytes = bytes;
            PageImage.Source = ImageHelpers.ToBitmap(bytes);
            LoadingText.Visibility = Visibility.Collapsed;
            ImageScroll.ScrollToHome();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LoadingText.Text = "불러오기 실패\n" + ex.Message; }
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(_page - 1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(_page + 1);

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.PageUp) await LoadPageAsync(_page - 1);
        else if (e.Key is Key.Right or Key.PageDown or Key.Space) await LoadPageAsync(_page + 1);
        else if (e.Key == Key.Home) await LoadPageAsync(0);
        else if (e.Key == Key.End) await LoadPageAsync(_urls.Count - 1);
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        var newWidth = PageImage.Width;
        if (double.IsNaN(newWidth) || newWidth <= 0) newWidth = PageImage.ActualWidth;
        newWidth = Math.Clamp(newWidth * (e.Delta > 0 ? 1.12 : 0.89), 300, 5000);
        PageImage.Width = newWidth;
        PageImage.Stretch = System.Windows.Media.Stretch.Uniform;
    }

    private void SavePage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBytes is null || _page >= _info.Files.Count) return;
        var dialog = new SaveFileDialog { FileName = $"{_galleryId}_{_page + 1:000}.webp", Filter = "WebP image|*.webp|All files|*.*" };
        if (dialog.ShowDialog(this) == true) File.WriteAllBytes(dialog.FileName, _currentBytes);
    }

    private async void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "저장할 폴더를 선택하세요", Multiselect = false };
        if (dlg.ShowDialog(this) != true) return;
        var folder = Path.Combine(dlg.FolderName, $"{_galleryId} - {ImageHelpers.SafeFileName(_info.Title)}");
        Directory.CreateDirectory(folder);
        try
        {
            LoadingText.Visibility = Visibility.Visible;
            for (var i = 0; i < _urls.Count; i++)
            {
                LoadingText.Text = $"저장 중… {i + 1}/{_urls.Count}";
                var bytes = await _client.DownloadBytesAsync(_urls[i]);
                await File.WriteAllBytesAsync(Path.Combine(folder, $"{i + 1:000}.webp"), bytes);
            }
            LoadingText.Text = "저장 완료";
            await Task.Delay(800);
            LoadingText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "저장 실패", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
