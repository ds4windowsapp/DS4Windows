using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests;

[TestClass]
public class DualSenseControllerOptionsTests
{
    [TestMethod]
    public void BluetoothStreamingDefaults_AreSafeAndUseHalfVolume()
    {
        DualSenseControllerOptions options =
            new DualSenseControllerOptions(InputDeviceType.DualSense);
        DualSenseControllerOptsDTO dto = new DualSenseControllerOptsDTO();

        Assert.AreEqual(DualSenseControllerOptions.HapticsMode.Off,
            options.BTHapticsMode);
        Assert.IsFalse(options.BTAudioEnabled);
        Assert.AreEqual(50, options.BTAudioVolume);

        Assert.AreEqual(DualSenseControllerOptions.HapticsMode.Off,
            dto.BTHapticsMode);
        Assert.IsFalse(dto.BTAudioEnabled);
        Assert.AreEqual(50, dto.BTAudioVolume);
    }

    [TestMethod]
    public void DtoMapping_PreservesBluetoothStreamingSettings()
    {
        DualSenseControllerOptions source =
            new DualSenseControllerOptions(InputDeviceType.DualSense)
        {
            BTHapticsMode = DualSenseControllerOptions.HapticsMode.Mix,
            BTHapticsGain = 4.5,
            BTHapticsLowPassHz = 420,
            BTHapticsAudioDeviceId = "endpoint-id",
            BTAudioEnabled = true,
            BTAudioRoute = DualSenseControllerOptions.AudioOutputRoute.Speaker,
            BTAudioVolume = 37,
            BTAudioLatency = DualSenseControllerOptions.AudioLatencyMode.LowLatency,
        };
        DualSenseControllerOptsDTO dto = new DualSenseControllerOptsDTO();
        DualSenseControllerOptions destination =
            new DualSenseControllerOptions(InputDeviceType.DualSense);

        dto.MapFrom(source);
        dto.MapTo(destination);

        Assert.AreEqual(source.BTHapticsMode, destination.BTHapticsMode);
        Assert.AreEqual(source.BTHapticsGain, destination.BTHapticsGain);
        Assert.AreEqual(source.BTHapticsLowPassHz, destination.BTHapticsLowPassHz);
        Assert.AreEqual(source.BTHapticsAudioDeviceId, destination.BTHapticsAudioDeviceId);
        Assert.AreEqual(source.BTAudioEnabled, destination.BTAudioEnabled);
        Assert.AreEqual(source.BTAudioRoute, destination.BTAudioRoute);
        Assert.AreEqual(source.BTAudioVolume, destination.BTAudioVolume);
        Assert.AreEqual(source.BTAudioLatency, destination.BTAudioLatency);
    }
}
