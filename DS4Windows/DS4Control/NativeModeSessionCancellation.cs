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
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Owns the cancellation source for one Native Mode startup/session.
    /// Stop registration is serialized with startup registration so a stop
    /// cannot miss a startup which has not reached the lifecycle gate yet.
    /// </summary>
    internal sealed class NativeModeSessionCancellation : IDisposable
    {
        private readonly object syncRoot = new object();
        private CancellationTokenSource activeSource;
        private bool stopRequested;

        public CancellationTokenSource Begin(CancellationToken callerToken)
        {
            lock (syncRoot)
            {
                if (stopRequested)
                {
                    throw new InvalidOperationException(
                        "Native mode is stopping; startup was canceled.");
                }

                if (activeSource != null)
                {
                    throw new InvalidOperationException(
                        "A Native Mode startup or session is already active.");
                }

                activeSource = CancellationTokenSource
                    .CreateLinkedTokenSource(callerToken);
                return activeSource;
            }
        }

        public void RequestStop()
        {
            lock (syncRoot)
            {
                stopRequested = true;
                activeSource?.Cancel();
            }
        }

        public void CompleteStop(bool allowFutureStarts)
        {
            if (!allowFutureStarts)
                return;

            lock (syncRoot)
                stopRequested = false;
        }

        public void CompleteSession(CancellationTokenSource expectedSource)
        {
            if (expectedSource == null)
                return;

            bool dispose = false;
            lock (syncRoot)
            {
                if (ReferenceEquals(activeSource, expectedSource))
                {
                    activeSource = null;
                    dispose = true;
                }
            }

            if (dispose)
                expectedSource.Dispose();
        }

        public void CompleteCurrentSession()
        {
            CancellationTokenSource source;
            lock (syncRoot)
            {
                source = activeSource;
                activeSource = null;
            }

            source?.Dispose();
        }

        public void ResetForServiceStart()
        {
            lock (syncRoot)
            {
                // A failed/deferred teardown keeps the active source as an
                // explicit barrier against starting a second session.
                if (activeSource == null)
                    stopRequested = false;
            }
        }

        public void Dispose()
        {
            CancellationTokenSource source;
            lock (syncRoot)
            {
                stopRequested = true;
                source = activeSource;
                activeSource = null;
                source?.Cancel();
            }

            source?.Dispose();
        }
    }
}
