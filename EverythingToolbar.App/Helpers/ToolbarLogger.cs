using System;
using System.IO;
using System.Reflection;
using System.Threading;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace EverythingToolbar.App.Helpers
{
    public static class ToolbarLogger
    {
        private static readonly string DebugFlagFileName = Path.Combine(ConfigPaths.GetConfigDirectory(), "debug.txt");
        private static readonly LogFactory LogFactory = new LogFactory();
        private static ILogger? RootLogger;
        private static LoggingRule? DebugRule;
        private static long DroppedDebugEventCount;

        public static long DroppedDebugEvents => Interlocked.Read(ref DroppedDebugEventCount);

        public static ILogger GetLogger(string name)
        {
            return LogFactory.GetLogger(name);
        }

        public static ILogger GetLogger<T>()
        {
            return LogFactory.GetLogger(typeof(T).FullName);
        }

        private static bool _forceDebugLogging;

        private static LogLevel GetLogLevel()
        {
            return _forceDebugLogging || File.Exists(DebugFlagFileName) ? LogLevel.Debug : LogLevel.Info;
        }

        /// <summary>
        /// Toggle debug-level file logging at runtime (settings switch or debug.txt).
        /// </summary>
        public static void SetDebugLoggingEnabled(bool enabled)
        {
            _forceDebugLogging = enabled;
            if (DebugRule == null)
                return;

            if (GetLogLevel() == LogLevel.Debug)
                DebugRule.EnableLoggingForLevel(LogLevel.Debug);
            else
                DebugRule.DisableLoggingForLevel(LogLevel.Debug);

            LogFactory.ReconfigExistingLoggers();
            RootLogger?.Debug("Debug logging configuration updated. Settings switch enabled={0}.", enabled);
        }

        private static void LogVersionInformation(ILogger logger)
        {
            logger.Debug("Debug logging enabled.");
            logger.Info(
                $"EverythingToolbar {Assembly.GetExecutingAssembly().GetName().Version} started. OS: {Environment.OSVersion}"
            );
        }

        private static void InitializeExceptionLoggers(ILogger logger)
        {
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (IsKnownBenignException(e.Exception))
                    return;

                logger.Debug(e.Exception, "Unhandled first chance exception");
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                logger.Error((Exception)args.ExceptionObject, "Unhandled exception");
            };
        }

        private static bool IsKnownBenignException(Exception exception)
        {
            // Cancellation is regular control flow (e.g. a search query superseded by a newer one)
            if (exception is OperationCanceledException)
                return true;

            // XmlSerializer probes for pre-generated *.XmlSerializers assemblies and falls back
            // to runtime code generation when they do not exist
            if (
                exception is FileNotFoundException fileNotFound
                && fileNotFound.FileName?.Contains(".XmlSerializers") == true
            )
                return true;

            return false;
        }

        private static void ConfigureLogger()
        {
            var logfile = new FileTarget("logfile")
            {
                FileName = Path.Combine(Path.GetTempPath(), "EverythingToolbar.log"),
                ArchiveEvery = FileArchivePeriod.Day,
                ArchiveNumbering = ArchiveNumberingMode.Date,
                MaxArchiveFiles = 3,
                KeepFileOpen = true,
                OpenFileCacheTimeout = 30,
                ConcurrentWrites = true,
                Layout =
                    "${longdate}|${level:uppercase=true}|${logger}|pid=${processid}|tid=${threadid}|doubleCtrl=${scopeproperty:item=DoubleCtrlTrigger}|${message}|${exception:format=tostring}",
            };
            // Debug traces can originate in WH_KEYBOARD_LL callbacks. Never wait for disk I/O
            // or a full logging queue there: Windows can silently remove a slow keyboard hook.
            var debugFile = new AsyncTargetWrapper(logfile, 4096, AsyncTargetWrapperOverflowAction.Discard)
            {
                Name = "debug-logfile",
            };
            debugFile.LogEventDropped += (_, _) => Interlocked.Increment(ref DroppedDebugEventCount);
            DebugRule = new LoggingRule("*", LogLevel.Debug, LogLevel.Debug, debugFile);
            if (GetLogLevel() != LogLevel.Debug)
                DebugRule.DisableLoggingForLevel(LogLevel.Debug);

            var fileRule = new LoggingRule("*", LogLevel.Info, logfile);
            var config = new LoggingConfiguration();
            config.LoggingRules.Add(DebugRule);
            config.LoggingRules.Add(fileRule);
            LogFactory.Configuration = config;
        }

        public static void Initialize(string name)
        {
            ConfigureLogger();

            RootLogger = GetLogger(name);
            LogVersionInformation(RootLogger);
            InitializeExceptionLoggers(RootLogger);
        }

        public static void LogUiThreadException(Exception exception) =>
            RootLogger?.Error(exception, "Unhandled exception on UI thread");
    }
}
