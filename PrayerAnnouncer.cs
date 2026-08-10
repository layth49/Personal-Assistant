using Personal_Assistant.Configuration;
using Personal_Assistant.Geolocator;
using Personal_Assistant.Triggers;
using PrayTimes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Personal_Assistant.PrayerTimesCalculator
{
    // Announces each prayer without being asked.
    //
    // The calculation was already here — GetPrayerTimes.CalculatePrayerTimes is
    // what the `get_prayer_times` tool answers from. All this adds is the other
    // direction: instead of waiting to be asked what time Maghrib is, say so
    // shortly before it arrives. Nothing here talks to the LLM.
    //
    // A day is planned in one pass, on startup and again just after midnight,
    // rather than one prayer at a time. Planning needs a location lookup, which
    // is a network call that can fail; doing it five times a day would be five
    // chances to fail where one will do.
    public sealed class PrayerAnnouncer
    {
        private const string TriggerPrefix = "prayer:";
        private const string TimePrefix = TriggerPrefix + "time:";

        // Every prayer name that can be announced, including the Friday
        // replacement for Dhuhr. ClipLines() renders one announcement per name,
        // so this is also the list that decides what gets pre-rendered.
        public static readonly string[] PrayerNames =
            { "Fajr", "Dhuhr", "Jumuah", "Asr", "Maghrib", "Isha" };

        private readonly TriggerService triggers;
        private readonly GetLocation location;
        private readonly Func<string, Task> announce;
        private readonly Func<string, Task> prepare;

        // Resolved once and kept. Prayer times move by a minute or two a day at a
        // fixed location; a laptop that has genuinely moved gets a fresh lookup
        // on the next app start, which is soon enough for a calculation whose
        // inputs change this slowly.
        private double? latitude;
        private double? longitude;

        public PrayerAnnouncer(
            TriggerService triggers,
            GetLocation location,
            Func<string, Task> announce,
            Func<string, Task> prepare = null)
        {
            this.triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
            this.location = location ?? throw new ArgumentNullException(nameof(location));
            this.announce = announce ?? throw new ArgumentNullException(nameof(announce));
            this.prepare = prepare;
        }

        // How long before each prayer to speak. 0 announces at the time itself.
        public static int LeadMinutes => LaithConfig.Int("PrayerLeadMinutes", 10, 0, 60);

        // Per-prayer default leads, where the general one is the wrong length.
        // Jumuah is the case that matters: ten minutes is right for a prayer you
        // do where you are and useless for one you travel to, so Friday gets
        // enough notice to actually leave.
        private static readonly Dictionary<string, int> DefaultLeadFor =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "Jumuah", 30 },
            };

        /// <summary>
        /// How long before <paramref name="prayer"/> to speak. Each prayer can be
        /// set individually in config — `JumuahLeadMinutes`, `FajrLeadMinutes` —
        /// falling back to that prayer's built-in default and then to the general
        /// `PrayerLeadMinutes`.
        /// </summary>
        public static int LeadFor(string prayer)
        {
            int fallback = DefaultLeadFor.TryGetValue(prayer ?? string.Empty, out int special)
                ? special
                : LeadMinutes;
            return LaithConfig.Int($"{prayer}LeadMinutes", fallback, 0, 180);
        }

        // The exact words spoken, shared with the pre-render list so the two
        // cannot drift. A clip cache keyed on the text turns any difference here
        // into a silent miss and the wrong voice — the same trap ReminderService
        // has AnnouncementFor for.
        public static string AnnouncementFor(string prayer, int leadMinutes)
        {
            if (leadMinutes <= 0) return $"It's time for {prayer}.";
            if (leadMinutes == 1) return $"{prayer} is in one minute.";
            return $"{prayer} is in {leadMinutes} minutes.";
        }

        // Every line this can ever say, for --render-clips. Per-prayer leads mean
        // the lines are no longer six variations on one number, so each name is
        // rendered against its OWN lead — render them against the general one and
        // Friday misses the cache and arrives in the wrong voice.
        public static IReadOnlyList<string> ClipLines()
        {
            var lines = new List<string>(PrayerNames.Length);
            foreach (string name in PrayerNames) lines.Add(AnnouncementFor(name, LeadFor(name)));
            return lines;
        }

        /// <summary>
        /// Plans today and arms the nightly re-plan. Safe to call and forget —
        /// it never throws, because a failed location lookup at startup must not
        /// be the reason the assistant doesn't start.
        /// </summary>
        public async Task StartAsync()
        {
            // Just after midnight, not at it: the calculation is for a date, and
            // asking for "today" within a second of the boundary is the kind of
            // thing that works for years and then doesn't.
            triggers.AddDaily(
                TriggerPrefix + "replan",
                TimeSpan.FromMinutes(5),
                () => PlanDayAsync(DateTime.Now),
                // The re-plan is bookkeeping, not an announcement: it says
                // nothing, so the presence gate has no opinion worth having
                // about it. Gating it would mean a night nobody is at the
                // machine is a day whose prayers are never armed — the schedule
                // has to be ready before the user arrives, not because of it.
                respectQuietHours: false,
                requiresPresence: false);

            await PlanDayAsync(DateTime.Now).ConfigureAwait(false);
        }

        private async Task PlanDayAsync(DateTime date)
        {
            try
            {
                if (!await EnsureLocationAsync().ConfigureAwait(false))
                {
                    // Retry sooner than tomorrow. A location lookup that failed at
                    // startup has usually failed because the network wasn't up yet,
                    // and waiting until 00:05 would cost the whole day's prayers.
                    triggers.AddOneShot(
                        TriggerPrefix + "retry",
                        DateTime.Now.AddMinutes(30),
                        () => PlanDayAsync(DateTime.Now),
                        respectQuietHours: false,
                        requiresPresence: false);
                    return;
                }

                // Yesterday's leftovers, and any earlier plan for today. Add()
                // replaces by name, but a prayer that has already fired is off the
                // list entirely, so clearing first is what makes a re-plan mid-day
                // match what a fresh start would have produced.
                triggers.RemoveWithPrefix(TimePrefix);

                var calculator = new GetPrayerTimes(latitude.Value, longitude.Value);
                Times times = calculator.CalculatePrayerTimes(date);

                // Friday's midday prayer is Jumuah, matching DescribePrayerTimes.
                bool isFriday = date.DayOfWeek == DayOfWeek.Friday;
                var todays = new List<KeyValuePair<string, TimeSpan>>
                {
                    new KeyValuePair<string, TimeSpan>("Fajr", times.Fajr),
                    new KeyValuePair<string, TimeSpan>(isFriday ? "Jumuah" : "Dhuhr", times.Dhuhr),
                    new KeyValuePair<string, TimeSpan>("Asr", times.Asr),
                    new KeyValuePair<string, TimeSpan>("Maghrib", times.Maghrib),
                    new KeyValuePair<string, TimeSpan>("Isha", times.Isha),
                };

                // The times are rendered in the SYSTEM clock's time zone, not the
                // coordinates'. When those two disagree — a laptop carried across
                // zones with the clock not yet updated, Windows location services
                // reporting somewhere the clock doesn't match — a prayer can wrap
                // past midnight and come back as an early-morning time as an
                // evening one. Announcing off that is worse than not announcing:
                // it is confidently, quietly several hours wrong. Out-of-order
                // times are the tell, and the only sane response is to say so and
                // arm nothing.
                for (int i = 1; i < todays.Count; i++)
                {
                    if (todays[i].Value > todays[i - 1].Value) continue;
                    Console.WriteLine(
                        "[prayer] times came back out of order for " +
                        $"{latitude.Value:F3},{longitude.Value:F3} in {TimeZoneInfo.Local.StandardName} (" +
                        string.Join(", ", todays.Select(p =>
                            $"{p.Key} {date.Date.Add(p.Value):HH:mm}")) +
                        ") — the location and the system clock's time zone disagree. " +
                        "No announcements armed today.");
                    return;
                }

                int armed = 0;
                var planned = new List<string>();
                foreach (KeyValuePair<string, TimeSpan> prayer in todays)
                {
                    string name = prayer.Key;
                    int lead = LeadFor(name);
                    DateTime at = date.Date.Add(prayer.Value).AddMinutes(-lead);
                    if (at <= DateTime.Now) continue; // already gone by

                    string message = AnnouncementFor(name, lead);

                    triggers.AddOneShot(
                        TimePrefix + name,
                        at,
                        () => announce(message),
                        // Held announcements stop being worth making once the
                        // prayer itself has started, so the grace window is
                        // exactly the lead time. With no lead there is nothing to
                        // be early by, and a couple of minutes is the most that
                        // "it's time" can survive being late by.
                        grace: lead > 0 ? TimeSpan.FromMinutes(lead) : TimeSpan.FromMinutes(2),
                        // Fajr and Isha routinely sit inside any sane quiet-hours
                        // window. Suppressing them would silence the feature
                        // exactly where it is most wanted, so prayer times are
                        // exempt — the presence check still applies, so an empty
                        // desk at 5am stays quiet.
                        respectQuietHours: false);

                    Prepare(message);
                    planned.Add($"{name} {at:HH:mm} (-{lead}m)");
                    armed++;
                }

                // Leads are per prayer now, so a single "Nm ahead of each" would
                // be a lie on Fridays. Each armed announcement reports its own.
                Console.WriteLine(
                    $"[prayer] planned {date:yyyy-MM-dd}: {armed} announcement(s) to come" +
                    (armed > 0 ? " — " + string.Join(", ", planned) : ""));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[prayer] planning failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The next prayer due, using the location already resolved for the
        /// announcements. False before the first successful location lookup, or
        /// if today's times can't be calculated — callers must treat "don't know"
        /// as a real answer rather than assuming one.
        ///
        /// Exists so other features can reason about prayer times without each
        /// doing its own location lookup: the suggestion that offers a Fajr alarm
        /// at 1am is asking exactly this question.
        /// </summary>
        public bool TryGetNextPrayer(out string name, out DateTime at)
        {
            name = null;
            at = default(DateTime);

            if (!latitude.HasValue || !longitude.HasValue) return false;

            try
            {
                DateTime now = DateTime.Now;
                var calculator = new GetPrayerTimes(latitude.Value, longitude.Value);

                // Today's remaining prayers, then tomorrow's first. After Isha the
                // next prayer is tomorrow's Fajr, and answering "Fajr, this
                // morning, hours ago" is worse than answering nothing.
                for (int dayOffset = 0; dayOffset <= 1; dayOffset++)
                {
                    DateTime day = now.Date.AddDays(dayOffset);
                    Times times = calculator.CalculatePrayerTimes(day);
                    bool isFriday = day.DayOfWeek == DayOfWeek.Friday;

                    var ordered = new List<KeyValuePair<string, TimeSpan>>
                    {
                        new KeyValuePair<string, TimeSpan>("Fajr", times.Fajr),
                        new KeyValuePair<string, TimeSpan>(isFriday ? "Jumuah" : "Dhuhr", times.Dhuhr),
                        new KeyValuePair<string, TimeSpan>("Asr", times.Asr),
                        new KeyValuePair<string, TimeSpan>("Maghrib", times.Maghrib),
                        new KeyValuePair<string, TimeSpan>("Isha", times.Isha),
                    };

                    foreach (KeyValuePair<string, TimeSpan> prayer in ordered)
                    {
                        DateTime when = day.Add(prayer.Value);
                        if (when <= now) continue;
                        name = prayer.Key;
                        at = when;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[prayer] next-prayer lookup failed: {ex.Message}");
            }

            return false;
        }

        private async Task<bool> EnsureLocationAsync()
        {
            if (latitude.HasValue && longitude.HasValue) return true;
            try
            {
                latitude = await location.GetLatitude().ConfigureAwait(false);
                longitude = await location.GetLongitude().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[prayer] location lookup failed: {ex.Message}");
                latitude = null;
                longitude = null;
                return false;
            }
        }

        // Render the clip now, hours before it is needed, so the announcement
        // itself is instant and in the Live voice. Same reasoning as the reminder
        // path: rendering takes 5-7s, which is not something to discover at the
        // moment the thing is supposed to speak.
        private void Prepare(string message)
        {
            if (prepare == null) return;
            Task.Run(async () =>
            {
                try { await prepare(message).ConfigureAwait(false); }
                catch (Exception ex) { Console.WriteLine($"[prayer] prepare failed: {ex.Message}"); }
            });
        }
    }
}
