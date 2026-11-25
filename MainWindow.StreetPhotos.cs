using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace SuitoRide;

public partial class MainWindow
{
    const string MapillaryProvider = "mapillary", MapillaryName = "Mapillary";
    StreetPhoto[] streetPhotoCandidates = [];
    (double Lat, double Lon)? streetPhotoSearchCenter;
    DateTimeOffset nextStreetPhotoSearch;
    CancellationTokenSource? streetPhotoRequest;
    bool refreshStreetPhotoAfterRequest;
    StreetPhotoFrame? streetPhotoFrame;
    string? streetPhotoFailure;
    StreetRidePhotos? streetRidePhotos, pendingStreetPhotos;

    Task ridePhotoSaves = Task.CompletedTask;
    bool closingAfterPhotos;

    static bool IsStreetPhotoProvider(string provider) => provider == MapillaryProvider;

    bool StreetPhotoVisible => IsStreetPhotoProvider(streetProvider) && !navigationMode && activeGoogle3DKey == null;
    static string StreetPhotoUnavailable => UiText.Format($"{MapillaryName} v tomto místě nemá dostupnou fotku.");

    void ResetStreetPhoto()
    {
        streetPhotoRequest?.Cancel();
        streetPhotoRequest = null;
        refreshStreetPhotoAfterRequest = false;
        streetPhotoCandidates = [];
        streetPhotoSearchCenter = null;
        streetPhotoFrame = null;
        streetPhotoFailure = null;
    }

    async void UpdateStreetPhoto(double meters)
    {
        if (streetPhotoRequest != null) return;
        var point = PositionAt(meters);
        var now = DateTimeOffset.UtcNow;
        var candidate = StreetPhoto.Nearby(streetPhotoCandidates, point.Lat, point.Lon, HeadingAt(meters)).FirstOrDefault();
        bool search = streetPhotoSearchCenter is not { } previous ||
            (now >= nextStreetPhotoSearch && Dist(point.Lat, point.Lon, previous.Lat, previous.Lon) >= 95);
        if (!search && candidate?.Key == streetPhotoFrame?.Photo.Key)
        {
            if (streetPhotoFrame == null)
                ShowStreetPhotoUnavailable(streetPhotoFailure ?? StreetPhotoUnavailable);
            return;
        }

        if (streetPhotoFrame == null)
            SendMap(new { type = "street-photo-loading", message = UiText.Format($"Načítám fotky z {MapillaryName}…") });
        using var request = new CancellationTokenSource();
        streetPhotoRequest = request;
        try
        {
            if (search)
            {
                streetPhotoSearchCenter = point;
                nextStreetPhotoSearch = now.AddSeconds(8);
                var found = await MapillaryService.SearchAsync(point.Lat, point.Lon, streetKey!, request.Token);
                if (request.IsCancellationRequested || !StreetPhotoVisible) return;
                streetPhotoFailure = null;
                streetPhotoCandidates = found;
                meters = DisplayDistance;
                point = PositionAt(meters);
                candidate = StreetPhoto.Nearby(streetPhotoCandidates, point.Lat, point.Lon, HeadingAt(meters)).FirstOrDefault();
            }

            if (candidate == null)
            {
                ShowStreetPhotoUnavailable(StreetPhotoUnavailable);
                return;
            }
            if (candidate.Key == streetPhotoFrame?.Photo.Key) return;
            var jpeg = await StreetPhotoClient.DownloadAsync(candidate, request.Token);
            if (request.IsCancellationRequested || !StreetPhotoVisible) return;
            point = PositionAt(DisplayDistance);
            if (Dist(point.Lat, point.Lon, candidate.Latitude, candidate.Longitude) > 100)
            {
                lastStreetDistance = double.NaN;
                return;
            }

            streetPhotoFrame = new StreetPhotoFrame(candidate, jpeg);
            SendMap(new
            {
                type = "street-photo",
                id = candidate.Key,
                url = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg),
                credit = candidate.Attribution,
                provider = candidate.Provider,
                source = candidate.Source,
                license = StreetPhoto.LicenseUrl
            });
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException or FormatException)
        {
            if (!request.IsCancellationRequested && StreetPhotoVisible)
            {
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
                    nextStreetPhotoSearch = DateTimeOffset.UtcNow.AddMinutes(2);
                streetPhotoCandidates = streetPhotoCandidates.Where(photo => photo.Key != candidate?.Key).ToArray();
                if (search &&
                    (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } or FormatException))
                    streetPhotoFailure = UiText.Get("Mapillary odmítlo token. Zkontroluj ho v nastavení.");
                else
                    streetPhotoFailure = UiText.Format($"Fotky z {MapillaryName} se nepodařilo načíst.");
                if (streetPhotoFrame == null) ShowStreetPhotoUnavailable(streetPhotoFailure);
            }
        }
        finally
        {
            if (ReferenceEquals(streetPhotoRequest, request))
            {
                streetPhotoRequest = null;
                if (refreshStreetPhotoAfterRequest)
                {
                    refreshStreetPhotoAfterRequest = false;
                    UpdateStreetView(true);
                }
            }
        }
    }

    void ShowStreetPhotoUnavailable(string message)
    {
        streetPhotoFrame = null;
        SendMap(new { type = "street-photo-unavailable", message });
    }

    void StreetPhotoStatus(string message)
    {
        if (!StreetPhotoVisible || streetPhotoFrame is not { } frame ||
            message != "street-photo-error:" + frame.Photo.Key) return;
        streetPhotoCandidates = streetPhotoCandidates.Where(photo => photo.Key != frame.Photo.Key).ToArray();
        ShowStreetPhotoUnavailable(StreetPhotoUnavailable);
    }

    static StreetRidePhotos? LoadRidePhotoCapture()
    {
        if (!MapillaryRidePhotoPreference.Load()) return null;

        try
        {
            var token = PanoramaKeyStore.Load(MapillaryProvider);
            return string.IsNullOrWhiteSpace(token) ? null : new StreetRidePhotos(token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    void UpdateRidePhotoCapture() =>
        streetRidePhotos?.Update(distance - rideStartDistance, route[^1].D - rideStartDistance, offset =>
        {
            var meters = rideStartDistance + offset;
            var point = PositionAt(meters);
            return (point.Lat, point.Lon, HeadingAt(meters));
        });

    async Task SaveRidePhotosAsync(StreetRidePhotos photos, Guid rideId)
    {
        try { await photos.SaveAsync(historyStore.PhotoFolder(rideId)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException)
        {
            Status.Text += " " + UiText.Get("Fotky z trasy se nepodařilo uložit.");
        }
    }

    async void CloseAfterRidePhotos()
    {
        closingAfterPhotos = true;
        Status.Text = UiText.Get("Ukládám fotky z trasy…");
        await ridePhotoSaves;
        closingAfterPhotos = false;
        Close();
    }
}
