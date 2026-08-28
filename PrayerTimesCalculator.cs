using System;
using PrayTimes;
using Personal_Assistant.Dispatch;

namespace Personal_Assistant.PrayerTimesCalculator
{
    public class GetPrayerTimes
    {
        private readonly double latitude;
        private readonly double longitude;
        private readonly CalculationMethods calculationMethod;
        private readonly AsrJuristicMethods asrJuristicMethod;

        public GetPrayerTimes(
            double latitude,
            double longitude,
            CalculationMethods calculationMethod = CalculationMethods.ISNA,
            AsrJuristicMethods asrJuristicMethod = AsrJuristicMethods.Shafii)
        {
            this.latitude = latitude;
            this.longitude = longitude;
            this.calculationMethod = calculationMethod;
            this.asrJuristicMethod = asrJuristicMethod;
        }

        public Times CalculatePrayerTimes(DateTime date)
        {
            var calc = new PrayTimesCalculator(latitude, longitude)
            {
                CalculationMethod = calculationMethod,
                AsrJuristicMethod = asrJuristicMethod
            };

            return calc.GetPrayerTimes(date, TimeZoneOffset - DaylightSavingsOffset);
        }

        // Plain English transliterations so Kokoro pronounces the Arabic prayer
        // names approximately as an English-speaking Muslim would say them.
        // Kokoro has no SSML support so IPA <phoneme> tags are not an option.
        private static readonly System.Collections.Generic.Dictionary<string, string> PrayerSpoken =
            new System.Collections.Generic.Dictionary<string, string>
            {
                { "[fajr]fad͡ʒer",    "Fajr" },
                { "[dhuhr]ðuhr",   "Dhuhr" },
                { "[jumuah]dʒʊˈmuːə",  "Jumuah" },
                { "[asr]ˈɑsɹ",     "Asr" },
                { "[maghrib]maɣrɪb", "Maghrib" },
                { "[isha]ʕiʃaːʔ",    "Isha" },
            };

        // Today's prayer times as a result: the announcement as plain text, and
        // every prayer as its own data key so a model holding the conversation can
        // answer "when is Maghrib?" from the times rather than from memory.
        //
        // PLAIN TEXT ON PURPOSE. main wraps the same announcement in <speak> /
        // <voice> and puts each name in an IPA <phoneme> tag, because Azure Neural
        // TTS understands them. Kokoro does not — it would read the tags out loud —
        // so pronunciation here stays the spelled-out transliteration in
        // PrayerSpoken above, and ToolResult.Ssml is left unset. See CLAUDE.md.
        //
        // This used to speak the five prayers as five separate utterances, each
        // with its own bubble. They are one utterance and one bubble now, because
        // a result is voiced once by whoever is doing the voicing — the sequence
        // was only possible while this method owned the speaker. The sentences
        // themselves are unchanged.
        public ToolResult DescribePrayerTimes(DateTime date)
        {
            Times prayerTimes = CalculatePrayerTimes(date);
            bool isFriday = date.DayOfWeek == DayOfWeek.Friday;

            string[] prayers = isFriday
                ? new[] { "Fajr", "Jumuah", "Asr", "Maghrib", "Isha" }
                : new[] { "Fajr", "Dhuhr", "Asr", "Maghrib", "Isha" };

            var plain = new System.Text.StringBuilder();
            var facts = ToolResult.None;

            for (int i = 0; i < prayers.Length; i++)
            {
                string name = prayers[i];
                string time12h = Format12HourTime(GetPrayerTime(prayerTimes, name));

                string spoken = PrayerSpoken.TryGetValue(name, out var hint) ? hint : name;

                if (plain.Length > 0) plain.Append(' ');
                plain.Append($"{spoken} is at {time12h}.");

                facts = facts.With(name.ToLowerInvariant(), time12h);
            }

            ToolResult result = ToolResult.Speak(plain.ToString());
            foreach (System.Collections.Generic.KeyValuePair<string, string> kv in facts.Data)
            {
                result = result.With(kv.Key, kv.Value);
            }
            return result.With("date_local", date.ToString("yyyy-MM-dd"));
        }

        private static TimeSpan GetPrayerTime(Times times, string prayerName)
        {
            switch (prayerName)
            {
                case "Fajr": return times.Fajr;
                case "Dhuhr":
                case "Jumuah": return times.Dhuhr;
                case "Asr": return times.Asr;
                case "Maghrib": return times.Maghrib;
                case "Isha": return times.Isha;
                default: throw new ArgumentException($"Invalid prayer name: {prayerName}", nameof(prayerName));
            }
        }

        private static string Format12HourTime(TimeSpan time) =>
            DateTime.Today.Add(time).ToString("h:mm tt");

        private static int TimeZoneOffset =>
            (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now.Date).TotalHours;

        private static int DaylightSavingsOffset =>
            TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now.Date) ? 1 : 0;
    }
}