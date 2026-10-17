using System.Text.Json;
using System.Net.Http;
using LithoSharp.Quality;

namespace LithoSharp.Tests;

public sealed class ExternalLinkOptionsTests
{
    [Test]
    public async Task ExternalLinkOptionsConstructorMatchesStaticDurationCorpus()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("EXTERNAL_OPTIONS_CORPUS")
            ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "ExternalLinkOptionsCases.json")));
        var results = new List<object>();
        foreach (var row in corpus.RootElement.EnumerateArray())
        {
            var slot = row.GetProperty("slot").GetInt32();
            string? path = slot == 0 ? row.GetProperty("path").GetString() : "cache.json";
            TimeSpan? duration = null;
            if (slot != 0)
            {
                var value = long.Parse(row.GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                duration = row.GetProperty("factory").GetString() switch {
                    "zero" => TimeSpan.Zero, "min" => TimeSpan.MinValue, "max" => TimeSpan.MaxValue,
                    "new" => new TimeSpan(value), "ticks" => TimeSpan.FromTicks(value),
                    "milliseconds" => TimeSpan.FromMilliseconds(value), "seconds" => TimeSpan.FromSeconds(value),
                    "minutes" => TimeSpan.FromMinutes(value), "hours" => TimeSpan.FromHours((int)value),
                    "days" => TimeSpan.FromDays((int)value), "doubleSeconds" => TimeSpan.FromSeconds((double)value),
                    "components" => TimeSpan.FromSeconds(seconds:0L, milliseconds:0L, microseconds:value),
                    "null" => null, _ => throw new InvalidOperationException("Unregistered runtime corpus factory") };
            }
            ArgumentException? failure = null;
            ExternalLinkCheckOptions? options = null;
            try { options = new ExternalLinkCheckOptions(path!, slot == 1 ? duration : null, slot == 2 ? duration : null, slot == 3 ? duration : null); }
            catch (ArgumentException exception) { failure = exception; }
            await Assert.That(failure is not null).IsEqualTo(row.GetProperty("rejected").GetBoolean());
            var parameter = new[] { "cacheFilePath", "cacheDuration", "minimumRequestInterval", "requestTimeout" }[slot];
            if (failure is not null) await Assert.That(failure.ParamName).IsEqualTo(parameter);
            if (options is not null)
            {
                using var client = new HttpClient { Timeout = options.RequestTimeout };
            }
            if (options is not null && duration is null)
            {
                await Assert.That(options.CacheDuration.Ticks).IsEqualTo(TimeSpan.FromHours(24).Ticks);
                await Assert.That(options.MinimumRequestInterval.Ticks).IsEqualTo(TimeSpan.FromMilliseconds(500).Ticks);
                await Assert.That(options.RequestTimeout.Ticks).IsEqualTo(TimeSpan.FromSeconds(10).Ticks);
            }
            results.Add(new { name = row.GetProperty("name").GetString(), slot, rejected = failure is not null, parameter,
                ticks = duration?.Ticks, exception = failure?.GetType().Name });
        }
        if (Environment.GetEnvironmentVariable("RA03_EVIDENCE_DIR") is { Length: > 0 } directory)
            File.WriteAllText(Path.Combine(directory, "external-options-runtime.json"), JsonSerializer.Serialize(new { status="PASS", results }, new JsonSerializerOptions { WriteIndented=true }));
    }
}
