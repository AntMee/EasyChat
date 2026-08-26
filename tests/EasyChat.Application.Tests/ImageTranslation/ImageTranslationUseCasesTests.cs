using System.Runtime.CompilerServices;
using EasyChat.Application.ImageTranslation;
using EasyChat.Application.Tests.Settings;
using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Tests.ImageTranslation;

[TestClass]
public sealed class ImageTranslationUseCasesTests
{
    [TestMethod]
    public async Task TranslateAsync_PreservesMasterRegionOrderingAndRendererBoundary()
    {
        var renderer = new FakeRenderer();
        var settings = SettingsTestData.CreateBundle() with
        {
            General = SettingsTestData.CreateBundle().General with
            {
                TranslationEngine = TranslationEngineNames.MachineTrans
            }
        };
        var useCases = new ImageTranslationUseCases(
            new FakeTranslationUseCases(),
            new FakeSettingsUseCases(settings),
            renderer);
        var image = new ImageFrame(2, 2, 8, 96, 96, new byte[16]);
        var lower = Region("lower", 20, 30);
        var upper = Region("upper", 5, 10);

        var result = await useCases.TranslateAsync(new ImageTranslationRequest(
            image,
            new OcrRecognitionResult([lower, upper, Region(" ", 0, 0)]),
            null,
            new TranslationLanguage("en", "English")));

        Assert.AreSame(image, renderer.Source);
        Assert.HasCount(2, renderer.Overlays);
        Assert.AreEqual("upper", renderer.Overlays[0].Region.Text);
        Assert.AreEqual("translated:upper", renderer.Overlays[0].Translation);
        Assert.AreEqual(2, result.DetectedBlockCount);
        Assert.AreEqual(2, result.TranslatedBlockCount);
    }

    [TestMethod]
    public async Task TranslateRegionsAsync_FallsBackForMissingAiBatchItems()
    {
        var settings = SettingsTestData.CreateBundle() with
        {
            General = SettingsTestData.CreateBundle().General with
            {
                TranslationEngine = TranslationEngineNames.AiModel
            }
        };
        var useCases = new ImageTranslationUseCases(
            new PartialBatchTranslationUseCases(),
            new FakeSettingsUseCases(settings),
            new FakeRenderer());
        var recognition = new OcrRecognitionResult(
        [
            Region("first", 0, 0),
            Region("second", 20, 0)
        ]);

        var result = await useCases.TranslateRegionsAsync(
            new ImageRegionTranslationRequest(
                recognition,
                [0, 1],
                new TranslationLanguage("en", "English"),
                new TranslationLanguage("zh-Hans", "Chinese")));

        Assert.HasCount(2, result.Translations);
        Assert.AreEqual("batch:first", result.Translations.Single(item => item.RegionIndex == 0).Translation);
        Assert.AreEqual("fallback:second", result.Translations.Single(item => item.RegionIndex == 1).Translation);
    }

    [TestMethod]
    public async Task TranslateAsync_TranslatesEachRegionIndividually()
    {
        var renderer = new FakeRenderer();
        var translator = new FakeTranslationUseCases();
        var useCases = new ImageTranslationUseCases(
            translator,
            new FakeSettingsUseCases(MachineTranslationSettings()),
            renderer);
        var image = new ImageFrame(2, 2, 8, 96, 96, new byte[16]);

        await useCases.TranslateAsync(new ImageTranslationRequest(
            image,
            new OcrRecognitionResult(
            [
                Region("first line", 10, 0),
                Region("second line", 10, 5),
                Region("third line", 10, 10)
            ]),
            null,
            new TranslationLanguage("zh-Hans", "Chinese")));

        CollectionAssert.AreEqual(
            new[] { "first line", "second line", "third line" },
            translator.Requests);
        Assert.HasCount(3, renderer.Overlays);
        CollectionAssert.AreEqual(
            new[] { "translated:first line", "translated:second line", "translated:third line" },
            renderer.Overlays.Select(overlay => overlay.Translation).ToArray());
        Assert.IsTrue(renderer.Overlays.All(overlay => overlay.EraseRegions is null));
    }

    [TestMethod]
    public async Task TranslateAsync_KeepsSeparateColumnsAsSeparateTranslationGroups()
    {
        var renderer = new FakeRenderer();
        var translator = new FakeTranslationUseCases();
        var useCases = new ImageTranslationUseCases(
            translator,
            new FakeSettingsUseCases(MachineTranslationSettings()),
            renderer);
        var image = new ImageFrame(2, 2, 8, 96, 96, new byte[16]);

        await useCases.TranslateAsync(new ImageTranslationRequest(
            image,
            new OcrRecognitionResult(
            [
                Region("left top", 0, 0),
                Region("right top", 100, 0),
                Region("left bottom", 0, 5),
                Region("right bottom", 100, 5)
            ]),
            null,
            new TranslationLanguage("zh-Hans", "Chinese")));

        CollectionAssert.AreEquivalent(
            new[] { "left top", "right top", "left bottom", "right bottom" },
            translator.Requests);
        Assert.HasCount(4, renderer.Overlays);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "translated:left top",
                "translated:right top",
                "translated:left bottom",
                "translated:right bottom"
            },
            renderer.Overlays.Select(overlay => overlay.Translation).ToArray());
    }

    [TestMethod]
    public async Task TranslateAsync_DoesNotCollapseAdjacentLines()
    {
        var renderer = new FakeRenderer();
        var useCases = new ImageTranslationUseCases(
            new CollapsingTranslationUseCases(),
            new FakeSettingsUseCases(MachineTranslationSettings()),
            renderer);
        var image = new ImageFrame(2, 2, 8, 96, 96, new byte[16]);

        var result = await useCases.TranslateAsync(new ImageTranslationRequest(
            image,
            new OcrRecognitionResult(
            [
                Region("first line", 10, 0),
                Region("second line", 10, 5),
                Region("third line", 10, 10)
            ]),
            null,
            new TranslationLanguage("zh-Hans", "Chinese")));

        Assert.HasCount(3, renderer.Overlays);
        CollectionAssert.AreEqual(
            new[] { "translated:first line", "translated:second line", "translated:third line" },
            renderer.Overlays.Select(overlay => overlay.Translation).ToArray());
        Assert.AreEqual(3, result.TranslatedBlockCount);
    }

    [TestMethod]
    public async Task TranslateRegionsAsync_CombinesOnlySelectedAdjacentRegions()
    {
        var translator = new CollapsingTranslationUseCases();
        var useCases = new ImageTranslationUseCases(
            translator,
            new FakeSettingsUseCases(MachineTranslationSettings()),
            new FakeRenderer());
        var recognition = new OcrRecognitionResult(
        [
                Region("first line", 10, 0),
                Region("second line", 10, 5),
                Region("third line", 10, 10)
        ]);

        var result = await useCases.TranslateRegionsAsync(
            new ImageRegionTranslationRequest(
                recognition,
                [0, 1],
                new TranslationLanguage("en", "English"),
                new TranslationLanguage("zh-Hans", "Chinese")));

        CollectionAssert.AreEqual(
            new[] { "first line\nsecond line" },
            translator.Requests);
        Assert.HasCount(1, result.Translations);
        Assert.AreEqual(0, result.Translations[0].RegionIndex);
        Assert.IsNotNull(result.Translations[0].RenderRegion);
        Assert.IsNotNull(result.Translations[0].EraseRegions);
        Assert.HasCount(2, result.Translations[0].EraseRegions!);
        Assert.IsFalse(translator.Requests[0].Contains("third line", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MergeRegions_PreservesTheOrientedBoundsForRotatedText()
    {
        const double angle = 30;
        var regions = new[]
        {
            RotatedRegion("first", 10, 0, 100, 20, angle, confidence: 0.95),
            RotatedRegion("second", 10, 24, 70, 20, angle, confidence: 0.80)
        };

        var merged = ImageTranslationUseCases.MergeRegions(regions, "first\nsecond");
        var bounds = ProjectedBounds(merged.Polygon, angle);

        Assert.AreEqual(angle, merged.Angle, 0.001);
        Assert.AreEqual(10, bounds.Left, 0.001);
        Assert.AreEqual(110, bounds.Right, 0.001);
        Assert.AreEqual(0, bounds.Top, 0.001);
        Assert.AreEqual(44, bounds.Bottom, 0.001);
        Assert.AreEqual(0.80, merged.Confidence, 0.001);
    }

    [TestMethod]
    public void MergeRegions_UsesACircularMeanAcrossTheAngleBoundary()
    {
        var regions = new[]
        {
            RotatedRegion("first", 10, 0, 100, 20, 179),
            RotatedRegion("second", 10, 24, 70, 20, -179)
        };

        var merged = ImageTranslationUseCases.MergeRegions(regions, "first\nsecond");

        Assert.AreEqual(180, Math.Abs(merged.Angle), 0.001);
    }

    [TestMethod]
    public async Task TranslateAsync_DoesNotJoinNearbyTitleAndBodyRegions()
    {
        var renderer = new FakeRenderer();
        var translator = new FakeTranslationUseCases();
        var useCases = new ImageTranslationUseCases(
            translator,
            new FakeSettingsUseCases(MachineTranslationSettings()),
            renderer);
        var image = new ImageFrame(2, 2, 8, 96, 96, new byte[16]);

        await useCases.TranslateAsync(new ImageTranslationRequest(
            image,
            new OcrRecognitionResult(
            [
                Region("Title", 10, 0),
                Region("Body", 10, 8)
            ]),
            null,
            new TranslationLanguage("zh-Hans", "Chinese")));

        CollectionAssert.AreEqual(
            new[] { "Title", "Body" },
            translator.Requests);
        Assert.HasCount(2, renderer.Overlays);
    }

    private static SettingsBundle MachineTranslationSettings() => SettingsTestData.CreateBundle() with
    {
        General = SettingsTestData.CreateBundle().General with
        {
            TranslationEngine = TranslationEngineNames.MachineTrans
        }
    };

    private static OcrTextRegion Region(string text, double x, double y) =>
        new(text,
        [
            new ImagePoint(x, y),
            new ImagePoint(x + 8, y),
            new ImagePoint(x + 8, y + 4),
            new ImagePoint(x, y + 4)
        ],
        0);

    private static OcrTextRegion RotatedRegion(
        string text,
        double left,
        double top,
        double width,
        double height,
        double angle,
        double confidence = 1)
    {
        var radians = angle * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        ImagePoint Point(double horizontal, double vertical) =>
            new(
                horizontal * cosine - vertical * sine,
                horizontal * sine + vertical * cosine);

        return new OcrTextRegion(
            text,
            [
                Point(left, top),
                Point(left + width, top),
                Point(left + width, top + height),
                Point(left, top + height)
            ],
            angle,
            confidence);
    }

    private static ProjectedRegionBounds ProjectedBounds(
        IReadOnlyList<ImagePoint> points,
        double angle)
    {
        var radians = angle * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var horizontal = points.Select(point => point.X * cosine + point.Y * sine).ToArray();
        var vertical = points.Select(point => -point.X * sine + point.Y * cosine).ToArray();
        return new ProjectedRegionBounds(
            horizontal.Min(),
            vertical.Min(),
            horizontal.Max(),
            vertical.Max());
    }

    private readonly record struct ProjectedRegionBounds(
        double Left,
        double Top,
        double Right,
        double Bottom);

    private sealed class FakeRenderer : IImageTranslationRenderer
    {
        public ImageFrame? Source { get; private set; }
        public IReadOnlyList<ImageTranslationOverlay> Overlays { get; private set; } = [];

        public Task<ImageTranslationRenderResult> RenderAsync(
            ImageFrame background,
            IReadOnlyList<ImageTranslationOverlay> overlays,
            ImageTranslationRenderOptions options,
            CancellationToken cancellationToken = default)
        {
            Source = background;
            Overlays = overlays;
            return Task.FromResult(new ImageTranslationRenderResult(
                background,
                [],
                overlays.Count));
        }
    }

    private sealed class FakeTranslationUseCases : ITranslationUseCases
    {
        public List<string> Requests { get; } = [];

        public ITranslationSession Prepare(TranslationProviderSelection? provider = null) =>
            new FakeTranslationSession(Requests);

        public Task<Result<TranslationResponse>> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<TranslationResponse>.Success(
                new TranslationResponse($"translated:{request.Text}")));

        public async IAsyncEnumerable<TranslationEvent> StreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeTranslationSession(ICollection<string>? requests = null) : ITranslationSession
    {
        public bool SupportsIdentifiedStreaming => false;

        public Task<TranslationResponse> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            requests?.Add(request.Text);
            return Task.FromResult(new TranslationResponse($"translated:{request.Text}"));
        }

        public async IAsyncEnumerable<TranslationEvent> StreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<IdentifiedTranslationDelta> StreamIdentifiedAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class PartialBatchTranslationUseCases : ITranslationUseCases
    {
        public ITranslationSession Prepare(TranslationProviderSelection? provider = null) =>
            provider?.PromptOverride is not null
                ? new PartialBatchSession()
                : new FallbackSession();

        public Task<Result<TranslationResponse>> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<TranslationEvent> StreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CollapsingTranslationUseCases : ITranslationUseCases
    {
        public List<string> Requests { get; } = [];

        public ITranslationSession Prepare(TranslationProviderSelection? provider = null) =>
            new CollapsingTranslationSession(Requests);

        public Task<Result<TranslationResponse>> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<TranslationEvent> StreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CollapsingTranslationSession(ICollection<string> requests) : ITranslationSession
    {
        public bool SupportsIdentifiedStreaming => false;

        public Task<TranslationResponse> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            requests.Add(request.Text);
            return Task.FromResult(new TranslationResponse(
                $"translated:{request.Text.Replace("\n", " ", StringComparison.Ordinal)}"));
        }

        public async IAsyncEnumerable<TranslationEvent> StreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<IdentifiedTranslationDelta> StreamIdentifiedAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class PartialBatchSession : ITranslationSession
    {
        public bool SupportsIdentifiedStreaming => true;

        public Task<TranslationResponse> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<TranslationEvent> StreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<IdentifiedTranslationDelta> StreamIdentifiedAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new IdentifiedTranslationDelta("block-0", "batch:first");
        }
    }

    private sealed class FallbackSession : ITranslationSession
    {
        public bool SupportsIdentifiedStreaming => true;

        public Task<TranslationResponse> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationResponse($"fallback:{request.Text}"));

        public async IAsyncEnumerable<TranslationEvent> StreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<IdentifiedTranslationDelta> StreamIdentifiedAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeSettingsUseCases(SettingsBundle current) : ISettingsUseCases
    {
        public event EventHandler<SettingsChangedEventArgs>? SettingsChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed
        {
            add { }
            remove { }
        }
        public bool IsInitialized => true;
        public SettingsBundle Current { get; } = current;

        public ValueTask<Result<SettingsBundle>> InitializeAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<SettingsBundle>.Success(Current));

        public Result Update(SettingsSection section, SettingsBundle settings) => Result.Success();
        public ValueTask<Result> FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
