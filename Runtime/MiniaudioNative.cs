using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Turian.Audio.Miniaudio;

static unsafe partial class MiniaudioNative
{
    const string Library = "turian_audio";

    static MiniaudioNative() => NativeLibrary.SetDllImportResolver(typeof(MiniaudioNative).Assembly, Resolve);

    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Published and AOT players use the application directory; assembly paths support editor precast loading.")]
    static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) { return 0; }
        var fileName = OperatingSystem.IsWindows() ? "turian_audio.dll"
            : OperatingSystem.IsMacOS() ? "libturian_audio.dylib" : "libturian_audio.so";
        var root = AppContext.BaseDirectory;
        var candidate = Path.Combine(root, fileName);
        if (File.Exists(candidate)) { return NativeLibrary.Load(candidate); }
        if (RuntimeFeature.IsDynamicCodeSupported && !string.IsNullOrEmpty(assembly.Location))
        {
            root = Path.GetDirectoryName(assembly.Location)!;
            candidate = Path.Combine(root, fileName);
            if (File.Exists(candidate)) { return NativeLibrary.Load(candidate); }
            candidate = Path.Combine(root, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", fileName);
            if (File.Exists(candidate)) { return NativeLibrary.Load(candidate); }
        }
        return 0;
    }

    [LibraryImport(Library, EntryPoint = "ta_device_open", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint OpenDevice(uint rate, uint period, string name,
        delegate* unmanaged[Cdecl]<nint, float*, uint, void> callback, nint user);

    [LibraryImport(Library, EntryPoint = "ta_device_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CloseDevice(nint device);

    [LibraryImport(Library, EntryPoint = "ta_decoder_open")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint OpenDecoder(delegate* unmanaged[Cdecl]<nint, byte*, nuint, nuint> read,
        delegate* unmanaged[Cdecl]<nint, long, int, int> seek, nint user, out uint channels, out uint rate);

    [LibraryImport(Library, EntryPoint = "ta_decoder_read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial long ReadDecoder(nint decoder, float* output, ulong frames);

    [LibraryImport(Library, EntryPoint = "ta_decoder_seek")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SeekDecoder(nint decoder, ulong frame);

    [LibraryImport(Library, EntryPoint = "ta_decoder_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CloseDecoder(nint decoder);
}
