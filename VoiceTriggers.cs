using Personal_Assistant.Configuration;
using Personal_Assistant.Dispatch;
using Personal_Assistant.Power;
using Personal_Assistant.Presence;
using Personal_Assistant.ProcessControl;
using Personal_Assistant.Resume;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Personal_Assistant.Triggers
{
    // Why a standing rule was refused, so the model can say something better than
    // "that didn't work".
    public sealed class TriggerRejection
    {
        public string Reason { get; }        // for the model / the log
        public string Spoken { get; }        // what the user hears

        public TriggerRejection(string spoken, string reason)
        {
            Spoken = spoken;
            Reason = reason;
        }
    }

    // Standing rules the user created by voice.
    //
    // The whole point of the design: the model parses an utterance into a
    // TriggerSpec exactly ONCE, at creation. From then on the rule is evaluated
    // locally by TriggerService, forever, for free. That is what makes "tell me
    // when Photoshop closes" affordable on a one-second ticker — the condition is
    // a process lookup, not a prompt.
    //
    // This class owns three things the spec deliberately doesn't: the live
    // binding to the engine, the store, and the question of what a rule created
    // by voice is allowed to DO.
    public sealed class VoiceTriggers
    {
        // Tools a standing rule may never run.
        //
        // The distinction is not "dangerous" — it is whether an action can reach
        // outside this machine and do something no one can undo by talking to the
        // assistant afterwards. Everything here fires unattended, with nobody
        // present to be asked "are you sure", which is exactly the guarantee
        // send_sms's confirmation flow depends on.
        //
        // Update this when a new irreversible or outward-facing tool is added.
        private static readonly Dictionary<string, string> Forbidden =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Reaches another person, cannot be recalled, and its read-back
                // confirmation is meaningless with no one there to confirm.
                { "send_sms", "sends a real text and cannot be undone" },
                // Physical security. A door that opens on a schedule while the
                // house is empty is a different category of mistake.
                { "control_door", "controls a physical door" },
                // Would stop the process that runs the triggers.
                { "exit_assistant", "would shut the assistant down" },
                // Self-replication and self-deletion loops.
                { "set_trigger", "would let a rule create more rules" },
                { "cancel_trigger", "would let a rule delete rules" },
                // Composition primitives: both can block for a long time, and a
                // ticker-driven action that sleeps is a ticker that stops.
                { "repeat", "is a composition primitive, not an action" },
                { "wait", "is a composition primitive, not an action" },
            };

        private readonly TriggerService triggers;
        private readonly ProcessController processes;
        private readonly Func<string, Task> announce;
        // On this branch handlers speak for themselves and return no result, so
        // there is no `speak` flag to pass and nothing to inspect afterwards —
        // see FireAsync for what that costs.
        private readonly Func<string, IReadOnlyDictionary<string, string>, Task> runTool;
        private readonly Func<string, Task> prepare;
        private readonly Func<string, bool> isKnownTool;

        private readonly List<TriggerSpec> specs = new List<TriggerSpec>();
        private readonly object gate = new object();

        // One per file_appears rule, keyed by trigger name. Each holds an OS
        // handle on a directory, so they are disposed with the rule.
        private readonly Dictionary<string, FinishedFileWatcher> watchers =
            new Dictionary<string, FinishedFileWatcher>(StringComparer.Ordinal);

        private readonly BatteryReader battery;
        private readonly Func<TimeSpan> idle;

        public VoiceTriggers(
            TriggerService triggers,
            ProcessController processes,
            Func<string, Task> announce,
            Func<string, IReadOnlyDictionary<string, string>, Task> runTool,
            Func<string, bool> isKnownTool,
            Func<string, Task> prepare = null,
            BatteryReader battery = null,
            Func<TimeSpan> idle = null)
        {
            this.triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
            this.processes = processes ?? throw new ArgumentNullException(nameof(processes));
            this.announce = announce ?? throw new ArgumentNullException(nameof(announce));
            this.runTool = runTool;
            this.isKnownTool = isKnownTool ?? (_ => true);
            this.prepare = prepare;
            this.battery = battery ?? new BatteryReader();
            // Injected for the same reason PresenceGate's is: harnesses that read
            // the real one end up testing how long ago someone touched the mouse.
            this.idle = idle ?? PresenceGate.IdleTime;
        }

        /// <summary>
        /// Restores the rules from disk and arms them. Spent one-shots are dropped
        /// rather than resurrected — a "remind me at 6" from yesterday must not
        /// come back at 6 today just because the app restarted.
        /// </summary>
        public ResumeSummary Restore() => Restore(null);

        /// <summary>
        /// As above, but able to tell a rule that EXPIRED from one that was
        /// MISSED.
        ///
        /// Those used to be the same thing here, and it was the quiet half of the
        /// timer bug: "remind me at 6pm", on a day the machine was rebooted at 5,
        /// came back as a spent one-shot and was deleted at startup without a
        /// word. From the user's side that is indistinguishable from the rule
        /// never having been set. Given <paramref name="lastSeen"/> we can say
        /// which side of the downtime its moment fell on, and a rule whose moment
        /// arrived while nobody was running is reported rather than binned.
        /// </summary>
        public ResumeSummary Restore(DateTime? lastSeen)
        {
            var summary = new ResumeSummary();

            List<TriggerSpec> loaded = TriggerStore.Load();
            DateTime now = DateTime.Now;
            TimeSpan catchUp = TimeSpan.FromMinutes(
                LaithConfig.Int("ReminderCatchUpMinutes", 60, 0, 10080));

            var live = new List<TriggerSpec>();

            foreach (TriggerSpec spec in loaded)
            {
                if (!spec.IsExpired(now))
                {
                    live.Add(spec);
                    continue;
                }

                DateTime due = spec.DueAt();

                // Due while we were off, and recent enough to still matter.
                bool missedDuringDowntime = lastSeen.HasValue && due > lastSeen.Value;
                if (missedDuringDowntime && now - due <= catchUp)
                {
                    summary.Missed.Add(DescribeMissedRule(spec, now - due));
                }
                else
                {
                    summary.Dropped.Add(spec.Describe());
                }
            }

            int dropped = loaded.Count - live.Count;

            lock (gate)
            {
                specs.Clear();
                specs.AddRange(live);
                if (dropped > 0) PersistLocked();
            }
            foreach (TriggerSpec spec in live) Bind(spec);

            summary.Resumed.AddRange(live.Select(s => s.Describe()));

            if (loaded.Count == 0) return summary;
            Console.WriteLine(
                $"[triggers] restored {live.Count} standing rule(s)" +
                (dropped > 0 ? $", dropped {dropped} spent one-shot(s)" : ""));
            foreach (string s in summary.Dropped) Console.WriteLine($"[triggers] expired unheard: {s}");

            return summary;
        }

        // Phrased to slot into ResumeSummary's one sentence, so it reads as
        // "While I was off, your reminder to take the bins out came due 20
        // minutes ago." A rule with no message names what it would have done.
        private static string DescribeMissedRule(TriggerSpec spec, TimeSpan late)
        {
            string what =
                !string.IsNullOrWhiteSpace(spec.Message) ? $"your reminder to {spec.Message.TrimEnd('.', '!', '?')}" :
                !string.IsNullOrWhiteSpace(spec.RunTool) ? $"your rule to run {spec.RunTool.Replace('_', ' ')}" :
                "one of your reminders";

            string when =
                late < TimeSpan.FromMinutes(2) ? "just now" :
                late < TimeSpan.FromHours(1) ? $"{late.TotalMinutes:F0} minutes ago" :
                $"{late.TotalHours:F0} hours ago";

            return $"{what} came due {when}";
        }

        /// <summary>
        /// Validates and arms a new rule. Returns the stored spec, or a rejection
        /// explaining why not — the model relays that rather than claiming success.
        /// </summary>
        public TriggerSpec Add(TriggerSpec spec, out TriggerRejection rejected)
        {
            rejected = Validate(spec);
            if (rejected != null) return null;

            lock (gate)
            {
                spec.Id = specs.Count == 0 ? 1 : specs.Max(s => s.Id) + 1;
                spec.CreatedAt = DateTime.Now;

                // Pin a one-shot to an absolute moment at creation, while we still
                // know which day was meant. Deciding that later — on a restart, in
                // the small hours — is what made "remind me at 00:10" said at
                // 23:50 look like it had already happened.
                if (spec.When == TriggerWhen.AtTime &&
                    spec.Repeat == TriggerRepeat.Once &&
                    !spec.FireAt.HasValue)
                {
                    DateTime at = DateTime.Today.Add(spec.TimeOfDay);
                    if (at <= DateTime.Now) at = at.AddDays(1);
                    spec.FireAt = at;
                }

                specs.Add(spec);
                PersistLocked();
            }

            // Armed after it is on disk, and outside the lock: Bind reaches into
            // TriggerService, and holding this lock across that call is the one
            // ordering that could ever deadlock against the ticker.
            Bind(spec);

            // Render the clip now rather than when it fires. A voice-authored
            // message can't be pre-rendered at build time the way the greetings
            // are, but it can be rendered at creation — which is minutes or hours
            // before it is needed, exactly like a reminder's label.
            if (prepare != null && !string.IsNullOrWhiteSpace(spec.Message))
            {
                string message = spec.Message;
                Task.Run(async () =>
                {
                    try { await prepare(message).ConfigureAwait(false); }
                    catch (Exception ex) { Console.WriteLine($"[triggers] prepare failed: {ex.Message}"); }
                });
            }

            return spec;
        }

        public IReadOnlyList<TriggerSpec> Snapshot()
        {
            lock (gate) { return specs.OrderBy(s => s.Id).ToList(); }
        }

        // Writes the current rules to disk. MUST be called with `gate` held.
        //
        // Snapshotting and writing have to be one atomic step. Serialising the
        // writes alone is not enough: rules retire themselves as they fire, on
        // independent threads, so two savers can each take a snapshot, then land
        // in the other order — and the one carrying the older list wins, putting
        // back a rule that had already been deleted. Eight rules due on the same
        // second reliably left one behind on disk.
        //
        // Lock order is always VoiceTriggers.gate -> TriggerStore.saveGate, and
        // nothing takes them the other way round.
        private void PersistLocked()
        {
            TriggerStore.Save(specs.OrderBy(s => s.Id).ToList());
        }

        // Releases the directory handle behind a file_appears rule. Called with
        // `gate` held, like PersistLocked.
        private void DisposeWatcherLocked(string triggerName)
        {
            if (!watchers.TryGetValue(triggerName, out FinishedFileWatcher watcher)) return;
            watchers.Remove(triggerName);
            try { watcher.Dispose(); } catch { }
        }

        /// <summary>Cancels one rule by its position in the listed order (1-based).</summary>
        public TriggerSpec CancelAt(int oneBasedIndex)
        {
            TriggerSpec removed;
            lock (gate)
            {
                List<TriggerSpec> ordered = specs.OrderBy(s => s.Id).ToList();
                if (oneBasedIndex < 1 || oneBasedIndex > ordered.Count) return null;
                removed = ordered[oneBasedIndex - 1];
                specs.Remove(removed);
                PersistLocked();
                DisposeWatcherLocked(removed.TriggerName);
            }
            triggers.RemoveWithPrefix(removed.TriggerName);
            return removed;
        }

        /// <summary>Cancels everything; returns how many went.</summary>
        public int CancelAll()
        {
            List<TriggerSpec> removed;
            lock (gate)
            {
                removed = specs.ToList();
                specs.Clear();
                PersistLocked();
                foreach (TriggerSpec s in removed) DisposeWatcherLocked(s.TriggerName);
            }
            foreach (TriggerSpec s in removed) triggers.RemoveWithPrefix(s.TriggerName);
            return removed.Count;
        }

        private TriggerRejection Validate(TriggerSpec spec)
        {
            if (spec == null) return new TriggerRejection("I couldn't work out what to set up.", "null spec");

            bool hasMessage = !string.IsNullOrWhiteSpace(spec.Message);
            bool hasTool = !string.IsNullOrWhiteSpace(spec.RunTool);

            // A battery rule needs neither. "Warn me when I'm down to half an
            // hour" is a complete instruction, and the model reliably says so
            // without a message because the announcement is self-evident — it
            // came back as battery_below(minutes_left=30) and got asked "what
            // should I do when that happens?", which is a silly question.
            //
            // It speaks the LIVE reading at fire time (see FireAsync), which is
            // strictly better than any fixed sentence written hours earlier:
            // "about 25 minutes left, at 12 percent" rather than "battery low".
            bool speaksForItself = spec.When == TriggerWhen.BatteryBelow;

            if (!hasMessage && !hasTool && !speaksForItself)
            {
                return new TriggerRejection(
                    "What should I do when that happens?",
                    "a trigger must either say something or run a tool");
            }

            if (hasTool)
            {
                if (Forbidden.TryGetValue(spec.RunTool, out string why))
                {
                    return new TriggerRejection(
                        $"I can't set that up automatically — {spec.RunTool.Replace('_', ' ')} {why}, " +
                        "so I'd rather you asked me each time.",
                        $"'{spec.RunTool}' is not allowed in an unattended trigger: {why}");
                }
                if (!isKnownTool(spec.RunTool))
                {
                    // A tool that does not exist cannot be what the user asked
                    // for — they can't request a capability the assistant has
                    // never had. Small models bolt a plausible-sounding tool onto
                    // most rules (a 4B model invented `web_search` for "tell me
                    // when my download finishes"), and refusing the whole rule
                    // over it loses the part the user actually wanted.
                    //
                    // Dropping it is NOT the silent-substitution failure this file
                    // guards against elsewhere: that one discards what the user
                    // asked for, this one discards something they never said. A
                    // rule with nothing left to do is still refused, below.
                    Console.WriteLine(
                        $"[triggers] dropping invented run_tool '{spec.RunTool}' — no such tool");
                    spec.RunTool = null;
                    spec.RunToolArgs = null;
                    hasTool = false;

                    if (!hasMessage && !speaksForItself)
                    {
                        return new TriggerRejection(
                            "What should I do when that happens?",
                            "the only action named was a tool that doesn't exist");
                    }
                }
                if (runTool == null)
                {
                    return new TriggerRejection(
                        "I can't run other actions on a schedule right now.",
                        "no RunTool wired");
                }
            }

            switch (spec.When)
            {
                case TriggerWhen.AtTime:
                    if (spec.TimeOfDay < TimeSpan.Zero || spec.TimeOfDay >= TimeSpan.FromDays(1))
                        return new TriggerRejection("What time should that be?", "time out of range");
                    break;

                case TriggerWhen.Every:
                    if (spec.IntervalMinutes < 1)
                        return new TriggerRejection("How often should I do that?", "interval must be >= 1 minute");
                    if (spec.IntervalMinutes > 24 * 60)
                        return new TriggerRejection(
                            "That's longer than a day — set it for a time instead.",
                            "interval over 24h; use AtTime");
                    break;

                case TriggerWhen.AppStarts:
                case TriggerWhen.AppStops:
                    if (string.IsNullOrWhiteSpace(spec.App))
                        return new TriggerRejection("Which app should I watch for?", "app name missing");
                    break;

                case TriggerWhen.FileAppears:
                    if (!TryResolveFolder(spec.Folder, out _))
                    {
                        return new TriggerRejection(
                            $"I couldn't find a folder called {spec.Folder}.",
                            $"no such folder: {spec.Folder}");
                    }
                    break;

                case TriggerWhen.IdleFor:
                case TriggerWhen.OnReturn:
                    if (spec.AwayMinutes < 1)
                    {
                        return new TriggerRejection(
                            "How long away should I count as away?",
                            "away minutes must be >= 1");
                    }
                    break;

                case TriggerWhen.BatteryBelow:
                    if (spec.Percent < 0 || spec.Percent > 100)
                        return new TriggerRejection("What battery level?", "percent must be 0-100");
                    if (spec.Percent == 0 && spec.MinutesLeft == 0)
                    {
                        return new TriggerRejection(
                            "At what battery level should I tell you?",
                            "battery rule needs a percent or a minutes_left");
                    }
                    // Said once, at creation, rather than leaving a rule that looks
                    // armed on a machine that can never satisfy it.
                    BatteryInfo now = battery.Read();
                    if (!now.HasBattery)
                    {
                        return new TriggerRejection(
                            "This machine doesn't have a battery.",
                            "no system battery");
                    }
                    break;
            }

            return null;
        }

        // Turns a stored spec into a live trigger. The one place a spec becomes
        // delegates, so Restore and Add share exactly the same semantics — a rule
        // must not behave differently after a restart than it did when set.
        private void Bind(TriggerSpec spec)
        {
            Func<Task> action = () => FireAsync(spec);

            switch (spec.When)
            {
                case TriggerWhen.AtTime:
                    if (spec.Repeat == TriggerRepeat.Once)
                    {
                        triggers.AddOneShot(spec.TriggerName, spec.DueAt(), WithCleanup(spec, action));
                    }
                    else
                    {
                        triggers.AddDaily(
                            spec.TriggerName, spec.TimeOfDay, action,
                            appliesOn: DayFilterFor(spec.Repeat));
                    }
                    break;

                case TriggerWhen.Every:
                    triggers.AddEvery(
                        spec.TriggerName,
                        TimeSpan.FromMinutes(spec.IntervalMinutes),
                        action,
                        appliesOn: spec.Until.HasValue
                            ? (Func<DateTime, bool>)(slot => slot.TimeOfDay <= spec.Until.Value)
                            : null);
                    break;

                case TriggerWhen.AppStarts:
                    triggers.AddWhen(
                        spec.TriggerName,
                        () => processes.IsRunning(spec.App),
                        action,
                        // An app that flaps (a crash-restart loop, a launcher
                        // respawning) must not become an announcement every second.
                        minInterval: TimeSpan.FromMinutes(1));
                    break;

                case TriggerWhen.AppStops:
                    triggers.AddWhen(
                        spec.TriggerName,
                        () => !processes.IsRunning(spec.App),
                        action,
                        minInterval: TimeSpan.FromMinutes(1));
                    break;

                case TriggerWhen.FileAppears:
                    BindFileWatcher(spec, action);
                    break;

                case TriggerWhen.IdleFor:
                    triggers.AddWhen(
                        spec.TriggerName,
                        () => idle() >= TimeSpan.FromMinutes(spec.AwayMinutes),
                        action,
                        minInterval: TimeSpan.FromMinutes(5),
                        // The condition IS "nobody is here". Running it through the
                        // presence gate would guarantee it never fires — the gate's
                        // whole job is to hold things when the user is away. So
                        // this is the one condition that must bypass it, and it is
                        // why an idle rule is for DOING something (lights off when
                        // you leave) rather than saying something.
                        requiresPresence: false,
                        respectQuietHours: false);
                    break;

                case TriggerWhen.OnReturn:
                {
                    // Latched by the predicate, cleared by the action, so the
                    // "came back" edge is a real transition rather than a level.
                    bool wasAway = false;
                    triggers.AddWhen(
                        spec.TriggerName,
                        () =>
                        {
                            if (idle() >= TimeSpan.FromMinutes(spec.AwayMinutes))
                            {
                                wasAway = true;
                                return false;
                            }
                            return wasAway;
                        },
                        async () =>
                        {
                            wasAway = false;
                            await action().ConfigureAwait(false);
                        },
                        minInterval: TimeSpan.FromMinutes(1));
                    break;
                }

                case TriggerWhen.BatteryBelow:
                    triggers.AddWhen(
                        spec.TriggerName,
                        () => BatteryIsLow(spec),
                        action,
                        // Once it is low it stays low until you plug in, so the
                        // edge only comes round again after a charge. The interval
                        // is belt and braces against a reading that flickers over
                        // the threshold.
                        minInterval: TimeSpan.FromMinutes(10));
                    break;
            }
        }

        // True when the machine is running down and has crossed whichever bound
        // the rule set. Either bound alone counts, so "under 20 percent" and
        // "under half an hour" are both expressible and can be combined.
        private bool BatteryIsLow(TriggerSpec spec)
        {
            BatteryInfo info = battery.Read();
            if (!info.HasBattery || info.OnMains) return false;

            if (spec.Percent > 0 && info.Percent <= spec.Percent) return true;

            // Only when Windows will actually say. It reports "unknown" for the
            // first minute or two after unplugging, and treating that as zero
            // would fire every low-battery rule the moment the plug came out.
            if (spec.MinutesLeft > 0 && info.Remaining.HasValue &&
                info.Remaining.Value <= TimeSpan.FromMinutes(spec.MinutesLeft))
            {
                return true;
            }

            return false;
        }

        // FileSystemWatcher rather than a directory scan on the ticker: the OS is
        // willing to tell us, so asking it 86,400 times a day is waste that grows
        // with the size of the folder. The watcher only decides WHEN — the signal
        // goes back through TriggerService, so a file landing while the user is
        // out still gets the presence gate and the grace window.
        private void BindFileWatcher(TriggerSpec spec, Func<Task> action)
        {
            if (!TryResolveFolder(spec.Folder, out string folder))
            {
                // Reachable on RESTORE, where Validate didn't run in this process
                // — a folder that existed when the rule was made and has since
                // been deleted or was on a drive that isn't mounted.
                Console.WriteLine(
                    $"[triggers] {spec.TriggerName}: folder '{spec.Folder}' is gone — rule not armed");
                return;
            }

            // Debounced: unzipping an archive lands fifty files, and that is one
            // "your download finished", not fifty.
            triggers.AddSignal(spec.TriggerName, action, minInterval: TimeSpan.FromSeconds(30));

            try
            {
                var watcher = new FinishedFileWatcher(
                    folder, spec.Pattern, _ => triggers.Signal(spec.TriggerName));

                lock (gate)
                {
                    // Replacing a rule of the same name must not leak its watcher,
                    // which holds an OS handle on the directory.
                    if (watchers.TryGetValue(spec.TriggerName, out FinishedFileWatcher old))
                    {
                        try { old.Dispose(); } catch { }
                    }
                    watchers[spec.TriggerName] = watcher;
                }
                Console.WriteLine($"[triggers] watching {folder} for {spec.Pattern ?? "any file"}");
            }
            catch (Exception ex)
            {
                // A folder that has gone away, or one we cannot watch. The rule
                // stays registered but will never signal, so say so rather than
                // leaving a rule that looks armed and is not.
                Console.WriteLine($"[triggers] cannot watch {folder}: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolves a spoken folder to a real path. False when one was NAMED and
        /// could not be found — which must not quietly become Downloads. Watching
        /// the wrong folder is indistinguishable from a rule that never fires,
        /// except that the user has been told it is set up.
        /// </summary>
        internal static bool TryResolveFolder(string folder, out string resolved)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                // Nothing named: "tell me when my download finishes" means here.
                resolved = DownloadsFolder();
                return Directory.Exists(resolved);
            }

            string expanded = Environment.ExpandEnvironmentVariables(folder.Trim());
            if (Directory.Exists(expanded)) { resolved = expanded; return true; }

            // A bare name like "Downloads" or "Desktop", which is what someone
            // says out loud, rather than a path.
            string underProfile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded);
            if (Directory.Exists(underProfile)) { resolved = underProfile; return true; }

            resolved = null;
            return false;
        }

        // There is no SpecialFolder for Downloads on .NET Framework; the shell
        // knows it as a KNOWNFOLDERID, but the profile path is right on every
        // ordinary install and needs no interop.
        private static string DownloadsFolder() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        // A one-shot is spent once it has fired, so it has to leave the store too
        // — otherwise it comes back on the next restart.
        private Func<Task> WithCleanup(TriggerSpec spec, Func<Task> action)
        {
            return async () =>
            {
                try { await action().ConfigureAwait(false); }
                finally
                {
                    // Removal and write together — this is the path where several
                    // one-shots retiring at once used to leave one behind.
                    lock (gate)
                    {
                        specs.Remove(spec);
                        PersistLocked();
                    }
                }
            };
        }

        private static Func<DateTime, bool> DayFilterFor(TriggerRepeat repeat)
        {
            switch (repeat)
            {
                case TriggerRepeat.Weekdays:
                    return slot => slot.DayOfWeek != DayOfWeek.Saturday &&
                                   slot.DayOfWeek != DayOfWeek.Sunday;
                case TriggerRepeat.Weekends:
                    return slot => slot.DayOfWeek == DayOfWeek.Saturday ||
                                   slot.DayOfWeek == DayOfWeek.Sunday;
                default:
                    return null; // Daily applies every day
            }
        }

        private async Task FireAsync(TriggerSpec spec)
        {
            if (!string.IsNullOrWhiteSpace(spec.Message))
            {
                await announce(spec.Message).ConfigureAwait(false);
            }
            else if (spec.When == TriggerWhen.BatteryBelow && string.IsNullOrWhiteSpace(spec.RunTool))
            {
                // Read now, not written when the rule was made. The whole value of
                // a battery warning is the number attached to it.
                await announce(battery.Read().Spoken()).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(spec.RunTool) || runTool == null) return;

            // On this branch handlers speak for themselves and there is no way to
            // ask one to stay quiet, so a rule carrying BOTH a message and a tool
            // produces two utterances — where main suppresses the tool's own line
            // and keeps just the message. Left as-is rather than dropping the
            // user's wording: the combination is rare in practice (the model
            // produces a message OR a run_tool for a given request, not both), and
            // saying the same thing twice is a smaller sin than silently
            // discarding the sentence someone chose.
            IReadOnlyDictionary<string, string> args =
                spec.RunToolArgs ?? (IReadOnlyDictionary<string, string>)VoiceCommand.EmptyArgs;

            // Re-checked at fire time, not just at creation. The store is a plain
            // JSON file the user can edit, and a rule loaded from disk has not
            // been through Validate's denylist in this process.
            if (Forbidden.ContainsKey(spec.RunTool))
            {
                Console.WriteLine(
                    $"[triggers] refusing to run '{spec.RunTool}' from a standing rule — " +
                    "it is on the unattended denylist");
                return;
            }

            await runTool(spec.RunTool, args).ConfigureAwait(false);
        }
    }
}
