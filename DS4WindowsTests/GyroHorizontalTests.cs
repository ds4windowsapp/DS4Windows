using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.DS4Control;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests
{
    [TestClass]
    [DoNotParallelize]
    public class GyroHorizontalTests
    {
        [DataTestMethod]
        [DataRow(0, 0, 300.0, 200.0, 0.0)]
        [DataRow(100, 0, 300.0, 200.0, 300.0)]
        [DataRow(0, 100, 300.0, 200.0, 200.0)]
        [DataRow(-100, 0, 300.0, 200.0, -300.0)]
        [DataRow(0, -100, 300.0, 200.0, -200.0)]
        [DataRow(100, 100, 300.0, 200.0, 500.0)]
        [DataRow(100, -100, 300.0, 300.0, 0.0)]
        [DataRow(-100, -100, 300.0, 200.0, -500.0)]
        [DataRow(1, -1, 1.0, 2.0, -0.01)]
        [DataRow(25, 50, -200.0, 300.0, 100.0)]
        public void CombinedUsesSignedWeights(int yaw, int roll, double yawVelocity,
            double rollVelocity, double expected)
        {
            var settings = new GyroHorizontalSettings { YawSensitivity = yaw, RollSensitivity = roll };
            Assert.AreEqual(expected, settings.Calculate(2, yawVelocity, rollVelocity), 0.000001);
            Assert.AreEqual(yawVelocity, settings.Calculate(0, yawVelocity, rollVelocity));
            Assert.AreEqual(rollVelocity, settings.Calculate(1, yawVelocity, rollVelocity));
        }

        private static ProfileDTO Read(string xml)
        {
            var serializer = new XmlSerializer(typeof(ProfileDTO), ProfileDTO.GetAttributeOverrides());
            using var reader = new StringReader(xml);
            return (ProfileDTO)serializer.Deserialize(reader);
        }

        [DataTestMethod]
        [DataRow(0, 0)]
        [DataRow(0, 1)]
        [DataRow(0, 2)]
        [DataRow(0, 3)]
        [DataRow(1, 0)]
        [DataRow(1, 1)]
        [DataRow(1, 2)]
        [DataRow(1, 3)]
        public void MissingWeightsUseLegacyAxisAndInversion(int axis, int invert)
        {
            var dto = Read($"<DS4Windows><GyroMouseHAxis>{axis}</GyroMouseHAxis>" +
                $"<GyroInvert>{invert}</GyroInvert><GyroMouseStickHAxis>{axis}</GyroMouseStickHAxis>" +
                $"<GyroMouseStickInvert>{invert}</GyroMouseStickInvert></DS4Windows>");
            var store = new BackingStore();
            dto.DeviceIndex = 1;
            dto.MapTo(store);

            int mouseSign = (invert & 2) != 0 ? -100 : 100;
            int stickSign = (invert & 1) != 0 ? -100 : 100;
            Assert.AreEqual(axis, store.gyroMouseHorizontalAxis[1]);
            Assert.AreEqual(axis, store.gyroMouseStickHorizontalAxis[1]);
            Assert.AreEqual(invert, store.gyroInvert[1]);
            Assert.AreEqual((uint)invert, store.gyroMStickInfo[1].inverted);
            Assert.AreEqual(axis == 0 ? mouseSign : 0, store.gyroMouseInfo[1].horizontalSettings.YawSensitivity);
            Assert.AreEqual(axis == 1 ? mouseSign : 0, store.gyroMouseInfo[1].horizontalSettings.RollSensitivity);
            Assert.AreEqual(axis == 0 ? stickSign : 0, store.gyroMStickInfo[1].horizontalSettings.YawSensitivity);
            Assert.AreEqual(axis == 1 ? stickSign : 0, store.gyroMStickInfo[1].horizontalSettings.RollSensitivity);
            Assert.AreEqual(100, store.gyroMouseInfo[0].horizontalSettings.YawSensitivity);
        }

        [DataTestMethod]
        [DataRow(-100, 100)]
        [DataRow(100, -100)]
        [DataRow(0, 0)]
        [DataRow(-37, 61)]
        [DataRow(-101, 101)]
        [DataRow(int.MinValue, int.MaxValue)]
        public void ProfileRoundTripsAndClampsWeights(int yaw, int roll)
        {
            var dto = Read($"<DS4Windows><GyroMouseHAxis>2</GyroMouseHAxis>" +
                $"<GyroMouseYawSensitivity>{yaw}</GyroMouseYawSensitivity>" +
                $"<GyroMouseRollSensitivity>{roll}</GyroMouseRollSensitivity>" +
                "<GyroMouseStickHAxis>2</GyroMouseStickHAxis>" +
                $"<GyroMouseStickYawSensitivity>{roll}</GyroMouseStickYawSensitivity>" +
                $"<GyroMouseStickRollSensitivity>{yaw}</GyroMouseStickRollSensitivity></DS4Windows>");
            var store = new BackingStore();
            dto.DeviceIndex = 1;
            dto.MapTo(store);
            var saved = new ProfileDTO { DeviceIndex = 1 };
            saved.MapFrom(store);
            using var writer = new StringWriter();
            new XmlSerializer(typeof(ProfileDTO), ProfileDTO.GetAttributeOverrides()).Serialize(writer, saved);
            var reloaded = Read(writer.ToString());
            var destination = new BackingStore();
            reloaded.DeviceIndex = 1;
            reloaded.MapTo(destination);

            Assert.AreEqual(2, destination.gyroMouseHorizontalAxis[1]);
            Assert.AreEqual(2, destination.gyroMouseStickHorizontalAxis[1]);
            Assert.AreEqual(Math.Clamp(yaw, -100, 100), destination.gyroMouseInfo[1].horizontalSettings.YawSensitivity);
            Assert.AreEqual(Math.Clamp(roll, -100, 100), destination.gyroMouseInfo[1].horizontalSettings.RollSensitivity);
            Assert.AreEqual(Math.Clamp(roll, -100, 100), destination.gyroMStickInfo[1].horizontalSettings.YawSensitivity);
            Assert.AreEqual(Math.Clamp(yaw, -100, 100), destination.gyroMStickInfo[1].horizontalSettings.RollSensitivity);
        }

        [TestMethod]
        public void PartialFieldsAndProfileResetDoNotReusePreviousWeights()
        {
            var store = new BackingStore();
            Assert.AreEqual(-100, store.gyroMouseInfo[0].horizontalSettings.RollSensitivity);
            Assert.AreEqual(-100, store.gyroMStickInfo[0].horizontalSettings.RollSensitivity);
            store.gyroMouseInfo[0].horizontalSettings.Load(0, false, 55, 66);
            store.gyroMStickInfo[0].horizontalSettings.Load(0, false, 77, 88);
            var dto = Read("<DS4Windows><GyroMouseHAxis>1</GyroMouseHAxis><GyroInvert>2</GyroInvert>" +
                "<GyroMouseYawSensitivity>0</GyroMouseYawSensitivity>" +
                "<GyroMouseStickHAxis>1</GyroMouseStickHAxis><GyroMouseStickInvert>1</GyroMouseStickInvert>" +
                "<GyroMouseStickRollSensitivity>0</GyroMouseStickRollSensitivity></DS4Windows>");
            dto.DeviceIndex = 0;
            dto.MapTo(store);
            Assert.AreEqual(0, store.gyroMouseInfo[0].horizontalSettings.YawSensitivity);
            Assert.AreEqual(-100, store.gyroMouseInfo[0].horizontalSettings.RollSensitivity);
            Assert.AreEqual(0, store.gyroMStickInfo[0].horizontalSettings.YawSensitivity);
            Assert.AreEqual(0, store.gyroMStickInfo[0].horizontalSettings.RollSensitivity);

            store.gyroMouseInfo[0].Reset();
            store.gyroMStickInfo[0].Reset();
            Assert.AreEqual(100, store.gyroMouseInfo[0].horizontalSettings.YawSensitivity);
            Assert.AreEqual(-100, store.gyroMouseInfo[0].horizontalSettings.RollSensitivity);
            Assert.AreEqual(100, store.gyroMStickInfo[0].horizontalSettings.YawSensitivity);
            Assert.AreEqual(-100, store.gyroMStickInfo[0].horizontalSettings.RollSensitivity);
        }

        private sealed class SensorDevice : DS4Device
        {
            // No HID handle is opened: only managed sensor state is used in these tests.
            public SensorDevice() : base((HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice)), "test") { }
            public override GyroMouseSens GyroMouseSensSettings => new GyroMouseSens();
        }

        private sealed class MouseOutput : VirtualKBMBase
        {
            public int X, Y;
            public override void MoveRelativeMouse(int x, int y) { X += x; Y += y; }
            public override bool Connect() => true;
            public override bool Disconnect() => true;
            public override void MoveAbsoluteMouse(double x, double y) { }
            public override void PerformMouseWheelEvent(int vertical, int horizontal) { }
            public override void PerformMouseButtonEvent(uint mouseButton) { }
            public override void PerformMouseButtonPress(uint mouseButton) { }
            public override void PerformMouseButtonRelease(uint mouseButton) { }
            public override void PerformKeyPress(uint key) { }
            public override void PerformKeyPressAlt(uint key) { }
            public override void PerformKeyRelease(uint key) { }
            public override void PerformKeyReleaseAlt(uint key) { }
            public override string GetDisplayName() => "test";
            public override string GetIdentifier() => "test";
            public override string GetFullDisplayName() => "test";
        }

        private static (int X, int Y) RunPipeline(bool stick, int axis, int yawWeight, int rollWeight,
            int yaw, int roll, int pitch, int invert, int smoothing = 0, bool active = true)
        {
            const int slot = 1;
            Global.GyroMouseInfo[slot].Reset();
            Global.GyroMouseStickInf[slot].Reset();
            Global.GyroMouseHorizontalAxis[slot] = axis;
            Global.GyroMouseStickHorizontalAxis[slot] = axis;
            Global.GyroMouseStickHorizontalAxis[0] = 0;
            Global.GyroInvert[slot] = invert;
            Global.GyroSensitivity[slot] = 100;
            Global.GyroSensVerticalScale[slot] = 100;
            Global.GyroOutputMode[slot] = stick ? GyroOutMode.MouseJoystick : GyroOutMode.Mouse;
            Global.GyroMouseInfo[slot].horizontalSettings.Load(0, false, yawWeight, rollWeight);
            var info = Global.GyroMouseStickInf[slot];
            info.horizontalSettings.Load(0, false, yawWeight, rollWeight);
            info.inverted = (uint)invert;
            info.useSmoothing = smoothing != 0;
            info.smoothingMethod = (GyroMouseStickInfo.SmoothingMethod)smoothing;
            Global.GyroMouseInfo[slot].enableSmoothing = smoothing != 0;
            Global.GyroMouseInfo[slot].smoothingMethod = (GyroMouseInfo.SmoothingMethod)smoothing;
            Global.GyroTriggerTurns[slot] = active;
            Global.GyroMouseStickTriggerTurns[slot] = active;
            Global.SATriggers[slot] = "-1";
            Global.SAMousestickTriggers[slot] = "-1";

            var output = new MouseOutput();
            var previousOutput = Global.outputKBMHandler;
            Global.outputKBMHandler = output;
            try
            {
                var mouse = new Mouse(slot, new SensorDevice());
                var args = new SixAxisEventArgs(DateTime.UtcNow,
                    new SixAxis(-yaw, pitch, -roll, 0, 0, 0, 0.004));
                for (int i = 0; i < 5; i++)
                {
                    Mapping.mapStickActionData[slot].Reset();
                    Mapping.gyroStickX[slot] = Mapping.gyroStickY[slot] = 128;
                    mouse.sixaxisMoved(null, args);
                }

                return stick ? (Mapping.gyroStickX[slot], Mapping.gyroStickY[slot]) : (output.X, output.Y);
            }
            finally
            {
                Global.outputKBMHandler = previousOutput;
                Global.GyroMouseInfo[slot].RemoveRefreshEvents();
            }
        }

        [DataTestMethod]
        [DataRow(false, 0)]
        [DataRow(true, 0)]
        [DataRow(false, 1)]
        [DataRow(true, 1)]
        [DataRow(false, 2)]
        [DataRow(true, 2)]
        public void CombinedIsProcessedOnceAndLegacySingleAxesMatch(bool stick, int smoothing)
        {
            var summed = RunPipeline(stick, 2, 100, 100, 180, 140, 80, 0, smoothing);
            var equivalent = RunPipeline(stick, 0, 0, 0, 320, 0, 80, 0, smoothing);
            Assert.AreEqual(equivalent.X, summed.X);
            Assert.AreEqual(equivalent.Y, summed.Y);

            foreach (int axis in new[] { 0, 1 })
            {
                var legacy = RunPipeline(stick, axis, 0, 0, 180, 140, 80, 0, smoothing);
                var combined = RunPipeline(stick, 2, axis == 0 ? 100 : 0, axis == 1 ? 100 : 0,
                    180, 140, 80, 0, smoothing);
                Assert.IsTrue(Math.Abs(legacy.X - combined.X) <= 1);
                Assert.IsTrue(Math.Abs(legacy.Y - combined.Y) <= 1);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CombinedSignsCancellationVerticalAndActivationArePreserved(bool stick)
        {
            int neutral = stick ? 128 : 0;
            var cancelled = RunPipeline(stick, 2, 100, -100, 180, 180, 0, 0);
            Assert.AreEqual(neutral, cancelled.X);
            Assert.AreEqual(neutral, cancelled.Y);
            Assert.AreEqual(neutral, RunPipeline(stick, 2, 0, 0, 180, 180, 0, 0).X);

            var positive = RunPipeline(stick, 2, 100, 0, 180, 0, 80, 0);
            var negative = RunPipeline(stick, 2, -100, 0, 180, 0, 80, 0);
            Assert.IsTrue(positive.X > neutral);
            Assert.IsTrue(negative.X < neutral);
            Assert.AreEqual(positive.Y, negative.Y);

            var legacyInvert = RunPipeline(stick, 0, 0, 0, 180, 0, 80, stick ? 1 : 2);
            Assert.IsTrue(legacyInvert.X < neutral);
            var ignoredInvert = RunPipeline(stick, 2, 100, 0, 180, 0, 80, stick ? 1 : 2);
            Assert.AreEqual(positive.X, ignoredInvert.X);
            var verticalInvert = RunPipeline(stick, 2, 100, 0, 180, 0, 80, stick ? 2 : 1);
            Assert.IsTrue((positive.Y - neutral) * (verticalInvert.Y - neutral) < 0);

            var inactive = RunPipeline(stick, 2, 100, 100, 180, 140, 80, 0, active: false);
            Assert.AreEqual(neutral, inactive.X);
            Assert.AreEqual(neutral, inactive.Y);
        }
    }
}
