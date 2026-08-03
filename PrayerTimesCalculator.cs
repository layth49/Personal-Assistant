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

        // IPA phonetic pronunciations for prayer names. The English-trained
        // neural voice has no native phonemes for Arabic letters like ع / ظ,
        // so we approximate using English phonemes that sound closest to how
        // an English-speaking Muslim says the names.
        private static readonly System.Collections.Generic.Dictionary<string, string> PrayerIpa =
            new System.Collections.Generic.Dictionary<string, string>
            {
                { "Fajr",    "fad͡ʒer" },     // FAH-jr
                { "Dhuhr",   "ðuhr" },      // DOO-her
                { "Jumuah",  "dʒʊˈmuːə" },   // joo-MOO-ah
                { "Asr",     "ˈɑsɹ" },       // AH-sr
                { "Maghrib", "maɣrɪb" },    // MAG-rib
                { "Isha",    "ʕiʃaːʔ" },     // EE-shah
            };

        // Today's prayer times as a result: one SSML block carrying the phonemes,
        // the plain-text equivalent for the bubble, and every prayer as its own
        // data key so the Live model can answer "when is Maghrib?" from the times
        // rather than from memory.
        //
        // This used to speak the five prayers as five separate utterances, each
        // with its own bubble. They are one utterance and one bubble now, because
        // a result is voiced once by whoever is doing the voicing — the sequence
        // was only possible while the handler owned the speaker.
        public ToolResult DescribePrayerTimes(DateTime date)
        {
            Times prayerTimes = CalculatePrayerTimes(date);
            bool isFriday = date.DayOfWeek == DayOfWeek.Friday;

            string[] prayers = isFriday
                ? new[] { "Fajr", "Jumuah", "Asr", "Maghrib", "Isha" }
                : new[] { "Fajr", "Dhuhr", "Asr", "Maghrib", "Isha" };

            var spoken = new System.Text.StringBuilder();
            var plain = new System.Text.StringBuilder();
            var result = ToolResult.None;

            spoken.Append("<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>");
            spoken.Append("<voice name='en-US-AndrewMultilingualNeural'>");

            for (int i = 0; i < prayers.Length; i++)
            {
                string name = prayers[i];
                string time12h = Format12HourTime(GetPrayerTime(prayerTimes, name));

                string spokenName = PrayerIpa.TryGetValue(name, out var ipa)
                    ? $"<phoneme alphabet='ipa' ph='{ipa}'>{name}</phoneme>"
                    : name;

                spoken.Append($"{spokenName} is at {time12h}. ");
                if (plain.Length > 0) plain.Append(' ');
                plain.Append($"{name} is at {time12h}.");

                result = result.With(name.ToLowerInvariant(), time12h);
            }

            spoken.Append("</voice></speak>");

            ToolResult withSsml = ToolResult.SpeakSsml(plain.ToString(), spoken.ToString());
            foreach (System.Collections.Generic.KeyValuePair<string, string> kv in result.Data)
            {
                withSsml = withSsml.With(kv.Key, kv.Value);
            }
            return withSsml.With("date_local", date.ToString("yyyy-MM-dd"));
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
