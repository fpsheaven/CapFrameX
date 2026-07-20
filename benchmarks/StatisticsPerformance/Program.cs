using CapFrameX.Data.Session.Classes;
using CapFrameX.Statistics.NetStandard;
using CapFrameX.Statistics.NetStandard.Contracts;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CapFrameX.Benchmarks.StatisticsPerformance
{
    internal static class Program
    {
        private const int Seed = 123456789;
        private const int WarmupCount = 3;
        private const int MetricIterations = 7;
        private const int DistributionWarmups = 1;
        private const int DistributionIterations = 3;
        private static double _sink;

        private static readonly EMetric[] Metrics =
        {
            EMetric.Max, EMetric.P99, EMetric.P95, EMetric.Average, EMetric.Median,
            EMetric.P5, EMetric.P1, EMetric.P0dot2, EMetric.P0dot1,
            EMetric.OnePercentLowAverage, EMetric.ZerodotTwoPercentLowAverage,
            EMetric.ZerodotOnePercentLowAverage, EMetric.OnePercentLowIntegral,
            EMetric.ZerodotTwoPercentLowIntegral, EMetric.ZerodotOnePercentLowIntegral,
            EMetric.Min
        };

        private static int Main(string[] args)
        {
            string outputPath = GetOutputPath(args);
            var options = new Options
            {
                FpsValuesRoundingDigits = 4,
                IntervalAverageWindowTime = 500,
                MovingAverageWindowSize = 20
            };
            var provider = new FrametimeStatisticProvider(options);
            var results = new List<BenchmarkResult>();

            try
            {
                Console.WriteLine("Validating numerical parity...");
                foreach (int count in new[] { 10000, 100000, 1000000 })
                {
                    var samples = CreateTimings(count, Seed + count);
                    AssertMetricParity(provider, samples);
                    RunMetricCase(provider, samples, "fps", results);
                    RunMetricCase(provider, samples, "frametime", results);
                }

                AssertMutationInvalidation(provider);
                RunDistributionCases(options, results);

                WriteCsv(outputPath, results);
                PrintSummary(results, outputPath);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void RunMetricCase(FrametimeStatisticProvider provider,
            IList<double> samples, string unit, IList<BenchmarkResult> results)
        {
            string caseName = unit + "_" + samples.Count.ToString(CultureInfo.InvariantCulture);
            Func<IList<double>, double> legacy = unit == "fps"
                ? new Func<IList<double>, double>(sequence => SumLegacyFps(provider, sequence))
                : sequence => SumLegacyFrametimes(provider, sequence);
            Func<IList<double>, double> batch = unit == "fps"
                ? new Func<IList<double>, double>(sequence => SumBatchFps(provider, sequence))
                : sequence => SumBatchFrametimes(provider, sequence);

            Warmup(() => _sink = legacy(samples), WarmupCount);
            results.Add(Measure("metrics", caseName, "legacy_repeated", samples.Count,
                MetricIterations, () => () => _sink = legacy(samples)));

            Warmup(() => _sink = batch(samples.ToArray()), WarmupCount);
            results.Add(Measure("metrics", caseName, "batch_cold", samples.Count,
                MetricIterations, () =>
                {
                    var coldSamples = samples.ToArray();
                    return () => _sink = batch(coldSamples);
                }));

            var warmSamples = samples.ToArray();
            _sink = batch(warmSamples);
            Warmup(() => _sink = batch(warmSamples), WarmupCount);
            results.Add(Measure("metrics", caseName, "batch_warm", samples.Count,
                MetricIterations, () => () => _sink = batch(warmSamples)));
        }

        private static void RunDistributionCases(Options options,
            IList<BenchmarkResult> results)
        {
            var cases = new[]
            {
                new DistributionCase("typical_frametime", CreateTimings(200000, Seed + 7), false),
                new DistributionCase("typical_display", CreateTimings(200000, Seed + 11), true),
                new DistributionCase("hitch_display", AddHitch(CreateTimings(20000, Seed + 13), 1000), true),
                new DistributionCase("pathological_frametime", AddHitch(CreateTimings(2000, Seed + 17), 10000), false)
            };

            foreach (DistributionCase benchmarkCase in cases)
            {
                Session session = CreateSession(benchmarkCase.Samples, benchmarkCase.UseDisplayTimes);
                IList<Point> sparse = GetSparseDistribution(session, benchmarkCase, options);
                IList<Point> legacy = GetLegacyDenseDistribution(benchmarkCase.Samples);
                AssertDistributionParity(legacy, sparse, benchmarkCase.Name);

                Warmup(() => _sink = SumDistribution(GetSparseDistribution(session, benchmarkCase, options)),
                    DistributionWarmups);
                results.Add(Measure("distribution", benchmarkCase.Name, "sparse",
                    benchmarkCase.Samples.Count, DistributionIterations,
                    () => () => _sink = SumDistribution(
                        GetSparseDistribution(session, benchmarkCase, options))));

                Warmup(() => _sink = SumDistribution(GetLegacyDenseDistribution(benchmarkCase.Samples)),
                    DistributionWarmups);
                results.Add(Measure("distribution", benchmarkCase.Name, "legacy_dense",
                    benchmarkCase.Samples.Count, DistributionIterations,
                    () => () => _sink = SumDistribution(
                        GetLegacyDenseDistribution(benchmarkCase.Samples))));
            }
        }

        private static BenchmarkResult Measure(string category, string caseName,
            string implementation, int sampleCount, int iterations, Func<Action> actionFactory)
        {
            var elapsed = new List<double>(iterations);
            var managedDeltas = new List<long>(iterations);
            var privateDeltas = new List<long>(iterations);
            long gen0 = 0;
            long gen1 = 0;
            long gen2 = 0;

            for (int i = 0; i < iterations; i++)
            {
                Action action = actionFactory();
                ForceGc();
                long managedBefore = GC.GetTotalMemory(false);
                long privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
                int gen0Before = GC.CollectionCount(0);
                int gen1Before = GC.CollectionCount(1);
                int gen2Before = GC.CollectionCount(2);

                var stopwatch = Stopwatch.StartNew();
                action();
                stopwatch.Stop();

                elapsed.Add(stopwatch.Elapsed.TotalMilliseconds);
                managedDeltas.Add(GC.GetTotalMemory(false) - managedBefore);
                privateDeltas.Add(Process.GetCurrentProcess().PrivateMemorySize64 - privateBefore);
                gen0 += GC.CollectionCount(0) - gen0Before;
                gen1 += GC.CollectionCount(1) - gen1Before;
                gen2 += GC.CollectionCount(2) - gen2Before;
            }

            elapsed.Sort();
            managedDeltas.Sort();
            privateDeltas.Sort();
            return new BenchmarkResult
            {
                Category = category,
                CaseName = caseName,
                Implementation = implementation,
                SampleCount = sampleCount,
                Iterations = iterations,
                MedianMilliseconds = Percentile(elapsed, 0.5),
                P95Milliseconds = Percentile(elapsed, 0.95),
                MinMilliseconds = elapsed.First(),
                MaxMilliseconds = elapsed.Last(),
                MedianManagedDeltaBytes = managedDeltas[managedDeltas.Count / 2],
                MedianPrivateDeltaBytes = privateDeltas[privateDeltas.Count / 2],
                Gen0Collections = gen0,
                Gen1Collections = gen1,
                Gen2Collections = gen2
            };
        }

        private static void AssertMetricParity(FrametimeStatisticProvider provider,
            IList<double> samples)
        {
            IDictionary<EMetric, double> fpsBatch = provider.GetFpsMetricValues(samples, Metrics);
            IDictionary<EMetric, double> frametimeBatch =
                provider.GetFrametimeMetricValues(samples, Metrics);

            foreach (EMetric metric in Metrics)
            {
                AssertEqual(provider.GetFpsMetricValue(samples, metric), fpsBatch[metric],
                    "FPS " + metric);
                AssertEqual(provider.GetFrametimeMetricValue(samples, metric),
                    frametimeBatch[metric], "Frametime " + metric);
            }
        }

        private static void AssertMutationInvalidation(FrametimeStatisticProvider provider)
        {
            var samples = CreateTimings(100000, Seed + 23);
            provider.GetFrametimeMetricValues(samples, Metrics);
            samples[samples.Count / 2] = 100000;
            IDictionary<EMetric, double> cached = provider.GetFrametimeMetricValues(samples, Metrics);
            IDictionary<EMetric, double> fresh =
                provider.GetFrametimeMetricValues(samples.ToArray(), Metrics);

            foreach (EMetric metric in Metrics)
                AssertEqual(fresh[metric], cached[metric], "Mutation " + metric);
        }

        private static double SumLegacyFps(FrametimeStatisticProvider provider,
            IList<double> samples)
        {
            double sum = 0;
            foreach (EMetric metric in Metrics)
                sum += provider.GetFpsMetricValue(samples, metric);
            return sum;
        }

        private static double SumLegacyFrametimes(FrametimeStatisticProvider provider,
            IList<double> samples)
        {
            double sum = 0;
            foreach (EMetric metric in Metrics)
                sum += provider.GetFrametimeMetricValue(samples, metric);
            return sum;
        }

        private static double SumBatchFps(FrametimeStatisticProvider provider,
            IList<double> samples)
        {
            return provider.GetFpsMetricValues(samples, Metrics).Values.Sum();
        }

        private static double SumBatchFrametimes(FrametimeStatisticProvider provider,
            IList<double> samples)
        {
            return provider.GetFrametimeMetricValues(samples, Metrics).Values.Sum();
        }

        private static IList<Point> GetSparseDistribution(Session session,
            DistributionCase benchmarkCase, Options options)
        {
            double endTime = benchmarkCase.Samples.Count - 1;
            return benchmarkCase.UseDisplayTimes
                ? session.GetDisplayTimeDistributionPoints(0, endTime, options)
                : session.GetFrametimeDistributionPoints(0, endTime, options);
        }

        private static IList<Point> GetLegacyDenseDistribution(IList<double> samples)
        {
            const double increment = 0.1;
            double maxValue = samples.Max();
            var bins = new List<Tuple<double, double>>();
            for (double start = 0; start < maxValue; start += increment)
            {
                double end = Math.Round(start + increment, 10);
                bins.Add(Tuple.Create(start, end));
            }
            if (bins.Count == 0 || bins[bins.Count - 1].Item2 < maxValue)
            {
                double start = bins.Count > 0 ? bins[bins.Count - 1].Item2 : 0;
                bins.Add(Tuple.Create(start, Math.Round(start + increment, 10)));
            }

            double total = samples.Sum();
            var result = new List<Point>();
            for (int binIndex = 0; binIndex < bins.Count; binIndex++)
            {
                double start = bins[binIndex].Item1;
                double end = bins[binIndex].Item2;
                bool isLast = binIndex == bins.Count - 1;
                double sum = 0;
                for (int i = 0; i < samples.Count; i++)
                {
                    double value = samples[i];
                    if (isLast ? value >= start && value <= end : value >= start && value < end)
                        sum += value;
                }
                if (sum > 0)
                    result.Add(new Point(end, sum / total * 100));
            }
            return result;
        }

        private static void AssertDistributionParity(IList<Point> expected,
            IList<Point> actual, string caseName)
        {
            if (expected.Count != actual.Count)
                throw new InvalidOperationException(caseName + " distribution count differs.");
            for (int i = 0; i < expected.Count; i++)
            {
                double xTolerance = Math.Max(1, Math.Abs(expected[i].X)) * 1E-9;
                if (Math.Abs(expected[i].X - actual[i].X) > xTolerance
                    || Math.Abs(expected[i].Y - actual[i].Y) > 1E-9)
                {
                    throw new InvalidOperationException(caseName
                        + " distribution differs at bin " + i.ToString(CultureInfo.InvariantCulture)
                        + ": expected (" + expected[i].X.ToString("R", CultureInfo.InvariantCulture)
                        + ", " + expected[i].Y.ToString("R", CultureInfo.InvariantCulture)
                        + "), actual (" + actual[i].X.ToString("R", CultureInfo.InvariantCulture)
                        + ", " + actual[i].Y.ToString("R", CultureInfo.InvariantCulture) + ").");
                }
            }
        }

        private static void AssertEqual(double expected, double actual, string name)
        {
            if (double.IsNaN(expected) && double.IsNaN(actual))
                return;
            if (expected != actual)
                throw new InvalidOperationException(name + " parity failed: "
                    + expected.ToString("R", CultureInfo.InvariantCulture) + " != "
                    + actual.ToString("R", CultureInfo.InvariantCulture));
        }

        private static List<double> CreateTimings(int count, int seed)
        {
            var random = new Random(seed);
            var samples = new List<double>(count);
            for (int i = 0; i < count; i++)
            {
                double value = 5 + random.NextDouble() * 28;
                if (i > 0 && i % 9973 == 0)
                    value += 40;
                samples.Add(value);
            }
            return samples;
        }

        private static List<double> AddHitch(List<double> samples, double hitch)
        {
            samples[samples.Count - 1] = hitch;
            return samples;
        }

        private static Session CreateSession(IList<double> timings, bool useDisplayTimes)
        {
            int count = timings.Count;
            var captureData = new SessionCaptureData(count);
            for (int i = 0; i < count; i++)
                captureData.TimeInSeconds[i] = i;

            double[] primary = timings.ToArray();
            double[] alternate = Enumerable.Repeat(16.67, count).ToArray();
            captureData.MsBetweenPresents = useDisplayTimes ? alternate : primary;
            captureData.MsBetweenDisplayChange = useDisplayTimes ? primary : alternate;

            var session = new Session();
            session.Runs.Add(new SessionRun { CaptureData = captureData });
            return session;
        }

        private static double SumDistribution(IList<Point> distribution)
        {
            double sum = 0;
            for (int i = 0; i < distribution.Count; i++)
                sum += distribution[i].X + distribution[i].Y;
            return sum;
        }

        private static void Warmup(Action action, int count)
        {
            for (int i = 0; i < count; i++)
                action();
        }

        private static void ForceGc()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static double Percentile(IList<double> sortedValues, double percentile)
        {
            int index = Math.Max(0,
                (int)Math.Ceiling(sortedValues.Count * percentile) - 1);
            return sortedValues[index];
        }

        private static string GetOutputPath(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--output")
                    return Path.GetFullPath(args[i + 1]);
            }
            return Path.GetFullPath(Path.Combine("benchmarks", "StatisticsPerformance",
                "results", "current.csv"));
        }

        private static void WriteCsv(string outputPath, IEnumerable<BenchmarkResult> results)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            string runtime = Environment.Version.ToString();
            string os = Environment.OSVersion.ToString();
            string cpu = GetCpuName();
            using (var writer = new StreamWriter(outputPath, false))
            {
                writer.WriteLine("category,case,implementation,samples,iterations,median_ms,p95_ms,min_ms,max_ms,median_managed_delta_bytes,median_private_delta_bytes,gen0,gen1,gen2,seed,warmups,runtime,os,cpu");
                foreach (BenchmarkResult result in results)
                {
                    writer.WriteLine(string.Join(",", new[]
                    {
                        Csv(result.Category), Csv(result.CaseName), Csv(result.Implementation),
                        result.SampleCount.ToString(CultureInfo.InvariantCulture),
                        result.Iterations.ToString(CultureInfo.InvariantCulture),
                        result.MedianMilliseconds.ToString("F6", CultureInfo.InvariantCulture),
                        result.P95Milliseconds.ToString("F6", CultureInfo.InvariantCulture),
                        result.MinMilliseconds.ToString("F6", CultureInfo.InvariantCulture),
                        result.MaxMilliseconds.ToString("F6", CultureInfo.InvariantCulture),
                        result.MedianManagedDeltaBytes.ToString(CultureInfo.InvariantCulture),
                        result.MedianPrivateDeltaBytes.ToString(CultureInfo.InvariantCulture),
                        result.Gen0Collections.ToString(CultureInfo.InvariantCulture),
                        result.Gen1Collections.ToString(CultureInfo.InvariantCulture),
                        result.Gen2Collections.ToString(CultureInfo.InvariantCulture),
                        Seed.ToString(CultureInfo.InvariantCulture),
                        (result.Category == "metrics" ? WarmupCount : DistributionWarmups)
                            .ToString(CultureInfo.InvariantCulture),
                        Csv(runtime), Csv(os), Csv(cpu)
                    }));
                }
            }
        }

        private static void PrintSummary(IEnumerable<BenchmarkResult> results, string outputPath)
        {
            foreach (BenchmarkResult result in results)
            {
                Console.WriteLine("{0,-13} {1,-26} {2,-16} median={3,10:F3} ms p95={4,10:F3} ms managed={5,12} B",
                    result.Category, result.CaseName, result.Implementation,
                    result.MedianMilliseconds, result.P95Milliseconds,
                    result.MedianManagedDeltaBytes);
            }
            Console.WriteLine("Raw CSV: " + outputPath);
            Console.WriteLine("Sink: " + _sink.ToString("R", CultureInfo.InvariantCulture));
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private static string GetCpuName()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    return Convert.ToString(key?.GetValue("ProcessorNameString"),
                        CultureInfo.InvariantCulture)?.Trim() ?? "unknown";
                }
            }
            catch
            {
                return "unknown";
            }
        }

        private sealed class Options : IFrametimeStatisticProviderOptions
        {
            public int MovingAverageWindowSize { get; set; }
            public int IntervalAverageWindowTime { get; set; }
            public int FpsValuesRoundingDigits { get; set; }
        }

        private sealed class DistributionCase
        {
            public DistributionCase(string name, List<double> samples, bool useDisplayTimes)
            {
                Name = name;
                Samples = samples;
                UseDisplayTimes = useDisplayTimes;
            }

            public string Name { get; }
            public List<double> Samples { get; }
            public bool UseDisplayTimes { get; }
        }

        private sealed class BenchmarkResult
        {
            public string Category { get; set; }
            public string CaseName { get; set; }
            public string Implementation { get; set; }
            public int SampleCount { get; set; }
            public int Iterations { get; set; }
            public double MedianMilliseconds { get; set; }
            public double P95Milliseconds { get; set; }
            public double MinMilliseconds { get; set; }
            public double MaxMilliseconds { get; set; }
            public long MedianManagedDeltaBytes { get; set; }
            public long MedianPrivateDeltaBytes { get; set; }
            public long Gen0Collections { get; set; }
            public long Gen1Collections { get; set; }
            public long Gen2Collections { get; set; }
        }
    }
}
