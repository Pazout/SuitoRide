using System.IO;
using System.Net.Http;

namespace SuitoRide;

internal sealed record StreetPhoto(long Id, string Author, string Provider, string Source,
    double Latitude, double Longitude, double? Heading, Uri Image)
{
    public const string LicenseUrl = "https://creativecommons.org/licenses/by-sa/4.0/";
    public string Key => FormattableString.Invariant($"{Provider}:{Id}");
    public string Attribution => $"© {Author} / {Provider}";
    public string Credit => Attribution + " · CC BY-SA 4.0";

    public static IEnumerable<StreetPhoto> Nearby(IEnumerable<StreetPhoto> photos, double lat, double lon, double direction) =>
        photos.Select(photo =>
            {
                double y = (photo.Latitude - lat) * 111195;
                double x = Math.IEEERemainder(photo.Longitude - lon, 360) * 111195 * Math.Cos(lat * Math.PI / 180);
                return (Photo: photo, Meters: Math.Sqrt(x * x + y * y));
            })
            .Where(candidate => candidate.Meters <= 90)
            .OrderBy(candidate => candidate.Meters + (candidate.Photo.Heading is double heading
                ? Math.Abs(Math.IEEERemainder(heading - direction, 360)) * .25 : 20))
            .Select(candidate => candidate.Photo);
}

internal static class StreetPhotoClient
{
    public static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12),
        MaxResponseContentBufferSize = 12 * 1024 * 1024
    };

    public static async Task<byte[]> DownloadAsync(StreetPhoto photo, CancellationToken cancellation)
    {
        var bytes = await Http.GetByteArrayAsync(photo.Image, cancellation);
        if (bytes is not [0xff, 0xd8, 0xff, ..])
            throw new InvalidDataException("Street photo is not a JPEG.");
        return bytes;
    }
}
