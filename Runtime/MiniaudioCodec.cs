using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Turian.Engine.Core;

namespace Turian.Audio.Miniaudio;

/// <summary>WAV, MP3 and FLAC decoder that reads asset streams incrementally through miniaudio.</summary>
public sealed unsafe class MiniaudioCodec : IAudioCodec
{
    static readonly string[] SupportedExtensions = [".wav", ".mp3", ".flac"];

    /// <inheritdoc/>
    public IReadOnlyList<string> Extensions => SupportedExtensions;

    /// <inheritdoc/>
    public IAudioStreamSource OpenStream(IAssetFileProvider asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var input = asset.GetAssetStream();
        try
        {
            if (!input.CanSeek)
            {
                // Format probing rewinds the input, so a forward-only payload is buffered once.
                var buffered = new MemoryStream();
                input.CopyTo(buffered);
                input.Dispose();
                buffered.Position = 0;
                input = buffered;
            }
            return new Decoder(input);
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    // Callbacks run on the thread calling Open, Read or Seek; the decoder is not shared between threads.
    sealed class Decoder : IAudioStreamSource
    {
        readonly Stream input;
        GCHandle self;
        nint decoder;

        public Decoder(Stream input)
        {
            this.input = input;
            self = GCHandle.Alloc(input);
            decoder = MiniaudioNative.OpenDecoder(&ReadInput, &SeekInput, GCHandle.ToIntPtr(self),
                out var channels, out var rate);
            if (decoder == 0)
            {
                self.Free();
                throw new InvalidDataException("miniaudio could not decode the audio asset.");
            }
            Format = new((int)channels, (int)rate);
        }

        public AudioFormat Format { get; }

        public int Read(Span<float> buffer)
        {
            ObjectDisposedException.ThrowIf(decoder == 0, this);
            fixed (float* output = buffer)
            {
                var frames = MiniaudioNative.ReadDecoder(decoder, output, (ulong)(buffer.Length / Format.Channels));
                return frames >= 0 ? checked((int)frames) : throw new InvalidDataException("Audio decoding failed.");
            }
        }

        public void Seek(long frame)
        {
            ObjectDisposedException.ThrowIf(decoder == 0, this);
            ArgumentOutOfRangeException.ThrowIfNegative(frame);
            if (MiniaudioNative.SeekDecoder(decoder, (ulong)frame) != 0)
            {
                throw new InvalidOperationException("The decoder could not seek to the requested frame.");
            }
        }

        public void Dispose()
        {
            if (decoder != 0)
            {
                MiniaudioNative.CloseDecoder(decoder);
                decoder = 0;
            }
            if (self.IsAllocated)
            {
                self.Free();
            }
            input.Dispose();
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        static nuint ReadInput(nint user, byte* buffer, nuint bytes)
        {
            try
            {
                var stream = (Stream)GCHandle.FromIntPtr(user).Target!;
                var span = new Span<byte>(buffer, checked((int)Math.Min(bytes, int.MaxValue)));
                var total = 0;
                int read;
                while (total < span.Length && (read = stream.Read(span[total..])) > 0)
                {
                    total += read;
                }
                return (nuint)total;
            }
            catch
            {
                return nuint.MaxValue;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        static int SeekInput(nint user, long offset, int origin)
        {
            try
            {
                var stream = (Stream)GCHandle.FromIntPtr(user).Target!;
                stream.Seek(offset, origin switch { 0 => SeekOrigin.Begin, 1 => SeekOrigin.Current, _ => SeekOrigin.End });
                return 0;
            }
            catch
            {
                return -1;
            }
        }
    }
}
