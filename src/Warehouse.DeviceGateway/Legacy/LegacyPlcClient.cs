using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Warehouse.Wms.DeviceGateway.Legacy;

public sealed class LegacyPlcClient
{
    private readonly HttpClient _httpClient;

    public LegacyPlcClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public Task<HttpResponseMessage> SubmitInboundAsync(
        LegacyInboundRequest request,
        CancellationToken cancellationToken = default) =>
        _httpClient.PostAsJsonAsync("api/plc-operations/inbound", request, cancellationToken);

    public Task<HttpResponseMessage> SubmitOutboundAsync(
        LegacyOutboundRequest request,
        CancellationToken cancellationToken = default) =>
        _httpClient.PostAsJsonAsync("api/plc-operations/outbound", request, cancellationToken);

    public Task<HttpResponseMessage> SubmitTransferAsync(
        LegacyTransferRequest request,
        CancellationToken cancellationToken = default) =>
        _httpClient.PostAsJsonAsync("api/plc-operations/transfer", request, cancellationToken);

    public Task<HttpResponseMessage> GetStatusAsync(
        string deviceId,
        CancellationToken cancellationToken = default) =>
        _httpClient.PostAsJsonAsync("api/plc-operations/readplcstatus", deviceId, cancellationToken);

    public Task<HttpResponseMessage> TestConnectionAsync(
        string deviceId,
        CancellationToken cancellationToken = default) =>
        _httpClient.PostAsync(
            $"api/PlcConfigurations/{Uri.EscapeDataString(deviceId)}/test-connection",
            content: null,
            cancellationToken);

    public Task<HttpResponseMessage> ProbeCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        return _httpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Options, "api/plc-operations"),
            cancellationToken);
    }
}

public sealed record LegacyInboundRequest(string PlcId, int Shelf, int Position, int LoadingPoint);

public sealed record LegacyOutboundRequest(string PlcId, int Shelf, int Position, int LoadingPoint);

public sealed record LegacyTransferRequest(
    string PlcId,
    int OutShelf,
    int OutPosition,
    int InShelf,
    int InPosition);

internal sealed class LegacyOperationResponse
{
    public bool? IsSuccess { get; set; }

    public string? Message { get; set; }
}

internal sealed class LegacyStatusResponse
{
    public bool Online { get; set; }

    public int Working { get; set; }

    public int TaskStatus { get; set; }

    public int ErrorCode { get; set; }

    public string? ErrorMSG { get; set; }

    public bool InboundCompleted { get; set; }

    public bool OutboundCompleted { get; set; }

    public bool TransferCompleted { get; set; }
}

internal static class LegacyJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
