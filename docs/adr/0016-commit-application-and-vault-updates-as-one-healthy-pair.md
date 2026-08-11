---
status: accepted
---

# Commit application and Vault updates as one healthy pair

Treat the active application version and Vault generation as one recoverable update pair. Keep the previous application directory and Vault generation intact, and do not make the new pair authoritative until the authenticated new Host passes the complete bounded health contract and a durable `NewHealthPassed` record is written.

Before that commit point, recovery restores and verifies both old selectors. At or after it, recovery completes and verifies both new selectors. Never repair only one pointer, run an old Host against a new-only Vault schema, infer Host ownership from a pointer, or clean the previous pair immediately.

Media absence pauses recovery without mutation. Every phase is reconciled against verified files, authenticated manifests, process identity, Data Root ownership, and journal evidence rather than trusting phase text alone. The logical crash matrix passed all 45 injected cases; release qualification remains conditional on real binaries, authentication, Vault migration, disk-full/removal, and Windows 10/11 testing.
