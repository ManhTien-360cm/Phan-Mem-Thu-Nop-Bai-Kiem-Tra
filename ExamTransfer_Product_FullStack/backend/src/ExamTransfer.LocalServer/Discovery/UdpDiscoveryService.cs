using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.LocalServer.Discovery;

public sealed class DiscoveryRuntimeState
{
    public bool Enabled { get; internal set; }
    public bool Listening { get; internal set; }
    public int? ListeningPort { get; internal set; }
    public string? LastErrorCode { get; internal set; }
}

public sealed class UdpDiscoveryService(
    IServiceScopeFactory scopeFactory,
    IOptions<ExamTransferOptions> options,
    DiscoveryRuntimeState state,
    ILogger<UdpDiscoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        state.Enabled = options.Value.Discovery.Enabled;
        if (!options.Value.Discovery.Enabled) return;
        var discoveryPort = options.Value.Discovery.Port;
        var bindFailureLogged = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpClient? udp = null;
            try
            {
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));

                state.Listening = true;
                state.ListeningPort = discoveryPort;
                state.LastErrorCode = null;
                bindFailureLogged = false;
                logger.LogInformation("UDP discovery listening on 0.0.0.0:{Port}", discoveryPort);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var received = await udp.ReceiveAsync(stoppingToken);
                        if (!DiscoveryProtocol.TryParseRequest(received.Buffer, out var request)
                            || request is null)
                        {
                            logger.LogDebug(
                                "Discarded malformed or incompatible UDP discovery request from {RemoteEndpoint}.",
                                received.RemoteEndPoint);
                            continue;
                        }
                        using var scope = scopeFactory.CreateScope();
                        var lanAccessPolicy = scope.ServiceProvider.GetRequiredService<ILanAccessPolicy>();
                        var decision = lanAccessPolicy.Evaluate(received.RemoteEndPoint.Address.ToString());
                        if (!decision.Allowed)
                        {
                            state.LastErrorCode = ErrorCodes.LanAccessDenied;
                            logger.LogWarning(
                                "UDP discovery response suppressed. RuntimeMode={RuntimeMode}; RemoteIp={RemoteIp}; EffectiveClientIp={EffectiveClientIp}; AllowedCidrs={AllowedCidrs}; MatchedRange={MatchedRange}; DeniedReason={DeniedReason}; SessionId={SessionId}; TraceId={TraceId}",
                                decision.RuntimeMode,
                                decision.RemoteIp ?? "unknown",
                                decision.EffectiveClientIp ?? "unknown",
                                decision.AllowedCidrs.Count == 0 ? "<none>" : string.Join(',', decision.AllowedCidrs),
                                decision.MatchedRange ?? "<none>",
                                decision.DeniedReason,
                                "<discovery>",
                                request.RequestId);
                            continue;
                        }
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var endpoint = LanNetworkConfiguration.ResolveAdvertisedEndpoint(
                            options.Value,
                            received.RemoteEndPoint.Address);
                        if (!endpoint.Ready)
                        {
                            state.LastErrorCode = endpoint.Code;
                            logger.LogWarning(
                                "UDP discovery response suppressed: {Code}. {Detail}",
                                endpoint.Code,
                                endpoint.Detail);
                            continue;
                        }
                        var serverId = MachineFingerprint();
                        var sessions = await OpenSessionDiscoveryBuilder.BuildAsync(
                            db,
                            options.Value,
                            endpoint.Address!,
                            serverId,
                            request.RoomCode,
                            stoppingToken);
                        var response = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryServerDto(
                            DiscoveryProtocol.ProtocolVersion,
                            Environment.MachineName,
                            endpoint.Address!,
                            options.Value.Server.Port,
                            MachineFingerprint(),
                            sessions.Count,
                            ReleaseIdentity.SemanticVersion,
                            DateTimeOffset.UtcNow,
                            serverId,
                            request.RequestId,
                            sessions,
                            ReleaseIdentity.BuildId,
                            discoveryPort,
                            ReleaseIdentity.SemanticVersion));
                        await udp.SendAsync(response, received.RemoteEndPoint, stoppingToken);
                        logger.LogInformation(
                            "UDP discovery response sent. RequestId={RequestId}; Remote={Remote}; AdvertisedEndpoint=http://{Address}:{Port}; RoomFilter={RoomFilter}; SessionCount={SessionCount}",
                            request.RequestId,
                            received.RemoteEndPoint,
                            endpoint.Address,
                            options.Value.Server.Port,
                            MaskRoomCode(request.RoomCode),
                            sessions.Count);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "UDP discovery request failed");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (SocketException ex)
            {
                state.Listening = false;
                state.ListeningPort = null;
                state.LastErrorCode = "UDP_DISCOVERY_PORT_CONFLICT";
                if (!bindFailureLogged)
                {
                    logger.LogWarning(
                        ex,
                        "UDP discovery could not bind fixed port {Port}. ExamTransfer will not fall back to another port; close the owning process and restart Local Server. REST remains available for actionable health diagnostics.",
                        discoveryPort);
                    bindFailureLogged = true;
                }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            }
            finally
            {
                udp?.Dispose();
                state.Listening = false;
                state.ListeningPort = null;
            }
        }

        if (stoppingToken.IsCancellationRequested)
        {
            state.LastErrorCode = null;
        }
    }

    private static string MachineFingerprint()
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|ExamTransfer|discovery"));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string MaskRoomCode(string? roomCode)
    {
        if (string.IsNullOrWhiteSpace(roomCode)) return "<none>";
        return roomCode.Length <= 2
            ? new string('*', roomCode.Length)
            : $"{roomCode[0]}{new string('*', roomCode.Length - 2)}{roomCode[^1]}";
    }
}
