using Microsoft.Extensions.DependencyInjection;
using Turian.Engine.Core;

namespace Turian.Audio.Miniaudio;

/// <summary>Supplies native playback and WAV decoding only for interactive sessions.</summary>
public sealed class MiniaudioModule : IEngineServiceModule
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
        if (!services.GetApplicationContext().Interactive) { return; }
        services.AddSingleton<IAudioDevice, MiniaudioDevice>();
        services.AddSingleton<IAudioCodec, MiniaudioCodec>();
    }
}
