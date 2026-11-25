using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SuitoRide;

internal static class MapillaryService
{
    public static async Task<StreetPhoto[]> SearchAsync(double lat, double lon, string token, CancellationToken cancellation)
    {
        const double latitudeDelta = 200.0 / 111195;
        double longitudeDelta = latitudeDelta / Math.Max(.05, Math.Cos(lat * Math.PI / 180));
        var query = FormattableString.Invariant(
            $"{Math.Max(-180, lon - longitudeDelta):F7},{Math.Max(-90, lat - latitudeDelta):F7},{Math.Min(180, lon + longitudeDelta):F7},{Math.Min(90, lat + latitudeDelta):F7}");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://graph.mapillary.com/images?bbox=" + query + "&limit=300&fields=id,geometry,compass_angle,is_pano,creator,thumb_1024_url");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        using var response = await StreetPhotoClient.Http.SendAsync(request, cancellation);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            using var error = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellation));
            if (error.RootElement.TryGetProperty("error", out var details) && details.ValueKind == JsonValueKind.Object &&
                details.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt32(out int errorCode) && errorCode == 190)
                throw new HttpRequestException("Mapillary rejected the token.", null, HttpStatusCode.Unauthorized);
        }
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellation));
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];
        return data.EnumerateArray().Select(Parse).OfType<StreetPhoto>().ToArray();
    }

    static StreetPhoto? Parse(JsonElement photo)
    {
        if (photo.ValueKind != JsonValueKind.Object ||
            !photo.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
            !long.TryParse(idValue.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0 ||
            !photo.TryGetProperty("is_pano", out var pano) || pano.ValueKind != JsonValueKind.False ||
            !photo.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object ||
            !geometry.TryGetProperty("coordinates", out var coordinates) ||
            coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() != 2 ||
            !ReadCoordinate(coordinates[0], -180, 180, out double lon) ||
            !ReadCoordinate(coordinates[1], -90, 90, out double lat) ||
            !photo.TryGetProperty("creator", out var creator) || creator.ValueKind != JsonValueKind.Object ||
            !creator.TryGetProperty("username", out var username) || username.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(username.GetString()) ||
            !photo.TryGetProperty("thumb_1024_url", out var thumbnail) || thumbnail.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(thumbnail.GetString(), UriKind.Absolute, out var image) ||
            image.Scheme != Uri.UriSchemeHttps || !image.IsDefaultPort || image.UserInfo.Length != 0 ||
            !(image.Host.EndsWith(".fbcdn.net", StringComparison.OrdinalIgnoreCase) ||
              image.Host.EndsWith(".mapillary.com", StringComparison.OrdinalIgnoreCase)))
            return null;

        double? heading = photo.TryGetProperty("compass_angle", out var angle) &&
            ReadCoordinate(angle, 0, 360, out double number) ? number : null;
        return new(id, username.GetString()!.Trim(), "Mapillary",
            FormattableString.Invariant($"https://www.mapillary.com/app/?pKey={id}"), lat, lon, heading, image);
    }

    static bool ReadCoordinate(JsonElement value, double min, double max, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) &&
            number >= min && number <= max;
    }
}
