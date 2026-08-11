---
status: accepted
---

# Use an explicit portable Data Root and encrypted Vault

Keep all mutable Rclone UI state in an explicit, movable Data Root that is never silently redirected to Windows profile storage, and protect rclone credentials plus sensitive application state with a Master Password and separate encrypted Vault. Default to portable password entry while allowing user-scoped DPAPI only as an opt-in, machine-bound convenience; enforce one writable instance per Data Root and prefer explicit read-only or relocation behavior over hidden fallbacks, because predictable portability and recoverable writes outweigh seamless startup in unwritable or shared locations.
