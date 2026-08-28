using Personal_Assistant.Presence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.Triggers
{
    // How a trigger decides it wants to run.
    internal enum TriggerKind
    {
        Scheduled,   // at a wall-clock moment, optionally repeating
        Conditional, // when a predicate goes false -> true
        Signalled    // when something outside says so
    }

    // A standing rule: something LAITH does without being asked.
    //
    // Distinct from a ScheduledItem in ReminderService, which is a one-shot the
    // user set out loud and can list and cancel by voice. Triggers are system
    // rules — they re-arm themselves, they are not in the reminder list, and
    // `cancel_reminders` does not touch them.
    internal sealed class Trigger
    {
        public string Name;
        public TriggerKind Kind;
        public Func<Task> Action;

        // Scheduled
        public DateTime FireAt;
        public TimeSpan? Repeat;      // null = one-shot

        // Optional filter on a repeating trigger's occurrences: given the slot it
        // is about to run in, may it? A weekday-only rule and an "every 30 minutes
        // until 18:00" rule are the same question asked twice, so they share this
        // rather than each wrapping the action. Checked BEFORE the presence gate,
        // so a Sunday occurrence of a weekday rule is silently skipped instead of
        // being held for five minutes and then logged as dropped.
        public Func<DateTime, bool> AppliesOn;

        // Conditional
        public Func<bool> Condition;
        public bool LastState;
        public TimeSpan MinInterval;
        public DateTime LastFiredAt;

        // Signalled: raised by an event source rather than found by asking. Read
        // and cleared on the ticker, so the trigger still gets the presence gate,
        // the grace window and the hold/drop logic that everything else does —
        // the event only decides WHEN, not whether.
        public int Pending;

        // Shared
        public TimeSpan Grace;        // how long a held trigger keeps trying
        public bool RequiresPresence;
        public bool RespectQuietHours;
        public DateTime? DueSince;    // set the moment it first wanted to run
        public string HeldReason;     // last reason the gate gave, logged once
        public int Busy;              // 1 while the action is running
    }

    // The engine behind everything LAITH does unprompted.
    //
    // Same shape as ReminderService's scheduler — one background ticker, one
    // second, an injected announce path — because that shape is already proven
    // here. What it adds is that a trigger is a standing rule rather than a one
    // shot: a daily trigger re-arms itself for tomorrow, and a conditional one
    // fires on the edge where its predicate becomes true and then waits for it
    // to go false again.
    //
    // Deliberately NOT built on the LLM, even here where the model is a local one
    // with no quota at all. A predicate on this ticker is a question asked 86,400
    // times a day, which is the wrong shape for an LLM call regardless of what it
    // costs. Conditions are local predicates; the model is only involved if an
    // action chooses to involve it.
    //
    // Every fire is gated on presence (see PresenceGate). A trigger that is due
    // while the user is away is HELD, not fired, and retried each tick until its
    // grace window expires — at which point it is dropped and re-armed for its
    // next occurrence. Grace is per trigger because staleness is per trigger: a
    // prayer reminder ten minutes early is worthless once the prayer has begun,
    // where a "you've been at this for two hours" nudge does not much care.
    public sealed class TriggerService : IDisposable
    {
        private readonly List<Trigger> triggers = new List<Trigger>();
        private readonly object gate = new object();
        private readonly PresenceGate presence;
        private readonly Timer ticker;
        private int ticking; // re-entrancy guard

        public TriggerService(PresenceGate presence)
        {
            this.presence = presence ?? throw new ArgumentNullException(nameof(presence));
            ticker = new Timer(_ => Tick(), null, 1000, 1000);
        }

        /// <summary>Runs once, at a wall-clock moment, then forgets itself.</summary>
        /// <param name="requiresPresence">
        /// False for triggers that do not speak. The gate exists to stop the
        /// assistant talking to an empty room; internal bookkeeping — re-planning
        /// a schedule, retrying a lookup — has no business waiting for someone to
        /// touch the keyboard, and gating it means a day nobody sits down for is
        /// a day that never gets planned.
        /// </param>
        public void AddOneShot(
            string name,
            DateTime fireAt,
            Func<Task> action,
            TimeSpan? grace = null,
            bool respectQuietHours = true,
            bool requiresPresence = true)
        {
            Add(new Trigger
            {
                Name = name,
                Kind = TriggerKind.Scheduled,
                FireAt = fireAt,
                Repeat = null,
                Action = action,
                Grace = grace ?? TimeSpan.FromMinutes(5),
                RequiresPresence = requiresPresence,
                RespectQuietHours = respectQuietHours
            });
        }

        /// <summary>
        /// Runs every day at the given time of day. If today's slot has already
        /// passed, the first run is tomorrow.
        /// </summary>
        public void AddDaily(
            string name,
            TimeSpan timeOfDay,
            Func<Task> action,
            TimeSpan? grace = null,
            bool respectQuietHours = true,
            bool requiresPresence = true,
            Func<DateTime, bool> appliesOn = null)
        {
            DateTime first = DateTime.Today.Add(timeOfDay);
            if (first <= DateTime.Now) first = first.AddDays(1);

            Add(new Trigger
            {
                Name = name,
                Kind = TriggerKind.Scheduled,
                FireAt = first,
                Repeat = TimeSpan.FromDays(1),
                Action = action,
                Grace = grace ?? TimeSpan.FromMinutes(5),
                RequiresPresence = requiresPresence,
                RespectQuietHours = respectQuietHours,
                AppliesOn = appliesOn
            });
        }

        /// <summary>
        /// Runs every <paramref name="interval"/>, starting one interval from now.
        /// Use <paramref name="appliesOn"/> to bound it ("every 30 minutes until
        /// six") — an unbounded repeating announcement is a thing you disable
        /// rather than live with.
        /// </summary>
        public void AddEvery(
            string name,
            TimeSpan interval,
            Func<Task> action,
            TimeSpan? grace = null,
            bool respectQuietHours = true,
            bool requiresPresence = true,
            Func<DateTime, bool> appliesOn = null)
        {
            if (interval < TimeSpan.FromMinutes(1)) interval = TimeSpan.FromMinutes(1);

            Add(new Trigger
            {
                Name = name,
                Kind = TriggerKind.Scheduled,
                FireAt = DateTime.Now.Add(interval),
                Repeat = interval,
                Action = action,
                Grace = grace ?? TimeSpan.FromMinutes(5),
                RequiresPresence = requiresPresence,
                RespectQuietHours = respectQuietHours,
                AppliesOn = appliesOn
            });
        }

        /// <summary>
        /// Runs when <paramref name="condition"/> becomes true, and not again
        /// until it has gone false and back. This is the hook for reacting to the
        /// machine rather than the clock — a device appearing, a process starting.
        /// The predicate runs on the ticker, so it must be cheap and must not
        /// block; anything expensive belongs in the action.
        /// </summary>
        public void AddWhen(
            string name,
            Func<bool> condition,
            Func<Task> action,
            TimeSpan? minInterval = null,
            TimeSpan? grace = null,
            bool respectQuietHours = true,
            bool requiresPresence = true)
        {
            // Seed the edge from the world as it is RIGHT NOW, rather than from
            // false. Starting at false means a condition that is already true when
            // the rule is armed reads as a rising edge and fires instantly: "tell
            // me when Discord closes", asked while Discord is closed, answered
            // "Discord has closed" the moment you finished saying it.
            //
            // A standing rule is about a change, so arming one can never itself be
            // the change. If the user wants to know the current state they can ask.
            bool initial;
            try { initial = condition(); }
            catch (Exception ex)
            {
                // Unreadable now is not the same as true now. False is the safe
                // seed: the worst case is one extra announcement when it next
                // becomes readable, rather than one immediately on a guess.
                Console.WriteLine($"[trigger] {name} condition threw while arming: {ex.Message}");
                initial = false;
            }

            Add(new Trigger
            {
                Name = name,
                Kind = TriggerKind.Conditional,
                Condition = condition,
                Action = action,
                LastState = initial,
                MinInterval = minInterval ?? TimeSpan.Zero,
                LastFiredAt = DateTime.MinValue,
                Grace = grace ?? TimeSpan.FromMinutes(2),
                RequiresPresence = requiresPresence,
                RespectQuietHours = respectQuietHours
            });
        }

        /// <summary>
        /// Runs when something calls <see cref="Signal"/> — a FileSystemWatcher,
        /// a device-arrival notification, anything with a real event to raise.
        ///
        /// Preferred over AddWhen wherever an event exists. A predicate on this
        /// ticker is a question asked 86,400 times a day, and for something like
        /// "did a file appear" that means enumerating a directory every second to
        /// hear about something the OS was willing to tell us.
        ///
        /// The signal only decides WHEN. Everything else — the presence gate, the
        /// grace window, hold-then-drop — still applies, which is the whole reason
        /// event sources route through here instead of just calling the action.
        /// </summary>
        /// <param name="minInterval">
        /// Floor between runs. A source can be chattier than the thing it means:
        /// unzipping an archive lands fifty files, and that is one "your download
        /// finished", not fifty.
        /// </param>
        public void AddSignal(
            string name,
            Func<Task> action,
            TimeSpan? minInterval = null,
            TimeSpan? grace = null,
            bool respectQuietHours = true,
            bool requiresPresence = true)
        {
            Add(new Trigger
            {
                Name = name,
                Kind = TriggerKind.Signalled,
                Action = action,
                MinInterval = minInterval ?? TimeSpan.Zero,
                LastFiredAt = DateTime.MinValue,
                Grace = grace ?? TimeSpan.FromMinutes(2),
                RequiresPresence = requiresPresence,
                RespectQuietHours = respectQuietHours
            });
        }

        /// <summary>
        /// Marks a signalled trigger as due. Safe to call from any thread and from
        /// inside an event handler — it sets a flag and returns, so a slow
        /// announcement can never block the source that raised it.
        /// </summary>
        public bool Signal(string name)
        {
            Trigger t;
            lock (gate)
            {
                t = triggers.FirstOrDefault(
                    x => x.Name == name && x.Kind == TriggerKind.Signalled);
            }
            if (t == null) return false;

            // Latched, not counted. Three files landing while one announcement is
            // being made is still one "your download finished" — a queue here
            // would mean the assistant works through a backlog out loud.
            Volatile.Write(ref t.Pending, 1);
            return true;
        }

        // Registering a name that already exists replaces it. Re-planning a day's
        // worth of triggers is then idempotent, which matters because the prayer
        // schedule is re-planned both at midnight and on every app start.
        private void Add(Trigger t)
        {
            lock (gate)
            {
                triggers.RemoveAll(x => x.Name == t.Name);
                triggers.Add(t);
            }
            string when = t.Kind == TriggerKind.Scheduled
                ? t.FireAt.ToString("ddd HH:mm")
                : "on condition";
            Console.WriteLine($"[trigger] registered {t.Name} ({when})");
        }

        /// <summary>Removes every trigger whose name starts with the prefix.</summary>
        public int RemoveWithPrefix(string prefix)
        {
            lock (gate) { return triggers.RemoveAll(t => t.Name.StartsWith(prefix, StringComparison.Ordinal)); }
        }

        /// <summary>
        /// When a named scheduled trigger will next run, or null if there is no
        /// such trigger (or it is conditional, and so has no time).
        /// </summary>
        public DateTime? NextFireAt(string name)
        {
            lock (gate)
            {
                Trigger t = triggers.FirstOrDefault(
                    x => x.Name == name && x.Kind == TriggerKind.Scheduled);
                return t?.FireAt;
            }
        }

        /// <summary>Scheduled triggers still to come, soonest first. For diagnostics.</summary>
        public IReadOnlyList<string> Upcoming()
        {
            lock (gate)
            {
                return triggers
                    .Where(t => t.Kind == TriggerKind.Scheduled)
                    .OrderBy(t => t.FireAt)
                    .Select(t => $"{t.Name} at {t.FireAt:ddd HH:mm}")
                    .ToList();
            }
        }

        private void Tick()
        {
            // A tick that overruns its second must not overlap the next one. The
            // actions themselves are dispatched off-thread, so this only guards
            // the (cheap) evaluation pass — but a slow user-supplied predicate is
            // exactly the kind of thing that would otherwise stack ticks up.
            if (Interlocked.CompareExchange(ref ticking, 1, 0) != 0) return;
            try
            {
                DateTime now = DateTime.Now;
                List<Trigger> snapshot;
                lock (gate) { snapshot = new List<Trigger>(triggers); }

                foreach (Trigger t in snapshot)
                {
                    if (Volatile.Read(ref t.Busy) != 0) continue;
                    if (!WantsToRun(t, now)) continue;

                    // An occurrence this rule doesn't apply to — Sunday for a
                    // weekday rule, or past the "until" bound. Skip to the next
                    // one without involving the presence gate at all: holding and
                    // then dropping something that was never going to run reads
                    // in the log like a real announcement was lost.
                    if (t.AppliesOn != null && !SafeApplies(t, t.FireAt))
                    {
                        Retire(t, now);
                        continue;
                    }

                    if (t.DueSince == null) t.DueSince = now;

                    PresenceVerdict verdict = t.RequiresPresence
                        ? presence.Check(t.RespectQuietHours)
                        : PresenceVerdict.Yes;
                    if (!verdict.Ready)
                    {
                        // Log the hold once per reason rather than once per second.
                        if (t.HeldReason != verdict.Reason)
                        {
                            t.HeldReason = verdict.Reason;
                            Console.WriteLine($"[trigger] {t.Name} held — {verdict.Reason}");
                        }

                        if (now - t.DueSince.Value <= t.Grace) continue; // keep trying

                        Console.WriteLine(
                            $"[trigger] {t.Name} dropped after {t.Grace.TotalMinutes:F0}m held — {verdict.Reason}");
                        Retire(t, now);
                        continue;
                    }

                    // Advance BEFORE dispatching. An announcement takes seconds to
                    // speak, and the ticker keeps running underneath it; a trigger
                    // that still looked due while its own action was in flight
                    // would fire again on the very next tick.
                    Retire(t, now);
                    Dispatch(t);
                }
            }
            catch (Exception ex)
            {
                // The ticker thread dying takes every proactive feature with it,
                // silently. Nothing in the loop should throw; if something does,
                // this keeps the next tick coming.
                Console.WriteLine($"[trigger] tick failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref ticking, 0);
            }
        }

        // A user-authored predicate that throws must not take the ticker down.
        // Treated as "does not apply", which skips the occurrence rather than
        // firing something whose applicability is unknown.
        private static bool SafeApplies(Trigger t, DateTime slot)
        {
            try { return t.AppliesOn(slot); }
            catch (Exception ex)
            {
                Console.WriteLine($"[trigger] {t.Name} appliesOn threw: {ex.Message}");
                return false;
            }
        }

        private static bool WantsToRun(Trigger t, DateTime now)
        {
            if (t.Kind == TriggerKind.Scheduled) return now >= t.FireAt;

            // Deliberately NOT cleared here. A signal held for presence must stay
            // pending — clearing on the first look would mean an event that
            // arrived while the user was away is silently lost, which is the one
            // thing a push source is supposed to be better at than polling.
            // Retire clears it, once the trigger has actually been resolved.
            if (t.Kind == TriggerKind.Signalled)
            {
                if (Volatile.Read(ref t.Pending) == 0) return false;
                return now - t.LastFiredAt >= t.MinInterval;
            }

            bool state;
            try { state = t.Condition(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[trigger] {t.Name} condition threw: {ex.Message}");
                return false;
            }

            // Edge detection. LastState is only updated once the edge has been
            // resolved — fired, dropped, or the condition went away again — so a
            // trigger held for presence does not silently consume its own edge
            // and go quiet until the condition next cycles.
            if (!state)
            {
                t.LastState = false;
                t.DueSince = null;
                t.HeldReason = null;
                return false;
            }
            if (t.LastState) return false;
            if (now - t.LastFiredAt < t.MinInterval) return false;
            return true;
        }

        // Moves a trigger past the occurrence it just resolved: repeating ones to
        // their next slot, one-shots off the list, conditional ones back to
        // waiting for the predicate to cycle.
        private void Retire(Trigger t, DateTime now)
        {
            t.DueSince = null;
            t.HeldReason = null;

            if (t.Kind == TriggerKind.Signalled)
            {
                Volatile.Write(ref t.Pending, 0);
                t.LastFiredAt = now;
                return;
            }

            if (t.Kind == TriggerKind.Conditional)
            {
                t.LastState = true;
                t.LastFiredAt = now;
                return;
            }

            if (t.Repeat.HasValue)
            {
                // Catch up rather than step once: a machine asleep over the
                // weekend would otherwise walk a daily trigger forward one day
                // per tick, firing it on each of them.
                do { t.FireAt = t.FireAt.Add(t.Repeat.Value); } while (t.FireAt <= now);
                return;
            }

            lock (gate) { triggers.Remove(t); }
        }

        private void Dispatch(Trigger t)
        {
            Volatile.Write(ref t.Busy, 1);
            Console.WriteLine($"[trigger] {t.Name} firing");
            Task.Run(async () =>
            {
                try { await t.Action().ConfigureAwait(false); }
                catch (Exception ex) { Console.WriteLine($"[trigger] {t.Name} failed: {ex.Message}"); }
                finally { Volatile.Write(ref t.Busy, 0); }
            });
        }

        public void Dispose()
        {
            ticker?.Dispose();
        }
    }
}
