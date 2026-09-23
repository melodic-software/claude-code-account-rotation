using System.Text.Json;
using System.Threading.Channels;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Switching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Hosting;

/// <summary>
/// Watches the state file and repairs a stale <c>oauthAccount</c> block as soon
/// as a session writes one back (see
/// <see cref="LiveDirectorySwitch.RepairStaleIdentityAsync"/>). Edge-triggered
/// by the file system, never a timer: every change to the file wakes one
/// debounced repair pass, and one pass runs at startup for a block written
/// while the tool was not running.
/// </summary>
internal sealed partial class StateFileWatcher : BackgroundService
{
    private static readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(750);

    private readonly LiveDirectorySwitch _executor;
    private readonly SwitchOptions _options;
    private readonly ILogger<StateFileWatcher> _logger;
    private readonly Channel<bool> _wakeups = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public StateFileWatcher(LiveDirectorySwitch executor, SwitchOptions options, ILogger<StateFileWatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _executor = executor;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(_options.StateFilePath))!;
        string fileName = Path.GetFileName(_options.StateFilePath);
        using FileSystemWatcher watcher = new(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = false,
        };
        watcher.Changed += (_, _) => _wakeups.Writer.TryWrite(true);
        watcher.Created += (_, _) => _wakeups.Writer.TryWrite(true);
        watcher.Renamed += (_, _) => _wakeups.Writer.TryWrite(true);
        watcher.Error += (_, error) => LogWatcherError(error.GetException().Message);
        watcher.EnableRaisingEvents = true;

        _wakeups.Writer.TryWrite(true);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wakeups.Reader.ReadAsync(stoppingToken);
                await Task.Delay(_debounce, stoppingToken);
                IdentityRepair outcome = await _executor.RepairStaleIdentityAsync(stoppingToken);
                if (outcome == IdentityRepair.Busy)
                {
                    // A switch is running; it patches the file itself and the write it makes
                    // wakes this loop again.
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException exception)
            {
                LogRepairFailed(exception.Message);
            }
            catch (InvalidDataException exception) when (CliLogoutMonitor.IsTokenAbsence(exception))
            {
                // Recorded once, on the read. Logging the exception would repeat
                // the credential path on every wake.
            }
            catch (InvalidDataException exception)
            {
                LogRepairFailed(exception.Message);
            }
            catch (JsonException exception)
            {
                // The state file reader turns a torn read into an InvalidDataException
                // above. This is the guard for every other file the repair parses: a
                // parse failure anywhere under it is a bad file, never a reason to stop
                // the host, which is what an unhandled one here does.
                LogRepairFailed(exception.Message);
            }
            catch (ArgumentException exception)
            {
                // A parsable but malformed record or profile (an e-mail the identity type
                // refuses); the service keeps watching rather than stopping silently.
                LogRepairFailed(exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                LogRepairFailed(exception.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "state file watcher error: {Reason}")]
    private partial void LogWatcherError(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "stale identity repair failed: {Reason}")]
    private partial void LogRepairFailed(string reason);
}
