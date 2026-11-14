using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Baymax.Contracts;
using QRCoder;

namespace Baymax.Desktop;

public partial class PairingWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly PairingInvitationResponse _invitation;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _refreshInProgress;

    public PairingWindow(HttpClient httpClient, PairingInvitationResponse invitation)
    {
        InitializeComponent();
        _httpClient = httpClient;
        _invitation = invitation;
        HostNameText.Text = invitation.Payload.HostName;
        EndpointText.Text = invitation.Payload.Endpoint;
        FingerprintText.Text = $"TLS SHA-256 pin: {invitation.Payload.CertificateSha256}";
        QrImage.Source = CreateQrCode(JsonSerializer.Serialize(invitation.Payload, JsonOptions));
        _refreshTimer.Tick += RefreshTimer_Tick;
        UpdateExpiryText();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Start();
        _ = RefreshPairingListsAsync();
    }

    private void Window_Closed(object? sender, EventArgs e) => _refreshTimer.Stop();

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        UpdateExpiryText();
        await RefreshPairingListsAsync();
    }

    private async Task RefreshPairingListsAsync()
    {
        if (_refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            var requests = await _httpClient.GetFromJsonAsync<List<PendingPairingRequestResponse>>(
                "pairing/requests",
                JsonOptions) ?? [];
            RenderRequests(requests);

            var devices = await _httpClient.GetFromJsonAsync<List<PairedDeviceResponse>>(
                "pairing/devices",
                JsonOptions) ?? [];
            RenderDevices(devices);
            PairingStatusText.Text = $"Polling for pairing requests. Code expires at {_invitation.ExpiresAt.LocalDateTime:t}.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            PairingStatusText.Text = $"Could not refresh pairing status: {exception.Message}";
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private void RenderRequests(IReadOnlyList<PendingPairingRequestResponse> requests)
    {
        PendingRequestsPanel.Children.Clear();
        PendingEmptyText.Visibility = requests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var request in requests)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(221, 229, 239)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 9)
            };
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = request.DeviceName,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59))
            });
            content.Children.Add(new TextBlock
            {
                Text = $"Key SHA-256: {request.PublicKeySha256}\nRequested: {request.CreatedAt.LocalDateTime:g}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(0, 4, 0, 8)
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var confirm = new Button
            {
                Content = "Confirm pairing",
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Foreground = Brushes.White
            };
            confirm.Click += async (_, _) => await ConfirmRequestAsync(request, confirm);
            var reject = new Button { Content = "Reject" };
            reject.Click += async (_, _) => await RejectRequestAsync(request, confirm, reject);
            buttons.Children.Add(confirm);
            buttons.Children.Add(reject);
            content.Children.Add(buttons);
            card.Child = content;
            PendingRequestsPanel.Children.Add(card);
        }
    }

    private void RenderDevices(IReadOnlyList<PairedDeviceResponse> devices)
    {
        DevicesPanel.Children.Clear();
        var activeDevices = devices.Where(device => device.RevokedAt is null).ToArray();
        DevicesEmptyText.Visibility = activeDevices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var device in activeDevices)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var revoke = new Button { Content = "Revoke" };
            revoke.Click += async (_, _) => await RevokeDeviceAsync(device, revoke);
            DockPanel.SetDock(revoke, Dock.Right);
            row.Children.Add(revoke);
            row.Children.Add(new TextBlock
            {
                Text = $"{device.DeviceName}\nKey SHA-256: {device.PublicKeySha256}",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Margin = new Thickness(0, 0, 10, 0)
            });
            DevicesPanel.Children.Add(row);
        }
    }

    private async Task ConfirmRequestAsync(PendingPairingRequestResponse request, Button button)
    {
        button.IsEnabled = false;
        try
        {
            using var response = await _httpClient.PostAsync($"pairing/requests/{request.RequestId:D}/confirm", null);
            if (!response.IsSuccessStatusCode)
            {
                throw await ReadApiErrorAsync(response);
            }

            PairingStatusText.Text = $"{request.DeviceName} is paired.";
            await RefreshPairingListsAsync();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            PairingStatusText.Text = $"Could not confirm pairing: {exception.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task RejectRequestAsync(
        PendingPairingRequestResponse request,
        Button confirmButton,
        Button rejectButton)
    {
        confirmButton.IsEnabled = false;
        rejectButton.IsEnabled = false;
        try
        {
            using var response = await _httpClient.DeleteAsync($"pairing/requests/{request.RequestId:D}");
            if (!response.IsSuccessStatusCode)
            {
                throw await ReadApiErrorAsync(response);
            }

            PairingStatusText.Text = $"Rejected pairing request from {request.DeviceName}.";
            await RefreshPairingListsAsync();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            PairingStatusText.Text = $"Could not reject pairing: {exception.Message}";
        }
        finally
        {
            confirmButton.IsEnabled = true;
            rejectButton.IsEnabled = true;
        }
    }

    private async Task RevokeDeviceAsync(PairedDeviceResponse device, Button button)
    {
        var answer = MessageBox.Show(
            this,
            $"Revoke {device.DeviceName}? It will no longer be trusted by Baymax.",
            "Revoke paired device",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            using var response = await _httpClient.DeleteAsync($"pairing/devices/{device.DeviceId}");
            if (!response.IsSuccessStatusCode)
            {
                throw await ReadApiErrorAsync(response);
            }

            PairingStatusText.Text = $"Revoked {device.DeviceName}.";
            await RefreshPairingListsAsync();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            PairingStatusText.Text = $"Could not revoke device: {exception.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task<HttpRequestException> ReadApiErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var document = JsonDocument.Parse(body);
            var detail = document.RootElement.TryGetProperty("detail", out var value)
                ? value.GetString()
                : null;
            return new HttpRequestException(detail ?? $"Baymax returned HTTP {(int)response.StatusCode}.");
        }
        catch (JsonException)
        {
            return new HttpRequestException($"Baymax returned HTTP {(int)response.StatusCode}.");
        }
    }

    private void UpdateExpiryText()
    {
        var remaining = _invitation.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            ExpiryText.Text = "Pairing code expired. Close this window and create a new code.";
            QrImage.Opacity = 0.3;
            CancelInvitationButton.IsEnabled = false;
        }
        else
        {
            ExpiryText.Text = $"Code expires in {Math.Ceiling(remaining.TotalSeconds)} seconds.";
        }
    }

    private async void CancelInvitationButton_Click(object sender, RoutedEventArgs e)
    {
        CancelInvitationButton.IsEnabled = false;
        try
        {
            using var response = await _httpClient.DeleteAsync(
                $"pairing/invitations/{_invitation.Payload.InvitationId:D}");
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                throw await ReadApiErrorAsync(response);
            }

            PairingStatusText.Text = "Pairing code cancelled.";
            Close();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            PairingStatusText.Text = $"Could not cancel invitation: {exception.Message}";
            CancelInvitationButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private static BitmapImage CreateQrCode(string value)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        var pngBytes = new PngByteQRCode(qrData).GetGraphic(8);
        using var stream = new MemoryStream(pngBytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
