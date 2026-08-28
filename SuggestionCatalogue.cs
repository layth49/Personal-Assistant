using Personal_Assistant.Dispatch;
using Personal_Assistant.Power;
using Personal_Assistant.PrayerTimesCalculator;
using Personal_Assistant.Triggers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Personal_Assistant.Suggestions
{
    // The things L.A.I.T.H. will volunteer.
    //
    // Every proposal here is built ONLY from state the assistant can actually
    // observe: the clock, prayer times, idle time, battery, volume, and its own
    // rule list. It deliberately contains nothing about the lights, because
    // LightControl fires a process and never reads back — "the bedroom light is
    // still on" is the obvious suggestion to want and the one that cannot be
    // written honestly. Offering it would mean guessing at state and being
    // confidently wrong, which is worse than staying quiet.
    //
    // Order is priority: the evaluator offers the first thing that applies.
    public static class SuggestionCatalogue
    {
        public static void Register(
            SuggestionService suggestions,
            CommandContext context,
            PrayerAnnouncer prayers,
            Func<TimeSpan> idle,
            Func<string, IReadOnlyDictionary<string, string>, Task> runTool)
        {
            suggestions
                .Add(FajrAlarm(prayers, runTool))
                .Add(BatteryFollowUp(context, runTool))
                .Add(LateNightVolume(context, runTool))
                .Add(TryAStandingRule(context, runTool));
        }

        // Late, and Fajr is coming. The one collision that happens most nights:
        // he prays Fajr and codes past 2am, and nothing currently connects those
        // two facts at the moment it would change what he does next.
        private static Suggestion FajrAlarm(
            PrayerAnnouncer prayers,
            Func<string, IReadOnlyDictionary<string, string>, Task> runTool)
        {
            string alarmTime = null;

            return new Suggestion(
                "fajr-alarm",
                propose: () =>
                {
                    // Null when prayer announcements are switched off, which is a
                    // supported configuration — without this the proposal throws on
                    // every evaluation and logs an error a minute for a feature the
                    // user deliberately turned off.
                    if (prayers == null) return null;

                    DateTime now = DateTime.Now;
                    // Small hours only: after midnight and before 4am. Offering
                    // this at 9pm would be nagging about something eight hours off.
                    if (now.Hour >= 4) return null;

                    if (!prayers.TryGetNextPrayer(out string name, out DateTime at)) return null;
                    if (!string.Equals(name, "Fajr", StringComparison.OrdinalIgnoreCase)) return null;

                    TimeSpan until = at - now;
                    if (until <= TimeSpan.Zero || until > TimeSpan.FromHours(6)) return null;

                    // Ten minutes before, so there is time to actually get up.
                    DateTime wake = at.AddMinutes(-10);
                    if (wake <= now.AddMinutes(30)) return null; // too close to be worth an alarm
                    alarmTime = wake.ToString("HH:mm");

                    return $"It's {now:h:mm tt} and Fajr is at {at:h:mm tt}. " +
                           $"Want me to set an alarm for {wake:h:mm tt}?";
                },
                accept: async () =>
                {
                    if (alarmTime == null) return null;
                    await runTool("set_alarm",
                        new Dictionary<string, string> { ["time"] = alarmTime, ["label"] = "Fajr" }).ConfigureAwait(false);
                    return $"Alarm set for {alarmTime}.";
                },
                // Once a night.
                cooldown: TimeSpan.FromHours(12));
        }

        // Running down, and there is a level worth being warned at. The offer is
        // a standing rule, so accepting also demonstrates the rules feature at
        // the exact moment its usefulness is obvious.
        private static Suggestion BatteryFollowUp(
            CommandContext context,
            Func<string, IReadOnlyDictionary<string, string>, Task> runTool)
        {
            return new Suggestion(
                "battery-follow-up",
                propose: () =>
                {
                    BatteryInfo info = context.Battery.Read();
                    if (!info.HasBattery || info.OnMains) return null;

                    // Only in the band where it is worth mentioning but not yet
                    // urgent. Below 20 the battery_below rules take over.
                    if (info.Percent > 40 || info.Percent <= 20) return null;

                    string state = info.Remaining.HasValue
                        ? $"about {(int)info.Remaining.Value.TotalMinutes} minutes left"
                        : $"{info.Percent} percent";

                    return $"You're on battery with {state}. " +
                           "Want me to tell you when you're down to fifteen percent?";
                },
                accept: async () =>
                {
                    await runTool("set_trigger", new Dictionary<string, string>
                    {
                        ["condition"] = "battery_below",
                        ["percent"] = "15"
                    }).ConfigureAwait(false);
                    return "Done — I'll tell you at fifteen percent.";
                },
                cooldown: TimeSpan.FromHours(8));
        }

        // Loud, late, and something is playing. Observable through the volume
        // endpoint, which is real state rather than a guess.
        private static Suggestion LateNightVolume(
            CommandContext context,
            Func<string, IReadOnlyDictionary<string, string>, Task> runTool)
        {
            return new Suggestion(
                "late-night-volume",
                propose: () =>
                {
                    DateTime now = DateTime.Now;
                    if (now.Hour < 23 && now.Hour >= 6) return null;

                    int volume;
                    try { volume = context.Audio.CurrentVolumePercent(); }
                    catch { return null; }
                    if (volume < 60) return null;

                    return $"It's {now:h:mm tt} and your volume is at {volume}. " +
                           "Want me to turn it down?";
                },
                accept: async () =>
                {
                    await runTool("control_volume",
                        new Dictionary<string, string> { ["action"] = "set", ["level"] = "30" }).ConfigureAwait(false);
                    return "Turned it down.";
                },
                cooldown: TimeSpan.FromHours(6));
        }

        // The discoverability one, and the reason this whole feature exists: a
        // capability you have to remember is a capability you don't have. This
        // offers a standing rule the user hasn't got, at a moment when it makes
        // sense, so the feature explains itself by doing rather than by being
        // read about.
        //
        // Only while the rule list is nearly empty. Once there are a few rules
        // the user has clearly got the idea and this becomes noise.
        private static Suggestion TryAStandingRule(
            CommandContext context,
            Func<string, IReadOnlyDictionary<string, string>, Task> runTool)
        {
            var idea = new Dictionary<string, string>();

            return new Suggestion(
                "try-a-standing-rule",
                propose: () =>
                {
                    VoiceTriggers rules = context.VoiceTriggers;
                    if (rules == null) return null;
                    if (rules.Snapshot().Count >= 2) return null;

                    // Offered against what is happening now, not at random — a
                    // download rule while something is downloading is a
                    // demonstration; the same words out of nowhere are an advert.
                    BatteryInfo battery = context.Battery.Read();
                    if (battery.HasBattery && !battery.OnMains)
                    {
                        idea["condition"] = "battery_below";
                        idea["percent"] = "20";
                        return "By the way — I can watch things in the background and tell you " +
                               "when they happen. Want me to warn you at twenty percent battery?";
                    }

                    idea["condition"] = "file_appears";
                    idea["message"] = "Your download has finished.";
                    return "By the way — I can watch things in the background and tell you when " +
                           "they happen. Want me to tell you when a download finishes?";
                },
                accept: async () =>
                {
                    await runTool("set_trigger",
                        new Dictionary<string, string>(idea)).ConfigureAwait(false);
                    return "Set up. Ask me what standing rules you have any time.";
                },
                // Rarely. This is a nudge, and a nudge repeated is a nag.
                cooldown: TimeSpan.FromDays(2));
        }
    }
}
