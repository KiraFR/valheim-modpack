using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;

namespace VoiceChat
{
    public enum TransmitMode
    {
        PushToTalk,
        OpenMic
    }

    /// <summary>
    /// Experimental (step 1): proximity voice chat.
    ///
    /// - Capture and compression: Steam voice API (SteamUser.StartVoiceRecording / GetVoice). The microphone device, input
    ///   volume and transmission threshold are the ones configured in Steam (Settings > Voice), not game settings.
    /// - Transport: ZRoutedRpc, addressed to the peer owning each in-range player's ZDO. The server forwards a routed RPC
    ///   before even looking up a handler (ZRoutedRpc.RPC_RoutedRPC), so it does not need the mod.
    ///   Known limitation: the game's sockets only send reliably, and voice shares the send queue with ZDO sync
    ///   (ZDOMan.SendZDOs throttles on GetSendQueueSize). Measuring that is the goal of step 2.
    /// - Playback: a 3D AudioSource on the speaking player's head, fed by VoiceFilter from a jitter buffer. Unity's
    ///   spatialization provides the proximity effect.
    /// - Settings panel (SettingsKey, F7 by default): transmission mode, push-to-talk key, microphone test with a level
    ///   meter, and playback settings. Everything is saved to the .cfg.
    /// - Display: talking players are listed on the left of the screen, and get a microphone icon next to the name above
    ///   their head (VoiceHud).
    ///
    /// Multiplayer: nothing is written to ZDOs. Both the speaker and the listener need the mod, and Steam running:
    /// crossplay players without Steam can neither talk nor hear.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.voicechat";
        public const string PluginName = "VoiceChat";
        public const string PluginVersion = "0.2.0";

        internal const string RpcVoice = "voicechat.Voice";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<TransmitMode> Mode;
        internal static ConfigEntry<KeyboardShortcut> PushToTalkKey;
        internal static ConfigEntry<KeyboardShortcut> SettingsKey;
        internal static ConfigEntry<float> Volume;
        internal static ConfigEntry<float> MinDistance;
        internal static ConfigEntry<float> MaxDistance;
        internal static ConfigEntry<float> BufferMs;
        internal static ConfigEntry<bool> AutoGain;
        internal static ConfigEntry<bool> ShowSpeakerList;
        internal static ConfigEntry<float> SpeakerListX;
        internal static ConfigEntry<float> SpeakerListY;
        internal static ConfigEntry<bool> ShowNameplateIcon;
        internal static ConfigEntry<bool> Loopback;
        internal static ConfigEntry<bool> LogStats;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            Mode = Config.Bind("Controls", "Mode", TransmitMode.PushToTalk,
                "PushToTalk: talk while holding PushToTalkKey. OpenMic: always transmitting, Steam's voice detection only " +
                "sends audio while you speak.");
            PushToTalkKey = Config.Bind("Controls", "PushToTalkKey", new KeyboardShortcut(KeyCode.B),
                "Key to hold in order to talk (push-to-talk).");
            SettingsKey = Config.Bind("Controls", "SettingsKey", new KeyboardShortcut(KeyCode.F7),
                "Opens or closes the voice chat settings panel.");

            Volume = Config.Bind("Audio", "Volume", 1f,
                new ConfigDescription("Volume of received voices. Above 1 (100%), voices are amplified, with soft limiting " +
                    "to avoid harsh clipping.", new AcceptableValueRange<float>(0f, 4f)));
            AutoGain = Config.Bind("Audio", "AutoGain", true,
                "Evens out microphones: each player's voice is brought towards the same level, quiet ones boosted up to 6x, " +
                "loud ones left as they are. Applied on top of Volume.");
            MinDistance = Config.Bind("Audio", "MinDistance", 3f,
                new ConfigDescription("Distance (m) below which a voice plays at full volume.",
                    new AcceptableValueRange<float>(0.5f, 50f)));
            MaxDistance = Config.Bind("Audio", "MaxDistance", 40f,
                new ConfigDescription("Distance (m) beyond which a voice is neither heard nor sent.",
                    new AcceptableValueRange<float>(5f, 200f)));
            BufferMs = Config.Bind("Audio", "BufferMs", 120f,
                new ConfigDescription("Audio accumulated before a voice starts playing. Higher: fewer dropouts, more latency.",
                    new AcceptableValueRange<float>(20f, 1000f)));

            ShowSpeakerList = Config.Bind("Display", "ShowSpeakerList", true,
                "Shows who is talking in a list on the left of the screen: a microphone icon and a name per talking player, " +
                "yourself included while your microphone is open.");
            SpeakerListX = Config.Bind("Display", "SpeakerListX", 20f,
                new ConfigDescription("Distance of the speaker list from the left edge of the screen, in HUD units.",
                    new AcceptableValueRange<float>(0f, 1500f)));
            SpeakerListY = Config.Bind("Display", "SpeakerListY", 150f,
                new ConfigDescription("Height of the top of the speaker list above the middle of the screen, in HUD units " +
                    "(negative: below the middle).", new AcceptableValueRange<float>(-500f, 500f)));
            ShowNameplateIcon = Config.Bind("Display", "ShowNameplateIcon", true,
                "Shows a microphone icon next to the name above a talking player's head. The game only shows player names " +
                "within 10 m, and not for crouching players.");

            Loopback = Config.Bind("Debug", "Loopback", false,
                "Plays your own voice back on your character, without going through the network. Useful to test alone.");
            LogStats = Config.Bind("Debug", "LogStats", true,
                "Writes voice statistics to the log: a summary per phrase, and sent/received data every 10 s when there is any.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            SettingsPanel.Update();
            VoiceCapture.Update();
            VoiceHud.Update();
            VoiceStats.Update();
        }

        private void OnDestroy()
        {
            SettingsPanel.Destroy();
            VoiceHud.Destroy();
            VoiceCapture.Stop();
            _harmony?.UnpatchSelf();
        }

        /// <summary>Client with Steam initialized: the only case where voice can work.</summary>
        internal static bool SteamVoiceAvailable()
        {
            if (ZNet.instance == null || ZNet.instance.IsDedicated()) return false;
            try
            {
                return SteamManager.Initialized;
            }
            catch (Exception)
            {
                // Build without Steam (Game Pass): the native DLL is missing.
                return false;
            }
        }

        /// <summary>Same locks as the other mods: no shortcut while typing or while a menu is open.</summary>
        internal static bool CanUseInput()
        {
            if (Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible()) return false;
            if (InventoryGui.IsVisible() || Minimap.IsOpen()) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            return true;
        }

        /// <summary>
        /// Key held, without KeyboardShortcut.IsPressed(): that one fails as soon as any other key is down, so as soon
        /// as you talk while walking.
        /// </summary>
        internal static bool KeyHeld(KeyboardShortcut key)
        {
            if (key.MainKey == KeyCode.None || !Input.GetKey(key.MainKey)) return false;
            foreach (KeyCode modifier in key.Modifiers)
            {
                if (!Input.GetKey(modifier)) return false;
            }
            return true;
        }

        /// <summary>Key pressed this frame, with the same tolerance for other keys as KeyHeld.</summary>
        internal static bool KeyDown(KeyboardShortcut key)
        {
            if (key.MainKey == KeyCode.None || !Input.GetKeyDown(key.MainKey)) return false;
            foreach (KeyCode modifier in key.Modifiers)
            {
                if (!Input.GetKey(modifier)) return false;
            }
            return true;
        }

        /// <summary>Loaded player whose object is owned by the given peer (that player's client).</summary>
        internal static Player FindPlayerByPeer(long peerId)
        {
            foreach (Player player in Player.GetAllPlayers())
            {
                if (player == null || player.m_nview == null || !player.m_nview.IsValid()) continue;
                if (player.m_nview.GetZDO().GetOwner() == peerId) return player;
            }
            return null;
        }
    }

    // ================================================================== capture

    /// <summary>Reads compressed voice from Steam while the microphone is open, and sends it in small batches.</summary>
    internal static class VoiceCapture
    {
        /// <summary>Maximum accumulation time before sending: bounds the latency added on the sender side.</summary>
        private const float FlushInterval = 0.08f;

        /// <summary>
        /// After StopVoiceRecording, Steam keeps recording for a moment (people often release the key too early) and
        /// GetVoice must be called until k_EVoiceResultNotRecording. This delay is only a safeguard if that result never comes.
        /// </summary>
        private const float MaxTrailingTime = 2f;

        private static readonly byte[] ReadBuffer = new byte[8192];

        /// <summary>
        /// One entry per GetVoice call, never concatenated: each Steam output is a complete packet (header and checksum),
        /// and DecompressVoice rejects several packets glued end to end.
        /// </summary>
        private static readonly List<byte[]> Pending = new List<byte[]>();

        private static bool _recording;
        private static bool _draining;
        private static bool _steamStopped;
        private static float _stopTime = -1f;
        private static float _lastFlush;
        private static bool _steamWarned;

        /// <summary>
        /// The current phrase involves the settings panel's microphone test: it stays local until Steam has finished
        /// recording, even if the test is stopped meanwhile, so the end of a test never reaches other players.
        /// </summary>
        private static bool _testCapture;

        // Summary of the current phrase, to tell whether Steam delivers voice in bursts.
        private static float _phraseStart;
        private static float _lastData;
        private static float _maxGap;
        private static int _reads;
        private static int _bytes;

        /// <summary>True while the microphone is open (used by the on-screen indicator).</summary>
        internal static bool Transmitting => _recording;

        /// <summary>True while the microphone is open for the settings panel's test only.</summary>
        internal static bool Testing => _recording && _testCapture;

        internal static void Update()
        {
            if (!Plugin.Enabled.Value || Player.m_localPlayer == null || ZRoutedRpc.instance == null)
            {
                Stop();
                return;
            }

            if (!Plugin.SteamVoiceAvailable())
            {
                if (!_steamWarned && ZNet.instance != null && !ZNet.instance.IsDedicated())
                {
                    Plugin.Log.LogWarning("Steam is not initialized: voice chat is inactive.");
                    _steamWarned = true;
                }
                return;
            }

            float now = Time.unscaledTime;
            bool wanted = MicrophoneWanted();
            if (wanted && !_recording)
            {
                if (!_draining)
                {
                    _phraseStart = now;
                    _lastData = -1f;
                    _maxGap = 0f;
                    _reads = 0;
                    _bytes = 0;
                    _lastFlush = now;
                    _testCapture = SettingsPanel.TestActive;
                }
                // Pressed again while the recording is finishing: same phrase, simply restart the capture.
                SteamUser.StartVoiceRecording();
                _recording = true;
                _draining = false;
                _steamStopped = false;
            }
            else if (!wanted && _recording)
            {
                SteamUser.StopVoiceRecording();
                _recording = false;
                _draining = true;
                _stopTime = now;
            }

            if (SettingsPanel.TestActive) _testCapture = true;
            if (!_recording && !_draining) return;

            Read(now);

            if (_draining && (_steamStopped || now - _stopTime > MaxTrailingTime))
            {
                _draining = false;
                if (Pending.Count > 0) Flush();
                LogPhrase(now);
                return;
            }

            if (Pending.Count > 0 && now - _lastFlush >= FlushInterval)
            {
                Flush();
            }
        }

        private static bool MicrophoneWanted()
        {
            if (SettingsPanel.TestActive) return true;
            if (SettingsPanel.IsVisible()) return false;
            if (Plugin.Mode.Value == TransmitMode.OpenMic) return !Menu.IsVisible();
            return Plugin.CanUseInput() && Plugin.KeyHeld(Plugin.PushToTalkKey.Value);
        }

        private static void Read(float now)
        {
            EVoiceResult result = SteamUser.GetAvailableVoice(out uint available);
            if (result == EVoiceResult.k_EVoiceResultNotRecording)
            {
                if (_draining) _steamStopped = true;
                return;
            }
            if (result != EVoiceResult.k_EVoiceResultOK || available == 0) return;

            result = SteamUser.GetVoice(true, ReadBuffer, (uint)ReadBuffer.Length, out uint written);
            if (result != EVoiceResult.k_EVoiceResultOK || written == 0) return;

            byte[] frame = new byte[written];
            Buffer.BlockCopy(ReadBuffer, 0, frame, 0, (int)written);
            Pending.Add(frame);

            if (_lastData >= 0f) _maxGap = Mathf.Max(_maxGap, now - _lastData);
            _lastData = now;
            _reads++;
            _bytes += (int)written;
        }

        private static void LogPhrase(float now)
        {
            if (!Plugin.LogStats.Value) return;
            float held = _stopTime - _phraseStart;
            Plugin.Log.LogInfo(
                $"Captured phrase{(_testCapture ? " (microphone test)" : "")}: open {held:0.00} s, Steam recording ended " +
                $"{(now - _stopTime) * 1000f:0} ms after release{(_steamStopped ? "" : " (max delay reached)")}, " +
                $"{_reads} non-empty reads, max gap between two {_maxGap * 1000f:0} ms, {_bytes} bytes.");
        }

        private static void Flush()
        {
            _lastFlush = Time.unscaledTime;
            byte[][] frames = Pending.ToArray();
            Pending.Clear();

            int total = 0;
            foreach (byte[] frame in frames) total += frame.Length;

            Player local = Player.m_localPlayer;
            if (local == null) return;

            // The level meter decodes our own voice through the local VoiceSpeaker, played back only with Loopback.
            if (Plugin.Loopback.Value || SettingsPanel.IsVisible())
            {
                VoiceNetwork.Play(local, frames, Plugin.Loopback.Value);
                if (Plugin.Loopback.Value) VoiceStats.Local(total);
            }

            if (_testCapture || ZRoutedRpc.instance == null) return;

            // One targeted send per in-range player rather than a broadcast: the server only forwards to those concerned.
            float max = Plugin.MaxDistance.Value;
            Vector3 origin = local.transform.position;
            foreach (Player player in Player.GetAllPlayers())
            {
                if (player == null || player == local) continue;
                if (player.m_nview == null || !player.m_nview.IsValid()) continue;
                if (Vector3.Distance(origin, player.transform.position) > max) continue;

                long owner = player.m_nview.GetZDO().GetOwner();
                if (owner == 0L || owner == ZRoutedRpc.instance.m_id) continue;

                // Format: number of Steam packets, then each one as a byte array.
                ZPackage pkg = new ZPackage();
                pkg.Write(frames.Length);
                foreach (byte[] frame in frames) pkg.Write(frame);
                ZRoutedRpc.instance.InvokeRoutedRPC(owner, Plugin.RpcVoice, pkg);
                VoiceStats.Sent(total);
            }
        }

        internal static void Stop()
        {
            if (!_recording && !_draining) return;
            _recording = false;
            _draining = false;
            _stopTime = -1f;
            Pending.Clear();
            try
            {
                SteamUser.StopVoiceRecording();
            }
            catch (Exception)
            {
                // Steam already shut down while the game is closing.
            }
        }
    }

    // ================================================================== network

    internal static class VoiceNetwork
    {
        internal static void Register()
        {
            if (ZRoutedRpc.instance == null) return;

            try
            {
                ZRoutedRpc.instance.Register<ZPackage>(Plugin.RpcVoice, RPC_Voice);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not register RPCs: {e.Message}");
            }
        }

        private static void RPC_Voice(long sender, ZPackage pkg)
        {
            if (!Plugin.Enabled.Value || !Plugin.SteamVoiceAvailable()) return;

            int count = pkg.ReadInt();
            if (count <= 0 || count > 64) return;

            byte[][] frames = new byte[count][];
            int total = 0;
            for (int i = 0; i < count; i++)
            {
                frames[i] = pkg.ReadByteArray();
                total += frames[i].Length;
            }
            VoiceStats.Received(total);

            Player speaker = Plugin.FindPlayerByPeer(sender);
            if (speaker == null) return;

            Play(speaker, frames, true);
        }

        /// <summary>Decodes the packets on the player's VoiceSpeaker; <paramref name="play"/> false only feeds the level meter.</summary>
        internal static void Play(Player speaker, byte[][] frames, bool play)
        {
            VoiceSpeaker voice = speaker.GetComponent<VoiceSpeaker>();
            if (voice == null) voice = speaker.gameObject.AddComponent<VoiceSpeaker>();
            foreach (byte[] frame in frames) voice.Push(frame, play);
        }
    }

    // ================================================================== playback

    /// <summary>
    /// A player's voice: decompresses Steam packets to PCM and stores them in a ring buffer, which the AudioSource's
    /// VoiceFilter drains. Destroyed along with the player's object.
    ///
    /// Why not a streaming AudioClip (PCMReaderCallback): Unity requests blocks of several thousand samples at once,
    /// more than the jitter buffer holds. Each request emptied it and ended with silence, hence regular dropouts.
    /// OnAudioFilterRead is called by the audio thread in small, regular blocks.
    /// </summary>
    internal class VoiceSpeaker : MonoBehaviour
    {
        /// <summary>Beyond this, the oldest audio is dropped: better to skip a bit than to talk 1 s behind.</summary>
        private const float MaxLatency = 0.5f;

        /// <summary>Automatic gain brings the voice peaks to this amplitude (1 = full scale).</summary>
        private const float TargetPeak = 0.6f;

        /// <summary>Highest automatic gain: beyond it, a very quiet microphone mostly brings up its own noise.</summary>
        private const float MaxAutoGain = 6f;

        /// <summary>Time for the peak envelope to fall by half: holds the level across the pauses between words.</summary>
        private const float EnvelopeHalfLife = 1.5f;

        private readonly object _lock = new object();

        private int _outputRate;
        private int _decodeRate;
        private double _step;
        private float[] _ring;
        private int _read;
        private int _count;
        private double _frac;
        private bool _prebuffering = true;
        private volatile int _prebufferSamples;
        private bool _formatLogged;

        private byte[] _pcm = new byte[22050 * 2];
        private AudioSource _source;

        /// <summary>Last time a packet was played (on-screen indicator).</summary>
        internal float LastHeard { get; private set; } = -100f;

        /// <summary>Peak amplitude (0 to 1) of the last decoded packet, played or not (settings panel level meter).</summary>
        internal float LastPeak { get; private set; }

        internal float LastPeakTime { get; private set; } = -100f;

        // Summary of the received phrase: arrival bursts (network or capture) versus playback dropouts.
        private int _phrasePackets;
        private int _phraseSamples;
        private float _phraseMaxGap;
        private int _phraseMaxChunk;
        private int _phraseUnderruns;
        private int _phraseFailures;
        private EVoiceResult _phraseLastFailure;
        private float _lastPush = -100f;

        /// <summary>Gain the audio thread applies to the voice: Volume times the automatic gain (1 when AutoGain is off).</summary>
        private volatile float _outputGain = 1f;

        // Automatic gain, main thread only: peak envelope of the received voice, and the gain derived from it.
        private float _envelope;
        private float _envelopeTime = -100f;
        private float _autoGain = 1f;

        private void Awake()
        {
            // Decode straight at the output rate: no resampling in the common case (44.1 or 48 kHz).
            // DecompressVoice only accepts 11025 to 48000 Hz; above that, linear interpolation.
            _outputRate = AudioSettings.outputSampleRate;
            _decodeRate = Mathf.Clamp(_outputRate, 11025, 48000);
            _step = (double)_decodeRate / _outputRate;
            _ring = new float[_decodeRate * 2];
            _prebufferSamples = (int)(_decodeRate * Plugin.BufferMs.Value / 1000f);

            Player player = GetComponent<Player>();
            Transform anchor = player != null && player.m_head != null ? player.m_head : transform;

            GameObject go = new GameObject("VoiceChat");
            go.transform.SetParent(anchor, false);

            // The clip is a constant signal of 1: Unity applies volume, attenuation and 3D panning to it, then VoiceFilter
            // multiplies it by the voice. The voice thus inherits the spatialization without recomputing it.
            _source = go.AddComponent<AudioSource>();
            _source.clip = CreateFlatClip(_outputRate);
            _source.loop = true;
            _source.spatialBlend = 1f;
            _source.dopplerLevel = 0f;
            _source.rolloffMode = AudioRolloffMode.Linear;
            _source.outputAudioMixerGroup = FindSfxGroup();
            ApplyConfig();

            VoiceFilter filter = go.AddComponent<VoiceFilter>();
            filter.Speaker = this;

            _source.Play();
        }

        /// <summary>
        /// Filled through a callback rather than SetData: SetData also has a ReadOnlySpan overload (netstandard 2.1)
        /// that the net48 compiler refuses to resolve (CS1705). Outside streaming, the callback fills the whole clip on creation.
        /// </summary>
        private static AudioClip CreateFlatClip(int rate)
        {
            return AudioClip.Create("VoiceChatFlat", rate, 1, rate, false, data =>
            {
                for (int i = 0; i < data.Length; i++) data[i] = 1f;
            });
        }

        private static AudioMixerGroup FindSfxGroup()
        {
            AudioMixer mixer = AudioMan.instance != null ? AudioMan.instance.m_masterMixer : null;
            if (mixer == null) return null;
            AudioMixerGroup[] groups = mixer.FindMatchingGroups("SFX");
            return groups != null && groups.Length > 0 ? groups[0] : null;
        }

        private void ApplyConfig()
        {
            // Unity clamps AudioSource.volume to 1, so a Volume above 100% would do nothing there: the source stays at 1
            // and the gain is applied to the samples in Fill.
            _source.volume = 1f;
            _outputGain = Plugin.Volume.Value * (Plugin.AutoGain.Value ? _autoGain : 1f);
            _source.minDistance = Plugin.MinDistance.Value;
            _source.maxDistance = Mathf.Max(Plugin.MinDistance.Value + 1f, Plugin.MaxDistance.Value);
        }

        private void Update()
        {
            if (_source != null) ApplyConfig();
            _prebufferSamples = (int)(_decodeRate * Plugin.BufferMs.Value / 1000f);

            // One second without a packet: the phrase is over.
            if ((_phrasePackets > 0 || _phraseFailures > 0) && Time.unscaledTime - _lastPush > 1f)
            {
                int underruns = System.Threading.Interlocked.Exchange(ref _phraseUnderruns, 0);
                if (Plugin.LogStats.Value)
                {
                    Player player = GetComponent<Player>();
                    Plugin.Log.LogInfo(
                        $"Played phrase ({(player != null ? player.GetPlayerName() : "?")}): {_phrasePackets} packets, " +
                        $"{_phraseSamples * 1000f / _decodeRate:0} ms of voice, max gap between two packets {_phraseMaxGap * 1000f:0} ms, " +
                        $"largest packet {_phraseMaxChunk * 1000f / _decodeRate:0} ms, underruns {underruns}, " +
                        $"failed decodes {_phraseFailures}{(_phraseFailures > 0 ? $" ({_phraseLastFailure})" : "")}, " +
                        $"buffer {Plugin.BufferMs.Value:0} ms, automatic gain x{_autoGain:0.0}" +
                        $"{(Plugin.AutoGain.Value ? "" : " (off)")}.");
                }
                _phrasePackets = _phraseSamples = _phraseMaxChunk = _phraseFailures = 0;
                _phraseMaxGap = 0f;
            }
        }

        internal void Push(byte[] compressed, bool play)
        {
            uint written;
            EVoiceResult result = SteamUser.DecompressVoice(compressed, (uint)compressed.Length, _pcm, (uint)_pcm.Length,
                out written, (uint)_decodeRate);
            if (result == EVoiceResult.k_EVoiceResultBufferTooSmall)
            {
                _pcm = new byte[written];
                result = SteamUser.DecompressVoice(compressed, (uint)compressed.Length, _pcm, (uint)_pcm.Length,
                    out written, (uint)_decodeRate);
            }
            float now = Time.unscaledTime;
            _lastPush = now;
            if (result != EVoiceResult.k_EVoiceResultOK || written < 2)
            {
                _phraseFailures++;
                _phraseLastFailure = result;
                return;
            }

            int samples = (int)written / 2;

            float peak = 0f;
            for (int i = 0; i < samples; i++)
            {
                // Signed 16-bit PCM, little-endian, mono.
                float amplitude = Math.Abs((short)(_pcm[i * 2] | (_pcm[i * 2 + 1] << 8)) / 32768f);
                if (amplitude > peak) peak = amplitude;
            }
            LastPeak = peak;
            LastPeakTime = now;

            if (!play) return;
            UpdateAutoGain(peak, now);

            if (_phrasePackets > 0) _phraseMaxGap = Mathf.Max(_phraseMaxGap, now - LastHeard);
            _phrasePackets++;
            _phraseSamples += samples;
            _phraseMaxChunk = Math.Max(_phraseMaxChunk, samples);
            LastHeard = now;
            int maxCount = (int)(_decodeRate * MaxLatency);

            lock (_lock)
            {
                for (int i = 0; i < samples; i++)
                {
                    short s = (short)(_pcm[i * 2] | (_pcm[i * 2 + 1] << 8));
                    if (_count == _ring.Length)
                    {
                        _read = (_read + 1) % _ring.Length;
                        _count--;
                    }
                    _ring[(_read + _count) % _ring.Length] = s / 32768f;
                    _count++;
                }

                if (_count > maxCount)
                {
                    int drop = _count - maxCount;
                    _read = (_read + drop) % _ring.Length;
                    _count -= drop;
                    VoiceStats.Dropped(drop);
                }
            }
        }

        /// <summary>
        /// Automatic gain of this speaker. The envelope rises instantly with a louder peak and falls slowly, and the gain
        /// brings it to TargetPeak, between 1 (never quieter) and MaxAutoGain. A loud packet lowers the gain before it is
        /// heard, since packets wait in the jitter buffer; a quiet speaker is brought up within a second or two. Steam sends
        /// nothing during silences, so the background noise between words is not amplified.
        /// </summary>
        private void UpdateAutoGain(float peak, float now)
        {
            float elapsed = Mathf.Max(0f, now - _envelopeTime);
            _envelopeTime = now;
            _envelope = Mathf.Max(peak, _envelope * Mathf.Pow(0.5f, elapsed / EnvelopeHalfLife));
            _autoGain = Mathf.Clamp(TargetPeak / Mathf.Max(_envelope, 0.0001f), 1f, MaxAutoGain);
            _outputGain = Plugin.Volume.Value * (Plugin.AutoGain.Value ? _autoGain : 1f);
        }

        /// <summary>Linear up to 0.8, then compressed towards 1: amplified peaks round off instead of clipping harshly.</summary>
        private static float SoftClip(float x)
        {
            float magnitude = Math.Abs(x);
            if (magnitude <= 0.8f) return x;
            float limited = 0.8f + 0.2f * (float)Math.Tanh((magnitude - 0.8f) / 0.2f);
            return x < 0f ? -limited : limited;
        }

        /// <summary>
        /// Called by VoiceFilter on the audio thread (hence the lock): multiplies the constant, already spatialized signal
        /// by the voice. <paramref name="data"/> is interleaved over <paramref name="channels"/> channels.
        /// </summary>
        internal void Fill(float[] data, int channels)
        {
            int frames = data.Length / channels;

            if (!_formatLogged)
            {
                _formatLogged = true;
                Plugin.Log.LogInfo($"Voice playback: output {_outputRate} Hz, {channels} channels, blocks of {frames} samples, " +
                    $"voice decoded at {_decodeRate} Hz.");
            }

            lock (_lock)
            {
                if (_prebuffering && _count < _prebufferSamples)
                {
                    Array.Clear(data, 0, data.Length);
                    return;
                }
                _prebuffering = false;

                float gain = _outputGain;
                int frame = 0;
                for (; frame < frames; frame++)
                {
                    // Interpolation between two neighbouring samples: two must be available.
                    if (_count < 2) break;

                    float a = _ring[_read];
                    float b = _ring[(_read + 1) % _ring.Length];
                    float sample = SoftClip((a + (b - a) * (float)_frac) * gain);

                    int offset = frame * channels;
                    for (int c = 0; c < channels; c++) data[offset + c] *= sample;

                    _frac += _step;
                    while (_frac >= 1.0 && _count > 0)
                    {
                        _frac -= 1.0;
                        _read = (_read + 1) % _ring.Length;
                        _count--;
                    }
                }

                if (frame < frames)
                {
                    // End of phrase or late packet: silence, then refill the buffer before resuming.
                    Array.Clear(data, frame * channels, data.Length - frame * channels);
                    _prebuffering = true;
                    if (frame > 0)
                    {
                        VoiceStats.Underrun();
                        System.Threading.Interlocked.Increment(ref _phraseUnderruns);
                    }
                }
            }
        }

        private void OnDestroy()
        {
            if (_source != null)
            {
                _source.Stop();
                Destroy(_source.clip);
                Destroy(_source.gameObject);
            }
        }
    }

    /// <summary>Must sit on the same object as the AudioSource, added after it, to be inserted into its audio chain.</summary>
    internal class VoiceFilter : MonoBehaviour
    {
        internal VoiceSpeaker Speaker;

        private void OnAudioFilterRead(float[] data, int channels)
        {
            VoiceSpeaker speaker = Speaker;
            if (speaker == null)
            {
                Array.Clear(data, 0, data.Length);
                return;
            }
            speaker.Fill(data, channels);
        }
    }

    // ================================================================== settings panel

    /// <summary>
    /// Settings panel built in uGUI on the game's canvas, like PortalMenu's: its own Canvas sorted above the HUD, a font
    /// borrowed from the game, and the player's controls blocked while it is open (patches at the end of the file).
    /// Every change is written straight to its ConfigEntry, so to the .cfg.
    /// </summary>
    internal static class SettingsPanel
    {
        private const string RootName = "VoiceChat_Settings";

        /// <summary>Above the HUD (hover text, center messages), which otherwise draws over the panel.</summary>
        private const int SortingOrder = 5000;

        private const float Width = 500f;
        private const float Padding = 16f;
        private const float RowHeight = 34f;

        /// <summary>Label column width, as a fraction of the row.</summary>
        private const float LabelSplit = 0.42f;

        private const string DefaultHint =
            "Microphone device, input volume and transmission threshold are set in Steam > Settings > Voice.";
        private const string NoSignalHint =
            "Steam is not sending any voice: check the microphone and the transmission threshold in Steam > Settings > Voice.";
        private const string QuietHint =
            "Your microphone is quiet: raise its input volume in Steam > Settings > Voice (others' automatic gain only partly makes up for it).";

        /// <summary>Below this peak during a microphone test, the microphone is reported as quiet.</summary>
        private const float QuietPeak = 0.15f;

        private static readonly Color Background = new Color(0.09f, 0.07f, 0.05f, 0.95f);
        private static readonly Color TextColor = new Color(0.95f, 0.91f, 0.82f);
        private static readonly Color TitleColor = new Color(1f, 0.94f, 0.8f);
        private static readonly Color AccentColor = new Color(0.9f, 0.72f, 0.4f);
        private static readonly Color DimColor = new Color(0.75f, 0.7f, 0.62f);
        private static readonly Color MeterColor = new Color(0.45f, 0.8f, 0.4f);
        private static readonly Color MeterHotColor = new Color(0.9f, 0.35f, 0.25f);

        private static readonly KeyCode[] AllKeys = (KeyCode[])Enum.GetValues(typeof(KeyCode));

        private sealed class SliderRow
        {
            public Slider Slider;
            public TMP_Text Value;
            public ConfigEntry<float> Entry;
            public Func<float, string> Format;
        }

        private static readonly List<SliderRow> Sliders = new List<SliderRow>();

        private static GameObject _root;
        private static TMP_FontAsset _font;
        private static TMP_Text _modeLabel;
        private static TMP_Text _keyLabel;
        private static TMP_Text _testLabel;
        private static TMP_Text _loopbackLabel;
        private static TMP_Text _autoGainLabel;
        private static TMP_Text _hint;
        private static RectTransform _meterFill;
        private static Image _meterImage;

        private static bool _waitingForKey;
        private static float _testStart;
        private static float _testMaxPeak;
        private static float _meter;

        /// <summary>Microphone test running: the microphone is open for the level meter, and nothing is sent.</summary>
        internal static bool TestActive { get; private set; }

        internal static bool IsVisible()
        {
            return _root != null && _root.activeSelf;
        }

        // -------------------------------------------------------------- lifecycle

        internal static void Update()
        {
            if (!Plugin.Enabled.Value || Player.m_localPlayer == null)
            {
                if (IsVisible()) Hide();
                return;
            }

            if (!IsVisible())
            {
                if (Plugin.CanUseInput() && Plugin.KeyDown(Plugin.SettingsKey.Value)) Show();
                return;
            }

            if (_waitingForKey)
            {
                CaptureKey();
            }
            else if (Plugin.KeyDown(Plugin.SettingsKey.Value))
            {
                Hide();
                return;
            }

            UpdateMeter();
        }

        private static void Show()
        {
            Transform canvas = FindCanvas();
            if (canvas == null)
            {
                Plugin.Log.LogWarning("Game canvas not found, settings panel not shown.");
                return;
            }
            if (!Build(canvas)) return;

            _root.SetActive(true);
            _root.transform.SetAsLastSibling();

            // Set on every opening: Unity can lose overrideSorting set on a canvas that is still inactive.
            Canvas own = _root.GetComponent<Canvas>();
            if (own != null)
            {
                own.overrideSorting = true;
                own.sortingOrder = SortingOrder;
            }

            _meter = 0f;
            Refresh();
        }

        internal static void Hide()
        {
            _waitingForKey = false;
            TestActive = false;
            if (_root != null) _root.SetActive(false);
        }

        /// <summary>Escape: cancels a key capture first, closes the panel otherwise.</summary>
        internal static void HandleEscape()
        {
            if (_waitingForKey)
            {
                _waitingForKey = false;
                Refresh();
                return;
            }
            Hide();
        }

        internal static void Destroy()
        {
            Hide();
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            Sliders.Clear();
        }

        // -------------------------------------------------------------- behaviour

        private static void Refresh()
        {
            if (_root == null) return;

            _modeLabel.text = Plugin.Mode.Value == TransmitMode.PushToTalk ? "Push-to-talk" : "Open microphone";
            _keyLabel.text = _waitingForKey ? "Press a key... (Esc: cancel)" : Plugin.PushToTalkKey.Value.ToString();
            _keyLabel.color = _waitingForKey ? AccentColor : TextColor;
            _testLabel.text = TestActive ? "Stop test" : "Start test";
            _loopbackLabel.text = Plugin.Loopback.Value ? "On" : "Off";
            _autoGainLabel.text = Plugin.AutoGain.Value ? "On" : "Off";

            foreach (SliderRow row in Sliders)
            {
                row.Slider.SetValueWithoutNotify(row.Entry.Value);
                row.Value.text = row.Format(row.Entry.Value);
            }
        }

        private static void ToggleMode()
        {
            Plugin.Mode.Value = Plugin.Mode.Value == TransmitMode.PushToTalk ? TransmitMode.OpenMic : TransmitMode.PushToTalk;
            Refresh();
        }

        private static void StartKeyCapture()
        {
            _waitingForKey = true;
            Refresh();
        }

        private static void ToggleTest()
        {
            TestActive = !TestActive;
            _testStart = Time.unscaledTime;
            _testMaxPeak = 0f;
            Refresh();
        }

        private static void ToggleLoopback()
        {
            Plugin.Loopback.Value = !Plugin.Loopback.Value;
            Refresh();
        }

        private static void ToggleAutoGain()
        {
            Plugin.AutoGain.Value = !Plugin.AutoGain.Value;
            Refresh();
        }

        /// <summary>
        /// First key pressed becomes the push-to-talk key. Left and right clicks are ignored (they operate the panel),
        /// as are gamepads; Escape is handled by the Menu.Update patch.
        /// </summary>
        private static void CaptureKey()
        {
            foreach (KeyCode key in AllKeys)
            {
                if (key == KeyCode.None || key == KeyCode.Escape || key == KeyCode.Mouse0 || key == KeyCode.Mouse1) continue;
                if (key >= KeyCode.JoystickButton0) continue;
                if (!Input.GetKeyDown(key)) continue;

                Plugin.PushToTalkKey.Value = new KeyboardShortcut(key);
                _waitingForKey = false;
                Refresh();
                return;
            }
        }

        /// <summary>
        /// Level meter on a -50 dB to 0 dB scale, fed by the peak of our own decoded packets. Falls back slowly so the
        /// short gaps between Steam packets do not make it flicker.
        /// </summary>
        private static void UpdateMeter()
        {
            if (_meterFill == null) return;

            float now = Time.unscaledTime;
            Player local = Player.m_localPlayer;
            VoiceSpeaker speaker = local != null ? local.GetComponent<VoiceSpeaker>() : null;

            float target = 0f;
            if (speaker != null && now - speaker.LastPeakTime < 0.15f) target = ToMeterScale(speaker.LastPeak);
            _meter = Mathf.Max(target, _meter - Time.unscaledDeltaTime * 1.5f);

            _meterFill.anchorMax = new Vector2(_meter, 1f);
            _meterImage.color = _meter > 0.92f ? MeterHotColor : MeterColor;

            bool heard = speaker != null && speaker.LastPeakTime >= _testStart;
            if (TestActive && heard) _testMaxPeak = Mathf.Max(_testMaxPeak, speaker.LastPeak);

            // Steam's raw level is what the others receive: a quiet microphone is best fixed at the source.
            bool silent = TestActive && now - _testStart > 2f && !heard;
            bool quiet = TestActive && heard && now - _testStart > 4f && _testMaxPeak < QuietPeak;
            _hint.text = silent ? NoSignalHint : quiet ? QuietHint : DefaultHint;
            _hint.color = silent || quiet ? AccentColor : DimColor;
        }

        private static float ToMeterScale(float peak)
        {
            if (peak <= 0.00001f) return 0f;
            float db = 20f * Mathf.Log10(peak);
            return Mathf.Clamp01((db + 50f) / 50f);
        }

        // -------------------------------------------------------------- construction

        private static bool Build(Transform canvas)
        {
            if (_root != null) return true;

            _font = FindFont(canvas);
            if (_font == null)
            {
                Plugin.Log.LogWarning("Game font not found, settings panel not shown.");
                return false;
            }

            RectTransform root = NewRect(RootName, canvas);
            _root = root.gameObject;
            root.anchorMin = new Vector2(0.5f, 0.5f);
            root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = new Vector2(Width, 0f);

            // Own canvas: drawn above the HUD and clickable whatever the order of the game's canvases.
            // The CanvasGroup ignores the parents' ones, whose opacity would make the panel translucent.
            _root.AddComponent<Canvas>();
            _root.AddComponent<GraphicRaycaster>();
            var group = _root.AddComponent<CanvasGroup>();
            group.ignoreParentGroups = true;
            group.alpha = 1f;
            group.interactable = true;
            group.blocksRaycasts = true;

            _root.AddComponent<Image>().color = Background;

            var stack = _root.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset((int)Padding, (int)Padding, (int)(Padding * 0.6f), (int)Padding);
            stack.spacing = 4f;
            stack.childControlWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;

            // The panel's height follows its rows.
            _root.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            NewLine(root, "Voice chat", 22f, TitleColor, 36f);

            NewLine(root, "Microphone", 17f, AccentColor, 26f);
            _modeLabel = NewButton(ControlArea(NewRow(root, "Transmission")), ToggleMode);
            _keyLabel = NewButton(ControlArea(NewRow(root, "Push-to-talk key")), StartKeyCapture);
            _testLabel = NewButton(ControlArea(NewRow(root, "Microphone test")), ToggleTest);
            NewMeter(NewRow(root, "Level"));
            _loopbackLabel = NewButton(ControlArea(NewRow(root, "Hear myself")), ToggleLoopback);

            _hint = NewLine(root, DefaultHint, 13f, DimColor, 38f);
            _hint.textWrappingMode = TextWrappingModes.Normal;
            _hint.overflowMode = TextOverflowModes.Overflow;

            NewLine(root, "Playback", 17f, AccentColor, 30f);
            NewSliderRow(root, "Voice volume", Plugin.Volume, 0.05f, v => $"{v * 100f:0} %");
            _autoGainLabel = NewButton(ControlArea(NewRow(root, "Automatic gain")), ToggleAutoGain);
            NewSliderRow(root, "Full volume within", Plugin.MinDistance, 0.5f, v => $"{v:0.0} m");
            NewSliderRow(root, "Heard up to", Plugin.MaxDistance, 1f, v => $"{v:0} m");
            NewSliderRow(root, "Latency buffer", Plugin.BufferMs, 10f, v => $"{v:0} ms");

            RectTransform footer = NewRect("footer", root);
            footer.gameObject.AddComponent<LayoutElement>().preferredHeight = RowHeight + 10f;
            RectTransform close = NewRect("close", footer);
            close.anchorMin = new Vector2(0.3f, 0f);
            close.anchorMax = new Vector2(0.7f, 1f);
            close.offsetMin = new Vector2(0f, 0f);
            close.offsetMax = new Vector2(0f, -10f);
            NewButton(close, Hide).text = "Close";

            return true;
        }

        /// <summary>A row: label on the left, control on the right (see ControlArea).</summary>
        private static RectTransform NewRow(RectTransform parent, string label)
        {
            RectTransform row = NewRect(label, parent);
            LayoutElement layout = row.gameObject.AddComponent<LayoutElement>();
            layout.preferredHeight = RowHeight;
            layout.minHeight = RowHeight;

            TMP_Text text = NewText(row, "label", label, 16f, TextAlignmentOptions.Left);
            text.color = TextColor;
            text.rectTransform.anchorMax = new Vector2(LabelSplit, 1f);
            return row;
        }

        private static RectTransform ControlArea(RectTransform row)
        {
            RectTransform area = NewRect("control", row);
            area.anchorMin = new Vector2(LabelSplit, 0f);
            area.anchorMax = new Vector2(1f, 1f);
            area.offsetMin = new Vector2(0f, 3f);
            area.offsetMax = new Vector2(0f, -3f);
            return area;
        }

        private static TMP_Text NewLine(RectTransform parent, string content, float size, Color color, float height)
        {
            RectTransform line = NewRect("line", parent);
            line.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            TMP_Text text = NewText(line, "text", content, size, TextAlignmentOptions.Left);
            text.color = color;
            return text;
        }

        private static TMP_Text NewButton(RectTransform area, UnityEngine.Events.UnityAction action)
        {
            var background = area.gameObject.AddComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.09f);

            TMP_Text text = NewText(area, "label", "", 16f, TextAlignmentOptions.Center);
            text.color = TextColor;

            var button = area.gameObject.AddComponent<Button>();
            button.targetGraphic = background;

            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(1f, 0.92f, 0.7f, 1f);
            colors.pressedColor = new Color(1f, 0.85f, 0.5f, 1f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            button.onClick.AddListener(action);
            return text;
        }

        private static void NewMeter(RectTransform row)
        {
            RectTransform area = ControlArea(row);
            area.offsetMin = new Vector2(0f, 10f);
            area.offsetMax = new Vector2(0f, -10f);
            area.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.45f);

            _meterFill = NewRect("fill", area);
            _meterFill.anchorMin = new Vector2(0f, 0f);
            _meterFill.anchorMax = new Vector2(0f, 1f);
            _meterFill.offsetMin = Vector2.zero;
            _meterFill.offsetMax = Vector2.zero;
            _meterImage = _meterFill.gameObject.AddComponent<Image>();
            _meterImage.color = MeterColor;
        }

        /// <summary>
        /// Slider bound to a ConfigEntry, its range taken from the entry's AcceptableValueRange. Values are rounded to
        /// <paramref name="step"/> so that dragging does not rewrite the .cfg on every pixel.
        /// </summary>
        private static void NewSliderRow(RectTransform parent, string label, ConfigEntry<float> entry, float step,
            Func<float, string> format)
        {
            RectTransform row = NewRow(parent, label);

            RectTransform area = NewRect("slider", row);
            area.anchorMin = new Vector2(LabelSplit, 0.5f);
            area.anchorMax = new Vector2(0.8f, 0.5f);
            area.sizeDelta = new Vector2(0f, 8f);
            area.anchoredPosition = Vector2.zero;
            area.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);

            RectTransform fillArea = NewRect("fillArea", area);
            Stretch(fillArea, 0f, 0f, 0f, 0f);
            RectTransform fill = NewRect("fill", fillArea);
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(0f, 1f);
            fill.sizeDelta = Vector2.zero;
            fill.gameObject.AddComponent<Image>().color = new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.8f);

            RectTransform handleArea = NewRect("handleArea", area);
            Stretch(handleArea, 6f, 6f, 0f, 0f);
            RectTransform handle = NewRect("handle", handleArea);
            handle.anchorMin = new Vector2(0f, 0f);
            handle.anchorMax = new Vector2(0f, 1f);
            handle.sizeDelta = new Vector2(12f, 12f);
            Image handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = TitleColor;

            var slider = area.gameObject.AddComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            slider.direction = Slider.Direction.LeftToRight;

            var range = entry.Description.AcceptableValues as AcceptableValueRange<float>;
            slider.minValue = range != null ? range.MinValue : 0f;
            slider.maxValue = range != null ? range.MaxValue : 1f;
            slider.SetValueWithoutNotify(entry.Value);

            TMP_Text value = NewText(row, "value", format(entry.Value), 15f, TextAlignmentOptions.Right);
            value.color = TextColor;
            value.rectTransform.anchorMin = new Vector2(0.8f, 0f);

            slider.onValueChanged.AddListener(v =>
            {
                float rounded = Mathf.Clamp(Mathf.Round(v / step) * step, slider.minValue, slider.maxValue);
                if (!Mathf.Approximately(rounded, entry.Value)) entry.Value = rounded;
                value.text = format(entry.Value);
            });

            Sliders.Add(new SliderRow { Slider = slider, Value = value, Entry = entry, Format = format });
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.localScale = Vector3.one;
            return rect;
        }

        private static TMP_Text NewText(RectTransform parent, string name, string content, float size,
            TextAlignmentOptions alignment)
        {
            RectTransform rect = NewRect(name, parent);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = _font;
            text.fontSize = size;
            text.text = content;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            Stretch(rect, 0f, 0f, 0f, 0f);
            return text;
        }

        private static void Stretch(RectTransform rect, float left, float right, float top, float bottom)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>The game's canvas, reached through a GUI that always exists in a session.</summary>
        internal static Transform FindCanvas()
        {
            Component anchor = null;
            if (StoreGui.instance != null) anchor = StoreGui.instance;
            else if (InventoryGui.instance != null) anchor = InventoryGui.instance;
            else if (Hud.instance != null) anchor = Hud.instance;
            if (anchor == null) return null;

            Canvas canvas = anchor.GetComponentInParent<Canvas>();
            return canvas != null ? canvas.transform : null;
        }

        /// <summary>
        /// Font borrowed from a game text, so no asset is shipped. Aims at the serif font of the hover text: the first text
        /// found on the canvas can be an unreadable pixel font.
        /// </summary>
        internal static TMP_FontAsset FindFont(Transform canvas)
        {
            if (Hud.instance != null && Hud.instance.m_hoverName != null && Hud.instance.m_hoverName.font != null)
                return Hud.instance.m_hoverName.font;

            if (InventoryGui.instance != null && InventoryGui.instance.m_recipeName != null
                && InventoryGui.instance.m_recipeName.font != null)
                return InventoryGui.instance.m_recipeName.font;

            // Fallback: the canvas's most used font, which is the game's and not a debug font.
            var uses = new Dictionary<TMP_FontAsset, int>();
            TMP_FontAsset best = null;
            foreach (TMP_Text text in canvas.GetComponentsInChildren<TMP_Text>(includeInactive: true))
            {
                if (text == null || text.font == null) continue;
                uses.TryGetValue(text.font, out int n);
                uses[text.font] = ++n;
                if (best == null || n > uses[best]) best = text.font;
            }
            return best;
        }
    }

    // ================================================================== display and stats

    /// <summary>
    /// Who is talking: a list on the left of the screen, with a microphone icon and a name per talking player (yourself
    /// included while your microphone is open), and a microphone icon next to the name above a talking player's head.
    /// Both are uGUI with the game's font; the icon is drawn in code, so no asset is shipped.
    /// </summary>
    internal static class VoiceHud
    {
        private const string ListName = "VoiceChat_Speakers";
        private const string NameplateIconName = "VoiceChat_Mic";

        /// <summary>A player counts as talking for this long after their last played packet, bridging the gaps between packets.</summary>
        private const float TalkingHold = 0.3f;

        private const float RowHeight = 28f;
        private const float IconSize = 22f;

        private static readonly Color SelfColor = new Color(0.45f, 0.85f, 0.4f);
        private static readonly Color OthersColor = new Color(1f, 0.85f, 0.4f);
        private static readonly Color NameColor = new Color(0.95f, 0.91f, 0.82f);

        private sealed class Row
        {
            public GameObject Root;
            public Image Icon;
            public TMP_Text Name;
        }

        private static readonly List<Row> Rows = new List<Row>();
        private static readonly List<string> Names = new List<string>();
        private static readonly List<Color> Colors = new List<Color>();

        private static RectTransform _list;
        private static TMP_FontAsset _font;
        private static Sprite _micSprite;

        internal static bool IsTalking(Character character)
        {
            VoiceSpeaker voice = character != null ? character.GetComponent<VoiceSpeaker>() : null;
            return voice != null && Time.unscaledTime - voice.LastHeard < TalkingHold;
        }

        // -------------------------------------------------------------- speaker list

        internal static void Update()
        {
            Player local = Player.m_localPlayer;
            Names.Clear();
            Colors.Clear();
            if (Plugin.Enabled.Value && Plugin.ShowSpeakerList.Value && local != null && !Hud.IsUserHidden())
            {
                if (VoiceCapture.Testing) Add("Microphone test", SelfColor);
                else if (VoiceCapture.Transmitting) Add(local.GetPlayerName(), SelfColor);

                foreach (Player player in Player.GetAllPlayers())
                {
                    // Yourself is already listed while the microphone is open, even when Loopback plays your voice.
                    if (player != null && player != local && IsTalking(player)) Add(player.GetPlayerName(), OthersColor);
                }
            }

            if (Names.Count == 0)
            {
                if (_list != null) _list.gameObject.SetActive(false);
                return;
            }
            if (!BuildList()) return;

            _list.gameObject.SetActive(true);
            _list.anchoredPosition = new Vector2(Plugin.SpeakerListX.Value, Plugin.SpeakerListY.Value);
            while (Rows.Count < Names.Count) Rows.Add(NewRow());
            for (int i = 0; i < Rows.Count; i++)
            {
                bool used = i < Names.Count;
                Rows[i].Root.SetActive(used);
                if (!used) continue;
                Rows[i].Name.text = Names[i];
                Rows[i].Icon.color = Colors[i];
            }
        }

        private static void Add(string name, Color color)
        {
            Names.Add(name);
            Colors.Add(color);
        }

        /// <summary>Built on first use, and again after the game destroyed the canvas (back to the main menu).</summary>
        private static bool BuildList()
        {
            if (_list != null) return true;
            Rows.Clear();

            Transform canvas = SettingsPanel.FindCanvas();
            _font = canvas != null ? SettingsPanel.FindFont(canvas) : null;
            if (_font == null) return false;

            var go = new GameObject(ListName, typeof(RectTransform));
            _list = (RectTransform)go.transform;
            _list.SetParent(canvas, false);
            // Anchored on the middle of the left edge; the list grows downwards from its position.
            _list.anchorMin = new Vector2(0f, 0.5f);
            _list.anchorMax = new Vector2(0f, 0.5f);
            _list.pivot = new Vector2(0f, 1f);
            _list.sizeDelta = new Vector2(320f, 0f);

            // Display only: clicks go through to the game.
            var group = go.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            var stack = go.AddComponent<VerticalLayoutGroup>();
            stack.spacing = 2f;
            stack.childControlWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;
            go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return true;
        }

        private static Row NewRow()
        {
            var root = new GameObject("row", typeof(RectTransform));
            root.transform.SetParent(_list, false);
            root.AddComponent<LayoutElement>().preferredHeight = RowHeight;

            var iconObject = new GameObject("icon", typeof(RectTransform));
            var iconRect = (RectTransform)iconObject.transform;
            iconRect.SetParent(root.transform, false);
            iconRect.anchorMin = new Vector2(0f, 0.5f);
            iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.sizeDelta = new Vector2(IconSize, IconSize);
            var icon = iconObject.AddComponent<Image>();
            icon.sprite = MicSprite;
            icon.preserveAspect = true;
            icon.raycastTarget = false;

            var nameObject = new GameObject("name", typeof(RectTransform));
            var nameRect = (RectTransform)nameObject.transform;
            nameRect.SetParent(root.transform, false);
            nameRect.anchorMin = new Vector2(0f, 0f);
            nameRect.anchorMax = new Vector2(1f, 1f);
            nameRect.offsetMin = new Vector2(IconSize + 6f, 0f);
            nameRect.offsetMax = Vector2.zero;
            var name = nameObject.AddComponent<TextMeshProUGUI>();
            name.font = _font;
            name.fontSize = 18f;
            name.color = NameColor;
            name.alignment = TextAlignmentOptions.Left;
            name.raycastTarget = false;
            name.textWrappingMode = TextWrappingModes.NoWrap;
            name.overflowMode = TextOverflowModes.Ellipsis;

            return new Row { Root = root, Icon = icon, Name = name };
        }

        internal static void Destroy()
        {
            if (_list != null) UnityEngine.Object.Destroy(_list.gameObject);
            _list = null;
            Rows.Clear();
        }

        // -------------------------------------------------------------- nameplates

        /// <summary>
        /// Called after EnemyHud.UpdateHuds, which rewrites every name each frame: the icon is a child image of the name text
        /// placed after the rendered text, rather than a character added to the name.
        /// </summary>
        internal static void UpdateNameplates(EnemyHud hud)
        {
            bool enabled = Plugin.Enabled.Value && Plugin.ShowNameplateIcon.Value;
            foreach (var pair in hud.m_huds)
            {
                TMP_Text name = pair.Value.m_name;
                if (pair.Key == null || name == null || !pair.Key.IsPlayer()) continue;

                Transform icon = name.transform.Find(NameplateIconName);
                bool talking = enabled && IsTalking(pair.Key);
                if (icon == null)
                {
                    if (!talking) continue;
                    icon = NewNameplateIcon(name);
                }
                icon.gameObject.SetActive(talking);
                if (talking) PlaceNameplateIcon((RectTransform)icon, name);
            }
        }

        private static Transform NewNameplateIcon(TMP_Text name)
        {
            var go = new GameObject(NameplateIconName, typeof(RectTransform));
            go.transform.SetParent(name.transform, false);
            var image = go.AddComponent<Image>();
            image.sprite = MicSprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.color = OthersColor;
            return go.transform;
        }

        /// <summary>
        /// Right after the rendered text. textBounds is in the name's local space, whose origin is its pivot, so the icon is
        /// anchored on that pivot. Until the text has been laid out once, half its preferred width stands in.
        /// </summary>
        private static void PlaceNameplateIcon(RectTransform icon, TMP_Text name)
        {
            float size = Mathf.Max(14f, name.fontSize * 1.1f);
            Bounds bounds = name.textBounds;
            bool laidOut = bounds.size.x > 0f;
            float right = laidOut ? bounds.max.x : name.preferredWidth * 0.5f;
            icon.anchorMin = name.rectTransform.pivot;
            icon.anchorMax = name.rectTransform.pivot;
            icon.sizeDelta = new Vector2(size, size);
            icon.anchoredPosition = new Vector2(right + 4f + size * 0.5f, laidOut ? bounds.center.y : 0f);
        }

        // -------------------------------------------------------------- icon

        private static Sprite MicSprite
        {
            get
            {
                if (_micSprite == null) _micSprite = CreateMicSprite();
                return _micSprite;
            }
        }

        /// <summary>
        /// Microphone drawn with signed distances on a 32-unit grid (a capsule, the lower half of a ring holding it, a stem
        /// and a base), rendered at 64 px with a one-pixel antialiased edge. White on transparent, so Image.color tints it.
        /// </summary>
        private static Sprite CreateMicSprite()
        {
            const int size = 64;
            float scale = size / 32f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2((x + 0.5f) / scale, (y + 0.5f) / scale);
                    float distance = DistanceToSegment(p, new Vector2(16f, 16f), new Vector2(16f, 24f)) - 4.5f;
                    float ring = p.y <= 17f
                        ? Mathf.Abs(Vector2.Distance(p, new Vector2(16f, 17f)) - 9f) - 1.25f
                        : Mathf.Min(Vector2.Distance(p, new Vector2(7f, 17f)), Vector2.Distance(p, new Vector2(25f, 17f))) - 1.25f;
                    distance = Mathf.Min(distance, ring);
                    distance = Mathf.Min(distance, DistanceToSegment(p, new Vector2(16f, 8f), new Vector2(16f, 4f)) - 1.25f);
                    distance = Mathf.Min(distance, DistanceToSegment(p, new Vector2(11f, 4f), new Vector2(21f, 4f)) - 1.25f);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - distance * scale)));
                }
            }
            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }
    }

    internal static class VoiceStats
    {
        private const float Period = 10f;

        private static int _sentBytes, _sentPackets, _receivedBytes, _receivedPackets, _localBytes, _localPackets;
        private static int _underruns, _droppedSamples;
        private static float _last;

        internal static void Sent(int bytes) { _sentBytes += bytes; _sentPackets++; }
        internal static void Received(int bytes) { _receivedBytes += bytes; _receivedPackets++; }
        internal static void Local(int bytes) { _localBytes += bytes; _localPackets++; }
        internal static void Dropped(int samples) { _droppedSamples += samples; }

        /// <summary>Called from the audio thread.</summary>
        internal static void Underrun() { System.Threading.Interlocked.Increment(ref _underruns); }

        internal static void Update()
        {
            if (Time.unscaledTime - _last < Period) return;
            _last = Time.unscaledTime;

            int underruns = System.Threading.Interlocked.Exchange(ref _underruns, 0);
            if (Plugin.LogStats.Value && (_sentPackets > 0 || _receivedPackets > 0 || _localPackets > 0))
            {
                Plugin.Log.LogInfo(
                    $"Voice over {Period:0} s: sent {_sentPackets} packets / {_sentBytes / Period / 1024f:0.00} KB/s, " +
                    $"received {_receivedPackets} packets / {_receivedBytes / Period / 1024f:0.00} KB/s, " +
                    $"loopback {_localPackets} packets / {_localBytes / Period / 1024f:0.00} KB/s, " +
                    $"underruns {underruns}, dropped samples {_droppedSamples}.");
            }
            _sentBytes = _sentPackets = _receivedBytes = _receivedPackets = _localBytes = _localPackets = _droppedSamples = 0;
        }
    }

    // ================================================================== patches

    /// <summary>Talking players get a microphone icon next to the name above their head.</summary>
    [HarmonyPatch(typeof(EnemyHud), nameof(EnemyHud.UpdateHuds))]
    internal static class EnemyHud_UpdateHuds_Patch
    {
        private static void Postfix(EnemyHud __instance)
        {
            VoiceHud.UpdateNameplates(__instance);
        }
    }

    /// <summary>The game registers its routed RPCs in Game.Start: ours are added there.</summary>
    [HarmonyPatch(typeof(Game), nameof(Game.Start))]
    internal static class Game_Start_Patch
    {
        private static void Postfix()
        {
            VoiceNetwork.Register();
        }
    }

    /// <summary>
    /// Settings panel open: no movement, jump, attack or camera rotation. PlayerController reads keyboard and mouse
    /// (FixedUpdate for movement, LateUpdate for looking around); Player.TakeInput, patched below, only covers
    /// interaction and the hotbar.
    /// </summary>
    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    internal static class PlayerController_TakeInput_Patch
    {
        private static void Postfix(ref bool __result)
        {
            if (SettingsPanel.IsVisible()) __result = false;
        }
    }

    /// <summary>Settings panel open: the player no longer interacts nor uses the hotbar.</summary>
    [HarmonyPatch(typeof(Player), "TakeInput")]
    internal static class Player_TakeInput_Patch
    {
        private static void Postfix(ref bool __result)
        {
            if (SettingsPanel.IsVisible()) __result = false;
        }
    }

    /// <summary>Settings panel open: the cursor is released and the camera no longer follows the mouse.</summary>
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
    internal static class GameCamera_UpdateMouseCapture_Patch
    {
        private static bool Prefix()
        {
            if (!SettingsPanel.IsVisible()) return true;

            ZCursor.LockState = CursorLockMode.None;
            ZCursor.Show();
            return false;
        }
    }

    /// <summary>Escape cancels a key capture or closes the settings panel instead of opening the game menu.</summary>
    [HarmonyPatch(typeof(Menu), "Update")]
    internal static class Menu_Update_Patch
    {
        private static bool Prefix()
        {
            if (!SettingsPanel.IsVisible() || Menu.IsVisible()) return true;

            if (ZInput.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
            {
                ZInput.ResetButtonStatus("JoyButtonB");
                SettingsPanel.HandleEscape();
            }
            return false;
        }
    }
}
