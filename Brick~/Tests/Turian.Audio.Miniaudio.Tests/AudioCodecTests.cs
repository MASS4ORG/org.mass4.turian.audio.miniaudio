namespace Turian.Audio.Miniaudio.Tests;

/// <summary>Checks real miniaudio decoding and the storage-independent PCM boundary.</summary>
public sealed class AudioCodecTests
{
    internal static byte[] Wave()
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write("RIFF"u8);
        writer.Write(36 + 8);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(8000);
        writer.Write(16000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(8);
        writer.Write((short)0);
        writer.Write((short)16384);
        writer.Write((short)-16384);
        writer.Write((short)32767);
        return bytes.ToArray();
    }

    static IAssetFileProvider Provider(byte[] encoded)
    {
        var provider = Substitute.For<IAssetFileProvider>();
        provider.GetAssetStream().Returns(_ => new MemoryStream(encoded));
        return provider;
    }

    static IAssetFileProvider Provider(Func<Stream> open)
    {
        var provider = Substitute.For<IAssetFileProvider>();
        provider.GetAssetStream().Returns(_ => open());
        return provider;
    }

    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    static byte[] LongWave(int frames)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(8000);
        writer.Write(16000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 2);
        for (var i = 0; i < frames; i++) { writer.Write((short)(i % 2 == 0 ? 8192 : -8192)); }
        return bytes.ToArray();
    }

    /// <summary>Counts the encoded bytes a decoder pulls, optionally hiding seeking.</summary>
    sealed class MeteredStream(Stream inner, bool seekable) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>MP3 and FLAC fixtures decode with their channel layout, amplitude and seek-to-start for looping.</summary>
    [Theory]
    [InlineData("tone.mp3", 1)]
    [InlineData("tone.flac", 2)]
    public void CompressedFormatsDecodeAndRewind(string name, int channels)
    {
        var codec = new MiniaudioCodec();
        Assert.Contains(Path.GetExtension(name), codec.Extensions);
        using var source = codec.OpenStream(Provider(() => File.OpenRead(Fixture(name))));
        Assert.Equal(new AudioFormat(channels, 22050), source.Format);
        var pcm = new float[30000 * channels];
        var frames = 0;
        int read;
        while ((read = source.Read(pcm.AsSpan(frames * channels))) > 0) { frames += read; }
        Assert.InRange(frames, 22050, 24000);
        var peak = pcm.Max(static sample => Math.Abs(sample));
        Assert.InRange(peak, 0.45f, 0.55f);
        if (channels == 2)
        {
            Assert.Equal(pcm[2000], -pcm[2001], 4);
        }
        source.Seek(0);
        var again = new float[64 * channels];
        Assert.Equal(64, source.Read(again));
        Assert.Equal(pcm.AsSpan(0, again.Length).ToArray(), again);
    }

    /// <summary>Decoding pulls encoded bytes on demand instead of copying the whole payload first.</summary>
    [Fact]
    public void SeekableAssetsAreReadIncrementally()
    {
        var encoded = LongWave(80000);
        var metered = new MeteredStream(new MemoryStream(encoded), seekable: true);
        using var source = new MiniaudioCodec().OpenStream(Provider(() => metered));
        var pcm = new float[256];
        Assert.Equal(256, source.Read(pcm));
        Assert.Equal(0.25f, pcm[0], 4);
        Assert.InRange(metered.BytesRead, 1, encoded.Length / 4);
    }

    /// <summary>Forward-only payloads are buffered once so format probing can rewind them.</summary>
    [Fact]
    public void ForwardOnlyAssetsAreBuffered()
    {
        var metered = new MeteredStream(new MemoryStream(Wave()), seekable: false);
        using var source = new MiniaudioCodec().OpenStream(Provider(() => metered));
        var pcm = new float[8];
        Assert.Equal(4, source.Read(pcm));
        Assert.Equal(0.5f, pcm[1], 5);
        Assert.Equal(Wave().Length, metered.BytesRead);
    }

    /// <summary>The service streams a real FLAC payload through the decoder thread into the production mixer.</summary>
    [Fact]
    public void ServiceStreamsCompressedAssets()
    {
        var codec = new MiniaudioCodec();
        var provider = Provider(() => File.OpenRead(Fixture("tone.flac")));
        var settings = new AudioSettings { SampleRate = 22050, StreamBufferFrames = 4096 };
        using var device = new SilentAudioDevice();
        using var mixer = new AudioMixerGraph(settings, new SimpleSpatializer());
        using var audio = new AudioService(device, mixer, settings);
        audio.PrepareStream(new AudioEvent { EventName = "flac" }, [() => codec.OpenStream(provider)]);
        audio.Post("flac", Guid.Empty, new AudioParameterSet());
        var pcm = new float[512];
        var peak = 0f;
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (audio.Statistics.ActiveVoices > 0 || peak == 0)
        {
            device.Render(pcm);
            peak = Math.Max(peak, pcm.Max(static sample => Math.Abs(sample)));
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), "The FLAC stream did not finish.");
            Thread.Sleep(1);
        }
        Assert.InRange(peak, 0.45f, 0.55f);
        Assert.Equal(0, audio.Statistics.StreamUnderruns);
    }

    /// <summary>WAV data is decoded through a provider, and frame-based reads and seeks match exact PCM.</summary>
    [Fact]
    public void WavDecoderReadsSeeksAndEnds()
    {
        var codec = new MiniaudioCodec();
        Assert.Contains(".wav", codec.Extensions);
        using var source = codec.OpenStream(Provider(Wave()));
        Assert.Equal(new AudioFormat(1, 8000), source.Format);
        var pcm = new float[8];
        Assert.Equal(4, source.Read(pcm));
        Assert.Equal(0, pcm[0]);
        Assert.Equal(0.5f, pcm[1], 5);
        Assert.Equal(-0.5f, pcm[2], 5);
        Assert.Equal(0, source.Read(pcm));
        source.Seek(1);
        Assert.Equal(3, source.Read(pcm));
        Assert.Equal(0.5f, pcm[0], 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Seek(-1));
        source.Dispose();
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.Read(pcm));
        Assert.Throws<ObjectDisposedException>(() => source.Seek(0));
    }

    /// <summary>Decoded clips own their PCM independently of the decoder and encoded asset.</summary>
    [Fact]
    public void AudioClipOwnsDecodedPcm()
    {
        var source = new MiniaudioCodec().OpenStream(Provider(Wave()));
        var clip = AudioClip.Decode(source);
        source.Dispose();
        Assert.Equal(4, clip.Frames);
        Assert.Equal(new AudioFormat(1, 8000), clip.Format);
        Assert.Equal(-0.5f, clip.Samples[2], 5);
        using var device = new SilentAudioDevice();
        using var mixer = new AudioMixerGraph(new AudioSettings(), new SimpleSpatializer());
        using var audio = new AudioService(device, mixer, new AudioSettings());
        audio.Prepare(new AudioEvent { EventName = "decoded", Loop = true }, [clip]);
        audio.Post("decoded", Guid.Empty, new AudioParameterSet());
        var pcm = new float[128];
        device.Render(pcm);
        Assert.Contains(pcm, static sample => sample != 0);
    }

    /// <summary>Corrupt codec input is rejected without retaining a decoder or pinned buffer.</summary>
    [Fact]
    public void CorruptWavIsRejected()
    {
        var codec = new MiniaudioCodec();
        for (var i = 0; i < 10; i++)
        {
            Assert.Throws<InvalidDataException>(() => codec.OpenStream(Provider([1, 2, 3, 4])));
        }
        Assert.Throws<ArgumentNullException>(() => codec.OpenStream(null!));
    }

    /// <summary>The importer recognizes only the decodable extensions and creates typed metadata.</summary>
    [Fact]
    public void ImporterCreatesAudioClipMetadata()
    {
        var importer = new AudioClipImporter();
        Assert.True(importer.IsValid("Assets/Sound.WAV"));
        Assert.True(importer.IsValid("Assets/Music.mp3"));
        Assert.True(importer.IsValid("Assets/Music.Flac"));
        Assert.False(importer.IsValid("Assets/Sound.ogg"));
        Assert.False(importer.IsValid("Assets/Sound.opus"));
        Assert.False(importer.IsValid(string.Empty));
        Assert.Equal("Assets/Sound.wav", Assert.IsType<AudioClip>(importer.CreateAsset("Assets/Sound.wav")).RelativePath);
    }

    /// <summary>Custom codecs can replace the default decoder without changing playback contracts.</summary>
    [Fact]
    public void RegistrySelectsCodecBySourceExtension()
    {
        var codec = Substitute.For<IAudioCodec>();
        codec.Extensions.Returns(new[] { ".test" });
        var source = Substitute.For<IAudioStreamSource>();
        var provider = Provider([]);
        codec.OpenStream(provider).Returns(source);
        var registry = new AudioCodecRegistry([codec]);
        Assert.Same(source, registry.OpenStream(provider, ".TEST"));
        Assert.Throws<NotSupportedException>(() => registry.OpenStream(provider, ".wav"));
        Assert.Throws<ArgumentException>(() => new AudioCodecRegistry([codec, codec]));
        codec.Extensions.Returns(new[] { "bad" });
        Assert.Throws<ArgumentException>(() => new AudioCodecRegistry([codec]));
    }

    /// <summary>Runtime loading uses imported bytes and catalog identity after source and metadata are absent.</summary>
    [Fact]
    public async Task RuntimeLoaderDecodesImportedAudioWithoutSources()
    {
        var directory = Directory.CreateTempSubdirectory("turian-audio-import-");
        try
        {
            var assets = Directory.CreateDirectory(Path.Combine(directory.FullName, "Assets"));
            var imported = Directory.CreateDirectory(Path.Combine(directory.FullName, ".Cache", "Audio"));
            var source = Path.Combine(assets.FullName, "click.wav");
            var payload = Path.Combine(imported.FullName, "primary.wav");
            File.WriteAllBytes(payload, Wave());
            var metadata = new AudioClip { RelativePath = source };
            var database = new AssetDatabase();
            Assert.True(database.RegisterAsset(metadata, payload));
            var loader = new RuntimeAssetLoader(database);
            var clip = await loader.LoadAsync<AudioClip>(metadata.Id);
            Assert.NotNull(clip);
            using (var sourcePcm = new MiniaudioCodec().OpenStream(clip.ContentProvider!))
            {
                clip.LoadPcm(sourcePcm);
            }
            Assert.Equal(metadata.Id, clip.Id);
            Assert.Equal(4, clip.Frames);
            Assert.Equal(0.5f, clip.Samples[1], 5);
            Assert.Same(clip, await loader.LoadAsync<AudioClip>(metadata.Id));
            loader.Release(metadata.Id);
            Assert.NotSame(clip, await loader.LoadAsync<AudioClip>(metadata.Id));
        }
        finally { directory.Delete(true); }
    }

    /// <summary>Event assets and their clip references retain identity through serialization.</summary>
    [Fact]
    public void EventAssetsRoundTripClipReferences()
    {
        var clipId = Guid.NewGuid();
        var original = new AudioEvent
        {
            EventName = "ui.click",
            Volume = 0.5f,
            Loop = true,
            Clips = [new AssetReference<AudioClip>(clipId)],
        };
        var copy = Assert.IsType<AudioEvent>(Serializer.LoadData<DataAsset>(Serializer.Serialize<DataAsset>(original)));
        Assert.Equal("ui.click", copy.EventName);
        Assert.Equal(0.5f, copy.Volume);
        Assert.True(copy.Loop);
        Assert.Equal(clipId, Assert.Single(copy.Clips).AssetId);
    }
}
