# Reference benchmark results

These results were produced by the BCL-only Release x64 harness on 2026-07-19. They are a reproducible reference point, not a universal performance guarantee. See [README.md](README.md) for the full methodology and [results/current.csv](results/current.csv) for raw timings, memory deltas, GC counts, and environment metadata.

Environment:

- CPU: AMD Ryzen 7 9800X3D 8-Core Processor
- Runtime: .NET Framework `4.0.30319.42000` (`net472` target)
- Seed: `123456789`
- Metric measurements: 3 warmups, 7 measured iterations
- Distribution measurements: 1 warmup, 3 measured iterations

## Metric calculation

Median wall-clock time in milliseconds:

| Case | Repeated single metrics | Cold batch | Warm batch | Cold speedup | Warm speedup |
| --- | ---: | ---: | ---: | ---: | ---: |
| FPS, 10k samples | 2.039 | 0.447 | 0.022 | 4.6x | 91.8x |
| Frametime, 10k samples | 1.764 | 0.434 | 0.022 | 4.1x | 79.5x |
| FPS, 100k samples | 25.652 | 5.375 | 0.182 | 4.8x | 141.3x |
| Frametime, 100k samples | 25.110 | 5.360 | 0.178 | 4.7x | 140.8x |
| FPS, 1m samples | 315.583 | 68.936 | 1.754 | 4.6x | 179.9x |
| Frametime, 1m samples | 272.178 | 68.520 | 2.241 | 4.0x | 121.5x |

The cold path includes a fresh sequence identity and a new sort. The warm path still validates every element bit-for-bit before reusing the sorted analysis, so in-place mutations cannot return stale metrics.

At one million samples, the median managed-heap delta fell from about 80.1 MB for repeated metric calls to 24.1 MB for a cold batch and 0.15 MB for a warm batch. These are coarse in-process deltas rather than allocation-profiler measurements.

## Frametime distribution

Median wall-clock time in milliseconds:

| Case | Dense bins | Sparse bins | Speedup |
| --- | ---: | ---: | ---: |
| Typical frametime, 200k samples | 469.522 | 11.155 | 42.1x |
| Typical display time, 200k samples | 475.358 | 11.037 | 43.1x |
| Display time, 20k samples with 1,000 ms hitch | 495.296 | 1.150 | 430.9x |
| Frametime, 2k samples with 10,000 ms hitch | 462.299 | 0.155 | 2,986.4x |

The sparse algorithm scales with the samples and occupied bins instead of scanning every sample for every possible 0.1 ms bin. That removes the severe empty-bin penalty caused by isolated long hitches.

Before timing, the harness checks every requested metric against the existing single-metric API, verifies cache invalidation after an in-place mutation, and compares every populated distribution bin. Tiny X-axis differences caused solely by accumulated floating-point increments in the dense reference use a relative `1e-9` tolerance; percentages use an absolute `1e-9` tolerance.
