using System;
using System.Collections.Generic;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests
{
    public sealed class AudioDeviceTests
    {
        [Fact]
        public void ProjectFiltersInactiveDevicesAndMarksDefault()
        {
            var devices = AudioDevicePolicy.Project(new[]
            {
                new AudioDeviceCandidate { Id = "a", Name = "Speakers", IsActive = true },
                new AudioDeviceCandidate { Id = "b", Name = "Disconnected", IsActive = false },
                new AudioDeviceCandidate { Id = "c", Name = "Headset", IsActive = true }
            }, "c", name => name == "Headset", name => name == "Speakers" ? 2 : 0);

            Assert.Equal(2, devices.Count);
            Assert.False(devices[0].IsDefault);
            Assert.Equal(2, devices[0].HotkeyIndex);
            Assert.True(devices[1].IsDefault);
            Assert.True(devices[1].IsHidden);
        }

        [Fact]
        public void VolumeClampHandlesBoundsAndNaN()
        {
            Assert.Equal(0f, VolumeControl.Clamp(-1f));
            Assert.Equal(1f, VolumeControl.Clamp(2f));
            Assert.Equal(0.4f, VolumeControl.Clamp(0.4f));
            Assert.Equal(0f, VolumeControl.Clamp(float.NaN));
        }

        [Fact]
        public void NotificationGateDebouncesAndStopsExactlyOnce()
        {
            int calls = 0;
            using (var gate = new AudioNotificationGate(() => calls++))
            {
                Assert.True(gate.TryQueue());
                Assert.False(gate.TryQueue());
                gate.Drain();
                Assert.Equal(1, calls);
                Assert.True(gate.TryQueue());
                gate.Dispose();
                gate.Drain();
                Assert.Equal(1, calls);
                Assert.False(gate.TryQueue());
            }
        }

        [Fact]
        public void DefaultPolicyCallsAllRolesAfterMiddleFailure()
        {
            var calls = new List<AudioDeviceRole>();
            bool result = AudioDefaultEndpointPolicy.Apply("endpoint", role =>
            {
                calls.Add(role);
                return role == AudioDeviceRole.Multimedia ? unchecked((int)0x80004005) : 0;
            });

            Assert.False(result);
            Assert.Equal(new[] { AudioDeviceRole.Console, AudioDeviceRole.Multimedia, AudioDeviceRole.Communications }, calls);

            calls.Clear();
            Assert.False(AudioDefaultEndpointPolicy.Apply("endpoint", role =>
            {
                calls.Add(role);
                if (role == AudioDeviceRole.Multimedia) throw new InvalidOperationException();
                return 0;
            }));
            Assert.Equal(3, calls.Count);
        }

        [Fact]
        public void VolumeSessionRecreatesEndpointAfterDisappearance()
        {
            int created = 0;
            var first = new FakeVolumeEndpoint { Volume = 0.25f };
            var second = new FakeVolumeEndpoint { Volume = 0.75f };
            var session = new AudioVolumeSession(() => ++created == 1 ? first : second);

            Assert.Equal(0.25f, session.GetVolume());
            first.ThrowOnRead = true;
            Assert.Equal(0f, session.GetVolume());
            Assert.Equal(0.75f, session.GetVolume());
            Assert.True(session.TrySetVolume(2f));
            Assert.Equal(1f, second.Volume);
            Assert.True(session.TrySetMute(true));
            Assert.True(second.Mute);
        }

        sealed class FakeVolumeEndpoint : IAudioEndpointVolumeSession
        {
            public float Volume { get; set; }
            public bool Mute { get; set; }
            public bool ThrowOnRead { get; set; }
            float IAudioEndpointVolumeSession.Volume
            {
                get { if (ThrowOnRead) throw new InvalidOperationException(); return Volume; }
                set { if (ThrowOnRead) throw new InvalidOperationException(); Volume = value; }
            }
        }
    }
}
