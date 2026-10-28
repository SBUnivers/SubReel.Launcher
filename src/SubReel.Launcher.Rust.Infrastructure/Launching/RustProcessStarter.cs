using System.Diagnostics;

namespace SubReel.Launcher.Rust.Infrastructure.Launching;

public sealed class RustProcessStarter : IRustProcessStarter
{
    public int Start(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows не создала процесс клиента Pomme.");
        return process.Id;
    }
}
