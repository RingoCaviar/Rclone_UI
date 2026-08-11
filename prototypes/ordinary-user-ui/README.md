# Ordinary-user UI prototype

> THROWAWAY PROTOTYPE — decision evidence for GitHub issue #5, not production code.

Three variants of the top-level Rclone UI workspace, switchable with `?variant=`, on the new prototype route.

Run from the repository root:

```powershell
python -m http.server 4173 -d prototypes/ordinary-user-ui
```

Then open <http://localhost:4173/?variant=A>. Use the floating bottom switcher or the left/right arrow keys to compare:

- `A` — Task home
- `B` — Guided workspace
- `C` — Dual-pane explorer

All state is in memory and all actions are simulated.
