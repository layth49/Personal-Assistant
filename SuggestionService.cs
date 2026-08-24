using Personal_Assistant.Configuration;
using Personal_Assistant.Triggers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.Suggestions
{
    // How talkative the assistant is allowed to be on its own initiative.
    public enum SuggestionLevel
    {
        Off,
        Rare,
        Normal,
        Chatty
    }

    // One thing the assistant might offer, unprompted.
    //
    // A suggestion is NOT an announcement. An announcement tells you something
    // ("Maghrib is in ten minutes"); a suggestion notices something and offers to
    // act on it ("it's 1am and Fajr is at 5:10 — want an alarm?"). The difference
    // matters because the offer is also the only documentation anyone reads: it
    // shows what the assistant can do at the moment doing it would help, which is
    // the one form of discoverability that survives not remembering a feature
    // exists.
    public sealed class Suggestion
    {
        public string Name { get; }

        // How long before this particular suggestion may be made again. Separate
        // from the global budget: "want an alarm for Fajr" is worth offering once
        // a night, and being under the daily cap does not make twice reasonable.
        public TimeSpan Cooldown { get; }

        // The offer to speak, or null when this doesn't apply right now. Runs on
        // the trigger ticker, so it must be cheap and must not block.
        public Func<string> Propose { get; }

        // What accepting does. Returns what to say afterwards, or null for
        // silence. May be null for a suggestion that is purely informational,
        // in which case "yes" has nothing to run.
        public Func<Task<string>> Accept { get; }

        public Suggestion(
            string name,
            Func<string> propose,
            Func<Task<string>> accept = null,
            TimeSpan? cooldown = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Propose = propose ?? throw new ArgumentNullException(nameof(propose));
            Accept = accept;
            Cooldown = cooldown ?? TimeSpan.FromHours(6);
        }
    }

    // An offer that has been made and not yet answered.
    public sealed class PendingSuggestion
    {
        public string Name { get; set; }
        public string Offer { get; set; }
        public Func<Task<string>> Accept { get; set; }
        public DateTime ExpiresAt { get; set; }

        public bool IsLive => DateTime.Now < ExpiresAt;
    }

    // How often the assistant is allowed to volunteer something.
    //
    // Exists as its own type because "it got annoying" and "it never says
    // anything" are the two ways this feature fails, and both are a dial rather
    // than a bug. Two independent limits, because they stop different things: a
    // minimum gap stops a burst, a daily cap stops a drip.
    public sealed class SuggestionBudget
    {
        private readonly object gate = new object();
        private DateTime lastSuggestionAt = DateTime.MinValue;
        private DateTime countingSince = DateTime.Today;
        private int madeToday;

        // Set by voice, and it wins over the config for the rest of the run.
        // "That's getting annoying" is a feeling you have RIGHT NOW, and the
        // answer to it cannot be "edit an XML file and restart me". Not
        // persisted: App.config stays the thing that decides how the assistant
        // starts, so a one-off "be quiet" can't silently become permanent.
        private static SuggestionLevel? runtimeLevel;

        // A bad value in the config would otherwise warn on every budget check —
        // once a minute, forever — for a setting that is wrong exactly once.
        private static bool warnedAboutLevel;

        public static SuggestionLevel Level
        {
            get
            {
                if (runtimeLevel.HasValue) return runtimeLevel.Value;

                string raw = LaithConfig.Text("Suggestions", "normal");
                switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "off": case "none": case "never": return SuggestionLevel.Off;
                    case "rare": case "occasional": return SuggestionLevel.Rare;
                    case "chatty": case "often": return SuggestionLevel.Chatty;
                    case "normal": case "default": return SuggestionLevel.Normal;
                    default:
                        if (!warnedAboutLevel)
                        {
                            warnedAboutLevel = true;
                            Console.WriteLine(
                                $"[suggest] Suggestions='{raw}' isn't off/rare/normal/chatty — using normal.");
                        }
                        return SuggestionLevel.Normal;
                }
            }
        }

        /// <summary>Changes how talkative it is for the rest of this run.</summary>
        public static void SetLevelForThisRun(SuggestionLevel level)
        {
            runtimeLevel = level;
            Console.WriteLine($"[suggest] level set to {level.ToString().ToLowerInvariant()} for this run");
        }

        /// <summary>Back to whatever App.config says.</summary>
        public static void ClearRuntimeLevel() => runtimeLevel = null;

        // The presets. Explicit settings win, so the level is a starting point
        // rather than a straitjacket — set SuggestionMinGapMinutes and the level's
        // gap is ignored.
        public TimeSpan MinGap
        {
            get
            {
                int configured = LaithConfig.Int("SuggestionMinGapMinutes", 0, 0, 1440);
                if (configured > 0) return TimeSpan.FromMinutes(configured);
                switch (Level)
                {
                    case SuggestionLevel.Rare: return TimeSpan.FromHours(4);
                    case SuggestionLevel.Chatty: return TimeSpan.FromMinutes(30);
                    default: return TimeSpan.FromMinutes(90);
                }
            }
        }

        public int PerDay
        {
            get
            {
                int configured = LaithConfig.Int("SuggestionsPerDay", 0, 0, 100);
                if (configured > 0) return configured;
                switch (Level)
                {
                    case SuggestionLevel.Rare: return 3;
                    case SuggestionLevel.Chatty: return 20;
                    default: return 8;
                }
            }
        }

        /// <summary>Whether another suggestion may be made right now.</summary>
        public bool Allows(out string why)
        {
            if (Level == SuggestionLevel.Off) { why = "suggestions are off"; return false; }

            lock (gate)
            {
                // Reset at midnight rather than on a rolling 24 hours: "eight a
                // day" should mean a fresh eight tomorrow, not a queue that
                // unblocks at odd hours.
                if (countingSince.Date != DateTime.Today)
                {
                    countingSince = DateTime.Today;
                    madeToday = 0;
                }

                int cap = PerDay;
                if (madeToday >= cap)
                {
                    why = $"{madeToday} already today (cap {cap})";
                    return false;
                }

                TimeSpan gap = MinGap;
                TimeSpan since = DateTime.Now - lastSuggestionAt;
                if (since < gap)
                {
                    why = $"last one {since.TotalMinutes:F0}m ago (min gap {gap.TotalMinutes:F0}m)";
                    return false;
                }
            }

            why = null;
            return true;
        }

        public void Record()
        {
            lock (gate)
            {
                lastSuggestionAt = DateTime.Now;
                madeToday++;
            }
        }

        public string Describe() =>
            $"{Level.ToString().ToLowerInvariant()} (min gap {MinGap.TotalMinutes:F0}m, up to {PerDay}/day)";
    }

    // Makes the assistant volunteer things.
    //
    // One evaluator on the trigger engine rather than a trigger per suggestion:
    // that gives "at most one at a time" for free, keeps the ordering explicit,
    // and means the frequency budget is consulted in exactly one place. It rides
    // the same presence gate as everything else, so nothing is offered to an
    // empty room or at 4am.
    public sealed class SuggestionService
    {
        private const string TriggerName = "suggest:tick";

        private readonly TriggerService triggers;
        private readonly Func<string, Task> announce;
        private readonly Action<string> remember;
        private readonly List<Suggestion> catalogue = new List<Suggestion>();
        private readonly Dictionary<string, DateTime> lastOffered =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly object gate = new object();

        public SuggestionBudget Budget { get; } = new SuggestionBudget();

        // The offer waiting for an answer. Read by LiveSession, which tells the
        // model about it so "yeah, go on" resolves to something.
        public PendingSuggestion Pending { get; private set; }

        /// <param name="remember">
        /// Records the offer in conversation history. The Live path can't use
        /// this — it never receives ConversationMemory — but the turn-based
        /// fallback can, and there the model resolves "yes" from history.
        /// </param>
        public SuggestionService(
            TriggerService triggers,
            Func<string, Task> announce,
            Action<string> remember = null)
        {
            this.triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
            this.announce = announce ?? throw new ArgumentNullException(nameof(announce));
            this.remember = remember;
        }

        public SuggestionService Add(Suggestion suggestion)
        {
            if (suggestion == null) return this;
            lock (gate) { catalogue.Add(suggestion); }
            return this;
        }

        /// <summary>Starts looking for something worth saying.</summary>
        public void Start()
        {
            Console.WriteLine(SuggestionBudget.Level == SuggestionLevel.Off
                ? "[suggest] off — nothing will be volunteered unless you say otherwise"
                : $"[suggest] {catalogue.Count} suggestion(s), {Budget.Describe()}");

            // Armed even when off, because the level can be raised by voice mid-run
            // and an evaluator that was never scheduled would leave "be a bit
            // chattier" silently doing nothing. Allows() is the gate, and it is
            // checked on every tick rather than once here.

            // Once a minute is plenty: every proposal is about a state that
            // persists for minutes at least, and a tighter loop would only make
            // the same offer arrive marginally sooner.
            triggers.AddEvery(
                TriggerName,
                TimeSpan.FromMinutes(1),
                ConsiderNowAsync,
                // Quiet hours apply. Unlike a prayer time, nothing here is worth
                // being woken for — an offer is by definition optional.
                respectQuietHours: true);
        }

        /// <summary>
        /// Runs whatever the last offer was. Called by the accept_suggestion tool
        /// when the user agrees. Returns what to say, or null if there was
        /// nothing pending — which the caller should treat as "I don't know what
        /// you're agreeing to" rather than silently doing nothing.
        /// </summary>
        public async Task<string> AcceptPendingAsync()
        {
            PendingSuggestion pending;
            lock (gate)
            {
                pending = Pending;
                Pending = null; // consumed either way; "yes" twice is not two yeses
            }

            if (pending == null || !pending.IsLive) return null;
            if (pending.Accept == null) return "Okay.";

            try
            {
                return await pending.Accept().ConfigureAwait(false) ?? "Done.";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[suggest] accepting '{pending.Name}' failed: {ex.Message}");
                return "Sorry, that didn't work.";
            }
        }

        /// <summary>Drops the pending offer, e.g. the user said no.</summary>
        public void DeclinePending()
        {
            lock (gate) { Pending = null; }
        }

        /// <summary>
        /// Makes an offer the user is already expecting, right now, bypassing the
        /// frequency budget.
        ///
        /// The budget exists to stop the assistant volunteering things nobody
        /// asked for. This is the opposite case: the user explicitly asked to be
        /// told when something happened, and it just happened. Running that
        /// through a ninety-minute minimum gap — or dropping it because eight
        /// unrelated suggestions were already made today — would silently swallow
        /// the one announcement that was actually requested, and the user would
        /// have no way to tell that from the event never occurring.
        ///
        /// It still goes through the pending/accept machinery, because that is
        /// what makes "yes, go on" resolve to something, and it still records
        /// against the budget on the way out so a confirmed offer is not
        /// immediately followed by an unrelated one.
        /// </summary>
        public async Task OfferNow(string name, string offer, Func<Task<string>> accept)
        {
            if (string.IsNullOrWhiteSpace(offer)) return;

            lock (gate)
            {
                Pending = new PendingSuggestion
                {
                    Name = name ?? "offer",
                    Offer = offer,
                    Accept = accept,
                    ExpiresAt = DateTime.Now.AddMinutes(10)
                };
            }

            Budget.Record();
            Console.WriteLine($"[suggest] offering '{name}' (asked for): {offer}");

            try { remember?.Invoke(offer); }
            catch (Exception ex) { Console.WriteLine($"[suggest] remember failed: {ex.Message}"); }

            await announce(offer).ConfigureAwait(false);
        }

        /// <summary>
        /// Looks for something worth saying right now, and says at most one thing.
        /// Normally driven by the once-a-minute tick that <see cref="Start"/>
        /// arms; public so it can be run on demand — which is what makes the
        /// feature testable without a minute per case, and leaves room for a
        /// "anything I should know?" command later.
        /// </summary>
        public async Task ConsiderNowAsync()
        {
            if (!Budget.Allows(out string why))
            {
                // Not logged every minute — that would be 1,440 lines a day for
                // a feature that is working as configured.
                return;
            }

            Suggestion chosen = null;
            string offer = null;

            lock (gate)
            {
                foreach (Suggestion s in catalogue)
                {
                    if (lastOffered.TryGetValue(s.Name, out DateTime last) &&
                        DateTime.Now - last < s.Cooldown)
                    {
                        continue;
                    }

                    string proposed;
                    try { proposed = s.Propose(); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[suggest] '{s.Name}' threw while proposing: {ex.Message}");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(proposed)) continue;

                    chosen = s;
                    offer = proposed;
                    break; // first applicable wins; catalogue order is priority
                }

                if (chosen == null) return;

                lastOffered[chosen.Name] = DateTime.Now;
                Pending = new PendingSuggestion
                {
                    Name = chosen.Name,
                    Offer = offer,
                    Accept = chosen.Accept,
                    // Long enough to finish a thought and answer, short enough
                    // that "yes" an hour later doesn't run something forgotten.
                    ExpiresAt = DateTime.Now.AddMinutes(10)
                };
            }

            Budget.Record();
            Console.WriteLine($"[suggest] offering '{chosen.Name}': {offer}");

            // Recorded before speaking, so the fallback path's history has it even
            // if the speech itself fails.
            try { remember?.Invoke(offer); }
            catch (Exception ex) { Console.WriteLine($"[suggest] remember failed: {ex.Message}"); }

            await announce(offer).ConfigureAwait(false);
        }
    }
}
