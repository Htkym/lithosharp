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

The values are rounded from existing results, not new measurements of 2.2.0. They exclude NuGet restore and C# compilation and do not represent total build time.
