Goal
- Produce an idiomatic, correct, and feature‑equivalent C# implementation of the google reference ASTC decoder found in `AstcSharp.Reference/astc-codec/src`.
- Prefer safe, idiomatic C# APIs (Span<T>, ReadOnlySpan<T>, structs for small value types, enums, System.Buffers.Binary for endianness). Use `unsafe` only if required for performance or bit-exact behavior.

Remaining
- Tidy up code to be more ideomatically C#, and not follow C++ conventions
- Improve efficiency, reduce buffer copies
- More tests?
- Measure performance and add BenchmarkDotNet project
- Document public APIs, add XML docs. Update README, add usage examples, include notes about compatibility
- Build pipeline and Github actions
- Publish package
