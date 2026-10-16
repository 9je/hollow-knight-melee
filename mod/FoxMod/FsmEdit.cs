using System;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace FoxMelee {
    /// Small edits to Hollow Knight's PlayMaker state machines, done once while they run (states, transitions, actions).
    static class FsmEdit {
        public static FsmState State(PlayMakerFSM fsm, string name) => fsm.FsmStates.FirstOrDefault(s => s.Name == name);

        public static T Action<T>(FsmState s) where T : FsmStateAction => s?.Actions.OfType<T>().FirstOrDefault();
        public static T[] Actions<T>(FsmState s) where T : FsmStateAction => s?.Actions.OfType<T>().ToArray() ?? new T[0];

        /// A copy of a state under a new name, its transitions to the same states. The copy's actions are new objects
        /// (made from the state's saved data), so changing them leaves the original alone.
        public static FsmState Copy(PlayMakerFSM fsm, FsmState from, string name) {
            from.SaveActions();
            var copy = new FsmState(from) { Name = name };
            foreach (var t in copy.Transitions) t.ToFsmState = fsm.Fsm.GetState(t.ToState);
            fsm.Fsm.States = fsm.Fsm.States.Concat(new[] { copy }).ToArray();
            fsm.Fsm.SaveActions();
            return copy;
        }

        public static void AddTransition(FsmState s, string evt, FsmState to) =>
            s.Transitions = s.Transitions.Concat(new[] { new FsmTransition { ToState = to.Name, ToFsmState = to, FsmEvent = FsmEvent.GetFsmEvent(evt) } }).ToArray();

        public static void Insert(FsmState s, int at, FsmStateAction a) {
            var list = s.Actions.ToList();
            list.Insert(Math.Max(0, Math.Min(list.Count, at)), a);
            s.Actions = list.ToArray();
            a.Init(s);
        }

        public static void Remove<T>(FsmState s) where T : FsmStateAction => s.Actions = s.Actions.Where(a => !(a is T)).ToArray();

        /// Sends an event as the state is entered (a page that is always there answers "is there another page?" yes).
        public sealed class SendNow : FsmStateAction {
            public FsmEvent Send;
            public override void OnEnter() { Fsm.Event(Send); Finish(); }
        }

        /// Turns a GameObject off as the state is entered (the inventory closing puts its pages away).
        public sealed class Deactivate : FsmStateAction {
            public GameObject Target;
            public override void OnEnter() { if (Target != null) Target.SetActive(false); Finish(); }
        }
    }
}
