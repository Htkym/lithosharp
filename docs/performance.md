# Performance

[日本語](performance.ja.md)

Performance figures are for reference. Results vary with the environment, configuration, and content.

These existing figures cover site generation for 100 Docs pages, including input loading and quality checks, without external HTTP checks.

| Operation | Reference time |
| --- | ---: |
| First generation | About 1.23 s |
| No changes | About 0.45 s |
| One page body changed | About 0.58 s |
| Layout changed | About 1.07 s |

These historical rounded values are not measurements of the 3.0 candidate. They
exclude NuGet restore and C# compilation and do not represent total build time.
For current evaluation, keep the same machine, inputs, profiles and validation
settings and measure full, warm and edited builds separately. See
[build graph](build-graph.md) and [incremental builds](incremental-builds.md).
