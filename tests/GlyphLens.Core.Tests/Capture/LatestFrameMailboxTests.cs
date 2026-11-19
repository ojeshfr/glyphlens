using GlyphLens.Core.Capture;

namespace GlyphLens.Core.Tests.Capture;

public class LatestFrameMailboxTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);

    [Fact]
    public void NewMailbox_IsEmpty()
    {
        using var mailbox = new LatestFrameMailbox();

        Assert.Equal(0, mailbox.Version);
        Assert.Null(mailbox.LatestMetadata);
        Assert.Null(mailbox.TryAcquireLatest());
    }

    [Fact]
    public void Publish_MakesFrameLatestAndAssignsIncreasingVersions()
    {
        var frames = new TestFrames();
        using var mailbox = new LatestFrameMailbox();

        Assert.Equal(1, mailbox.Publish(frames.Create(sequence: 1)));
        Assert.Equal(2, mailbox.Publish(frames.Create(sequence: 2)));

        Assert.Equal(2, mailbox.Version);
        Assert.Equal(2, mailbox.LatestMetadata?.Sequence);
    }

    [Fact]
    public void Publish_ReplacesAndReleasesThePreviousFrame()
    {
        var frames = new TestFrames();
        using var mailbox = new LatestFrameMailbox();

        mailbox.Publish(frames.Create());
        mailbox.Publish(frames.Create());

        Assert.Equal(1, frames.Released);
        Assert.Equal(1, frames.Live);
    }

    [Fact]
    public void SlowConsumer_CausesNoBacklog_OnlyTheLatestFrameStaysAlive()
    {
        var frames = new TestFrames();
        using var mailbox = new LatestFrameMailbox();

        for (var i = 1; i <= 1000; i++)
        {
            mailbox.Publish(frames.Create(sequence: i));
        }

        Assert.Equal(1, frames.Live); // 999 obsolete frames were dropped and their buffers released
        using var latest = mailbox.TryAcquireLatest()!.Value.Frame;
        Assert.Equal(1000, latest.Metadata.Sequence);
    }

    [Fact]
    public void AcquiredFrame_StaysValidAfterBeingReplaced_ThenReleasesWhenConsumerFinishes()
    {
        var frames = new TestFrames();
        using var mailbox = new LatestFrameMailbox();
        mailbox.Publish(frames.Create(sequence: 1));

        var acquired = mailbox.TryAcquireLatest()!.Value.Frame; // consumer starts working on frame 1
        mailbox.Publish(frames.Create(sequence: 2)); // newer frame replaces it

        Assert.Equal(0, frames.Released);
        Assert.Equal(1, acquired.Metadata.Sequence);
        Assert.Equal(32, acquired.Pixels.Length);

        acquired.Dispose();
        Assert.Equal(1, frames.Released);
    }

    [Fact]
    public void TryAcquireLatest_ReturnsFrameWithItsVersion()
    {
        var frames = new TestFrames();
        using var mailbox = new LatestFrameMailbox();
        mailbox.Publish(frames.Create());
        mailbox.Publish(frames.Create());

        var latest = mailbox.TryAcquireLatest();

        Assert.NotNull(latest);
        Assert.Equal(2, latest.Value.Version);
        latest.Value.Frame.Dispose();
    }

    [Fact]
    public void Clear_ReleasesFrameButKeepsVersionMonotonic()
    {
        var frames = new TestFrames();
        using var mailbox = new LatestFrameMailbox();
        mailbox.Publish(frames.Create());

        mailbox.Clear();

        Assert.Null(mailbox.LatestMetadata);
        Assert.Null(mailbox.TryAcquireLatest());
        Assert.Equal(1, frames.Released);
        Assert.Equal(1, mailbox.Version);
        Assert.Equal(2, mailbox.Publish(frames.Create()));
    }

    [Fact]
    public async Task WaitForNewer_ReturnsImmediatelyWhenAFrameIsAlreadyNewer()
    {
        var frames = new TestFrames();
        using var mailbox = new LatestFrameMailbox();
        mailbox.Publish(frames.Create(sequence: 5));

        var result = await mailbox.WaitForNewerAsync(0, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result.Value.Version);
        result.Value.Frame.Dispose();
    }

    [Fact]
    public async Task WaitForNewer_BlocksUntilANewFrameIsPublished()
    {
        var frames = new TestFrames();
        using var mailbox = new LatestFrameMailbox();
        mailbox.Publish(frames.Create(sequence: 1));

        var waiting = mailbox.WaitForNewerAsync(mailbox.Version, CancellationToken.None).AsTask();
        await Task.Delay(Short);
        Assert.False(waiting.IsCompleted);

        mailbox.Publish(frames.Create(sequence: 2));
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Equal(2, result.Value.Frame.Metadata.Sequence);
        result.Value.Frame.Dispose();
    }

    [Fact]
    public async Task WaitForNewer_SkipsStaleFramesAndReturnsTheLatest()
    {
        var frames = new TestFrames();
        using var mailbox = new LatestFrameMailbox();
        for (var i = 1; i <= 5; i++)
        {
            mailbox.Publish(frames.Create(sequence: i));
        }

        var result = await mailbox.WaitForNewerAsync(2, CancellationToken.None);

        Assert.Equal(5, result!.Value.Version);
        result.Value.Frame.Dispose();
    }

    [Fact]
    public async Task WaitForNewer_ThrowsWhenCancelled_AndLeavesNoReferenceBehind()
    {
        var frames = new TestFrames();
        using var mailbox = new LatestFrameMailbox();
        mailbox.Publish(frames.Create());
        using var cts = new CancellationTokenSource();

        var waiting = mailbox.WaitForNewerAsync(mailbox.Version, cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        mailbox.Dispose();
        Assert.Equal(1, frames.Released);
    }

    [Fact]
    public async Task WaitForNewer_AfterCancellationRequested_ThrowsWithoutWaiting()
    {
        using var mailbox = new LatestFrameMailbox();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mailbox.WaitForNewerAsync(0, cts.Token).AsTask());
    }

    [Fact]
    public async Task Dispose_ReleasesTheFrameAndCompletesWaitersWithNull()
    {
        var frames = new TestFrames();
        var mailbox = new LatestFrameMailbox();
        mailbox.Publish(frames.Create());
        var waiting = mailbox.WaitForNewerAsync(mailbox.Version, CancellationToken.None).AsTask();

        mailbox.Dispose();

        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, frames.Released);
    }

    [Fact]
    public void Publish_AfterDispose_ReleasesTheFrameAndReturnsZero()
    {
        var frames = new TestFrames();
        var mailbox = new LatestFrameMailbox();
        mailbox.Dispose();

        var version = mailbox.Publish(frames.Create());

        Assert.Equal(0, version);
        Assert.Equal(1, frames.Released);
        Assert.Null(mailbox.TryAcquireLatest());
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var mailbox = new LatestFrameMailbox();

        mailbox.Dispose();
        mailbox.Dispose();
    }

    [Fact]
    public void Publish_RejectsNull()
    {
        using var mailbox = new LatestFrameMailbox();

        Assert.Throws<ArgumentNullException>(() => mailbox.Publish(null!));
    }

    [Fact]
    public async Task ConcurrentProducerAndConsumers_NeverLeakOrCorruptFrames()
    {
        var frames = new TestFrames();
        var mailbox = new LatestFrameMailbox();
        using var stop = new CancellationTokenSource();
        var consumed = 0;

        var consumers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            long seen = 0;
            while (!stop.IsCancellationRequested)
            {
                var result = await mailbox.WaitForNewerAsync(seen, CancellationToken.None);
                if (result is null)
                {
                    return;
                }

                using (result.Value.Frame)
                {
                    seen = result.Value.Version;
                    _ = result.Value.Frame.Pixels.Length; // would throw if the buffer had been released too early
                    Interlocked.Increment(ref consumed);
                }
            }
        })).ToList();

        // Frames must be created on one thread (TestFrames.Created is not synchronised).
        for (var i = 1; i <= 5000; i++)
        {
            mailbox.Publish(frames.Create(sequence: i));
            if (i % 50 == 0)
            {
                await Task.Yield();
            }
        }

        await stop.CancelAsync();
        mailbox.Dispose();
        await Task.WhenAll(consumers).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, frames.Live);
        Assert.True(consumed > 0);
    }
}
