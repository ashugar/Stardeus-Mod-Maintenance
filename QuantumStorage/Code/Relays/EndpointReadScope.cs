using System;

namespace Game.Systems.QuantumStorage.Relays
{
    // Contents-only call scope. It does not change local/pooled availability.
    internal static class EndpointReadScope<T> where T : class
    {
        [ThreadStatic] private static T current;
        [ThreadStatic] private static int epoch;
        internal static T Current => current;
        internal struct State
        {
            internal T Previous;
            internal int Epoch;
            internal bool Entered;
        }
        internal static State Enter(T endpoint)
        {
            if (endpoint == null) return default;
            var state = new State { Previous = current, Epoch = epoch, Entered = true };
            current = endpoint;
            return state;
        }
        internal static void Exit(State state)
        {
            if (state.Entered && state.Epoch == epoch) current = state.Previous;
        }
        internal static void Reset() { current = null; unchecked { epoch++; } }
    }
}
