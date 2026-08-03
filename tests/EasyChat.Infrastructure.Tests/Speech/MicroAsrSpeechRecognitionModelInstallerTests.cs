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
        Assert.HasCount(2, second.ExistingModels);
        Assert.AreEqual(1, changeCount);
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

    private static void CreateModel(string directory, string locale, bool includeVad)
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
        File.WriteAllText(Path.Combine(directory, "sr.ini"), """
            token-path=tokens.txt
            vad-model-path=svad.quantized.onnx
            """);
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
