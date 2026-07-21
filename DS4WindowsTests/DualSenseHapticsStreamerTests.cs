using DS4Windows;
using DS4Windows.InputDevices;
using NAudio.Wave;

namespace DS4WindowsTests;

[TestClass]
public class DualSenseHapticsStreamerTests
{
    [DataTestMethod]
    [DataRow((byte)0)]
    [DataRow(DualSenseHapticsStreamer.RUMBLE_SYNTH_DEADZONE)]
    public void ScaleRumbleStrength_SuppressesNoiseFloor(byte strength)
    {
        Assert.AreEqual(0.0, DualSenseHapticsStreamer.ScaleRumbleStrength(strength));
    }

    [TestMethod]
    public void ScaleRumbleStrength_PreservesFullStrength()
    {
        Assert.AreEqual(1.0, DualSenseHapticsStreamer.ScaleRumbleStrength(byte.MaxValue));
    }

    [TestMethod]
    public void ScaleRumbleStrength_RescalesValuesAboveNoiseFloor()
    {
        byte strength = (byte)(DualSenseHapticsStreamer.RUMBLE_SYNTH_DEADZONE + 1);
        double expected = 1.0 /
            (byte.MaxValue - DualSenseHapticsStreamer.RUMBLE_SYNTH_DEADZONE);

        Assert.AreEqual(expected, DualSenseHapticsStreamer.ScaleRumbleStrength(strength), 1e-12);
    }

    [TestMethod]
    public void HasHapticSignal_TreatsCenteredChunkAsSilence()
    {
        byte[] chunk = Enumerable.Repeat((byte)0x80, 64).ToArray();

        Assert.IsFalse(DualSenseHapticsStreamer.HasHapticSignal(chunk));
    }

    [TestMethod]
    public void HasHapticSignal_DetectsSampleAwayFromCenter()
    {
        byte[] chunk = Enumerable.Repeat((byte)0x80, 64).ToArray();
        chunk[31] = 0x81;

        Assert.IsTrue(DualSenseHapticsStreamer.HasHapticSignal(chunk));
    }

    [DataTestMethod]
    [DataRow(-1.0, (byte)1)]
    [DataRow(0.0, (byte)128)]
    [DataRow(1.0, (byte)255)]
    public void UnitSampleToU8_UsesFullSignedPcmRange(double sample, byte expected)
    {
        Assert.AreEqual(expected, DualSenseHapticsStreamer.UnitSampleToU8(sample));
    }

    [TestMethod]
    public void DownmixToStereo_PreservesStereoSamples()
    {
        float[] samples = { 0.25f, -0.5f };

        DualSenseHapticsStreamer.DownmixToStereo(samples, 0, 2, out float left, out float right);

        Assert.AreEqual(0.25f, left);
        Assert.AreEqual(-0.5f, right);
    }

    [TestMethod]
    public void DownmixToStereo_SharesSevenPointOneCenterChannel()
    {
        float[] samples = { 0.0f, 0.0f, 0.4f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f };
        float expected = MathF.Tanh(0.4f * 0.70710678f);

        DualSenseHapticsStreamer.DownmixToStereo(samples, 0, 8, out float left, out float right);

        Assert.AreEqual(expected, left, 1e-6f);
        Assert.AreEqual(expected, right, 1e-6f);
    }

    [TestMethod]
    public void DownmixToStereo_RoutesSevenPointOneSideChannels()
    {
        float[] samples = { 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.5f, -0.25f };

        DualSenseHapticsStreamer.DownmixToStereo(samples, 0, 8, out float left, out float right);

        Assert.IsTrue(left > 0.0f);
        Assert.IsTrue(right < 0.0f);
        Assert.IsTrue(Math.Abs(left) < 1.0f);
        Assert.IsTrue(Math.Abs(right) < 1.0f);
    }

    [TestMethod]
    public void DitherQuantizeU8_ExactSilencePassesThrough()
    {
        // Dithered silence would defeat HasHapticSignal idle detection and the
        // silence gate, so exact digital zero must always map to exactly 0x80.
        uint state = 0x12345678;
        uint initialState = state;
        byte[] chunk = new byte[64];
        for (int i = 0; i < 100; i++)
        {
            chunk[i % chunk.Length] =
                DualSenseHapticsStreamer.DitherQuantizeU8(0.0, ref state);
        }

        Assert.AreEqual(initialState, state);
        Assert.IsFalse(DualSenseHapticsStreamer.HasHapticSignal(chunk));
    }

    [TestMethod]
    public void DitherQuantizeU8_StaysWithinOneLsbOfUndithered()
    {
        const double x = 0.25;
        double softClipped = x / (1.0 + Math.Abs(x));
        int reference = (int)Math.Round(128.0 + softClipped * 127.0);

        uint state = 0x9E3779B9;
        byte first = DualSenseHapticsStreamer.DitherQuantizeU8(x, ref state);
        bool observedDifferentValue = false;
        for (int i = 0; i < 1000; i++)
        {
            byte q = DualSenseHapticsStreamer.DitherQuantizeU8(x, ref state);
            Assert.IsTrue(Math.Abs(q - reference) <= 1,
                $"dithered value {q} strayed more than 1 LSB from {reference}");
            observedDifferentValue |= q != first;
        }

        Assert.IsTrue(observedDifferentValue, "dither did not vary the quantized output");
    }

    [DataTestMethod]
    [DataRow(1.0, DualSenseHapticsStreamer.AUDIO_RATE_TRIM_LIMIT)]
    [DataRow(-1.0, -DualSenseHapticsStreamer.AUDIO_RATE_TRIM_LIMIT)]
    [DataRow(0.001, 0.001)]
    public void ClampRateTrim_LimitsServoAuthority(double input, double expected)
    {
        Assert.AreEqual(expected, DualSenseHapticsStreamer.ClampRateTrim(input), 1e-12);
    }

    [TestMethod]
    public void BuildFillerFrame_DecaysToSilenceWithoutStep()
    {
        short[] pcm = new short[960];
        double lastL = 16000.0, lastR = -12000.0;

        DualSenseHapticsStreamer.BuildFillerFrame(pcm, ref lastL, ref lastR);

        // First sample continues from the previous output (no step discontinuity)
        Assert.IsTrue(Math.Abs(pcm[0] - 16000.0 * 0.985) < 2.0);
        Assert.IsTrue(Math.Abs(pcm[1] + 12000.0 * 0.985) < 2.0);

        // A second filler frame is effectively silent
        DualSenseHapticsStreamer.BuildFillerFrame(pcm, ref lastL, ref lastR);
        Assert.IsTrue(Math.Abs(lastL) < 1.0);
        Assert.IsTrue(Math.Abs(lastR) < 1.0);
    }

    [TestMethod]
    public void ApplyResumeFade_RampsInFromZero()
    {
        short[] pcm = new short[960];
        Array.Fill(pcm, (short)10000);

        DualSenseHapticsStreamer.ApplyResumeFade(pcm);

        Assert.AreEqual(0, pcm[0]);
        Assert.AreEqual(0, pcm[1]);
        Assert.IsTrue(pcm[2] < 100); // early ramp stays near zero
        // Beyond the fade window the content is untouched
        Assert.AreEqual(10000, pcm[DualSenseHapticsStreamer.RESUME_FADE_FRAMES * 2]);
    }

    [DataTestMethod]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.LowLatency, (byte)32)]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.Balanced, (byte)64)]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.Smooth, (byte)120)]
    public void GetLatencyProfile_CarriesControllerDejitterDepth(
        DualSenseControllerOptions.AudioLatencyMode mode, byte expectedBuffer)
    {
        Assert.AreEqual(expectedBuffer,
            DualSenseHapticsStreamer.GetLatencyProfile(mode).ControllerBuffer);
    }

    [DataTestMethod]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.LowLatency)]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.Balanced)]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.Smooth)]
    public void GetLatencyProfile_LeavesAdaptiveRingHeadroom(
        DualSenseControllerOptions.AudioLatencyMode mode)
    {
        var profile = DualSenseHapticsStreamer.GetLatencyProfile(mode);
        int ringFrames = profile.AudioRingSamples / 960;

        // The adaptive prebuffer escalates up to ringFrames - 4; the base
        // target must leave at least that headroom to escalate into.
        Assert.IsTrue(profile.PrebufferFrames + 4 <= ringFrames,
            $"{mode}: prebuffer {profile.PrebufferFrames}f has no headroom in {ringFrames}f ring");
    }

    [TestMethod]
    public void ShouldTransmitCaptureTick_SendsSixTailReportsThenGates()
    {
        int silenceTail = 0;
        for (int i = 0; i < DualSenseHapticsStreamer.SILENCE_TAIL_REPORTS; i++)
        {
            Assert.IsTrue(DualSenseHapticsStreamer.ShouldTransmitCaptureTick(
                false, false, ref silenceTail));
        }

        Assert.IsFalse(DualSenseHapticsStreamer.ShouldTransmitCaptureTick(
            false, false, ref silenceTail));

        Assert.IsTrue(DualSenseHapticsStreamer.ShouldTransmitCaptureTick(
            true, false, ref silenceTail));
        Assert.AreEqual(0, silenceTail);

        Assert.IsTrue(DualSenseHapticsStreamer.ShouldTransmitCaptureTick(
            false, true, ref silenceTail));
        Assert.AreEqual(0, silenceTail);
    }

    [DataTestMethod]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.LowLatency, (byte)32)]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.Balanced, (byte)64)]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.Smooth, (byte)120)]
    public void BuildHapticsReport_UsesExpectedProtocolLayoutAndBufferDepth(
        DualSenseControllerOptions.AudioLatencyMode latencyMode, byte expectedBuffer)
    {
        DualSenseHapticsStreamer streamer = CreateStreamer(latencyMode,
            DualSenseControllerOptions.AudioOutputRoute.Speaker);
        byte[] chunk = Enumerable.Range(0, 64).Select(i => (byte)(0x80 + i)).ToArray();
        byte[] report = new byte[398];

        streamer.BuildHapticsReport(report, chunk);

        Assert.AreEqual((byte)0x36, report[0]);
        Assert.AreEqual((byte)0x00, report[1]);
        Assert.AreEqual((byte)0x91, report[2]);
        Assert.AreEqual((byte)0x07, report[3]);
        Assert.AreEqual((byte)0xFE, report[4]);
        CollectionAssert.AreEqual(Enumerable.Repeat(expectedBuffer, 5).ToArray(),
            report[5..10]);
        Assert.AreEqual((byte)0x01, report[10]);
        Assert.AreEqual((byte)0x90, report[11]);
        Assert.AreEqual((byte)63, report[12]);
        Assert.AreEqual((byte)0x92, report[76]);
        Assert.AreEqual((byte)64, report[77]);
        CollectionAssert.AreEqual(chunk.Select(sample => (byte)(sample ^ 0x80)).ToArray(),
            report[78..142]);
        CollectionAssert.AreEqual(new byte[252], report[142..394]);
        AssertValidReportCrc(report);
    }

    [DataTestMethod]
    [DataRow(DualSenseControllerOptions.AudioOutputRoute.Speaker, (byte)0x93)]
    [DataRow(DualSenseControllerOptions.AudioOutputRoute.Headphone, (byte)0x96)]
    public void BuildAudioReport_UsesExpectedRoutePayloadPaddingAndCrc(
        DualSenseControllerOptions.AudioOutputRoute route, byte expectedRoute)
    {
        DualSenseHapticsStreamer streamer = CreateStreamer(
            DualSenseControllerOptions.AudioLatencyMode.Balanced, route);
        byte[] chunk = Enumerable.Repeat((byte)0x80, 64).ToArray();
        byte[] opus = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        byte[] report = new byte[398];

        streamer.BuildAudioReport(report, chunk, opus);

        Assert.AreEqual((byte)0x36, report[0]);
        Assert.AreEqual(expectedRoute, report[142]);
        Assert.AreEqual((byte)200, report[143]);
        CollectionAssert.AreEqual(opus, report[144..344]);
        CollectionAssert.AreEqual(new byte[50], report[344..394]);
        AssertValidReportCrc(report);
    }

    [TestMethod]
    public void IsSupportedLoopbackFormat_AcceptsFloatAndRejectsPcm()
    {
        Assert.IsTrue(DualSenseHapticsStreamer.IsSupportedLoopbackFormat(
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
        Assert.IsTrue(DualSenseHapticsStreamer.IsSupportedLoopbackFormat(
            new WaveFormatExtensible(48000, 32, 2)));
        Assert.IsFalse(DualSenseHapticsStreamer.IsSupportedLoopbackFormat(
            new WaveFormat(48000, 16, 2)));
        Assert.IsFalse(DualSenseHapticsStreamer.IsSupportedLoopbackFormat(null));
    }

    private static DualSenseHapticsStreamer CreateStreamer(
        DualSenseControllerOptions.AudioLatencyMode latencyMode,
        DualSenseControllerOptions.AudioOutputRoute route)
    {
        Crc32Algorithm.InitializeTable(Crc32Algorithm.DefaultPolynomial);
        DualSenseHapticsStreamer streamer = new DualSenseHapticsStreamer(null, null);
        streamer.Configure(DualSenseControllerOptions.HapticsMode.Off,
            3.0, 350, string.Empty, false, route, 50, latencyMode);
        return streamer;
    }

    private static void AssertValidReportCrc(byte[] report)
    {
        int crcOffset = report.Length - sizeof(uint);
        uint expected = ~Crc32Algorithm.Compute(new byte[] { 0xA2 });
        expected = ~Crc32Algorithm.CalculateBasicHash(
            ref expected, ref report, 0, crcOffset);
        uint actual = BitConverter.ToUInt32(report, crcOffset);

        Assert.AreEqual(expected, actual);
    }
}
