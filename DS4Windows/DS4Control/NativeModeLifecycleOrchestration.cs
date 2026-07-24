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
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows
{
    /// <summary>
    /// Keeps the ordering-sensitive part of native-mode startup explicit and
    /// independently testable. The audio-default listener must exist before
    /// any operation in <paramref name="protectedStartup"/> can enumerate the
    /// virtual USB audio endpoints.
    /// </summary>
    internal static class NativeModeStartupOrchestration
    {
        public static async Task RunWithAudioDefaultProtectionAsync(
            Func<NativeModeAudioDefaultsSnapshot> captureAudioDefaults,
            Action<NativeModeAudioDefaultsSnapshot> beginAudioDefaultGuard,
            Func<Task> protectedStartup)
        {
            if (captureAudioDefaults == null)
                throw new ArgumentNullException(nameof(captureAudioDefaults));
            if (beginAudioDefaultGuard == null)
                throw new ArgumentNullException(nameof(beginAudioDefaultGuard));
            if (protectedStartup == null)
                throw new ArgumentNullException(nameof(protectedStartup));

            NativeModeAudioDefaultsSnapshot snapshot = captureAudioDefaults() ??
                throw new InvalidOperationException(
                    "Native Mode cannot start without an audio-default snapshot.");
            beginAudioDefaultGuard(snapshot);
            await protectedStartup().ConfigureAwait(false);
        }

        public static async Task ObserveUnexpectedTerminationAsync(
            Task<Exception> unexpectedTermination,
            Func<bool> isCurrentSession,
            Func<Exception, Task> markFaulted)
        {
            if (unexpectedTermination == null)
                throw new ArgumentNullException(nameof(unexpectedTermination));
            if (isCurrentSession == null)
                throw new ArgumentNullException(nameof(isCurrentSession));
            if (markFaulted == null)
                throw new ArgumentNullException(nameof(markFaulted));

            Exception failure;
            try
            {
                failure = await unexpectedTermination.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // BeginTeardown cancels the observer by design.
                return;
            }

            if (failure != null && isCurrentSession())
                await markFaulted(failure).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs recovery only after an already-dispatched command reaches a
        /// terminal state. Its eventual success, cancellation, or failure is
        /// deliberately observed rather than propagated: all three permit the
        /// owner to begin ordered teardown, while a still-pending UAC launch
        /// does not.
        /// </summary>
        public static async Task RunAfterCommandTerminationAsync(
            Task commandCompletion, Func<Task> recover)
        {
            if (commandCompletion == null)
                throw new ArgumentNullException(nameof(commandCompletion));
            if (recover == null)
                throw new ArgumentNullException(nameof(recover));

            try
            {
                await commandCompletion.ConfigureAwait(false);
            }
            catch
            {
                // The command is terminal; its outcome is reported by the
                // original startup failure. Recovery still has to run.
            }

            await recover().ConfigureAwait(false);
        }
    }

    internal sealed class NativeModeTeardownAttempt
    {
        public NativeModeTeardownAttempt(bool released, Exception failure,
            string deferredReason)
        {
            Released = released;
            Failure = failure;
            DeferredReason = deferredReason;
        }

        public bool Released { get; }
        public Exception Failure { get; }
        public string DeferredReason { get; }
    }

    /// <summary>
    /// Encodes the fail-closed native-mode teardown rule. Releasing the audio
    /// guard, controller suppression, or rescanning is safe only after the
    /// server is no longer owned, the exact virtual child is absent, and the
    /// render keepalive has confirmed its tracked endpoint is absent too.
    /// </summary>
    internal static class NativeModeTeardownOrchestration
    {
        public static async Task<NativeModeTeardownAttempt> StopAndTryReleaseAsync(
            Action beginRenderTeardown,
            Func<Task> stopServer,
            Func<bool> ownsServerProcess,
            Func<Task> completeRenderTeardown,
            Func<bool> isExactVirtualChildPresent,
            Func<bool> hasRenderKeepaliveSession,
            Action releaseProtections)
        {
            if (beginRenderTeardown == null)
                throw new ArgumentNullException(nameof(beginRenderTeardown));
            if (stopServer == null)
                throw new ArgumentNullException(nameof(stopServer));
            if (ownsServerProcess == null)
                throw new ArgumentNullException(nameof(ownsServerProcess));
            if (completeRenderTeardown == null)
                throw new ArgumentNullException(nameof(completeRenderTeardown));
            if (isExactVirtualChildPresent == null)
                throw new ArgumentNullException(nameof(isExactVirtualChildPresent));
            if (hasRenderKeepaliveSession == null)
                throw new ArgumentNullException(nameof(hasRenderKeepaliveSession));
            if (releaseProtections == null)
                throw new ArgumentNullException(nameof(releaseProtections));

            beginRenderTeardown();
            Exception failure = null;
            try
            {
                await stopServer().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            try
            {
                await completeRenderTeardown().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }

            // A failed stop/confirmation must be retried by the owner. Even if
            // a later probe happens to look clear, the failed operation did not
            // establish a trustworthy teardown boundary.
            if (failure != null)
            {
                return new NativeModeTeardownAttempt(false, failure,
                    "native-mode stop or removal confirmation failed");
            }

            return TryReleaseIfConfirmed(ownsServerProcess,
                isExactVirtualChildPresent, hasRenderKeepaliveSession,
                releaseProtections);
        }

        public static NativeModeTeardownAttempt TryReleaseIfConfirmed(
            Func<bool> ownsServerProcess,
            Func<bool> isExactVirtualChildPresent,
            Func<bool> hasRenderKeepaliveSession,
            Action releaseProtections)
        {
            if (ownsServerProcess == null)
                throw new ArgumentNullException(nameof(ownsServerProcess));
            if (isExactVirtualChildPresent == null)
                throw new ArgumentNullException(nameof(isExactVirtualChildPresent));
            if (hasRenderKeepaliveSession == null)
                throw new ArgumentNullException(nameof(hasRenderKeepaliveSession));
            if (releaseProtections == null)
                throw new ArgumentNullException(nameof(releaseProtections));

            var pending = new List<string>();
            try
            {
                if (ownsServerProcess())
                    pending.Add("server process ownership remains");
            }
            catch (Exception ex)
            {
                return ProbeFailed("server process ownership", ex);
            }

            try
            {
                if (isExactVirtualChildPresent())
                    pending.Add("the exact virtual child is still present");
            }
            catch (Exception ex)
            {
                return ProbeFailed("exact virtual-child removal", ex);
            }

            try
            {
                if (hasRenderKeepaliveSession())
                    pending.Add("the virtual audio endpoint is still present");
            }
            catch (Exception ex)
            {
                return ProbeFailed("virtual audio-endpoint removal", ex);
            }

            if (pending.Count != 0)
            {
                return new NativeModeTeardownAttempt(false, null,
                    string.Join("; ", pending));
            }

            releaseProtections();
            return new NativeModeTeardownAttempt(true, null, null);
        }

        /// <summary>
        /// Runs a bounded deferred-removal poll. The caller supplies a probe
        /// which serializes with the owning lifecycle gate and releases exactly
        /// once when every removal signal is confirmed.
        /// </summary>
        public static async Task<bool> WaitForConfirmedReleaseAsync(
            Func<Task<bool>> tryRelease,
            Func<TimeSpan, CancellationToken, Task> delay,
            int maximumAttempts, TimeSpan pollInterval,
            CancellationToken cancellationToken)
        {
            if (tryRelease == null)
                throw new ArgumentNullException(nameof(tryRelease));
            if (delay == null)
                throw new ArgumentNullException(nameof(delay));
            if (maximumAttempts <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
            if (pollInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(pollInterval));

            for (int attempt = 0; attempt < maximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await tryRelease().ConfigureAwait(false))
                    return true;
                if (attempt + 1 < maximumAttempts)
                {
                    await delay(pollInterval, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return false;
        }

        private static NativeModeTeardownAttempt ProbeFailed(string probe,
            Exception exception) => new NativeModeTeardownAttempt(false, null,
                $"could not confirm {probe}: {exception.Message}");
    }
}
