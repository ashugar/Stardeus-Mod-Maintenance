using System;
using System.Globalization;
using Game;
using LocalLogMod.Logging;
using UnityEngine;

namespace LocalLogMod.UIEvents {
    internal static class AudioObserver {
        private static bool attached;

        internal static void Attach() {
            if (attached) { return; }
            The.SysSig.PlayUISound.AddListener(OnUISound);
            The.SysSig.PlaySound.AddListener(OnSound);
            The.SysSig.PlaySoundVol.AddListener(OnSoundVol);
            The.SysSig.PlaySoundLoud.AddListener(OnSoundLoud);
            attached = true;
            SessionLog.Write("Audio", "observer_attached hoverSoundsExcluded=True");
        }

        internal static void Detach() {
            if (!attached) { return; }
            The.SysSig.PlayUISound.RemoveListener(OnUISound);
            The.SysSig.PlaySound.RemoveListener(OnSound);
            The.SysSig.PlaySoundVol.RemoveListener(OnSoundVol);
            The.SysSig.PlaySoundLoud.RemoveListener(OnSoundLoud);
            attached = false;
        }

        private static void OnUISound(string sound, float volume) {
            Write("ui", sound, null, false, volume);
        }

        private static void OnSound(string sound, Vector2 position, bool globalPitch) {
            Write("world", sound, position, globalPitch, null);
        }

        private static void OnSoundVol(string sound, Vector2 position, bool globalPitch, float volume) {
            Write("world_volume", sound, position, globalPitch, volume);
        }

        private static void OnSoundLoud(string sound, Vector2 position, bool globalPitch, float volume) {
            Write("world_loud", sound, position, globalPitch, volume);
        }

        private static void Write(string channel, string sound, Vector2? position,
                bool globalPitch, float? volume) {
            if (string.IsNullOrEmpty(sound)
                    || sound.IndexOf("/Hover", StringComparison.OrdinalIgnoreCase) >= 0
                    || sound.StartsWith("FX/Nanobots_", StringComparison.OrdinalIgnoreCase)
                    || sound.Equals("FX/BreakDown", StringComparison.OrdinalIgnoreCase)
                    || sound.Equals("FX/ConstructionComplete", StringComparison.OrdinalIgnoreCase)) { return; }
            SessionLog.Guard("audio.event", () => {
                string pos = position.HasValue
                    ? position.Value.x.ToString("0.###", CultureInfo.InvariantCulture) + ","
                        + position.Value.y.ToString("0.###", CultureInfo.InvariantCulture)
                    : "none";
                string vol = volume.HasValue
                    ? volume.Value.ToString("0.###", CultureInfo.InvariantCulture) : "default";
                SessionLog.Write("Audio", "sound channel=" + channel + " asset=" + sound
                    + " position=" + pos + " globalPitch=" + globalPitch + " volume=" + vol
                    + " caller=" + UIEventHooks.CallerChain());
            });
        }
    }
}
