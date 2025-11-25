// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Statistics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ManagedBass;
using ManagedBass; // Changed from Wasapi
using ManagedBass.Fx;  // Added for splitters
using ManagedBass.Mix;
using ManagedBass.Asio;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Logging;
using osu.Framework.Platform.Linux.Native;

namespace osu.Framework.Threading
{
    public class AudioThread : GameThread
    {
        public AudioThread()
            : base(name: "Audio")
        {
            OnNewFrame += onNewFrame;
            PreloadBass();
        }

        public override bool IsCurrent => ThreadSafety.IsAudioThread;

        internal sealed override void MakeCurrent()
        {
            base.MakeCurrent();

            ThreadSafety.IsAudioThread = true;
        }

        internal override IEnumerable<StatisticsCounterType> StatisticsCounters => new[]
        {
            StatisticsCounterType.TasksRun,
            StatisticsCounterType.Tracks,
            StatisticsCounterType.Samples,
            StatisticsCounterType.SChannels,
            StatisticsCounterType.Components,
            StatisticsCounterType.MixChannels,
        };

        private readonly List<AudioManager> managers = new List<AudioManager>();

        private static readonly HashSet<int> initialised_devices = new HashSet<int>();

        private static readonly GlobalStatistic<double> cpu_usage = GlobalStatistics.Get<double>("Audio", "Bass CPU%");

        private long frameCount;

        private void onNewFrame()
        {
            if (frameCount++ % 1000 == 0)
                cpu_usage.Value = Bass.CPUUsage;

            lock (managers)
            {
                for (int i = 0; i < managers.Count; i++)
                {
                    var m = managers[i];
                    m.Update();
                }
            }
        }

        internal void RegisterManager(AudioManager manager)
        {
            lock (managers)
            {
                if (managers.Contains(manager))
                    throw new InvalidOperationException($"{manager} was already registered");

                managers.Add(manager);
            }

            manager.GlobalMixerHandle.BindTo(globalMixerHandle);
        }

        internal void UnregisterManager(AudioManager manager)
        {
            lock (managers)
                managers.Remove(manager);

            manager.GlobalMixerHandle.UnbindFrom(globalMixerHandle);
        }

        protected override void OnExit()
        {
            base.OnExit();

            lock (managers)
            {
                // AudioManagers are iterated over backwards since disposal will unregister and remove them from the list.
                for (int i = managers.Count - 1; i >= 0; i--)
                {
                    var m = managers[i];

                    m.Dispose();

                    // Audio component disposal (including the AudioManager itself) is scheduled and only runs when the AudioThread updates.
                    // But the AudioThread won't run another update since it's exiting, so an update must be performed manually in order to finish the disposal.
                    m.Update();
                }

                managers.Clear();
            }

            // Safety net to ensure we have freed all devices before exiting.
            // This is mainly required for device-lost scenarios.
            // See https://github.com/ppy/osu-framework/pull/3378 for further discussion.
            foreach (int d in initialised_devices.ToArray())
                FreeDevice(d);
        }

        #region BASS Initialisation

        // TODO: All this bass init stuff should probably not be in this class.

        // BASSASIO uses non-interleaved channels, so we need splitters to map the mixer
        private readonly List<int> asioSplitters = new List<int>();
        private AsioNotifyProcedure? asioNotifyProcedure;

        /// <summary>
        /// If a global mixer is being used, this will be the BASS handle for it.
        /// If non-null, all game mixers should be added to this mixer.
        /// </summary>
        private readonly Bindable<int?> globalMixerHandle = new Bindable<int?>();

        internal bool InitDevice(int deviceId, bool useAsio) // Renamed parameter
        {
            Debug.Assert(ThreadSafety.IsAudioThread);
            Trace.Assert(deviceId != -1); // The real device ID should always be used, as the -1 device has special cases which are hard to work with.

            // Try to initialise the device, or request a re-initialise.
            if (!Bass.Init(deviceId, Flags: (DeviceInitFlags)128)) // 128 == BASS_DEVICE_REINIT
                return false;

            if (useAsio)
                attemptAsioInitialisation();
            else
                freeAsio();

            initialised_devices.Add(deviceId);
            return true;
        }

        internal void FreeDevice(int deviceId)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);

            int selectedDevice = Bass.CurrentDevice;

            if (canSelectDevice(deviceId))
            {
                Bass.CurrentDevice = deviceId;
                Bass.Free();
            }

            freeAsio(); // Renamed method

            if (selectedDevice != deviceId && canSelectDevice(selectedDevice))
                Bass.CurrentDevice = selectedDevice;

            initialised_devices.Remove(deviceId);

            static bool canSelectDevice(int deviceId) => Bass.GetDeviceInfo(deviceId, out var deviceInfo) && deviceInfo.IsInitialized;
        }

        /// <summary>
        /// Makes BASS available to be consumed.
        /// </summary>
        internal static void PreloadBass()
        {
            if (RuntimeInfo.OS == RuntimeInfo.Platform.Linux)
            {
                // required for the time being to address libbass_fx.so load failures (see https://github.com/ppy/osu/issues/2852)
                Library.Load("libbass.so", Library.LoadFlags.RTLD_LAZY | Library.LoadFlags.RTLD_GLOBAL);
            }
        }

       private bool attemptAsioInitialisation()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return false;

            Logger.Log("Attempting local BassAsio initialisation");

            int asioDevice = -1;
            if (Bass.CurrentDevice > 0)
            {
                string driver = Bass.GetDeviceInfo(Bass.CurrentDevice).Driver;

                if (!string.IsNullOrEmpty(driver))
                {
                    int currentAsioIndex = 0;
                    while (BassAsio.GetDeviceInfo(currentAsioIndex, out AsioDeviceInfo info))
                    {
                        if (info.Name == driver)
                        {
                            asioDevice = currentAsioIndex;
                            Logger.Log($"Matched BASS device driver '{driver}' to ASIO device {asioDevice} ('{info.Name}')");
                            break;
                        }
                        currentAsioIndex++;
                    }

                    if (asioDevice == -1)
                        Logger.Log($"BASS device driver '{driver}' has no matching ASIO device. Falling back to default ASIO device.");
                }
                else
                {
                    Logger.Log("Current BASS device has no driver name, cannot match to ASIO device. Falling back to default ASIO device.");
                }
            }
            else
            {
                Logger.Log("Current BASS device is No-Sound, falling back to default ASIO device.");
            }


            freeAsio();
            return initAsio(0);
        }

      private bool initAsio(int asioDevice)
        {
            // This is intentionally initialised inline and stored to a field...
            asioNotifyProcedure = (notify, _) => Scheduler.Add(() =>
            {
                Logger.Log($"BASSASIO notification received: {notify}");
                if (notify == AsioNotify.Reset)
                {
                    Logger.Log("ASIO device reset, freeing resources. Device must be re-initialised externally.");
                    freeAsio();
                }
            });

            bool initialised = BassAsio.Init(asioDevice, AsioInitFlags.Thread);
            Logger.Log($"Initialising BassAsio for device {asioDevice}...{(initialised ? "success!" : "FAILED")}");

            if (!initialised)
                return false;

            BassAsio.GetInfo(out var asioInfo);
            Logger.Log($"ASIO Info: Device={asioDevice}, Rate={BassAsio.Rate}, Outputs={asioInfo.Outputs}");


            globalMixerHandle.Value = BassMix.CreateMixerStream((int)BassAsio.Rate, asioInfo.Outputs, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);

            if (globalMixerHandle.Value == null)
            {
                Logger.Log($"Failed to create global mixer stream: {Bass.LastError}");
                BassAsio.Free();
                return false;
            }

            var mixerInfo = Bass.ChannelGetInfo(globalMixerHandle.Value.Value);
            Logger.Log($"Mixer stream created. Handle: {globalMixerHandle.Value.Value}, Channels: {mixerInfo.Channels}, Freq: {mixerInfo.Frequency}");
            Logger.Log("Setting mixer as source for ASIO channel 0");
            if (!BassAsio.ChannelEnableBass(false, 0, globalMixerHandle.Value.Value, true))
            {
                Logger.Log($"Failed to set ASIO channel 0 with join: {Bass.LastError}");

                Bass.StreamFree(globalMixerHandle.Value.Value);
                globalMixerHandle.Value = null;
                BassAsio.Free();
                return false;
            }

            BassAsio.Start();
            Logger.Log($"BassASIO started?: {BassAsio.IsStarted.ToString()}");
            BassAsio.SetNotify(asioNotifyProcedure);
            return true;
        }

        private void freeAsio()
        {
            if (globalMixerHandle.Value == null) return;

            BassAsio.Stop();
            BassAsio.SetNotify(null);
            foreach (int splitter in asioSplitters)
                Bass.StreamFree(splitter);
            asioSplitters.Clear();
            Bass.StreamFree(globalMixerHandle.Value.Value);
            BassAsio.Free();
            globalMixerHandle.Value = null;
        }

        #endregion
    }
}
