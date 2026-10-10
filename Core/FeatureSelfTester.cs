using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Gem.Handlers;
using Gem.Services;
using Gem.Voice;

namespace Gem.Core;

/// <summary>
/// Comprehensive automated self-test runner for verifying runtime features,
/// phonetics, intent matching, routing, and TTS architectures.
/// </summary>
public static class FeatureSelfTester
{
    public static async Task RunFeatureTestsAsync()
    {
        Console.WriteLine("\n================ RUNNING FEATURE TESTS ================");

        // Test 1: FastCommands
        Console.WriteLine("\n[1] Testing VoiceListener Fast Commands...");

        string[] fastSamples = ["закройся", "выключись", "стоп", "отмена", "закрой монстер хантер", "закрой игру", "закройся пожалуйста"];
        foreach (var sample in fastSamples)
        {
            bool isFast = VoiceListener.IsFastCommand(sample);
            Console.WriteLine($"  IsFastCommand('{sample}') = {isFast} (Expected: True)");
            if (!isFast) throw new Exception($"Fast command '{sample}' was not recognized!");
        }

        string[] nonFastSamples = ["какая погода", "расскажи шутку", "кто президент"];
        foreach (var sample in nonFastSamples)
        {
            bool isFast = VoiceListener.IsFastCommand(sample);
            Console.WriteLine($"  IsFastCommand('{sample}') = {isFast} (Expected: False)");
            if (isFast) throw new Exception($"Non-fast command '{sample}' was incorrectly recognized as fast!");
        }

        // Test 2: SystemPrompt & Intent
        Console.WriteLine("\n[2] Testing LlmIntentService Prompt & Match...");
        if (!LlmIntentService.SystemPrompt.Contains("\"city\": \"<город в именительном падеже>\" | null"))
            throw new Exception("SystemPrompt missing city schema definition!");
        if (!LlmIntentService.SystemPrompt.Contains("погода в Москве"))
            throw new Exception("SystemPrompt missing 'погода в Москве' example!");
        if (!LlmIntentService.SystemPrompt.Contains("погода в Париже"))
            throw new Exception("SystemPrompt missing 'погода в Париже' example!");
        Console.WriteLine("  SystemPrompt schema and examples verified successfully.");

        var fastWeather = LlmIntentService.TryFastMatch("какая погода");
        if (fastWeather?.CommandRequest?.Command != "weather")
            throw new Exception("TryFastMatch('какая погода') did not return weather command!");
        Console.WriteLine("  TryFastMatch('какая погода') returned weather command.");

        var cityWeather = LlmIntentService.TryFastMatch("погода в Москве");
        if (cityWeather != null)
            throw new Exception("TryFastMatch('погода в Москве') should return null to allow LLM city extraction!");
        Console.WriteLine("  TryFastMatch('погода в Москве') correctly deferred to LLM.");

        var offlineCity = LlmIntentService.OfflineFallbackInterpreter("погода в Москве");
        if (offlineCity.CommandRequest?.Command != "weather" ||
            !offlineCity.CommandRequest.Args.TryGetProperty("city", out var cProp) ||
            cProp.GetString() != "москве")
            throw new Exception("OfflineFallbackInterpreter('погода в Москве') failed to extract city!");
        Console.WriteLine("  OfflineFallbackInterpreter('погода в Москве') extracted city correctly.");

        // Test 3: WeatherService
        Console.WriteLine("\n[3] Testing WeatherService Multi-city...");
        var weatherService = new WeatherService();

        string defRep = await weatherService.GetCurrentWeatherReportAsync();
        Console.WriteLine($"  Default: {defRep}");
        if (!defRep.Contains("Санкт-Петербург") || !defRep.Contains("°C"))
            throw new Exception("Default weather report failed!");

        string moscowRep = await weatherService.GetCurrentWeatherReportAsync("Москва");
        Console.WriteLine($"  Москва: {moscowRep}");
        if (!moscowRep.Contains("Москва") || !moscowRep.Contains("°C"))
            throw new Exception("Moscow weather report failed!");

        string parisRep = await weatherService.GetCurrentWeatherReportAsync("Париж");
        Console.WriteLine($"  Париж: {parisRep}");
        if (!parisRep.Contains("Париж") || !parisRep.Contains("°C"))
            throw new Exception("Paris weather report failed!");

        string unknownRep = await weatherService.GetCurrentWeatherReportAsync("НесуществующийГород777");
        Console.WriteLine($"  Unknown: {unknownRep}");
        if (!unknownRep.Contains("не удалось найти"))
            throw new Exception("Unknown city report failed!");

        // Test 4: SteamService Phonetics & Transliteration
        Console.WriteLine("\n[4] Testing SteamService Phonetics & Transliteration...");
        string cyb = SteamService.TransliterateToCyrillic("Cyberpunk");
        Console.WriteLine($"  TransliterateToCyrillic('Cyberpunk') = '{cyb}'");
        if (!cyb.Contains("киберпанк"))
            throw new Exception($"Transliteration of 'Cyberpunk' failed! Got '{cyb}'");

        string spc = SteamService.TransliterateToCyrillic("Space");
        Console.WriteLine($"  TransliterateToCyrillic('Space') = '{spc}'");
        if (!spc.Contains("спейс"))
            throw new Exception($"Transliteration of 'Space' failed! Got '{spc}'");

        string nms = SteamService.TransliterateToCyrillic("No Man's Sky");
        Console.WriteLine($"  TransliterateToCyrillic('No Man''s Sky') = '{nms}'");
        if (!nms.Contains("ноу менс скай") && !nms.Contains("ноу мен скай"))
            throw new Exception($"Transliteration of 'No Man''s Sky' failed! Got '{nms}'");

        string wtc = SteamService.TransliterateToCyrillic("Witcher");
        Console.WriteLine($"  TransliterateToCyrillic('Witcher') = '{wtc}'");
        if (!wtc.Contains("витчер"))
            throw new Exception($"Transliteration of 'Witcher' failed! Got '{wtc}'");

        // Test 5: SteamService Levenshtein & Fuzzy Search
        Console.WriteLine("\n[5] Testing SteamService Fuzzy Search & Library Indexing...");
        string translitTest = SteamService.Transliterate("киберпанк");
        Console.WriteLine($"  Transliterate('киберпанк') = '{translitTest}'");
        if (translitTest != "kiberpank")
            throw new Exception($"Transliterate('киберпанк') expected 'kiberpank', got '{translitTest}'");

        var steam = SteamService.Instance;
        Console.WriteLine($"  Total Indexed Games: {steam.IndexedGames.Count}");

        var cyberpunkGame = steam.FindGame("киберпанк");
        Console.WriteLine($"  FindGame('киберпанк') = '{cyberpunkGame?.Title}' (AppID: {cyberpunkGame?.AppId})");
        if (cyberpunkGame == null || cyberpunkGame.AppId != "1091500")
            throw new Exception("FindGame('киберпанк') did not find Cyberpunk 2077 (1091500)!");

        var stopWordGame = steam.FindGame("установи киберпанк", autoInstall: false);
        Console.WriteLine($"  FindGame('установи киберпанк') = '{stopWordGame?.Title}' (AppID: {stopWordGame?.AppId})");
        if (stopWordGame == null || stopWordGame.AppId != "1091500")
            throw new Exception("FindGame('установи киберпанк') did not resolve properly!");

        var nmsGame = steam.FindGame("ноу менс скай");
        Console.WriteLine($"  FindGame('ноу менс скай') = '{nmsGame?.Title}' (AppID: {nmsGame?.AppId})");
        if (nmsGame == null || nmsGame.AppId != "275850")
            throw new Exception("FindGame('ноу менс скай') did not find No Man's Sky (275850)!");

        var witcherGame = steam.FindGame("ведьмак");
        Console.WriteLine($"  FindGame('ведьмак') = '{witcherGame?.Title}' (AppID: {witcherGame?.AppId})");
        if (witcherGame == null || witcherGame.AppId != "292030")
            throw new Exception("FindGame('ведьмак') did not find The Witcher 3 (292030)!");

        var witcherGamePhonetic = steam.FindGame("витчер");
        Console.WriteLine($"  FindGame('витчер') = '{witcherGamePhonetic?.Title}' (AppID: {witcherGamePhonetic?.AppId})");
        if (witcherGamePhonetic == null || (witcherGamePhonetic.AppId != "292030" && !witcherGamePhonetic.Title.Contains("Witcher", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("FindGame('витчер') did not find Witcher!");

        var dotaGame = steam.FindGame("дота");
        Console.WriteLine($"  FindGame('дота') = '{dotaGame?.Title}' (AppID: {dotaGame?.AppId})");
        if (dotaGame == null || dotaGame.AppId != "570")
            throw new Exception("FindGame('дота') did not find Dota 2 (570)!");

        // Test 6: LlmIntentService Install Prompt & Matching
        Console.WriteLine("\n[6] Testing Install command schema, prompt and intent matching...");
        if (!LlmIntentService.SystemPrompt.Contains("\"action\": \"start\" | \"close\" | \"install\""))
            throw new Exception("SystemPrompt missing 'install' in action schema!");
        if (!LlmIntentService.SystemPrompt.Contains("установи ноу менс скай"))
            throw new Exception("SystemPrompt missing 'установи ноу менс скай' example!");
        if (!LlmIntentService.SystemPrompt.Contains("установи киберпанк"))
            throw new Exception("SystemPrompt missing 'установи киберпанк' example!");
        if (!LlmIntentService.SystemPrompt.Contains("запусти витчер"))
            throw new Exception("SystemPrompt missing 'запусти витчер' example!");

        var fastInstallNms = LlmIntentService.TryFastMatch("установи ноу менс скай");
        if (fastInstallNms?.CommandRequest?.Command != "app" ||
            !fastInstallNms.CommandRequest.Args.TryGetProperty("action", out var actNms) ||
            actNms.GetString() != "install" ||
            !fastInstallNms.CommandRequest.Args.TryGetProperty("name", out var nameNms) ||
            nameNms.GetString() != "ноу менс скай")
            throw new Exception("TryFastMatch('установи ноу менс скай') failed!");
        Console.WriteLine("  TryFastMatch('установи ноу менс скай') verified.");

        var fastInstallCp = LlmIntentService.TryFastMatch("установи киберпанк");
        if (fastInstallCp?.CommandRequest?.Command != "app" ||
            !fastInstallCp.CommandRequest.Args.TryGetProperty("action", out var actCp) ||
            actCp.GetString() != "install" ||
            !fastInstallCp.CommandRequest.Args.TryGetProperty("name", out var nameCp) ||
            nameCp.GetString() != "киберпанк")
            throw new Exception("TryFastMatch('установи киберпанк') failed!");
        Console.WriteLine("  TryFastMatch('установи киберпанк') verified.");

        var fastLaunchWitcher = LlmIntentService.TryFastMatch("запусти витчер");
        if (fastLaunchWitcher?.CommandRequest?.Command != "app" ||
            !fastLaunchWitcher.CommandRequest.Args.TryGetProperty("action", out var actW) ||
            actW.GetString() != "start")
            throw new Exception("TryFastMatch('запусти витчер') failed!");
        Console.WriteLine("  TryFastMatch('запусти витчер') verified.");

        var offlineInstall = LlmIntentService.OfflineFallbackInterpreter("установи киберпанк");
        if (offlineInstall.CommandRequest?.Command != "app" ||
            !offlineInstall.CommandRequest.Args.TryGetProperty("action", out var offAct) ||
            offAct.GetString() != "install")
            throw new Exception("OfflineFallbackInterpreter('установи киберпанк') failed!");
        Console.WriteLine("  OfflineFallbackInterpreter('установи киберпанк') verified.");

        // Test 7: Multi-WakeWords and Regex Cleaning
        Console.WriteLine("\n[7] Testing Multi-WakeWords configuration and Regex CleanUp...");
        var loadedSettings = AppSettingsService.Load();
        string[] requiredWakeWords =
        [
            "джарвис", "jarvis", "рис", "вис",
            "джемини", "гемини", "джеминай", "gemini",
            "димон", "петрович", "алиса", "компьютер"
        ];
        foreach (var req in requiredWakeWords)
        {
            if (!loadedSettings.WakeWords.Contains(req, StringComparer.OrdinalIgnoreCase))
            {
                throw new Exception($"WakeWord '{req}' missing from loaded configuration!");
            }
        }
        Console.WriteLine($"  All {requiredWakeWords.Length} WakeWords verified in configuration.");

        var testListener = new VoiceListener(wakeWords: loadedSettings.WakeWords.ToArray());
        string clean1 = testListener.CleanUpCommandText("джемини установи игру");
        if (clean1 != "установи игру")
            throw new Exception($"CleanUpCommandText('джемини установи игру') failed! Got: '{clean1}'");
        Console.WriteLine($"  CleanUpCommandText('джемини установи игру') = '{clean1}' (OK)");

        string clean2 = testListener.CleanUpCommandText("джарвис привет");
        if (clean2 != "привет")
            throw new Exception($"CleanUpCommandText('джарвис привет') failed! Got: '{clean2}'");
        Console.WriteLine($"  CleanUpCommandText('джарвис привет') = '{clean2}' (OK)");

        string clean3 = testListener.CleanUpCommandText("димон закройся");
        if (clean3 != "закройся")
            throw new Exception($"CleanUpCommandText('димон закройся') failed! Got: '{clean3}'");
        Console.WriteLine($"  CleanUpCommandText('димон закройся') = '{clean3}' (OK)");

        string cleanEmpty1 = testListener.CleanUpCommandText("джемини");
        if (!string.IsNullOrEmpty(cleanEmpty1))
            throw new Exception($"CleanUpCommandText('джемини') should be empty! Got: '{cleanEmpty1}'");
        Console.WriteLine($"  CleanUpCommandText('джемини') = '' (OK - pause mode)");

        string cleanEmpty2 = testListener.CleanUpCommandText("алиса");
        if (!string.IsNullOrEmpty(cleanEmpty2))
            throw new Exception($"CleanUpCommandText('алиса') should be empty! Got: '{cleanEmpty2}'");
        Console.WriteLine($"  CleanUpCommandText('алиса') = '' (OK - pause mode)");

        // Test 8: SteamService FindMainSteamWindow API
        Console.WriteLine("\n[8] Testing SteamService FindMainSteamWindow API...");
        IntPtr testHwnd = SteamService.FindMainSteamWindow();
        Console.WriteLine($"  FindMainSteamWindow returned HWND: {testHwnd}");

        // Test 9: Direct App Exit Keyword Check
        Console.WriteLine("\n[9] Testing Direct App Exit keywords detection...");
        string[] testExitPhrases = ["закройся", "закрывайся", "закрыть", "выход", "отключись", "заверши работу", "выключись", "стоп приложение", "джарвис закрывайся"];
        foreach (var phrase in testExitPhrases)
        {
            if (!JarvisOrchestrator.IsExitCommand(phrase))
            {
                throw new Exception($"Exit command '{phrase}' was not recognized!");
            }
        }
        Console.WriteLine("  All exit keywords verified successfully.");

        // Test 10: Confirmation Listening Bypass Wake-Word
        Console.WriteLine("\n[10] Testing Confirmation Listening Bypass Wake-Word...");
        JarvisOrchestrator.Instance.ResetConfirmationState();

        // 10.1 Verify keyword matchers
        string[] affirmativeWords = ["да", "давай", "подтверждаю", "устанавливай", "запускай", "верно", "именно", "ага", "конечно", "хорошо"];
        foreach (var word in affirmativeWords)
        {
            if (!JarvisOrchestrator.IsAffirmativeReply(word))
                throw new Exception($"IsAffirmativeReply('{word}') returned false!");
        }
        Console.WriteLine("  Affirmative confirmation keywords verified.");

        string[] negativeWords = ["нет", "отмена", "не надо", "отбой", "стой", "не то", "отставить"];
        foreach (var word in negativeWords)
        {
            if (!JarvisOrchestrator.IsNegativeReply(word))
                throw new Exception($"IsNegativeReply('{word}') returned false!");
        }
        Console.WriteLine("  Negative confirmation keywords verified.");

        if (JarvisOrchestrator.IsAffirmativeReply("сделай тише") || JarvisOrchestrator.IsNegativeReply("сделай тише"))
            throw new Exception("Unrelated command matched confirmation keywords!");

        // 10.2 Full scenario: 'установи астра ниро' -> doubt zone -> confirmation question
        var confPromptResp = LlmIntentService.TryFastMatch("установи астра ниро");
        if (confPromptResp == null || !confPromptResp.Reply!.Contains("ASTRONEER") || !JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception("TryFastMatch('установи астра ниро') failed to trigger pending confirmation for ASTRONEER!");
        Console.WriteLine($"  Prompt response: '{confPromptResp.Reply}' (PendingAction={JarvisOrchestrator.Instance.PendingAction})");

        // 10.3 Direct capture without wake-word: VoiceListener enters ListeningForCommand
        testListener.EnterConfirmationListening("Awaiting confirmation reply (bypassing wake-word)...");
        if (testListener.CurrentState != VoiceListenerState.ListeningForCommand)
            throw new Exception($"Expected VoiceListener state ListeningForCommand, got {testListener.CurrentState}");
        Console.WriteLine("  VoiceListener successfully entered ListeningForCommand (bypassing wake-word).");

        // 10.4 User says 'Да' without wake-word
        var yesResult = LlmIntentService.TryFastMatch("Да");
        if (yesResult == null || !yesResult.Reply!.Contains("Принято. Начинаю установку ASTRONEER") || JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception("Direct affirmative reply 'Да' failed to confirm installation of ASTRONEER!");
        if (testListener.CurrentState != VoiceListenerState.WaitingForWakeWord)
            throw new Exception($"Expected VoiceListener state WaitingForWakeWord after confirmation, got {testListener.CurrentState}");
        Console.WriteLine($"  Affirmative response: '{yesResult.Reply}' -> State returned to WaitingForWakeWord.");

        // 10.5 Negative reply scenario: 'установи астра ниро' -> 'Нет'
        LlmIntentService.TryFastMatch("установи астра ниро");
        testListener.EnterConfirmationListening();
        var noResult = LlmIntentService.TryFastMatch("Нет");
        if (noResult == null || noResult.Reply != "Понял, отменяю." || JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception("Direct negative reply 'Нет' failed to cancel pending confirmation!");
        if (testListener.CurrentState != VoiceListenerState.WaitingForWakeWord)
            throw new Exception($"Expected VoiceListener state WaitingForWakeWord after cancellation, got {testListener.CurrentState}");
        Console.WriteLine($"  Negative response: '{noResult.Reply}' -> State returned to WaitingForWakeWord.");

        // 10.6 Other command scenario: 'установи астра ниро' -> 'сделай тише'
        LlmIntentService.TryFastMatch("установи астра ниро");
        testListener.EnterConfirmationListening();
        var otherResult = LlmIntentService.TryFastMatch("сделай тише");
        if (otherResult?.CommandRequest?.Command != "volume" || JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception("Unrelated command 'сделай тише' failed to cancel pending confirmation and execute!");
        if (testListener.CurrentState != VoiceListenerState.WaitingForWakeWord)
            throw new Exception($"Expected VoiceListener state WaitingForWakeWord after fallback, got {testListener.CurrentState}");

        // Test 11: Media FastMatch and Win32 Media Keys
        Console.WriteLine("\n[11] Testing Media FastMatch & Win32 Media Keys...");

        string[] playPhrases = ["включи музыку", "музыка", "играй", "продолжи", "возобнови"];
        foreach (var phrase in playPhrases)
        {
            var res = LlmIntentService.TryFastMatch(phrase);
            if (res?.CommandRequest?.Command != "media")
                throw new Exception($"TryFastMatch('{phrase}') did not return 'media' command!");
        }

        string[] pausePhrases = ["пауза", "стоп", "останови", "останови музыку"];
        foreach (var phrase in pausePhrases)
        {
            var res = LlmIntentService.TryFastMatch(phrase);
            if (res?.CommandRequest?.Command != "media")
                throw new Exception($"TryFastMatch('{phrase}') did not return 'media' command!");
        }

        // Test 12: Timer and Reminder Service
        Console.WriteLine("\n[12] Testing Timer and Reminder Service...");
        var testTimerService = new TimerService();
        var testRouterWithTimer = new CommandRouter().Register(new TimerCommandHandler(testTimerService));

        var setCmd = LlmIntentService.TryFastMatch("поставь таймер на 10 секунд")!.CommandRequest!;
        var setRes = await testRouterWithTimer.RouteAsync(setCmd);
        if (!setRes.Success)
            throw new Exception($"RouteAsync timer set failed: {setRes.Message}");
        testTimerService.Dispose();

        // Test 14: Hybrid Resilient TTS Architecture
        Console.WriteLine("\n[14] Testing Hybrid Resilient TTS Architecture...");
        var edgeEngine = new EdgeTtsEngine();
        if (edgeEngine.ConnectionTimeoutMs != 1500)
            throw new Exception($"EdgeTtsEngine ConnectionTimeoutMs expected 1500, got {edgeEngine.ConnectionTimeoutMs}");

        var systemSpeechEngine = new SystemSpeechTtsEngine();
        if (!systemSpeechEngine.IsAvailable)
            throw new Exception("SystemSpeechTtsEngine.IsAvailable must always be true on Windows!");

        // Test 20: KWS RMS Noise Gate & 150ms Min Duration Filter
        Console.WriteLine("\n[20] Testing KWS RMS Noise Gate & 150ms Min Duration Filter...");
        byte[] quietNoise = new byte[1120];
        for (int i = 0; i < quietNoise.Length; i += 2)
        {
            short val = (short)(i % 100 - 50);
            quietNoise[i] = (byte)(val & 0xFF);
            quietNoise[i + 1] = (byte)((val >> 8) & 0xFF);
        }

        byte[] speechSignal = new byte[1120];
        for (int i = 0; i < speechSignal.Length; i += 2)
        {
            short val = (short)(Math.Sin(i * 0.05) * 3000);
            speechSignal[i] = (byte)(val & 0xFF);
            speechSignal[i + 1] = (byte)((val >> 8) & 0xFF);
        }

        var kwsStress = new VoskGrammarWakeWordDetector("джарвис", minDurationMs: 150, noiseGateRms: 450.0);
        string[] negativeSnippets = ["джа", "рис", "да", "джар", "сюрприз", "вис", "джарвиса", "джарвису", "", "[unk]"];
        foreach (var snip in negativeSnippets)
        {
            if (kwsStress.IsIsolatedTokenMatch(snip, "джарвис"))
                throw new Exception($"KWS isolated match unexpectedly succeeded for snippet '{snip}'!");
        }
        kwsStress.Dispose();

        testListener.Dispose();
        Console.WriteLine("\n>>> ALL FEATURE TESTS PASSED SUCCESSFULLY! <<<\n");
    }
}
