# Remote setup journey prototype

> THROWAWAY PROTOTYPE — decision evidence for GitHub issue #9, not production code.

Three variants of Remote setup and recovery inside the accepted Task Home shell, switchable with `?variant=`.

Open `index.html` directly, or run:

```powershell
python -m http.server 4173 -d prototypes/remote-setup-journey
```

Then compare `?variant=A`, `?variant=B`, and `?variant=C`. All credentials, tests, imports, exports, and deletes are simulated in memory.
