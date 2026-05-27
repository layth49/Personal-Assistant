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

        // Plain English transliterations so Kokoro pronounces the Arabic prayer
        // names approximately as an English-speaking Muslim would say them.
        // Kokoro has no SSML support so IPA <phoneme> tags are not an option.
        private static readonly System.Collections.Generic.Dictionary<string, string> PrayerSpoken =
            new System.Collections.Generic.Dictionary<string, string>
            {
                { "Fajr",    "FAH jr" },
                { "Dhuhr",   "DOO her" },
                { "Jumuah",  "joo MOO ah" },
                { "Asr",     "AH sr" },
                { "Maghrib", "MAG rib" },
                { "Isha",    "EE shah" },
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

                string spoken = PrayerSpoken.TryGetValue(name, out var hint) ? hint : name;
                string bubbleText = $"{name} is at: {time12h}";

                await speechManager.Say(bubbleText, $"{spoken} is at {time12h}");
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
