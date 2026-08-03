using System.IO.Compression;
using System.Formats.Tar;
using EasyChat.Contracts.Speech;
using EasyChat.Infrastructure.Speech.Recognition;

namespace EasyChat.Infrastructure.Tests.Speech;

[TestClass]
public sealed class MicroAsrSpeechRecognitionModelInstallerTests
{
    [TestMethod]
    public async Task ImportDirectoryFindsModelsAndMakesSharedVadSelfContained()
    {
        using var workspace = new TestWorkspace();
        var downloads = workspace.CreateDirectory("downloads", "bundle");
        CreateModel(Path.Combine(downloads, "en-US"), "en-US", includeVad: true);
        CreateModel(Path.Combine(downloads, "zh-CN"), "zh-CN", includeVad: false);
        var models = workspace.PathFor("application", "Models");
        var catalog = new MicroAsrSpeechRecognitionModelCatalog(models);
        var installer = new MicroAsrSpeechRecognitionModelInstaller(catalog);
        var changeCount = 0;
        catalog.ModelsChanged += (_, _) => changeCount++;

        var first = await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
            workspace.PathFor("downloads"),
            SpeechRecognitionModelImportSourceKind.Directory));
        var second = await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
            workspace.PathFor("downloads"),
            SpeechRecognitionModelImportSourceKind.Directory));

        CollectionAssert.AreEquivalent(
            new[] { "en-US", "zh-CN" },
            first.ImportedModels.Select(model => model.Id).ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(models, "zh-CN", "svad.quantized.onnx")));
        Assert.IsEmpty(second.ImportedModels);
        Assert.HasCount(2, second.SkippedModels);
        Assert.AreEqual(1, changeCount);
    }

    [TestMethod]
    public async Task ImportDirectoriesAcceptsModelFoldersDirectly()
    {
        using var workspace = new TestWorkspace();
        var firstSource = workspace.CreateDirectory("downloads", "first-package");
        var secondSource = workspace.CreateDirectory("downloads", "second-package");
        CreateModel(firstSource, "de-DE", includeVad: true);
        CreateModel(secondSource, "ja-JP", includeVad: true);
        var models = workspace.PathFor("application", "Models");
        var installer = new MicroAsrSpeechRecognitionModelInstaller(models);

        var result = await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
            [firstSource + Path.DirectorySeparatorChar, secondSource],
            SpeechRecognitionModelImportSourceKind.Directory));

        CollectionAssert.AreEquivalent(
            new[] { "de-DE", "ja-JP" },
            result.ImportedModels.Select(model => model.Id).ToArray());
    }

    [TestMethod]
    [DataRow(".zip")]
    [DataRow(".tar")]
    public async Task ImportArchiveAcceptsModelFilesAtArchiveRoot(string extension)
    {
        using var workspace = new TestWorkspace();
        var source = workspace.CreateDirectory("source-model");
        CreateModel(source, "en-US", includeVad: true);
        var archive = workspace.PathFor("en-US" + extension);
        if (extension == ".zip")
            ZipFile.CreateFromDirectory(source, archive);
        else
            TarFile.CreateFromDirectory(source, archive, includeBaseDirectory: false);
        var models = workspace.PathFor("application", "Models");
        var installer = new MicroAsrSpeechRecognitionModelInstaller(models);

        var result = await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
            archive,
            SpeechRecognitionModelImportSourceKind.Archive));

        Assert.HasCount(1, result.ImportedModels);
        Assert.AreEqual("en-US", result.ImportedModels[0].Id);
        Assert.IsTrue(File.Exists(Path.Combine(models, "en-US", "model_onnx_quant.config")));
    }

    [TestMethod]
    public async Task ImportArchivesAcceptsMultipleArchivesWithArbitraryFileNames()
    {
        using var workspace = new TestWorkspace();
        var firstSource = workspace.CreateDirectory("source-one");
        var secondSource = workspace.CreateDirectory("source-two");
        CreateModel(firstSource, "fr-FR", includeVad: true);
        CreateModel(secondSource, "zh-CN", includeVad: true);
        var firstArchive = workspace.PathFor("download-1.zip");
        var secondArchive = workspace.PathFor("download-2.zip");
        ZipFile.CreateFromDirectory(firstSource, firstArchive);
        ZipFile.CreateFromDirectory(secondSource, secondArchive);
        var models = workspace.PathFor("application", "Models");
        var installer = new MicroAsrSpeechRecognitionModelInstaller(models);

        var result = await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
            [firstArchive, secondArchive],
            SpeechRecognitionModelImportSourceKind.Archive));

        CollectionAssert.AreEquivalent(
            new[] { "fr-FR", "zh-CN" },
            result.ImportedModels.Select(model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task ImportArchivesUsesArchiveNamesWhenConfigurationHasNoLocale()
    {
        using var workspace = new TestWorkspace();
        var firstSource = workspace.CreateDirectory("source-one");
        var secondSource = workspace.CreateDirectory("source-two");
        CreateModel(firstSource, "de-DE", includeVad: true, includeOutputLocale: false);
        CreateModel(secondSource, "ja-JP", includeVad: true, includeOutputLocale: false);
        var firstArchive = workspace.PathFor("de-DE.zip");
        var secondArchive = workspace.PathFor("ja-JP.zip");
        ZipFile.CreateFromDirectory(firstSource, firstArchive);
        ZipFile.CreateFromDirectory(secondSource, secondArchive);
        var installer = new MicroAsrSpeechRecognitionModelInstaller(
            workspace.PathFor("application", "Models"));

        var result = await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
            [firstArchive, secondArchive],
            SpeechRecognitionModelImportSourceKind.Archive));

        CollectionAssert.AreEquivalent(
            new[] { "de-DE", "ja-JP" },
            result.ImportedModels.Select(model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task ImportArchivesSkipsDuplicateLocalesAndContinuesBatch()
    {
        using var workspace = new TestWorkspace();
        var firstEnglish = workspace.CreateDirectory("english-one");
        var secondEnglish = workspace.CreateDirectory("english-two");
        var chinese = workspace.CreateDirectory("chinese");
        CreateModel(firstEnglish, "en-US", includeVad: true);
        CreateModel(secondEnglish, "en-US", includeVad: true);
        CreateModel(chinese, "zh-CN", includeVad: true);
        var firstArchive = workspace.PathFor("english-one.zip");
        var secondArchive = workspace.PathFor("english-two.zip");
        var thirdArchive = workspace.PathFor("chinese.zip");
        ZipFile.CreateFromDirectory(firstEnglish, firstArchive);
        ZipFile.CreateFromDirectory(secondEnglish, secondArchive);
        ZipFile.CreateFromDirectory(chinese, thirdArchive);
        var models = workspace.PathFor("application", "Models");
        var installer = new MicroAsrSpeechRecognitionModelInstaller(models);

        var result = await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
            [firstArchive, secondArchive, thirdArchive],
            SpeechRecognitionModelImportSourceKind.Archive));

        CollectionAssert.AreEquivalent(
            new[] { "en-US", "zh-CN" },
            result.ImportedModels.Select(model => model.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "en-US" },
            result.SkippedModels.Select(model => model.Id).ToArray());
        Assert.IsTrue(Directory.Exists(Path.Combine(models, "en-US")));
        Assert.IsTrue(Directory.Exists(Path.Combine(models, "zh-CN")));
    }

    [TestMethod]
    public async Task ImportArchiveUsesVadFromInstalledModel()
    {
        using var workspace = new TestWorkspace();
        var models = workspace.CreateDirectory("application", "Models");
        CreateModel(Path.Combine(models, "en-US"), "en-US", includeVad: true);
        var source = workspace.CreateDirectory("italian-source");
        CreateModel(
            source,
            "it-IT",
            includeVad: false,
            includeOutputLocale: false,
            includeVadPath: false);
        var archive = workspace.PathFor("it-IT.zip");
        ZipFile.CreateFromDirectory(source, archive);
        var installer = new MicroAsrSpeechRecognitionModelInstaller(models);

        var result = await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
            archive,
            SpeechRecognitionModelImportSourceKind.Archive));

        CollectionAssert.AreEqual(
            new[] { "it-IT" },
            result.ImportedModels.Select(model => model.Id).ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(models, "it-IT", "svad.quantized.onnx")));
    }

    [TestMethod]
    public async Task ImportArchiveUsesSharedVadAtModelLibraryRoot()
    {
        using var workspace = new TestWorkspace();
        var models = workspace.CreateDirectory("application", "Models");
        File.WriteAllBytes(Path.Combine(models, "svad.quantized.onnx"), [1]);
        var source = workspace.CreateDirectory("italian-source");
        CreateModel(
            source,
            "it-IT",
            includeVad: false,
            includeOutputLocale: false,
            includeVadPath: false);
        var archive = workspace.PathFor("it-IT.zip");
        ZipFile.CreateFromDirectory(source, archive);
        var installer = new MicroAsrSpeechRecognitionModelInstaller(models);

        var result = await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
            archive,
            SpeechRecognitionModelImportSourceKind.Archive));

        CollectionAssert.AreEqual(
            new[] { "it-IT" },
            result.ImportedModels.Select(model => model.Id).ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(models, "it-IT", "svad.quantized.onnx")));
    }

    [TestMethod]
    public async Task ImportArchivesSharesVadWithinBatch()
    {
        using var workspace = new TestWorkspace();
        var englishSource = workspace.CreateDirectory("english-source");
        var italianSource = workspace.CreateDirectory("italian-source");
        CreateModel(englishSource, "en-US", includeVad: true);
        CreateModel(
            italianSource,
            "it-IT",
            includeVad: false,
            includeOutputLocale: false,
            includeVadPath: false);
        var englishArchive = workspace.PathFor("en-US.zip");
        var italianArchive = workspace.PathFor("it-IT.zip");
        ZipFile.CreateFromDirectory(englishSource, englishArchive);
        ZipFile.CreateFromDirectory(italianSource, italianArchive);
        var models = workspace.PathFor("application", "Models");
        var installer = new MicroAsrSpeechRecognitionModelInstaller(models);

        var result = await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
            [italianArchive, englishArchive],
            SpeechRecognitionModelImportSourceKind.Archive));

        CollectionAssert.AreEquivalent(
            new[] { "en-US", "it-IT" },
            result.ImportedModels.Select(model => model.Id).ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(models, "it-IT", "svad.quantized.onnx")));
    }

    [TestMethod]
    public async Task ImportArchiveReportsMissingSharedVad()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.CreateDirectory("italian-source");
        CreateModel(
            source,
            "it-IT",
            includeVad: false,
            includeOutputLocale: false,
            includeVadPath: false);
        var archive = workspace.PathFor("it-IT.zip");
        ZipFile.CreateFromDirectory(source, archive);
        var installer = new MicroAsrSpeechRecognitionModelInstaller(
            workspace.PathFor("application", "Models"));

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
                archive,
                SpeechRecognitionModelImportSourceKind.Archive)));

        StringAssert.Contains(exception.Message, "neural VAD");
    }

    [TestMethod]
    public async Task DeleteRemovesInstalledModelAndRaisesCatalogChange()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.CreateDirectory("source-model");
        CreateModel(source, "it-IT", includeVad: true);
        var models = workspace.PathFor("application", "Models");
        var catalog = new MicroAsrSpeechRecognitionModelCatalog(models);
        var installer = new MicroAsrSpeechRecognitionModelInstaller(catalog);
        var changeCount = 0;
        catalog.ModelsChanged += (_, _) => changeCount++;
        await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
            source,
            SpeechRecognitionModelImportSourceKind.Directory));

        var deleted = await installer.DeleteAsync("it-IT");

        Assert.IsTrue(deleted);
        Assert.IsFalse(Directory.Exists(Path.Combine(models, "it-IT")));
        Assert.AreEqual(2, changeCount);
    }

    [TestMethod]
    [DataRow("da-DK", "丹麦语", "Danish", "dk.png")]
    [DataRow("de-DE", "德语", "German", "de.png")]
    [DataRow("en-US", "英语（美国）", "English (United States)", "us.png")]
    [DataRow("es-ES", "西班牙语（西班牙）", "Spanish (Spain)", "es.png")]
    [DataRow("fr-FR", "法语（法国）", "French (France)", "fr.png")]
    [DataRow("it-IT", "意大利语", "Italian", "it.png")]
    [DataRow("ja-JP", "日语", "Japanese", "jp.png")]
    [DataRow("ko-KR", "韩语", "Korean", "kr.png")]
    [DataRow("pt-BR", "葡萄牙语（巴西）", "Portuguese (Brazil)", "br.png")]
    [DataRow("zh-CN", "中文（简体）", "Chinese (Simplified)", "cn.png")]
    public void ModelMetadataCoversSupportedLocales(
        string id,
        string chineseName,
        string englishName,
        string icon)
    {
        var model = new SpeechRecognitionModel(id);

        Assert.AreEqual(chineseName, model.ChineseName);
        Assert.AreEqual(englishName, model.EnglishName);
        Assert.AreEqual(icon, model.Icon);
    }

    [TestMethod]
    public async Task ImportArchiveRejectsEntriesOutsideExtractionDirectory()
    {
        using var workspace = new TestWorkspace();
        var archivePath = workspace.PathFor("invalid.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escaped.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("invalid");
        }
        var installer = new MicroAsrSpeechRecognitionModelInstaller(
            workspace.PathFor("application", "Models"));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await installer.ImportAsync(new SpeechRecognitionModelImportRequest(
                archivePath,
                SpeechRecognitionModelImportSourceKind.Archive)));

        Assert.IsFalse(File.Exists(workspace.PathFor("application", "escaped.txt")));
    }

    private static void CreateModel(
        string directory,
        string locale,
        bool includeVad,
        bool includeOutputLocale = true,
        bool includeVadPath = true)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "model_onnx_quant.config"), $"""
            ModelType=ONNX_TRANSFORMER_ENCODER
            ModelEncoder=encoder.onnx
            ModelPredictor=predictor.onnx
            ModelJoint=joint.onnx
            LangCandidates={locale}
            Lang={locale}
            """);
        var speechConfig = new List<string> { "token-path=tokens.txt" };
        if (includeVadPath)
            speechConfig.Add("vad-model-path=svad.quantized.onnx");
        if (includeOutputLocale)
            speechConfig.Add($"output-locale={locale}");
        File.WriteAllLines(Path.Combine(directory, "sr.ini"), speechConfig);
        File.WriteAllBytes(Path.Combine(directory, "encoder.onnx"), [1]);
        File.WriteAllBytes(Path.Combine(directory, "predictor.onnx"), [1]);
        File.WriteAllBytes(Path.Combine(directory, "joint.onnx"), [1]);
        File.WriteAllText(Path.Combine(directory, "tokens.txt"), "token");
        if (includeVad)
            File.WriteAllBytes(Path.Combine(directory, "svad.quantized.onnx"), [1]);
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "easychat-model-import-tests",
            Guid.NewGuid().ToString("N"));

        public TestWorkspace() => Directory.CreateDirectory(_root);

        public string PathFor(params string[] parts) =>
            parts.Aggregate(_root, Path.Combine);

        public string CreateDirectory(params string[] parts)
        {
            var path = PathFor(parts);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
