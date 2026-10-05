# Native release validation

The workflow validates actual Linux/Windows x64/ARM64 processes from one clean,
exact commit. It pins action identities, SDK 10.0.302/runtime 10.0.10 and native
PowerShell 7.6.6. Official SDK archive SHA-512 identities are checked before a
fresh isolated installation; a runner-global newer runtime cannot be selected.
Tracked LF checkout bytes must equal their Git blobs before any compiler runs.
All root projects use the complete Release analyzer policy; voxel coverage,
both wrapper tests, isolated candidate consumers, rooted trimmed execution and
actual NativeAOT execution are required. No skipped or failed TRX outcome passes.

Before package/runtime test gates, separate controlled and default child processes
capture actual Region/Arena and matched managed code generation, exact output,
worker identity, settings and exits. Instrumented timings are not performance
acceptance. An instrumented worker's floor failure remains exit 3 in its evidence;
the unchanged uninstrumented test floors still determine validation. Captures are
retained and printed in job logs even when a later floor fails.

The unchanged uninstrumented Region, Arena and ArenaScoped test children also
retain their original JSON and stderr before any assertion or deserialization,
including failures and timeouts. Individual TRX output links their command
records, exact worker/runtime lengths and SHA-256, argument lists and compilation
settings. Passing validation independently reconciles all three raw records with
those passing tests, actual zero exits, ten-second deadlines and original floors.
Passing test counts alone do not stand in for retained performance measurements.

Evidence lives outside the checkout. Each child retains arguments, declared
deadline, actual exit, complete streams and original compiler inputs. Source,
packages, caches, binaries, native runner/toolchain identities and length/SHA-256
manifests are uploaded even when validation fails. Native payloads are bundled in
tar.gz before Actions upload, preserving executable modes and native cache names
that artifact ZIP storage otherwise rejects. Bootstrap failure retention records
missing native disposition/manifest honestly; it does not invent a passing gate.
Every retained file is explicitly enumerated into the PAX archive, including
hidden files and dot-directories containing isolated consumer build/cache data.
The manifest and retrieved archive must reconcile completely before acceptance.
Remote-dependent fixtures use a retained per-consumer NuGet.config: NAM maps only
to the exact local candidate, while tooling/runtime prerequisites map to the
official HTTPS feed. Inherited feeds are cleared and no restore-source URL is
passed through filesystem normalization.
Native labels, publishing
success and ordinary JIT builds are not substitutes for native execution.

GitHub artifacts expire after 14 days. Required release evidence must be retrieved,
verified and retained in durable artifact storage before relying on a run. Reports
and references belong in central documentation; binaries do not. These workflow
requirements do not claim that an unexecuted matrix has passed or that NAM is
generally faster than the equivalent managed implementation.
