using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Warehouse.Wms.DeviceGateway;
using Warehouse.Wms.DeviceGateway.Legacy;
using Warehouse.Wms.Domain.Devices;

namespace Warehouse.DeviceGateway.ContractTests;

public sealed class LegacyPlcApiGatewayTests
{
    private static DeviceTask CreateTask(
        string idempotencyKey = "legacy-idem-001",
        string source = "1-2",
        string? destination = "3-4",
        string loadingPoint = "0") => new(
        idempotencyKey,
        wmsTaskId: "wms-legacy-001",
        deviceId: "plc-test-01",
        sourceLocation: source,
        destinationLocation: destination,
        loadingPoint: loadingPoint,
        protocolVersion: "legacy-v1");

    [Fact]
    public async Task Inbound_maps_to_the_legacy_route_and_keeps_trigger_success_as_accepted()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("api/plc-operations/inbound", request.RequestUri!.PathAndQuery.TrimStart('/'));
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new { isSuccess = true, message = "triggered" }));
        });
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitInboundAsync(CreateTask());

        Assert.Equal(DeviceOperationStatus.Accepted, result.Status);
        Assert.Equal("legacy-wms-legacy-001", result.DeviceTaskNumber);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Transfer_maps_source_and_destination_coordinates_without_using_erp_data()
    {
        var handler = new StubHttpMessageHandler(async request =>
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("plc-test-01", body.GetProperty("plcId").GetString());
            Assert.Equal(1, body.GetProperty("outShelf").GetInt32());
            Assert.Equal(2, body.GetProperty("outPosition").GetInt32());
            Assert.Equal(3, body.GetProperty("inShelf").GetInt32());
            Assert.Equal(4, body.GetProperty("inPosition").GetInt32());
            return JsonResponse(HttpStatusCode.OK, new { isSuccess = true, message = "triggered" });
        });
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitTransferAsync(CreateTask());

        Assert.Equal(DeviceOperationStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task Outbound_uses_source_location_when_source_and_destination_are_both_present()
    {
        var handler = new StubHttpMessageHandler(async request =>
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, body.GetProperty("shelf").GetInt32());
            Assert.Equal(2, body.GetProperty("position").GetInt32());
            return JsonResponse(HttpStatusCode.OK, new { isSuccess = true, message = "triggered" });
        });
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitOutboundAsync(CreateTask());

        Assert.Equal(DeviceOperationStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task Request_timeout_becomes_physical_state_unknown_and_is_not_retried()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("legacy request timeout")));
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitOutboundAsync(CreateTask("legacy-timeout"));

        Assert.Equal(DeviceOperationStatus.PhysicalStateUnknown, result.Status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_claim_physical_state_unknown()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("request canceled")));
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitInboundAsync(CreateTask("legacy-canceled"), cancellation.Token);

        Assert.Equal(DeviceOperationStatus.Failed, result.Status);
        Assert.Equal("LEGACY_REQUEST_CANCELED", result.ErrorCode);
    }

    [Fact]
    public async Task Service_unavailable_maps_to_offline()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, new { message = "PLC offline" })));
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitOutboundAsync(CreateTask("legacy-offline"));

        Assert.Equal(DeviceOperationStatus.Offline, result.Status);
        Assert.Equal("HTTP_503", result.ErrorCode);
    }

    [Fact]
    public async Task Legacy_busy_response_maps_to_failed_without_retry()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse(HttpStatusCode.BadRequest, new { isSuccess = false, message = "PLC busy" })));
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitInboundAsync(CreateTask("legacy-busy"));

        Assert.Equal(DeviceOperationStatus.Failed, result.Status);
        Assert.Equal("PLC_BUSY", result.ErrorCode);
    }

    [Fact]
    public async Task Successful_http_response_with_invalid_json_is_unknown_and_not_accepted()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-json")
            });
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitInboundAsync(CreateTask("legacy-invalid-json"));

        Assert.Equal(DeviceOperationStatus.Unknown, result.Status);
        Assert.Equal("LEGACY_INVALID_RESPONSE", result.ErrorCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Successful_http_response_without_json_body_is_unknown_and_not_accepted()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            });
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitOutboundAsync(CreateTask("legacy-empty-response"));

        Assert.Equal(DeviceOperationStatus.Unknown, result.Status);
        Assert.Equal("LEGACY_INVALID_RESPONSE", result.ErrorCode);
    }

    [Fact]
    public async Task Successful_http_response_without_is_success_is_unknown_and_not_retried()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse(HttpStatusCode.OK, new { message = "triggered" }));
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitInboundAsync(CreateTask("legacy-missing-success"));

        Assert.Equal(DeviceOperationStatus.Unknown, result.Status);
        Assert.Equal("LEGACY_INVALID_RESPONSE", result.ErrorCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Invalid_location_and_loading_point_are_rejected_before_http_call()
    {
        var handler = new StubHttpMessageHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
                throw new InvalidOperationException("invalid input must not reach the legacy API")));
        var gateway = CreateGateway(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateway.SubmitInboundAsync(CreateTask("legacy-invalid-location", destination: "A-2")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateway.SubmitOutboundAsync(CreateTask("legacy-invalid-loading", loadingPoint: "bad")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateway.SubmitOutboundAsync(CreateTask("legacy-negative-location", source: "-1-2")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateway.SubmitOutboundAsync(CreateTask("legacy-invalid-loading-range", loadingPoint: "2")));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Legacy_status_maps_online_executing_and_completed_flags_to_observation()
    {
        var handler = new StubHttpMessageHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Equal("\"plc-test-01\"", body);
            return JsonResponse(HttpStatusCode.OK, new
            {
            online = true,
            working = 1,
            taskStatus = 1,
            errorCode = 0,
            errorMSG = "",
            inboundCompleted = false,
            outboundCompleted = false,
            transferCompleted = false
            });
        });
        var gateway = CreateGateway(handler, DeviceCapability.TaskQuery, "plc-test-01");

        var observation = await gateway.GetStatusAsync("legacy-wms-001");

        Assert.NotNull(observation);
        Assert.Equal(DeviceOperationStatus.Executing, observation!.Status);
        Assert.Equal(DeviceObservationSource.Polling, observation.Source);
        Assert.Equal(1, observation.ResultVersion);

        var second = await gateway.GetStatusAsync("legacy-wms-001");
        Assert.Equal(2, second!.ResultVersion);
    }

    [Fact]
    public async Task Legacy_capability_probe_keeps_unknown_capabilities_blocked_when_endpoint_is_missing()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse(HttpStatusCode.NotFound, new { message = "not supported" })));
        var probe = new LegacyCapabilityProbe(new LegacyPlcClient(CreateHttpClient(handler)));

        var result = await probe.ProbeAsync("plc-test-01");

        Assert.Equal(DeviceCapability.None, result.Capabilities);
        Assert.Contains("TaskKeyDeduplication", result.BlockedCapabilities);
        Assert.Contains("TaskQuery", result.BlockedCapabilities);
        Assert.Contains("StopControl", result.BlockedCapabilities);
        Assert.Contains("CompletionCallback", result.BlockedCapabilities);
    }

    [Fact]
    public async Task Request_stop_is_explicitly_unsupported_by_the_legacy_contract()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("stop endpoint must not be called")));
        var gateway = CreateGateway(handler);

        var result = await gateway.RequestStopAsync(CreateTask("legacy-stop"));

        Assert.Equal(DeviceOperationStatus.Failed, result.Status);
        Assert.Equal("LEGACY_STOP_UNSUPPORTED", result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Connection_test_maps_legacy_success_to_succeeded()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("api/PlcConfigurations/plc-test-01/test-connection", request.RequestUri!.PathAndQuery.TrimStart('/'));
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new { isSuccess = true, message = "connected" }));
        });
        var gateway = CreateGateway(handler);

        var result = await gateway.TestConnectionAsync("plc-test-01");

        Assert.Equal(DeviceOperationStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Connection_test_timeout_is_not_physical_device_unknown()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("connection timeout")));
        var gateway = CreateGateway(handler);

        var result = await gateway.TestConnectionAsync("plc-test-01");

        Assert.Equal(DeviceOperationStatus.TimedOut, result.Status);
        Assert.Equal("LEGACY_CONNECTION_TIMEOUT", result.ErrorCode);
    }

    private static LegacyPlcApiGateway CreateGateway(
        StubHttpMessageHandler handler,
        DeviceCapability capabilities = DeviceCapability.None,
        string? statusDeviceId = null)
    {
        var client = new LegacyPlcClient(CreateHttpClient(handler));
        return new LegacyPlcApiGateway(client, capabilities, statusDeviceId);
    }

    private static HttpClient CreateHttpClient(StubHttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("http://legacy-plc.test/")
        };
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, object body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = JsonContent.Create(body)
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this(request => Task.FromResult(handler(request)))
        {
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return await _handler(request);
        }
    }
}
