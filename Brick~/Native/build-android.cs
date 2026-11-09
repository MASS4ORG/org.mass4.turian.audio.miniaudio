#!/usr/bin/env dotnet
// Rebuilds the Android native audio libraries with 16 KB ELF alignment: dotnet run Brick~/Native/build-android.cs -- <NDK directory>

using System.Diagnostics;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: dotnet run Brick~/Native/build-android.cs -- <NDK directory>");
    return 2;
}

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", "..", ".."));
var compilers = Path.Combine(args[0], "toolchains", "llvm", "prebuilt", "linux-x86_64", "bin");
foreach (var (rid, compiler) in new[] { ("android-arm64", "aarch64-linux-android33-clang"), ("android-x64", "x86_64-linux-android33-clang") })
{
    var output = Path.Combine(root, "runtimes", rid, "native", "libturian_audio.so");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var start = new ProcessStartInfo(Path.Combine(compilers, compiler))
    {
        ArgumentList =
        {
            "-shared", "-fPIC", "-O2", "-fvisibility=hidden", "-Wl,-z,max-page-size=16384",
            "-Wl,-z,common-page-size=16384", Path.Combine(root, "Brick~", "Native", "turian_audio.c"),
            "-o", output, "-ldl", "-lm",
        },
    };
    using var process = Process.Start(start)!;
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        return process.ExitCode;
    }
    Console.WriteLine($"{rid}: {new FileInfo(output).Length} bytes");
}

return 0;
