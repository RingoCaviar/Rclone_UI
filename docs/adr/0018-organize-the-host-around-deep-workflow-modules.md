---
status: accepted
---

# Organize the Host around deep workflow modules

Organize implementation around deep Host-owned workflow modules—Data Root Session, Work Coordinator, Remote Catalog, Transfer Orchestrator, Mount Manager, Rclone Runtime, Managed Update Coordinator, and Diagnostic Exporter—rather than technical layers or thin wrappers around SQLite, rclone RC, Win32, and WinFsp.

Each module presents one semantic interface that owns its invariants, ordering, failure vocabulary, and recovery behavior. The UI crosses only the versioned Host Protocol seam and never calls persistence, rclone, or Windows platform mechanisms directly. Third-party and platform adapters remain internal seams with production and deterministic test implementations.

This structure concentrates safety policy and makes the module interface the acceptance-test surface. It deliberately rejects generic platform-service collections, repository-per-table persistence, and endpoint-per-command wrappers because those would force callers to reconstruct workflows and spread correctness across the codebase.
