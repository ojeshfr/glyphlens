using System.Globalization;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Core.Translation;

namespace GlyphLens.Core.Tests.Translation;

public class LruTranslationCacheTests
{
    private const string ModelV1 = "argos:de-en@1.9";
    private const string ModelV2 = "argos:de-en@2.0";

    private static TranslationCacheKey Key(string text, string model = ModelV1, string source = "de", string target = "en") =>
        new(text, source, target, model);

    private static TranslationResult Result(string text, string model = ModelV1, string translated = "translated") =>
        new(text, translated, "de", "en", model);

    private static void Store(LruTranslationCache cache, string text, string model = ModelV1) =>
        cache.Store(Key(text, model), Result(text, model));

    private static bool Contains(LruTranslationCache cache, string text, string model = ModelV1) =>
        cache.TryGet(Key(text, model), out _);

    // ---- construction ----

    [Fact]
    public void NewCache_IsEmpty_WithTheDefaultCapacityOf512()
    {
        var cache = new LruTranslationCache();

        Assert.Equal(0, cache.Count);
        Assert.Equal(512, cache.Capacity);
        Assert.Equal(512, LruTranslationCache.DefaultCapacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_RejectsACapacityThatIsNotPositive(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruTranslationCache(capacity));
    }

    [Fact]
    public void Constructor_AcceptsACapacityOfOne()
    {
        var cache = new LruTranslationCache(1);
        Store(cache, "a");
        Store(cache, "b");

        Assert.Equal(1, cache.Count);
        Assert.False(Contains(cache, "a"));
        Assert.True(Contains(cache, "b"));
    }

    // ---- basic behaviour ----

    [Fact]
    public void TryGet_OnAnEmptyCache_MissesWithANullResult()
    {
        var cache = new LruTranslationCache();

        var hit = cache.TryGet(Key("a"), out var result);

        Assert.False(hit);
        Assert.Null(result);
    }

    [Fact]
    public void Store_ThenTryGet_ReturnsTheStoredResult()
    {
        var cache = new LruTranslationCache();
        var stored = Result("Hallo", translated: "Hello");
        cache.Store(Key("Hallo"), stored);

        var hit = cache.TryGet(Key("Hallo"), out var result);

        Assert.True(hit);
        Assert.Same(stored, result);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Keys_AreComparedByValue_NotByReference()
    {
        var cache = new LruTranslationCache();
        cache.Store(new TranslationCacheKey("Hallo", "de", "en", ModelV1), Result("Hallo"));

        Assert.True(cache.TryGet(new TranslationCacheKey("Hallo", "de", "en", ModelV1), out _));
    }

    [Theory]
    [InlineData("hallo", "de", "en", ModelV1)] // text case matters
    [InlineData("Hallo", "DE", "en", ModelV1)] // language codes are compared exactly
    [InlineData("Hallo", "de", "EN", ModelV1)]
    [InlineData("Hallo", "fr", "en", ModelV1)]
    [InlineData("Hallo", "de", "fr", ModelV1)]
    [InlineData("Hallo", "de", "en", ModelV2)] // a new model version is a different key
    [InlineData("Hallo ", "de", "en", ModelV1)] // no whitespace folding: callers normalise first
    public void TryGet_MissesWhenAnyPartOfTheKeyDiffers(string text, string source, string target, string model)
    {
        var cache = new LruTranslationCache();
        cache.Store(Key("Hallo"), Result("Hallo"));

        Assert.False(cache.TryGet(new TranslationCacheKey(text, source, target, model), out _));
    }

    [Fact]
    public void Store_KeepsTheSameTextOnDifferentModelVersionsApart()
    {
        var cache = new LruTranslationCache();
        cache.Store(Key("Hallo", ModelV1), Result("Hallo", ModelV1, "old"));
        cache.Store(Key("Hallo", ModelV2), Result("Hallo", ModelV2, "new"));

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(Key("Hallo", ModelV1), out var old));
        Assert.True(cache.TryGet(Key("Hallo", ModelV2), out var fresh));
        Assert.Equal("old", old!.TranslatedText);
        Assert.Equal("new", fresh!.TranslatedText);
    }

    [Fact]
    public void NullArguments_AreRejected()
    {
        var cache = new LruTranslationCache();

        Assert.Throws<ArgumentNullException>(() => cache.TryGet(null!, out _));
        Assert.Throws<ArgumentNullException>(() => cache.Store(null!, Result("a")));
        Assert.Throws<ArgumentNullException>(() => cache.Store(Key("a"), null!));
        Assert.Throws<ArgumentNullException>(() => cache.InvalidateModel(null!));
    }

    // ---- LRU order ----

    [Fact]
    public void Store_BeyondCapacity_EvictsTheLeastRecentlyStored()
    {
        var cache = new LruTranslationCache(3);
        foreach (var text in new[] { "a", "b", "c", "d" })
        {
            Store(cache, text);
        }

        Assert.Equal(3, cache.Count);
        Assert.False(Contains(cache, "a"));
        Assert.True(Contains(cache, "b"));
        Assert.True(Contains(cache, "c"));
        Assert.True(Contains(cache, "d"));
    }

    [Fact]
    public void EvictionOrder_FollowsInsertionOrder_WhenNothingIsRead()
    {
        var cache = new LruTranslationCache(3);
        foreach (var text in new[] { "a", "b", "c" })
        {
            Store(cache, text);
        }

        Store(cache, "d");
        Assert.False(Contains(cache, "a"));
        Store(cache, "e");
        Assert.False(Contains(cache, "b"));
        Store(cache, "f");
        Assert.False(Contains(cache, "c"));

        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public void TryGet_RefreshesRecency_SoTheReadEntrySurvives()
    {
        var cache = new LruTranslationCache(3);
        foreach (var text in new[] { "a", "b", "c" })
        {
            Store(cache, text);
        }

        Assert.True(cache.TryGet(Key("a"), out _)); // "a" is now the most recent; "b" is the oldest
        Store(cache, "d");

        Assert.True(Contains(cache, "a"));
        Assert.False(Contains(cache, "b"));
        Assert.True(Contains(cache, "c"));
        Assert.True(Contains(cache, "d"));
    }

    [Fact]
    public void TryGet_OfAMissingKey_DoesNotChangeRecency()
    {
        var cache = new LruTranslationCache(2);
        Store(cache, "a");
        Store(cache, "b");

        Assert.False(cache.TryGet(Key("zzz"), out _));
        Store(cache, "c");

        Assert.False(Contains(cache, "a"));
        Assert.True(Contains(cache, "b"));
    }

    [Fact]
    public void Store_OfAnExistingKey_ReplacesTheValueAndRefreshesRecency()
    {
        var cache = new LruTranslationCache(3);
        foreach (var text in new[] { "a", "b", "c" })
        {
            Store(cache, text);
        }

        var replacement = Result("a", translated: "replaced");
        cache.Store(Key("a"), replacement); // "a" is now the most recent; "b" is the oldest
        Assert.Equal(3, cache.Count);
        Store(cache, "d");

        Assert.False(Contains(cache, "b"));
        Assert.True(cache.TryGet(Key("a"), out var result));
        Assert.Same(replacement, result);
    }

    [Fact]
    public void Store_OfTheSameKeyRepeatedly_NeverGrowsTheCache()
    {
        var cache = new LruTranslationCache(2);
        for (var i = 0; i < 100; i++)
        {
            cache.Store(Key("a"), Result("a", translated: i.ToString(CultureInfo.InvariantCulture)));
        }

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(Key("a"), out var result));
        Assert.Equal("99", result!.TranslatedText);
    }

    [Fact]
    public void Count_NeverExceedsTheCapacity()
    {
        var cache = new LruTranslationCache();
        for (var i = 0; i < 5_000; i++)
        {
            Store(cache, "text " + i.ToString(CultureInfo.InvariantCulture));
            Assert.True(cache.Count <= LruTranslationCache.DefaultCapacity);
        }

        Assert.Equal(LruTranslationCache.DefaultCapacity, cache.Count);
        // The newest entries survive; the oldest are gone.
        Assert.True(Contains(cache, "text 4999"));
        Assert.False(Contains(cache, "text 0"));
    }

    // ---- invalidation and clearing ----

    [Fact]
    public void InvalidateModel_RemovesOnlyTheEntriesOfThatModelVersion()
    {
        var cache = new LruTranslationCache();
        Store(cache, "a", ModelV1);
        Store(cache, "b", ModelV1);
        Store(cache, "a", ModelV2);

        cache.InvalidateModel(ModelV1);

        Assert.Equal(1, cache.Count);
        Assert.False(Contains(cache, "a", ModelV1));
        Assert.False(Contains(cache, "b", ModelV1));
        Assert.True(Contains(cache, "a", ModelV2));
    }

    [Fact]
    public void InvalidateModel_ComparesTheVersionExactly()
    {
        var cache = new LruTranslationCache();
        const string pivot = "argos:de-en@1.9+en-fr@1.9";
        Store(cache, "a", pivot);
        Store(cache, "b", ModelV1);

        cache.InvalidateModel(ModelV1); // a prefix of the pivot version, but a different model identity
        cache.InvalidateModel(ModelV1.ToUpperInvariant());

        Assert.Equal(1, cache.Count);
        Assert.True(Contains(cache, "a", pivot));
    }

    [Fact]
    public void InvalidateModel_OfAnUnknownVersion_ChangesNothing()
    {
        var cache = new LruTranslationCache();
        Store(cache, "a");

        cache.InvalidateModel("argos:xx-yy@9");

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void InvalidateModel_KeepsTheRecencyOrderOfTheRemainingEntries()
    {
        var cache = new LruTranslationCache(3);
        Store(cache, "a", ModelV1);
        Store(cache, "b", ModelV2);
        Store(cache, "c", ModelV1);

        cache.InvalidateModel(ModelV1); // only "b" remains
        Store(cache, "d");
        Store(cache, "e");
        Assert.Equal(3, cache.Count);
        Assert.True(Contains(cache, "b", ModelV2)); // still there: nothing has been evicted yet
        Store(cache, "f"); // now the oldest ("d", after "b" was just refreshed) goes

        Assert.False(Contains(cache, "d"));
        Assert.True(Contains(cache, "b", ModelV2));
        Assert.True(Contains(cache, "e"));
        Assert.True(Contains(cache, "f"));
    }

    [Fact]
    public void InvalidateModel_RemovesEveryEntryEvenWhenAllMatch()
    {
        var cache = new LruTranslationCache(10);
        for (var i = 0; i < 10; i++)
        {
            Store(cache, "t" + i.ToString(CultureInfo.InvariantCulture));
        }

        cache.InvalidateModel(ModelV1);

        Assert.Equal(0, cache.Count);
        Store(cache, "again");
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Clear_RemovesEverything_AndTheCacheStaysUsable()
    {
        var cache = new LruTranslationCache(3);
        foreach (var text in new[] { "a", "b", "c" })
        {
            Store(cache, text);
        }

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(Contains(cache, "a"));
        foreach (var text in new[] { "x", "y", "z", "w" })
        {
            Store(cache, text);
        }

        Assert.Equal(3, cache.Count);
        Assert.False(Contains(cache, "x"));
    }

    [Fact]
    public void Clear_OnAnEmptyCache_IsHarmless()
    {
        var cache = new LruTranslationCache();

        cache.Clear();

        Assert.Equal(0, cache.Count);
    }

    // ---- concurrency ----

    [Fact]
    public async Task ConcurrentAccess_KeepsTheCacheConsistentAndBounded()
    {
        const int capacity = 64;
        const int workers = 8;
        const int iterations = 20_000;
        const int keySpace = 300;
        var cache = new LruTranslationCache(capacity);
        var models = new[] { ModelV1, ModelV2 };
        using var start = new ManualResetEventSlim(false);
        var overCapacity = 0;
        var corrupted = 0;

        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
        {
            var random = new Random(1234 + worker);
            start.Wait();
            for (var i = 0; i < iterations; i++)
            {
                var text = "text " + random.Next(keySpace).ToString(CultureInfo.InvariantCulture);
                var model = models[random.Next(models.Length)];
                var op = random.Next(1000);
                if (op < 550)
                {
                    cache.Store(Key(text, model), Result(text, model));
                }
                else if (op < 980)
                {
                    if (cache.TryGet(Key(text, model), out var result) && (result is null || result.OriginalText != text || result.ModelIdentifier != model))
                    {
                        Interlocked.Increment(ref corrupted);
                    }
                }
                else if (op < 998)
                {
                    cache.InvalidateModel(model);
                }
                else
                {
                    cache.Clear();
                }

                if (cache.Count > capacity)
                {
                    Interlocked.Increment(ref overCapacity);
                }
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(0, overCapacity);
        Assert.Equal(0, corrupted);
        Assert.True(cache.Count <= capacity);

        // The map and the recency list agree: every entry that is counted can be found, and nothing else can.
        var found = 0;
        for (var i = 0; i < keySpace; i++)
        {
            foreach (var model in models)
            {
                if (Contains(cache, "text " + i.ToString(CultureInfo.InvariantCulture), model))
                {
                    found++;
                }
            }
        }

        Assert.Equal(cache.Count, found);
    }

    [Fact]
    public async Task ConcurrentStores_OfDistinctKeys_NeverExceedTheCapacity()
    {
        var cache = new LruTranslationCache(100);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                Store(cache, $"worker {worker} item {i}");
            }
        })));

        Assert.Equal(100, cache.Count);
    }
}
