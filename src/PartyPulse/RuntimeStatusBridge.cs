#if PARTYPULSE_RUNTIME_STATUS
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse;

[SupportedOSPlatform("windows")]
internal sealed class RuntimeStatusBridge(
    NamedPipeServerStream pipe,
    IFramework framework,
    IClientState clientState,
    PlayerIdentityProvider identityProvider,
    TargetPlayerProvider targetProvider,
    VenueLocationProvider locationProvider,
    IPluginLog log,
    CancellationToken stopping,
    Func<VenueAddress?, object>? readFeatures) : IDisposable
{
    private const byte StatusRequest = 1;
    private const byte GameStateRequest = 2;
    private const int ProtocolVersion = 1;
    private const int MaxReplyBytes = 4096;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions GameStateJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Assembly PluginAssembly = typeof(RuntimeStatusBridge).Assembly;
    private static readonly Guid BuildId = PluginAssembly.ManifestModule.ModuleVersionId;
    private static readonly string Version = PluginAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? PluginAssembly.GetName().Version?.ToString() ?? "unknown";

    private readonly Guid sessionId = Guid.NewGuid();

    public static RuntimeStatusBridge? TryStart(
        IFramework framework,
        IClientState clientState,
        PlayerIdentityProvider identityProvider,
        TargetPlayerProvider targetProvider,
        VenueLocationProvider locationProvider,
        IPluginLog log,
        CancellationToken stopping,
        Func<VenueAddress?, object>? readFeatures = null)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("Missing Windows user SID.");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(user);
            // Windows named pipes can also accept SMB clients. Keep this pipe local to the game PC.
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

            var pipe = NamedPipeServerStreamAcl.Create(FormattableString.Invariant($"PartyPulse.RuntimeStatus.v{ProtocolVersion}.{Environment.ProcessId}"),
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, MaxReplyBytes, security);
            var bridge = new RuntimeStatusBridge(pipe, framework, clientState,
                identityProvider, targetProvider, locationProvider, log, stopping, readFeatures);
            _ = Task.Run(bridge.ServeAsync);
            log.Information("PartyPulse runtime status bridge is listening.");
            return bridge;
        }
        catch (Exception exception)
        {
            log.Warning($"PartyPulse runtime status bridge could not start ({exception.GetType().Name}).");
            return null;
        }
    }

    public void Dispose() => pipe.Dispose();

    private async Task ServeAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                request.CancelAfter(RequestTimeout);
                try
                {
                    await ReplyAsync(request.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested)
                {
                }
                catch (IOException)
                {
                    // The caller can disconnect at any point. Accept the next client.
                }
                finally
                {
                    if (!stopping.IsCancellationRequested)
                    {
                        pipe.Disconnect();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (!stopping.IsCancellationRequested)
            {
                log.Warning($"PartyPulse runtime status bridge stopped ({exception.GetType().Name}).");
            }
        }
        finally
        {
            pipe.Dispose();
        }
    }

    private async Task ReplyAsync(CancellationToken cancellationToken)
    {
        var opcode = new byte[1];
        if (await pipe.ReadAsync(opcode, cancellationToken).ConfigureAwait(false) != 1 ||
            opcode[0] is not (StatusRequest or GameStateRequest))
        {
            return;
        }

        var includeGameState = opcode[0] == GameStateRequest;
        var snapshot = await framework.RunOnTick(() => new
        {
            SampledAtUtc = DateTimeOffset.UtcNow,
            LoggedIn = clientState.IsLoggedIn,
            GameState = includeGameState ? ReadGameState() : null,
        }, cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

        var payload = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(payload))
        {
            json.WriteStartObject();
            json.WriteNumber("protocolVersion", ProtocolVersion);
            json.WriteNumber("processId", Environment.ProcessId);
            json.WriteString("sessionId", sessionId);
            json.WriteString("buildId", BuildId);
            json.WriteString("version", Version);
            json.WriteString("sampledAtUtc", snapshot.SampledAtUtc);
            json.WriteBoolean("loggedIn", snapshot.LoggedIn);
            if (snapshot.GameState is not null)
            {
                json.WritePropertyName("gameState");
                JsonSerializer.Serialize(json, snapshot.GameState, GameStateJson);
            }
            json.WriteEndObject();
        }

        if (payload.WrittenCount > MaxReplyBytes)
        {
            return;
        }

        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.WrittenCount);
        await pipe.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(payload.WrittenMemory, cancellationToken).ConfigureAwait(false);

        // Disconnect discards unread bytes. The client closes after reading the framed reply.
        await pipe.ReadAsync(opcode, cancellationToken).ConfigureAwait(false);
    }

    private delegate bool TryRead<T>(out T? value, out string reason) where T : class;

    private sealed record GameRead<T>(T? Value, string? Reason) where T : class;

    private object ReadGameState()
    {
        var address = Read<VenueAddress>(locationProvider.TryGetCurrentHousingAddress);
        return new
        {
            Character = Read<PlayerIdentity>(identityProvider.TryGetCurrent),
            Target = Read<PlayerIdentity>(targetProvider.TryGetCurrentTarget),
            Location = Read<VenueLocation>(locationProvider.TryGetCurrentLocation),
            HousingAddress = address,
            Features = readFeatures?.Invoke(address.Value),
        };
    }

    private static GameRead<T> Read<T>(TryRead<T> read) where T : class
    {
        try
        {
            return read(out var value, out var reason) ? new(value, null) : new(null, reason);
        }
        catch (Exception exception)
        {
            return new(null, $"Game read failed ({exception.GetType().Name}).");
        }
    }
}
#endif
