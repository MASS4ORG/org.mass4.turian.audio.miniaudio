using System;

namespace Turian.Audio.Miniaudio;

/// <summary>miniaudio stereo device with an AOT-safe native callback.</summary>
public sealed unsafe class MiniaudioDevice : IAudioDevice
{
    nint device;

    /// <inheritdoc/>
    public AudioDeviceState State => device == 0 ? AudioDeviceState.Stopped : AudioDeviceState.Started;

    /// <inheritdoc/>
    public void Start(in AudioDeviceConfig config)
    {
        Stop();
        if (config.Format.Channels != 2 || config.Format.SampleRate <= 0 || config.Callback == null)
        {
            throw new ArgumentException("The playback device requires stereo float PCM and a callback.", nameof(config));
        }
        device = MiniaudioNative.OpenDevice((uint)config.Format.SampleRate, (uint)config.BufferFrames,
            config.DeviceName, config.Callback, config.Context);
        if (device == 0)
        {
            throw new InvalidOperationException("miniaudio could not start the requested playback device.");
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (device != 0)
        {
            MiniaudioNative.CloseDevice(device);
            device = 0;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();
}
