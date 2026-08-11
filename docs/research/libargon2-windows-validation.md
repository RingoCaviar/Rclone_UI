# Native libargon2 Windows validation

Validation date: 2026-08-11  
Issue: #31  
Workflow run: [31476553644](https://github.com/RingoCaviar/Rclone_UI/actions/runs/31476553644)

## Candidate identity

- Upstream: official [P-H-C/phc-winner-argon2](https://github.com/P-H-C/phc-winner-argon2) reference implementation.
- Pinned source revision/tag: `62358ba2123abd17fccf2a108a301d4b52c01a7c` (`20190702`).
- Build: upstream `Argon2OptDll`, `ReleaseStatic|x64`, MSVC on the GitHub `windows-2022` image.
- DLL SHA-256: `27795CBA4FFDE27D77B59C29A676F1FA98A88289F5A8F0DAD8A1113DDBE8470D`.
- Internal-memory clearing: the upstream `FLAG_clear_internal_memory` default remains enabled. The official project documents that internal clearing is enabled by default and that the caller remains responsible for its password buffer.
- Redistribution: upstream dual CC0/Apache-2.0 licensing is already represented by the repository's third-party notice process; the workflow artifact contains the exact DLL and machine-readable identity manifest.

## Automated result

The official optimized upstream known-answer test executable passed before the application boundary was exercised. The application then loaded the DLL only by absolute path plus the expected SHA-256 digest and ran its 64 MiB, t=3, p=4, 32-byte-output gate.

The seven measured derivations each stayed below the 1.5-second p95 sample gate. Three simultaneous callers completed through the production single-admission queue below the five-second bound, demonstrating that excess requests wait rather than allocating three Argon2 workspaces. The complete application gate took 932 ms on the hosted Windows Server 2022 x64 runner. This is a mainstream CI result, not minimum-hardware evidence.

Production hardening included with the gate:

- one process-wide native derivation is admitted at a time;
- hostile memory/iteration/lane bounds are checked before admission and native allocation;
- password, salt and output spans are pinned for the call;
- bad architecture, missing DLL/export, malformed digest and digest mismatch become a typed unavailable result;
- caller-owned password and derived-key buffers remain explicitly zeroed by their owners.

## Remaining acceptance

Issue #31 must remain open until the same artifact and workflow-derived digest are exercised on the declared minimum Windows 10 x64 hardware and a named mainstream Windows 11 machine under normal memory pressure. Record cold/warm samples, paging/private-memory observations, and interruption/allocation-failure results. Do not infer minimum-hardware acceptance from the GitHub-hosted runner and do not lower the 64 MiB/t=3/p=4 floor.
