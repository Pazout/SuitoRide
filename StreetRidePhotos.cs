using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SuitoRide;

internal sealed record StreetPhotoFrame(StreetPhoto Photo, byte[] Jpeg)
{
    public void Save(string path)
    {
        using var input = new MemoryStream(Jpeg, writable: false);
        var bitmap = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        int width = Math.Min(1600, bitmap.PixelWidth);
        int height = (int)Math.Round((double)bitmap.PixelHeight * width / bitmap.PixelWidth);
        int fontSize = Math.Clamp(width / 44, 14, 32);
        int inset = fontSize;
        int footer = fontSize * 4;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height + footer));
            dc.DrawImage(bitmap, new Rect(0, 0, width, height));
            Text(Photo.Credit, height + fontSize / 2);
            Text(Photo.Source, height + fontSize * 2);
            void Text(string text, int top)
            {
                var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), fontSize, Brushes.White, 1)
                {
                    MaxTextWidth = Math.Max(1, width - inset * 2),
                    MaxTextHeight = fontSize * 1.5,
                    Trimming = TextTrimming.CharacterEllipsis
                };
                dc.DrawText(label, new Point(inset, top));
            }
        }
        var image = new RenderTargetBitmap(width, height + footer, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temp)) encoder.Save(output);
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}

internal sealed class StreetRidePhotos(string token)
{
    Task<StreetPhotoFrame?>? start, finish;

    public bool HasRequests => start != null || finish != null;

    public void Update(double covered, double total, Func<double, (double Lat, double Lon, double Heading)> positionAt)
    {
        double startTarget = Math.Min(80, .12 * total);
        double finishTarget = total - Math.Min(140, .15 * total);
        if (start == null && covered >= startTarget)
            start = CaptureAsync(positionAt(startTarget), null);
        if (finish == null && covered >= finishTarget)
            finish = CaptureAsync(positionAt(finishTarget), start);
    }

    async Task<StreetPhotoFrame?> CaptureAsync(
        (double Lat, double Lon, double Heading) position, Task<StreetPhotoFrame?>? earlier)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            string? startKey = earlier == null ? null : (await earlier.WaitAsync(deadline.Token))?.Photo.Key;
            var result = await MapillaryService.SearchAsync(position.Lat, position.Lon, token, deadline.Token);
            var candidates = StreetPhoto.Nearby(result, position.Lat, position.Lon, position.Heading)
                .Where(photo => photo.Key != startKey).Take(3);
            foreach (var photo in candidates)
            {
                try { return new(photo, await StreetPhotoClient.DownloadAsync(photo, deadline.Token)); }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidDataException) { }
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException
            or OperationCanceledException or FormatException) { }
        return null;
    }

    public async Task SaveAsync(string folder)
    {
        var first = start == null ? null : await start;
        var last = finish == null ? null : await finish;
        if (first == null && last == null) return;

        Directory.CreateDirectory(folder);
        first?.Save(Path.Combine(folder, "01-start.jpg"));
        last?.Save(Path.Combine(folder, "02-finish.jpg"));
        File.WriteAllText(Path.Combine(folder, "CC-BY-SA.txt"), string.Join(Environment.NewLine, new[]
        {
            first == null ? null : $"01-start.jpg | {first.Photo.Credit} | {first.Photo.Source}",
            last == null ? null : $"02-finish.jpg | {last.Photo.Credit} | {last.Photo.Source}",
            StreetPhoto.LicenseUrl,
            "Changes: resized and attribution added."
        }.OfType<string>()));
    }
}
