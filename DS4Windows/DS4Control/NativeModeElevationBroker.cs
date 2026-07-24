/*
DS4Windows
Copyright (C) 2026  DS4Windows contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows
{
    public enum NativeModeAttachFailureKind
    {
        None,
        UsbipMissing,
        InvalidUsbipPath,
        SetupRequired,
        LegacyTaskCleanupFailed,
        CommandFailed,
        DeviceArrivalTimeout,
        DriverValidationFailed,
    }

    public sealed class NativeModeAttachResult
    {
        private NativeModeAttachResult(bool success,
            NativeModeAttachFailureKind failureKind, string reason,
            Task pendingCommandCompletion = null)
        {
            Success = success;
            FailureKind = failureKind;
            Reason = reason;
            PendingCommandCompletion = pendingCommandCompletion;
        }

        public bool Success { get; }
        public NativeModeAttachFailureKind FailureKind { get; }
        public string Reason { get; }

        /// <summary>
        /// When non-null, an elevated command was already handed to Windows
        /// but had not reached a terminal state when this result was returned.
        /// Native Mode must retain its helper and teardown protections until
        /// this task completes, because consent can still be granted late.
        /// </summary>
        internal Task PendingCommandCompletion { get; }

        public static NativeModeAttachResult Succeeded(string reason) =>
            new NativeModeAttachResult(true, NativeModeAttachFailureKind.None, reason);

        public static NativeModeAttachResult Failed(
            NativeModeAttachFailureKind kind, string reason,
            Task pendingCommandCompletion = null) =>
            new NativeModeAttachResult(false, kind, reason,
                pendingCommandCompletion);
    }

    internal sealed class NativeModeCommandExecution
    {
        public NativeModeCommandExecution(NativeModeCommandResult result,
            Task pendingCompletion = null)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
            PendingCompletion = pendingCompletion;
        }

        public NativeModeCommandResult Result { get; }
        public Task PendingCompletion { get; }
    }

    internal sealed class NativeModeCommandResult
    {
        public NativeModeCommandResult(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            Output = output ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public int ExitCode { get; }
        public string Output { get; }
        public string Error { get; }
    }

    internal interface INativeModeCommandRunner
    {
        Task<NativeModeCommandResult> RunAsync(string executable,
            IReadOnlyList<string> arguments, bool elevate,
            CancellationToken cancellationToken);
    }

    internal sealed class NativeModeProcessRunner : INativeModeCommandRunner
    {
        public async Task<NativeModeCommandResult> RunAsync(string executable,
            IReadOnlyList<string> arguments, bool elevate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // ShellExecute can remain inside the UAC broker until the user
                // answers the prompt. Dispatch both process creation and Start
                // explicitly so this call can never block a captured UI context.
                Process process = await Task.Run(
                    () => CreateAndStartProcess(executable, arguments, elevate),
                    CancellationToken.None).ConfigureAwait(false);
                if (process == null)
                {
                    return new NativeModeCommandResult(-1, string.Empty,
                        $"Could not start {Path.GetFileName(executable)}.");
                }

                using (process)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        Task<string> outputTask = elevate
                            ? Task.FromResult(string.Empty)
                            : process.StandardOutput.ReadToEndAsync();
                        Task<string> errorTask = elevate
                            ? Task.FromResult(string.Empty)
                            : process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync(cancellationToken)
                            .ConfigureAwait(false);
                        return new NativeModeCommandResult(process.ExitCode,
                            await outputTask.ConfigureAwait(false),
                            await errorTask.ConfigureAwait(false));
                    }
                    catch (OperationCanceledException)
                    {
                        // This is the short-lived usbip/schtasks command client,
                        // never the attached Native Mode helper process.
                        TryTerminate(process);
                        // Do not complete this runner until the elevated
                        // client is definitely gone. A failed termination can
                        // otherwise leave a late usbip attach racing cleanup.
                        await WaitForConfirmedExitAsync(process)
                            .ConfigureAwait(false);
                        throw;
                    }
                    catch
                    {
                        // No error after Process.Start is allowed to make the
                        // command look terminal while its process might live.
                        TryTerminate(process);
                        await WaitForConfirmedExitAsync(process)
                            .ConfigureAwait(false);
                        throw;
                    }
                }
            }
            catch (Win32Exception ex)
            {
                string reason = ex.NativeErrorCode == 1223
                    ? "The elevation prompt was canceled."
                    : ex.Message;
                return new NativeModeCommandResult(ex.NativeErrorCode,
                    string.Empty, reason);
            }
        }

        private static Process CreateAndStartProcess(string executable,
            IReadOnlyList<string> arguments, bool elevate)
        {
            Process process = CreateProcess(executable, arguments, elevate);
            try
            {
                if (process.Start())
                    return process;
                process.Dispose();
                return null;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        private static void TryTerminate(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException ||
                ex is Win32Exception || ex is NotSupportedException)
            {
                // The command may have exited between the probe and Kill, or
                // an elevated process handle may not grant termination rights.
            }
        }

        private static async Task WaitForConfirmedExitAsync(Process process)
        {
            while (true)
            {
                try
                {
                    if (process.HasExited)
                        return;

                    await process.WaitForExitAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException ||
                    ex is Win32Exception || ex is NotSupportedException)
                {
                    // Losing query or wait rights is not proof of exit. Keep
                    // the completion barrier pending and retry fail-closed.
                    await Task.Delay(TimeSpan.FromMilliseconds(250))
                        .ConfigureAwait(false);
                }
            }
        }

        private static Process CreateProcess(string executable,
            IReadOnlyList<string> arguments, bool elevate)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = elevate,
                CreateNoWindow = !elevate,
                WindowStyle = elevate ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                RedirectStandardOutput = !elevate,
                RedirectStandardError = !elevate,
                Verb = elevate ? "runas" : string.Empty,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            return new Process { StartInfo = startInfo };
        }
    }

    public sealed class NativeModeElevationBroker
    {
        private const string UsbipExecutableName = "usbip.exe";
        internal const string LegacyAttachTaskName =
            @"DS4Windows\NativeDualSenseAttach";
        private static readonly TimeSpan DefaultArrivalTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ArrivalPollInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

        private readonly Func<string, bool> fileExists;
        private readonly Func<IReadOnlyList<string>> trustedProgramFilesRoots;
        private readonly Func<bool> legacyAttachTaskPresent;
        private readonly Func<bool> isAdministrator;
        private readonly INativeModeCommandRunner commandRunner;
        private readonly Func<bool> virtualDevicePresent;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly string taskSchedulerExecutable;
        private readonly TimeSpan commandTimeout;
        private readonly NativeModeDriverGate driverGate;

        public NativeModeElevationBroker() : this((NativeModeDriverGate)null)
        {
        }

        /// <summary>
        /// Production constructor. The driver gate re-validates the usbip-win2
        /// packages before this broker requests elevation, so a direct broker
        /// caller cannot elevate on an unvalidated driver set (policy §4).
        /// </summary>
        public NativeModeElevationBroker(NativeModeDriverGate driverGate)
            : this(File.Exists,
                GetTrustedProgramFilesRoots, IsLegacyAttachTaskPresent,
                Global.IsAdministrator, new NativeModeProcessRunner(),
                NativeModeDevicePresence.IsVirtualDualSensePresent,
                Task.Delay, Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.System), "schtasks.exe"),
                commandTimeout: null, driverGate: driverGate)
        {
        }

        internal NativeModeElevationBroker(Func<string, bool> fileExists,
            Func<IReadOnlyList<string>> trustedProgramFilesRoots,
            Func<bool> legacyAttachTaskPresent, Func<bool> isAdministrator,
            INativeModeCommandRunner commandRunner,
            Func<bool> virtualDevicePresent,
            Func<TimeSpan, CancellationToken, Task> delay,
            string taskSchedulerExecutable, TimeSpan? commandTimeout = null,
            NativeModeDriverGate driverGate = null)
        {
            this.driverGate = driverGate;
            this.fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
            this.trustedProgramFilesRoots = trustedProgramFilesRoots ??
                throw new ArgumentNullException(nameof(trustedProgramFilesRoots));
            this.legacyAttachTaskPresent = legacyAttachTaskPresent ??
                throw new ArgumentNullException(nameof(legacyAttachTaskPresent));
            this.isAdministrator = isAdministrator ??
                throw new ArgumentNullException(nameof(isAdministrator));
            this.commandRunner = commandRunner ??
                throw new ArgumentNullException(nameof(commandRunner));
            this.virtualDevicePresent = virtualDevicePresent ??
                throw new ArgumentNullException(nameof(virtualDevicePresent));
            this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
            this.taskSchedulerExecutable = taskSchedulerExecutable ??
                throw new ArgumentNullException(nameof(taskSchedulerExecutable));
            this.commandTimeout = commandTimeout ?? DefaultCommandTimeout;
            if (this.commandTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        }

        public bool IsUsbipInstalled(string usbipPath) =>
            ValidateUsbipExecutable(usbipPath, out _) == null;

        /// <summary>
        /// Retains the former setup API as a prerequisite check and removes the
        /// fixed legacy elevated task if an earlier build installed it. No task,
        /// service, helper, or other reusable privileged artifact is installed.
        /// Each attach receives a fresh UAC decision instead.
        /// </summary>
        public async Task<NativeModeAttachResult> EnsureAttachTaskAsync(
            string usbipPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NativeModeAttachResult cleanup =
                await EnsureLegacyAttachTaskRemovedAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!cleanup.Success)
                return cleanup;

            NativeModeAttachResult validation =
                ValidateUsbipExecutable(usbipPath, out _);
            return validation ??
                NativeModeAttachResult.Succeeded(
                    "Native mode is ready and no legacy elevated attach task " +
                    "remains. Windows will request administrator " +
                    "approval for the fixed usbip.exe attach action each time " +
                    "Native Mode starts.");
        }

        public async Task<NativeModeAttachResult> RunAttachAsync(string usbipPath,
            CancellationToken cancellationToken = default)
        {
            // Fail closed before any elevation: neither the legacy task cleanup
            // (which elevates) nor the attach may proceed on an unvalidated
            // usbip-win2 driver set.
            NativeModeAttachResult driverValidation =
                driverGate?.ValidateBeforeElevation(usbipPath);
            if (driverValidation != null)
                return driverValidation;

            NativeModeAttachResult cleanup =
                await EnsureLegacyAttachTaskRemovedAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!cleanup.Success)
                return cleanup;

            NativeModeAttachResult validation =
                ValidateUsbipExecutable(usbipPath, out string executable);
            if (validation != null)
                return validation;

            IReadOnlyList<string> arguments = BuildDirectAttachArguments();
            bool elevate = !isAdministrator();

            NativeModeCommandExecution command = await RunCommandAsync(
                executable, arguments, elevate, cancellationToken)
                .ConfigureAwait(false);
            NativeModeCommandResult commandResult = command.Result;
            if (commandResult.ExitCode != 0)
            {
                return NativeModeAttachResult.Failed(
                    NativeModeAttachFailureKind.CommandFailed,
                    CommandFailure("USB/IP attach could not be started",
                        commandResult),
                    command.PendingCompletion);
            }

            bool arrived;
            try
            {
                arrived = await WaitForDeviceArrivalAsync(virtualDevicePresent,
                    delay, DefaultArrivalTimeout, ArrivalPollInterval,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Win32Exception ||
                ex is UnauthorizedAccessException)
            {
                return NativeModeAttachResult.Failed(
                    NativeModeAttachFailureKind.DeviceArrivalTimeout,
                    "Could not query present devices while confirming the " +
                    $"virtual DualSense attachment: {ex.Message}");
            }
            return arrived
                ? NativeModeAttachResult.Succeeded("Virtual DualSense attached.")
                : NativeModeAttachResult.Failed(
                    NativeModeAttachFailureKind.DeviceArrivalTimeout,
                    "Virtual DualSense did not appear within 10 seconds; check the native server log and USB/IP driver state.");
        }

        internal static string[] BuildDirectAttachArguments() =>
            new[]
            {
                "attach", "-r", "127.0.0.1", "-b", "1-1",
                "--serial", NativeModeDevicePresence.VirtualDualSenseSerial,
                "--once",
            };

        internal static string[] BuildLegacyTaskDeleteArguments() =>
            new[] { "/Delete", "/TN", LegacyAttachTaskName, "/F" };

        internal static async Task<bool> WaitForDeviceArrivalAsync(
            Func<bool> devicePresent,
            Func<TimeSpan, CancellationToken, Task> delay,
            TimeSpan timeout, TimeSpan pollInterval,
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            do
            {
                if (devicePresent())
                    return true;
                if (DateTimeOffset.UtcNow >= deadline)
                    return false;
                await delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (true);
        }

        private async Task<NativeModeAttachResult>
            EnsureLegacyAttachTaskRemovedAsync(
                CancellationToken cancellationToken)
        {
            bool taskPresent;
            try
            {
                taskPresent = legacyAttachTaskPresent();
            }
            catch (Exception ex)
            {
                return LegacyTaskCleanupFailure(
                    "Could not check for the legacy elevated Native Mode " +
                    $"attach task: {ex.Message}");
            }

            if (!taskPresent)
            {
                return NativeModeAttachResult.Succeeded(
                    "No legacy elevated Native Mode attach task is installed.");
            }

            NativeModeCommandExecution deleteCommand =
                await RunCommandAsync(taskSchedulerExecutable,
                    BuildLegacyTaskDeleteArguments(), elevate: true,
                    cancellationToken).ConfigureAwait(false);
            NativeModeCommandResult deleteResult = deleteCommand.Result;
            if (deleteResult.ExitCode != 0)
            {
                return NativeModeAttachResult.Failed(
                    NativeModeAttachFailureKind.LegacyTaskCleanupFailed,
                    CommandFailure(
                        "The legacy elevated Native Mode attach task must be " +
                        "removed before Native Mode can start",
                        deleteResult),
                    deleteCommand.PendingCompletion);
            }

            try
            {
                if (legacyAttachTaskPresent())
                {
                    return LegacyTaskCleanupFailure(
                        "The legacy elevated Native Mode attach task still " +
                        "exists after Windows reported successful removal. " +
                        "Native Mode is blocked.");
                }
            }
            catch (Exception ex)
            {
                return LegacyTaskCleanupFailure(
                    "Windows reported that the legacy elevated Native Mode " +
                    "attach task was removed, but its absence could not be " +
                    $"confirmed: {ex.Message}");
            }

            return NativeModeAttachResult.Succeeded(
                "The legacy elevated Native Mode attach task was removed.");
        }

        private async Task<NativeModeCommandExecution> RunCommandAsync(
            string executable, IReadOnlyList<string> arguments, bool elevate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(commandTimeout);

            Task<NativeModeCommandResult> commandTask = commandRunner.RunAsync(
                executable, arguments, elevate, timeout.Token);
            try
            {
                NativeModeCommandResult result = await commandTask.WaitAsync(
                    commandTimeout, cancellationToken).ConfigureAwait(false);
                return new NativeModeCommandExecution(result);
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                ObserveAbandonedCommand(commandTask);
                return new NativeModeCommandExecution(
                    CommandTimedOutResult(), commandTask);
            }
            catch (OperationCanceledException)
            {
                timeout.Cancel();
                ObserveAbandonedCommand(commandTask);
                if (cancellationToken.IsCancellationRequested)
                {
                    return new NativeModeCommandExecution(
                        CommandCanceledResult(), commandTask);
                }
                return new NativeModeCommandExecution(
                    CommandTimedOutResult(), commandTask);
            }
        }

        private static NativeModeCommandResult CommandCanceledResult() =>
            new NativeModeCommandResult(-1, string.Empty,
                "The elevated action was canceled; cleanup is waiting for " +
                "Windows to confirm that the command has stopped.");

        private NativeModeCommandResult CommandTimedOutResult()
        {
            string duration = commandTimeout.TotalSeconds >= 1
                ? $"{commandTimeout.TotalSeconds:0.#} seconds"
                : $"{commandTimeout.TotalMilliseconds:0} milliseconds";
            return new NativeModeCommandResult(-1, string.Empty,
                $"The elevated action did not finish within {duration}.");
        }

        private static void ObserveAbandonedCommand(
            Task<NativeModeCommandResult> commandTask)
        {
            _ = commandTask.ContinueWith(task =>
                {
                    _ = task.Exception;
                }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static IReadOnlyList<string> GetTrustedProgramFilesRoots() =>
            new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        private static bool IsLegacyAttachTaskPresent()
        {
            using var taskService =
                new Microsoft.Win32.TaskScheduler.TaskService();
            return taskService.GetTask(@"\" + LegacyAttachTaskName) != null;
        }

        private NativeModeAttachResult ValidateUsbipExecutable(
            string usbipPath, out string executable)
        {
            executable = null;
            if (string.IsNullOrWhiteSpace(usbipPath))
                return MissingUsbipResult(usbipPath);

            string candidate = usbipPath.Trim();
            if (!Path.IsPathFullyQualified(candidate) ||
                candidate.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return InvalidUsbipResult(
                    "UsbipExePath must be a fully qualified local path.");
            }

            try
            {
                executable = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException ||
                ex is NotSupportedException || ex is PathTooLongException ||
                ex is IOException)
            {
                executable = null;
                return InvalidUsbipResult(
                    $"UsbipExePath is invalid: {ex.Message}");
            }

            if (!string.Equals(candidate, executable,
                StringComparison.OrdinalIgnoreCase))
            {
                executable = null;
                return InvalidUsbipResult(
                    "UsbipExePath must already be a canonical local path " +
                    "without relative segments.");
            }

            if (!string.Equals(Path.GetFileName(executable),
                UsbipExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                executable = null;
                return InvalidUsbipResult(
                    "UsbipExePath must point to a local executable named usbip.exe.");
            }

            IReadOnlyList<string> trustedRoots;
            try
            {
                trustedRoots = trustedProgramFilesRoots() ??
                    Array.Empty<string>();
            }
            catch (Exception ex)
            {
                executable = null;
                return InvalidUsbipResult(
                    $"Could not determine trusted Program Files roots: {ex.Message}");
            }

            string validatedExecutable = executable;
            if (!trustedRoots.Any(root => IsPathWithinTrustedRoot(
                validatedExecutable, root)))
            {
                executable = null;
                return InvalidUsbipResult(
                    "UsbipExePath must be a canonical path under a trusted " +
                    "Windows Program Files directory.");
            }

            bool exists;
            try
            {
                exists = fileExists(executable);
            }
            catch (Exception ex) when (ex is IOException ||
                ex is UnauthorizedAccessException)
            {
                executable = null;
                return InvalidUsbipResult(
                    $"Could not verify UsbipExePath: {ex.Message}");
            }

            if (!exists)
            {
                string missingPath = executable;
                executable = null;
                return MissingUsbipResult(missingPath);
            }

            return null;
        }

        private static bool IsPathWithinTrustedRoot(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return false;

            string candidateRoot = root.Trim();
            if (!Path.IsPathFullyQualified(candidateRoot) ||
                candidateRoot.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                string canonicalRoot = Path.GetFullPath(candidateRoot)
                    .TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                if (!string.Equals(candidateRoot.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar), canonicalRoot,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return path.StartsWith(canonicalRoot +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException ||
                ex is NotSupportedException || ex is PathTooLongException ||
                ex is IOException)
            {
                return false;
            }
        }

        private static NativeModeAttachResult MissingUsbipResult(string path) =>
            NativeModeAttachResult.Failed(NativeModeAttachFailureKind.UsbipMissing,
                $"usbip.exe was not found at '{path}'. Install the supported " +
                "usbip-win2 userspace client under Program Files as described " +
                "in doc/dev/native_dualsense_mode.md, or correct UsbipExePath.");

        private static NativeModeAttachResult InvalidUsbipResult(string reason) =>
            NativeModeAttachResult.Failed(
                NativeModeAttachFailureKind.InvalidUsbipPath, reason);

        private static NativeModeAttachResult LegacyTaskCleanupFailure(
            string reason) => NativeModeAttachResult.Failed(
                NativeModeAttachFailureKind.LegacyTaskCleanupFailed, reason);

        private static string CommandFailure(string prefix,
            NativeModeCommandResult result)
        {
            string detail = string.IsNullOrWhiteSpace(result.Error)
                ? result.Output.Trim()
                : result.Error.Trim();
            return string.IsNullOrWhiteSpace(detail)
                ? $"{prefix} (exit code {result.ExitCode})."
                : $"{prefix} (exit code {result.ExitCode}): {detail}";
        }
    }

    internal static class NativeModeDevicePresence
    {
        internal const string VirtualDualSenseSerial = "DS4WSPKCOMP001";
        internal const string VirtualDualSenseParentInstanceId =
            @"USB\VID_054C&PID_0CE6\DS4WSPKCOMP001";
        private const string DualSenseUsbInstancePrefix =
            @"USB\VID_054C&PID_0CE6";
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorNoMoreItems = 259;
        internal const int PresentAllClassesFlags =
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_ALLCLASSES;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        // Cleanup callers deliberately receive probe failures. Treating a failed
        // query as "absent" could release the retained render pin while a stale
        // or still-removing composite child exists. The attach broker catches
        // expected SetupAPI failures and returns a normal attach failure instead.
        public static bool IsVirtualDualSensePresent() =>
            GetPresentVirtualDualSenseInstanceIds().Count != 0;

        internal static IReadOnlyList<string>
            GetPresentVirtualDualSenseInstanceIds() =>
            GetPresentVirtualDualSenseInstanceIds(
                EnumeratePresentDeviceInstanceIds);

        internal static IReadOnlyList<string>
            GetPresentVirtualDualSenseInstanceIds(
                Func<int, IReadOnlyList<string>> enumerateDeviceInstanceIds)
        {
            if (enumerateDeviceInstanceIds == null)
            {
                throw new ArgumentNullException(
                    nameof(enumerateDeviceInstanceIds));
            }

            return (enumerateDeviceInstanceIds(PresentAllClassesFlags) ??
                    Array.Empty<string>())
                .Where(IsVirtualDualSenseParentInstanceId)
                .ToArray();
        }

        private static IReadOnlyList<string>
            EnumeratePresentDeviceInstanceIds(int flags)
        {
            IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevs(
                IntPtr.Zero, null, 0, flags);
            if (deviceInfoSet == InvalidHandleValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "SetupDiGetClassDevs could not enumerate present devices.");
            }

            var instanceIds = new List<string>();
            try
            {
                for (int index = 0; ; index++)
                {
                    var deviceInfo = new NativeMethods.SP_DEVINFO_DATA
                    {
                        cbSize = Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>(),
                    };
                    if (!NativeMethods.SetupDiEnumDeviceInfo(deviceInfoSet,
                        index, ref deviceInfo))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == ErrorNoMoreItems)
                            break;
                        throw new Win32Exception(error,
                            "SetupDiEnumDeviceInfo could not enumerate a present device.");
                    }

                    instanceIds.Add(GetDeviceInstanceId(deviceInfoSet,
                        ref deviceInfo));
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return instanceIds;
        }

        private static string GetDeviceInstanceId(IntPtr deviceInfoSet,
            ref NativeMethods.SP_DEVINFO_DATA deviceInfo)
        {
            int requiredSize = 0;
            if (!NativeMethods.SetupDiGetDeviceInstanceId(deviceInfoSet,
                ref deviceInfo, null, 0, ref requiredSize))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorInsufficientBuffer)
                {
                    throw new Win32Exception(error,
                        "SetupDiGetDeviceInstanceId could not size an instance ID.");
                }
            }

            if (requiredSize <= 1)
            {
                throw new Win32Exception(
                    "SetupDiGetDeviceInstanceId returned an empty instance ID.");
            }

            var instanceId = new StringBuilder(requiredSize);
            if (!NativeMethods.SetupDiGetDeviceInstanceId(deviceInfoSet,
                ref deviceInfo, instanceId, instanceId.Capacity,
                ref requiredSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "SetupDiGetDeviceInstanceId could not read an instance ID.");
            }

            return instanceId.ToString();
        }

        internal static bool IsVirtualDualSenseParentInstanceId(
            string instanceId) =>
            string.Equals(instanceId, VirtualDualSenseParentInstanceId,
                StringComparison.OrdinalIgnoreCase);

        internal static bool IsVirtualDualSenseInstanceOrDescendant(
            string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId) ||
                !instanceId.StartsWith(DualSenseUsbInstancePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int separator = instanceId.LastIndexOf('\\');
            if (separator < 0 || separator == instanceId.Length - 1)
                return false;

            string instanceSuffix = instanceId.Substring(separator + 1);
            return string.Equals(instanceSuffix, VirtualDualSenseSerial,
                    StringComparison.OrdinalIgnoreCase) ||
                instanceSuffix.StartsWith(VirtualDualSenseSerial + "&",
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static Guid? TryGetContainerId(string deviceInstanceId)
        {
            if (string.IsNullOrWhiteSpace(deviceInstanceId))
                return null;

            NativeMethods.SP_DEVINFO_DATA deviceInfo =
                new NativeMethods.SP_DEVINFO_DATA
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf(
                        typeof(NativeMethods.SP_DEVINFO_DATA))
                };
            IntPtr deviceInfoSet = NativeMethods.SetupDiCreateDeviceInfoList(
                IntPtr.Zero, 0);
            if (deviceInfoSet == new IntPtr(-1))
                return null;

            try
            {
                if (!NativeMethods.SetupDiOpenDeviceInfo(deviceInfoSet,
                    deviceInstanceId, IntPtr.Zero, 0, ref deviceInfo))
                {
                    return null;
                }

                ulong propertyType = 0;
                int requiredSize = 0;
                byte[] data = new byte[16];
                NativeMethods.DEVPROPKEY key =
                    NativeMethods.DEVPKEY_Device_ContainerId;
                if (!NativeMethods.SetupDiGetDeviceProperty(deviceInfoSet,
                    ref deviceInfo, ref key, ref propertyType, data,
                    data.Length, ref requiredSize, 0) || requiredSize != 16)
                {
                    return null;
                }

                return new Guid(data);
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }
    }
}
