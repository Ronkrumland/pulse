using Microsoft.Extensions.Logging;
using Pulse.Agent.Windows.Session.Services;
using Pulse.Agent.Windows.Session.UI;

namespace Pulse.Agent.Windows.Session;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\Pulse.Agent.Windows.Session";

    [STAThread]
    private static int Main(string[] args)
    {
        // Elevated helper modes used when saving %ProgramData%\Pulse\agent.json or controlling the Windows Service
        if (args.Length == 3 && args[0].Equals("--write-agent-config", StringComparison.OrdinalIgnoreCase))
        {
            return RunElevatedConfigWrite(args[1], args[2]);
        }

        if (args.Length == 3 && args[0].Equals("--service-action", StringComparison.OrdinalIgnoreCase))
        {
            var (ok, _) = WindowsServiceManager.ExecuteDirect(args[2], args[1]);
            return ok ? 0 : 1;
        }

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddDebug();
        });

        using var context = new SessionApplicationContext(loggerFactory);
        Application.Run(context);
        return 0;
    }

    private static int RunElevatedConfigWrite(string sourceTempPath, string destinationPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(sourceTempPath, destinationPath, overwrite: true);
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
