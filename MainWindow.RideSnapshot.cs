using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SuitoRide;

public partial class MainWindow
{
    Task<byte[]?>? rideSnapshot, pendingRideSnapshot;

    void UpdateRideSnapshot()
    {
        if (session == null || rideSnapshot != null ||
            distance < rideStartDistance + (route[^1].D - rideStartDistance) / 2) return;

        rideSnapshot = CaptureRideSnapshotAsync();
    }

    bool CanCaptureMap => mapReady && activeGoogle3DKey == null &&
        (navigationMode || streetKey == null || IsStreetPhotoProvider(streetProvider));

    async Task<byte[]?> CaptureRideSnapshotAsync()
    {
        if (Content is not FrameworkElement content || content.ActualWidth <= 0 || content.ActualHeight <= 0)
            return null;

        try
        {
            var size = content.RenderSize;
            var dpi = VisualTreeHelper.GetDpi(content);
            int width = (int)Math.Ceiling(size.Width * dpi.DpiScaleX);
            int height = (int)Math.Ceiling(size.Height * dpi.DpiScaleY);
            var image = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            image.Render(content);
            var origin = MapView.TranslatePoint(new Point(), content);
            var bounds = new Rect(origin, MapView.RenderSize);
            BitmapSource? map = null;
            if (CanCaptureMap && MapView.CoreWebView2 is { } webView)
            {
                using var mapStream = new MemoryStream();
                try
                {
                    const string visibleRestrictedView = """
                        (() => {
                            const visible = id => getComputedStyle(document.getElementById(id)).display !== 'none';
                            return visible('google3dwrap') ||
                                visible('panowrap') && !!document.querySelector('#panowrap .panoimg, #panowrap .googlepano');
                        })()
                        """;
                    var restricted = await webView.ExecuteScriptAsync(visibleRestrictedView).WaitAsync(TimeSpan.FromSeconds(3));
                    if (restricted == "false" && CanCaptureMap)
                        await webView.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, mapStream)
                            .WaitAsync(TimeSpan.FromSeconds(5));
                    if (CanCaptureMap && riding && mapStream.Length > 0)
                    {
                        mapStream.Position = 0;
                        map = BitmapFrame.Create(mapStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or FormatException or TimeoutException)
                { }
            }

            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Background, null, new Rect(size));
                drawing.DrawImage(image, new Rect(size));
                if (map != null) drawing.DrawImage(map, bounds);
                else drawing.DrawRectangle(Background, null, bounds);
            }
            var result = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            result.Render(visual);

            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(result));
            using var output = new MemoryStream();
            png.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    async Task SaveRideSnapshotAsync(Task<byte[]?> snapshot, Guid rideId)
    {
        try
        {
            if (await snapshot is not { } bytes) return;
            string folder = historyStore.PhotoFolder(rideId);
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, "midpoint.png"), bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text += " " + UiText.Get("Snímek z jízdy se nepodařilo uložit.");
        }
    }
}
