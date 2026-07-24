/*
DS4Windows
Copyright (C) 2023  Travis Nickles

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
using System.Xml;
using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.InputDevices;
using static DS4Windows.DualSenseControllerOptions;

namespace DS4WinWPF.DS4Control.DTOXml
{
    [XmlRoot(DualSenseControllerOptions.XML_ELEMENT_NAME)]
    public class DualSenseControllerOptsDTO : IDTO<DualSenseControllerOptions>
    {
        [XmlElement("LEDBarMode")]
        public LEDBarMode LEDMode
        {
            get; set;
        }

        [XmlElement("MuteLEDMode")]
        public MuteLEDMode MuteLedMode
        {
            get; set;
        }

        [XmlElement("BTHapticsMode")]
        public HapticsMode BTHapticsMode
        {
            get; set;
        } = HapticsMode.Off;

        [XmlElement("BTHapticsGain")]
        public double BTHapticsGain
        {
            get; set;
        } = 3.0;

        [XmlElement("BTHapticsLowPassHz")]
        public int BTHapticsLowPassHz
        {
            get; set;
        } = 350;

        [XmlElement("BTHapticsAudioDeviceId")]
        public string BTHapticsAudioDeviceId
        {
            get; set;
        } = string.Empty;

        [XmlElement("BTAudioEnabled")]
        public bool BTAudioEnabled
        {
            get; set;
        } = false;

        [XmlElement("BTAudioRoute")]
        public AudioOutputRoute BTAudioRoute
        {
            get; set;
        } = AudioOutputRoute.Auto;

        [XmlElement("BTAudioVolume")]
        public int BTAudioVolume
        {
            get; set;
        } = 50;

        [XmlElement("BTAudioLatency")]
        public AudioLatencyMode BTAudioLatency
        {
            get; set;
        } = AudioLatencyMode.Smooth;

        [XmlElement("NativeModeSpeakerAudio")]
        public bool NativeModeSpeakerAudio
        {
            get; set;
        } = true;

        [XmlElement("NativeModeSpeakerVolume")]
        public int NativeModeSpeakerVolume
        {
            get; set;
        } = DualSenseControllerOptions.DEFAULT_NATIVE_MODE_SPEAKER_VOLUME;

        [XmlElement("NativeModeRoute")]
        public AudioOutputRoute NativeModeRoute
        {
            get; set;
        } = AudioOutputRoute.Auto;

        public void MapFrom(DualSenseControllerOptions source)
        {
            LEDMode = source.LedMode;
            MuteLedMode = source.MuteLedMode;
            BTHapticsMode = source.BTHapticsMode;
            BTHapticsGain = source.BTHapticsGain;
            BTHapticsLowPassHz = source.BTHapticsLowPassHz;
            BTHapticsAudioDeviceId = source.BTHapticsAudioDeviceId;
            BTAudioEnabled = source.BTAudioEnabled;
            BTAudioRoute = source.BTAudioRoute;
            BTAudioVolume = source.BTAudioVolume;
            BTAudioLatency = source.BTAudioLatency;
            NativeModeSpeakerAudio = source.NativeModeSpeakerAudio;
            NativeModeSpeakerVolume = source.NativeModeSpeakerVolume;
            NativeModeRoute = source.NativeModeRoute;
        }

        public void MapTo(DualSenseControllerOptions destination)
        {
            destination.LedMode = LEDMode;
            destination.MuteLedMode = MuteLedMode;
            destination.BTHapticsMode = BTHapticsMode;
            destination.BTHapticsGain = BTHapticsGain;
            destination.BTHapticsLowPassHz = BTHapticsLowPassHz;
            destination.BTHapticsAudioDeviceId = BTHapticsAudioDeviceId;
            destination.BTAudioEnabled = BTAudioEnabled;
            destination.BTAudioRoute = BTAudioRoute;
            destination.BTAudioVolume = BTAudioVolume;
            destination.BTAudioLatency = BTAudioLatency;
            destination.NativeModeSpeakerAudio = NativeModeSpeakerAudio;
            destination.NativeModeSpeakerVolume = NativeModeSpeakerVolume;
            destination.NativeModeRoute = NativeModeRoute;
        }
    }
}
