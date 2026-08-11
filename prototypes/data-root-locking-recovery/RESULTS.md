# Prototype result

Environment: Windows NT 10.0.26200.0, local fixed NTFS temporary directory.

Passed observations:

- dot-segment and case aliases normalize to the same case-insensitive root identity;
- `FileShare.None` rejects both a second handle and an independent child process;
- deleting the live lock is rejected, while the same persistent lock file can be reacquired after the owner closes;
- a directory symbolic link escaping the root is discoverable by resolving its final target;
- an NTFS hard link demonstrates that path containment alone does not establish unique file identity;
- simulated interruption before selector creation and after selector flush retains generation 1;
- replacement of the selector activates the already-valid generation 2.

The prototype intentionally did not claim results for FAT32/exFAT, UNC/network locking, cloud synchronization overlays, physical removal, real disk-full, controller flush behavior, or power loss. No safe disposable volume was present. Those cases remain a required destructive media matrix.
