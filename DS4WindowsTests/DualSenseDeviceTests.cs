using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
public class DualSenseDeviceTests
{
    [TestMethod]
    public void BtOutputControl_WhenStreaming_PreservesTriggersAndClearsRumble()
    {
        byte[] report = Enumerable.Repeat((byte)0xAA, 78).ToArray();

        DualSenseDevice.BtOutputControl control = DualSenseDevice.ApplyBtOutputControl(
            report, useRumble: true, useAccurateRumble: true,
            hapticsStreamActive: true, lightMotor: 0x33, heavyMotor: 0x44);

        Assert.AreEqual((byte)0x0C, control.EnableFlags);
        Assert.AreEqual((byte)0x02, control.PlayerLedFlags);
        Assert.IsFalse(control.WriteMotorBytes);
        Assert.AreEqual((byte)0x0C, report[2]);
        Assert.AreEqual((byte)0x00, report[4]);
        Assert.AreEqual((byte)0x00, report[5]);
        Assert.AreEqual((byte)0x02, report[40]);
    }

    [TestMethod]
    public void BtOutputControl_WhenNotStreaming_RetainsConfiguredRumble()
    {
        byte[] report = new byte[78];

        DualSenseDevice.BtOutputControl control = DualSenseDevice.ApplyBtOutputControl(
            report, useRumble: true, useAccurateRumble: true,
            hapticsStreamActive: false, lightMotor: 0x33, heavyMotor: 0x44);

        Assert.AreEqual((byte)0x0F, control.EnableFlags);
        Assert.AreEqual((byte)0x06, control.PlayerLedFlags);
        Assert.IsTrue(control.WriteMotorBytes);
        Assert.AreEqual((byte)0x0F, report[2]);
        Assert.AreEqual((byte)0x33, report[4]);
        Assert.AreEqual((byte)0x44, report[5]);
        Assert.AreEqual((byte)0x06, report[40]);
    }
}
