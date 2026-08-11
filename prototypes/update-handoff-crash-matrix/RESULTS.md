# Prototype result

The harness exercised 45 cases across 11 update phases:

- failure before each phase action;
- failure after the action but before the phase journal;
- failure after the phase journal;
- Data Root disappearance after each journal, followed by return;
- explicit new-Host health failure.

All 45 cases passed.

- Before durable `NewHealthPassed`, recovery selected `v1` plus Vault generation `1`.
- At or after durable `NewHealthPassed`, recovery selected `v2` plus Vault generation `2`.
- While media was missing, recovery returned `DataRootUnavailable` and performed no pointer mutation; after return it converged normally.
- Explicit new health failure rolled back to the old pair.
- Every recovered healthy state had exactly one Host marker.

The prototype validates logical ordering and same-volume file replacement in a temporary NTFS directory. It does not establish physical power-loss durability, signature verification, actual process authentication, real SQLite/AEAD migration, FAT/exFAT behavior, or a real 30-second health deadline.
