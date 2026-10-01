using System.Threading;

namespace WritingVault.Tray;

internal static class Program
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, $@"Local\WritingVaultMCP-Tray-{Configuration}", out var ownsMutex);
        if (!ownsMutex)
            return;

        ApplicationConfiguration.Initialize();
        using var context = new VaultTrayContext(VaultServiceController.FindProjectRoot(), Configuration);
        Application.Run(context);
    }
}
