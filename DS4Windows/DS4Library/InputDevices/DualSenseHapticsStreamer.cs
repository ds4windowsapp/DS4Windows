/*
DS4Windows
Copyright (C) 2026 DS4Windows contributors

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
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Dmo;
using NAudio.Wave;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Streams haptic and listening audio to a Bluetooth-connected DualSense.
    ///
    /// Haptics-only mode uses the self-contained HID output report 0x36 carrying
    /// controller state plus 3 kHz stereo PCM for the voice-coil actuators. When
    /// listening audio is enabled, the same container also carries one
    /// Opus-encoded 48 kHz stereo frame (10 ms, 160 kbps CBR) routed to the
    /// controller's headphone jack or internal speaker.
    ///
    /// An integral rate servo trims the capture resampler onto the pad's real
    /// consumption clock, stalls skip slots instead of burst-catching-up, and
    /// the stream is silence-gated so an idle source stops consuming Bluetooth
    /// airtime. Filler frames are encoded through the live Opus encoder so its
    /// state remains coherent across underruns.
    ///
    /// Protocol research credit: egormanga/SAxense (haptics stream),
    /// awalol/DS5Dongle, and loteran/DS5Dongle AutoHaptics (audio container,
    /// controller state, Opus parameters, and routing).
    /// </summary>
    internal sealed class DualSenseHapticsStreamer
    {
        // 0x36 self-contained haptics report (state + signed 3 kHz PCM)
        private const int HAPTICS_REPORT_SIZE = 398;
        private const byte HAPTICS_REPORT_ID = 0x36;

        // Packetized SetStateData command used to initialize the listening-audio amp.
        private const int STATE_SETUP_REPORT_SIZE = 142;
        private const byte STATE_SETUP_REPORT_ID = 0x32;

        // 0x36 haptics + one listening-audio frame
        private const int AUDIO_REPORT_SIZE = 398;
        private const byte AUDIO_REPORT_ID = 0x36;
        private const int OPUS_FRAME_BYTES = 200;      // CBR: 160 kbps * 10 ms / 8
        private const int OPUS_SAMPLES_PER_FRAME = 480; // one frame per report, per channel
        private const int FRAME_SHORTS = OPUS_SAMPLES_PER_FRAME * 2; // interleaved stereo
        private const int AUDIO_SAMPLE_RATE = 48000;   // Opus codec rate

        // The controller consumes one 480-sample Opus frame per ~10.667 ms haptic
        // slot (audio is slaved to the 3 kHz haptics clock), so audio must be
        // delivered at 480 / 10.667 ms = 45000 samples/s or the stream overruns
        // and drops frames audibly. Same reason DS5Dongle resamples 512->480.
        private const int AUDIO_DELIVERY_RATE = 45000;

        // The PC capture clock and this loop's Stopwatch cadence are different
        // crystals; without correction their drift slowly walks the frame
        // backlog into an underrun or an overflow drop no matter how deep the
        // buffers are. An integral servo trims the capture resampler's output
        // rate using the backlog level as the error signal. Hardware validation
        // showed the queue parking on target without dry-outs. Gain is per
        // frame of error per tick; authority is limited to 2.5 percent.
        internal const double AUDIO_RATE_TRIM_GAIN = 0.00003;
        internal const double AUDIO_RATE_TRIM_LIMIT = 0.025;

        // Source-energy gate: the stream only runs while the capture source
        // carried real energy recently (or haptics are active). Windows keeps
        // idle render pins primed with silence indefinitely; streaming that
        // costs airtime and pad battery for nothing. ~0.005 is about -46 dBFS.
        private const double AUDIO_ENERGY_THRESHOLD = 0.005;
        private const double SILENCE_GATE_MS = 2000.0;
        internal const int SILENCE_TAIL_REPORTS = 6;

        // ~5 ms ramp applied to the first content frame after a rebuffer or a
        // backlog drop so the resume edge is inaudible.
        internal const int RESUME_FADE_FRAMES = 240;

        // After an underrun the local prebuffer target escalates one frame
        // (bounded by ring headroom); after this many clean ticks (~3 min) it
        // decays one frame back toward the profile's base. Users get the
        // latency they asked for on clean links and stability on bad ones.
        private const long ADAPTIVE_DECAY_TICKS = 16875;

        /// <summary>
        /// One coherent set of buffer sizes for the whole pipeline. Bigger
        /// buffers survive congested links (2.4 GHz Wi-Fi, wireless headset
        /// dongles); smaller buffers cut end-to-end delay for game audio.
        /// </summary>
        internal readonly struct LatencyProfile
        {
            public readonly byte ControllerBuffer;   // controller-side dejitter buffer [16,127]
            public readonly int PrebufferFrames;     // frames banked before playback starts
            public readonly double MaxCatchupMs;     // haptics-only burst catch-up window
            public readonly int AudioRingSamples;    // capture ring feeding the encoder
            public readonly int HapticsRingBytes;    // capture ring feeding the actuators
            public readonly int HapticsPrebufferBytes;

            public LatencyProfile(byte controllerBuffer, int prebufferFrames,
                double maxCatchupMs, int audioRingSamples, int hapticsRingBytes,
                int hapticsPrebufferBytes)
            {
                ControllerBuffer = controllerBuffer;
                PrebufferFrames = prebufferFrames;
                MaxCatchupMs = maxCatchupMs;
                AudioRingSamples = audioRingSamples;
                HapticsRingBytes = hapticsRingBytes;
                HapticsPrebufferBytes = hapticsPrebufferBytes;
            }
        }

        internal static LatencyProfile GetLatencyProfile(DualSenseControllerOptions.AudioLatencyMode latencyMode)
        {
            switch (latencyMode)
            {
                case DualSenseControllerOptions.AudioLatencyMode.LowLatency:
                    // ~80-120 ms end to end; needs a clean link
                    return new LatencyProfile(32, 2, 100.0, FRAME_SHORTS * 12, 960, 192);
                case DualSenseControllerOptions.AudioLatencyMode.Balanced:
                    // ~150-250 ms
                    return new LatencyProfile(64, 3, 150.0, FRAME_SHORTS * 16, 1440, 384);
                case DualSenseControllerOptions.AudioLatencyMode.Smooth:
                default:
                    // ~300-400 ms; proven on congested 2.4 GHz environments
                    return new LatencyProfile(120, 4, 250.0, FRAME_SHORTS * 20, 1920, 576);
            }
        }

        private const int SAMPLE_RATE = 3000;          // haptic PCM rate per channel
        private const int HAPTIC_CHUNK_BYTES = 64;     // 32 stereo frames
        private const double TICK_MS = 32 * 1000.0 / SAMPLE_RATE; // ~10.667 ms
        private const int MAX_CONSECUTIVE_WRITE_FAILURES = 50;

        // Known-good advanced-haptics state used by DS5Dongle-AutoHaptics.
        // UseRumbleNotHaptics (state byte 0 bit 1) is clear.
        private static readonly byte[] HAPTICS_STATE = new byte[63]
        {
            0xFD, 0xF7, 0x00, 0x00,
            0x7F, 0x64, 0xFF, 0x09, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A,
            0x07, 0x00, 0x00, 0x02, 0x01, 0x00, 0xFF, 0xD7, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };

        private const double HEAVY_FREQ_HZ = 62.0;
        private const double LIGHT_FREQ_HZ = 170.0;
        private const double ENVELOPE_ATTACK = 0.35;
        private const double ENVELOPE_RELEASE = 0.015;

        // Voice-coil haptics can reproduce rumble values that are too weak to
        // move a conventional eccentric motor. Some games continuously emit
        // those tiny values during stick movement, turning them into an
        // unintended buzz. Match the effective noise floor of a normal motor,
        // then rescale the remaining range so full-strength rumble stays full.
        internal const byte RUMBLE_SYNTH_DEADZONE = 16;

        private readonly DualSenseDevice device;
        private readonly HidDevice hidDevice;
        private readonly byte[] outputBTCrc32Head = new byte[] { 0xA2 };

        private readonly object stateLock = new object();
        private Thread streamThread;
        private CancellationTokenSource streamCancellation;
        private DualSenseControllerOptions.HapticsMode mode =
            DualSenseControllerOptions.HapticsMode.Off;
        // These three values apply without restarting the capture pipeline.
        // Store them in atomically readable primitive fields so the WASAPI and
        // HID threads never observe a torn value on 32-bit builds.
        private long gainBits = BitConverter.DoubleToInt64Bits(3.0);
        private int lowPassHz = 350;
        private string endpointId = string.Empty;
        private bool audioEnabled = false;
        private int audioRouteValue =
            (int)DualSenseControllerOptions.AudioOutputRoute.Auto;
        private int audioVolume = 50;
        private DualSenseControllerOptions.AudioLatencyMode latencyMode =
            DualSenseControllerOptions.AudioLatencyMode.Smooth;
        private LatencyProfile profile = GetLatencyProfile(DualSenseControllerOptions.AudioLatencyMode.Smooth);

        // Rumble-to-haptics synth state
        private double heavyEnv, lightEnv, heavyPhase, lightPhase;

        // Rate-servo output applied by the capture callback; written by the
        // stream thread, read by the WASAPI callback thread.
        private double audioRateTrim;

        // Stopwatch timestamp of the last capture buffer that carried real
        // energy; drives the silence gate.
        private long lastEnergyTimestamp;

        private byte seq;
        private byte packetCounter;

        public bool Active => Volatile.Read(ref streamCancellation) != null;

        public DualSenseHapticsStreamer(DualSenseDevice device, HidDevice hidDevice)
        {
            this.device = device;
            this.hidDevice = hidDevice;
        }

        public void Configure(DualSenseControllerOptions.HapticsMode newMode,
            double newGain, int newLowPassHz, string newEndpointId,
            bool newAudioEnabled, DualSenseControllerOptions.AudioOutputRoute newAudioRoute,
            int newAudioVolume, DualSenseControllerOptions.AudioLatencyMode newLatencyMode)
        {
            lock (stateLock)
            {
                newGain = Math.Clamp(newGain, 0.1, 10.0);
                newLowPassHz = Math.Clamp(newLowPassHz, 40, 1000);
                newEndpointId ??= string.Empty;
                newAudioVolume = Math.Clamp(newAudioVolume, 0, 100);

                // Gain, volume, and routing apply live; anything that changes
                // the pipeline shape needs a restart.
                if (Active && newMode == mode && newLowPassHz == lowPassHz &&
                    newEndpointId == endpointId && newAudioEnabled == audioEnabled &&
                    newLatencyMode == latencyMode)
                {
                    Interlocked.Exchange(ref gainBits, BitConverter.DoubleToInt64Bits(newGain));
                    Volatile.Write(ref audioVolume, newAudioVolume);
                    Volatile.Write(ref audioRouteValue, (int)newAudioRoute);
                    return;
                }

                bool wasRunning = Active;
                StopLocked();

                mode = newMode;
                Interlocked.Exchange(ref gainBits, BitConverter.DoubleToInt64Bits(newGain));
                lowPassHz = newLowPassHz;
                endpointId = newEndpointId;
                audioEnabled = newAudioEnabled;
                Volatile.Write(ref audioRouteValue, (int)newAudioRoute);
                Volatile.Write(ref audioVolume, newAudioVolume);
                latencyMode = newLatencyMode;
                profile = GetLatencyProfile(newLatencyMode);

                if (audioEnabled || mode != DualSenseControllerOptions.HapticsMode.Off)
                {
                    StartLocked();
                }
                else if (wasRunning)
                {
                    AppLogger.LogToGui($"{device.MacAddress}: BT haptics/audio streaming stopped", false);
                }
            }
        }

        public void Stop()
        {
            lock (stateLock)
            {
                StopLocked();
            }
        }

        private void StartLocked()
        {
            CancellationTokenSource cancellationSource = new CancellationTokenSource();
            Volatile.Write(ref audioRateTrim, 0.0);
            Volatile.Write(ref lastEnergyTimestamp, 0L);
            streamCancellation = cancellationSource;
            streamThread = new Thread(() => StreamLoop(cancellationSource))
            {
                Priority = ThreadPriority.AboveNormal,
                IsBackground = true,
                Name = $"DualSense Haptics thread: {device.MacAddress}",
            };
            streamThread.Start();
            AppLogger.LogToGui($"{device.MacAddress}: BT streaming started " +
                $"(haptics: {mode}{(audioEnabled ? ", audio: on" : "")})", false);
        }

        private void StopLocked()
        {
            CancellationTokenSource cancellationSource = streamCancellation;
            Thread thread = streamThread;

            try
            {
                cancellationSource?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The worker may have completed between the snapshot and Cancel.
            }
            if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            {
                thread.Join(500);
            }

            if (ReferenceEquals(streamThread, thread))
            {
                streamThread = null;
            }

            if (ReferenceEquals(streamCancellation, cancellationSource))
            {
                streamCancellation = null;
            }
        }

        private void StreamLoop(CancellationTokenSource cancellationSource)
        {
            CancellationToken cancellationToken = cancellationSource.Token;
            bool captureForHaptics = mode == DualSenseControllerOptions.HapticsMode.SystemAudio ||
                                     mode == DualSenseControllerOptions.HapticsMode.Mix;
            bool useRumbleSynth = mode == DualSenseControllerOptions.HapticsMode.RumbleToHaptics ||
                                  mode == DualSenseControllerOptions.HapticsMode.Mix;
            bool needCapture = captureForHaptics || audioEnabled;

            SampleRing hapticsRing = captureForHaptics ? new SampleRing(profile.HapticsRingBytes) : null;
            ShortRing audioRing = audioEnabled ? new ShortRing(profile.AudioRingSamples) : null;
            WasapiLoopbackCapture capture = null;
            IOpusEncoder opusEncoder = null;
            IntPtr mmcssHandle = IntPtr.Zero;
            IntPtr highResTimer = IntPtr.Zero;

            EnterLatencySensitiveGC();
            try
            {
                // MMCSS puts this thread in the same scheduling class WASAPI
                // clients use; base priority alone still loses to scheduler
                // jitter under load.
                uint mmcssTaskIndex = 0;
                try
                {
                    mmcssHandle = AvSetMmThreadCharacteristicsW("Pro Audio", ref mmcssTaskIndex);
                }
                catch (Exception) { mmcssHandle = IntPtr.Zero; }

                highResTimer = CreateHighResTimer();

                if (needCapture)
                {
                    capture = CreateCapture(hapticsRing, audioRing);
                    if (capture == null)
                    {
                        return;
                    }

                    capture.StartRecording();
                }

                short[] pcmFrame = null;
                byte[] opusA = null;
                if (audioEnabled)
                {
                    // The controller's audio amp defaults to muted volume; it only
                    // plays the stream after headphone/speaker volume is set.
                    if (!SendAudioVolumeSetup(out int setupError))
                    {
                        AppLogger.LogToGui($"{device.MacAddress}: BT audio amplifier setup write failed " +
                            $"(Win32 error {setupError})", true);
                    }

                    opusEncoder = OpusCodecFactory.CreateEncoder(AUDIO_SAMPLE_RATE, 2,
                        OpusApplication.OPUS_APPLICATION_AUDIO);
                    opusEncoder.Bitrate = OPUS_FRAME_BYTES * 8 * 100;
                    opusEncoder.UseVBR = false;
                    // Complexity is a pure quality dial at fixed CBR bitrate;
                    // one 10 ms encode per tick fits the slot with margin.
                    opusEncoder.Complexity = 10;

                    pcmFrame = new short[FRAME_SHORTS];
                    opusA = new byte[OPUS_FRAME_BYTES];
                }

                byte[] report = new byte[audioEnabled ? AUDIO_REPORT_SIZE : HAPTICS_REPORT_SIZE];
                byte[] chunk = new byte[HAPTIC_CHUNK_BYTES];
                bool primed = false;
                bool audioPrimed = false;
                bool needFadeIn = false;
                double fillerL = 0.0, fillerR = 0.0;
                int targetFrames = profile.PrebufferFrames;
                int targetFramesCap = Math.Max(profile.PrebufferFrames,
                    profile.AudioRingSamples / FRAME_SHORTS - 4);
                int silenceTail = 0;
                double rateTrim = 0.0;
                long lastUnderrunTick = 0;

                // Interval health telemetry (logged ~every 30 s when nonzero)
                long audioUnderruns = 0;
                long stallSkips = 0;
                long slowWrites = 0;
                double maxWriteMs = 0.0;
                long lastHealthLogTick = 0;

                int consecutiveFailures = 0;
                int firstWriteError = 0;
                int lastWriteError = 0;
                long tick = 0;
                bool synthStreamWasActive = false;

                Stopwatch clock = Stopwatch.StartNew();
                double nextDeadlineMs = 0.0;

                while (!cancellationToken.IsCancellationRequested)
                {
                    nextDeadlineMs += TICK_MS;
                    double wait = nextDeadlineMs - clock.Elapsed.TotalMilliseconds;
                    if (wait > 2.0)
                    {
                        WaitPrecise(highResTimer, wait - 1.0);
                    }

                    while (!cancellationToken.IsCancellationRequested &&
                        clock.Elapsed.TotalMilliseconds < nextDeadlineMs)
                    {
                        Thread.SpinWait(80);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    double behindMs = clock.Elapsed.TotalMilliseconds - nextDeadlineMs;
                    if (audioEnabled)
                    {
                        // A catch-up burst dequeues one audio frame per report and
                        // instantly drains the prebuffer after a scheduler or GC
                        // pause. Skip missed slots instead; the pad's dejitter
                        // buffer already covered the stall on its side.
                        if (behindMs > TICK_MS * 2.0)
                        {
                            int skip = (int)(behindMs / TICK_MS);
                            nextDeadlineMs += skip * TICK_MS;
                            stallSkips += skip;
                        }
                    }
                    else if (behindMs > profile.MaxCatchupMs)
                    {
                        // Haptics-only: back-to-back catch-up preserves rumble
                        // timing; only resync after a stall too large to absorb.
                        nextDeadlineMs = clock.Elapsed.TotalMilliseconds;
                    }

                    bool hapticsActive = FillHapticsChunk(chunk, hapticsRing,
                        useRumbleSynth, ref primed);

                    bool energyRecent = needCapture &&
                        (Stopwatch.GetTimestamp() - Volatile.Read(ref lastEnergyTimestamp)) *
                        1000.0 / Stopwatch.Frequency < SILENCE_GATE_MS;

                    if (needCapture)
                    {
                        // When neither channel has anything to say, stop after a
                        // short drain tail to save Bluetooth airtime and battery.
                        if (!ShouldTransmitCaptureTick(hapticsActive, energyRecent,
                            ref silenceTail))
                        {
                            if (audioEnabled)
                            {
                                audioPrimed = false;
                                audioRing.TrimToNewest(targetFrames * FRAME_SHORTS);
                            }

                            tick++;
                            continue;
                        }
                    }
                    else if (useRumbleSynth)
                    {
                        // A continuous stream of nominally silent haptic packets can
                        // leave the voice-coil actuators faintly energized. In the
                        // pure rumble-synth pipeline there is no audio clock to
                        // maintain, so send one final centered chunk after an effect
                        // and stop writing until a real rumble signal arrives.
                        bool idleRumbleSynth = !hapticsActive;
                        if (idleRumbleSynth && !synthStreamWasActive)
                        {
                            tick++;
                            continue;
                        }

                        synthStreamWasActive = hapticsActive;
                    }

                    if (audioEnabled)
                    {
                        int framesAvailable = audioRing.Count / FRAME_SHORTS;
                        if (!audioPrimed && framesAvailable >= targetFrames)
                        {
                            audioPrimed = true;
                            needFadeIn = true;
                        }

                        bool gotAudio = audioPrimed && audioRing.ReadExact(pcmFrame);
                        if (audioPrimed && !gotAudio)
                        {
                            audioPrimed = false; // ran dry: rebuffer before resuming
                            if (energyRecent)
                            {
                                audioUnderruns++;
                                lastUnderrunTick = tick;
                                targetFrames = Math.Min(targetFrames + 1, targetFramesCap);
                            }
                        }

                        if (gotAudio)
                        {
                            if (audioRing.TakeDropFlag())
                            {
                                needFadeIn = true; // backlog drop: mask the splice
                            }

                            if (needFadeIn)
                            {
                                ApplyResumeFade(pcmFrame);
                                needFadeIn = false;
                            }

                            fillerL = pcmFrame[FRAME_SHORTS - 2];
                            fillerR = pcmFrame[FRAME_SHORTS - 1];
                        }
                        else
                        {
                            // No content: encode a decay-to-silence filler through
                            // the LIVE encoder. Opus delta-codes state across
                            // frames, so replaying a pre-encoded silence frame
                            // desynchronizes the pad's decoder and clicks at every
                            // underrun boundary; a live encode stays coherent.
                            BuildFillerFrame(pcmFrame, ref fillerL, ref fillerR);
                        }

                        EncodeOpusFrame(opusEncoder, pcmFrame, opusA);

                        if (audioPrimed)
                        {
                            // Integral servo: backlog below target => positive
                            // error => raise the resampler's output rate so each
                            // capture buffer yields more delivery-rate samples,
                            // and vice versa. Converges on the true clock offset
                            // within seconds and holds. Frozen while unprimed so
                            // rebuffer transients cannot wind up the integral.
                            double levelError = (targetFrames + 1) -
                                audioRing.Count / (double)FRAME_SHORTS;
                            rateTrim = ClampRateTrim(rateTrim + levelError * AUDIO_RATE_TRIM_GAIN);
                            Volatile.Write(ref audioRateTrim, rateTrim);
                        }

                        // With a long clean run, decay the escalated prebuffer
                        // back toward the profile's base latency.
                        if (targetFrames > profile.PrebufferFrames &&
                            tick - lastUnderrunTick > ADAPTIVE_DECAY_TICKS)
                        {
                            targetFrames--;
                            lastUnderrunTick = tick;
                        }

                        BuildAudioReport(report, chunk, opusA);
                    }
                    else
                    {
                        BuildHapticsReport(report, chunk);
                    }

                    long writeStart = Stopwatch.GetTimestamp();
                    bool wrote = hidDevice.WriteOutputReportViaInterruptWithTimeout(
                        report, 100, out int writeError);
                    double writeMs = (Stopwatch.GetTimestamp() - writeStart) * 1000.0 / Stopwatch.Frequency;
                    if (writeMs > maxWriteMs)
                    {
                        maxWriteMs = writeMs;
                    }

                    if (writeMs > 20.0)
                    {
                        slowWrites++;
                    }

                    if (wrote)
                    {
                        consecutiveFailures = 0;
                        firstWriteError = 0;
                        lastWriteError = 0;
                    }
                    else
                    {
                        if (consecutiveFailures == 0)
                        {
                            firstWriteError = writeError;
                        }

                        lastWriteError = writeError;
                        if (++consecutiveFailures >= MAX_CONSECUTIVE_WRITE_FAILURES)
                        {
                            AppLogger.LogToGui($"{device.MacAddress}: BT haptics/audio stream aborted after " +
                                $"{consecutiveFailures} consecutive write failures " +
                                $"(Win32 first={firstWriteError}, last={lastWriteError}, " +
                                $"report=0x{report[0]:X2}, bytes={report.Length})", true);
                            break;
                        }
                    }

                    if (tick - lastHealthLogTick > 2800) // ~30 s
                    {
                        long ringDrops = audioRing?.TakeDropCount() ?? 0;
                        if (audioUnderruns > 0 || stallSkips > 0 || slowWrites > 0 || ringDrops > 0)
                        {
                            AppLogger.LogToGui($"{device.MacAddress}: BT stream health: " +
                                $"underruns={audioUnderruns} drops={ringDrops} stallSkips={stallSkips} " +
                                $"slowWrites={slowWrites} maxWrite={maxWriteMs:F1}ms " +
                                $"trim={rateTrim * 1e6:F0}ppm prebuffer={targetFrames}f", false);
                        }

                        audioUnderruns = 0;
                        stallSkips = 0;
                        slowWrites = 0;
                        maxWriteMs = 0.0;
                        lastHealthLogTick = tick;
                    }

                    tick++;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui($"{device.MacAddress}: BT haptics/audio stream error: {ex.Message}", true);
            }
            finally
            {
                if (capture != null)
                {
                    try
                    {
                        capture.StopRecording();
                        capture.Dispose();
                    }
                    catch (Exception) { }
                }

                (opusEncoder as IDisposable)?.Dispose();

                if (highResTimer != IntPtr.Zero)
                {
                    CloseHandle(highResTimer);
                }

                if (mmcssHandle != IntPtr.Zero)
                {
                    try { AvRevertMmThreadCharacteristics(mmcssHandle); }
                    catch (Exception) { }
                }

                ExitLatencySensitiveGC();

                // Stop and Configure may be waiting for this worker while holding
                // stateLock. Use generation-checked atomics here so cleanup cannot
                // deadlock or impose the full join timeout on a normal stop.
                if (ReferenceEquals(Interlocked.CompareExchange(
                    ref streamCancellation, null, cancellationSource), cancellationSource))
                {
                    Interlocked.CompareExchange(ref streamThread, null,
                        Thread.CurrentThread);
                }

                cancellationSource.Dispose();
            }
        }

        /// <summary>
        /// Fills one 64-byte haptic chunk (u8 offset-binary, silence = 0x80)
        /// from the capture ring and/or the rumble synth.
        /// </summary>
        private bool FillHapticsChunk(byte[] chunk, SampleRing ring, bool useRumbleSynth, ref bool primed)
        {
            int captured = 0;
            if (ring != null)
            {
                if (!primed && ring.Count >= profile.HapticsPrebufferBytes)
                {
                    primed = true;
                }

                captured = primed ? ring.ReadPartial(chunk) : 0;
                if (primed && captured == 0)
                {
                    primed = false;
                }
            }

            if (captured < chunk.Length)
            {
                Array.Fill(chunk, (byte)0x80, captured, chunk.Length - captured);
            }

            if (useRumbleSynth)
            {
                bool pureRumbleSynth = ring == null;
                byte rawHeavy = device.CurrentRumbleHeavy;
                byte rawLight = device.CurrentRumbleLight;
                double heavyTarget = ScaleRumbleStrength(rawHeavy);
                double lightTarget = ScaleRumbleStrength(rawLight);
                double heavyInc = 2.0 * Math.PI * HEAVY_FREQ_HZ / SAMPLE_RATE;
                double lightInc = 2.0 * Math.PI * LIGHT_FREQ_HZ / SAMPLE_RATE;
                for (int i = 0; i < chunk.Length / 2; i++)
                {
                    heavyEnv += (heavyTarget - heavyEnv) *
                        (heavyTarget > heavyEnv ? ENVELOPE_ATTACK : ENVELOPE_RELEASE);
                    lightEnv += (lightTarget - lightEnv) *
                        (lightTarget > lightEnv ? ENVELOPE_ATTACK : ENVELOPE_RELEASE);

                    double rumbleLeft = heavyEnv * Math.Sin(heavyPhase);
                    double rumbleRight = lightEnv * Math.Sin(lightPhase);
                    double left = (chunk[i * 2] - 128) / 127.0 + rumbleLeft;
                    double right = (chunk[i * 2 + 1] - 128) / 127.0 + rumbleRight;
                    heavyPhase += heavyInc;
                    lightPhase += lightInc;

                    if (pureRumbleSynth)
                    {
                        // A full XInput motor command should span the actuator's
                        // full signed PCM range. The generic soft clipper maps a
                        // unit peak to only 50%, making game rumble unnecessarily weak.
                        chunk[i * 2] = UnitSampleToU8(rumbleLeft);
                        chunk[i * 2 + 1] = UnitSampleToU8(rumbleRight);
                    }
                    else
                    {
                        // Mix mode can contain both captured PCM and synthesized
                        // rumble, so use a smooth limiter to avoid hard clipping.
                        chunk[i * 2] = TanhSampleToU8(left * 1.5);
                        chunk[i * 2 + 1] = TanhSampleToU8(right * 1.5);
                    }
                }
            }

            return HasHapticSignal(chunk);
        }

        internal static double ScaleRumbleStrength(byte strength)
        {
            if (strength <= RUMBLE_SYNTH_DEADZONE)
            {
                return 0.0;
            }

            return (strength - RUMBLE_SYNTH_DEADZONE) /
                (double)(byte.MaxValue - RUMBLE_SYNTH_DEADZONE);
        }

        internal static bool HasHapticSignal(byte[] chunk)
        {
            for (int i = 0; i < chunk.Length; i++)
            {
                if (chunk[i] != 0x80)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool ShouldTransmitCaptureTick(bool hapticsActive,
            bool energyRecent, ref int silenceTail)
        {
            if (hapticsActive || energyRecent)
            {
                silenceTail = 0;
                return true;
            }

            silenceTail++;
            return silenceTail <= SILENCE_TAIL_REPORTS;
        }

        internal static byte UnitSampleToU8(double sample)
        {
            return (byte)Math.Clamp(128.0 + sample * 127.0, 1.0, 255.0);
        }

        private static byte TanhSampleToU8(double sample)
        {
            return (byte)Math.Clamp(128.0 + Math.Tanh(sample) * 127.0, 1.0, 255.0);
        }

        internal static double ClampRateTrim(double trim)
        {
            return Math.Clamp(trim, -AUDIO_RATE_TRIM_LIMIT, AUDIO_RATE_TRIM_LIMIT);
        }

        /// <summary>
        /// Soft-clips and quantizes one unit-range haptic sample to u8
        /// offset-binary with 1-LSB TPDF dither. 8-bit quantization makes
        /// quiet haptic tails feel granular; dither trades that for a far
        /// less perceptible noise floor. Exact digital silence passes through
        /// untouched so silence detection and idle gating still work.
        /// </summary>
        internal static byte DitherQuantizeU8(double x, ref uint rngState)
        {
            if (x == 0.0)
            {
                return 0x80;
            }

            double y = x / (1.0 + Math.Abs(x));
            double dither = NextUnit(ref rngState) + NextUnit(ref rngState) - 1.0;
            return (byte)Math.Clamp(Math.Round(128.0 + y * 127.0 + dither), 0.0, 255.0);
        }

        private static double NextUnit(ref uint state)
        {
            // xorshift32; cheap enough for one call per haptic sample.
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state & 0xFFFFFF) / 16777216.0;
        }

        /// <summary>
        /// Fills one PCM frame with an exponential decay from the last sent
        /// sample pair toward digital silence, so an underrun ends in a fade
        /// instead of a step discontinuity. Subsequent filler frames are
        /// effectively silent.
        /// </summary>
        internal static void BuildFillerFrame(short[] pcm, ref double lastL, ref double lastR)
        {
            const double decay = 0.985; // reaches ~-45 dB across one 480-sample frame
            double l = lastL;
            double r = lastR;
            for (int i = 0; i < pcm.Length / 2; i++)
            {
                l *= decay;
                r *= decay;
                pcm[i * 2] = (short)l;
                pcm[i * 2 + 1] = (short)r;
            }

            lastL = l;
            lastR = r;
        }

        /// <summary>Linear ~5 ms fade-in masking the resume edge after a rebuffer.</summary>
        internal static void ApplyResumeFade(short[] pcm)
        {
            for (int i = 0; i < RESUME_FADE_FRAMES && i < pcm.Length / 2; i++)
            {
                double scale = i / (double)RESUME_FADE_FRAMES;
                pcm[i * 2] = (short)(pcm[i * 2] * scale);
                pcm[i * 2 + 1] = (short)(pcm[i * 2 + 1] * scale);
            }
        }

        private void EncodeOpusFrame(IOpusEncoder encoder, short[] pcm, byte[] dest)
        {
            int written = encoder.Encode(pcm, OPUS_SAMPLES_PER_FRAME, dest, dest.Length);
            if (written < dest.Length && written > 0)
            {
                Array.Clear(dest, written, dest.Length - written);
            }
        }

        /// <summary>
        /// Self-contained report 0x36 layout (398 bytes), per DS5Dongle-AutoHaptics:
        /// config packet 0x11, SetStateData packet 0x10, then one signed 64-byte
        /// haptic PCM packet 0x12. Embedding state in every report is required on
        /// the Windows Bluetooth HID path used by this controller.
        /// </summary>
        internal void BuildHapticsReport(byte[] report, byte[] chunk)
        {
            Array.Clear(report, 0, HAPTICS_REPORT_SIZE);
            report[0] = HAPTICS_REPORT_ID;
            report[1] = (byte)((seq & 0x0F) << 4);
            seq = (byte)((seq + 1) & 0x0F);

            WriteConfigAndState(report);

            report[76] = 0x92; // haptic audio packet: PID 0x12 | sized
            report[77] = HAPTIC_CHUNK_BYTES;
            for (int i = 0; i < HAPTIC_CHUNK_BYTES; i++)
            {
                // The internal ring is u8 offset-binary; report 0x36 carries s8 PCM.
                report[78 + i] = (byte)(chunk[i] ^ 0x80);
            }

            ApplyCrc(report, HAPTICS_REPORT_SIZE);
        }

        /// <summary>
        /// Self-contained report 0x36 with listening audio, per
        /// DS5Dongle-AutoHaptics: config, SetStateData, one signed haptic chunk,
        /// and one 200-byte Opus speaker/headphone packet.
        /// </summary>
        internal void BuildAudioReport(byte[] report, byte[] chunk, byte[] opus)
        {
            Array.Clear(report, 0, AUDIO_REPORT_SIZE);
            report[0] = AUDIO_REPORT_ID;
            report[1] = (byte)((seq & 0x0F) << 4);
            seq = (byte)((seq + 1) & 0x0F);

            WriteConfigAndState(report);

            report[76] = 0x92; // haptic packet: PID 0x12 | sized
            report[77] = HAPTIC_CHUNK_BYTES;
            for (int i = 0; i < HAPTIC_CHUNK_BYTES; i++)
            {
                report[78 + i] = (byte)(chunk[i] ^ 0x80);
            }

            DualSenseControllerOptions.AudioOutputRoute currentRoute =
                (DualSenseControllerOptions.AudioOutputRoute)Volatile.Read(ref audioRouteValue);
            bool headphone;
            switch (currentRoute)
            {
                case DualSenseControllerOptions.AudioOutputRoute.Headphone:
                    headphone = true;
                    break;
                case DualSenseControllerOptions.AudioOutputRoute.Speaker:
                    headphone = false;
                    break;
                case DualSenseControllerOptions.AudioOutputRoute.Auto:
                default:
                    headphone = device.HeadsetPlugged;
                    break;
            }

            report[142] = (byte)((headphone ? 0x16 : 0x13) | 0x80);
            report[143] = OPUS_FRAME_BYTES;
            Buffer.BlockCopy(opus, 0, report, 144, OPUS_FRAME_BYTES);

            ApplyCrc(report, AUDIO_REPORT_SIZE);
        }

        private void WriteConfigAndState(byte[] report)
        {
            report[2] = 0x91; // config packet: PID 0x11 | sized
            report[3] = 0x07;
            report[4] = 0xFE;
            // Controller-side dejitter depth. Low Latency (32), Balanced (64),
            // and Smooth (120) were each validated on first-party hardware.
            byte controllerBuffer = profile.ControllerBuffer;
            report[5] = controllerBuffer;
            report[6] = controllerBuffer;
            report[7] = controllerBuffer;
            report[8] = controllerBuffer;
            report[9] = controllerBuffer;
            report[10] = ++packetCounter;

            report[11] = 0x90; // SetStateData packet: PID 0x10 | sized
            report[12] = (byte)HAPTICS_STATE.Length;
            Buffer.BlockCopy(HAPTICS_STATE, 0, report, 13, HAPTICS_STATE.Length);
        }

        /// <summary>
        /// Sends a SetStateData container packet (PID 0x10 inside a 0x32 report)
        /// that unmutes the controller's audio amp: headphone/speaker volume 100
        /// with a mild speaker pre-gain boost. Mirrors what DS5Dongle emits when
        /// the USB host sets its volume; without this the Opus stream is silent.
        /// </summary>
        private bool SendAudioVolumeSetup(out int win32Error)
        {
            byte[] pkt = new byte[STATE_SETUP_REPORT_SIZE];
            pkt[0] = STATE_SETUP_REPORT_ID;
            // SetStateData uses a fixed command byte here, not the rolling
            // report sequence used by 0x11/0x12 audio containers. Sending a
            // sequence nibble causes the controller to ignore the amplifier
            // unmute/volume state while still accepting subsequent audio writes.
            pkt[1] = 0x10;

            pkt[2] = 0x90; // SetStateData packet: PID 0x10 | sized
            pkt[3] = 0x3F;

            // SetStateData payload starts at pkt[4] (DS5Dongle layout).
            pkt[4] = 0xB0;      // AllowHeadphoneVolume | AllowSpeakerVolume | AllowAudioControl
            pkt[5] = 0x80;      // AllowAudioControl2
            pkt[4 + 4] = 0x64;  // VolumeHeadphones (max 0x7F)
            pkt[4 + 5] = 0x64;  // VolumeSpeaker (PS5 uses 0x3D..0x64)
            pkt[4 + 7] = 0x00;  // AudioControl: mic auto, default output path
            pkt[4 + 37] = 0x02; // AudioControl2: SpeakerCompPreGain = 2

            ApplyCrc(pkt, STATE_SETUP_REPORT_SIZE);
            return hidDevice.WriteOutputReportViaInterruptWithTimeout(
                pkt, 100, out win32Error);
        }

        private void ApplyCrc(byte[] report, int totalSize)
        {
            int crcOffset = totalSize - 4;
            uint calcCrc32 = ~Crc32Algorithm.Compute(outputBTCrc32Head);
            calcCrc32 = ~Crc32Algorithm.CalculateBasicHash(ref calcCrc32, ref report, 0, crcOffset);
            report[crcOffset] = (byte)calcCrc32;
            report[crcOffset + 1] = (byte)(calcCrc32 >> 8);
            report[crcOffset + 2] = (byte)(calcCrc32 >> 16);
            report[crcOffset + 3] = (byte)(calcCrc32 >> 24);
        }

        internal static bool IsSupportedLoopbackFormat(WaveFormat format)
        {
            if (format == null || format.BitsPerSample != 32)
            {
                return false;
            }

            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                return true;
            }

            return format.Encoding == WaveFormatEncoding.Extensible &&
                format is WaveFormatExtensible extensible &&
                extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
        }

        private WasapiLoopbackCapture CreateCapture(SampleRing hapticsRing, ShortRing audioRing)
        {
            WasapiLoopbackCapture capture = null;
            try
            {
                string endpointName = null;
                if (!string.IsNullOrEmpty(endpointId))
                {
                    using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                    try
                    {
                        MMDevice endpoint = enumerator.GetDevice(endpointId);
                        if (endpoint != null && endpoint.State == DeviceState.Active)
                        {
                            capture = new WasapiLoopbackCapture(endpoint);
                            endpointName = endpoint.FriendlyName;
                        }
                    }
                    catch (Exception)
                    {
                        AppLogger.LogToGui($"{device.MacAddress}: configured haptics audio device unavailable, using default output", true);
                    }
                }

                if (capture == null)
                {
                    capture = new WasapiLoopbackCapture();
                    try
                    {
                        using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                        endpointName = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
                    }
                    catch (Exception) { }
                }

                if (!IsSupportedLoopbackFormat(capture.WaveFormat))
                {
                    throw new NotSupportedException(
                        $"Unsupported loopback mix format {capture.WaveFormat}; " +
                        "DualSense streaming requires 32-bit IEEE float samples.");
                }

                AppLogger.LogToGui($"{device.MacAddress}: capturing audio from \"{endpointName ?? "default output"}\"", false);

                int inRate = capture.WaveFormat.SampleRate;
                int inChannels = capture.WaveFormat.Channels;

                // Two cascaded biquads (4th-order, ~70 dB at the fold) before
                // decimating to 3 kHz. The old single 2nd-order stage let bright
                // content alias back into the tactile band as non-harmonic mud.
                BiquadLowPass lpfL = hapticsRing != null ? new BiquadLowPass(lowPassHz, inRate) : null;
                BiquadLowPass lpfR = hapticsRing != null ? new BiquadLowPass(lowPassHz, inRate) : null;
                BiquadLowPass lpfL2 = hapticsRing != null ? new BiquadLowPass(lowPassHz, inRate) : null;
                BiquadLowPass lpfR2 = hapticsRing != null ? new BiquadLowPass(lowPassHz, inRate) : null;
                int decimPhase = 0;
                uint ditherState = 0x9E3779B9;

                WdlResampler resampler = null;
                float[] resampleOut = null;
                if (audioRing != null)
                {
                    resampler = new WdlResampler();
                    // Sinc mode: materially better anti-aliasing than the
                    // default linear-interpolation mode for 96k -> 45k, at a
                    // still-trivial CPU cost.
                    resampler.SetMode(true, 0, true, 64, 32);
                    resampler.SetFilterParms();
                    resampler.SetFeedMode(true);
                    resampler.SetRates(inRate, AUDIO_DELIVERY_RATE);
                    resampleOut = new float[16384];
                }

                capture.DataAvailable += (sender, e) =>
                {
                    double captureGain = BitConverter.Int64BitsToDouble(
                        Interlocked.Read(ref gainBits));
                    double volumeScale = Volatile.Read(ref audioVolume) / 100.0;
                    ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(
                        e.Buffer.AsSpan(0, e.BytesRecorded));
                    int frames = samples.Length / inChannels;
                    if (frames <= 0)
                    {
                        return;
                    }

                    bool energyFound = false;

                    if (audioRing != null)
                    {
                        // Apply the stream thread's servo correction before
                        // resampling this buffer; all resampler access stays on
                        // the capture callback thread.
                        double trim = Volatile.Read(ref audioRateTrim);
                        resampler.SetRates(inRate, AUDIO_DELIVERY_RATE * (1.0 + trim));

                        float[] inBuffer;
                        int inOffset;
                        resampler.ResamplePrepare(frames, 2, out inBuffer, out inOffset);
                        for (int i = 0; i < frames; i++)
                        {
                            DownmixToStereo(samples, i * inChannels, inChannels,
                                out float left, out float right);
                            if (!energyFound &&
                                (Math.Abs(left) > AUDIO_ENERGY_THRESHOLD ||
                                 Math.Abs(right) > AUDIO_ENERGY_THRESHOLD))
                            {
                                energyFound = true;
                            }

                            inBuffer[inOffset + i * 2] = left;
                            inBuffer[inOffset + i * 2 + 1] = right;
                        }

                        int outFrames = resampler.ResampleOut(resampleOut, 0, frames, resampleOut.Length / 2, 2);
                        audioRing.Write(resampleOut, outFrames * 2, volumeScale);
                    }

                    if (hapticsRing != null)
                    {
                        for (int i = 0; i < frames; i++)
                        {
                            DownmixToStereo(samples, i * inChannels, inChannels,
                                out float left, out float right);
                            if (audioRing == null && !energyFound &&
                                (Math.Abs(left) > AUDIO_ENERGY_THRESHOLD ||
                                 Math.Abs(right) > AUDIO_ENERGY_THRESHOLD))
                            {
                                energyFound = true;
                            }

                            double l = lpfL2.Process(lpfL.Process(left));
                            double r = lpfR2.Process(lpfR.Process(right));

                            decimPhase += SAMPLE_RATE;
                            if (decimPhase < inRate)
                            {
                                continue;
                            }

                            decimPhase -= inRate;
                            hapticsRing.Write(
                                DitherQuantizeU8(l * captureGain, ref ditherState),
                                DitherQuantizeU8(r * captureGain, ref ditherState));
                        }
                    }

                    if (energyFound)
                    {
                        Volatile.Write(ref lastEnergyTimestamp, Stopwatch.GetTimestamp());
                    }
                };

                return capture;
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui($"{device.MacAddress}: failed to open audio capture: {ex.Message}", true);
                capture?.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Downmixes common mono, stereo, quad, 5.0, 5.1, and 7.1 channel orders
        /// to stereo. Center is shared at -3 dB, LFE at -6 dB, and surround
        /// channels at -3 dB. Multichannel sums use a soft limiter so loud scenes
        /// cannot hard-clip before Opus encoding.
        /// </summary>
        internal static void DownmixToStereo(ReadOnlySpan<float> samples, int offset,
            int channels, out float left, out float right)
        {
            if (channels <= 0 || offset < 0 || offset + channels > samples.Length)
            {
                left = 0.0f;
                right = 0.0f;
                return;
            }

            float frontLeft = samples[offset];
            if (channels == 1)
            {
                left = frontLeft;
                right = frontLeft;
                return;
            }

            float frontRight = samples[offset + 1];
            if (channels == 2)
            {
                left = frontLeft;
                right = frontRight;
                return;
            }

            const float centerWeight = 0.70710678f;
            const float surroundWeight = 0.70710678f;
            const float lfeWeight = 0.5f;

            float mixedLeft = frontLeft;
            float mixedRight = frontRight;
            if (channels == 3)
            {
                float center = samples[offset + 2] * centerWeight;
                mixedLeft += center;
                mixedRight += center;
            }
            else if (channels == 4)
            {
                mixedLeft += samples[offset + 2] * surroundWeight;
                mixedRight += samples[offset + 3] * surroundWeight;
            }
            else if (channels == 5)
            {
                float center = samples[offset + 2] * centerWeight;
                mixedLeft += center + samples[offset + 3] * surroundWeight;
                mixedRight += center + samples[offset + 4] * surroundWeight;
            }
            else
            {
                // Standard WAVEFORMATEXTENSIBLE 5.1/7.1 order:
                // FL, FR, FC, LFE, BL, BR, [SL, SR].
                float center = samples[offset + 2] * centerWeight;
                float lfe = samples[offset + 3] * lfeWeight;
                mixedLeft += center + lfe + samples[offset + 4] * surroundWeight;
                mixedRight += center + lfe + samples[offset + 5] * surroundWeight;
                if (channels >= 8)
                {
                    mixedLeft += samples[offset + 6] * surroundWeight;
                    mixedRight += samples[offset + 7] * surroundWeight;
                }
            }

            left = MathF.Tanh(mixedLeft);
            right = MathF.Tanh(mixedRight);
        }

        // --- Scheduling support -------------------------------------------------

        // SustainedLowLatency while any streamer is active reduces blocking
        // full collections on the timing-sensitive writer. Refcounted across pads.
        private static readonly object gcModeGate = new object();
        private static int gcModeUsers;
        private static GCLatencyMode gcPreviousMode;

        private static void EnterLatencySensitiveGC()
        {
            lock (gcModeGate)
            {
                if (++gcModeUsers == 1)
                {
                    gcPreviousMode = GCSettings.LatencyMode;
                    try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; }
                    catch (Exception) { }
                }
            }
        }

        private static void ExitLatencySensitiveGC()
        {
            lock (gcModeGate)
            {
                if (gcModeUsers > 0 && --gcModeUsers == 0)
                {
                    try { GCSettings.LatencyMode = gcPreviousMode; }
                    catch (Exception) { }
                }
            }
        }

        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        private const uint TIMER_ALL_ACCESS = 0x1F0003;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name,
            uint flags, uint desiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime,
            int period, IntPtr completionRoutine, IntPtr argToCompletionRoutine, bool resume);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

        [DllImport("avrt.dll")]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        /// <summary>
        /// High-resolution waitable timer (Windows 10 1803+); IntPtr.Zero on
        /// older systems, in which case the loop falls back to Thread.Sleep.
        /// Sub-millisecond wakeups without depending on the global timer
        /// resolution and without long spin waits.
        /// </summary>
        private static IntPtr CreateHighResTimer()
        {
            try
            {
                return CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
                    CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        private static void WaitPrecise(IntPtr timer, double milliseconds)
        {
            if (milliseconds <= 0.0)
            {
                return;
            }

            if (timer != IntPtr.Zero)
            {
                long dueTime = -(long)(milliseconds * 10000.0); // relative, 100 ns units
                if (SetWaitableTimer(timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    WaitForSingleObject(timer, (uint)milliseconds + 16);
                    return;
                }
            }

            Thread.Sleep((int)milliseconds);
        }

        /// <summary>Byte ring for interleaved L/R u8 haptic samples with bounded latency.</summary>
        private sealed class SampleRing
        {
            private readonly byte[] buffer;
            private readonly object gate = new object();
            private int head;
            private int count;

            public int Count { get { lock (gate) return count; } }

            public SampleRing(int capacityBytes)
            {
                buffer = new byte[capacityBytes];
            }

            public void Write(byte left, byte right)
            {
                lock (gate)
                {
                    if (count > buffer.Length - 2)
                    {
                        head = (head + 2) % buffer.Length;
                        count -= 2;
                    }

                    int tail = (head + count) % buffer.Length;
                    buffer[tail] = left;
                    buffer[(tail + 1) % buffer.Length] = right;
                    count += 2;
                }
            }

            public int ReadPartial(byte[] dest)
            {
                lock (gate)
                {
                    int n = Math.Min(count, dest.Length) & ~1;
                    for (int i = 0; i < n; i++)
                    {
                        dest[i] = buffer[head];
                        head = (head + 1) % buffer.Length;
                    }

                    count -= n;
                    return n;
                }
            }
        }

        /// <summary>
        /// Ring of 16-bit interleaved stereo samples at the delivery rate
        /// feeding the Opus encoder. Drops oldest data when full to bound
        /// latency and records the drop so the reader can mask the splice.
        /// </summary>
        private sealed class ShortRing
        {
            private readonly short[] buffer;
            private readonly object gate = new object();
            private int head;
            private int count;
            private bool droppedSinceRead;
            private long droppedTotal;

            public int Count { get { lock (gate) return count; } }

            public ShortRing(int capacitySamples)
            {
                buffer = new short[capacitySamples];
            }

            public void Write(float[] samples, int sampleCount, double volumeScale)
            {
                lock (gate)
                {
                    for (int i = 0; i < sampleCount; i++)
                    {
                        if (count >= buffer.Length)
                        {
                            head = (head + 2) % buffer.Length;
                            count -= 2;
                            droppedSinceRead = true;
                            droppedTotal++;
                        }

                        double v = samples[i] * volumeScale * 32767.0;
                        int tail = (head + count) % buffer.Length;
                        buffer[tail] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                        count++;
                    }
                }
            }

            /// <summary>Reads exactly dest.Length samples or returns false leaving state unchanged.</summary>
            public bool ReadExact(short[] dest)
            {
                lock (gate)
                {
                    if (count < dest.Length)
                    {
                        return false;
                    }

                    for (int i = 0; i < dest.Length; i++)
                    {
                        dest[i] = buffer[head];
                        head = (head + 1) % buffer.Length;
                    }

                    count -= dest.Length;
                    return true;
                }
            }

            /// <summary>Drops oldest samples so at most maxSamples remain (keeps sample pairs aligned).</summary>
            public void TrimToNewest(int maxSamples)
            {
                lock (gate)
                {
                    if (count > maxSamples)
                    {
                        int drop = count - maxSamples;
                        drop -= drop % 2;
                        head = (head + drop) % buffer.Length;
                        count -= drop;
                    }
                }
            }

            /// <summary>True once if an overflow drop occurred since the last call.</summary>
            public bool TakeDropFlag()
            {
                lock (gate)
                {
                    bool value = droppedSinceRead;
                    droppedSinceRead = false;
                    return value;
                }
            }

            /// <summary>Total dropped samples since the last call (telemetry).</summary>
            public long TakeDropCount()
            {
                lock (gate)
                {
                    long value = droppedTotal;
                    droppedTotal = 0;
                    return value;
                }
            }
        }

        /// <summary>2nd-order Butterworth low-pass (Q = 0.7071), direct form 1.</summary>
        private sealed class BiquadLowPass
        {
            private readonly double b0, b1, b2, a1, a2;
            private double x1, x2, y1, y2;

            public BiquadLowPass(double cutoffHz, double sampleRate)
            {
                double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
                double alpha = Math.Sin(w0) / (2.0 * 0.70710678);
                double cosW0 = Math.Cos(w0);
                double a0 = 1.0 + alpha;
                b0 = (1.0 - cosW0) / 2.0 / a0;
                b1 = (1.0 - cosW0) / a0;
                b2 = b0;
                a1 = -2.0 * cosW0 / a0;
                a2 = (1.0 - alpha) / a0;
            }

            public double Process(double x)
            {
                double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x;
                y2 = y1; y1 = y;
                return y;
            }
        }
    }
}
