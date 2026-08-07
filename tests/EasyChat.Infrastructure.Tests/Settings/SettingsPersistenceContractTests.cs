using System.Globalization;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Settings.Persistence;
using EasyChat.Infrastructure.Settings.Persistence;
using Newtonsoft.Json;

namespace EasyChat.Infrastructure.Tests.Settings;

[TestClass]
public sealed class SettingsPersistenceContractTests
{
    private static readonly string[] ExpectedFileNames =
    [
        "AiModel.json",
        "General.json",
        "Input.json",
        "MachineTrans.json",
        "Ocr.json",
        "Prompts.json",
        "Proxy.json",
        "Result.json",
        "Screenshot.json",
        "SelectionTranslation.json",
        "Shortcut.json",
        "SpeechRecognition.json",
        "TextAssist.json",
        "Tts.json"
    ];

    [TestMethod]
    public async Task ReadAllAsync_CreatesTheFourteenCompatibleDefaultFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gateway = new JsonSettingsPersistenceGateway(directory);

            var result = await gateway.ReadAllAsync();

            Assert.IsTrue(result.IsSuccess, result.Error.Message);
            CollectionAssert.AreEqual(
                ExpectedFileNames,
                Directory.GetFiles(directory)
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
            Assert.AreEqual("auto", result.Value.General.SourceLanguage.Id);
            Assert.AreEqual("zh-Hans", result.Value.General.TargetLanguage.Id);
            Assert.AreEqual("AiModel", result.Value.General.TranslationEngine);
            Assert.AreEqual("OpenAI", result.Value.General.AiModel);
            Assert.AreEqual(ThemeMode.System, result.Value.General.BaseTheme);
            Assert.AreEqual(5000, result.Value.Result.AutoCloseDelay);
            Assert.AreEqual(InputDeliveryMode.Paste, result.Value.Input.DeliveryMode);
            Assert.AreEqual(OcrRecognitionMode.Normal, result.Value.Screenshot.OcrMode);
            Assert.AreEqual(
                ScreenshotSettings.DefaultOcrIdleTimeoutSeconds,
                result.Value.Screenshot.OcrIdleTimeoutSeconds);
            Assert.AreEqual("EdgeTTS", result.Value.Tts.Provider);
            Assert.HasCount(8, result.Value.Prompts.Entries);
            Assert.AreEqual("builtin-professional-translator", result.Value.Prompts.SelectedPromptId);
            Assert.AreEqual(
                "builtin-professional-translator",
                result.Value.Prompts.Entries.Single(prompt => prompt.IsDefault).Id);
            Assert.IsTrue(result.Value.Prompts.Entries.Any(prompt =>
                prompt.Id == "builtin-professional-translator-zh"));
            StringAssert.Contains(
                await File.ReadAllTextAsync(Path.Combine(directory, "General.json")),
                "\"BaseTheme\": \"Default\"");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ScreenshotOcrMode_RoundTripsAndOldFilesDefaultToNormal()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gateway = new JsonSettingsPersistenceGateway(directory);
            var initial = await gateway.ReadAllAsync();
            Assert.IsTrue(initial.IsSuccess, initial.Error.Message);
            var changed = initial.Value with
            {
                Screenshot = initial.Value.Screenshot with
                {
                    OcrMode = OcrRecognitionMode.IdleRelease,
                    OcrIdleTimeoutSeconds = 45
                }
            };

            var write = await gateway.WriteAsync(SettingsSection.Screenshot, changed);
            var reread = await gateway.ReadAllAsync();

            Assert.IsTrue(write.IsSuccess, write.Error.Message);
            Assert.IsTrue(reread.IsSuccess, reread.Error.Message);
            Assert.AreEqual(OcrRecognitionMode.IdleRelease, reread.Value.Screenshot.OcrMode);
            Assert.AreEqual(45, reread.Value.Screenshot.OcrIdleTimeoutSeconds);

            await File.WriteAllTextAsync(
                Path.Combine(directory, "Screenshot.json"),
                """
                {
                  "Mode": "Precise",
                  "FixedAreas": []
                }
                """);
            var previous = await gateway.ReadAllAsync();

            Assert.IsTrue(previous.IsSuccess, previous.Error.Message);
            Assert.AreEqual(OcrRecognitionMode.Normal, previous.Value.Screenshot.OcrMode);
            Assert.AreEqual(
                ScreenshotSettings.DefaultOcrIdleTimeoutSeconds,
                previous.Value.Screenshot.OcrIdleTimeoutSeconds);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ReadAllAsync_AcceptsThePreviousLanguageFieldAlias()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gateway = new JsonSettingsPersistenceGateway(directory);
            Assert.IsTrue((await gateway.ReadAllAsync()).IsSuccess);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "General.json"),
                """
                {
                  "Language": "English",
                  "TransEngine": null,
                  "UsingAiModel": null,
                  "BaseTheme": null
                }
                """);

            var result = await gateway.ReadAllAsync();

            Assert.IsTrue(result.IsSuccess, result.Error.Message);
            Assert.AreEqual("English", result.Value.General.DisplayLanguage);
            Assert.AreEqual("AiModel", result.Value.General.TranslationEngine);
            Assert.AreEqual("OpenAI", result.Value.General.AiModel);
            Assert.AreEqual(ThemeMode.System, result.Value.General.BaseTheme);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ReadAllAsync_PreservesPreviousExplicitThemeChoices()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gateway = new JsonSettingsPersistenceGateway(directory);
            Assert.IsTrue((await gateway.ReadAllAsync()).IsSuccess);

            await File.WriteAllTextAsync(
                Path.Combine(directory, "General.json"),
                """
                {
                  "BaseTheme": "Light"
                }
                """);
            var light = await gateway.ReadAllAsync();
            Assert.IsTrue(light.IsSuccess, light.Error.Message);
            Assert.AreEqual(ThemeMode.Light, light.Value.General.BaseTheme);

            await File.WriteAllTextAsync(
                Path.Combine(directory, "General.json"),
                """
                {
                  "BaseTheme": "Dark"
                }
                """);
            var dark = await gateway.ReadAllAsync();
            Assert.IsTrue(dark.IsSuccess, dark.Error.Message);
            Assert.AreEqual(ThemeMode.Dark, dark.Value.General.BaseTheme);

            var useSystem = dark.Value with
            {
                General = dark.Value.General with { BaseTheme = ThemeMode.System }
            };
            var write = await gateway.WriteAsync(SettingsSection.General, useSystem);
            Assert.IsTrue(write.IsSuccess, write.Error.Message);
            StringAssert.Contains(
                await File.ReadAllTextAsync(Path.Combine(directory, "General.json")),
                "\"BaseTheme\": \"Default\"");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public void GeneralDefaults_FollowSystemUiLanguageWithoutPersistingAnImplicitChoice()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            var english = new GeneralSettingsDto();
            Assert.AreEqual("English", english.DisplayLanguage);
            Assert.IsFalse(JsonConvert.SerializeObject(english).Contains(
                "DisplayLanguage",
                StringComparison.Ordinal));

            CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
            var chinese = new GeneralSettingsDto();
            Assert.AreEqual("Simplified Chinese", chinese.DisplayLanguage);
            Assert.IsFalse(JsonConvert.SerializeObject(chinese).Contains(
                "DisplayLanguage",
                StringComparison.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [TestMethod]
    public async Task ShortcutRemarks_RoundTripAndRemainCompatibleWithPreviousFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gateway = new JsonSettingsPersistenceGateway(directory);
            var initial = await gateway.ReadAllAsync();
            Assert.IsTrue(initial.IsSuccess, initial.Error.Message);
            var changed = initial.Value with
            {
                Shortcut = new ShortcutSettings(
                [
                    new ShortcutEntrySettings(
                        "InputTranslate",
                        null,
                        "Ctrl + Enter",
                        true,
                        "Translate and send")
                ])
            };

            var write = await gateway.WriteAsync(SettingsSection.Shortcut, changed);
            var reread = await gateway.ReadAllAsync();

            Assert.IsTrue(write.IsSuccess, write.Error.Message);
            Assert.IsTrue(reread.IsSuccess, reread.Error.Message);
            Assert.AreEqual("Translate and send", reread.Value.Shortcut.Entries.Single().Remark);
            StringAssert.Contains(
                await File.ReadAllTextAsync(Path.Combine(directory, "Shortcut.json")),
                "\"Remark\": \"Translate and send\"");

            await File.WriteAllTextAsync(
                Path.Combine(directory, "Shortcut.json"),
                """
                {
                  "Entries": [
                    {
                      "ActionType": "Screenshot",
                      "Parameter": null,
                      "KeyCombination": "Ctrl + F8",
                      "IsEnabled": true
                    }
                  ]
                }
                """);

            var previous = await gateway.ReadAllAsync();

            Assert.IsTrue(previous.IsSuccess, previous.Error.Message);
            Assert.IsNull(previous.Value.Shortcut.Entries.Single().Remark);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task SpeechRecognitionPromptId_RoundTripsAndOldFilesRemainCompatible()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gateway = new JsonSettingsPersistenceGateway(directory);
            var initial = await gateway.ReadAllAsync();
            Assert.IsTrue(initial.IsSuccess, initial.Error.Message);
            var changed = initial.Value with
            {
                SpeechRecognition = initial.Value.SpeechRecognition with
                {
                    PromptId = "speech-prompt"
                }
            };

            var write = await gateway.WriteAsync(SettingsSection.SpeechRecognition, changed);
            var reread = await gateway.ReadAllAsync();

            Assert.IsTrue(write.IsSuccess, write.Error.Message);
            Assert.IsTrue(reread.IsSuccess, reread.Error.Message);
            Assert.AreEqual("speech-prompt", reread.Value.SpeechRecognition.PromptId);
            StringAssert.Contains(
                await File.ReadAllTextAsync(Path.Combine(directory, "SpeechRecognition.json")),
                "\"PromptId\": \"speech-prompt\"");

            await File.WriteAllTextAsync(
                Path.Combine(directory, "SpeechRecognition.json"),
                "{}");
            var previous = await gateway.ReadAllAsync();

            Assert.IsTrue(previous.IsSuccess, previous.Error.Message);
            Assert.IsNull(previous.Value.SpeechRecognition.PromptId);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task SelectionToolbarExplanation_RoundTripsAndOldFilesDefaultToEnabled()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gateway = new JsonSettingsPersistenceGateway(directory);
            var initial = await gateway.ReadAllAsync();
            Assert.IsTrue(initial.IsSuccess, initial.Error.Message);
            Assert.IsTrue(initial.Value.SelectionTranslation.ExplanationEnabled);

            var changed = initial.Value with
            {
                SelectionTranslation = initial.Value.SelectionTranslation with
                {
                    ExplanationEnabled = false
                }
            };
            var write = await gateway.WriteAsync(SettingsSection.SelectionTranslation, changed);
            var reread = await gateway.ReadAllAsync();

            Assert.IsTrue(write.IsSuccess, write.Error.Message);
            Assert.IsTrue(reread.IsSuccess, reread.Error.Message);
            Assert.IsFalse(reread.Value.SelectionTranslation.ExplanationEnabled);
            StringAssert.Contains(
                await File.ReadAllTextAsync(Path.Combine(directory, "SelectionTranslation.json")),
                "\"ExplanationEnabled\": false");

            await File.WriteAllTextAsync(
                Path.Combine(directory, "SelectionTranslation.json"),
                "{ \"Enabled\": true }");
            var legacy = await gateway.ReadAllAsync();

            Assert.IsTrue(legacy.IsSuccess, legacy.Error.Message);
            Assert.IsTrue(legacy.Value.SelectionTranslation.ExplanationEnabled);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task BuiltInPromptRoles_SeedOnceAndStayDeleted()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gateway = new JsonSettingsPersistenceGateway(directory);
            Assert.IsTrue((await gateway.ReadAllAsync()).IsSuccess);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "Prompts.json"),
                """
                {
                  "SelectedPromptId": "legacy",
                  "Entries": [
                    {
                      "Id": "legacy",
                      "Name": "Legacy",
                      "Content": "A legacy role.",
                      "IsDefault": true
                    }
                  ]
                }
                """);

            var seeded = await gateway.ReadAllAsync();

            Assert.IsTrue(seeded.IsSuccess, seeded.Error.Message);
            Assert.HasCount(9, seeded.Value.Prompts.Entries);
            Assert.IsTrue(seeded.Value.Prompts.Entries.Any(prompt =>
                prompt.Id == "builtin-professional-translator"));
            Assert.IsTrue(seeded.Value.Prompts.Entries.Any(prompt =>
                prompt.Id == "builtin-professional-translator-zh"));
            StringAssert.Contains(
                await File.ReadAllTextAsync(Path.Combine(directory, "Prompts.json")),
                "\"BuiltInPromptsSeeded\": true");

            var deleted = seeded.Value with
            {
                Prompts = new PromptSettings(string.Empty, [])
            };
            var write = await gateway.WriteAsync(SettingsSection.Prompts, deleted);
            var reread = await gateway.ReadAllAsync();

            Assert.IsTrue(write.IsSuccess, write.Error.Message);
            Assert.IsTrue(reread.IsSuccess, reread.Error.Message);
            Assert.IsEmpty(reread.Value.Prompts.Entries);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task BuiltInPromptRoles_UpgradeV1WithoutRestoringDeletedEntries()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gateway = new JsonSettingsPersistenceGateway(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "Prompts.json"),
                """
                {
                  "SelectedPromptId": "builtin-professional-translator",
                  "BuiltInPromptsSeeded": true,
                  "Entries": [
                    {
                      "Id": "builtin-professional-translator",
                      "Name": "Professional translator",
                      "Content": "You are a professional translator. Produce accurate, fluent translations that preserve meaning, tone, terminology, formatting, and intent.",
                      "IsDefault": true
                    },
                    {
                      "Id": "custom",
                      "Name": "Custom",
                      "Content": "My custom role.",
                      "IsDefault": false
                    }
                  ]
                }
                """);

            var upgraded = await gateway.ReadAllAsync();

            Assert.IsTrue(upgraded.IsSuccess, upgraded.Error.Message);
            Assert.HasCount(6, upgraded.Value.Prompts.Entries);
            var professional = upgraded.Value.Prompts.Entries.Single(prompt =>
                prompt.Id == "builtin-professional-translator");
            StringAssert.Contains(professional.Content, "senior professional translator");
            Assert.IsFalse(upgraded.Value.Prompts.Entries.Any(prompt =>
                prompt.Id == "builtin-technical-translator"));
            Assert.IsTrue(upgraded.Value.Prompts.Entries.Any(prompt =>
                prompt.Id == "builtin-technical-translator-zh"));
            StringAssert.Contains(
                await File.ReadAllTextAsync(Path.Combine(directory, "Prompts.json")),
                "\"BuiltInPromptCatalogVersion\": 2");

            var removedChinesePreset = upgraded.Value with
            {
                Prompts = upgraded.Value.Prompts with
                {
                    Entries = upgraded.Value.Prompts.Entries
                        .Where(prompt => prompt.Id != "builtin-professional-translator-zh")
                        .ToArray()
                }
            };
            Assert.IsTrue((await gateway.WriteAsync(SettingsSection.Prompts, removedChinesePreset)).IsSuccess);

            var reread = await gateway.ReadAllAsync();

            Assert.IsTrue(reread.IsSuccess, reread.Error.Message);
            Assert.IsFalse(reread.Value.Prompts.Entries.Any(prompt =>
                prompt.Id == "builtin-professional-translator-zh"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task WriteAsync_ReplacesOnlyTheSelectedFileWithoutLeavingTemporaryFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gateway = new JsonSettingsPersistenceGateway(directory);
            var read = await gateway.ReadAllAsync();
            Assert.IsTrue(read.IsSuccess, read.Error.Message);
            var before = Directory.GetFiles(directory)
                .Where(path => Path.GetFileName(path) != "Proxy.json")
                .ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);
            var changed = read.Value with { Proxy = new ProxySettings("http://127.0.0.1:7890") };

            var write = await gateway.WriteAsync(SettingsSection.Proxy, changed);

            Assert.IsTrue(write.IsSuccess, write.Error.Message);
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(directory, "Proxy.json")),
                "http://127.0.0.1:7890");
            Assert.IsFalse(Directory.EnumerateFiles(directory, "*.tmp").Any());
            foreach (var snapshot in before)
                Assert.AreEqual(snapshot.Value, File.ReadAllText(snapshot.Key));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "EasyChat.RefactorV2.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var expectedRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "EasyChat.RefactorV2.Tests"));
        if (fullPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
