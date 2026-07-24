using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeIsoTelemetryParserTests
{
    [TestMethod]
    public void TryParse_ExtractsProductionHapticTelemetry()
    {
        const string line =
            "14:10:22.419 ISO OUT total=500 seq=28610 ep=1 urbs/s=98.7 KiB/s=370.1 packets=5000 current=10x384..384 gap-ms=9.1..17.0 rms%=0.00/0.00/15.90/15.90 peak%=0.0/0.0/35.0/35.0 start=5000 interval=1 bt36=461 btq=0 bt-underrun=90 bt-errors=0 bt-audio=420 spkq=5 spk-underrun=0";

        bool parsed = NativeModeIsoTelemetryParser.TryParse(line, out var telemetry);

        Assert.IsTrue(parsed);
        Assert.AreEqual(15.90, telemetry.Channel3RmsPercent);
        Assert.AreEqual(15.90, telemetry.Channel4RmsPercent);
        Assert.AreEqual(0L, telemetry.BluetoothErrorCount);
        Assert.IsTrue(telemetry.HasNativeHapticSignal);
    }

    [TestMethod]
    public void TryParse_SpeakerOnlyEnergyIsNotNativeHapticSignal()
    {
        const string line =
            "14:10:22.419 ISO OUT total=500 rms%=31.25/18.75/0.00/0.00 peak%=50.0/40.0/0.0/0.0 bt-errors=2";

        bool parsed = NativeModeIsoTelemetryParser.TryParse(line, out var telemetry);

        Assert.IsTrue(parsed);
        Assert.AreEqual(0.0, telemetry.Channel3RmsPercent);
        Assert.AreEqual(0.0, telemetry.Channel4RmsPercent);
        Assert.AreEqual(2L, telemetry.BluetoothErrorCount);
        Assert.IsFalse(telemetry.HasNativeHapticSignal);
    }

    [TestMethod]
    public void TryParse_EitherHapticChannelCanReportSignal()
    {
        const string line =
            "14:10:22.419 ISO OUT total=1 rms%=0.00/0.00/0.00/0.01 bt-errors=0";

        bool parsed = NativeModeIsoTelemetryParser.TryParse(line, out var telemetry);

        Assert.IsTrue(parsed);
        Assert.IsTrue(telemetry.HasNativeHapticSignal);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("14:10:22.419 AUDIO rms%=0.00/0.00/15.90/15.90 bt-errors=0")]
    [DataRow("14:10:22.419 ISO OUT total=1 bt-errors=0")]
    [DataRow("14:10:22.419 ISO OUT total=1 rms%=0.00/0.00/15.90 bt-errors=0")]
    [DataRow("14:10:22.419 ISO OUT total=1 rms%=0.00/0.00/nope/15.90 bt-errors=0")]
    [DataRow("14:10:22.419 ISO OUT total=1 rms%=0.00/0.00/15.90/15.90")]
    [DataRow("14:10:22.419 ISO OUT total=1 rms%=0.00/0.00/15.90/15.90 bt-errors=-1")]
    public void TryParse_RejectsNonProductionOrMalformedTelemetry(string line)
    {
        Assert.IsFalse(NativeModeIsoTelemetryParser.TryParse(line, out _));
    }
}
