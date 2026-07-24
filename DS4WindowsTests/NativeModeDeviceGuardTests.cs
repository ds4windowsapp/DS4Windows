using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeDeviceGuardTests
{
    private const string MacAddress = "AA:BB:CC:DD:EE:FF";
    private const string DevicePath =
        @"\\?\hid#vid_054c&pid_0ce6#8&1234567&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    [TestMethod]
    public void Activate_SuppressesMatchingMacOrPathCaseInsensitively()
    {
        var guard = new NativeModeDeviceGuard();

        guard.Activate(MacAddress, DevicePath);

        Assert.IsTrue(guard.ShouldSuppress("aa:bb:cc:dd:ee:ff", null));
        Assert.IsTrue(guard.ShouldSuppress(null, DevicePath.ToUpperInvariant()));
        Assert.IsTrue(guard.ShouldSuppressPath(DevicePath.ToUpperInvariant()));
        Assert.IsFalse(guard.ShouldSuppress("11:22:33:44:55:66", @"\\?\hid#other"));
    }

    [TestMethod]
    public void Clear_AllowsControllerAgain()
    {
        var guard = new NativeModeDeviceGuard();
        guard.Activate(MacAddress, DevicePath);

        guard.Clear();

        Assert.IsFalse(guard.IsActive);
        Assert.IsFalse(guard.ShouldSuppress(MacAddress, DevicePath));
    }

    [TestMethod]
    public void Activate_TrimsIdentifiers()
    {
        var guard = new NativeModeDeviceGuard();

        guard.Activate($"  {MacAddress}  ", $"  {DevicePath}  ");

        Assert.AreEqual(MacAddress, guard.MacAddress);
        Assert.IsTrue(guard.ShouldSuppress(MacAddress, DevicePath));
    }

    [TestMethod]
    public void Activate_RejectsSecondReservation()
    {
        var guard = new NativeModeDeviceGuard();
        guard.Activate(MacAddress, DevicePath);

        Assert.ThrowsException<InvalidOperationException>(() =>
            guard.Activate("11:22:33:44:55:66", @"\\?\hid#other"));
    }

    [DataTestMethod]
    [DataRow(null, DevicePath)]
    [DataRow("", DevicePath)]
    [DataRow(MacAddress, null)]
    [DataRow(MacAddress, "  ")]
    public void Activate_RequiresBothIdentifiers(string macAddress, string devicePath)
    {
        var guard = new NativeModeDeviceGuard();

        Assert.ThrowsException<ArgumentException>(() =>
            guard.Activate(macAddress, devicePath));
    }
}
