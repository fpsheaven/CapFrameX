# Statistics performance benchmark

This standalone `net472` console project uses only the BCL and project references. It is not part of `CapFrameX.sln` and adds no application dependency.

Build and run from the repository root:

```powershell
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
& $msbuild .\benchmarks\StatisticsPerformance\StatisticsPerformance.csproj /restore /p:Configuration=Release /p:Platform=x64
& .\benchmarks\StatisticsPerformance\bin\x64\Release\net472\StatisticsPerformance.exe --output .\benchmarks\StatisticsPerformance\results\current.csv
```

The program exits nonzero if numerical parity fails. Parity is checked before timing for every FPS and frametime metric and for every distribution bin. Cache invalidation after in-place mutation is also verified before measurements.

## Methodology

- Fixed random seed: `123456789`.
- Metrics: 10k, 100k, and 1m samples; both FPS and frametime APIs.
- Implementations: repeated legacy single-metric calls, cold batch calls using a fresh sequence identity, and warm batch calls using an already analyzed sequence.
- Metric warmups/measurements: 3 warmups and 7 measured iterations.
- Distribution cases: 200k typical frametimes, 200k typical display times, a 20k display-time capture with a 1,000 ms hitch, and a 2k frametime capture with a 10,000 ms pathological hitch.
- Distribution warmups/measurements: 1 warmup and 3 measured iterations.
- Reported elapsed values are median, p95, minimum, and maximum wall-clock milliseconds.
- Memory columns are approximate in-process deltas after a forced full GC: managed heap (`GC.GetTotalMemory`) and process private bytes. Collection-count deltas for all GC generations are also included.
- The raw CSV records the runtime, OS, CPU, sample count, seed, warmups, and iteration count on every row.

Run the Release x64 executable on an otherwise idle system. Do not compare results gathered under different power plans, runtimes, hardware, or active background loads. Memory deltas can be negative because the runtime may release or compact memory; use them as diagnostic context, not as allocation-precise measurements.

No MSTest test asserts elapsed time. Unit tests cover parity and cache invalidation only.
