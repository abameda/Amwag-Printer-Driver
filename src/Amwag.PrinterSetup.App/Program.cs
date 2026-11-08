using Amwag.PrinterSetup.App.Elevation;
using Amwag.PrinterSetup.App.Flow;
using System.Globalization;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.Silent;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App;

internal static class Program
{
    private static ILog s_log = NullLog.Instance;
    private static CommandLineOptions s_options = new();
    private static int s_fatalErrorShown;

    [STAThread]
    private static int Main(string[] args)
    {
        CommandLineOptions options = CommandLineParser.Parse(args, out ConfigurationPin? workerPin);
        s_options = options;
        ApplicationConfiguration.Initialize();
        InstallExceptionHandlers();

        return options.Role switch
        {
            AppRole.Worker => RunWorker(options, workerPin, args),
            AppRole.Silent => RunSilent(options, args),
            _ => RunInteractive(options, args),
        };
    }

    private static int RunInteractive(CommandLineOptions options, string[] args)
    {
        if (options.Errors.Count > 0)
        {
            HostDialogs.ShowInvalidArguments(options.Language, options.Errors);
            return ExitCodes.InvalidArguments;
        }
        if (options.ShowHelp)
        {
            HostDialogs.ShowHelp(options.Language);
            return ExitCodes.Ready;
        }

        using SingleInstance? instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            HostDialogs.ShowAlreadyRunning(options.Language);
            return ExitCodes.Cancelled;
        }

        return RunLogged(options, args, AppBootstrap.NewSessionId(), log =>
        {
            using var splash = new StartupSplash(UiLanguages.Resolve(options.Language, CultureInfo.CurrentUICulture), AppBootstrap.AppVersion);
            splash.ShowNow();

            FlowEnvironment environment;
            ConfigurationPin pin;
            try
            {
                environment = AppBootstrap.Build(options, log, out pin);
            }
            catch (ConfigurationException ex)
            {
                splash.Close();
                log.Error("config.invalid", "The configuration is not valid; nothing was changed.", ex, new Dictionary<string, object?> { ["errors"] = ex.Errors });
                HostDialogs.ShowConfigurationErrors(options.Language, ex.Source, ex.Errors, log.FilePath);
                return ExitCodes.ConfigurationInvalid;
            }
            catch (StartupArgumentException ex)
            {
                splash.Close();
                log.Error("app.arguments.invalid", ex.Message);
                HostDialogs.ShowStartupError(options.Language, ex.Message, log.FilePath);
                return ExitCodes.InvalidArguments;
            }

            splash.Report("splash.openingWindow", 0.7);
            var runtime = new AppRuntime(
                environment,
                new ElevationBroker(log, options.ConfigPath, options.Address, pin),
                options,
                log,
                AppBootstrap.AppVersion,
                AppBootstrap.ExecutableDirectory);
            Application.Run(splash.HandOver(new MainForm(runtime)));
            return Environment.ExitCode;
        });
    }

    private static int RunSilent(CommandLineOptions options, string[] args)
    {
        if (options is { ShowHelp: true, Errors.Count: 0 })
        {
            SilentRunner.WriteText(options, NullLog.Instance, CommandLineParser.Usage);
            return ExitCodes.Ready;
        }

        return RunLogged(options, args, AppBootstrap.NewSessionId(), log =>
        {
            if (options.Errors.Count == 0)
                return SilentRunner.Run(options, log);

            log.Error("app.arguments.invalid", "The command line is not valid; nothing was changed.", data: new Dictionary<string, object?> { ["errors"] = options.Errors });
            SilentRunner.WriteText(options, log,
                "The command line is not valid:" + string.Concat(options.Errors.Select(e => Environment.NewLine + "- " + e)) +
                Environment.NewLine + Environment.NewLine + CommandLineParser.Usage);
            return ExitCodes.InvalidArguments;
        });
    }

    private static int RunWorker(CommandLineOptions options, ConfigurationPin? pin, string[] args)
    {
        // The worker logs under the window's session id so both logs correlate; unusable arguments get a fresh one.
        string sessionId = options is { Errors.Count: 0, SessionId: { } id } ? id : AppBootstrap.NewSessionId();
        return RunLogged(options, args, sessionId, log =>
        {
            if (options.Errors.Count == 0 && pin is not null)
                return WorkerHost.Run(options, pin, log);

            log.Error("app.arguments.invalid", "The worker command line is not valid; nothing was changed.", data: new Dictionary<string, object?> { ["errors"] = options.Errors });
            return ExitCodes.InvalidArguments;
        });
    }

    /// <summary>Runs one role with its log open, so even an unexpected failure is logged before the process exits.</summary>
    private static int RunLogged(CommandLineOptions options, string[] args, string sessionId, Func<ILog, int> run)
    {
        using JsonLinesLog log = AppBootstrap.OpenLog(options.Role, sessionId);
        s_log = log;
        AppBootstrap.LogStartup(log, options, args);
        int exitCode;
        try
        {
            exitCode = run(log);
        }
        catch (Exception ex)
        {
            ReportFatalError("app.failed", ex);
            exitCode = ExitCodes.InternalError;
        }

        log.Info("app.exit", "AMWAG Printer Setup is exiting.", new Dictionary<string, object?> { ["exitCode"] = exitCode });
        s_log = NullLog.Instance;
        return exitCode;
    }

    private static void InstallExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            ReportFatalError("app.unhandled.ui", e.Exception);
            // After an unhandled exception the window's state is unknown; close rather than carry on half-configured.
            Environment.ExitCode = ExitCodes.InternalError;
            Application.Exit();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatalError("app.unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            s_log.Warn("app.task.unobserved", "A background task failed without being observed.", exception: e.Exception);
            e.SetObserved();
        };
    }

    private static void ReportFatalError(string eventName, Exception? exception)
    {
        s_log.Error(eventName, "Unexpected error.", exception);
        if (s_options.Role != AppRole.Interactive || Interlocked.Exchange(ref s_fatalErrorShown, 1) == 1)
            return;
        try
        {
            HostDialogs.ShowUnexpectedError(s_options.Language, s_log.FilePath);
        }
        catch (Exception)
        {
            // Nothing else can be shown; the log entry above is what IT gets.
        }
    }
}
