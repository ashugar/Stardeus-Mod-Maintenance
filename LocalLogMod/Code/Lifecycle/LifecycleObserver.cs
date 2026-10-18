using Game.Data;
using LocalLogMod.Logging;

namespace LocalLogMod.Lifecycle {
    internal static class LifecycleObserver {
        internal static void Attach(GameState state) {
            state.Sig.BeforeSave.AddListener(BeforeSave);
        }

        internal static void Detach(GameState state) {
            state?.Sig?.BeforeSave.RemoveListener(BeforeSave);
        }

        internal static void Loaded(GameState state) {
            if (SessionLog.Lifecycle) {
                SessionLog.Write("Lifecycle", "state_loaded tick=" + state.Ticks);
            }
        }

        private static void BeforeSave(GameState state, bool willExit) {
            if (SessionLog.Lifecycle) {
                SessionLog.Guard("lifecycle.save", () => SessionLog.Write("Lifecycle",
                    "before_save tick=" + state.Ticks + " willExit=" + willExit));
            }
            SessionLog.Flush();
        }

        internal static void Unloaded() {
            if (SessionLog.Lifecycle) { SessionLog.Write("Lifecycle", "state_unloaded"); }
        }
    }
}
