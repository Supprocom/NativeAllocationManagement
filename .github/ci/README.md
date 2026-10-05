# Native release validation

The workflow validates actual Linux/Windows x64/ARM64 processes from one clean,
exact commit. It pins action identities, SDK 10.0.302/runtime 10.0.10 and native
PowerShell 7.6.6. Official SDK archive SHA-512 identities are checked before a
fresh isolated installation; a runner-global newer runtime cannot be selected.
Tracked LF checkout bytes must equal their Git blobs before any compiler runs.
All nine root projects use the complete Release analyzer policy; voxel coverage,
both wrapper tests, isolated candidate consumers, rooted trimmed execution and
actual NativeAOT execution are required. No skipped or failed TRX outcome passes.

Evidence lives outside the checkout. Each child retains arguments, declared
deadline, actual exit, complete streams and original compiler inputs. Source,
packages, caches, binaries, native runner/toolchain identities and length/SHA-256
manifests are uploaded even when validation fails. Native labels, publishing
success and ordinary JIT builds are not substitutes for native execution.

GitHub artifacts expire after 14 days. Required release evidence must be retrieved,
verified and retained in durable artifact storage before relying on a run. Reports
and references belong in central documentation; binaries do not. These workflow
requirements do not claim that an unexecuted matrix has passed or that NAM is
generally faster than the equivalent managed implementation.
