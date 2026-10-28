using System.Diagnostics;

namespace SubReel.Launcher.Rust.Infrastructure.Launching;

public interface IRustProcessStarter
{
    int Start(ProcessStartInfo startInfo);
}
