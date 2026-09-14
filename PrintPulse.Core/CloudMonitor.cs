using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using System.Text.Json;

namespace PrintPulse.Core;

public sealed class CloudMonitor(BambuApi api)
{
    public event Action<JsonElement[]>? Devices;
    public event Action<string, JsonElement>? Report;
    public event Action<JsonElement[]>? TaskMetadata;
    public event Action<string, bool>? Connection;
    public async Task Run(Session session, CancellationToken ct)
    {
        var attempt = 0;
        var lastPush = new Dictionary<string, DateTimeOffset>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Connection?.Invoke("Connecting to Bambu Cloud…", false);
                var devices = await api.Discover(session, ct);
                Devices?.Invoke(devices);
                using var mqtt = new MqttFactory().CreateMqttClient();
                mqtt.ApplicationMessageReceivedAsync += e =>
                {
                    try
                    {
                        var topic = e.ApplicationMessage.Topic.Split('/');
                        if (topic.Length == 3 && topic[0] == "device" && topic[2] == "report" && e.ApplicationMessage.PayloadSegment.Count <= 2_000_000)
                        {
                            using var doc = JsonDocument.Parse(e.ApplicationMessage.PayloadSegment);
                            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("print", out var print) && print.ValueKind == JsonValueKind.Object) Report?.Invoke(topic[1], print.Clone());
                        }
                    } catch (JsonException) { /* Malformed reports do not end other subscriptions. */ }
                    return Task.CompletedTask;
                };
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                mqtt.DisconnectedAsync += e => { disconnected.TrySetResult(); return Task.CompletedTask; };
                var options = new MqttClientOptionsBuilder().WithClientId("PrintPulse_" + Guid.NewGuid().ToString("N"))
                    .WithTcpServer(session.China ? "cn.mqtt.bambulab.com" : "us.mqtt.bambulab.com", 8883)
                    .WithCredentials(session.Username, session.Token).WithTlsOptions(o => o.UseTls())
                    .WithProtocolVersion(MqttProtocolVersion.V311).WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                    .WithTimeout(TimeSpan.FromSeconds(20)).WithCleanSession().Build();
                var connected = await mqtt.ConnectAsync(options, ct);
                if (connected.ResultCode is MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.NotAuthorized)
                    throw new CloudException("Cloud session rejected. Sign in again in Settings.", true);
                if (connected.ResultCode != MqttClientConnectResultCode.Success) throw new CloudException("Cloud connection refused. Retrying…");
                await Subscribe(mqtt, devices, lastPush, ct);
                Connection?.Invoke(devices.Length == 0 ? "Connected · No printers on this account" : "Live · Bambu Cloud", true);
                attempt = 0;
                var nextDiscovery = DateTimeOffset.UtcNow.AddMinutes(5);
                while (mqtt.IsConnected && !ct.IsCancellationRequested)
                {
                    try { TaskMetadata?.Invoke(await api.Tasks(session, ct)); }
                    catch (CloudException e) when (!e.Expired) { }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
                    if (DateTimeOffset.UtcNow >= nextDiscovery)
                    {
                        devices = await api.Discover(session, ct); Devices?.Invoke(devices);
                        await Subscribe(mqtt, devices, lastPush, ct);
                        nextDiscovery = DateTimeOffset.UtcNow.AddMinutes(5);
                    }
                    await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(60), ct), disconnected.Task);
                    ct.ThrowIfCancellationRequested();
                    if (disconnected.Task.IsCompleted) break;
                }
                throw new CloudException("Cloud disconnected · Reconnecting…");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (MQTTnet.Adapter.MqttConnectingFailedException e) when (e.ResultCode is MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.NotAuthorized)
            { Connection?.Invoke("Cloud session rejected. Sign in again in Settings.", false); return; }
            catch (CloudException e) when (e.Expired) { Connection?.Invoke(e.Message, false); return; }
            catch (Exception e) when (e is CloudException or HttpRequestException or OperationCanceledException or MQTTnet.Exceptions.MqttCommunicationException or System.IO.IOException)
            {
                Connection?.Invoke("Cloud disconnected · Last-known data · Retrying…", false);
            }
            var delay = Math.Min(120, 3 * Math.Pow(2, Math.Min(attempt++, 6))) + Random.Shared.NextDouble() * 2;
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { return; }
        }
    }
    private static async Task Subscribe(IMqttClient mqtt, JsonElement[] devices, Dictionary<string, DateTimeOffset> lastPush, CancellationToken ct)
    {
        foreach (var device in devices)
        {
            var id = Printer.Text(device, "dev_id"); if (string.IsNullOrEmpty(id) || id.Contains('/') || id.Contains('#') || id.Contains('+')) continue;
            var response = await mqtt.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(f => f.WithTopic($"device/{id}/report")).Build(), ct);
            if (response.Items.Any(x => (int)x.ResultCode >= 128)) throw new CloudException("Printer subscription was rejected. Retrying…");
            if (!lastPush.TryGetValue(id, out var last) || DateTimeOffset.UtcNow - last > TimeSpan.FromMinutes(5))
            {
                await mqtt.PublishAsync(new MqttApplicationMessageBuilder().WithTopic($"device/{id}/request")
                    .WithPayload("{\"pushing\":{\"sequence_id\":\"0\",\"command\":\"pushall\",\"version\":1,\"push_target\":1}}").Build(), ct);
                lastPush[id] = DateTimeOffset.UtcNow;
            }
        }
    }
}
