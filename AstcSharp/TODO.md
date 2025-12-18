Conversion plan: google ASTC reference C++ -> idiomatic C#

Goal
- Produce an idiomatic, correct, and feature‑equivalent C# implementation of the google reference ASTC decoder found in `AstcSharp.Reference/astc-codec/src`.
- Work file-by-file: each step converts one reference source file and adds tests that assert parity with the reference behavior.
- Keep tests passing after each step. Use the reference decoder's tests and testdata as the basis for the C# tests.
- Prefer safe, idiomatic C# APIs (Span<T>, ReadOnlySpan<T>, structs for small value types, enums, System.Buffers.Binary for endianness). Use `unsafe` only if required for performance or bit-exact behavior.

Prerequisites (initial work before file-by-file conversion)
Add test helper utilities to the test project: a port of any `image_utils.h` helpers needed to compare decoded images and utilities to run the reference binary or to read expected output. Store reference testdata files in `AstcSharp.Reference/astc-codec/src/decoder/testdata` and reference test expectations under `AstcSharp.Tests/TestData` if needed.

Conversion steps (one source file per step)
- For each step:
  - Implement a C# file under `AstcSharp/Reference` (or another appropriate namespace/project) that corresponds to the reference source.
  - Port the public API and internal behavior faithfully; maintain naming and algorithm semantics while making idiomatic changes.
  - Add a unit test in `AstcSharp.Tests` that mirrors the corresponding `decoder/test/<something>_test.cc`. Use the ASTC testdata provided to assert bit-exact outputs where the reference tests do so.
  - Build and run tests; iterate until passing.

Ordering rationale: start with foundational types and low-level utilities, then move to codecs and block/partition logic, then to top-level file/CLI handling. This keeps dependencies minimal during early stages.

Steps

1) `types.h`
- Objective: Port core typedefs, small enums, and basic constants used across the decoder (e.g., fixed-width integer aliases, useful constants).
- Deliverable: `AstcSharp/Reference/Types.cs` with enums, constants, and small structs.
- Tests: None complex; add a tiny compile test that references the constants and types.

2) `base/bit_stream.h`
- Objective: Implement a bit reader/writer equivalent used by integer sequence codec and other low-level components.
- Deliverable: `AstcSharp/Reference/BitStream.cs` exposing a safe API (e.g., `BitReader` with `ReadBits(int)` and `ReadBool()`), using `ReadOnlySpan<byte>` internally and `System.Buffers.Binary` for endianness.
- Tests: Port `base/test/bit_stream_test.cpp` to `AstcSharp.Tests/BitStreamTests.cs`. Assert exact bit sequences and edge cases.

3) `base/bottom_n.h`
- Objective: Port utilities that select the bottom N elements (used by some codec decisions).
- Deliverable: `AstcSharp/Reference/BottomN.cs` providing an idiomatic implementation using `Span<T>` and minimal allocations.
- Tests: Port `base/test/bottom_n_test.cpp` to `AstcSharp.Tests/BottomNTests.cs`.

4) `decoder/integer_sequence_codec.h` + `decoder/integer_sequence_codec.cc`
- Objective: Implement integer sequence encoding/decoding logic that relies on bit streams.
- Deliverable: `AstcSharp/Reference/IntegerSequenceCodec.cs` using `BitReader` and `BitWriter` where needed.
- Tests: Port `decoder/test/integer_sequence_codec_test.cc`.

5) `decoder/quantization.h` + `decoder/quantization.cc`
- Objective: Port quantization tables and conversion routines used to reconstruct color endpoints from compressed representation.
- Deliverable: `AstcSharp/Reference/Quantization.cs` containing quantization tables and helper methods.
- Tests: Port `decoder/test/quantization_test.cc`.

6) `decoder/endpoint_codec.h` + `decoder/endpoint_codec.cc`
- Objective: Port endpoint decode/encode logic (major color endpoint handling). Ensure floating point behavior matches reference where required.
- Deliverable: `AstcSharp/Reference/EndpointCodec.cs` with deterministic behavior and documented numeric choices.
- Tests: Port `decoder/test/endpoint_codec_test.cc` and assert endpoint results match reference.

7) `decoder/partition.h` + `decoder/partition.cc`
- Objective: Port partitioning code that computes pixel partitions inside a block, which many later components rely on.
- Deliverable: `AstcSharp/Reference/Partition.cs` that exposes partition masks and utilities.
- Tests: Port `decoder/test/partition_test.cc`. Use the reference partition test vectors.

8) `decoder/footprint.h` + `decoder/footprint.cc`
- Objective: Implement footprint computations (used to calculate which texels are covered) and any small helper polygons/structs.
- Deliverable: `AstcSharp/Reference/Footprint.cs` with equivalent APIs.
- Tests: Port `decoder/test/footprint_test.cc` and use the `testdata/footprint_*` files if needed.

9) `decoder/weight_infill.h` + `decoder/weight_infill.cc`
- Objective: Port weight infill algorithms used to expand sparse weight representations to per-pixel weights.
- Deliverable: `AstcSharp/Reference/WeightInfill.cs` with clear, well-documented functions.
- Tests: Port `decoder/test/weight_infill_test.cc`.

10) `decoder/intermediate_astc_block.h` + `decoder/intermediate_astc_block.cc`
- Objective: Port the intermediate representation of an ASTC block (the decoded representation prior to final pixel reconstruction).
- Deliverable: `AstcSharp/Reference/IntermediateAstcBlock.cs` as a struct/class representing the decoded fields.
- Tests: Port `decoder/test/intermediate_astc_block_test.cc`.

11) `decoder/logical_astc_block.h` + `decoder/logical_astc_block.cc`
- Objective: Port logical block-level operations (mapping to texel coordinate space, interpolation helpers).
- Deliverable: `AstcSharp/Reference/LogicalAstcBlock.cs`.
- Tests: Port `decoder/test/logical_astc_block_test.cc`.

12) `decoder/physical_astc_block.h` + `decoder/physical_astc_block.cc`
- Objective: Implement physical block handling (the low-level block representation read from file) and conversion to logical/intermediate form.
- Deliverable: `AstcSharp/Reference/PhysicalAstcBlock.cs`.
- Tests: Port `decoder/test/physical_astc_block_test.cc` and relevant testdata inputs.

13) `decoder/codec.h` + `decoder/codec.cc`
- Objective: Port the main codec logic that takes a compressed block, decodes endpoints and weights, and constructs final pixel values.
- Deliverable: `AstcSharp/Reference/Codec.cs` with a public `DecodeBlock(...)` method producing an array of pixels or an `Image` object.
- Tests: Port `decoder/test/codec_test.cc` and use testdata `rgb_*.astc` to assert pixel equality with reference outputs where test expectations are available.

14) `decoder/astc_file.h` + `decoder/astc_file.cc`
- Objective: Port ASTC file reader/inspector: parse headers, iterate blocks, decode the full image using `Codec`.
- Deliverable: `AstcSharp/Reference/AstcFile.cs` and supporting types for image metadata and header parsing.
- Tests: Port `decoder/test/astc_fuzzer.cc` and/or add functional tests that decode the `testdata/*.astc` files and compare properties (dimensions, format) and pixel data where available.

15) Tools and CLI parity (`decoder/tools/astc_inspector_cli.cc`)
- Objective: Provide a parity CLI or API entry points so that higher-level consumers can inspect or decode ASTC files like the reference tool.
- Deliverable: `AstcSharp/Tools/AstcInspector.cs` or a small console app project that uses `AstcFile` and `Codec`.
- Tests: Add integration tests to `AstcSharp.Tests` that run the CLI functionality programmatically and verify outputs for small testdata files.

16) Integration & cross-file cleanup
- Objective: Run a full integration test suite to ensure all pieces fit together and produce identical outputs to the reference implementation for the provided testdata.
- Deliverable: polishing of APIs, shared helper utilities, and performance-sensitive parts (consider `Span<T>`, pooling, or `unsafe` blocks if necessary).
- Tests: Full decoding of each `testdata/*.astc` and pixel-by-pixel comparison where the reference outputs are known. If exact pixel outputs differ due to floating point nondeterminism, document tolerances and add regression tests.

17) Finalization: documentation and performance
- Objective: Document public APIs, add XML docs, and measure performance. Consider micro-optimizations if tests show hotspots.
- Deliverable: update README, add usage examples, include notes about compatibility, and finalize unit tests.
- Tests: Add performance benchmarks if desired (Benchmarks.NET) but keep them separate from unit tests.

Notes and best practices
- Keep each C# source file small and focused; prefer small `internal` types for helpers.
- Prefer immutable or readonly structs for small value types and mark arrays as `ReadOnlySpan<T>` where appropriate.
- For bit-exact behavior follow the C++ logic closely; if you suspect numeric differences due to floating point, replicate the C++ ordering and casts exactly (e.g., use `MathF` for single-precision math when needed).
- Use the reference test binaries and data as oracles: where possible compare output of the reference C++ decoder against the C# decoder on the same testdata and assert equality.
- Add unit tests gradually: each step should add a test that would fail if the port were incorrect.

Completion criteria
- All tests in `AstcSharp.Tests` pass.
- For all provided testdata, the C# decoder produces outputs matching the reference or documented tolerances.
- The codebase follows idiomatic C# conventions and targets .NET 9.

Appendix: mapping of reference tests to C# test files
- `base/test/bit_stream_test.cpp` -> `AstcSharp.Tests/BitStreamTests.cs`
- `base/test/bottom_n_test.cpp` -> `AstcSharp.Tests/BottomNTests.cs`
- `decoder/test/integer_sequence_codec_test.cc` -> `AstcSharp.Tests/IntegerSequenceCodecTests.cs`
- `decoder/test/quantization_test.cc` -> `AstcSharp.Tests/QuantizationTests.cs`
- `decoder/test/endpoint_codec_test.cc` -> `AstcSharp.Tests/EndpointCodecTests.cs`
- `decoder/test/partition_test.cc` -> `AstcSharp.Tests/PartitionTests.cs`
- `decoder/test/footprint_test.cc` -> `AstcSharp.Tests/FootprintTests.cs`
- `decoder/test/weight_infill_test.cc` -> `AstcSharp.Tests/WeightInfillTests.cs`
- `decoder/test/intermediate_astc_block_test.cc` -> `AstcSharp.Tests/IntermediateAstcBlockTests.cs`
- `decoder/test/logical_astc_block_test.cc` -> `AstcSharp.Tests/LogicalAstcBlockTests.cs`
- `decoder/test/physical_astc_block_test.cc` -> `AstcSharp.Tests/PhysicalAstcBlockTests.cs`
- `decoder/test/codec_test.cc` -> `AstcSharp.Tests/CodecTests.cs`