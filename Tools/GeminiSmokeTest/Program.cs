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
    "Elf Skeleton. You're meant to use this as a clue to figure out the three passwords. LUX used for the Spectres, SHESTNI lets you skip combat with patrols before you encounter Ferran, and SAMOSUD is used on patrols after you mean him. You do not need to check the skeleton to learn the passwords, you just have to type them when prompted.",
    "Beginning of the patrol area. Skeletons and Zombies are encountered in this open courtyard. Use one of the two passwords above to avoid combat",
    "Insects attack you here. Looking at the files, it looks like they have an advantage for getting surprise on you. Because they have poison that can kill in one shot if you're unlucky, it's not recommended to fight them. There are no items of value in this area. Encounter: HUGE SCORPION x 2"
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
