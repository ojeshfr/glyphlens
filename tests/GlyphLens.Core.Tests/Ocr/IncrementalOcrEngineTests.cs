using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Capture;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;
using GlyphLens.Core.Ocr;

namespace GlyphLens.Core.Tests.Ocr;

public sealed class IncrementalOcrEngineTests
{
    private const int Width = 400;
    private const int Height = 600;

    /// <summary>A frame built from solid blocks on white; a block's red value is its id, which the fake recogniser reads as text.</summary>
    private sealed class Scene
    {
        private readonly byte[] _pixels = new byte[Width * Height * 4];

        public Scene()
        {
            Array.Fill(_pixels, (byte)255);
        }

        public Scene Block(int id, int x, int y, int width = 120, int height = 24)
        {
            for (int row = y; row < y + height; row++)
            {
                for (int col = x; col < x + width; col++)
                {
                    int i = ((row * Width) + col) * 4;
                    _pixels[i] = 0;
                    _pixels[i + 1] = 0;
                    _pixels[i + 2] = (byte)id;
                    _pixels[i + 3] = 255;
                }
            }

            return this;
        }

        public Scene Pixel(int x, int y, byte blue)
        {
            _pixels[((y * Width) + x) * 4] = blue;
            return this;
        }

        public Scene Clone()
        {
            var copy = new Scene();
            _pixels.CopyTo(copy._pixels, 0);
            return copy;
        }

        public CapturedFrame ToFrame(long sequence, string target = "window:1")
        {
            var metadata = new FrameMetadata(target, Width, Height, Width * 4, PixelFormat.Bgra8, DateTimeOffset.UnixEpoch, sequence);
            return new CapturedFrame(metadata, (byte[])_pixels.Clone());
        }
    }

    /// <summary>Reads the blocks of whatever frame (or crop) it is given: one region per connected non-white area.</summary>
    private sealed class BlockReader : IOcrEngine
    {
        public List<(int Width, int Height)> Requests { get; } = [];

        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<ScreenTextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            var m = frame.Metadata;
            Requests.Add((m.Width, m.Height));
            var pixels = frame.Pixels.Span;
            var seen = new bool[m.Width * m.Height];
            var regions = new List<ScreenTextRegion>();
            for (int y = 0; y < m.Height; y++)
            {
                for (int x = 0; x < m.Width; x++)
                {
                    if (seen[(y * m.Width) + x] || IsWhite(pixels, m, x, y))
                    {
                        continue;
                    }

                    int minX = x, maxX = x, minY = y, maxY = y;
                    byte id = pixels[(y * m.Stride) + (x * 4) + 2];
                    var stack = new Stack<(int X, int Y)>();
                    stack.Push((x, y));
                    seen[(y * m.Width) + x] = true;
                    while (stack.Count > 0)
                    {
                        var (cx, cy) = stack.Pop();
                        minX = Math.Min(minX, cx);
                        maxX = Math.Max(maxX, cx);
                        minY = Math.Min(minY, cy);
                        maxY = Math.Max(maxY, cy);
                        foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                        {
                            if (nx >= 0 && ny >= 0 && nx < m.Width && ny < m.Height && !seen[(ny * m.Width) + nx] && !IsWhite(pixels, m, nx, ny))
                            {
                                seen[(ny * m.Width) + nx] = true;
                                stack.Push((nx, ny));
                            }
                        }
                    }

                    regions.Add(new ScreenTextRegion(Guid.NewGuid(), $"message {id}", minX, minY, maxX - minX + 1, maxY - minY + 1, DetectionSource.Ocr, null, null, DateTimeOffset.UnixEpoch));
                }
            }

            regions.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            return Task.FromResult<IReadOnlyList<ScreenTextRegion>>(regions);
        }

        private static bool IsWhite(ReadOnlySpan<byte> pixels, FrameMetadata m, int x, int y)
        {
            int i = (y * m.Stride) + (x * 4);
            return pixels[i] == 255 && pixels[i + 1] == 255 && pixels[i + 2] == 255;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeCapture : IScreenCaptureService
    {
        public CaptureStatus Status { get; private set; } = CaptureStatus.Capturing;

        public CaptureTarget? CurrentTarget => null;

        public IFrameSource Frames { get; } = new LatestFrameMailbox();

        public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged;

        public Task StartAsync(CaptureTarget target, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Move(CaptureStatus next)
        {
            var previous = Status;
            Status = next;
            StatusChanged?.Invoke(this, new CaptureStatusChangedEventArgs(previous, next));
        }
    }

    private static string Signature(IReadOnlyList<ScreenTextRegion> regions) =>
        string.Join(";", regions.OrderBy(r => r.Y).ThenBy(r => r.X).Select(r => $"{r.Text}@{r.X},{r.Y},{r.Width},{r.Height}"));

    private static async Task<string> FullReadAsync(Scene scene)
    {
        using var frame = scene.ToFrame(1);
        return Signature(await new BlockReader().RecognizeAsync(frame, CancellationToken.None));
    }

    private static Scene Chat() =>
        new Scene().Block(1, 20, 40).Block(2, 160, 100).Block(3, 20, 160).Block(4, 160, 220);

    [Fact]
    public async Task TheFirstPass_ReadsTheWholeFrame()
    {
        var reader = new BlockReader();
        using var engine = new IncrementalOcrEngine(reader);
        using var frame = Chat().ToFrame(1);

        var regions = await engine.RecognizeAsync(frame, CancellationToken.None);

        Assert.Equal(4, regions.Count);
        Assert.Equal([(Width, Height)], reader.Requests);
        Assert.Equal(OcrPassKind.Full, engine.LastPass!.Kind);
        Assert.Equal("first pass", engine.LastPass.Reason);
    }

    [Fact]
    public async Task AnIdenticalFrame_ReusesTheResult_WithoutReading()
    {
        var reader = new BlockReader();
        using var engine = new IncrementalOcrEngine(reader);
        var scene = Chat();
        using var first = scene.ToFrame(1);
        var firstResult = await engine.RecognizeAsync(first, CancellationToken.None);

        using var second = scene.ToFrame(2);
        var secondResult = await engine.RecognizeAsync(second, CancellationToken.None);

        Assert.Single(reader.Requests);
        Assert.Equal(OcrPassKind.Unchanged, engine.LastPass!.Kind);
        Assert.Equal(0, engine.LastPass.ReadFraction);
        Assert.Equal(firstResult.Select(r => r.Id), secondResult.Select(r => r.Id));
    }

    [Fact]
    public async Task ANewMessageAtTheBottom_ReadsOnlyThatPart_AndGivesTheSameResultAsAFullRead()
    {
        var reader = new BlockReader();
        using var engine = new IncrementalOcrEngine(reader);
        var before = Chat();
        using (var first = before.ToFrame(1))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        var after = before.Clone().Block(5, 20, 400);
        using var second = after.ToFrame(2);
        var result = await engine.RecognizeAsync(second, CancellationToken.None);

        Assert.Equal(OcrPassKind.Partial, engine.LastPass!.Kind);
        Assert.Equal(2, reader.Requests.Count);
        Assert.True((long)reader.Requests[1].Width * reader.Requests[1].Height < (long)Width * Height / 4, "only a small part was read");
        Assert.Equal(await FullReadAsync(after), Signature(result));
    }

    [Fact]
    public async Task UnchangedLines_KeepTheirIdentity_WhenAPartialReadAddsAnother()
    {
        using var engine = new IncrementalOcrEngine(new BlockReader());
        var before = Chat();
        using var first = before.ToFrame(1);
        var firstResult = await engine.RecognizeAsync(first, CancellationToken.None);

        using var second = before.Clone().Block(5, 20, 400).ToFrame(2);
        var secondResult = await engine.RecognizeAsync(second, CancellationToken.None);

        Assert.Equal(firstResult.Select(r => r.Id), secondResult.Take(4).Select(r => r.Id));
    }

    [Fact]
    public async Task ASinglePixelChange_InsideALine_IsNotMissed()
    {
        var reader = new BlockReader();
        using var engine = new IncrementalOcrEngine(reader);
        var before = Chat();
        using (var first = before.ToFrame(1))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        var after = before.Clone().Pixel(100, 170, 7);
        using var second = after.ToFrame(2);
        _ = await engine.RecognizeAsync(second, CancellationToken.None);

        Assert.Equal(OcrPassKind.Partial, engine.LastPass!.Kind);
        Assert.Equal(2, reader.Requests.Count);
    }

    [Fact]
    public async Task AChangeWhereThereIsNoText_StillGivesTheSameResultAsAFullRead()
    {
        using var engine = new IncrementalOcrEngine(new BlockReader());
        var before = Chat();
        using (var first = before.ToFrame(1))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        var after = before.Clone().Pixel(350, 500, 9);
        using var second = after.ToFrame(2);

        Assert.Equal(await FullReadAsync(after), Signature(await engine.RecognizeAsync(second, CancellationToken.None)));
    }

    [Fact]
    public async Task ANewLineNextToAnExistingOne_RereadsTheExistingLineAsAWhole()
    {
        var reader = new BlockReader();
        using var engine = new IncrementalOcrEngine(reader);
        var before = new Scene().Block(1, 20, 100);
        using (var first = before.ToFrame(1))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        var after = before.Clone().Block(2, 20, 134); // 10 pixels below the first line: inside the margin of the change
        using var second = after.ToFrame(2);
        var result = await engine.RecognizeAsync(second, CancellationToken.None);

        Assert.Equal(await FullReadAsync(after), Signature(result));
    }

    [Fact]
    public async Task AChangeInsideALine_RereadsTheWholeLine_AndRemovesTheOldOne()
    {
        using var engine = new IncrementalOcrEngine(new BlockReader());
        var before = Chat();
        using (var first = before.ToFrame(1))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        var after = before.Clone().Block(9, 160, 100); // line 2 now reads differently
        using var second = after.ToFrame(2);
        var result = await engine.RecognizeAsync(second, CancellationToken.None);

        Assert.Equal(4, result.Count);
        Assert.Contains(result, r => r.Text == "message 9");
        Assert.DoesNotContain(result, r => r.Text == "message 2");
        Assert.Equal(await FullReadAsync(after), Signature(result));
    }

    [Fact]
    public async Task AScroll_ReadsTheWholeFrame()
    {
        var reader = new BlockReader();
        using var engine = new IncrementalOcrEngine(reader);
        using (var first = Chat().ToFrame(1))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        // Everything moves 120 pixels up and a block comes into view at the bottom.
        var scrolled = new Scene().Block(1, 20, 20).Block(3, 20, 40).Block(4, 160, 100).Block(5, 20, 460).Block(6, 160, 250).Block(7, 20, 330).Block(8, 160, 520).Block(9, 20, 560);
        using var second = scrolled.ToFrame(2);
        var result = await engine.RecognizeAsync(second, CancellationToken.None);

        Assert.Equal(OcrPassKind.Full, engine.LastPass!.Kind);
        Assert.Equal(await FullReadAsync(scrolled), Signature(result));
    }

    [Fact]
    public async Task ManySeparateChanges_ReadTheWholeFrame()
    {
        var reader = new BlockReader();
        using var engine = new IncrementalOcrEngine(reader);
        var before = Chat();
        using (var first = before.ToFrame(1))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        var after = before.Clone().Pixel(5, 5, 1).Pixel(390, 5, 1).Pixel(5, 590, 1).Pixel(390, 590, 1).Pixel(200, 300, 1);
        using var second = after.ToFrame(2);
        _ = await engine.RecognizeAsync(second, CancellationToken.None);

        Assert.Equal(OcrPassKind.Full, engine.LastPass!.Kind);
        Assert.Equal("too many separate changes", engine.LastPass.Reason);
    }

    [Fact]
    public async Task AfterTheLimitOfPartialPasses_TheWholeFrameIsReadAgain()
    {
        var reader = new BlockReader();
        using var engine = new IncrementalOcrEngine(reader, options: new IncrementalOcrOptions { FullEveryPartialPasses = 2 });
        var scene = Chat();
        using (var first = scene.ToFrame(1))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        for (int i = 0; i < 2; i++)
        {
            scene = scene.Clone().Pixel(100, 170, (byte)(i + 1));
            using var frame = scene.ToFrame(2 + i);
            _ = await engine.RecognizeAsync(frame, CancellationToken.None);
            Assert.Equal(OcrPassKind.Partial, engine.LastPass!.Kind);
        }

        scene = scene.Clone().Pixel(100, 170, 99);
        using var third = scene.ToFrame(10);
        _ = await engine.RecognizeAsync(third, CancellationToken.None);

        Assert.Equal(OcrPassKind.Full, engine.LastPass!.Kind);
        Assert.Equal("periodic full read", engine.LastPass.Reason);
    }

    [Fact]
    public async Task AfterTheTimeLimit_TheWholeFrameIsReadAgain()
    {
        var time = new ManualTime();
        using var engine = new IncrementalOcrEngine(new BlockReader(), options: new IncrementalOcrOptions { FullAfter = TimeSpan.FromSeconds(10) }, timeProvider: time);
        var scene = Chat();
        using (var first = scene.ToFrame(1))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        time.Advance(TimeSpan.FromSeconds(11));
        using var second = scene.Clone().Pixel(100, 170, 3).ToFrame(2);
        _ = await engine.RecognizeAsync(second, CancellationToken.None);

        Assert.Equal(OcrPassKind.Full, engine.LastPass!.Kind);
        Assert.Equal("periodic full read", engine.LastPass.Reason);
    }

    [Fact]
    public async Task ADifferentTarget_OrEngineSetting_ReadsTheWholeFrame()
    {
        string? setting = "a";
        using var engine = new IncrementalOcrEngine(new BlockReader(), engineKey: () => setting);
        var scene = Chat();
        using (var first = scene.ToFrame(1))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        using (var otherTarget = scene.ToFrame(2, target: "window:2"))
        {
            _ = await engine.RecognizeAsync(otherTarget, CancellationToken.None);
            Assert.Equal(OcrPassKind.Full, engine.LastPass!.Kind);
        }

        setting = "b";
        using var otherEngine = scene.ToFrame(3, target: "window:2");
        _ = await engine.RecognizeAsync(otherEngine, CancellationToken.None);
        Assert.Equal(OcrPassKind.Full, engine.LastPass!.Kind);
    }

    [Fact]
    public async Task ANewCapture_WithRestartedSequenceNumbers_ReadsTheWholeFrame()
    {
        using var engine = new IncrementalOcrEngine(new BlockReader());
        var scene = Chat();
        using (var first = scene.ToFrame(50))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        using var restarted = scene.ToFrame(1);
        _ = await engine.RecognizeAsync(restarted, CancellationToken.None);

        Assert.Equal(OcrPassKind.Full, engine.LastPass!.Kind);
        Assert.Equal("a new capture started", engine.LastPass.Reason);
    }

    [Fact]
    public async Task AFailedRead_ForgetsTheState_SoTheNextPassIsFull()
    {
        var reader = new BlockReader();
        using var engine = new IncrementalOcrEngine(reader);
        var scene = Chat();
        using (var first = scene.ToFrame(1))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        reader.Failure = new OcrException("engine failed");
        using (var failing = scene.Clone().Block(5, 20, 400).ToFrame(2))
        {
            _ = await Assert.ThrowsAsync<OcrException>(() => engine.RecognizeAsync(failing, CancellationToken.None));
        }

        reader.Failure = null;
        using var again = scene.ToFrame(3);
        _ = await engine.RecognizeAsync(again, CancellationToken.None);

        Assert.Equal(OcrPassKind.Full, engine.LastPass!.Kind);
        Assert.Equal("first pass", engine.LastPass.Reason);
    }

    [Fact]
    public async Task WhenCaptureStops_TheSavedFrameAndTextAreDropped()
    {
        var capture = new FakeCapture();
        var reader = new BlockReader();
        using var engine = new IncrementalOcrEngine(reader, capture);
        var scene = Chat();
        using (var first = scene.ToFrame(1))
        {
            _ = await engine.RecognizeAsync(first, CancellationToken.None);
        }

        capture.Move(CaptureStatus.Idle);
        using var second = scene.ToFrame(2);
        _ = await engine.RecognizeAsync(second, CancellationToken.None);

        Assert.Equal(2, reader.Requests.Count);
        Assert.Equal(OcrPassKind.Full, engine.LastPass!.Kind);
    }

    [Fact]
    public async Task DisposingTheEngine_StopsListeningToCapture()
    {
        var capture = new FakeCapture();
        var engine = new IncrementalOcrEngine(new BlockReader(), capture);
        engine.Dispose();

        capture.Move(CaptureStatus.Idle);

        await Task.CompletedTask;
        Assert.Null(engine.LastPass);
    }

    [Fact]
    public async Task ACancelledPassAfterTheGate_LeavesTheEngineUsable()
    {
        var reader = new BlockReader();
        using var engine = new IncrementalOcrEngine(reader);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var frame = Chat().ToFrame(1);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RecognizeAsync(frame, cts.Token));

        _ = await engine.RecognizeAsync(frame, CancellationToken.None);
        Assert.Equal(OcrPassKind.Full, engine.LastPass!.Kind);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(12345)]
    public async Task ThePartialResult_EqualsAFullRead_ForARunOfRandomEdits(int seed)
    {
        var random = new Random(seed);
        var reader = new BlockReader();
        using var engine = new IncrementalOcrEngine(reader, options: new IncrementalOcrOptions { FullEveryPartialPasses = 1000, FullAfter = TimeSpan.FromHours(1) });
        var scene = new Scene();
        long sequence = 1;
        int nextId = 1;
        var slots = Enumerable.Range(0, 10).Select(i => (X: 20 + ((i % 2) * 180), Y: 20 + (i * 56))).ToList();

        for (int step = 0; step < 40; step++)
        {
            var slot = slots[random.Next(slots.Count)];
            scene = scene.Clone();
            if (random.Next(4) == 0)
            {
                scene.Block(255, slot.X, slot.Y, 120, 24); // overwrite with a block of a new id
            }
            else
            {
                scene.Block(nextId++ % 200 + 1, slot.X, slot.Y, 120, 24);
            }

            using var frame = scene.ToFrame(sequence++);
            var result = await engine.RecognizeAsync(frame, CancellationToken.None);

            Assert.Equal(await FullReadAsync(scene), Signature(result));
        }
    }
}
