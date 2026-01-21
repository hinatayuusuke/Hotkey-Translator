using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

const string defaultKeyFile = "Key.txt";
var keyPath = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), defaultKeyFile);
if (!File.Exists(keyPath))
{
    Console.WriteLine($"Key file not found: {keyPath}");
    Console.WriteLine("Place Key.txt in the repo root or pass a path as the first argument.");
    return;
}

var apiKey = File.ReadAllText(keyPath).Trim();
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("Key file is empty.");
    return;
}

var settings = new AppSettings
{
    EnableGemini = true,
    ApiKey = apiKey,
    SourceLanguage = "en",
    TargetLanguage = "ja"
};

var texts = new[]
{
    "This will most likely be the biggest fight you have faced in the game so far, it's one of the biggest fights in the game. It's not as bad as the Troll & Ogre fight, but without area effect spells, this combat can take a while. It's generally recommended to enter this room from the north, through the armory where you get the hammer. Otherwise you have a bit of a nasty gap below you that can have foes sneak into. Sleep spells are very useful here, as are hold spells and stinking clouds. If you can keep the foes next to you helpless, you can pick off the archers to help limit damage. Note that the archers have a very limited number of arrows, and should run out in about 4 turns. Encounter: Orc x 31, Hobgoblin x 15, Orc Leader x 4. Treasure: All non-magical items."
};

try
{
    using var httpClient = new HttpClient();
    var client = new GeminiClient(httpClient);
    var results = await client.TranslateAsync(texts, settings, CancellationToken.None);

    if (results.Count == 0)
    {
        Console.WriteLine("No translations returned.");
        return;
    }

    foreach (var text in texts)
    {
        if (results.TryGetValue(text, out var translated))
        {
            Console.WriteLine($"{text} => {translated}");
        }
        else
        {
            Console.WriteLine($"{text} => (no result)");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Gemini smoke test failed: {ex.Message}");
}
