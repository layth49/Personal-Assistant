using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Python.Runtime;
using System;
using System.IO;
using System.Threading.Tasks;


namespace Personal_Assistant.SpeechManager
{
    public class SpeechService
    {
        public static readonly string speechKey = Environment.GetEnvironmentVariable("SPEECH_KEY");
        public static readonly string speechRegion = Environment.GetEnvironmentVariable("SPEECH_REGION");

        // Shared across SpeechRecognizer / KeywordRecognizer
        public readonly AudioConfig audioConfig = AudioConfig.FromDefaultMicrophoneInput();
        public readonly SpeechConfig speechConfig;

        // Reused — recreating per call adds websocket handshake latency to every TTS.
        // (Azure Speech SDK guidance: reuse SpeechSynthesizer.) The connection
        // is established lazily by the SDK on the first SpeakTextAsync call.
        private readonly SpeechSynthesizer synthesizer;

        // Reused across keyword cycles. Constructing a new KeywordRecognizer per
        // cycle re-opens the microphone via WASAPI and reloads the on-device
        // keyword model — on some machines that setup takes several seconds,
        // during which the SDK misses the keyword and only catches it on a
        // later retry. Creating once at startup eliminates that gap entirely.
        private readonly KeywordRecognitionModel keywordModel;
        private readonly KeywordRecognizer keywordRecognizer;

        private readonly dynamic textDisplay;
        private PyDict state;

        public SpeechService()
        {
            // Recognition config — EndpointId here targets a CUSTOM RECOGNITION model.
            // It must NOT be applied to the synthesizer, or TTS calls hit the wrong
            // endpoint and return immediately with no audio.
            speechConfig = SpeechConfig.FromSubscription(speechKey, speechRegion);
            speechConfig.SpeechRecognitionLanguage = "en-US";

            var endpointId = Environment.GetEnvironmentVariable("SPEECH_ENDPOINT_ID");
            if (!string.IsNullOrEmpty(endpointId))
            {
                speechConfig.EndpointId = endpointId;
            }

            // Dedicated synthesis config — no EndpointId.
            var synthConfig = SpeechConfig.FromSubscription(speechKey, speechRegion);
            synthConfig.SpeechSynthesisVoiceName = "en-US-AndrewMultilingualNeural";
            synthesizer = new SpeechSynthesizer(synthConfig);

            // Pre-open the synth websocket so the first SpeakTextAsync doesn't pay
            // the TCP+TLS+protocol-upgrade handshake (which clips the start of audio).
            // The Connection wrapper is disposed; the underlying connection stays
            // attached to the synthesizer. Per Azure Speech SDK guidance.
            using (var connection = Connection.FromSpeechSynthesizer(synthesizer))
            {
                connection.Open(forContinuousRecognition: true);
            }

            // Load the keyword model + recognizer once. Reused across every
            // keyword cycle so we don't re-pay WASAPI setup each time.
            try
            {
                keywordModel = KeywordRecognitionModel.FromFile(@"..\..\keyword.table");
                keywordRecognizer = new KeywordRecognizer(audioConfig);
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine("Error: Keyword model file not found. " + ex.Message);
            }

            using (Py.GIL())
            {
                textDisplay = Py.Import("SpeechBubble");
            }
        }

        public async Task KeywordRecognizer()
        {
            if (keywordRecognizer == null || keywordModel == null)
            {
                Console.WriteLine("KeywordRecognizer: not initialised (model load failed). Waiting forever.");
                await Task.Delay(-1);
                return;
            }
            try
            {
                await keywordRecognizer.RecognizeOnceAsync(keywordModel);
                Console.WriteLine("Keyword was recognized!");
            }
            catch (Exception ex)
            {
                Console.WriteLine("An unexpected error occurred in the KeywordRecognizer Method: " + ex.Message);
            }
        }

        public void ConvertSpeechToText(SpeechRecognitionResult speechRecognitionResult)
        {
            switch (speechRecognitionResult.Reason)
            {
                case ResultReason.RecognizedSpeech:
                    Console.WriteLine($"RECOGNIZED: {speechRecognitionResult.Text}");
                    break;
                case ResultReason.NoMatch:
                    // Fire-and-forget the synth so the bubble can show in parallel
                    // and be retracted when audio finishes.
                    _ = SynthesizeTextToSpeech("Sorry I didn't get that. Can you say it again?");
                    SpeechBubble("", "Sorry I didn't get that. Can you say it again?");
                    break;
                case ResultReason.Canceled:
                    var cancellation = CancellationDetails.FromResult(speechRecognitionResult);
                    Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");
                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                        Console.WriteLine($"CANCELED: ErrorDetails={cancellation.ErrorDetails}");
                        Console.WriteLine($"CANCELED: Did you set the speech resource key and region values?");
                    }
                    break;
            }
        }

        // Synthesise SSML directly. Use this when you need <lang>, <phoneme>,
        // <break>, <prosody>, or other pronunciation control beyond plain text.
        public async Task SynthesizeSsmlAsync(string ssml)
        {
            using (var result = await synthesizer.SpeakSsmlAsync(ssml))
            {
                Console.WriteLine($"TTS (SSML): Reason={result.Reason}, AudioDuration={result.AudioDuration}");

                if (result.Reason == ResultReason.Canceled)
                {
                    var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                    Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");
                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                        Console.WriteLine($"CANCELED: ErrorDetails=[{cancellation.ErrorDetails}]");
                    }
                }

                if (state != null)
                {
                    using (Py.GIL())
                    {
                        state.SetItem("running", PythonEngine.Eval("False"));
                    }
                }
            }
        }

        public async Task SynthesizeTextToSpeech(string textToSynthesize)
        {
            using (var result = await synthesizer.SpeakTextAsync(textToSynthesize))
            {
                Console.WriteLine($"TTS: Reason={result.Reason}, AudioDuration={result.AudioDuration}");

                if (result.Reason == ResultReason.Canceled)
                {
                    var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                    Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");
                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                        Console.WriteLine($"CANCELED: ErrorDetails=[{cancellation.ErrorDetails}]");
                    }
                }

                // Signal the speech bubble to retract once audio finishes.
                // (state may be null if no bubble was set up — that's fine.)
                if (state != null)
                {
                    using (Py.GIL())
                    {
                        state.SetItem("running", PythonEngine.Eval("False"));
                    }
                }
            }
        }

        public void SpeechBubble(string userInput, string response)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"[bubble t+{sw.ElapsedMilliseconds}ms] SpeechBubble: entered");
            using (Py.GIL())
            {
                Console.WriteLine($"[bubble t+{sw.ElapsedMilliseconds}ms] SpeechBubble: GIL acquired");
                state = new PyDict();
                state.SetItem("running", PythonEngine.Eval("True"));
                Console.WriteLine($"[bubble t+{sw.ElapsedMilliseconds}ms] SpeechBubble: state set, calling show_bubble");
                try
                {
                    textDisplay.show_bubble(userInput, response, state);
                }
                catch (PythonException ex)
                {
                    Console.WriteLine("PythonException caught:");
                    Console.WriteLine("Type: " + ex.Type);
                    Console.WriteLine("Message: " + ex.Message);
                    Console.WriteLine("StackTrace: " + ex.StackTrace);
                }
                Console.WriteLine($"[bubble t+{sw.ElapsedMilliseconds}ms] SpeechBubble: show_bubble returned");
            }
        }

        // Plays ~250ms of silence to wake the audio output device. Bluetooth
        // headphones / wireless speakers / sleep-enabled DACs suppress the first
        // ~200ms after a period of silence, which clips the start of the greeting.
        // Call this once at startup so the first real synth plays in full.
        public async Task WarmUpAudioAsync()
        {
            try
            {
                const string ssml =
                    "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
                    "<voice name='en-US-AndrewMultilingualNeural'> " +
                    "<break time='750ms'/>" +
                    "</voice></speak>";
                using (await synthesizer.SpeakSsmlAsync(ssml)) { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audio warm-up failed (non-fatal): {ex.Message}");
            }
        }

        // Convenience wrapper: starts TTS, shows the bubble in parallel, and the
        // bubble retracts automatically when the audio finishes. Always prefer this
        // over calling Synthesize+SpeechBubble separately — the synth must run
        // concurrently with the bubble so it can signal completion.
        //
        // SpeakTextAsync has a synchronous prelude (audio device + format setup)
        // that runs on the calling thread before it yields. Hand it to the
        // threadpool so the main thread can immediately show the bubble.
        public async Task Say(string userInput, string response)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"[t+{sw.ElapsedMilliseconds}ms] Say: entered");
            var synthTask = Task.Run(() => SynthesizeTextToSpeech(response));
            Console.WriteLine($"[t+{sw.ElapsedMilliseconds}ms] Say: synth task scheduled, calling SpeechBubble");
            SpeechBubble(userInput, response);
            Console.WriteLine($"[t+{sw.ElapsedMilliseconds}ms] Say: SpeechBubble returned");
            try { await synthTask; }
            catch (Exception ex) { Console.WriteLine($"TTS error: {ex.Message}"); }
        }
    }
}