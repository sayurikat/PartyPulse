using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace PartyPulse.Windows;

internal sealed class PublicOpeningImages(ITextureProvider textureProvider, IPluginLog log) : IDisposable
{
    private sealed record ImageLoad(CancellationTokenSource Cancellation, Task<IDalamudTextureWrap?> Texture);

    private readonly Dictionary<string, ImageLoad> loads = new(StringComparer.Ordinal);
    private readonly HttpClient client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxConnectionsPerServer = 4,
    })
    {
        Timeout = TimeSpan.FromSeconds(8),
        MaxResponseContentBufferSize = 1024 * 1024,
    };

    public IDalamudTextureWrap? Get(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!loads.TryGetValue(url, out var load))
        {
            if (!PublicOpeningRenderer.TryGetHttpsLink(url, out var uri) || uri!.IdnHost != "static-cdn.jtvnw.net")
            {
                return null;
            }

            var cancellation = new CancellationTokenSource();
            load = new ImageLoad(cancellation, DownloadAsync(uri, cancellation.Token));
            loads.Add(url, load);
        }

        return load.Texture.IsCompletedSuccessfully ? load.Texture.Result : null;
    }

    public void Retain(IReadOnlyList<PublicOpeningDj>? djs)
    {
        if (djs is null)
        {
            Clear();
            return;
        }

        var retained = new HashSet<string?>(StringComparer.Ordinal);
        foreach (var dj in djs)
        {
            retained.Add(dj.TwitchProfileImageUrl);
            retained.Add(dj.TwitchStreamPreviewUrl);
        }

        foreach (var (url, load) in loads.Where(item => !retained.Contains(item.Key) ||
                     (item.Value.Texture.IsCompletedSuccessfully && item.Value.Texture.Result is null)).ToArray())
        {
            _ = ReleaseAsync(load);
            loads.Remove(url);
        }
    }

    public void Clear()
    {
        foreach (var load in loads.Values)
        {
            _ = ReleaseAsync(load);
        }

        loads.Clear();
    }

    public void Dispose()
    {
        Clear();
        client.Dispose();
    }

    private async Task<IDalamudTextureWrap?> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || mediaType is not ("image/jpeg" or "image/png" or "image/webp"))
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return await textureProvider.CreateFromImageAsync(bytes, "PartyPulse Twitch image", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            log.Debug(exception, "Could not load a public opening Twitch image.");
            return null;
        }
    }

    private static async Task ReleaseAsync(ImageLoad load)
    {
        using var cancellation = load.Cancellation;
        cancellation.Cancel();
        var texture = await load.Texture.ConfigureAwait(false);
        texture?.Dispose();
    }
}
