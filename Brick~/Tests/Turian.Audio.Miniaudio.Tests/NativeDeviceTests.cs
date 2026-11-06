namespace Turian.Audio.Miniaudio.Tests;

/// <summary>Opt-in native playback qualification.</summary>
public sealed class NativeDeviceTests
{
    /// <summary>Opt-in device qualification runs the complete service callback and shutdown path on real hardware.</summary>
    [Fact]
    public void NativePlaybackDeviceRunsAndJoinsCallbacks()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TURIAN_AUDIO_DEVICE_TEST") == "1",
            "Set TURIAN_AUDIO_DEVICE_TEST=1 to qualify native playback on this workstation.");
        var settings = new AudioSettings();
        using var device = new MiniaudioDevice();
        using var mixer = new AudioMixerGraph(settings, new SimpleSpatializer());
        using var audio = new AudioService(device, mixer, settings);
        for (var i = 0; i < 3; i++)
        {
            audio.Prepare(new AudioEvent { EventName = "native", Loop = true },
                [AudioClip.FromPcm([0], new(1, 48000))]);
            audio.Post("native", Guid.Empty, new AudioParameterSet());
            Assert.True(SpinWait.SpinUntil(() => mixer.ActiveVoiceCount == 1, TimeSpan.FromSeconds(3)));
            Assert.Equal(AudioDeviceState.Started, device.State);
            audio.Reset();
            Assert.Equal(AudioDeviceState.Stopped, device.State);
            Assert.Equal(0, mixer.ActiveVoiceCount);
        }
    }

}
