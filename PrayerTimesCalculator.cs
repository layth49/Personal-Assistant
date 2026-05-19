using System;
using System.Threading.Tasks;
using PrayTimes;
using Personal_Assistant.SpeechManager;

namespace Personal_Assistant.PrayerTimesCalculator
{
    public class GetPrayerTimes
    {
        private readonly SpeechService speechManager = new SpeechService();

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

        public async Task AnnouncePrayerTimes(DateTime date)
        {
            Times prayerTimes = CalculatePrayerTimes(date);
            bool isFriday = date.DayOfWeek == DayOfWeek.Friday;

            string[] prayers = isFriday
                ? new[] { "Fajr", "Jumuah", "Asr", "Maghrib", "Isha" }
                : new[] { "Fajr", "Dhuhr", "Asr", "Maghrib", "Isha" };

            for (int i = 0; i < prayers.Length; i++)
            {
                string name = prayers[i];
                string time12h = Format12HourTime(GetPrayerTime(prayerTimes, name));

                string spokenName = PrayerIpa.TryGetValue(name, out var ipa)
                    ? $"<phoneme alphabet='ipa' ph='{ipa}'>{name}</phoneme>"
                    : name;

                string ssml =
                    "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
                    "<voice name='en-US-AndrewMultilingualNeural'>" +
                    $"{spokenName} is at {time12h}" +
                    "</voice></speak>";

                var synthTask = speechManager.SynthesizeSsmlAsync(ssml);
                speechManager.SpeechBubble(string.Empty, $"{name} is at: {time12h}");
                await synthTask;
            }
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
