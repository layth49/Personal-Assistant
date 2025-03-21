﻿using System;
using System.Threading.Tasks;
using PrayTimes; // Library for the PrayTimesCalculator library
using Personal_Assistant.SpeechManager;

namespace Personal_Assistant.PrayerTimesCalculator
{
    public class GetPrayerTimes
    {
        SpeechService speechManager = new SpeechService();

        // Class to handle prayer time calculations and announcements
        private readonly double latitude;  // Latitude of the user's location
        private readonly double longitude; // Longitude of the user's location
        private readonly CalculationMethods calculationMethod; // Calculation method for prayer times (e.g., ISNA)
        private readonly AsrJuristicMethods asrJuristicMethod; // Juristic method for Asr prayer calculation (e.g., Shafii)

        public GetPrayerTimes(double latitude, double longitude, CalculationMethods calculationMethod = CalculationMethods.ISNA, AsrJuristicMethods asrJuristicMethod = AsrJuristicMethods.Shafii)
        {
            // Constructor to initialize class properties
            this.latitude = latitude;
            this.longitude = longitude;
            this.calculationMethod = calculationMethod;
            this.asrJuristicMethod = asrJuristicMethod;
        }

        public Times CalculatePrayerTimes(DateTime date)
        {
            // Function to calculate prayer times for a specific date
            PrayTimesCalculator calc = new PrayTimesCalculator(latitude, longitude)
            {
                CalculationMethod = calculationMethod,
                AsrJuristicMethod = asrJuristicMethod
            };

            return calc.GetPrayerTimes(date, TimeZoneOffset - DaylightSavingsOffset);
        }

        public async Task AnnouncePrayerTimes(DateTime date)
        {
            // Function to announce prayer times with text-to-speech (asynchronous)
            Times prayerTimes = CalculatePrayerTimes(date);

            string[] prayers = dayOfWeek == "Friday" ?
                new[] { "Fajr", "Jumuah", "Asr", "Maghrib", "Isha" } :
                new[] { "Fajr", "Dhuhr", "Asr", "Maghrib", "Isha" };

            string[] texts = dayOfWeek == "Friday" ?
                new[] { "فَجْر", "جمعة", "عسر", "مغرب", "عشع" } :
                new[] { "فَجْر", "ظهر", "عسر", "مغرب", "عشع" };

            for (int i = 0; i < prayers.Length; i++)
            {
                string prayerName = prayers[i];
                string prayerText = texts[i];

                Console.WriteLine($"Assistant: {prayerName} is at: {Format12HourTime(GetPrayerTime(prayerTimes, prayerName))}");

                //                                          Hey! That's my name 😃
                await speechManager.SynthesizeTextToSpeech("ar-SY-LaithNeural", prayerText); // Arabic text-to-speech

                // English text-to-speech
                await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"is at: {Format12HourTime(GetPrayerTime(prayerTimes, prayerName))}");
            }
        }

        private TimeSpan GetPrayerTime(Times times, string prayerName)
        {
            // Function to get the specific prayer time from the Times object
            switch (prayerName)
            {
                case "Fajr":
                    return times.Fajr;
                case "Dhuhr":
                    return times.Dhuhr;
                case "Jumuah":
                    return times.Dhuhr; // Jumuah is same time as Dhuhr on Fridays
                case "Asr":
                    return times.Asr;
                case "Maghrib":
                    return times.Maghrib;
                case "Isha":
                    return times.Isha;
                default:
                    throw new ArgumentException("Invalid prayer name");
            }
        }

        private string Format12HourTime(TimeSpan time)
        {
            // Function to format the prayer time in 12-hour format
            DateTime dateTime = DateTime.Today.Add(time);
            return dateTime.ToString("h:mm tt"); // e.g., 5:30 AM
        }

        private int TimeZoneOffset => (int)TimeZone.CurrentTimeZone.GetUtcOffset(DateTime.Now.Date).TotalHours;
        private int DaylightSavingsOffset => TimeZone.CurrentTimeZone.IsDaylightSavingTime(DateTime.Now.Date) ? 1 : 0;
        private string dayOfWeek => DateTime.Now.DayOfWeek.ToString();
    }
}